// Writes the API reference under docs/api from the source: every public type and member of this
// repository's packages, with its doc comments, one page per type.
//
//     dotnet run tools/api-docs/generate.cs                 writes docs/api
//     dotnet run tools/api-docs/generate.cs -- --check      fails if docs/api isn't up to date
//     dotnet run tools/api-docs/generate.cs -- --missing    lists public members with no doc comment
//
// It reads source rather than built assemblies, so it needs no build first and no platform's
// workload.
//
// This file is the same in every DotGame repository that has an API reference; what's particular to
// one is in tools/api-docs/packages.json beside it: its packages, where their source is, and other
// repositories' packages it reads for links to their types, from the clones their variables name. The
// copy in DotGame.Imaging is the original: change it there, then copy it to the others, whose CI fails
// while theirs differs from it.

#:package Microsoft.CodeAnalysis.CSharp@4.14.0
#:property RunAnalyzers=false
#:property TreatWarningsAsErrors=false

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var repo = FindRepository();
var (packages, layout) = ReadPackages(repo);

bool check = args.Contains("--check");
bool missing = args.Contains("--missing");

var model = new Model();
foreach (var package in packages)
{
    foreach (var root in package.Roots)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var inRoot = "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            if (inRoot.Contains("/obj/") || inRoot.Contains("/bin/") || inRoot.Contains("/build/") || inRoot.Contains("/test/"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: relative);
            model.AddFile(package, tree, relative);
        }
    }
}

model.Link();

if (missing)
{
    int total = 0;
    int bare = 0;
    foreach (var type in model.Types.Where(t => t.Package.IsLocal).OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        if (type.Summary(model) is null)
        {
            Console.WriteLine($"{type.File}:{type.Line}: {type.FullName}");
            bare++;
        }

        total++;
        foreach (var member in type.Members)
        {
            // An operator says what it does, and an enum's value or a palette's color is its name
            if ((member.Kind is MemberKind.Operator or MemberKind.EnumValue)
                || (type.Members.Count(m => (m.Kind == member.Kind) && m.Signature.Contains(" static ", StringComparison.Ordinal)) >= 20))
            {
                continue;
            }

            total++;
            if (member.Summary(model) is null)
            {
                Console.WriteLine($"{member.File}:{member.Line}: {type.FullName}.{member.Name}");
                bare++;
            }
        }
    }

    Console.WriteLine($"{bare} of {total} public types and members have no doc comment");
    return 0;
}

var output = new Dictionary<string, string>(StringComparer.Ordinal);
Writer.WriteAll(model, packages, layout, output);

// A signature that spans lines keeps the source file's line endings: the pages have LF, whatever the checkout's
foreach (var path in output.Keys.ToList())
{
    output[path] = output[path].ReplaceLineEndings("\n");
}

var apiRoot = Path.Combine(repo, "docs", "api");
if (check)
{
    var stale = new List<string>();
    foreach (var (path, content) in output)
    {
        var full = Path.Combine(apiRoot, path);
        if (!File.Exists(full) || (File.ReadAllText(full).ReplaceLineEndings("\n") != content))
        {
            stale.Add(path);
        }
    }

    if (Directory.Exists(apiRoot))
    {
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace('\\', '/');
            if (!output.ContainsKey(relative))
            {
                stale.Add(relative);
            }
        }
    }

    foreach (var path in stale.Order(StringComparer.Ordinal))
    {
        Console.WriteLine($"docs/api/{path} is out of date");
    }

    if (stale.Count > 0)
    {
        Console.WriteLine("Run: dotnet run tools/api-docs/generate.cs");
        return 1;
    }

    Console.WriteLine($"docs/api is up to date: {output.Count} pages");
    return 0;
}

if (Directory.Exists(apiRoot))
{
    Directory.Delete(apiRoot, recursive: true);
}

foreach (var (path, content) in output)
{
    var full = Path.Combine(apiRoot, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, content);
}

Console.WriteLine($"Wrote {output.Count} pages to docs/api");
return 0;

// The repository is the folder above the current one with tools/api-docs/packages.json
static string FindRepository()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "tools", "api-docs", "packages.json")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Run this from inside a repository with tools/api-docs/packages.json.");
}

