#!/usr/bin/env python3
"""Checks the docs, so what they say can't drift from the code.

    python3 tools/api-docs/check-docs.py            # links, then compiles every C# sample
    python3 tools/api-docs/check-docs.py --links    # only the links

Links: every relative link in README.md, CONTRIBUTING.md and docs/, its file and its #anchor, must
resolve. Links to other repositories are absolute (https://github.com/...), and aren't checked.

Code: every ```csharp block in README.md goes into a throwaway project and is compiled against the
library (src/DotGame.Imaging.csproj). A block holding whole types is compiled as it is. One with
members is wrapped in a class, and one with statements in a method. Each gets the fields of Prelude,
below, as the names the samples share. A <!-- no-check --> line before a block skips it, for a
fragment that can't compile on its own.

DotGame.Framework's and DotGame.Toolkit's tools/api-docs/check-guides.py do the same for their guides.
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs"
SAMPLES = [ROOT / "README.md"]
# Outside the repository, so its code style rules don't apply to fragments
WORK = Path(tempfile.gettempdir()).resolve() / "dotgame-imaging-doc-samples"

USINGS = """
global using System;
global using System.Buffers;
global using System.Collections.Generic;
global using System.IO;
global using DotGame.Imaging;
global using DotGame.Imaging.Jpeg;
global using DotGame.Imaging.Png;
global using DotGame.Imaging.WebP;
"""

# The names the samples share: an image's size and pixels
PRELUDE = """
internal abstract class Prelude
{
    protected int width;
    protected int height;
    protected byte[] pixels = null!;
}
"""


def blocks(path):
    """Yields each ```csharp block's first line and its code."""
    lines = path.read_text(encoding="utf-8").splitlines()
    index = 0
    while index < len(lines):
        if lines[index].strip() == "```csharp":
            skip = (index > 0) and (lines[index - 1].strip() == "<!-- no-check -->")
            first = index + 1
            index += 1
            while lines[index].strip() != "```":
                index += 1
            if not skip:
                yield first + 1, "\n".join(lines[first:index])
        index += 1


def wrap(name, code):
    """Makes a sample into a file. Its using directives go at the top, whatever it holds."""
    usings = "\n".join(line for line in code.splitlines() if line.startswith("using "))
    body = "\n".join(line for line in code.splitlines() if not line.startswith(("using ", "namespace ")))
    if re.search(r"^(internal |public |sealed |static |abstract )*(class|struct|record|interface) ", body, re.M):
        return f"{usings}\nnamespace Samples {{\n{body}\n}}\n"

    if re.search(r"^(private|protected|public|internal) ", body, re.M):
        return f"{usings}\nnamespace Samples {{\ninternal abstract class {name} : Prelude\n{{\n{body}\n}}\n}}\n"

    return f"{usings}\nnamespace Samples {{\ninternal abstract class {name} : Prelude\n{{\n    private void Run()\n    {{\n{body}\n    }}\n}}\n}}\n"


def slug(heading):
    text = re.sub(r"[`*]", "", heading).strip().lower()
    text = re.sub(r"[^\w\- ]", "", text)
    return text.replace(" ", "-")


def anchors(path):
    text = path.read_text(encoding="utf-8")
    found = {slug(m.group(1)) for m in re.finditer(r"^#+ (.+)$", text, re.M)}
    found.update(m.group(1) for m in re.finditer(r'<a name="([^"]+)"', text))
    return found


def check_links():
    errors = []
    pages = [ROOT / "README.md", ROOT / "CONTRIBUTING.md", *DOCS.rglob("*.md")]
    for page in pages:
        text = re.sub(r"```.*?```", "", page.read_text(encoding="utf-8"), flags=re.S)
        for match in re.finditer(r"\]\(([^)\s]+)\)", text):
            target = match.group(1)
            if re.match(r"[a-z]+:", target):
                continue
            file, _, anchor = target.partition("#")
            destination = (page.parent / file).resolve() if file else page
            if not destination.exists():
                errors.append(f"{page.relative_to(ROOT)}: no file {target}")
            elif anchor and (destination.suffix == ".md") and (anchor not in anchors(destination)):
                errors.append(f"{page.relative_to(ROOT)}: no anchor {target}")
    return errors


def check_code():
    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)

    origins = {}
    for path in SAMPLES:
        for line, code in blocks(path):
            name = f"Sample_{path.stem.replace('-', '_')}_{line}"
            (WORK / f"{name}.cs").write_text(wrap(name, code), encoding="utf-8")
            origins[name] = f"{path.relative_to(ROOT)}:{line}"

    (WORK / "Prelude.cs").write_text(USINGS + PRELUDE, encoding="utf-8")
    (WORK / "Program.cs").write_text("internal static class CheckProgram { public static void Main() { } }\n", encoding="utf-8")
    library = ROOT / "src" / "DotGame.Imaging.csproj"
    (WORK / "DocSamples.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <StartupObject>CheckProgram</StartupObject>
    <NoWarn>$(NoWarn);CS0169;CS0219;CS0414;CS0649;CS8321;CS0162;IDE0051</NoWarn>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{library}" />
  </ItemGroup>
</Project>
""", encoding="utf-8")

    result = subprocess.run(
        ["dotnet", "build", str(WORK / "DocSamples.csproj"), "-v:q", "-nologo", "-tl:off"],
        capture_output=True, text=True, check=False)
    errors = sorted({line.strip() for line in result.stdout.splitlines() if ": error " in line})
    report = []
    for error in errors:
        match = re.search(r"(Sample_\w+)\.cs\((\d+),\d+\): error (\w+): (.*?)( \[|$)", error)
        report.append(f"{origins.get(match.group(1), match.group(1))} (+{match.group(2)}): {match.group(3)} {match.group(4)}" if match else error)
    if (result.returncode != 0) and not errors:
        report.append(result.stdout[-2000:])
    return report, len(origins)


def main():
    errors = check_links()
    for error in errors:
        print(error)
    print(f"Links: {len(errors)} broken")

    code_errors = []
    if "--links" not in sys.argv:
        code_errors, count = check_code()
        for error in code_errors:
            print(error)
        print(f"Code: {count} samples, {len(code_errors)} errors")

    return 1 if (errors or code_errors) else 0


if __name__ == "__main__":
    sys.exit(main())
