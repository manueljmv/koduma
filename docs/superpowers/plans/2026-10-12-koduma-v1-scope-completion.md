# Koduma v1 Scope Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the five v1 spec items the original Koduma plan left unimplemented (SQL-injection rule, duplication metric, configurable exit threshold, MCP `format`/`rules_config` parameters, golden/snapshot report tests), and add a configured-root restriction to the MCP server's `analyze_project` tool.

**Architecture:** All additive to the existing, already-reviewed Koduma codebase — no architectural changes. Each task extends an existing component (`SecretsAnalyzer`, `AnalysisOrchestrator`, `AnalyzeCommand`, the four `IReportGenerator`s, `KodumaMcpServer`) with its own tests.

**Tech Stack:** .NET 10, xUnit, `System.Security.Cryptography` (SHA256, built-in), `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-10-12-koduma-v1-scope-completion-design.md`

## Global Constraints

- Target framework for every project: `net10.0` (unchanged from the original plan).
- All JSON (de)serialization of Koduma's own types continues to use the source-generated `KodumaJsonContext`/`McpJsonContext` — no new reflection-based JSON calls.
- Rule ID format: `<PREFIX>-<CATEGORY>-<NNN>` — the new rule is `SEC-SQLI-001`.
- CLI exit codes: `0` no issue at/above the fail-on threshold, `1` an issue at/above the threshold found, `2` execution or config error (including an unrecognized `--fail-on` value) — no stack trace printed on exit 2.
- Any new MCP tool-call failure path must go through the existing `ToolError` mechanism (`CallToolResult.IsError = true`) — never let an exception propagate unhandled out of `CallToolHandler`.
- Touch only what each task names. Do not refactor unrelated code.

## Review Focus

- `--fail-on` given a value that isn't a real `Severity` name → must exit 2 with a clean message, not throw. Covered in Task 3.
- MCP `analyze_project`'s `path` using `..` segments (or an absolute path) to escape the configured root → must be rejected with a tool error, never silently allowed. Covered in Task 5.
- MCP `rules_config` argument that is malformed (not a JSON object, or a rule shaped wrong) → must produce a clean MCP tool error via the existing `KodumaConfigException`/`ToolError` path, not an unhandled exception. Covered in Task 5.
- A golden-file comparison run on a tree with CRLF line endings (e.g. a future Windows contributor, or a git `autocrlf` setting) must still match after normalization, not fail spuriously. Covered in Task 4.
- Duplication computation encountering a binary or unreadable file among the discovered files must not crash the whole analysis run (same lesson as the C1 fix in the prior plan). Covered in Task 2.

---

## Task 1: SQL injection rule in `SecretsAnalyzer`

**Files:**
- Modify: `src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: a new rule `SEC-SQLI-001` (`Category.Security`, `Severity.Critical`) emitted by `SecretsAnalyzer`, discoverable via its `Rules` property — no signature changes to `ILanguageAnalyzer`/`AnalyzeAsync`.

- [ ] **Step 1: Write the failing test**

Add this test method to the existing `SecretsAnalyzerTests` class (keep the existing test method and `Dispose`/`_file` field as they are):

```csharp
    [Fact]
    public async Task AnalyzeAsync_FlagsStringConcatenatedAndInterpolatedSql_ButNotFixedQueries()
    {
        File.WriteAllText(_file, """
        var ok = "SELECT fixed_value FROM t";
        var risky1 = "SELECT * FROM users WHERE id = " + userId;
        var risky2 = `SELECT * FROM t WHERE x = ${x}`;
        """);

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        var sqliIssues = result.Issues.Where(i => i.RuleId == "SEC-SQLI-001").ToList();
        Assert.Equal(2, sqliIssues.Count);
        Assert.All(sqliIssues, i => Assert.Equal(Category.Security, i.Category));
        Assert.Contains(sqliIssues, i => i.Line == 2);
        Assert.Contains(sqliIssues, i => i.Line == 3);
    }
```

Add `using System.Linq;` to the top of the test file if it isn't already implicitly available (the project has `<ImplicitUsings>enable</ImplicitUsings>`, so this is likely already available — if the build fails on `.Where`/`.ToList`, add the explicit `using`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj`
Expected: FAIL — `sqliIssues.Count` is 0, not 2 (no `SEC-SQLI-001` rule exists yet).

- [ ] **Step 3: Implement the rule**

Replace the `Patterns` field and the matching loop in `src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs` as follows. First, replace:

```csharp
    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("AWS access key", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("Hardcoded password", new Regex(@"password\s*=\s*""[^""]{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };
```

with:

