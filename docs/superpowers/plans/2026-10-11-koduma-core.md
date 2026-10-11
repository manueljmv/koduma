# Koduma Core (CLI + MCP) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Koduma v1 — a Native-AOT .NET binary that analyzes C# (Roslyn) and JS/TS (ESLint wrapper) repos for code smells, metrics and basic security issues, reporting results as table/JSON/HTML/SARIF, usable both as a CLI and as an embedded MCP server.

**Architecture:** A dependency-free `Koduma.Core` domain + orchestrator sits at the center; language-specific plugins (`Koduma.Analyzers.CSharp`, `Koduma.Analyzers.External`, `Koduma.Analyzers.Secrets`) implement a common `ILanguageAnalyzer` interface; `Koduma.Reporting` turns an `AnalysisResult` into table/JSON/HTML/SARIF; `Koduma.Cli` and `Koduma.Mcp` are two thin front ends over the same in-proc core, compiled into one Native AOT executable.

**Tech Stack:** .NET 10, `Microsoft.CodeAnalysis.CSharp` (Roslyn), `System.Text.Json` source generation, `ModelContextProtocol` (official MCP C# SDK, low-level handler API), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-11-koduma-core-design.md`

## Global Constraints

- Target framework for every project: `net10.0`.
- Only `Koduma.Cli` is published; it has `<PublishAot>true</PublishAot>`. Every library project must avoid reflection-based APIs so the published AOT binary works (no `JsonSerializer.Serialize<T>(obj)` without a source-generated context, no attribute+assembly-scanning for MCP tool discovery).
- All JSON (de)serialization of our own types uses a single `System.Text.Json` source-generated `JsonSerializerContext` (`KodumaJsonContext`), not reflection-based serialization.
- No persistence or state between runs (per spec) — every command/tool call is self-contained.
- Config file name: `koduma.json` (root of analyzed repo). Ignore file: `.kodumaignore`. Both optional.
- Rule ID format: `<PREFIX>-<CATEGORY>-<NNN>`, e.g. `CS-NAMING-001`, `SEC-SECRET-001`.
- CLI exit codes: `0` no blocker/critical issues, `1` blocker/critical issues found, `2` execution or config error.
- Install NuGet packages via `dotnet add package <Name>` without pinning an explicit version (let NuGet resolve the latest stable release at implementation time); do not hand-type a version number into a `.csproj`.
- Add every new project to `koduma.sln` via `dotnet sln add` in the same step that creates it.

## Review Focus

- Repo with zero issues and zero files of a given language → `analyze` must still exit `0` and produce valid (empty-but-well-formed) output in every requested format, not throw on empty collections. Covered in Task 14.
- A language is present in the repo but its external tool is missing from `PATH` (e.g. no `eslint`) → that language degrades to a recorded `AnalysisError`, the rest of the analysis still completes, exit code reflects only what was actually analyzed. Covered in Task 8/Task 5.
- Malformed or semantically invalid `koduma.json` (bad JSON, or a rule override shaped wrong) → CLI aborts before analyzing anything, exit code `2`, no stack trace printed. Covered in Task 4.
- Very large or binary files (e.g. a checked-in image or minified bundle) picked up by discovery → must not crash the analyzer or hang; discovery skips files above a size threshold and known binary extensions. Covered in Task 3.
- File paths containing spaces or non-ASCII characters → must survive discovery, issue reporting in every format, and the external process invocation (argument quoting for the `eslint` subprocess). Covered in Task 3 and Task 8.

---

## File Structure

```
koduma.sln
src/
  Koduma.Core/
    Koduma.Core.csproj
    Model/Severity.cs
    Model/Category.cs
    Model/IIssue.cs
    Model/Issue.cs
    Model/Rule.cs
    Model/AnalysisMetrics.cs
    Model/AnalysisError.cs
    Model/AnalysisResult.cs
    Model/KodumaJsonContext.cs
    Config/KodumaConfig.cs
    Config/RuleOverride.cs
    Config/KodumaConfigException.cs
    Config/KodumaConfigLoader.cs
    Discovery/IFileDiscovery.cs
    Discovery/FileDiscovery.cs
    Analysis/ILanguageAnalyzer.cs
    Analysis/LanguageAnalysisResult.cs
    Analysis/AnalysisOrchestrator.cs
  Koduma.Analyzers.Secrets/
    Koduma.Analyzers.Secrets.csproj
    SecretsAnalyzer.cs
  Koduma.Analyzers.CSharp/
    Koduma.Analyzers.CSharp.csproj
    CSharpAnalyzer.cs
    NamingRule.cs
    ComplexityRule.cs
  Koduma.Analyzers.External/
    Koduma.Analyzers.External.csproj
    ExternalProcessRunner.cs
    EslintAnalyzer.cs
    EslintOutputMapper.cs
  Koduma.Reporting/
    Koduma.Reporting.csproj
    IReportGenerator.cs
    TableReportGenerator.cs
    JsonReportGenerator.cs
    HtmlReportGenerator.cs
    SarifReportGenerator.cs
  Koduma.Cli/
    Koduma.Cli.csproj
    Program.cs
    AnalyzeCommand.cs
    RulesCommand.cs
  Koduma.Mcp/
    Koduma.Mcp.csproj
    KodumaMcpServer.cs
tests/
  Koduma.Core.Tests/
  Koduma.Analyzers.Secrets.Tests/
  Koduma.Analyzers.CSharp.Tests/
  Koduma.Analyzers.External.Tests/
  Koduma.Reporting.Tests/
  Koduma.Cli.Tests/
  Koduma.Integration.Tests/
```

---

### Task 1: Solution scaffold + Native AOT / Roslyn viability spike

This is a go/no-go check for the single riskiest assumption in the spec (Native AOT + Roslyn in-proc). It must run as a **published, standalone binary**, not `dotnet run`, because trimming/AOT problems only show up at publish time.

**Files:**
- Create: `koduma.sln`
- Create: `src/Koduma.Core/Koduma.Core.csproj` (empty class library, placeholder for later tasks)
- Create: `spike/AotRoslynSpike/AotRoslynSpike.csproj`
- Create: `spike/AotRoslynSpike/Program.cs`

**Interfaces:**
- Produces: nothing consumed by later tasks — this spike project is deleted in Task 1 Step 6 once it has done its job. `Koduma.Core.csproj` persists as the shell for Task 2.

- [ ] **Step 1: Create the solution and the empty Core project**

```bash
mkdir -p /home/manuel/projects/dotnet/koduma/src/Koduma.Core
cd /home/manuel/projects/dotnet/koduma
dotnet new sln -n koduma
dotnet new classlib -n Koduma.Core -o src/Koduma.Core -f net10.0
dotnet sln add src/Koduma.Core/Koduma.Core.csproj
rm src/Koduma.Core/Class1.cs
```

- [ ] **Step 2: Create the spike console project referencing Roslyn, with AOT enabled**

```bash
mkdir -p /home/manuel/projects/dotnet/koduma/spike/AotRoslynSpike
dotnet new console -n AotRoslynSpike -o spike/AotRoslynSpike -f net10.0
dotnet add spike/AotRoslynSpike/AotRoslynSpike.csproj package Microsoft.CodeAnalysis.CSharp
```

Edit `spike/AotRoslynSpike/AotRoslynSpike.csproj` so it contains:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write a minimal Roslyn analysis in Program.cs**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

const string source = """
public class Example
{
    private int badlyNamedField;

    public void Foo()
    {
        if (true) { }
    }
}
""";

var tree = CSharpSyntaxTree.ParseText(source);
var root = tree.GetCompilationUnitRoot();

var fieldCount = root.DescendantNodes()
    .OfType<FieldDeclarationSyntax>()
    .Count();

var compilation = CSharpCompilation.Create("spike")
    .AddSyntaxTrees(tree);

var model = compilation.GetSemanticModel(tree);
var diagnostics = compilation.GetDiagnostics();

Console.WriteLine($"FIELD_COUNT={fieldCount}");
Console.WriteLine($"DIAGNOSTIC_COUNT={diagnostics.Length}");
Console.WriteLine($"SEMANTIC_MODEL_NOT_NULL={model is not null}");
Console.WriteLine("SPIKE_OK");
```

- [ ] **Step 4: Publish as a real Native AOT binary and run it**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet publish spike/AotRoslynSpike/AotRoslynSpike.csproj -c Release -r linux-x64 --self-contained -o spike/AotRoslynSpike/publish
./spike/AotRoslynSpike/publish/AotRoslynSpike
```

Expected: the binary runs without a `MissingMethodException`/`NotSupportedException`/reflection-related crash, and prints:
```
FIELD_COUNT=1
DIAGNOSTIC_COUNT=0
SEMANTIC_MODEL_NOT_NULL=True
SPIKE_OK
```

- [ ] **Step 5: Decide and record the outcome**

If Step 4 prints `SPIKE_OK` with correct values: Native AOT + Roslyn in-proc is viable as designed — proceed to Task 2 unchanged.

If Step 4 fails (crash, wrong output, or trimming warnings that change behavior): stop and escalate to the user before continuing — this invalidates a Global Constraint and the rest of the plan needs re-scoping (e.g. drop `PublishAot` for `Koduma.Cli` and use a self-contained non-AOT publish instead). Do not silently work around it by deleting the spike and hoping — the spike exists specifically to catch this now.

- [ ] **Step 6: Remove the spike project and commit the scaffold**

```bash
cd /home/manuel/projects/dotnet/koduma
rm -rf spike
git add koduma.sln src/Koduma.Core
git commit -m "chore: scaffold solution, verify Native AOT + Roslyn viability"
```

---

### Task 2: Core domain model

**Files:**
- Create: `src/Koduma.Core/Model/Severity.cs`
- Create: `src/Koduma.Core/Model/Category.cs`
- Create: `src/Koduma.Core/Model/IIssue.cs`
- Create: `src/Koduma.Core/Model/Issue.cs`
- Create: `src/Koduma.Core/Model/Rule.cs`
- Create: `src/Koduma.Core/Model/AnalysisMetrics.cs`
- Create: `src/Koduma.Core/Model/AnalysisError.cs`
- Create: `src/Koduma.Core/Model/AnalysisResult.cs`
- Create: `src/Koduma.Core/Model/KodumaJsonContext.cs`
- Test: `tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj`
- Test: `tests/Koduma.Core.Tests/ModelSerializationTests.cs`

**Interfaces:**
- Produces: `Severity` enum (`Info, Minor, Major, Critical, Blocker`), `Category` enum (`CodeSmell, Bug, Security, Duplication`), `IIssue` interface, `Issue` record implementing it, `Rule` record, `AnalysisMetrics` record, `AnalysisError` record, `AnalysisResult` record, `KodumaJsonContext : JsonSerializerContext` covering all of the above — every later task serializes through this context.

- [ ] **Step 1: Create the test project**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new xunit -n Koduma.Core.Tests -o tests/Koduma.Core.Tests -f net10.0
dotnet sln add tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj
dotnet add tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj reference src/Koduma.Core/Koduma.Core.csproj
rm tests/Koduma.Core.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Koduma.Core.Tests/ModelSerializationTests.cs
using System.Text.Json;
using Koduma.Core.Model;
using Xunit;

public class ModelSerializationTests
{
    [Fact]
    public void AnalysisResult_RoundTrips_ThroughSourceGeneratedContext()
    {
        var issue = new Issue(
            RuleId: "CS-NAMING-001",
            Severity: Severity.Major,
            Category: Category.CodeSmell,
            FilePath: "src/Foo.cs",
            Line: 10,
            Column: 5,
            EndLine: 10,
            EndColumn: 20,
            Message: "Field should be camelCase with leading underscore.",
            Language: "csharp");

        var result = new AnalysisResult(
            ProjectPath: "/repo",
            Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DurationMs: 42,
            Issues: new IIssue[] { issue },
            Metrics: new AnalysisMetrics(LinesOfCode: 100, CyclomaticComplexity: 3, DuplicatedLines: 0, FilesAnalyzed: 1),
            Errors: Array.Empty<AnalysisError>());

        var json = JsonSerializer.Serialize(result, KodumaJsonContext.Default.AnalysisResult);
        var roundTripped = JsonSerializer.Deserialize(json, KodumaJsonContext.Default.AnalysisResult);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.Issues.Count);
        Assert.Equal("CS-NAMING-001", roundTripped.Issues[0].RuleId);
        Assert.Equal(Severity.Major, roundTripped.Issues[0].Severity);
        Assert.Equal(100, roundTripped.Metrics.LinesOfCode);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj`
Expected: FAIL to compile — `Koduma.Core.Model` namespace does not exist yet.

- [ ] **Step 4: Implement the model types**

```csharp
// src/Koduma.Core/Model/Severity.cs
namespace Koduma.Core.Model;

public enum Severity { Info, Minor, Major, Critical, Blocker }
```

```csharp
// src/Koduma.Core/Model/Category.cs
namespace Koduma.Core.Model;

public enum Category { CodeSmell, Bug, Security, Duplication }
```

```csharp
// src/Koduma.Core/Model/IIssue.cs
namespace Koduma.Core.Model;

public interface IIssue
{
    string RuleId { get; }
    Severity Severity { get; }
    Category Category { get; }
    string FilePath { get; }
    int Line { get; }
    int Column { get; }
    int EndLine { get; }
    int EndColumn { get; }
    string Message { get; }
    string Language { get; }
}
```

```csharp
// src/Koduma.Core/Model/Issue.cs
namespace Koduma.Core.Model;

public record Issue(
    string RuleId,
    Severity Severity,
    Category Category,
    string FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Message,
    string Language) : IIssue;
```

```csharp
// src/Koduma.Core/Model/Rule.cs
namespace Koduma.Core.Model;

public record Rule(
    string Id,
    string Name,
    Category Category,
    Severity DefaultSeverity,
    string Language,
    string? EngineRuleId);
```

```csharp
// src/Koduma.Core/Model/AnalysisMetrics.cs
namespace Koduma.Core.Model;

public record AnalysisMetrics(
    int LinesOfCode,
    int CyclomaticComplexity,
    int DuplicatedLines,
    int FilesAnalyzed);
```

```csharp
// src/Koduma.Core/Model/AnalysisError.cs
namespace Koduma.Core.Model;

public record AnalysisError(string Language, string Message);
```

```csharp
// src/Koduma.Core/Model/AnalysisResult.cs
namespace Koduma.Core.Model;

public record AnalysisResult(
    string ProjectPath,
    DateTimeOffset Timestamp,
    long DurationMs,
    IReadOnlyList<IIssue> Issues,
    AnalysisMetrics Metrics,
    IReadOnlyList<AnalysisError> Errors);
```

```csharp
// src/Koduma.Core/Model/KodumaJsonContext.cs
using System.Text.Json.Serialization;

namespace Koduma.Core.Model;

[JsonSerializable(typeof(AnalysisResult))]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(IIssue))]
[JsonSerializable(typeof(Rule))]
[JsonSerializable(typeof(Rule[]))]
[JsonSerializable(typeof(AnalysisMetrics))]
[JsonSerializable(typeof(AnalysisError))]
public partial class KodumaJsonContext : JsonSerializerContext
{
}
```

Note: `IIssue` is an interface — `Issue` instances stored in an `IReadOnlyList<IIssue>` need `JsonDerivedType`/polymorphism to deserialize back to `Issue`. Add it directly on the interface:

```csharp
// src/Koduma.Core/Model/IIssue.cs (replace the interface declaration)
using System.Text.Json.Serialization;

