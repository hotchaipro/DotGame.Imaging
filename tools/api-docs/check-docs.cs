// Checks the docs, so what they say can't drift from the code.
//
//     dotnet run tools/api-docs/check-docs.cs                links, then compiles every C# sample
//     dotnet run tools/api-docs/check-docs.cs -- --links     only the links
//
// Links: every relative link in the pages check-docs.json lists, its file and its #anchor, must
// resolve. Links to other repositories are absolute (https://github.com/...), and aren't checked.
//
// Code: every ```csharp block in the files check-docs.json lists goes into a throwaway project, in the
// temporary folder so the repository's code style rules don't apply to fragments, and is compiled
// against what check-docs.json references. A block holding whole types is compiled as it is. One with
// members is wrapped in a class, and one with statements in a method. Each class derives from Prelude,
// in the file check-docs.json names, whose fields are the names the samples share. A sample's using
// directives go at the top of its file, and a file's samples share a namespace, so they can use each
// other's types. A <!-- no-check --> line before a block skips it, for a fragment that can't compile
// on its own.
//
// This file is the same in every DotGame repository that has docs to check; what's particular to one
// is in tools/api-docs/check-docs.json beside it. Each repository keeps its own copy, so an improvement
// to one is worth copying to the others.

#:property RunAnalyzers=false
#:property TreatWarningsAsErrors=false

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var repo = FindRepository();
var config = Config.Read(repo);

var linkErrors = CheckLinks(repo, config);
foreach (var error in linkErrors)
{
    Console.WriteLine(error);
}

Console.WriteLine($"Links: {linkErrors.Count} broken");

var codeErrors = new List<string>();
if (!args.Contains("--links"))
{
    (codeErrors, int count) = CheckCode(repo, config);
    foreach (var error in codeErrors)
    {
        Console.WriteLine(error);
    }

    Console.WriteLine($"Code: {count} samples, {codeErrors.Count} errors");
}

return ((linkErrors.Count > 0) || (codeErrors.Count > 0)) ? 1 : 0;

static string FindRepository()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "tools", "api-docs", "check-docs.json")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Run this from inside a repository with tools/api-docs/check-docs.json.");
}

// A folder's path with its links followed. On macOS the temporary folder is under /var, a link to
// /private/var, and a relative path from the project there to the repository climbs from the real
// folder, so it would miss.
static string RealPath(
    string path)
{
    var full = Path.GetFullPath(path);
    var root = Path.GetPathRoot(full)!;
    var real = root;
    foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
    {
        real = Path.Combine(real, part);
        if (new DirectoryInfo(real).LinkTarget is { } target)
        {
            real = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(real)!, target));
        }
    }

    return real;
}

// The files a list of patterns names, relative to the repository, in order: "README.md" is one file,
// "docs/*.md" a folder's, and "docs/api/**/*.md" a folder's and every folder's under it
static List<string> Files(
    string repo,
    IEnumerable<string> patterns)
{
    var files = new List<string>();
    foreach (var pattern in patterns)
    {
        var parts = pattern.Split('/');
        var name = parts[^1];
        bool recursive = (parts.Length > 1) && (parts[^2] == "**");
        var folder = Path.Combine(repo, Path.Combine(parts[..^(recursive ? 2 : 1)]));
        if (!name.Contains('*'))
        {
            if (File.Exists(Path.Combine(folder, name)))
            {
                files.Add(Path.GetFullPath(Path.Combine(folder, name)));
            }

            continue;
        }

        if (Directory.Exists(folder))
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files.AddRange(Directory.EnumerateFiles(folder, name, option).Select(Path.GetFullPath).Order(StringComparer.Ordinal));
        }
    }

    return files.Distinct().ToList();
}

static string Relative(
    string repo,
    string path)
{
    return Path.GetRelativePath(repo, path).Replace('\\', '/');
}

static string Slug(
    string heading)
{
    var text = Regex.Replace(heading, "[`*]", string.Empty).Trim().ToLowerInvariant();
    text = Regex.Replace(text, @"[^\w\- ]", string.Empty);
    return text.Replace(' ', '-');
}