```csharp
    private static readonly (string RuleId, string Name, Regex Pattern)[] Patterns =
    {
        ("SEC-SECRET-001", "AWS access key", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("SEC-SECRET-001", "Hardcoded password", new Regex(@"password\s*=\s*""[^""]{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("SEC-SQLI-001", "Possible SQL injection", new Regex(
            @"(SELECT|INSERT|UPDATE|DELETE|DROP)\b[^\n]*(?:[""']\s*\+|\$\{|#\{)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };
```

Then, inside `AnalyzeAsync`, replace the `foreach (var (name, pattern) in Patterns)` loop body with:

```csharp
                foreach (var (ruleId, name, pattern) in Patterns)
                {
                    var match = pattern.Match(lines[lineIndex]);
                    if (!match.Success)
                        continue;

                    issues.Add(new Issue(
                        RuleId: ruleId,
                        Severity: Severity.Critical,
                        Category: Category.Security,
                        FilePath: file,
                        Line: lineIndex + 1,
                        Column: match.Index + 1,
                        EndLine: lineIndex + 1,
                        EndColumn: match.Index + match.Length + 1,
                        Message: ruleId == "SEC-SQLI-001" ? "Possible SQL injection." : $"Possible hardcoded secret ({name}).",
                        Language: "security"));
                }
```

Finally, update the `Rules` property to also list the new rule:

```csharp
    public IReadOnlyList<Rule> Rules { get; } = new[]
    {
        new Rule("SEC-SECRET-001", "Hardcoded secret", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SQLI-001", "Possible SQL injection", Category.Security, Severity.Critical, "security", EngineRuleId: null),
    };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Analyzers.Secrets.Tests/Koduma.Analyzers.Secrets.Tests.csproj`
Expected: PASS (both the existing secret-detection test and the new SQL-injection test).

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test koduma.sln`
Expected: all tests still pass (no project outside `Koduma.Analyzers.Secrets`/`Koduma.Analyzers.Secrets.Tests` is touched by this task, so nothing else should be affected — but confirm, since `Rules` metadata changes are exactly the kind of cross-cutting addition that broke things in the original plan's Task 12).

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs
git commit -m "feat: add basic SQL injection detection rule (SEC-SQLI-001)"
```

---

## Task 2: Duplication metric in `AnalysisOrchestrator`