// packages.json: the packages, in the index's order, and how the pages link back to an index.
//
//     {
//       "writeIndex": true,                // docs/api/README.md is an index of every package
//       "allPackages": "../README.md",     // where a package page's link back goes
//       "allPackagesText": "All packages", // and what it says (this by default)
//       "packages": [
//         // This repository's: its pages in docs/api/<folder> ("" or none for docs/api itself)
//         { "name": "Framework", "description": "...", "roots": ["framework"], "folder": "Framework" },
//         // Another's, read from the clone the variable names, its roots in that clone: links to its
//         // types go to its home page, or to its pages if it has them
//         { "name": "Imaging", "description": "...", "clone": "DotGameImagingDir", "repository": "DotGame.Imaging",
//           "roots": ["src"], "home": "https://...", "pages": "https://..." }
//       ]
//     }
//
// Comments and trailing commas are allowed.
static (Package[] Packages, Layout Layout) ReadPackages(
    string repo)
{
    var path = Path.Combine(repo, "tools", "api-docs", "packages.json");
    using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    var root = json.RootElement;

    string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
    string Required(JsonElement element, string name) => Text(element, name) ?? throw new InvalidOperationException($"{path}: no \"{name}\".");

    var packages = new List<Package>();
    foreach (var entry in root.GetProperty("packages").EnumerateArray())
    {
        var name = Required(entry, "name");
        var clone = Text(entry, "clone");
        var home = Text(entry, "home");
        if ((clone is null) != (home is null))
        {
            throw new InvalidOperationException($"{path}: {name} needs both \"clone\" and \"home\" (another repository's package), or neither (this one's).");
        }

        var baseFolder = (clone is null) ? repo : Clone(clone, Required(entry, "repository"));
        var roots = entry.GetProperty("roots").EnumerateArray().Select(r => Path.Combine(baseFolder, r.GetString()!)).ToArray();
        packages.Add(new Package(name, Required(entry, "description"), roots)
        {
            Folder = Text(entry, "folder") ?? string.Empty,
            Home = home,
            Pages = Text(entry, "pages"),
        });
    }

    var layout = new Layout(
        root.TryGetProperty("writeIndex", out var writeIndex) && writeIndex.GetBoolean(),
        Required(root, "allPackages"),
        Text(root, "allPackagesText") ?? "All packages");
    return (packages.ToArray(), layout);
}

// Another repository's clone, where the variable says
static string Clone(
    string variable,
    string name)
{
    var path = Environment.GetEnvironmentVariable(variable);
    if (string.IsNullOrEmpty(path))
    {
        throw new InvalidOperationException($"Set {variable} to a clone of {name}.");
    }

    path = Path.GetFullPath(path);
    if (!Directory.Exists(path))
    {
        throw new InvalidOperationException($"No clone of {name} at {path}: set {variable} to where it is.");
    }

    return path;
}

// A package's roots are the folders its source is in. One of this repository's has pages in its
// Folder of docs/api ("" for docs/api itself); another repository's has a Home page and, if its
// types have pages, the URL they're under (Pages)
sealed record Package(string Name, string Description, string[] Roots)
{
    public string Folder { get; init; } = string.Empty;

    public string? Home { get; init; }

    public string? Pages { get; init; }

    public bool IsLocal => this.Home is null;

    public string ReadmePath => (this.Folder.Length == 0) ? "README.md" : $"{this.Folder}/README.md";
}

// Whether docs/api/README.md is an index of every package, and where a package page's link back
// goes, and what it says
sealed record Layout(bool WriteIndex, string AllPackages, string AllPackagesText = "All packages");

enum MemberKind
{
    Constructor,
    Field,
    Property,
    Indexer,
    Event,
    Method,
    Operator,
    EnumValue,
}

sealed class DocComment
{
    public XElement? Xml { get; init; }

    public string? InheritCref => this.Xml?.Element("inheritdoc")?.Attribute("cref")?.Value;

    public bool Inherits => this.Xml?.Element("inheritdoc") is not null;

    // What this comment says for itself, and what it takes from the comment it inherits: all of it
    // that it doesn't say, or what the inheritdoc's path selects, such as /remarks
    public DocComment Inheriting(
        DocComment inherited)
    {
        var own = this.Xml?.Elements().Where(e => e.Name != "inheritdoc").ToList() ?? [];
        if ((own.Count == 0) && (this.Xml?.Element("inheritdoc")?.Attribute("path") is null))
        {
            return inherited;
        }

        var path = this.Xml?.Element("inheritdoc")?.Attribute("path")?.Value;
        var merged = new XElement(this.Xml!.Name, own);
        foreach (var element in inherited.Xml?.Elements() ?? [])
        {
            bool selected = (path is null) || (path == $"/{element.Name}");
            bool said = own.Any(e => (e.Name == element.Name) && (e.Attribute("name")?.Value == element.Attribute("name")?.Value));
            if (selected && (!said) && (element.Name != "inheritdoc" || (path is null)))
            {
                merged.Add(element);
            }
        }

        return new DocComment { Xml = merged };
    }
}

sealed class ApiMember
{
    public required string Name { get; init; }

    public required MemberKind Kind { get; init; }