namespace Koduma.Core.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Issue), "issue")]
public interface IIssue
{
    string RuleId { get; }
    Severity Severity { get; }
    Category Category { get; }
    string FilePath { get; }
    int Line { get; }
    int Column { get; }
    int EndLine { get; }
    int EndColumn { get; }
    string Message { get; }
    string Language { get; }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Core tests/Koduma.Core.Tests koduma.sln
git commit -m "feat: add core domain model with AOT-safe JSON serialization"
```

---

### Task 3: File discovery

**Files:**
- Create: `src/Koduma.Core/Discovery/IFileDiscovery.cs`
- Create: `src/Koduma.Core/Discovery/FileDiscovery.cs`
- Test: `tests/Koduma.Core.Tests/FileDiscoveryTests.cs`

**Interfaces:**
- Consumes: `KodumaConfig` (from Task 4) — to avoid a circular task dependency, Task 3 is implemented first against a minimal `KodumaConfig` shape (`IReadOnlyList<string> Exclude`) that Task 4 will extend; declare that minimal shape here and let Task 4 add to it.
- Produces: `IFileDiscovery.DiscoverFiles(string rootPath, KodumaConfig config) -> IReadOnlyList<string>` — absolute file paths, used by the orchestrator in Task 5.

- [ ] **Step 1: Declare the minimal config shape this task needs**

```csharp
// src/Koduma.Core/Config/KodumaConfig.cs
namespace Koduma.Core.Config;

public record KodumaConfig(
    Dictionary<string, object> Rules,
    List<string> Exclude)
{
    public static KodumaConfig Default { get; } = new(new(), new());
}
```

(Task 4 will replace `Dictionary<string, object> Rules` with a typed `RuleOverride` map; nothing else in this task depends on `Rules`.)

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Koduma.Core.Tests/FileDiscoveryTests.cs
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Xunit;

public class FileDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-discovery-" + Guid.NewGuid());

    public FileDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void DiscoverFiles_SkipsGitignoredAndExcludedAndBinaryAndOversizedFiles()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P {}");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "ignored.cs"), "class I {}");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(_root, "weird name"));
        File.WriteAllText(Path.Combine(_root, "weird name", "spéciäl.cs"), "class W {}");
        File.WriteAllBytes(Path.Combine(_root, "photo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllText(Path.Combine(_root, "huge.cs"), new string('x', 11 * 1024 * 1024));

        var config = new KodumaConfig(new(), new List<string> { "**/excluded/**" });
        Directory.CreateDirectory(Path.Combine(_root, "excluded"));
        File.WriteAllText(Path.Combine(_root, "excluded", "skip.cs"), "class S {}");

        var files = new FileDiscovery().DiscoverFiles(_root, config);

        var relative = files.Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).ToHashSet();

        Assert.Contains("Program.cs", relative);
        Assert.Contains("weird name/spéciäl.cs", relative);
        Assert.DoesNotContain("bin/ignored.cs", relative);
        Assert.DoesNotContain("excluded/skip.cs", relative);
        Assert.DoesNotContain("photo.png", relative);
        Assert.DoesNotContain("huge.cs", relative);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter FileDiscoveryTests`
Expected: FAIL to compile — `IFileDiscovery`/`FileDiscovery` don't exist yet.

- [ ] **Step 4: Implement file discovery**

```csharp
// src/Koduma.Core/Discovery/IFileDiscovery.cs
using Koduma.Core.Config;

namespace Koduma.Core.Discovery;

public interface IFileDiscovery
{
    IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config);
}
```

```csharp
// src/Koduma.Core/Discovery/FileDiscovery.cs
using System.Text.RegularExpressions;
using Koduma.Core.Config;

namespace Koduma.Core.Discovery;

public class FileDiscovery : IFileDiscovery
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".pdf", ".zip", ".dll", ".exe", ".so", ".dylib"
    };

    public IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config)
    {
        var gitignorePatterns = LoadIgnorePatterns(Path.Combine(rootPath, ".gitignore"));
        var kodumaIgnorePatterns = LoadIgnorePatterns(Path.Combine(rootPath, ".kodumaignore"));
        var allPatterns = gitignorePatterns.Concat(kodumaIgnorePatterns).Concat(config.Exclude).ToList();
        var regexes = allPatterns.Select(GlobToRegex).ToList();

        var result = new List<string>();

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');

            if (relative.Split('/').Contains(".git"))
                continue;

            if (regexes.Any(r => r.IsMatch(relative)))
                continue;

            if (BinaryExtensions.Contains(Path.GetExtension(file)))
                continue;

            var info = new FileInfo(file);
            if (info.Length > MaxFileSizeBytes)
                continue;

            result.Add(file);
        }

        return result;
    }

    private static List<string> LoadIgnorePatterns(string path)
    {
        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.TrimEnd('/') + "/**")
            .ToList();
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            + "$";
        return new Regex(pattern, RegexOptions.Compiled);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter FileDiscoveryTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Core/Discovery src/Koduma.Core/Config tests/Koduma.Core.Tests/FileDiscoveryTests.cs
git commit -m "feat: add file discovery with gitignore/exclude/binary/size filtering"
```

---

### Task 4: `koduma.json` config loader

**Files:**
- Modify: `src/Koduma.Core/Config/KodumaConfig.cs`
- Create: `src/Koduma.Core/Config/RuleOverride.cs`
- Create: `src/Koduma.Core/Config/KodumaConfigException.cs`
- Create: `src/Koduma.Core/Config/KodumaConfigLoader.cs`
- Modify: `src/Koduma.Core/Model/KodumaJsonContext.cs`
- Test: `tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `KodumaConfigLoader.LoadOrDefault(string projectPath) -> KodumaConfig` (throws `KodumaConfigException` on invalid input), `KodumaConfig.Rules -> Dictionary<string, RuleOverride>`, `RuleOverride(bool? Enabled, Severity? Severity)` — consumed by the orchestrator (Task 5) and analyzers (Tasks 6-8) to decide whether/how to report a rule, and by the CLI (Task 11) to map a load failure to exit code 2.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class KodumaConfigLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-config-" + Guid.NewGuid());

    public KodumaConfigLoaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void LoadOrDefault_ReturnsDefault_WhenNoFileExists()
    {
        var config = KodumaConfigLoader.LoadOrDefault(_root);

        Assert.Empty(config.Rules);
        Assert.Empty(config.Exclude);
    }

    [Fact]
    public void LoadOrDefault_ParsesOffStringAndSeverityObjectOverrides()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        {
          "rules": {
            "CS-NAMING-001": "off",
            "SEC-SECRET-001": { "severity": "blocker" }
          },
          "exclude": ["**/bin/**"]
        }
        """);

        var config = KodumaConfigLoader.LoadOrDefault(_root);

        Assert.False(config.Rules["CS-NAMING-001"].Enabled);
        Assert.Equal(Severity.Blocker, config.Rules["SEC-SECRET-001"].Severity);
        Assert.Contains("**/bin/**", config.Exclude);
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenJsonIsMalformed()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), "{ not valid json");

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenRuleOverrideShapeIsInvalid()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "rules": { "CS-NAMING-001": 123 } }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter KodumaConfigLoaderTests`
Expected: FAIL to compile — `RuleOverride`/`KodumaConfigException`/`KodumaConfigLoader` don't exist yet.

- [ ] **Step 3: Implement the typed config, exception, and loader**

```csharp
// src/Koduma.Core/Config/RuleOverride.cs
using Koduma.Core.Model;

namespace Koduma.Core.Config;

public record RuleOverride(bool? Enabled, Severity? Severity);
```

```csharp
// src/Koduma.Core/Config/KodumaConfig.cs (replace file contents)
namespace Koduma.Core.Config;

public record KodumaConfig(
    Dictionary<string, RuleOverride> Rules,
    List<string> Exclude)
{
    public static KodumaConfig Default { get; } = new(new(), new());
}
```

```csharp
// src/Koduma.Core/Config/KodumaConfigException.cs
namespace Koduma.Core.Config;

public class KodumaConfigException : Exception
{
    public KodumaConfigException(string message, Exception? inner = null) : base(message, inner) { }
}
```

```csharp
// src/Koduma.Core/Config/KodumaConfigLoader.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Koduma.Core.Model;

namespace Koduma.Core.Config;

public static class KodumaConfigLoader
{
    public static KodumaConfig LoadOrDefault(string projectPath)
    {
        var path = Path.Combine(projectPath, "koduma.json");
        if (!File.Exists(path))
            return KodumaConfig.Default;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new KodumaConfigException($"koduma.json is not valid JSON: {ex.Message}", ex);
        }

        var rules = new Dictionary<string, RuleOverride>();
        if (root?["rules"] is JsonObject rulesObject)
        {
            foreach (var (ruleId, value) in rulesObject)
            {
                rules[ruleId] = ParseRuleOverride(ruleId, value);
            }
        }

        var exclude = new List<string>();
        if (root?["exclude"] is JsonArray excludeArray)
        {
            foreach (var entry in excludeArray)
            {
                if (entry?.GetValue<string>() is { } pattern)
                    exclude.Add(pattern);
            }
        }

        return new KodumaConfig(rules, exclude);
    }

    private static RuleOverride ParseRuleOverride(string ruleId, JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
                return new RuleOverride(Enabled: false, Severity: null);

            throw new KodumaConfigException($"Rule '{ruleId}': unsupported string value '{text}', expected \"off\".");
        }

        if (value is JsonObject obj)
        {
            Severity? severity = null;
            if (obj["severity"] is JsonValue severityValue && severityValue.TryGetValue<string>(out var severityText))
            {
                if (!Enum.TryParse<Severity>(severityText, ignoreCase: true, out var parsed))
                    throw new KodumaConfigException($"Rule '{ruleId}': unknown severity '{severityText}'.");
                severity = parsed;
            }

            return new RuleOverride(Enabled: null, Severity: severity);
        }

        throw new KodumaConfigException($"Rule '{ruleId}': expected \"off\" or an object with a \"severity\" field.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter KodumaConfigLoaderTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Core/Config tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs
git commit -m "feat: load and validate koduma.json rule overrides and excludes"
```

---

### Task 5: `ILanguageAnalyzer` + `AnalysisOrchestrator`

**Files:**
- Create: `src/Koduma.Core/Analysis/ILanguageAnalyzer.cs`
- Create: `src/Koduma.Core/Analysis/LanguageAnalysisResult.cs`
- Create: `src/Koduma.Core/Analysis/AnalysisOrchestrator.cs`
- Test: `tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: `IFileDiscovery` (Task 3), `KodumaConfig` (Task 4).
- Produces: `ILanguageAnalyzer { string Language; IReadOnlyCollection<string> FileExtensions; bool AppliesToAllFiles; Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken ct); }`, `LanguageAnalysisResult(IReadOnlyList<IIssue> Issues, AnalysisMetrics Metrics, string? Error)`, `AnalysisOrchestrator(IEnumerable<ILanguageAnalyzer> analyzers, IFileDiscovery fileDiscovery)` with `Task<AnalysisResult> AnalyzeAsync(string projectPath, KodumaConfig config, CancellationToken ct = default)` — this is what Tasks 6-8's analyzers plug into, and what the CLI (Task 11) and MCP server (Task 13) call directly.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using Xunit;

public class AnalysisOrchestratorTests
{
    private class FakeDiscovery : IFileDiscovery
    {
        public IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config) =>
            new[] { "/repo/a.cs", "/repo/b.js", "/repo/c.cs" };
    }

    private class FakeAnalyzer : ILanguageAnalyzer
    {
        private readonly Func<IReadOnlyList<string>, LanguageAnalysisResult> _handler;
        public FakeAnalyzer(string language, IReadOnlyCollection<string> extensions, bool appliesToAll, Func<IReadOnlyList<string>, LanguageAnalysisResult> handler)
        {
            Language = language;
            FileExtensions = extensions;
            AppliesToAllFiles = appliesToAll;
            _handler = handler;
        }

        public string Language { get; }
        public IReadOnlyCollection<string> FileExtensions { get; }
        public bool AppliesToAllFiles { get; }

        public Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken ct) =>
            Task.FromResult(_handler(filePaths));
    }

    [Fact]
    public async Task AnalyzeAsync_DispatchesFilesByExtension_AndAggregatesIssuesAndMetrics()
    {
        var csIssue = new Issue("CS-X-001", Severity.Major, Category.CodeSmell, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var csAnalyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, files =>
        {
            Assert.Equal(new[] { "/repo/a.cs", "/repo/c.cs" }, files);
            return new LanguageAnalysisResult(new IIssue[] { csIssue }, new AnalysisMetrics(10, 1, 0, 2), Error: null);
        });

        var allFilesAnalyzer = new FakeAnalyzer("security", Array.Empty<string>(), appliesToAll: true, files =>
        {
            Assert.Equal(3, files.Count);
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: null);
        });

        var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { csAnalyzer, allFilesAnalyzer }, new FakeDiscovery());

        var result = await orchestrator.AnalyzeAsync("/repo", KodumaConfig.Default);

        Assert.Single(result.Issues);
        Assert.Equal(10, result.Metrics.LinesOfCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsAnalysisError_ButKeepsOtherLanguagesResults()
    {
        var failingAnalyzer = new FakeAnalyzer("javascript", new[] { ".js" }, appliesToAll: false, _ =>
            new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: "eslint not found on PATH"));

        var okIssue = new Issue("CS-X-001", Severity.Minor, Category.CodeSmell, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var okAnalyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, _ =>
            new LanguageAnalysisResult(new IIssue[] { okIssue }, new AnalysisMetrics(5, 1, 0, 2), Error: null));

        var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { failingAnalyzer, okAnalyzer }, new FakeDiscovery());

        var result = await orchestrator.AnalyzeAsync("/repo", KodumaConfig.Default);

        Assert.Single(result.Errors);
        Assert.Equal("javascript", result.Errors[0].Language);
        Assert.Single(result.Issues);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter AnalysisOrchestratorTests`
