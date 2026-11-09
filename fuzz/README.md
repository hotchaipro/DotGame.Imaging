# Fuzzing the decoders

The decoders read files from anywhere, and the JPEG and WebP decoders are ports of C, with unsafe code.
This is a coverage-guided fuzzer for them: it makes damaged images from good ones, millions of them, and
keeps any that reach code the others didn't. It isn't in `DotGame.Imaging.slnx`, so `dotnet build` and
`dotnet test` at the root don't build it, and it's never packed.

```bash
cd fuzz
dotnet run -c Release -- run --minutes 60
```

That fuzzes `PngCodec`, `JpegCodec`, `WebPCodec` and `ImageDecoder`, each in two processes, one with
SIMD and one without (`DOTNET_EnableHWIntrinsic=0`), for an hour. `--formats png,jpeg` picks decoders,
`--simd on` runs only the SIMD ones. It reports every five minutes, and leaves in `out/` (ignored by
git):

- `corpus/<format>/`: the inputs that reached new code. The next run starts from them, as well as from
  `test/Images` and images the encoders write.
- `findings/`: an input for each kind of failure, the smallest seen, with a `.txt` that says what
  happened.
- `logs/`: each process's output.

## What it checks

Each input is decoded the way an app would: `TryGetDecodedSize`, then `Decode` into a buffer of that size,
twice. A failure is:

- An exception other than `InvalidDataException` or `NotSupportedException`, the two the decoders
  document for bad bytes. `findings/<format>-<exception>-<method>.bin` has the input.
- A read or write past the end of the input or the pixels: both end at an inaccessible page, so the
  process crashes. The supervisor saves the input it was running as `<format>-crash-exit<code>-<hash>.bin`
  and starts the process again.
- An input that runs for 20 seconds, saved as `<format>-hang-<hash>.bin`. On a machine busy with other
  work this can be a slow start rather than a slow input (an empty input is always that): `replay` it to
  see.
- Two decodes that give different pixels, when the buffer started as zeros for one and 0xFF for the
  other: a pixel the decoder didn't write, or one it read from memory it hadn't set.
- `TryGetDecodedSize` giving a byte count that isn't width x height x 4, or `Decode` accepting an image
  whose size didn't read.

Images with more than `--max-pixels` pixels (512 x 512 by default, at most 2048 x 2048) have their size
read but aren't decoded, which keeps each run short.

## How it works

[SharpFuzz](https://github.com/Metalnem/sharpfuzz) instruments the build's copy of `DotGame.Imaging.dll`,
so each branch the decoders take counts in a table, as AFL's instrumentation does in C. The loop is
AFL's: take an input from the corpus, change it, run it, and keep it if it took branches, or took them
a number of times, that no input had. The changes are AFL's havoc (bit flips, interesting values,
blocks deleted, copied and inserted, truncation, two inputs spliced), and changes that know each format:
a PNG image's pixel data is inflated, changed and deflated again, its header fields and chunks changed
and its CRCs fixed; a WebP file's chunks and header fields changed and its sizes fixed; a JPEG file's
segment headers, lengths and markers changed, and segments moved between files.

## After a finding

`replay` decodes files once each and reports what the fuzzer would, with line numbers in a Debug build:

```bash
dotnet run -- replay out/findings/jpeg-IndexOutOfRangeException-HuffmanTable--ctor.bin
```

Fix the decoder, copy the file to `test/Images/Fuzz/<format>/` with a name that says what it is, and add
it to `FuzzedImageTests`. Every file there is also checked as the fuzzer checks it.

To check that SIMD and scalar code give the same pixels for the whole corpus, and that nothing corrupts
the heap (the runtime checks it at each collection, made frequent here):

```bash
dotnet run -c Release -- replay out/corpus --hashes simd.txt
DOTNET_EnableHWIntrinsic=0 dotnet run -c Release -- replay out/corpus --hashes scalar.txt
DOTNET_HeapVerify=1 DOTNET_GCgen0size=0x40000 dotnet run -c Release -- replay out/corpus
```

and compare the two hash files.