    public required string Signature { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public required DocComment Doc { get; init; }

    public required ApiType Owner { get; init; }

    public DocComment Resolved(
        Model model)
    {
        var seen = new HashSet<ApiMember>();
        var doc = this.Doc;
        var owner = this.Owner;

        // An override with no comment of its own says what the member it overrides says
        bool isOverride = this.Signature.Contains(" override ", StringComparison.Ordinal);
        while (doc.Inherits || (isOverride && (doc.Xml is null)))
        {
            var target = model.ResolveInherit(doc.InheritCref, owner, this.Name);
            if ((target is null) || (!seen.Add(target)))
            {
                break;
            }

            doc = doc.Inheriting(target.Doc);
            owner = target.Owner;
        }

        return doc;
    }

    public string? Summary(
        Model model)
    {
        return Xml2Md.Inline(this.Resolved(model).Xml?.Element("summary"), model, this.Owner);
    }
}

sealed class ApiType
{
    public required string Namespace { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required Package Package { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public ApiType? Parent { get; init; }

    public string Declaration { get; set; } = string.Empty;

    public DocComment Doc { get; set; } = new();

    public List<string> Bases { get; } = [];

    public List<ApiMember> Members { get; } = [];

    public HashSet<string> Signatures { get; } = new(StringComparer.Ordinal);

    public string DisplayName => (this.Parent is null) ? this.Name : $"{this.Parent.DisplayName}.{this.Name}";

    public string FullName => $"{this.Namespace}.{this.DisplayName}";

    public string SimpleName => Regex.Replace(this.Name, "<.*>", string.Empty);

    public string PageName
    {
        get
        {
            var name = Regex.Replace(this.DisplayName, "<(.*)>", m => "-" + (m.Groups[1].Value.Count(c => c == ',') + 1));
            return name.Replace('.', '-');
        }
    }

    public string PagePath => (this.Package.Folder.Length == 0) ? $"{this.PageName}.md" : $"{this.Package.Folder}/{this.PageName}.md";

    public string? Summary(
        Model model)
    {
        return Xml2Md.Inline(this.Doc.Xml?.Element("summary"), model, this);
    }
}

sealed class Model
{
    private readonly Dictionary<string, ApiType> _byKey = new(StringComparer.Ordinal);

    public List<ApiType> Types { get; } = [];

    public void AddFile(
        Package package,
        SyntaxTree tree,
        string file)
    {
        foreach (var node in tree.GetCompilationUnitRoot().Members)
        {
            this.AddMember(package, node, string.Empty, null, file);
        }
    }

    private void AddMember(
        Package package,
        MemberDeclarationSyntax node,
        string ns,
        ApiType? parent,
        string file)
    {
        switch (node)
        {
            case BaseNamespaceDeclarationSyntax space:
            {
                var inner = (ns.Length == 0) ? space.Name.ToString() : $"{ns}.{space.Name}";
                foreach (var member in space.Members)
                {
                    this.AddMember(package, member, inner, parent, file);
                }

                break;
            }

            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                this.AddType(package, node, ns, parent, file);
                break;
        }
    }

    private static bool IsPublic(
        SyntaxTokenList modifiers)
    {
        return modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword) && !modifiers.Any(SyntaxKind.PrivateKeyword) && !modifiers.Any(SyntaxKind.InternalKeyword);
    }

    private void AddType(
        Package package,
        MemberDeclarationSyntax node,
        string ns,
        ApiType? parent,
        string file)
    {
        var modifiers = node.Modifiers;
        bool isInterfaceMember = parent?.Kind == "interface";
        if (!IsPublic(modifiers) && !isInterfaceMember)
        {
            return;
        }

        string name;
        string kind;
        string declaration;
        switch (node)
        {
            case EnumDeclarationSyntax e:
                name = e.Identifier.Text;
                kind = "enum";
                declaration = $"{Text.Modifiers(e.Modifiers)}enum {name}{Text.BaseList(e.BaseList)}";
                break;

            case DelegateDeclarationSyntax d:
                name = d.Identifier.Text + d.TypeParameterList;
                kind = "delegate";
                declaration = $"{Text.Modifiers(d.Modifiers)}delegate {d.ReturnType} {name}{Text.Collapse(d.ParameterList.ToString())}";
                break;

            case TypeDeclarationSyntax t:
                name = t.Identifier.Text + t.TypeParameterList;
                kind = t switch
                {
                    RecordDeclarationSyntax r when r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record struct",
                    RecordDeclarationSyntax => "record",
                    StructDeclarationSyntax => "struct",
                    InterfaceDeclarationSyntax => "interface",
                    _ => "class",
                };
                declaration = $"{Text.Modifiers(t.Modifiers, dropPartial: true)}{kind} {name}{Text.Collapse(t.ParameterList?.ToString() ?? string.Empty)}{Text.BaseList(t.BaseList)}";
                break;

            default:
                return;
        }

        var key = $"{ns}|{parent?.DisplayName}|{name}|{package.Name}";
        if (!this._byKey.TryGetValue(key, out var type))
        {
            type = new ApiType
            {
                Namespace = ns,
                Name = name,
                Kind = kind,
                Package = package,
                File = file,
                Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Parent = parent,
            };
            this._byKey[key] = type;
            this.Types.Add(type);
        }

        // A partial type: the part with the base list or the docs says most
        if (type.Declaration.Length < declaration.Length)
        {
            type.Declaration = declaration;
        }

        var doc = Text.ReadDoc(node);
        if (doc.Xml is not null)
        {
            type.Doc = doc;
        }

        if (node is BaseTypeDeclarationSyntax { BaseList: { } bases })
        {
            foreach (var entry in bases.Types)
            {
                var baseName = Regex.Replace(entry.Type.ToString(), "<.*>", string.Empty).Split('.').Last();
                if (!type.Bases.Contains(baseName))
                {
                    type.Bases.Add(baseName);
                }
            }
        }

        switch (node)
        {
            case EnumDeclarationSyntax e:
                foreach (var value in e.Members)
                {
                    var text = (value.EqualsValue is null) ? value.Identifier.Text : $"{value.Identifier.Text} {value.EqualsValue}";
                    this.Add(type, value, value.Identifier.Text, MemberKind.EnumValue, text, file);
                }

                break;

            case RecordDeclarationSyntax { ParameterList: { } parameters } record:
                foreach (var parameter in parameters.Parameters)
                {
                    var sig = $"public {parameter.Type} {parameter.Identifier.Text} {{ get; init; }}";
                    var described = Text.ParamDoc(doc, parameter.Identifier.Text);
                    var member = new ApiMember
                    {
                        Name = parameter.Identifier.Text,
                        Kind = MemberKind.Property,
                        Signature = sig,
                        File = file,
                        Line = record.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Doc = described,
                        Owner = type,
                    };
                    if (type.Signatures.Add(sig))
                    {
                        type.Members.Add(member);
                    }
                }

                foreach (var member in record.Members)
                {
                    this.AddTypeMember(package, type, member, file);
                }

                break;

            case TypeDeclarationSyntax t:
                foreach (var member in t.Members)
                {
                    this.AddTypeMember(package, type, member, file);
                }

                break;
        }
    }

    private void AddTypeMember(
        Package package,
        ApiType type,
        MemberDeclarationSyntax member,
        string file)
    {
        bool isInterface = type.Kind == "interface";
        bool isPublic = isInterface ? !member.Modifiers.Any(SyntaxKind.PrivateKeyword) : IsPublic(member.Modifiers);

        // NOTE: An overload that's an error to call is there to stop a mistake compiling, not to be read about
        if (member.AttributeLists.Any(list => list.Attributes.Any(attribute => attribute.Name.ToString() == "Obsolete")))
        {
            return;
        }

        switch (member)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                this.AddType(package, member, type.Namespace, type, file);
                return;

            case IncompleteMemberSyntax:
                return;
        }

        if (!isPublic)
        {
            return;
        }

        // What every value has: it says nothing about this API
        if (member is MethodDeclarationSyntax { Identifier.Text: "Equals" or "GetHashCode" or "ToString" or "GetEnumerator" })
        {
            return;
        }

        switch (member)
        {
            case ConstructorDeclarationSyntax c:
                if (!c.Modifiers.Any(SyntaxKind.StaticKeyword))
                {
                    this.Add(type, c, c.Identifier.Text, MemberKind.Constructor, $"{Text.Modifiers(c.Modifiers)}{c.Identifier.Text}{Text.Collapse(c.ParameterList.ToString())}", file);
                }

                break;

            case MethodDeclarationSyntax m:
                if (m.ExplicitInterfaceSpecifier is null)
                {
                    this.Add(type, m, m.Identifier.Text, MemberKind.Method, $"{Text.Modifiers(m.Modifiers)}{m.ReturnType} {m.Identifier.Text}{m.TypeParameterList}{Text.Collapse(m.ParameterList.ToString())}{Text.Constraints(m.ConstraintClauses)}", file);
                }

                break;

            case PropertyDeclarationSyntax p:
                if (p.ExplicitInterfaceSpecifier is null)
                {
                    this.Add(type, p, p.Identifier.Text, MemberKind.Property, $"{Text.Modifiers(p.Modifiers)}{p.Type} {p.Identifier.Text} {Text.Accessors(p.AccessorList, p.ExpressionBody, isInterface)}", file);
                }

                break;

            case IndexerDeclarationSyntax i:
                if (i.ExplicitInterfaceSpecifier is null)
                {
                    this.Add(type, i, "this[]", MemberKind.Indexer, $"{Text.Modifiers(i.Modifiers)}{i.Type} this{Text.Collapse(i.ParameterList.ToString())} {Text.Accessors(i.AccessorList, i.ExpressionBody, isInterface)}", file);
                }

                break;

            case FieldDeclarationSyntax f:
                foreach (var variable in f.Declaration.Variables)
                {
                    var value = f.Modifiers.Any(SyntaxKind.ConstKeyword) ? $" {variable.Initializer}" : string.Empty;
                    this.Add(type, f, variable.Identifier.Text, MemberKind.Field, $"{Text.Modifiers(f.Modifiers)}{f.Declaration.Type} {variable.Identifier.Text}{value};", file);
                }

                break;

            case EventFieldDeclarationSyntax e:
                foreach (var variable in e.Declaration.Variables)
                {
                    this.Add(type, e, variable.Identifier.Text, MemberKind.Event, $"{Text.Modifiers(e.Modifiers)}event {e.Declaration.Type} {variable.Identifier.Text};", file);
                }

                break;

            case EventDeclarationSyntax e:
                if (e.ExplicitInterfaceSpecifier is null)
                {
                    this.Add(type, e, e.Identifier.Text, MemberKind.Event, $"{Text.Modifiers(e.Modifiers)}event {e.Type} {e.Identifier.Text};", file);
                }

                break;

            case OperatorDeclarationSyntax o:
                this.Add(type, o, $"operator {o.OperatorToken.Text}", MemberKind.Operator, $"{Text.Modifiers(o.Modifiers)}{o.ReturnType} operator {o.OperatorToken.Text}{Text.Collapse(o.ParameterList.ToString())}", file);
                break;

            case ConversionOperatorDeclarationSyntax c:
                this.Add(type, c, $"{c.ImplicitOrExplicitKeyword.Text} operator {c.Type}", MemberKind.Operator, $"{Text.Modifiers(c.Modifiers)}{c.ImplicitOrExplicitKeyword.Text} operator {c.Type}{Text.Collapse(c.ParameterList.ToString())}", file);
                break;
        }
    }