**Files:**
- Modify: `src/Koduma.Core/Analysis/AnalysisOrchestrator.cs`
- Test: `tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: nothing new (operates on the `allFiles` list `AnalysisOrchestrator` already discovers).
- Produces: `AnalysisResult.Metrics.DuplicatedLines` now reflects a real cross-file count instead of always `0`. No public signature changes.

- [ ] **Step 1: Write the failing test**

Add this test method to `tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs` (inside the existing `AnalysisOrchestratorTests` class, alongside the existing tests — reuse the existing `FakeAnalyzer` class already defined in that file, and add `using Koduma.Core.Discovery;` at the top of the file if it is not already imported):

```csharp
    [Fact]
    public async Task AnalyzeAsync_ComputesDuplicatedLines_AcrossFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "koduma-dup-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var duplicateBlock = string.Join('\n', Enumerable.Range(1, 6).Select(i => $"line{i}();"));
            File.WriteAllText(Path.Combine(root, "a.cs"), duplicateBlock + "\nuniqueA();");
            File.WriteAllText(Path.Combine(root, "b.cs"), duplicateBlock + "\nuniqueB();");

            var analyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, _ =>
                new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 2), Error: null));

            var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { analyzer }, new FileDiscovery());

            var result = await orchestrator.AnalyzeAsync(root, KodumaConfig.Default);

            Assert.Equal(12, result.Metrics.DuplicatedLines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter AnalyzeAsync_ComputesDuplicatedLines_AcrossFiles`
Expected: FAIL — `result.Metrics.DuplicatedLines` is `0`, not `12`.

- [ ] **Step 3: Implement duplication computation**

At the top of `src/Koduma.Core/Analysis/AnalysisOrchestrator.cs`, add two `using` statements alongside the existing ones:

```csharp
using System.Security.Cryptography;
using System.Text;
```

Replace the metrics-aggregation loop's `totalDuplicated` variable and its accumulation. First, remove this line from the variable declarations:

```csharp
        var totalDuplicated = 0;
```

and remove this line from inside the `foreach (var (language, languageResult) in perLanguageResults)` loop:

```csharp
            totalDuplicated += languageResult.Metrics.DuplicatedLines;
```

Then change the final `AnalysisMetrics` construction from:

```csharp
            Metrics: new AnalysisMetrics(totalLoc, totalComplexity, totalDuplicated, totalFiles),
```

to:

```csharp
            Metrics: new AnalysisMetrics(totalLoc, totalComplexity, ComputeDuplicatedLines(allFiles), totalFiles),
```

Finally, add this new private method to the `AnalysisOrchestrator` class (alongside the existing `ApplyRuleOverrides` method):

```csharp
    private static int ComputeDuplicatedLines(IReadOnlyList<string> files)
    {
        const int WindowSize = 6;
        var windowOccurrences = new Dictionary<string, List<(string File, int StartLine)>>();

        foreach (var file in files)
        {
            List<string> lines;
            try
            {
                lines = File.ReadAllLines(file)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i + WindowSize <= lines.Count; i++)
            {
                var window = string.Join("\n", lines.Skip(i).Take(WindowSize));
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(window)));
                if (!windowOccurrences.TryGetValue(hash, out var occurrences))
                    windowOccurrences[hash] = occurrences = new List<(string, int)>();
                occurrences.Add((file, i));
            }
        }

        var duplicatedLineKeys = new HashSet<(string File, int Line)>();
        foreach (var occurrences in windowOccurrences.Values.Where(o => o.Count > 1))
        {
            foreach (var (file, startLine) in occurrences)
                for (var offset = 0; offset < WindowSize; offset++)
                    duplicatedLineKeys.Add((file, startLine + offset));
        }

        return duplicatedLineKeys.Count;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter AnalyzeAsync_ComputesDuplicatedLines_AcrossFiles`
Expected: PASS

- [ ] **Step 5: Run the full Koduma.Core.Tests suite**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj`
Expected: all tests pass, including the existing `AnalysisOrchestratorTests` (which use a `FakeDiscovery` returning fictitious, non-existent file paths — `ComputeDuplicatedLines` will fail to read those via `File.ReadAllLines`, hit the `catch (IOException)` branch, and silently contribute `0` duplicated lines, which is what those tests already expect since none of them assert on `DuplicatedLines`).

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Core/Analysis/AnalysisOrchestrator.cs tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs
git commit -m "feat: compute code duplication metric across discovered files"
```

---

## Task 3: Configurable exit threshold (`--fail-on`)

**Files:**
- Modify: `src/Koduma.Cli/AnalyzeCommand.cs`
- Test: `tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs`

**Interfaces:**
- Consumes: `Severity` enum (`Info, Minor, Major, Critical, Blocker`, declared in that ascending order, so `>=` comparison works directly — from `Koduma.Core.Model`).
- Produces: a `--fail-on <severity>` CLI argument on `analyze`. No change to `AnalyzeCommand.Run`'s public signature.

- [ ] **Step 1: Write the failing tests**

Add these two test methods to `tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs` (inside the existing `AnalyzeCommandTests` class):

```csharp
    [Fact]
    public async Task Run_ReturnsOne_WhenFailOnThresholdLoweredToMajor_AndOnlyAMajorIssueExists()
    {
        File.WriteAllText(Path.Combine(_root, "Example.cs"), """
        public class Example
        {
            private int badlyNamedField;
        }
        """);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        ILanguageAnalyzer[] analyzers = { new Koduma.Analyzers.CSharp.CSharpAnalyzer() };

        var exitCode = await AnalyzeCommand.Run(
            new[] { "analyze", _root, "--fail-on", "minor" }, analyzers, Generators(), stdout, stderr);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Run_ReturnsTwo_AndPrintsCleanMessage_WhenFailOnSeverityIsUnrecognized()
    {
        File.WriteAllText(Path.Combine(_root, "ok.txt"), "nothing suspicious here");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(
            new[] { "analyze", _root, "--fail-on", "bogus" }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("bogus", stderr.ToString());
        Assert.DoesNotContain("Exception", stderr.ToString());
    }
```

The first test needs a reference to `Koduma.Analyzers.CSharp` — `tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj` already has this available transitively through `Koduma.Cli`'s own project references (confirm by checking `Koduma.Cli.csproj`'s `ProjectReference`s; if the build fails on the `Koduma.Analyzers.CSharp.CSharpAnalyzer` reference, add `<ProjectReference Include="..\..\src\Koduma.Analyzers.CSharp\Koduma.Analyzers.CSharp.csproj" />` to `tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj --filter "Run_ReturnsOne_WhenFailOnThresholdLoweredToMajor_AndOnlyAMajorIssueExists|Run_ReturnsTwo_AndPrintsCleanMessage_WhenFailOnSeverityIsUnrecognized"`
Expected: FAIL — the first test fails because the default threshold (effectively `Critical`) doesn't fire on a `Minor` naming issue (exit code is `0`, not `1`); the second test fails because `--fail-on` isn't parsed at all yet, so `bogus` is silently ignored (or misparsed as the path) rather than producing a clean exit-2 error.

- [ ] **Step 3: Implement the threshold**

In `src/Koduma.Cli/AnalyzeCommand.cs`, change the `ParseArguments` method's return type and body from:

```csharp
    private static (string Path, List<string> Formats) ParseArguments(string[] args)
    {
        string? path = null;
        var formats = new List<string> { "table" };

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--format")
            {
                if (i + 1 < args.Length)
                    formats = args[++i].Split(',').ToList();
                continue;
            }

            if (args[i].StartsWith("--"))
                continue;

            path ??= args[i];
        }

        return (path ?? ".", formats);
    }
```

to:

```csharp
    private static (string Path, List<string> Formats, string? FailOn) ParseArguments(string[] args)
    {
        string? path = null;
        var formats = new List<string> { "table" };
        string? failOn = null;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--format")
            {
                if (i + 1 < args.Length)
                    formats = args[++i].Split(',').ToList();
                continue;
            }

            if (args[i] == "--fail-on")
            {
                if (i + 1 < args.Length)
                    failOn = args[++i];
                continue;
            }

            if (args[i].StartsWith("--"))
                continue;

            path ??= args[i];
        }

        return (path ?? ".", formats, failOn);
    }