static HashSet<string> Anchors(
    string path)
{
    var text = File.ReadAllText(path);
    var found = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match match in Regex.Matches(text, @"^#+ (.+)$", RegexOptions.Multiline))
    {
        found.Add(Slug(match.Groups[1].Value.TrimEnd('\r')));
    }

    foreach (Match match in Regex.Matches(text, "<a name=\"([^\"]+)\""))
    {
        found.Add(match.Groups[1].Value);
    }

    return found;
}

static List<string> CheckLinks(
    string repo,
    Config config)
{
    var errors = new List<string>();
    var anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var page in Files(repo, config.Links))
    {
        var text = Regex.Replace(File.ReadAllText(page), "```.*?```", string.Empty, RegexOptions.Singleline);
        foreach (Match match in Regex.Matches(text, @"\]\(([^)\s]+)\)"))
        {
            var target = match.Groups[1].Value;
            if (Regex.IsMatch(target, "^[a-z]+:"))
            {
                continue;
            }

            int hash = target.IndexOf('#');
            var file = (hash < 0) ? target : target[..hash];
            var anchor = (hash < 0) ? string.Empty : target[(hash + 1)..];
            var destination = (file.Length > 0) ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, Uri.UnescapeDataString(file))) : page;
            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                errors.Add($"{Relative(repo, page)}: no file {target}");
            }
            else if ((anchor.Length > 0) && destination.EndsWith(".md", StringComparison.Ordinal))
            {
                if (!anchors.TryGetValue(destination, out var known))
                {
                    known = Anchors(destination);
                    anchors[destination] = known;
                }

                if (!known.Contains(anchor))
                {
                    errors.Add($"{Relative(repo, page)}: no anchor {target}");
                }
            }
        }
    }

    return errors;
}

// Each checked ```csharp block: the line its code starts on, and the code
static IEnumerable<(int Line, string Code)> Blocks(
    string path)
{
    var lines = File.ReadAllLines(path);
    for (int index = 0; index < lines.Length; index++)
    {
        if (lines[index].Trim() != "```csharp")
        {
            continue;
        }

        bool skip = (index > 0) && (lines[index - 1].Trim() == "<!-- no-check -->");
        int first = index + 1;
        index++;
        while ((index < lines.Length) && (lines[index].Trim() != "```"))
        {
            index++;
        }

        if (!skip)
        {
            yield return (first + 1, string.Join('\n', lines[first..index]));
        }
    }
}

static string Wrap(
    string space,
    string name,
    string code)
{
    // Directives only: a using declaration (using var file = ...;) is a statement
    var directive = new Regex(@"^using (static )?[\w.]+( = [\w.<>]+)?;\s*$");
    var lines = code.Split('\n');
    var usings = string.Join('\n', lines.Where(line => directive.IsMatch(line)));
    var body = string.Join('\n', lines.Where(line => !directive.IsMatch(line) && !line.StartsWith("namespace ", StringComparison.Ordinal)));

    if (Regex.IsMatch(body, @"^(internal |public |sealed |static |abstract )*(class|struct|record|interface) ", RegexOptions.Multiline))
    {
        return $"{usings}\nnamespace {space} {{\n{body}\n}}\n";
    }

    if (Regex.IsMatch(body, "^(private|protected|public|internal) ", RegexOptions.Multiline))
    {
        return $"{usings}\nnamespace {space} {{\ninternal abstract class {name} : Prelude\n{{\n{body}\n}}\n}}\n";
    }

    return $"{usings}\nnamespace {space} {{\ninternal abstract class {name} : Prelude\n{{\n    private async Task Run()\n    {{\n{body}\n    }}\n}}\n}}\n";
}