    private void Add(
        ApiType type,
        MemberDeclarationSyntax node,
        string name,
        MemberKind kind,
        string signature,
        string file)
    {
        if (type.Signatures.Add(signature))
        {
            type.Members.Add(new ApiMember
            {
                Name = name,
                Kind = kind,
                Signature = signature,
                File = file,
                Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Doc = Text.ReadDoc(node),
                Owner = type,
            });
        }
    }

    public void Link()
    {
        // Nothing to do beyond finding names: kept as a step so the writer sees a finished model
        this.Types.Sort((a, b) => string.CompareOrdinal(a.FullName + a.Package.Name, b.FullName + b.Package.Name));
    }

    public ApiType? FindType(
        string cref,
        ApiType? from)
    {
        var name = Regex.Replace(cref.Replace('{', '<').Replace('}', '>'), "<.*>", string.Empty);
        var last = name.Split('.').Last();

        ApiType? best = null;
        foreach (var type in this.Types)
        {
            if (type.SimpleName != last)
            {
                continue;
            }

            var plain = Regex.Replace(type.FullName, "<[^>]*>", string.Empty);
            if ((plain == name) || plain.EndsWith("." + name, StringComparison.Ordinal))
            {
                // Prefer the same package, for a name more than one platform has
                if ((best is null) || (type.Package == from?.Package))
                {
                    best = type;
                }
            }
        }

        return best;
    }