```

Then, in `Run`, change the call site and add threshold parsing. Replace:

```csharp
        var (path, formats) = ParseArguments(args);
```

with:

```csharp
        var (path, formats, failOnText) = ParseArguments(args);

        var failOnThreshold = Severity.Critical;
        if (failOnText is not null && !Enum.TryParse(failOnText, ignoreCase: true, out failOnThreshold))
        {
            stderr.WriteLine($"Unknown --fail-on severity '{failOnText}'.");
            return 2;
        }
```

Finally, replace the existing threshold check at the bottom of `Run`:

```csharp
        var hasBlockerOrCritical = result.Issues.Any(i => i.Severity is Severity.Blocker or Severity.Critical);
        return hasBlockerOrCritical ? 1 : 0;
```

with:

```csharp
        var hasThresholdIssue = result.Issues.Any(i => i.Severity >= failOnThreshold);
        return hasThresholdIssue ? 1 : 0;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Cli.Tests/Koduma.Cli.Tests.csproj`
Expected: PASS for all tests, including every pre-existing `AnalyzeCommandTests` test (the default threshold of `Severity.Critical` reproduces the exact old `Blocker or Critical` check, since `Severity` is declared `Info < Minor < Major < Critical < Blocker` and `>= Critical` matches exactly `Critical` and `Blocker`).

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Cli/AnalyzeCommand.cs tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs
git commit -m "feat: add configurable --fail-on exit threshold"
```

---

## Task 4: Golden/snapshot tests for the four report generators

**Files:**
- Create: `tests/Koduma.Reporting.Tests/GoldenFixture.cs`
- Create: `tests/Koduma.Reporting.Tests/GoldenFile.cs`
- Create: `tests/Koduma.Reporting.Tests/GoldenTests.cs`
- Create (bootstrapped by running the tests, see Step 3): `tests/Koduma.Reporting.Tests/golden/sample.table.txt`, `tests/Koduma.Reporting.Tests/golden/sample.json`, `tests/Koduma.Reporting.Tests/golden/sample.html`, `tests/Koduma.Reporting.Tests/golden/sample.sarif.json`

**Interfaces:**
- Consumes: `AnalysisResult`, `IIssue`/`Issue`, `AnalysisMetrics`, `AnalysisError` (from `Koduma.Core.Model`), the four existing `IReportGenerator` implementations.
- Produces: nothing consumed by later tasks — this is a standalone test-only addition.

- [ ] **Step 1: Create the fixture and golden-file helper**

```csharp
// tests/Koduma.Reporting.Tests/GoldenFixture.cs
using Koduma.Core.Model;

internal static class GoldenFixture
{
    public static AnalysisResult SampleResult() => new(
        ProjectPath: "/sample/project",
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        DurationMs: 42,
        Issues: new IIssue[]
        {
            new Issue("CS-NAMING-001", Severity.Minor, Category.CodeSmell, "src/Foo.cs", 10, 5, 10, 20, "Field should be camelCase.", "csharp"),
            new Issue("SEC-SECRET-001", Severity.Critical, Category.Security, "config/secrets.txt", 3, 1, 3, 30, "Possible hardcoded secret (AWS access key).", "security"),
        },
        Metrics: new AnalysisMetrics(LinesOfCode: 120, CyclomaticComplexity: 8, DuplicatedLines: 12, FilesAnalyzed: 4),
        Errors: new[] { new AnalysisError("javascript", "eslint could not be run: not found") });
}
```