Expected: FAIL to compile — types don't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/Koduma.Core/Analysis/ILanguageAnalyzer.cs
using Koduma.Core.Config;

namespace Koduma.Core.Analysis;

public interface ILanguageAnalyzer
{
    string Language { get; }
    IReadOnlyCollection<string> FileExtensions { get; }
    bool AppliesToAllFiles { get; }

    Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken);
}
```

```csharp
// src/Koduma.Core/Analysis/LanguageAnalysisResult.cs
using Koduma.Core.Model;

namespace Koduma.Core.Analysis;

public record LanguageAnalysisResult(IReadOnlyList<IIssue> Issues, AnalysisMetrics Metrics, string? Error);
```

```csharp
// src/Koduma.Core/Analysis/AnalysisOrchestrator.cs
using System.Diagnostics;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;

namespace Koduma.Core.Analysis;

public class AnalysisOrchestrator
{
    private readonly IReadOnlyList<ILanguageAnalyzer> _analyzers;
    private readonly IFileDiscovery _fileDiscovery;

    public AnalysisOrchestrator(IEnumerable<ILanguageAnalyzer> analyzers, IFileDiscovery fileDiscovery)
    {
        _analyzers = analyzers.ToList();
        _fileDiscovery = fileDiscovery;
    }

    public async Task<AnalysisResult> AnalyzeAsync(string projectPath, KodumaConfig config, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var allFiles = _fileDiscovery.DiscoverFiles(projectPath, config);

        var tasks = _analyzers.Select(async analyzer =>
        {
            var filesForAnalyzer = analyzer.AppliesToAllFiles
                ? allFiles
                : allFiles.Where(f => analyzer.FileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();

            if (filesForAnalyzer.Count == 0)
                return (analyzer.Language, new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: null));

            var languageResult = await analyzer.AnalyzeAsync(filesForAnalyzer, config, cancellationToken);
            return (analyzer.Language, languageResult);
        });

        var perLanguageResults = await Task.WhenAll(tasks);

        var issues = new List<IIssue>();
        var errors = new List<AnalysisError>();
        var totalLoc = 0;
        var totalComplexity = 0;
        var totalDuplicated = 0;
        var totalFiles = 0;

        foreach (var (language, languageResult) in perLanguageResults)
        {
            if (languageResult.Error is { } error)
            {
                errors.Add(new AnalysisError(language, error));
                continue;
            }

            issues.AddRange(languageResult.Issues);
            totalLoc += languageResult.Metrics.LinesOfCode;
            totalComplexity += languageResult.Metrics.CyclomaticComplexity;
            totalDuplicated += languageResult.Metrics.DuplicatedLines;
            totalFiles += languageResult.Metrics.FilesAnalyzed;
        }

        stopwatch.Stop();

        return new AnalysisResult(
            ProjectPath: projectPath,
            Timestamp: DateTimeOffset.UtcNow,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Issues: issues,
            Metrics: new AnalysisMetrics(totalLoc, totalComplexity, totalDuplicated, totalFiles),
            Errors: errors);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter AnalysisOrchestratorTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Core/Analysis tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs
git commit -m "feat: add language analyzer interface and orchestrator with partial degradation"
```

---

### Task 6: Secrets analyzer (language-agnostic SAST)

**Files:**
- Create: `src/Koduma.Analyzers.Secrets/Koduma.Analyzers.Secrets.csproj`
- Create: `src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj`
- Test: `tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs`

**Interfaces:**
- Consumes: `ILanguageAnalyzer`, `LanguageAnalysisResult`, `KodumaConfig` (Task 5/4).
- Produces: `SecretsAnalyzer : ILanguageAnalyzer` with `Language = "security"`, `AppliesToAllFiles = true` — registered alongside the other analyzers in the CLI/MCP composition root (Tasks 11/13).

- [ ] **Step 1: Create the project**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new classlib -n Koduma.Analyzers.Secrets -o src/Koduma.Analyzers.Secrets -f net10.0
dotnet sln add src/Koduma.Analyzers.Secrets/Koduma.Analyzers.Secrets.csproj
dotnet add src/Koduma.Analyzers.Secrets/Koduma.Analyzers.Secrets.csproj reference src/Koduma.Core/Koduma.Core.csproj
rm src/Koduma.Analyzers.Secrets/Class1.cs

dotnet new xunit -n Koduma.Analyzers.Secrets.Tests -o tests/Koduma.Analyzers.Secrets.Tests -f net10.0
dotnet sln add tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj
dotnet add tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj reference src/Koduma.Analyzers.Secrets/Koduma.Analyzers.Secrets.csproj
rm tests/Koduma.Analyzers.Secrets.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs
using Koduma.Analyzers.Secrets;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class SecretsAnalyzerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "koduma-secret-" + Guid.NewGuid() + ".txt");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task AnalyzeAsync_FlagsHardcodedAwsKeyAndPassword_ButNotOrdinaryCode()
    {
        File.WriteAllText(_file, """
        var ordinary = "hello world";
        var key = "AKIAABCDEFGHIJKLMNOP";
        var password = "SuperSecret123!";
        """);

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, i => Assert.Equal("SEC-SECRET-001", i.RuleId));
        Assert.All(result.Issues, i => Assert.Equal(Category.Security, i.Category));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj`
Expected: FAIL to compile — `SecretsAnalyzer` doesn't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs
using System.Text.RegularExpressions;
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Analyzers.Secrets;

public class SecretsAnalyzer : ILanguageAnalyzer
{
    public string Language => "security";
    public IReadOnlyCollection<string> FileExtensions => Array.Empty<string>();
    public bool AppliesToAllFiles => true;

    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("AWS access key", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("Hardcoded password", new Regex("""password\s*=\s*"[^"]{8,}"""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var issues = new List<IIssue>();

        foreach (var file in filePaths)
        {
            var lines = File.ReadAllLines(file);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var (name, pattern) in Patterns)
                {
                    var match = pattern.Match(lines[lineIndex]);
                    if (!match.Success)
                        continue;

                    issues.Add(new Issue(
                        RuleId: "SEC-SECRET-001",
                        Severity: Severity.Critical,
                        Category: Category.Security,
                        FilePath: file,
                        Line: lineIndex + 1,
                        Column: match.Index + 1,
                        EndLine: lineIndex + 1,
                        EndColumn: match.Index + match.Length + 1,
                        Message: $"Possible hardcoded secret ({name}).",
                        Language: "security"));
                }
            }
        }

        return Task.FromResult(new LanguageAnalysisResult(issues, new AnalysisMetrics(0, 0, 0, filePaths.Count), Error: null));
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.Secrets tests/Koduma.Analyzers.Secrets.Tests koduma.sln
git commit -m "feat: add language-agnostic secrets analyzer"
```

---

### Task 7: C# analyzer (Roslyn) — naming rule + complexity metric

**Files:**
- Create: `src/Koduma.Analyzers.CSharp/Koduma.Analyzers.CSharp.csproj`
- Create: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`
- Create: `src/Koduma.Analyzers.CSharp/NamingRule.cs`
- Create: `src/Koduma.Analyzers.CSharp/ComplexityRule.cs`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/Koduma.Analyzers.CSharp.Tests.csproj`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`

**Interfaces:**
- Consumes: `ILanguageAnalyzer`, `LanguageAnalysisResult` (Task 5).
- Produces: `CSharpAnalyzer : ILanguageAnalyzer` with `Language = "csharp"`, `FileExtensions = [".cs"]`, rules `CS-NAMING-001` and `CS-COMPLEXITY-001` — registered in Tasks 11/13.

- [ ] **Step 1: Create the projects**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new classlib -n Koduma.Analyzers.CSharp -o src/Koduma.Analyzers.CSharp -f net10.0
dotnet sln add src/Koduma.Analyzers.CSharp/Koduma.Analyzers.CSharp.csproj
dotnet add src/Koduma.Analyzers.CSharp/Koduma.Analyzers.CSharp.csproj reference src/Koduma.Core/Koduma.Core.csproj
dotnet add src/Koduma.Analyzers.CSharp/Koduma.Analyzers.CSharp.csproj package Microsoft.CodeAnalysis.CSharp
rm src/Koduma.Analyzers.CSharp/Class1.cs

dotnet new xunit -n Koduma.Analyzers.CSharp.Tests -o tests/Koduma.Analyzers.CSharp.Tests -f net10.0
dotnet sln add tests/Koduma.Analyzers.CSharp.Tests/Koduma.Analyzers.CSharp.Tests.csproj
dotnet add tests/Koduma.Analyzers.CSharp.Tests/Koduma.Analyzers.CSharp.Tests.csproj reference src/Koduma.Analyzers.CSharp/Koduma.Analyzers.CSharp.csproj
rm tests/Koduma.Analyzers.CSharp.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs
using Koduma.Analyzers.CSharp;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class CSharpAnalyzerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "koduma-cs-" + Guid.NewGuid() + ".cs");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task AnalyzeAsync_FlagsBadFieldNaming_AndHighComplexityMethod()
    {
        File.WriteAllText(_file, """
        public class Example
        {
            private int badlyNamedField;

            public void Complex(int x)
            {
                if (x == 1) { }
                else if (x == 2) { }
                else if (x == 3) { }
                else if (x == 4) { }
                else if (x == 5) { }
                else if (x == 6) { }
                else if (x == 7) { }
                else if (x == 8) { }
                else if (x == 9) { }
                else if (x == 10) { }
                else if (x == 11) { }
            }
        }
        """);

        var analyzer = new CSharpAnalyzer();
        var result = await analyzer.AnalyzeAsync(new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        Assert.Contains(result.Issues, i => i.RuleId == "CS-NAMING-001" && i.Message.Contains("badlyNamedField"));
        Assert.Contains(result.Issues, i => i.RuleId == "CS-COMPLEXITY-001" && i.Message.Contains("Complex"));
        Assert.True(result.Metrics.LinesOfCode > 0);
        Assert.True(result.Metrics.CyclomaticComplexity >= 11);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests/Koduma.Analyzers.CSharp.Tests.csproj`
Expected: FAIL to compile — `CSharpAnalyzer` doesn't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/Koduma.Analyzers.CSharp/NamingRule.cs
using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class NamingRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.PrivateKeyword))
                continue;

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                if (name.StartsWith('_'))
                    continue;

                var lineSpan = variable.GetLocation().GetLineSpan();
                yield return new Issue(
                    RuleId: "CS-NAMING-001",
                    Severity: Severity.Minor,
                    Category: Category.CodeSmell,
                    FilePath: filePath,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Column: lineSpan.StartLinePosition.Character + 1,
                    EndLine: lineSpan.EndLinePosition.Line + 1,
                    EndColumn: lineSpan.EndLinePosition.Character + 1,
                    Message: $"Private field '{name}' should be named with a leading underscore.",
                    Language: "csharp");
            }
        }
    }
}
```

```csharp
// src/Koduma.Analyzers.CSharp/ComplexityRule.cs
using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class ComplexityRule
{
    private const int Threshold = 10;

    public static (IEnumerable<IIssue> Issues, int TotalComplexity) Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();
        var issues = new List<IIssue>();
        var total = 0;

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var complexity = 1 + method.DescendantNodes().Count(n =>
                n is IfStatementSyntax or ForStatementSyntax or WhileStatementSyntax or
                     CaseSwitchLabelSyntax or ConditionalExpressionSyntax or CatchClauseSyntax);

            total += complexity;

            if (complexity > Threshold)
            {
                var lineSpan = method.Identifier.GetLocation().GetLineSpan();
                issues.Add(new Issue(
                    RuleId: "CS-COMPLEXITY-001",
                    Severity: Severity.Major,
                    Category: Category.CodeSmell,
                    FilePath: filePath,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Column: lineSpan.StartLinePosition.Character + 1,
                    EndLine: lineSpan.EndLinePosition.Line + 1,
                    EndColumn: lineSpan.EndLinePosition.Character + 1,
                    Message: $"Method '{method.Identifier.ValueText}' has cyclomatic complexity {complexity}, exceeding {Threshold}.",
                    Language: "csharp"));
            }
        }

        return (issues, total);
    }
}
```

```csharp
// src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Microsoft.CodeAnalysis.CSharp;

namespace Koduma.Analyzers.CSharp;

public class CSharpAnalyzer : ILanguageAnalyzer
{
    public string Language => "csharp";
    public IReadOnlyCollection<string> FileExtensions => new[] { ".cs" };
    public bool AppliesToAllFiles => false;

    public Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var issues = new List<IIssue>();
        var totalLoc = 0;
        var totalComplexity = 0;

        foreach (var file in filePaths)
        {
            var source = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(source, path: file);

            totalLoc += source.Split('\n').Length;
            issues.AddRange(NamingRule.Evaluate(tree, file));

            var (complexityIssues, complexity) = ComplexityRule.Evaluate(tree, file);
            issues.AddRange(complexityIssues);
            totalComplexity += complexity;
        }

        var metrics = new AnalysisMetrics(totalLoc, totalComplexity, DuplicatedLines: 0, FilesAnalyzed: filePaths.Count);
        return Task.FromResult(new LanguageAnalysisResult(issues, metrics, Error: null));
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests/Koduma.Analyzers.CSharp.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.CSharp tests/Koduma.Analyzers.CSharp.Tests koduma.sln
git commit -m "feat: add Roslyn-based C# analyzer with naming and complexity rules"
```

---

### Task 8: External analyzer (ESLint wrapper)

**Files:**
- Create: `src/Koduma.Analyzers.External/Koduma.Analyzers.External.csproj`
- Create: `src/Koduma.Analyzers.External/ExternalProcessRunner.cs`
- Create: `src/Koduma.Analyzers.External/EslintOutputMapper.cs`
- Create: `src/Koduma.Analyzers.External/EslintAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj`
- Test: `tests/Koduma.Analyzers.External.Tests/EslintOutputMapperTests.cs`
- Test: `tests/Koduma.Analyzers.External.Tests/ExternalProcessRunnerTests.cs`

**Interfaces:**
- Consumes: `ILanguageAnalyzer`, `LanguageAnalysisResult` (Task 5).
- Produces: `EslintAnalyzer : ILanguageAnalyzer` with `Language = "javascript"`, `FileExtensions = [".js", ".jsx", ".ts", ".tsx"]` — registered in Tasks 11/13. `ExternalProcessRunner.Run(string executable, IReadOnlyList<string> arguments) -> (int ExitCode, string StdOut, string StdErr)`, reusable by future external-tool plugins.

- [ ] **Step 1: Create the projects**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new classlib -n Koduma.Analyzers.External -o src/Koduma.Analyzers.External -f net10.0
dotnet sln add src/Koduma.Analyzers.External/Koduma.Analyzers.External.csproj
dotnet add src/Koduma.Analyzers.External/Koduma.Analyzers.External.csproj reference src/Koduma.Core/Koduma.Core.csproj
rm src/Koduma.Analyzers.External/Class1.cs

dotnet new xunit -n Koduma.Analyzers.External.Tests -o tests/Koduma.Analyzers.External.Tests -f net10.0
dotnet sln add tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj
dotnet add tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj reference src/Koduma.Analyzers.External/Koduma.Analyzers.External.csproj
rm tests/Koduma.Analyzers.External.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write the failing test for argument quoting (handles spaces/non-ASCII paths)**

```csharp
// tests/Koduma.Analyzers.External.Tests/ExternalProcessRunnerTests.cs
using Koduma.Analyzers.External;
using Xunit;

public class ExternalProcessRunnerTests
{
    [Fact]
    public async Task Run_PassesArgumentsWithSpacesAndNonAsciiCharacters_Verbatim()
    {
        var weirdPath = "/tmp/weird name/spéciäl.js";

        var runner = new ExternalProcessRunner();
        var (exitCode, stdOut, _) = await runner.RunAsync("echo", new[] { weirdPath });

        Assert.Equal(0, exitCode);
        Assert.Contains(weirdPath, stdOut);
    }

    [Fact]
    public async Task Run_ReturnsNonZeroExitAndStdErr_WhenExecutableDoesNotExist()
    {
        var runner = new ExternalProcessRunner();

        var (exitCode, _, _) = await runner.RunAsync("koduma-nonexistent-tool-xyz", new[] { "arg" });

        Assert.NotEqual(0, exitCode);
    }
}
```

```csharp
// tests/Koduma.Analyzers.External.Tests/EslintOutputMapperTests.cs
using Koduma.Analyzers.External;
using Koduma.Core.Model;
using Xunit;

public class EslintOutputMapperTests
{
    [Fact]
    public void Map_TranslatesEslintJsonToIssues()
    {
        const string eslintJson = """
        [
          {
            "filePath": "/repo/app.js",
            "messages": [
              { "ruleId": "no-unused-vars", "severity": 2, "message": "unused var", "line": 3, "column": 5, "endLine": 3, "endColumn": 10 },
              { "ruleId": "no-console", "severity": 1, "message": "no console", "line": 7, "column": 1, "endLine": 7, "endColumn": 8 }
            ]
          }
        ]
        """;

        var issues = EslintOutputMapper.Map(eslintJson);

        Assert.Equal(2, issues.Count);
        Assert.Equal(Severity.Major, issues[0].Severity);
        Assert.Equal(Severity.Minor, issues[1].Severity);
        Assert.Equal("javascript", issues[0].Language);
        Assert.Equal("/repo/app.js", issues[0].FilePath);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj`
Expected: FAIL to compile — types don't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/Koduma.Analyzers.External/ExternalProcessRunner.cs
using System.Diagnostics;

namespace Koduma.Analyzers.External;

public class ExternalProcessRunner
{
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{executable}'.");

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
```

```csharp
// src/Koduma.Analyzers.External/EslintOutputMapper.cs
using System.Text.Json;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

public static class EslintOutputMapper
{
    public static List<IIssue> Map(string eslintJson)
    {
        var issues = new List<IIssue>();
        using var document = JsonDocument.Parse(eslintJson);

        foreach (var fileResult in document.RootElement.EnumerateArray())
        {
            var filePath = fileResult.GetProperty("filePath").GetString() ?? "";

            foreach (var message in fileResult.GetProperty("messages").EnumerateArray())
            {
                var eslintSeverity = message.GetProperty("severity").GetInt32();
                var line = message.GetProperty("line").GetInt32();
                var column = message.GetProperty("column").GetInt32();
                var endLine = message.TryGetProperty("endLine", out var el) ? el.GetInt32() : line;
                var endColumn = message.TryGetProperty("endColumn", out var ec) ? ec.GetInt32() : column;
                var ruleId = message.TryGetProperty("ruleId", out var r) ? r.GetString() ?? "unknown" : "unknown";

                issues.Add(new Issue(
                    RuleId: $"JS-{ruleId}",
                    Severity: eslintSeverity == 2 ? Severity.Major : Severity.Minor,
                    Category: Category.CodeSmell,
                    FilePath: filePath,
                    Line: line,
                    Column: column,
                    EndLine: endLine,
                    EndColumn: endColumn,
                    Message: message.GetProperty("message").GetString() ?? "",
                    Language: "javascript"));
            }
        }

        return issues;
    }
}
```

```csharp
// src/Koduma.Analyzers.External/EslintAnalyzer.cs
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

public class EslintAnalyzer : ILanguageAnalyzer
{
    private readonly ExternalProcessRunner _runner;

    public EslintAnalyzer(ExternalProcessRunner runner) => _runner = runner;

    public string Language => "javascript";
    public IReadOnlyCollection<string> FileExtensions => new[] { ".js", ".jsx", ".ts", ".tsx" };
    public bool AppliesToAllFiles => false;

    public async Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--format", "json" };
        arguments.AddRange(filePaths);

        var (exitCode, stdOut, stdErr) = await _runner.RunAsync("eslint", arguments);

        if (exitCode == -1)
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: $"eslint could not be run: {stdErr}");

        // ESLint exits 1 when it finds lint errors, which is expected, not a failure.
        if (string.IsNullOrWhiteSpace(stdOut))
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, filePaths.Count), Error: null);

        var issues = EslintOutputMapper.Map(stdOut);
        return new LanguageAnalysisResult(issues, new AnalysisMetrics(0, 0, 0, filePaths.Count), Error: null);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj`
Expected: PASS (the `echo` and nonexistent-tool tests exercise argument passing and the missing-executable path without needing `eslint` installed; they must pass regardless of whether `eslint` is on the machine, satisfying the Review Focus item on missing external tools).

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.External tests/Koduma.Analyzers.External.Tests koduma.sln
git commit -m "feat: add ESLint-wrapping analyzer with graceful degradation on missing tool"
```

---

### Task 9: Reporting — Table + JSON generators

**Files:**
- Create: `src/Koduma.Reporting/Koduma.Reporting.csproj`
- Create: `src/Koduma.Reporting/IReportGenerator.cs`
- Create: `src/Koduma.Reporting/TableReportGenerator.cs`
- Create: `src/Koduma.Reporting/JsonReportGenerator.cs`
- Test: `tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj`
- Test: `tests/Koduma.Reporting.Tests/TableReportGeneratorTests.cs`
- Test: `tests/Koduma.Reporting.Tests/JsonReportGeneratorTests.cs`

**Interfaces:**
- Consumes: `AnalysisResult`, `KodumaJsonContext` (Task 2).
- Produces: `IReportGenerator { string Format; void Write(AnalysisResult result, TextWriter writer); }`, `TableReportGenerator` (`Format = "table"`), `JsonReportGenerator` (`Format = "json"`) — consumed by the CLI (Task 11) and the Review Focus "empty repo" test (Task 14).

- [ ] **Step 1: Create the projects**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new classlib -n Koduma.Reporting -o src/Koduma.Reporting -f net10.0
dotnet sln add src/Koduma.Reporting/Koduma.Reporting.csproj
dotnet add src/Koduma.Reporting/Koduma.Reporting.csproj reference src/Koduma.Core/Koduma.Core.csproj
rm src/Koduma.Reporting/Class1.cs

dotnet new xunit -n Koduma.Reporting.Tests -o tests/Koduma.Reporting.Tests -f net10.0
dotnet sln add tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj
dotnet add tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj reference src/Koduma.Reporting/Koduma.Reporting.csproj
rm tests/Koduma.Reporting.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Koduma.Reporting.Tests/TableReportGeneratorTests.cs
using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class TableReportGeneratorTests
{
    [Fact]
    public void Write_IncludesEachIssueAndSummaryCounts_EvenWhenEmpty()
    {
        var emptyResult = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();
        new TableReportGenerator().Write(emptyResult, writer);

        Assert.Contains("0 issues", writer.ToString());

        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "src/A.cs", 1, 1, 1, 1, "bad name", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer2 = new StringWriter();
        new TableReportGenerator().Write(result, writer2);

        Assert.Contains("CS-NAMING-001", writer2.ToString());
        Assert.Contains("src/A.cs", writer2.ToString());
    }
}
```

```csharp
// tests/Koduma.Reporting.Tests/JsonReportGeneratorTests.cs
using System.Text.Json;
using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class JsonReportGeneratorTests
{
    [Fact]
    public void Write_ProducesValidJson_ParseableBackToAnalysisResult()
    {
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new JsonReportGenerator().Write(result, writer);

        var parsed = JsonSerializer.Deserialize(writer.ToString(), KodumaJsonContext.Default.AnalysisResult);
        Assert.NotNull(parsed);
        Assert.Equal("/repo", parsed!.ProjectPath);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj`
Expected: FAIL to compile — types don't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/Koduma.Reporting/IReportGenerator.cs
using Koduma.Core.Model;

namespace Koduma.Reporting;

public interface IReportGenerator
{
    string Format { get; }
    void Write(AnalysisResult result, TextWriter writer);
}
```

```csharp
// src/Koduma.Reporting/TableReportGenerator.cs
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class TableReportGenerator : IReportGenerator
{
    public string Format => "table";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        writer.WriteLine($"Koduma analysis of {result.ProjectPath} ({result.DurationMs} ms)");
        writer.WriteLine($"Files analyzed: {result.Metrics.FilesAnalyzed}, LOC: {result.Metrics.LinesOfCode}");
        writer.WriteLine();

        foreach (var issue in result.Issues)
        {
            writer.WriteLine($"[{issue.Severity}] {issue.RuleId} {issue.FilePath}:{issue.Line}:{issue.Column} — {issue.Message}");
        }

        foreach (var error in result.Errors)
        {
            writer.WriteLine($"[ERROR] {error.Language}: {error.Message}");
        }

        writer.WriteLine();
        writer.WriteLine($"{result.Issues.Count} issues, {result.Errors.Count} errors");
    }
}
```

```csharp
// src/Koduma.Reporting/JsonReportGenerator.cs
using System.Text.Json;
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class JsonReportGenerator : IReportGenerator
{
    public string Format => "json";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        var json = JsonSerializer.Serialize(result, KodumaJsonContext.Default.AnalysisResult);
        writer.Write(json);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Reporting tests/Koduma.Reporting.Tests koduma.sln
git commit -m "feat: add table and JSON report generators"
```

---

### Task 10: Reporting — HTML + SARIF generators

**Files:**
- Create: `src/Koduma.Reporting/HtmlReportGenerator.cs`
- Create: `src/Koduma.Reporting/SarifReportGenerator.cs`
- Create: `src/Koduma.Core/Model/KodumaJsonContext.cs` (modify — add SARIF DTOs)
- Test: `tests/Koduma.Reporting.Tests/HtmlReportGeneratorTests.cs`
- Test: `tests/Koduma.Reporting.Tests/SarifReportGeneratorTests.cs`

**Interfaces:**
- Consumes: `AnalysisResult` (Task 2), `IReportGenerator` (Task 9).
- Produces: `HtmlReportGenerator` (`Format = "html"`), `SarifReportGenerator` (`Format = "sarif"`) — consumed by the CLI (Task 11).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Koduma.Reporting.Tests/HtmlReportGeneratorTests.cs
using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class HtmlReportGeneratorTests
{
    [Fact]
    public void Write_ProducesSelfContainedHtml_EscapingUserContent()
    {
        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "src/<A>.cs", 1, 1, 1, 1, "<script>alert(1)</script>", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new HtmlReportGenerator().Write(result, writer);
        var html = writer.ToString();

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
```

```csharp
// tests/Koduma.Reporting.Tests/SarifReportGeneratorTests.cs
using System.Text.Json;
using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class SarifReportGeneratorTests
{
    [Fact]
    public void Write_ProducesValidSarifWithOneResultPerIssue()
    {
        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "src/A.cs", 1, 1, 1, 1, "bad name", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new SarifReportGenerator().Write(result, writer);
        using var document = JsonDocument.Parse(writer.ToString());

        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("CS-NAMING-001", results[0].GetProperty("ruleId").GetString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj`
Expected: FAIL to compile — types don't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/Koduma.Reporting/HtmlReportGenerator.cs
using System.Net;
using System.Text;
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class HtmlReportGenerator : IReportGenerator
{
    public string Format => "html";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Koduma report</title>");
        sb.AppendLine("<style>body{font-family:sans-serif} table{border-collapse:collapse;width:100%} td,th{border:1px solid #ccc;padding:4px}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>Koduma report — {WebUtility.HtmlEncode(result.ProjectPath)}</h1>");
        sb.AppendLine($"<p>{result.Metrics.FilesAnalyzed} files, {result.Metrics.LinesOfCode} LOC, {result.Issues.Count} issues</p>");
        sb.AppendLine("<table><tr><th>Severity</th><th>Rule</th><th>File</th><th>Line</th><th>Message</th></tr>");

        foreach (var issue in result.Issues)
        {
            sb.AppendLine("<tr>" +
                $"<td>{WebUtility.HtmlEncode(issue.Severity.ToString())}</td>" +
                $"<td>{WebUtility.HtmlEncode(issue.RuleId)}</td>" +
                $"<td>{WebUtility.HtmlEncode(issue.FilePath)}</td>" +
                $"<td>{issue.Line}</td>" +
                $"<td>{WebUtility.HtmlEncode(issue.Message)}</td>" +
                "</tr>");
        }

        sb.AppendLine("</table></body></html>");
        writer.Write(sb.ToString());
    }
}
```

```csharp
// src/Koduma.Reporting/SarifReportGenerator.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class SarifReportGenerator : IReportGenerator
{
    public string Format => "sarif";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        var results = new JsonArray();
        foreach (var issue in result.Issues)
        {
            results.Add(new JsonObject
            {
                ["ruleId"] = issue.RuleId,
                ["level"] = MapLevel(issue.Severity),
                ["message"] = new JsonObject { ["text"] = issue.Message },
                ["locations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject { ["uri"] = issue.FilePath },
                            ["region"] = new JsonObject
                            {
                                ["startLine"] = issue.Line,
                                ["startColumn"] = issue.Column,
                                ["endLine"] = issue.EndLine,
                                ["endColumn"] = issue.EndColumn,
                            }
                        }
                    }
                }
            });
        }

        var sarif = new JsonObject
        {
            ["version"] = "2.1.0",
            ["$schema"] = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject { ["name"] = "koduma", ["version"] = "0.1.0" }
                    },
                    ["results"] = results
                }
            }
        };

        writer.Write(sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string MapLevel(Severity severity) => severity switch
    {
        Severity.Blocker or Severity.Critical => "error",
        Severity.Major => "warning",
        _ => "note",
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Reporting tests/Koduma.Reporting.Tests
git commit -m "feat: add HTML and SARIF report generators"
```

---

### Task 11: CLI — argument parsing, `analyze` command, exit codes

**Files:**
- Create: `src/Koduma.Cli/Koduma.Cli.csproj`
- Create: `src/Koduma.Cli/Program.cs`
- Create: `src/Koduma.Cli/AnalyzeCommand.cs`
- Test: `tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`
- Test: `tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs`

**Interfaces:**
- Consumes: `AnalysisOrchestrator` (Task 5), `KodumaConfigLoader`/`KodumaConfigException` (Task 4), `IReportGenerator` implementations (Tasks 9-10), `CSharpAnalyzer`/`EslintAnalyzer`/`SecretsAnalyzer` (Tasks 6-8).
- Produces: `AnalyzeCommand.Run(string[] args, IReadOnlyList<ILanguageAnalyzer> analyzers, IReadOnlyList<IReportGenerator> generators, TextWriter stdout, TextWriter stderr) -> Task<int>` (the exit code) — called from `Program.cs` and from Task 14's integration test.

- [ ] **Step 1: Create the projects**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new console -n Koduma.Cli -o src/Koduma.Cli -f net10.0
dotnet sln add src/Koduma.Cli/Koduma.Cli.csproj
dotnet add src/Koduma.Cli/Koduma.Cli.csproj reference src/Koduma.Core/Koduma.Core.csproj
dotnet add src/Koduma.Cli/Koduma.Cli.csproj reference src/Koduma.Analyzers.CSharp/Koduma.Analyzers.CSharp.csproj
dotnet add src/Koduma.Cli/Koduma.Cli.csproj reference src/Koduma.Analyzers.External/Koduma.Analyzers.External.csproj
dotnet add src/Koduma.Cli/Koduma.Cli.csproj reference src/Koduma.Analyzers.Secrets/Koduma.Analyzers.Secrets.csproj
dotnet add src/Koduma.Cli/Koduma.Cli.csproj reference src/Koduma.Reporting/Koduma.Reporting.csproj
rm src/Koduma.Cli/Program.cs

dotnet new xunit -n Koduma.Cli.Tests -o tests/Koduma.Cli.Tests -f net10.0
dotnet sln add tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj
dotnet add tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj reference src/Koduma.Cli/Koduma.Cli.csproj
rm tests/Koduma.Cli.Tests/UnitTest1.cs
```

Edit `src/Koduma.Cli/Koduma.Cli.csproj` to add:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs
using Koduma.Analyzers.Secrets;
using Koduma.Cli;
using Koduma.Core.Analysis;
using Koduma.Reporting;
using Xunit;

public class AnalyzeCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-cli-" + Guid.NewGuid());

    public AnalyzeCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ILanguageAnalyzer[] Analyzers() => new ILanguageAnalyzer[] { new SecretsAnalyzer() };
    private static IReportGenerator[] Generators() => new IReportGenerator[] { new TableReportGenerator(), new JsonReportGenerator() };

    [Fact]
    public async Task Run_ReturnsZero_WhenNoBlockerOrCriticalIssues()
    {
        File.WriteAllText(Path.Combine(_root, "ok.txt"), "nothing suspicious here");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_ReturnsOne_WhenBlockerOrCriticalIssuesFound()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "password = \"SuperSecret123!\"");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Run_ReturnsTwo_AndPrintsCleanMessage_WhenConfigIsInvalid()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), "{ not valid");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("koduma.json", stderr.ToString());
        Assert.DoesNotContain("Exception", stderr.ToString());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`
Expected: FAIL to compile — `AnalyzeCommand` doesn't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/Koduma.Cli/AnalyzeCommand.cs
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using Koduma.Reporting;

namespace Koduma.Cli;

public static class AnalyzeCommand
{
    public static async Task<int> Run(
        string[] args,
        IReadOnlyList<ILanguageAnalyzer> analyzers,
        IReadOnlyList<IReportGenerator> generators,
        TextWriter stdout,
        TextWriter stderr)
    {
        var path = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".";
        var formats = ParseFormats(args);

        KodumaConfig config;
        try
        {
            config = KodumaConfigLoader.LoadOrDefault(path);
        }
        catch (KodumaConfigException ex)
        {
            stderr.WriteLine($"Invalid koduma.json: {ex.Message}");
            return 2;
        }

        var orchestrator = new AnalysisOrchestrator(analyzers, new FileDiscovery());
        var result = await orchestrator.AnalyzeAsync(path, config);

        foreach (var format in formats)
        {
            var generator = generators.FirstOrDefault(g => g.Format == format)
                ?? throw new KodumaConfigException($"Unknown format '{format}'.");
            generator.Write(result, stdout);
            stdout.WriteLine();
        }

        var hasBlockerOrCritical = result.Issues.Any(i => i.Severity is Severity.Blocker or Severity.Critical);
        return hasBlockerOrCritical ? 1 : 0;
    }

    private static List<string> ParseFormats(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--format" && i + 1 < args.Length)
                return args[i + 1].Split(',').ToList();
        }

        return new List<string> { "table" };
    }
}
```

```csharp
// src/Koduma.Cli/Program.cs
using Koduma.Analyzers.CSharp;
using Koduma.Analyzers.External;
using Koduma.Analyzers.Secrets;
using Koduma.Cli;
using Koduma.Core.Analysis;
using Koduma.Reporting;

ILanguageAnalyzer[] analyzers =
{
    new CSharpAnalyzer(),
    new EslintAnalyzer(new ExternalProcessRunner()),
    new SecretsAnalyzer(),
};

IReportGenerator[] generators =
{
    new TableReportGenerator(),
    new JsonReportGenerator(),
    new HtmlReportGenerator(),
    new SarifReportGenerator(),
};

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: koduma <analyze|rules|mcp-serve> [options]");
    return 2;
}

return args[0] switch
{
    "analyze" => await AnalyzeCommand.Run(args, analyzers, generators, Console.Out, Console.Error),
    "rules" => RulesCommand.Run(analyzers, Console.Out),
    _ => Unknown(args[0]),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    return 2;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`
Expected: FAIL to compile (`RulesCommand` referenced but not created yet — created in Task 12). For this step only, temporarily comment out the `"rules" => RulesCommand.Run(...)` line and the `mcp-serve` case is already absent; replace with `_ => Unknown(args[0])` as the only branch besides `analyze`, run the tests, confirm PASS, then move to Task 12 which adds `RulesCommand` back.

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Cli tests/Koduma.Cli.Tests koduma.sln
git commit -m "feat: add CLI analyze command with exit codes and format selection"
```

---

### Task 12: CLI — `rules` command

**Files:**
- Create: `src/Koduma.Cli/RulesCommand.cs`
- Modify: `src/Koduma.Cli/Program.cs`
- Modify: `src/Koduma.Core/Analysis/ILanguageAnalyzer.cs` (add `Rules` metadata)
- Modify: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`, `src/Koduma.Analyzers.External/EslintAnalyzer.cs`, `src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs` (expose static rule metadata)
- Test: `tests/Koduma.Cli.Tests/RulesCommandTests.cs`

**Interfaces:**
- Consumes: `ILanguageAnalyzer.Rules` (new), `Rule` (Task 2).
- Produces: `RulesCommand.Run(IReadOnlyList<ILanguageAnalyzer> analyzers, TextWriter stdout) -> int` — called from `Program.cs`, and used by Task 13's `list_rules` MCP tool for a single shared source of truth.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Koduma.Cli.Tests/RulesCommandTests.cs
using Koduma.Analyzers.CSharp;
using Koduma.Cli;
using Koduma.Core.Analysis;
using Xunit;

public class RulesCommandTests
{
    [Fact]
    public void Run_ListsEachAnalyzersRules()
    {
        ILanguageAnalyzer[] analyzers = { new CSharpAnalyzer() };
        using var stdout = new StringWriter();

        var exitCode = RulesCommand.Run(analyzers, stdout);

        Assert.Equal(0, exitCode);
        Assert.Contains("CS-NAMING-001", stdout.ToString());
        Assert.Contains("CS-COMPLEXITY-001", stdout.ToString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj --filter RulesCommandTests`
Expected: FAIL to compile — `ILanguageAnalyzer.Rules` and `RulesCommand` don't exist yet.

- [ ] **Step 3: Add `Rules` metadata to the analyzer interface and each analyzer**

```csharp
// src/Koduma.Core/Analysis/ILanguageAnalyzer.cs (add member to the interface)
using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Core.Analysis;

public interface ILanguageAnalyzer
{
    string Language { get; }
    IReadOnlyCollection<string> FileExtensions { get; }
    bool AppliesToAllFiles { get; }
    IReadOnlyList<Rule> Rules { get; }

    Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken);
}
```

```csharp
// src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs (add property)
public IReadOnlyList<Rule> Rules { get; } = new[]
{
    new Rule("CS-NAMING-001", "Private field naming", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
    new Rule("CS-COMPLEXITY-001", "Cyclomatic complexity", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
};
```

```csharp
// src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs (add property)
public IReadOnlyList<Rule> Rules { get; } = new[]
{
    new Rule("SEC-SECRET-001", "Hardcoded secret", Category.Security, Severity.Critical, "security", EngineRuleId: null),
};
```

```csharp
// src/Koduma.Analyzers.External/EslintAnalyzer.cs (add property)
public IReadOnlyList<Rule> Rules { get; } = Array.Empty<Rule>(); // ESLint's own rule set is dynamic/user-configured, not enumerable in advance.
```

Update every fake/test analyzer implementation elsewhere in the test suite (`AnalysisOrchestratorTests.FakeAnalyzer`) to also implement `Rules => Array.Empty<Rule>();` — this is a required fix to keep Task 5's tests compiling, not optional cleanup.

- [ ] **Step 4: Implement `RulesCommand`**

```csharp
// src/Koduma.Cli/RulesCommand.cs
using Koduma.Core.Analysis;

namespace Koduma.Cli;

public static class RulesCommand
{
    public static int Run(IReadOnlyList<ILanguageAnalyzer> analyzers, TextWriter stdout)
    {
        foreach (var analyzer in analyzers)
        {
            foreach (var rule in analyzer.Rules)
            {
                stdout.WriteLine($"{rule.Id}\t{rule.Language}\t{rule.Category}\t{rule.DefaultSeverity}\t{rule.Name}");
            }
        }

        return 0;
    }
}
```

- [ ] **Step 5: Restore the `rules` branch in `Program.cs`**

```csharp
// src/Koduma.Cli/Program.cs (restore the switch arm removed as a stopgap in Task 11 Step 5)
return args[0] switch
{
    "analyze" => await AnalyzeCommand.Run(args, analyzers, generators, Console.Out, Console.Error),
    "rules" => RulesCommand.Run(analyzers, Console.Out),
    _ => Unknown(args[0]),
};
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`
Expected: PASS (and re-run `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj` to confirm the `Rules` addition didn't break Task 5's fakes)

- [ ] **Step 7: Commit**

```bash
git add src/Koduma.Cli src/Koduma.Core src/Koduma.Analyzers.CSharp src/Koduma.Analyzers.Secrets src/Koduma.Analyzers.External tests/Koduma.Cli.Tests tests/Koduma.Core.Tests
git commit -m "feat: add rules command and expose rule metadata from each analyzer"
```

---

### Task 13: MCP server

**Files:**
- Create: `src/Koduma.Mcp/Koduma.Mcp.csproj`
- Create: `src/Koduma.Mcp/KodumaMcpServer.cs`
- Modify: `src/Koduma.Cli/Program.cs` (add `mcp-serve` branch)
- Modify: `src/Koduma.Cli/Koduma.Cli.csproj` (reference `Koduma.Mcp`)

**Interfaces:**
- Consumes: `AnalysisOrchestrator` (Task 5), `KodumaConfigLoader` (Task 4), `ILanguageAnalyzer` list, `KodumaJsonContext` (Task 2).
- Produces: `KodumaMcpServer.RunAsync(IReadOnlyList<ILanguageAnalyzer> analyzers, CancellationToken ct) -> Task` — runs the stdio MCP server loop; called from `Program.cs`'s `mcp-serve` branch.

Tool registration must be explicit/manual (no `[McpServerToolType]` attribute scanning), to stay consistent with the Native AOT global constraint. The three tools are `analyze_project`, `list_rules`, `get_capabilities`, matching the spec's MCP surface. If the installed `ModelContextProtocol` package version names its handler/options types slightly differently than below, keep the *behavior* (explicit registration, these three tools, these JSON shapes) and adjust the call sites to match the installed API — that is a normal compile-fix, not a deviation from the plan.

- [ ] **Step 1: Create the project**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new classlib -n Koduma.Mcp -o src/Koduma.Mcp -f net10.0
dotnet sln add src/Koduma.Mcp/Koduma.Mcp.csproj
dotnet add src/Koduma.Mcp/Koduma.Mcp.csproj reference src/Koduma.Core/Koduma.Core.csproj
dotnet add src/Koduma.Mcp/Koduma.Mcp.csproj package ModelContextProtocol
rm src/Koduma.Mcp/Class1.cs
dotnet add src/Koduma.Cli/Koduma.Cli.csproj reference src/Koduma.Mcp/Koduma.Mcp.csproj
```

- [ ] **Step 2: Implement the server with explicit tool handlers**

```csharp
// src/Koduma.Mcp/KodumaMcpServer.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Koduma.Mcp;

public static class KodumaMcpServer
{
    public static async Task RunAsync(IReadOnlyList<ILanguageAnalyzer> analyzers, CancellationToken cancellationToken)
    {
        var tools = new List<Tool>
        {
            new()
            {
                Name = "analyze_project",
                Description = "Analyzes a project path for code smells, metrics and security issues.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray { "path" },
                }),
            },
            new()
            {
                Name = "list_rules",
                Description = "Lists the rules Koduma can check, optionally filtered by language.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["language"] = new JsonObject { ["type"] = "string" } },
                }),
            },
            new()
            {
                Name = "get_capabilities",
                Description = "Lists the languages and categories Koduma supports.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object" }),
            },
        };

        var builder = McpServerOptions.Create();
        builder.ServerInfo = new Implementation { Name = "koduma", Version = "0.1.0" };
        builder.Capabilities = new ServerCapabilities { Tools = new ToolsCapability() };

        builder.Handlers.ListTools = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools });

        builder.Handlers.CallTool = async (request, ct) =>
        {
            var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();

            object payload = request.Params?.Name switch
            {
                "analyze_project" => await HandleAnalyzeProject(arguments, analyzers, ct),
                "list_rules" => HandleListRules(arguments, analyzers),
                "get_capabilities" => HandleGetCapabilities(analyzers),
                _ => throw new InvalidOperationException($"Unknown tool '{request.Params?.Name}'."),
            };

            return new CallToolResult
            {
                Content = new[] { new TextContentBlock { Text = JsonSerializer.Serialize(payload) } },
            };
        };

        await using var transport = new StdioServerTransport("koduma");
        await using var server = McpServerFactory.Create(transport, builder);
        await server.RunAsync(cancellationToken);
    }

    private static async Task<AnalysisResult> HandleAnalyzeProject(IReadOnlyDictionary<string, JsonElement> arguments, IReadOnlyList<ILanguageAnalyzer> analyzers, CancellationToken ct)
    {
        var path = arguments.TryGetValue("path", out var pathElement) ? pathElement.GetString()! : ".";
        var config = KodumaConfigLoader.LoadOrDefault(path);
        var orchestrator = new AnalysisOrchestrator(analyzers, new FileDiscovery());
        return await orchestrator.AnalyzeAsync(path, config, ct);
    }

    private static Rule[] HandleListRules(IReadOnlyDictionary<string, JsonElement> arguments, IReadOnlyList<ILanguageAnalyzer> analyzers)
    {
        var language = arguments.TryGetValue("language", out var languageElement) ? languageElement.GetString() : null;
        return analyzers
            .SelectMany(a => a.Rules)
            .Where(r => language is null || r.Language == language)
            .ToArray();
    }

    private static object HandleGetCapabilities(IReadOnlyList<ILanguageAnalyzer> analyzers) => new
    {
        Languages = analyzers.Select(a => a.Language).ToArray(),
        Categories = Enum.GetNames<Category>(),
    };
}
```

- [ ] **Step 3: Wire `mcp-serve` into `Program.cs`**

```csharp
// src/Koduma.Cli/Program.cs (replace the switch expression)
return args[0] switch
{
    "analyze" => await AnalyzeCommand.Run(args, analyzers, generators, Console.Out, Console.Error),
    "rules" => RulesCommand.Run(analyzers, Console.Out),
    "mcp-serve" => await RunMcpServe(analyzers),
    _ => Unknown(args[0]),
};

static async Task<int> RunMcpServe(ILanguageAnalyzer[] analyzers)
{
    await Koduma.Mcp.KodumaMcpServer.RunAsync(analyzers, CancellationToken.None);
    return 0;
}
```

- [ ] **Step 4: Manual verification (no unit test — this is a protocol-level smoke check)**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet build src/Koduma.Cli/Koduma.Cli.csproj
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}' | dotnet run --project src/Koduma.Cli -- mcp-serve
```

Expected: a JSON-RPC response on stdout containing `"serverInfo"` with `"name":"koduma"`, with no exception text on stderr. This confirms the handshake works before relying on it from an actual MCP client.

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Mcp src/Koduma.Cli
git commit -m "feat: add MCP server exposing analyze_project, list_rules, get_capabilities"
```

---

### Task 14: Integration tests + Native AOT publish verification

**Files:**
- Create: `tests/Koduma.Integration.Tests/Koduma.Integration.Tests.csproj`
- Create: `tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs`

**Interfaces:**
- Consumes: the published `koduma` binary (built from `src/Koduma.Cli`), end to end — no new production interfaces are produced by this task.

- [ ] **Step 1: Create the test project**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new xunit -n Koduma.Integration.Tests -o tests/Koduma.Integration.Tests -f net10.0
dotnet sln add tests/Koduma.Integration.Tests/Koduma.Integration.Tests.csproj
rm tests/Koduma.Integration.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write the failing tests (build the real AOT binary, run it against fixtures)**

```csharp
// tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs
using System.Diagnostics;
using Xunit;

public class EndToEndAnalyzeTests : IDisposable
{
    private readonly string _repoRoot = FindRepoRoot();
    private readonly string _publishDir;
    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), "koduma-e2e-" + Guid.NewGuid());