    // The overload a cref's parameter types name, if it names any
    private static ApiMember? OverloadOf(
        string cref,
        ApiType? type,
        string name)
    {
        var open = cref.IndexOf('(');
        if ((type is null) || (open < 0))
        {
            return null;
        }

        var parameters = cref[(open + 1)..].TrimEnd(')').Replace('{', '<').Replace('}', '>');
        var wanted = parameters.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return type.Members.FirstOrDefault(m => (m.Name == name)
            && (Text.ParameterCount(m.Signature) == wanted.Length)
            && wanted.All(w => m.Signature.Contains(w + " ", StringComparison.Ordinal)));
    }

    public ApiMember? ResolveInherit(
        string? cref,
        ApiType owner,
        string memberName)
    {
        if (cref is null)
        {
            // Its base types and interfaces, by the member's name
            foreach (var baseName in owner.Bases)
            {
                var parent = this.FindType(baseName, owner);
                var found = parent?.Members.FirstOrDefault(m => (m.Name == memberName) && (m.Doc.Xml is not null));
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        var plain = Regex.Replace(cref.Replace('{', '<').Replace('}', '>'), "[<(].*$", string.Empty);
        var parts = plain.Split('.');
        var name = parts[^1];
        var type = (parts.Length > 1) ? this.FindType(string.Join('.', parts[..^1]), owner) : owner;
        var overload = OverloadOf(cref, type, name);
        if (overload is not null)
        {
            return overload;
        }

        return type?.Members.FirstOrDefault(m => (m.Name == name) && (m.Doc.Xml?.Element("summary") is not null))
            ?? type?.Members.FirstOrDefault(m => m.Name == name);
    }
}

static class Text
{
    // How many parameters a signature has, not counting commas inside generic arguments or tuples
    public static int ParameterCount(
        string signature)
    {
        var open = signature.IndexOf('(');
        if ((open < 0) || signature[(open + 1)..].TrimStart().StartsWith(')'))
        {
            return 0;
        }

        int depth = 0;
        int count = 1;
        foreach (var c in signature[(open + 1)..])
        {
            if (c is '<' or '(' or '[')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']')
            {
                if ((c == ')') && (depth == 0))
                {
                    break;
                }

                depth--;
            }
            else if ((c == ',') && (depth == 0))
            {
                count++;
            }
        }

        return count;
    }

    public static string Collapse(
        string text)
    {
        return Regex.Replace(text, @"\s+", " ").Replace("( ", "(").Replace(" )", ")");
    }

    public static string Modifiers(
        SyntaxTokenList modifiers,
        bool dropPartial = false)
    {
        var builder = new StringBuilder();
        foreach (var modifier in modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PartialKeyword) && dropPartial)
            {
                continue;
            }

            builder.Append(modifier.Text).Append(' ');
        }

        return builder.ToString();
    }

    public static string BaseList(
        BaseListSyntax? list)
    {
        return (list is null) ? string.Empty : " : " + string.Join(", ", list.Types.Select(t => t.Type.ToString()));
    }

    public static string Constraints(
        SyntaxList<TypeParameterConstraintClauseSyntax> clauses)
    {
        return (clauses.Count == 0) ? string.Empty : " " + Collapse(string.Join(" ", clauses.Select(c => c.ToString())));
    }