```csharp
// tests/Koduma.Reporting.Tests/GoldenFile.cs
using System.Runtime.CompilerServices;
using Xunit;

internal static class GoldenFile
{
    public static void AssertMatches(string fileName, string actual, [CallerFilePath] string callerFilePath = "")
    {
        var goldenDir = Path.Combine(Path.GetDirectoryName(callerFilePath)!, "golden");
        var goldenPath = Path.Combine(goldenDir, fileName);
        var normalizedActual = actual.Replace("\r\n", "\n");

        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(goldenDir);
            File.WriteAllText(goldenPath, normalizedActual);
            Assert.Fail($"Golden file '{fileName}' did not exist; created it from the current output at {goldenPath}. Review its content, then re-run this test — it should now pass — and commit the new file.");
        }

        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        Assert.Equal(expected, normalizedActual);
    }
}
```

- [ ] **Step 2: Write the four golden tests**

```csharp
// tests/Koduma.Reporting.Tests/GoldenTests.cs
using Koduma.Reporting;
using Xunit;

public class GoldenTests
{
    [Fact]
    public void TableReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new TableReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.table.txt", writer.ToString());
    }

    [Fact]
    public void JsonReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new JsonReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.json", writer.ToString());
    }

    [Fact]
    public void HtmlReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new HtmlReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.html", writer.ToString());
    }

    [Fact]
    public void SarifReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new SarifReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.sarif.json", writer.ToString());
    }
}
```

- [ ] **Step 3: Run the tests once to bootstrap the golden files**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj --filter GoldenTests`
Expected: all four tests FAIL with the message from `GoldenFile.AssertMatches`'s `Assert.Fail` call (e.g. "Golden file 'sample.table.txt' did not exist; created it..."). This is expected — each failing run writes `tests/Koduma.Reporting.Tests/golden/sample.<ext>` from the generator's real current output.

Read each of the four newly-created files under `tests/Koduma.Reporting.Tests/golden/` and confirm they look correct (table: human-readable lines with both issues, the ESLint error line, and a `2 issues, 1 errors` summary; json: a single line of compact JSON with `"Severity":"Minor"`/`"Severity":"Critical"` as strings and `"$type":"issue"` on each issue; html: a full HTML document with a two-row issues table and an "Analysis errors" section; sarif: indented JSON with `"version": "2.1.0"`, two `results`, two `rules`, and one `toolExecutionNotifications` entry). If anything looks wrong, fix the relevant generator (not the golden file) and re-run this step — the golden file must reflect genuinely correct output, not just "whatever the code currently does."

- [ ] **Step 4: Run the tests again to verify they now pass**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj --filter GoldenTests`
Expected: PASS — all four tests now compare against the committed golden files and match.

- [ ] **Step 5: Run the full Koduma.Reporting.Tests suite**

Run: `dotnet test tests/Koduma.Reporting.Tests/Koduma.Reporting.Tests.csproj`
Expected: all tests pass, including the pre-existing functional tests from the original plan (Tasks 9-10) — this task only adds new test files, it doesn't modify any existing ones.

- [ ] **Step 6: Commit**

```bash
git add tests/Koduma.Reporting.Tests/GoldenFixture.cs tests/Koduma.Reporting.Tests/GoldenFile.cs tests/Koduma.Reporting.Tests/GoldenTests.cs tests/Koduma.Reporting.Tests/golden/
git commit -m "test: add golden/snapshot tests for all four report generators"
```

---

## Task 5: MCP server — configured root restriction + `format`/`rules_config` parameters

**Files:**
- Modify: `src/Koduma.Mcp/Koduma.Mcp.csproj`
- Modify: `src/Koduma.Mcp/KodumaMcpServer.cs`
- Modify: `src/Koduma.Core/Config/KodumaConfigLoader.cs`
- Modify: `src/Koduma.Cli/Program.cs`
- Create: `src/Koduma.Mcp/AssemblyInfo.cs`
- Create: `tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj`
- Create: `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs`
- Test: `tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs`

