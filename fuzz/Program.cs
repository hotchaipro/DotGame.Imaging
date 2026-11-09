// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DotGame.Imaging.Fuzz;

/// <summary>
/// A coverage-guided fuzzer for the decoders. See README.md.
/// </summary>
internal static unsafe class Program
{
    private const int MapSize = 1 << 16;

    // A worker's shared file: how many inputs it has run, then the one it's running (its length and
    // bytes), so the supervisor can save the input that crashed or hung it
    private const int HeaderSize = 16;

    private static readonly string Root = FindRoot();

    // The most pixels an image can have to be decoded (see Target)
    private static int maxPixels;

    public static int Main(
        string[] args)
    {
        // Instrumented code counts its branches here; it must be set before any of it runs
        SharpFuzz.Common.Trace.SharedMem = (byte*)NativeMemory.AllocZeroed(MapSize);
        if (args.Length == 0)
        {
            return Usage();
        }

        var options = new Dictionary<string, string>();
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && (i + 1 < args.Length))
            {
                options[args[i][2..]] = args[++i];
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        string output = options.GetValueOrDefault("out", Path.Combine(Root, "fuzz", "out"));
        maxPixels = int.Parse(options.GetValueOrDefault("max-pixels", "262144"), CultureInfo.InvariantCulture);
        switch (args[0])
        {
            case "run":
                Instrument();
                return Supervise(
                    options.GetValueOrDefault("formats", "png,jpeg,webp,any"),
                    double.Parse(options.GetValueOrDefault("minutes", "10"), CultureInfo.InvariantCulture),
                    options.GetValueOrDefault("simd", "both"),
                    output);
            case "worker":
                return Worker(
                    Enum.Parse<Format>(options["format"], true),
                    options["name"],
                    int.Parse(options["seed"], CultureInfo.InvariantCulture),
                    double.Parse(options["seconds"], CultureInfo.InvariantCulture),
                    output);
            case "replay":
                return Replay(positional, options.GetValueOrDefault("format", "any"), options.GetValueOrDefault("hashes"));
            default:
                return Usage();
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            dotnet run -c Release -- run [--formats png,jpeg,webp,any] [--minutes 10] [--simd both|on|off] [--max-pixels 262144] [--out fuzz/out]
                Fuzzes each decoder in its own process (two, with SIMD on and off, for --simd both) for the
                minutes given. The corpus grows in out/corpus/<format>, what's found goes in out/findings. An image with more pixels than --max-pixels (at most 2048 x 2048) has its size read but isn't decoded.
            dotnet run -c Release -- replay <files or folders> [--format any] [--hashes hashes.txt]
                Decodes each file once and reports what the fuzzer would; --hashes writes each file's pixels'
                hash, to compare a run with DOTNET_EnableHWIntrinsic=0 against one without.
            """);
        return 2;
    }

    private static string FindRoot()
    {
        string? folder = AppContext.BaseDirectory;
        while ((folder != null) && !File.Exists(Path.Combine(folder, "DotGame.Imaging.slnx")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        return folder ?? Directory.GetCurrentDirectory();
    }

    // Instrumentation ------------------------------------------------------------------------------

    private static void Instrument()
    {
        // Instruments this build's copy of DotGame.Imaging.dll in place, once: a build copies a fresh
        // one over it, and the marker then no longer matches
        string assembly = Path.Combine(AppContext.BaseDirectory, "DotGame.Imaging.dll");
        string marker = assembly + ".instrumented";
        if (File.Exists(marker) && (File.ReadAllText(marker) == Stamp(assembly)))
        {
            return;
        }

        SharpFuzz.Fuzzer.Instrument(assembly, name => name.StartsWith("DotGame.Imaging", StringComparison.Ordinal), SharpFuzz.Options.Value);
        File.WriteAllText(marker, Stamp(assembly));
        Console.WriteLine($"Instrumented {assembly}");
    }

    private static string Stamp(
        string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length} {info.LastWriteTimeUtc.Ticks}";
    }

    // Supervisor -----------------------------------------------------------------------------------

    private sealed class Job
    {
        public string Name = "";
        public Format Format;
        public bool Simd;
        public Process? Process;
        public MemoryMappedFile? Map;
        public MemoryMappedViewAccessor? View;
        public long Execs;
        public long LastCount = -1;
        public DateTime LastProgress;
        public int Restarts;
        public StreamWriter? Log;
    }

    private static int Supervise(
        string formats,
        double minutes,
        string simd,
        string output)
    {
        var jobs = new List<Job>();
        foreach (string name in formats.Split(','))
        {
            Format format = Enum.Parse<Format>(name.Trim(), true);
            foreach (bool on in simd == "both" ? new[] { true, false } : new[] { simd != "off" })
            {
                jobs.Add(new Job { Name = $"{format.ToString().ToLowerInvariant()}-{(on ? "simd" : "scalar")}", Format = format, Simd = on });
            }
        }

        Directory.CreateDirectory(Path.Combine(output, "findings"));
        Directory.CreateDirectory(Path.Combine(output, "logs"));
        DateTime start = DateTime.UtcNow;
        DateTime end = start.AddMinutes(minutes);
        var random = new Random();
        foreach (Job job in jobs)
        {
            Start(job, end, random.Next(), output);
        }

        DateTime nextReport = start.AddMinutes(1);
        while (true)
        {
            Thread.Sleep(1000);
            DateTime now = DateTime.UtcNow;
            bool running = false;
            foreach (Job job in jobs)
            {
                if (job.Process == null)
                {
                    continue;
                }

                long count = job.View!.ReadInt64(0);
                if (count != job.LastCount)
                {
                    job.LastCount = count;
                    job.LastProgress = now;
                }

                if (job.Process.HasExited)
                {
                    int code = job.Process.ExitCode;
                    job.Execs += count;
                    if (code != 0)
                    {
                        Save(job, output, $"crash-exit{code}");
                    }

                    Stop(job);
                    if ((code != 0) && (now < end.AddSeconds(-5)))
                    {
                        job.Restarts++;
                        Start(job, end, random.Next(), output);
                        running = true;
                    }

                    continue;
                }

                running = true;
                if ((now - job.LastProgress).TotalSeconds > 20)
                {
                    // One input has run for 20 seconds: a hang, or something far slower than it should be
                    Save(job, output, "hang");
                    job.Process.Kill();
                    job.Process.WaitForExit();
                    job.Execs += count;
                    Stop(job);
                    if (now < end.AddSeconds(-5))
                    {
                        job.Restarts++;
                        Start(job, end, random.Next(), output);
                    }
                }
            }

            if ((now >= nextReport) || !running)
            {
                nextReport = now.AddMinutes(5);
                Report(jobs, output, now - start);
            }

            if (!running)
            {
                return 0;
            }
        }
    }

    private static void Start(
        Job job,
        DateTime end,
        int seed,
        string output)
    {
        string mapPath = Path.Combine(output, "logs", job.Name + ".current");
        double seconds = Math.Max(1, (end - DateTime.UtcNow).TotalSeconds);
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "worker", "--format", job.Format.ToString(), "--name", job.Name, "--seed", seed.ToString(CultureInfo.InvariantCulture), "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--out", output, "--max-pixels", maxPixels.ToString(CultureInfo.InvariantCulture) })
        {
            info.ArgumentList.Add(arg);
        }

        if (!job.Simd)
        {
            info.Environment["DOTNET_EnableHWIntrinsic"] = "0";
        }

        // An allocation that runs away throws OutOfMemoryException, which the worker reports
        info.Environment["DOTNET_GCHeapHardLimit"] = "0x40000000";
        var file = new FileStream(mapPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        job.Map = MemoryMappedFile.CreateFromFile(file, null, HeaderSize + (4 << 20), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
        job.View = job.Map.CreateViewAccessor();
        job.LastCount = -1;
        job.LastProgress = DateTime.UtcNow.AddSeconds(60);
        var log = new StreamWriter(Path.Combine(output, "logs", job.Name + ".log"), true) { AutoFlush = true };
        job.Log = log;
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (log)
                {
                    log.WriteLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (log)
                {
                    log.WriteLine(e.Data);
                }
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        job.Process = process;
    }

    private static void Stop(
        Job job)
    {
        job.Process?.WaitForExit();
        job.Process?.Dispose();
        job.Process = null;
        job.View?.Dispose();
        job.Map?.Dispose();
        if (job.Log != null)
        {
            lock (job.Log)
            {
                job.Log.Dispose();
            }
        }

        job.Log = null;
        job.View = null;
        job.Map = null;
    }

    private static void Save(
        Job job,
        string output,
        string kind)
    {
        int length = job.View!.ReadInt32(8);
        var input = new byte[Math.Clamp(length, 0, 4 << 20)];
        job.View.ReadArray(HeaderSize, input, 0, input.Length);
        string name = $"{job.Format.ToString().ToLowerInvariant()}-{kind}-{Target.Hash(input):x16}";
        File.WriteAllBytes(Path.Combine(output, "findings", name + ".bin"), input);
        File.WriteAllText(Path.Combine(output, "findings", name + ".txt"), $"{kind} in {job.Name} (SIMD {(job.Simd ? "on" : "off")}), {input.Length} bytes\n");
        Console.WriteLine($"{DateTime.Now:T} {job.Name}: {kind}, saved {name}.bin");
    }

    private static void Report(
        List<Job> jobs,
        string output,
        TimeSpan elapsed)
    {
        var text = new StringBuilder($"{DateTime.Now:T} after {elapsed:hh\\:mm\\:ss}:\n");
        foreach (Job job in jobs)
        {
            long execs = job.Execs + ((job.View != null) ? Math.Max(0, job.View.ReadInt64(0)) : 0);
            string corpus = Path.Combine(output, "corpus", job.Format.ToString().ToLowerInvariant());
            int entries = Directory.Exists(corpus) ? Directory.GetFiles(corpus).Length : 0;
            text.Append(CultureInfo.InvariantCulture, $"  {job.Name,-12} {execs,12:N0} inputs, {entries,5} in the corpus, {job.Restarts} restarts\n");
        }

        int findings = Directory.GetFiles(Path.Combine(output, "findings"), "*.bin").Length;
        text.Append(CultureInfo.InvariantCulture, $"  {findings} findings in {Path.Combine(output, "findings")}");
        Console.WriteLine(text);
        File.AppendAllText(Path.Combine(output, "logs", "summary.log"), text + "\n");
    }

    // Worker ---------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Worker(
        Format format,
        string name,
        int seed,
        double seconds,
        string output)
    {
        string mapPath = Path.Combine(output, "logs", name + ".current");
        var mapFile = new FileStream(mapPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var map = MemoryMappedFile.CreateFromFile(mapFile, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
        using var view = map.CreateViewAccessor();
        byte* shared = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref shared);

        string corpusFolder = Path.Combine(output, "corpus", format.ToString().ToLowerInvariant());
        string findingsFolder = Path.Combine(output, "findings");
        Directory.CreateDirectory(corpusFolder);
        var target = new Target(format, maxPixels);
        var mutator = new Mutator(seed, Math.Min(target.MaxInput, 1 << 20));
        var random = new Random(seed);
        var virgin = new byte[MapSize];
        byte* trace = SharpFuzz.Common.Trace.SharedMem;
        var corpus = new List<byte[]>();
        var seen = new HashSet<string>();
        long execs = 0;

        Failure? Execute(byte[] input)
        {
            *(int*)(shared + 8) = input.Length;
            new ReadOnlySpan<byte>(input).CopyTo(new Span<byte>(shared + HeaderSize, input.Length));
            new Span<byte>(trace, MapSize).Clear();
            SharpFuzz.Common.Trace.PrevLocation = 0;
            Failure? failure = target.Run(input, out _);
            execs++;
            *(long*)shared = execs;
            return failure;
        }

        void Record(byte[] input, Failure failure)
        {
            string signature = $"{format.ToString().ToLowerInvariant()}-{Sanitize(failure.Signature)}";
            string path = Path.Combine(findingsFolder, signature + ".bin");
            bool known = !seen.Add(signature);
            if (!File.Exists(path) || (new FileInfo(path).Length > input.Length))
            {
                File.WriteAllBytes(path, input);
                File.WriteAllText(Path.ChangeExtension(path, ".txt"), $"{failure.Signature} in {name}, {input.Length} bytes\n{failure.Details}\n");
            }

            if (!known)
            {
                Console.WriteLine($"{DateTime.Now:T} found {failure.Signature}");
            }
        }

        // Starts from the seeds and from what earlier runs kept
        var inputs = Seeds.For(format, Path.Combine(Root, "test", "Images"));
        foreach (string file in Directory.GetFiles(corpusFolder))
        {
            inputs.Add(File.ReadAllBytes(file));
        }

        foreach (byte[] input in inputs)
        {
            Failure? failure = Execute(input);
            if (failure != null)
            {
                Record(input, failure);
            }

            Merge(trace, virgin);
            corpus.Add(input);
        }

        Console.WriteLine($"{DateTime.Now:T} {name} (seed {seed}): {corpus.Count} inputs to start, {Edges(virgin)} edges");
        var clock = Stopwatch.StartNew();
        double nextStats = 10;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            // Newer inputs are likelier to lead somewhere new; mostly pick from the latest
            int index = (random.Next(3) == 0) ? random.Next(corpus.Count) : corpus.Count - 1 - random.Next(Math.Min(corpus.Count, 64));
            byte[] input;
            try
            {
                input = mutator.Mutate(corpus[index], corpus);
            }
            catch (Exception exception)
            {
                // A bug in the mutator, not the decoders: say so, and carry on
                Console.WriteLine($"The mutator threw {exception}");
                continue;
            }
            Failure? failure = Execute(input);
            if (failure != null)
            {
                Record(input, failure);
                continue;
            }

            if (Merge(trace, virgin))
            {
                corpus.Add(input);
                File.WriteAllBytes(Path.Combine(corpusFolder, $"{Target.Hash(input):x16}"), input);
            }

            if (clock.Elapsed.TotalSeconds >= nextStats)
            {
                nextStats += 60;
                Console.WriteLine($"{DateTime.Now:T} {execs:N0} inputs ({execs / clock.Elapsed.TotalSeconds:N0}/s), {corpus.Count} in the corpus, {Edges(virgin)} edges, {seen.Count} kinds of failure");
            }
        }

        Console.WriteLine($"{DateTime.Now:T} done: {execs:N0} inputs, {corpus.Count} in the corpus, {Edges(virgin)} edges, {seen.Count} kinds of failure");
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        return 0;
    }

    private static string Sanitize(
        string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        return builder.ToString();
    }

    // Adds the run's coverage, in AFL's hit-count buckets, to what's been seen: true if it saw something new
    private static bool Merge(
        byte* trace,
        byte[] virgin)
    {
        bool found = false;
        ulong* words = (ulong*)trace;
        for (int w = 0; w < MapSize / 8; w++)
        {
            if (words[w] == 0)
            {
                continue;
            }

            for (int i = w * 8; i < (w * 8) + 8; i++)
            {
                byte count = trace[i];
                if (count == 0)
                {
                    continue;
                }

                byte bucket = count switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 4,
                    < 8 => 8,
                    < 16 => 16,
                    < 32 => 32,
                    < 128 => 64,
                    _ => 128,
                };
                if ((virgin[i] & bucket) == 0)
                {
                    virgin[i] |= bucket;
                    found = true;
                }
            }
        }

        return found;
    }

    private static int Edges(
        byte[] virgin)
    {
        int edges = 0;
        foreach (byte b in virgin)
        {
            if (b != 0)
            {
                edges++;
            }
        }

        return edges;
    }

    // Replay ---------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Replay(
        List<string> paths,
        string formatName,
        string? hashes)
    {
        Format format = Enum.Parse<Format>(formatName, true);
        var target = new Target(format, maxPixels);
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
            }
            else
            {
                files.Add(path);
            }
        }

        files.Sort(StringComparer.Ordinal);
        var lines = new List<string>();
        int failures = 0;
        foreach (string file in files)
        {
            if (file.EndsWith(".txt", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] input = File.ReadAllBytes(file);
            var clock = Stopwatch.StartNew();
            Failure? failure = target.Run(input, out ulong hash);
            if (clock.ElapsedMilliseconds > 1000)
            {
                Console.WriteLine($"{file}: slow, {clock.ElapsedMilliseconds} ms");
            }

            if (failure != null)
            {
                failures++;
                Console.WriteLine($"{file}: {failure.Signature}\n  {failure.Details.Replace("\n", "\n  ", StringComparison.Ordinal)}");
            }

            lines.Add($"{hash:x16} {Path.GetRelativePath(Root, file)}");
        }

        if (hashes != null)
        {
            File.WriteAllLines(hashes, lines);
        }

        Console.WriteLine($"{files.Count} files, {failures} failures");
        return (failures == 0) ? 0 : 1;
    }
}