    public static string Accessors(
        AccessorListSyntax? list,
        ArrowExpressionClauseSyntax? expression,
        bool isInterface)
    {
        if (list is null)
        {
            return expression is null ? "{ }" : "{ get; }";
        }

        var parts = new List<string>();
        foreach (var accessor in list.Accessors)
        {
            bool hidden = accessor.Modifiers.Any(SyntaxKind.PrivateKeyword) || accessor.Modifiers.Any(SyntaxKind.InternalKeyword);
            if (hidden && !isInterface)
            {
                continue;
            }

            var prefix = accessor.Modifiers.Any(SyntaxKind.ProtectedKeyword) ? "protected " : string.Empty;
            parts.Add($"{prefix}{accessor.Keyword.Text};");
        }

        return "{ " + string.Join(" ", parts) + " }";
    }

    public static DocComment ReadDoc(
        SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is DocumentationCommentTriviaSyntax comment)
            {
                var lines = comment.ToFullString().ReplaceLineEndings("\n").Split('\n')
                    .Select(line => Regex.Replace(line, @"^\s*///\s?", string.Empty));
                var xml = "<doc>" + string.Join("\n", lines) + "</doc>";
                try
                {
                    return new DocComment { Xml = XElement.Parse(xml, LoadOptions.PreserveWhitespace) };
                }
                catch (System.Xml.XmlException)
                {
                    return new DocComment();
                }
            }
        }

        return new DocComment();
    }

    public static DocComment ParamDoc(
        DocComment doc,
        string name)
    {
        var param = doc.Xml?.Elements("param").FirstOrDefault(p => p.Attribute("name")?.Value == name);
        if (param is null)
        {
            return new DocComment();
        }

        var summary = new XElement("summary", param.Nodes());
        return new DocComment { Xml = new XElement("doc", summary) };
    }
}

static class Xml2Md
{
    public static string? Inline(
        XElement? element,
        Model model,
        ApiType from)
    {
        if (element is null)
        {
            return null;
        }

        var text = Clean(Render(element, model, from));
        return (text.Length == 0) ? null : text;
    }

    public static string Clean(
        string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var builder = new StringBuilder();
        bool inCode = false;
        foreach (var raw in lines)
        {
            var line = inCode ? raw : raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }

            builder.Append(line).Append('\n');
        }

        var result = builder.ToString();

        // Joins lines within a paragraph, so a source comment's wrapping doesn't matter
        var paragraphs = Regex.Split(result, @"\n\s*\n");
        var output = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim('\n', ' ');
            if (trimmed.Length == 0)
            {
                continue;
            }

            bool isBlock = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("- ", StringComparison.Ordinal);
            output.Add(isBlock ? trimmed : Regex.Replace(trimmed, @"\s*\n\s*", " "));
        }

        return string.Join("\n\n", output);
    }

    private static string Render(
        XNode node,
        Model model,
        ApiType from)
    {
        switch (node)
        {
            case XText text:
                return text.Value.Replace("|", "\\|");

            case XElement element:
                var inner = string.Concat(element.Nodes().Select(n => Render(n, model, from)));
                switch (element.Name.LocalName)
                {
                    case "c":
                        // NOTE: A type named in prose links, as a cref does
                        return (Regex.IsMatch(element.Value, "^[A-Z][A-Za-z0-9_]*$")
                            && (model.FindType(element.Value, from) is { } named)
                            && (named != from))
                            ? $"[`{element.Value}`]({Writer.RelativeLink(from, named)})"
                            : $"`{element.Value}`";

                    case "code":
                        return $"\n\n```csharp\n{Dedent(element.Value)}\n```\n\n";

                    case "para":
                        return $"\n\n{inner.Trim()}\n\n";

                    case "paramref":
                    case "typeparamref":
                        return $"`{element.Attribute("name")?.Value}`";

                    case "see":
                    case "seealso":
                        return See(element, inner, model, from);

                    case "list":
                        return "\n\n" + string.Concat(element.Elements("item").Select(i => $"- {Clean(string.Concat(i.Nodes().Select(n => Render(n, model, from)))).Replace("\n\n", " ")}\n")) + "\n";

                    case "term":
                        return $"**{inner.Trim()}**: ";

                    case "description":
                        return inner;

                    default:
                        return inner;
                }

            default:
                return string.Empty;
        }
    }

    private static string See(
        XElement element,
        string inner,
        Model model,
        ApiType from)
    {
        if (element.Attribute("langword")?.Value is { } word)
        {
            return $"`{word}`";
        }

        if (element.Attribute("href")?.Value is { } href)
        {
            return $"[{(inner.Length > 0 ? inner : href)}]({href})";
        }

        var cref = element.Attribute("cref")?.Value;
        if (cref is null)
        {
            return inner;
        }

        var plain = Regex.Replace(cref.Replace('{', '<').Replace('}', '>'), @"\(.*$", string.Empty);
        var label = (inner.Length > 0) ? inner.Trim() : Regex.Replace(plain, "^.*\\.(?=[^.]+\\.[^.]+$)", string.Empty);

        var noGeneric = Regex.Replace(plain, "<.*?>", string.Empty);
        var target = model.FindType(noGeneric, from);
        string? anchor = null;
        if (target is null && noGeneric.Contains('.'))
        {
            var parts = noGeneric.Split('.');
            target = model.FindType(string.Join('.', parts[..^1]), from);
            anchor = parts[^1].ToLowerInvariant();
        }
        else if (target is null)
        {
            // A member of the type it's written on
            var member = from.Members.FirstOrDefault(m => m.Name == noGeneric);
            if (member is not null)
            {
                return $"[`{label}`](#{noGeneric.ToLowerInvariant()})";
            }

            return $"`{label}`";
        }

        if (target is null)
        {
            return $"`{label}`";
        }

        var path = (target == from) ? string.Empty : Writer.RelativeLink(from, target);

        // A package with no pages for its types has no anchors for their members
        var hash = ((anchor is null) || ((!target.Package.IsLocal) && (target.Package.Pages is null))) ? string.Empty : $"#{anchor}";
        return ((path.Length == 0) && (hash.Length == 0)) ? $"`{label}`" : $"[`{label}`]({path}{hash})";
    }

    private static string Dedent(
        string code)
    {
        var lines = code.ReplaceLineEndings("\n").Trim('\n').Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => (l.Length >= indent) ? l[indent..] : l.TrimStart())).TrimEnd();
    }
}