    public EndToEndAnalyzeTests()
    {
        _publishDir = Path.Combine(_repoRoot, "src", "Koduma.Cli", "bin", "e2e-publish");
        Directory.CreateDirectory(_fixtureRoot);

        var publish = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{Path.Combine(_repoRoot, "src", "Koduma.Cli", "Koduma.Cli.csproj")}\" -c Release -r linux-x64 --self-contained -o \"{_publishDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        publish.WaitForExit();
        if (publish.ExitCode != 0)
            throw new InvalidOperationException("publish failed: " + publish.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        Directory.Delete(_fixtureRoot, recursive: true);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "koduma.sln")))
            dir = Directory.GetParent(dir)!.FullName;
        return dir;
    }

    [Fact]
    public async Task Analyze_EmptyRepo_ExitsZero_AndProducesValidReportsInEveryFormat()
    {
        var (exitCode, stdOut, stdErr) = await RunKoduma("analyze", _fixtureRoot, "--format", "table,json,html,sarif");

        Assert.Equal(0, exitCode);
        Assert.Contains("0 issues", stdOut);
        Assert.Contains("\"issues\"", stdOut.ToLowerInvariant());
        Assert.Contains("<!DOCTYPE html>", stdOut);
        Assert.Contains("\"version\": \"2.1.0\"", stdOut);
        Assert.Empty(stdErr);
    }

    [Fact]
    public async Task Analyze_RepoWithSecretAndBadCSharpNaming_ExitsOne_AndReportsBothIssues()
    {
        File.WriteAllText(Path.Combine(_fixtureRoot, "Example.cs"), """
        public class Example
        {
            private int badlyNamedField;
        }
        """);
        File.WriteAllText(Path.Combine(_fixtureRoot, "secret.txt"), "password = \"SuperSecret123!\"");

        var (exitCode, stdOut, _) = await RunKoduma("analyze", _fixtureRoot, "--format", "json");

        Assert.Equal(1, exitCode);
        Assert.Contains("CS-NAMING-001", stdOut);
        Assert.Contains("SEC-SECRET-001", stdOut);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunKoduma(params string[] args)
    {
        var binary = Path.Combine(_publishDir, "Koduma.Cli");
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdOut, stdErr);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Integration.Tests/Koduma.Integration.Tests.csproj`
Expected: FAIL — the publish step in the constructor will succeed (prior tasks already build), but assertions fail until `Program.cs`'s output matches exactly; iterate on the implementation from earlier tasks if format strings don't match (e.g. the JSON generator wraps an `AnalysisResult`, confirm the lowercase check for `"issues"` matches the camelCase/PascalCase the System.Text.Json source generator produces — if it emits `"Issues"`, adjust the assertion to `Assert.Contains("issues", stdOut.ToLowerInvariant())`, which the test above already does correctly).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Integration.Tests/Koduma.Integration.Tests.csproj`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add tests/Koduma.Integration.Tests koduma.sln
git commit -m "test: add end-to-end tests against the published Native AOT binary"
```