**Interfaces:**
- Consumes: `KodumaConfig`, `RuleOverride` (Koduma.Core.Config), `IReportGenerator` (Koduma.Reporting — `Koduma.Mcp` gains a new `ProjectReference` to `Koduma.Reporting` in this task).
- Produces: `KodumaMcpServer.RunAsync(IReadOnlyList<ILanguageAnalyzer> analyzers, IReadOnlyList<IReportGenerator> generators, string root, CancellationToken cancellationToken) -> Task` (signature change from the original plan's `RunAsync(analyzers, cancellationToken)` — `Program.cs`'s `mcp-serve` branch is the only caller and is updated in this same task). `KodumaConfigLoader.ParseRulesConfig(JsonElement rulesElement) -> Dictionary<string, RuleOverride>` (new public method, reusing the existing private `ParseRuleOverride` validator).

- [ ] **Step 1: Write the failing `KodumaConfigLoader.ParseRulesConfig` tests**

Add these test methods to `tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs` (inside the existing `KodumaConfigLoaderTests` class; add `using System.Text.Json;` to the top of the file if not already present):

```csharp
    [Fact]
    public void ParseRulesConfig_ParsesOffStringAndSeverityObject()
    {
        using var document = JsonDocument.Parse("""
        {
          "CS-NAMING-001": "off",
          "SEC-SECRET-001": { "severity": "blocker" }
        }
        """);

        var rules = KodumaConfigLoader.ParseRulesConfig(document.RootElement);

        Assert.False(rules["CS-NAMING-001"].Enabled);
        Assert.Equal(Severity.Blocker, rules["SEC-SECRET-001"].Severity);
    }

    [Fact]
    public void ParseRulesConfig_Throws_WhenARuleShapeIsInvalid()
    {
        using var document = JsonDocument.Parse("""{ "CS-NAMING-001": 123 }""");

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.ParseRulesConfig(document.RootElement));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter ParseRulesConfig`
Expected: FAIL to compile — `KodumaConfigLoader.ParseRulesConfig` doesn't exist yet.

- [ ] **Step 3: Implement `ParseRulesConfig`**

Add this public method to `src/Koduma.Core/Config/KodumaConfigLoader.cs`, anywhere inside the `KodumaConfigLoader` class (it calls the existing private `ParseRuleOverride` method already defined in that same class — no visibility change needed there):

```csharp
    public static Dictionary<string, RuleOverride> ParseRulesConfig(JsonElement rulesElement)
    {
        var rules = new Dictionary<string, RuleOverride>();
        foreach (var property in rulesElement.EnumerateObject())
        {
            var node = JsonNode.Parse(property.Value.GetRawText());
            rules[property.Name] = ParseRuleOverride(property.Name, node);
        }
        return rules;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Core.Tests/Koduma.Core.Tests.csproj --filter ParseRulesConfig`
Expected: PASS

- [ ] **Step 5: Commit the config-loader change**

```bash
git add src/Koduma.Core/Config/KodumaConfigLoader.cs tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs
git commit -m "feat: add KodumaConfigLoader.ParseRulesConfig for inline rule overrides"
```

- [ ] **Step 6: Set up the `Koduma.Mcp.Tests` project and make `KodumaMcpServer`'s helpers testable**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet new xunit -n Koduma.Mcp.Tests -o tests/Koduma.Mcp.Tests -f net10.0
dotnet sln add tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj
dotnet add tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj reference src/Koduma.Mcp/Koduma.Mcp.csproj
dotnet add tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj reference src/Koduma.Analyzers.Secrets/Koduma.Analyzers.Secrets.csproj
dotnet add tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj reference src/Koduma.Reporting/Koduma.Reporting.csproj
rm tests/Koduma.Mcp.Tests/UnitTest1.cs
```

Create `src/Koduma.Mcp/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Koduma.Mcp.Tests")]
```

In `src/Koduma.Mcp/Koduma.Mcp.csproj`, add a `ProjectReference` to `Koduma.Reporting` inside the existing `ItemGroup` that already contains the reference to `Koduma.Core`:

```xml
    <ProjectReference Include="..\Koduma.Reporting\Koduma.Reporting.csproj" />
```

- [ ] **Step 7: Write the failing MCP tests**

```csharp
// tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs
using System.Text.Json;
using Koduma.Analyzers.Secrets;
using Koduma.Core.Analysis;
using Koduma.Mcp;
using Koduma.Reporting;
using Xunit;