static class Writer
{
    private static readonly (MemberKind Kind, string Title)[] Sections =
    [
        (MemberKind.EnumValue, "Values"),
        (MemberKind.Constructor, "Constructors"),
        (MemberKind.Property, "Properties"),
        (MemberKind.Indexer, "Indexers"),
        (MemberKind.Field, "Fields"),
        (MemberKind.Event, "Events"),
        (MemberKind.Method, "Methods"),
        (MemberKind.Operator, "Operators"),
    ];

    public static string RelativeLink(
        ApiType from,
        ApiType to)
    {
        if (!to.Package.IsLocal)
        {
            return (to.Package.Pages is null) ? to.Package.Home! : $"{to.Package.Pages}{to.PageName}.md";
        }

        return (from.Package == to.Package) ? $"{to.PageName}.md" : $"../{to.PagePath}";
    }

    public static void WriteAll(
        Model model,
        Package[] packages,
        Layout layout,
        Dictionary<string, string> output)
    {
        if (layout.WriteIndex)
        {
            output["README.md"] = Index(model, packages);
        }

        foreach (var package in packages.Where(p => p.IsLocal))
        {
            output[package.ReadmePath] = PackagePage(model, package, layout);
        }

        foreach (var type in model.Types.Where(t => t.Package.IsLocal))
        {
            output[type.PagePath] = TypePage(model, type);
        }
    }

    private const int CompactListSize = 20;

    private const string Generated ="<!-- Generated by tools/api-docs/generate.cs from the source's doc comments: edit those, not this. -->\n\n";

    private static string Index(
        Model model,
        Package[] packages)
    {
        var builder = new StringBuilder(Generated);
        builder.Append("# API reference\n\n");
        builder.Append("Every public type and member, generated from the source. For how to use them, start with the [guides](../README.md).\n\n");
        builder.Append("| Package | What it's for | Types |\n|---|---|---|\n");
        foreach (var package in packages)
        {
            int count = model.Types.Count(t => t.Package == package);
            builder.Append($"| [{package.Name}]({package.Home ?? package.ReadmePath}) | {package.Description} | {count} |\n");
        }

        return builder.ToString();
    }