static (List<string> Errors, int Count) CheckCode(
    string repo,
    Config config)
{
    var work = Path.Combine(RealPath(Path.GetTempPath()), config.WorkFolder);
    if (Directory.Exists(work))
    {
        Directory.Delete(work, recursive: true);
    }

    Directory.CreateDirectory(work);

    var origins = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in Files(repo, config.Samples))
    {
        var stem = Path.GetFileNameWithoutExtension(path).Replace('-', '_').Replace('.', '_');
        foreach (var (line, code) in Blocks(path))
        {
            var name = $"Sample_{stem}_{line}";
            File.WriteAllText(Path.Combine(work, name + ".cs"), Wrap($"Samples_{stem}", name, code));
            origins[name] = $"{Relative(repo, path)}:{line}";
        }
    }

    var usings = new StringBuilder();
    foreach (var space in config.Usings)
    {
        usings.Append("global using ").Append(space).Append(";\n");
    }

    File.WriteAllText(Path.Combine(work, "Prelude.cs"), usings + "\n" + File.ReadAllText(Path.Combine(repo, "tools", "api-docs", config.Prelude)));
    File.WriteAllText(Path.Combine(work, "Program.cs"), "internal static class CheckProgram { public static void Main() { } }\n");

    var project = new StringBuilder();
    project.Append("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net11.0</TargetFramework>
            <Nullable>enable</Nullable>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            <StartupObject>CheckProgram</StartupObject>
            <NoWarn>$(NoWarn);CS0169;CS0219;CS0414;CS0649;CS1998;CS8321;CS0162;CS0108;CS0114;IDE0051;RS0030</NoWarn>
            <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
          </PropertyGroup>

        """);
    foreach (var import in config.Imports)
    {
        project.Append($"  <Import Project=\"{Path.Combine(repo, import)}\" />\n");
    }

    project.Append("  <ItemGroup>\n");
    foreach (var reference in config.References)
    {
        project.Append($"    <ProjectReference Include=\"{Path.Combine(repo, reference)}\" />\n");
    }

    project.Append("  </ItemGroup>\n</Project>\n");
    var projectFile = Path.Combine(work, "DocSamples.csproj");
    File.WriteAllText(projectFile, project.ToString());

    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "build", projectFile, "-v:q", "-nologo", "-tl:off" })
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start)!;
    var errorOutput = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    _ = errorOutput.Result;

    var errors = output.Split('\n').Select(line => line.Trim()).Where(line => line.Contains(": error ", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal);
    var report = new List<string>();
    foreach (var error in errors)
    {
        var match = Regex.Match(error, @"(Sample_\w+)\.cs\((\d+),\d+\): error (\w+): (.*?)( \[|$)");
        report.Add(match.Success
            ? $"{origins.GetValueOrDefault(match.Groups[1].Value, match.Groups[1].Value)} (+{match.Groups[2].Value}): {match.Groups[3].Value} {match.Groups[4].Value}"
            : error);
    }

    if ((process.ExitCode != 0) && (report.Count == 0))
    {
        report.Add(output.Length > 2000 ? output[^2000..] : output);
    }

    return (report, origins.Count);
}

// check-docs.json: what this repository checks.
//
//     {
//       "links": ["README.md", "docs/**/*.md"],        // pages whose links are checked
//       "samples": ["README.md", "docs/guide.md"],     // files whose ```csharp blocks are compiled
//       "workFolder": "dotgame-imaging-doc-samples",   // the throwaway project's folder, in the temporary folder
//       "usings": ["System", "DotGame.Imaging"],       // global usings for every sample
//       "prelude": "check-docs.prelude.cs",            // beside this file: class Prelude, the shared names
//       "imports": [],                                 // MSBuild files the project imports, from the repository
//       "references": ["src/DotGame.Imaging.csproj"]   // projects it references, from the repository
//     }
internal sealed record Config(
    List<string> Links,
    List<string> Samples,
    string WorkFolder,
    List<string> Usings,
    string Prelude,
    List<string> Imports,
    List<string> References)
{
    public static Config Read(
        string repo)
    {
        var path = Path.Combine(repo, "tools", "api-docs", "check-docs.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;

        List<string> List(string name) => root.TryGetProperty(name, out var value)
            ? value.EnumerateArray().Select(item => item.GetString()!).ToList()
            : new List<string>();

        string Text(string name) => root.TryGetProperty(name, out var value)
            ? value.GetString()!
            : throw new InvalidOperationException($"{path}: \"{name}\" is missing.");

        return new Config(List("links"), List("samples"), Text("workFolder"), List("usings"), Text("prelude"), List("imports"), List("references"));
    }
}