public class KodumaMcpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-mcp-" + Guid.NewGuid());

    public KodumaMcpServerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IReadOnlyDictionary<string, JsonElement> Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void ResolveAndValidatePath_Throws_WhenPathEscapesRoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath("../outside", _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    [Fact]
    public void ResolveAndValidatePath_Allows_PathWithinRoot()
    {
        var resolved = KodumaMcpServer.ResolveAndValidatePath(".", _root);

        Assert.Equal(Path.GetFullPath(_root), resolved);
    }

    [Fact]
    public async Task HandleAnalyzeProject_UsesRequestedFormat_InsteadOfJson()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "password = \"SuperSecret123!\"");

        ILanguageAnalyzer[] analyzers = { new SecretsAnalyzer() };
        IReportGenerator[] generators = { new TableReportGenerator(), new JsonReportGenerator() };

        var output = await KodumaMcpServer.HandleAnalyzeProject(
            Args($$"""{"path": "{{_root}}", "format": "table"}"""), analyzers, generators, _root, CancellationToken.None);

        Assert.Contains("[Critical] SEC-SECRET-001", output);
        Assert.DoesNotContain("\"Severity\"", output);
    }

    [Fact]
    public async Task HandleAnalyzeProject_AppliesRulesConfigOverride()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "password = \"SuperSecret123!\"");

        ILanguageAnalyzer[] analyzers = { new SecretsAnalyzer() };
        IReportGenerator[] generators = { new JsonReportGenerator() };

        var output = await KodumaMcpServer.HandleAnalyzeProject(
            Args($$"""{"path": "{{_root}}", "rules_config": {"SEC-SECRET-001": "off"}}"""), analyzers, generators, _root, CancellationToken.None);

        using var document = JsonDocument.Parse(output);
        Assert.Equal(0, document.RootElement.GetProperty("Issues").GetArrayLength());
    }
}
```

- [ ] **Step 8: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj`
Expected: FAIL to compile — `KodumaMcpServer.ResolveAndValidatePath` and the new `HandleAnalyzeProject` overload don't exist yet.

- [ ] **Step 9: Implement the root restriction and format/rules_config handling**

Replace the whole contents of `src/Koduma.Mcp/KodumaMcpServer.cs` with:

```csharp
using System.Text.Json;
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using Koduma.Reporting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Koduma.Mcp;

public static class KodumaMcpServer
{
    public static async Task RunAsync(
        IReadOnlyList<ILanguageAnalyzer> analyzers,
        IReadOnlyList<IReportGenerator> generators,
        string root,
        CancellationToken cancellationToken)
    {
        var tools = new List<Tool>
        {
            new()
            {
                Name = "analyze_project",
                Description = "Analyzes a project path for code smells, metrics and security issues.",
                InputSchema = ParseSchema(
                    """{"type":"object","properties":{"path":{"type":"string"},"format":{"type":"string"},"rules_config":{"type":"object"}},"required":["path"]}"""),
            },
            new()
            {
                Name = "list_rules",
                Description = "Lists the rules Koduma can check, optionally filtered by language.",
                InputSchema = ParseSchema(
                    """{"type":"object","properties":{"language":{"type":"string"}}}"""),
            },
            new()
            {
                Name = "get_capabilities",
                Description = "Lists the languages and categories Koduma supports.",
                InputSchema = ParseSchema("""{"type":"object"}"""),
            },
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "koduma", Version = "0.1.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        };

        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools });

        options.Handlers.CallToolHandler = async (request, ct) =>
        {
            var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();

            try
            {
                string payload = request.Params?.Name switch
                {
                    "analyze_project" => await HandleAnalyzeProject(arguments, analyzers, generators, root, ct),
                    "list_rules" => JsonSerializer.Serialize(
                        HandleListRules(arguments, analyzers),
                        KodumaJsonContext.Default.RuleArray),
                    "get_capabilities" => JsonSerializer.Serialize(
                        HandleGetCapabilities(analyzers),
                        McpJsonContext.Default.McpCapabilities),
                    _ => throw new InvalidOperationException($"Unknown tool '{request.Params?.Name}'."),
                };

                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new TextContentBlock { Text = payload } },
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (KodumaConfigException ex)
            {
                return ToolError($"Invalid koduma.json: {ex.Message}");
            }
            catch (Exception ex)
            {
                return ToolError($"Tool '{request.Params?.Name}' failed: {ex.Message}");
            }
        };

        await using var transport = new StdioServerTransport("koduma");
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken);
    }

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CallToolResult ToolError(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
    };

    internal static string ResolveAndValidatePath(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path, fullRoot);
        if (fullPath != fullRoot && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{path}' is outside the configured root '{root}'.");

        return fullPath;
    }

    internal static async Task<string> HandleAnalyzeProject(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyList<ILanguageAnalyzer> analyzers,
        IReadOnlyList<IReportGenerator> generators,
        string root,
        CancellationToken ct)
    {
        var path = arguments.TryGetValue("path", out var pathElement) ? pathElement.GetString()! : ".";
        var fullPath = ResolveAndValidatePath(path, root);

        var format = arguments.TryGetValue("format", out var formatElement) ? formatElement.GetString()! : "json";
        var generator = generators.FirstOrDefault(g => g.Format == format)
            ?? throw new InvalidOperationException($"Unknown format '{format}'.");

        var config = KodumaConfigLoader.LoadOrDefault(fullPath);
        if (arguments.TryGetValue("rules_config", out var rulesConfigElement))
        {
            var overriddenRules = KodumaConfigLoader.ParseRulesConfig(rulesConfigElement);
            config = config with { Rules = overriddenRules };
        }

        var orchestrator = new AnalysisOrchestrator(analyzers, new FileDiscovery());
        var result = await orchestrator.AnalyzeAsync(fullPath, config, ct);

        using var writer = new StringWriter();
        generator.Write(result, writer);
        return writer.ToString();
    }

    private static Rule[] HandleListRules(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyList<ILanguageAnalyzer> analyzers)
    {
        var language = arguments.TryGetValue("language", out var languageElement) ? languageElement.GetString() : null;
        return analyzers
            .SelectMany(a => a.Rules)
            .Where(r => language is null || r.Language == language)
            .ToArray();
    }

    private static McpCapabilities HandleGetCapabilities(IReadOnlyList<ILanguageAnalyzer> analyzers) => new(
        Languages: analyzers.Select(a => a.Language).ToArray(),
        Categories: Enum.GetNames<Category>());
}
```