    private static string PackagePage(
        Model model,
        Package package,
        Layout layout)
    {
        var builder = new StringBuilder(Generated);
        builder.Append($"# {package.Name}\n\n{package.Description}\n\n[{layout.AllPackagesText}]({layout.AllPackages})\n\n");
        foreach (var group in model.Types.Where(t => t.Package == package).GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.Append($"## {group.Key}\n\n| Type | Kind | Summary |\n|---|---|---|\n");
            foreach (var type in group.OrderBy(t => t.DisplayName, StringComparer.Ordinal))
            {
                builder.Append($"| [{Escape(type.DisplayName)}]({type.PageName}.md) | {type.Kind} | {FirstSentence(type.Summary(model)).Replace("](#", $"]({type.PageName}.md#")} |\n");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string TypePage(
        Model model,
        ApiType type)
    {
        var builder = new StringBuilder(Generated);
        builder.Append($"# {Escape(type.DisplayName)}\n\n");
        builder.Append($"[{type.Package.Name}](README.md) · `{type.Namespace}` · {type.Kind}\n\n");
        builder.Append($"```csharp\n{type.Declaration}\n```\n\n");
        WriteTypeLinks(builder, model, type, type.Declaration, memberName: null);

        var doc = type.Doc.Xml;
        if (Xml2Md.Inline(doc?.Element("summary"), model, type) is { } summary)
        {
            builder.Append(summary).Append("\n\n");
        }

        foreach (var param in doc?.Elements("typeparam") ?? [])
        {
            builder.Append($"**`{param.Attribute("name")?.Value}`:** {Xml2Md.Inline(param, model, type)}\n\n");
        }

        if (Xml2Md.Inline(doc?.Element("remarks"), model, type) is { } remarks)
        {
            builder.Append("## Remarks\n\n").Append(remarks).Append("\n\n");
        }

        foreach (var example in doc?.Elements("example") ?? [])
        {
            builder.Append("## Example\n\n").Append(Xml2Md.Inline(example, model, type)).Append("\n\n");
        }

        var nested = model.Types.Where(t => t.Parent == type).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (nested.Count > 0)
        {
            builder.Append("## Nested types\n\n| Type | Summary |\n|---|---|\n");
            foreach (var inner in nested)
            {
                builder.Append($"| [{Escape(inner.Name)}]({inner.PageName}.md) | {FirstSentence(inner.Summary(model)).Replace("](#", $"]({inner.PageName}.md#")} |\n");
            }

            builder.Append('\n');
        }

        foreach (var (kind, title) in Sections)
        {
            var members = type.Members.Where(m => m.Kind == kind).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            builder.Append($"## {title}\n\n");

            if (kind == MemberKind.EnumValue)
            {
                builder.Append("| Value | Summary |\n|---|---|\n");
                foreach (var member in members)
                {
                    // NOTE: A | in a table cell ends it, even in code: Both = Horizontal | Vertical
                    builder.Append($"| <a name=\"{member.Name.ToLowerInvariant()}\"></a>`{member.Signature.Replace("|", "\\|")}` | {OneLine(member.Summary(model))} |\n");
                }

                builder.Append('\n');
                continue;
            }

            // A long list of uncommented members is a palette or the like: their names say it all
            var plain = members.Where(m => (m.Summary(model) is null) && m.Signature.Contains(" static ")).ToList();
            if (plain.Count >= CompactListSize)
            {
                builder.Append(string.Join(", ", plain.Select(m => $"<a name=\"{m.Name.ToLowerInvariant()}\"></a>`{m.Name}`"))).Append("\n\n");
                members = members.Except(plain).ToList();
            }

            foreach (var group in members.GroupBy(m => m.Name).OrderBy(g => (kind == MemberKind.Constructor) ? string.Empty : g.Key, StringComparer.Ordinal))
            {
                if (kind != MemberKind.Constructor)
                {
                    builder.Append($"### {Escape(group.Key)}\n\n");
                }

                foreach (var member in group)
                {
                    WriteMember(builder, model, member);
                }
            }
        }

        return builder.ToString().TrimEnd() + "\n";
    }

    // NOTE: Markdown has no links in a code block, so the types a signature names follow it
    private static void WriteTypeLinks(
        StringBuilder builder,
        Model model,
        ApiType owner,
        string signature,
        string? memberName)
    {
        var names = Regex.Matches(signature, "\\b[A-Z][A-Za-z0-9_]*\\b").Select(m => m.Value).ToList();
        var seen = new HashSet<ApiType>();
        var links = new List<string>();
        foreach (var name in names)
        {
            // A member's own name isn't a mention of a type, unless the signature says it twice:
            // public Sound Sound { get; }
            if ((name == memberName) && (names.Count(n => n == name) == 1))
            {
                continue;
            }

            if ((model.FindType(name, owner) is { } type) && (type != owner) && seen.Add(type))
            {
                links.Add($"[`{Escape(type.DisplayName)}`]({RelativeLink(owner, type)})");
            }
        }

        if (links.Count > 0)
        {
            builder.Append($"Types: {string.Join(", ", links)}\n\n");
        }
    }

    private static void WriteMember(
        StringBuilder builder,
        Model model,
        ApiMember member)
    {
        builder.Append($"```csharp\n{member.Signature}\n```\n\n");
        WriteTypeLinks(builder, model, member.Owner, member.Signature, member.Name);

        var doc = member.Resolved(model).Xml;
        var owner = member.Owner;
        if (Xml2Md.Inline(doc?.Element("summary"), model, owner) is { } summary)
        {
            builder.Append(summary).Append("\n\n");
        }

        var parameters = doc?.Elements("param").ToList() ?? [];
        if (parameters.Count > 0)
        {
            builder.Append("| Parameter | |\n|---|---|\n");
            foreach (var parameter in parameters)
            {
                builder.Append($"| `{parameter.Attribute("name")?.Value}` | {OneLine(Xml2Md.Inline(parameter, model, owner))} |\n");
            }

            builder.Append('\n');
        }

        if (Xml2Md.Inline(doc?.Element("returns"), model, owner) is { } returns)
        {
            builder.Append($"**Returns:** {returns}\n\n");
        }

        foreach (var exception in doc?.Elements("exception") ?? [])
        {
            var name = exception.Attribute("cref")?.Value.Split('.').Last();
            builder.Append($"**Throws `{name}`:** {OneLine(Xml2Md.Inline(exception, model, owner))}\n\n");
        }

        if (Xml2Md.Inline(doc?.Element("remarks"), model, owner) is { } remarks)
        {
            builder.Append(remarks).Append("\n\n");
        }

        foreach (var example in doc?.Elements("example") ?? [])
        {
            builder.Append(Xml2Md.Inline(example, model, owner)).Append("\n\n");
        }
    }

    private static string Escape(
        string text)
    {
        return text.Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string OneLine(
        string? text)
    {
        return (text is null) ? string.Empty : text.Replace("\n\n", " ").Replace("\n", " ");
    }

    private static string FirstSentence(
        string? text)
    {
        var line = OneLine(text);
        var match = Regex.Match(line, @"^.*?(?<!\b(?:e\.g|i\.e|etc|vs))\.(?=\s|$)");
        return match.Success ? match.Value : line;
    }
}