Note the two signature changes from the original plan's version of this file: `RunAsync` now takes `generators` and `root`; `HandleAnalyzeProject`/`HandleListRules` now take `IReadOnlyDictionary<string, JsonElement>` instead of `IDictionary<string, JsonElement>` (matching what the test helper in Step 7 builds via `ToDictionary`, which returns `Dictionary<TKey,TValue>` — assignable to either, `IReadOnlyDictionary` is used here since neither method mutates the argument map). `HandleAnalyzeProject` and `ResolveAndValidatedPath` are `internal` (not `private`) specifically so the tests in Step 7 can call them directly.

- [ ] **Step 10: Update `Program.cs` to parse `--root` and pass `generators`**

Replace the `mcp-serve` branch and its two local functions in `src/Koduma.Cli/Program.cs`. Change:

```csharp
    "mcp-serve" => await RunMcpServe(analyzers),
```

to:

```csharp
    "mcp-serve" => await RunMcpServe(analyzers, generators, args),
```

and replace:

```csharp
static async Task<int> RunMcpServe(ILanguageAnalyzer[] analyzers)
{
    await Koduma.Mcp.KodumaMcpServer.RunAsync(analyzers, CancellationToken.None);
    return 0;
}
```

with:

```csharp
static async Task<int> RunMcpServe(ILanguageAnalyzer[] analyzers, IReportGenerator[] generators, string[] args)
{
    var root = ParseRoot(args) ?? Directory.GetCurrentDirectory();
    await Koduma.Mcp.KodumaMcpServer.RunAsync(analyzers, generators, root, CancellationToken.None);
    return 0;
}

static string? ParseRoot(string[] args)
{
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--root" && i + 1 < args.Length)
            return args[i + 1];
    }

    return null;
}
```

- [ ] **Step 11: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj`
Expected: PASS for all four new tests.

- [ ] **Step 12: Run the full solution test suite**

Run: `dotnet test koduma.sln`
Expected: all tests pass across every project, including `Koduma.Cli.Tests` (confirm `Program.cs`'s changes didn't break the build — note `Program.cs` has no dedicated unit tests since it's a top-level statements entry point, but a successful full-solution build is the proof it still compiles and wires correctly) and `Koduma.Integration.Tests` (which doesn't exercise `mcp-serve` and is unaffected by this task).

- [ ] **Step 13: Manually verify the real published binary rejects an escaping path and honors `--root`**

```bash
cd /home/manuel/projects/dotnet/koduma
dotnet publish -c Release -r linux-x64 --self-contained -o /tmp/koduma-scope-verify src/Koduma.Cli/Koduma.Cli.csproj
mkdir -p /tmp/koduma-scope-verify-project
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"analyze_project","arguments":{"path":"/etc"}}}' | /tmp/koduma-scope-verify/Koduma.Cli mcp-serve --root /tmp/koduma-scope-verify-project
```

Expected: the second response's `result` has `"isError":true` with a message containing "outside the configured root" — `/etc` is rejected, not analyzed.

- [ ] **Step 14: Commit**

```bash
git add src/Koduma.Mcp src/Koduma.Cli/Program.cs tests/Koduma.Mcp.Tests koduma.sln
git commit -m "feat: restrict MCP analyze_project to a configured root, add format/rules_config parameters"
```
