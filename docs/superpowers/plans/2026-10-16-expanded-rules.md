# Expanded Rule Catalog + Config-Driven Security Patterns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users add security-pattern rules via `koduma.json` without recompiling, and expand the built-in catalog from 4 rules to 17 (8 new security patterns + 5 new C# rules).

**Architecture:** `koduma.json` gains a `customPatterns` array parsed by `KodumaConfigLoader` into `CustomPatternRule` records on `KodumaConfig`; `SecretsAnalyzer` merges them with its (now 10-entry) built-in regex catalog at scan time. Five new Roslyn-based rule files join `NamingRule`/`ComplexityRule` in `Koduma.Analyzers.CSharp`, wired into `CSharpAnalyzer` the same way the existing two are.

**Tech Stack:** .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`), `System.Text.Json.Nodes`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-16-expanded-rules-design.md`

## Global Constraints

- Native AOT / `InvariantGlobalization` compatible: `customPatterns` parsing uses `JsonNode`, matching the existing `rules`/`exclude` parsing in `KodumaConfigLoader` — no reflection-based (de)serialization.
- Custom-pattern regexes are compiled with `new Regex(pattern)` (no `RegexOptions.Compiled`) — they're arbitrary user strings compiled once per analysis run, not worth a dynamic-codegen hint.
- The existing `rules: { "RULE-ID": "off" | {severity} }` override mechanism is untouched; it already applies generically by `RuleId` in `AnalysisOrchestrator`.
- `customPatterns` is parsed only from `koduma.json`, never from the MCP `analyze_project` call's `rules_config` argument (that stays severity/enabled-only).
- `SEC-SECRET-001` (today's AWS-key + password patterns) keeps its existing ID unchanged — do not renumber it.
- `koduma rules` / `list_rules` list each analyzer's static `Rules` catalog only; custom patterns from `koduma.json` apply during `analyze` but intentionally do not appear there (same as existing severity overrides already not appearing there).

## Review Focus

- A `koduma.json` with no `customPatterns` key, or `"customPatterns": []`, must parse to an empty list, not throw or crash.
- A `customPatterns` entry missing `id` or `pattern` must raise a clean `KodumaConfigException`, not a `NullReferenceException`.
- A custom pattern `id` colliding with ANY analyzer's built-in rule ID (not just `Koduma.Analyzers.Secrets`'s own catalog — e.g. `CS-NAMING-001`) must be rejected, since the known-ID set passed in comes from every registered analyzer.
- `TodoCommentRule` must match `TODO`/`FIXME` only inside comments, never inside string literals that happen to contain those words.
- Threshold rules (`ParameterCountRule`, `MethodLengthRule`) must not flag a method sitting exactly at the threshold (e.g. exactly 7 parameters, exactly 60 lines) — only strictly over.

---

### Task 1: Config model — `CustomPatternRule` and `customPatterns` parsing

**Files:**
- Create: `src/Koduma.Core/Model/CustomPatternRule.cs`
- Modify: `src/Koduma.Core/Config/KodumaConfig.cs`
- Modify: `src/Koduma.Core/Config/KodumaConfigLoader.cs`
- Modify: `tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs:87-93` (add third positional arg to the one `new KodumaConfig(...)` call)
- Modify: `tests/Koduma.Core.Tests/FileDiscoveryTests.cs:41` (add third positional arg)
- Test: `tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs`

**Interfaces:**
- Produces: `public record CustomPatternRule(string Id, string Name, string Pattern, Severity Severity, string Message);`
- Produces: `KodumaConfig` gains a third positional field `List<CustomPatternRule> CustomPatterns`; `KodumaConfig.Default` becomes `new(new(), new(), new())`.
- Produces: `public static KodumaConfig LoadOrDefault(string projectPath, IReadOnlyCollection<string> knownRuleIds)` — signature change, second parameter is now required (was single-arg before). `ParseRulesConfig` is unchanged.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs` (update the 6 existing `KodumaConfigLoader.LoadOrDefault(_root)` calls in that file to `KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>())` first, so the file still compiles against the new signature):

```csharp
[Fact]
public void LoadOrDefault_ParsesCustomPatterns_WithDefaultsForSeverityAndMessage()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [
        { "id": "SEC-CUSTOM-001", "name": "Internal token", "pattern": "ITOK-[0-9A-F]{8}" }
    ] }
    """);

    var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

    var rule = Assert.Single(config.CustomPatterns);
    Assert.Equal("SEC-CUSTOM-001", rule.Id);
    Assert.Equal(Severity.Critical, rule.Severity);
    Assert.Equal("Possible Internal token leak.", rule.Message);
}

[Fact]
public void LoadOrDefault_ParsesCustomPatterns_WithExplicitSeverityAndMessage()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [
        { "id": "SEC-CUSTOM-001", "name": "Internal token", "pattern": "ITOK-[0-9A-F]{8}",
          "severity": "major", "message": "Internal token leaked." }
    ] }
    """);

    var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

    var rule = Assert.Single(config.CustomPatterns);
    Assert.Equal(Severity.Major, rule.Severity);
    Assert.Equal("Internal token leaked.", rule.Message);
}

[Fact]
public void LoadOrDefault_ReturnsEmptyCustomPatterns_WhenKeyIsAbsent()
{
    var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

    Assert.Empty(config.CustomPatterns);
}

[Fact]
public void LoadOrDefault_Throws_WhenCustomPatternIsMissingId()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [ { "name": "x", "pattern": "a" } ] }
    """);

    Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
}

[Fact]
public void LoadOrDefault_Throws_WhenCustomPatternIsMissingPattern()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [ { "id": "SEC-CUSTOM-001", "name": "x" } ] }
    """);

    Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
}

[Fact]
public void LoadOrDefault_Throws_WhenCustomPatternRegexIsInvalid()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [ { "id": "SEC-CUSTOM-001", "name": "x", "pattern": "[unterminated" } ] }
    """);

    Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
}

[Fact]
public void LoadOrDefault_Throws_WhenCustomPatternIdsCollideWithEachOther()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [
        { "id": "SEC-CUSTOM-001", "name": "x", "pattern": "a" },
        { "id": "SEC-CUSTOM-001", "name": "y", "pattern": "b" }
    ] }
    """);

    Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
}

[Fact]
public void LoadOrDefault_Throws_WhenCustomPatternIdCollidesWithAnyKnownBuiltInRuleId()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [ { "id": "CS-NAMING-001", "name": "x", "pattern": "a" } ] }
    """);

    Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, new[] { "CS-NAMING-001" }));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Core.Tests --filter "FullyQualifiedName~KodumaConfigLoaderTests"`
Expected: compile error (no `knownRuleIds` parameter, no `CustomPatterns` property, no `CustomPatternRule` type) — fix compile errors by adding the production types below, then re-run until the new `[Fact]`s fail on assertions rather than failing to build.

- [ ] **Step 3: Implement `CustomPatternRule` in `src/Koduma.Core/Model/CustomPatternRule.cs`**

```csharp
namespace Koduma.Core.Model;

public record CustomPatternRule(string Id, string Name, string Pattern, Severity Severity, string Message);
```

- [ ] **Step 4: Add `CustomPatterns` to `KodumaConfig` in `src/Koduma.Core/Config/KodumaConfig.cs`**

Add `List<CustomPatternRule> CustomPatterns` as the third positional parameter; update `Default` to `new(new(), new(), new())`. Fix the two existing call sites that construct `KodumaConfig` positionally (`AnalysisOrchestratorTests.cs:87-93`, `FileDiscoveryTests.cs:41`) to pass `new()` as the third argument.

- [ ] **Step 5: Implement `customPatterns` parsing in `src/Koduma.Core/Config/KodumaConfigLoader.cs`**

Change `LoadOrDefault`'s signature to `public static KodumaConfig LoadOrDefault(string projectPath, IReadOnlyCollection<string> knownRuleIds)`. Add a private `ParseCustomPatterns(JsonNode? node, IReadOnlyCollection<string> knownRuleIds) -> List<CustomPatternRule>` following the existing `rules`/`exclude` parsing style in this file (`JsonNode`/`JsonArray`/`JsonObject`, throwing `KodumaConfigException` with a `"customPatterns: ..."`-prefixed message on error, mirroring how `exclude:` and `Rule '{ruleId}':` messages read today). It must:
- Treat an absent `customPatterns` key as an empty list.
- Require each entry to be a JSON object with non-empty `id`, `name`, and `pattern` strings; throw otherwise.
- Throw if `pattern` doesn't compile as a `Regex` (wrap the `ArgumentException`).
- Throw on a duplicate `id` within the array, and on an `id` present in `knownRuleIds`.
- Parse optional `severity` the same way `ParseRuleOverride` does (`Enum.TryParse<Severity>`, case-insensitive, throw on unknown value); default `Severity.Critical` when absent.
- Default `message` to `$"Possible {name} leak."` when absent.

Call `ParseCustomPatterns(root?["customPatterns"], knownRuleIds)` from `LoadOrDefault` and pass the result into the returned `KodumaConfig`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Core.Tests`
Expected: PASS (all tests in `Koduma.Core.Tests`, including the pre-existing ones with their calls updated in Step 1)

- [ ] **Step 7: Commit**

```bash
git add src/Koduma.Core/Model/CustomPatternRule.cs src/Koduma.Core/Config/KodumaConfig.cs src/Koduma.Core/Config/KodumaConfigLoader.cs tests/Koduma.Core.Tests/KodumaConfigLoaderTests.cs tests/Koduma.Core.Tests/AnalysisOrchestratorTests.cs tests/Koduma.Core.Tests/FileDiscoveryTests.cs
git commit -m "feat: parse customPatterns from koduma.json into CustomPatternRule"
```

---

### Task 2: Wire known rule IDs through CLI and MCP entry points

**Files:**
- Modify: `src/Koduma.Cli/AnalyzeCommand.cs:40-49`
- Modify: `src/Koduma.Mcp/KodumaMcpServer.cs:205`
- Test: `tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs`
- Test: `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs`

**Interfaces:**
- Consumes: `KodumaConfigLoader.LoadOrDefault(string, IReadOnlyCollection<string>)` from Task 1.
- Consumes: `ILanguageAnalyzer.Rules` (existing) to build the known-ID set via `analyzers.SelectMany(a => a.Rules).Select(r => r.Id)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs`:

```csharp
[Fact]
public async Task Run_ReturnsTwo_AndPrintsCleanMessage_WhenCustomPatternIdCollidesWithBuiltInRule()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [ { "id": "SEC-SECRET-001", "name": "x", "pattern": "a" } ] }
    """);
    File.WriteAllText(Path.Combine(_root, "ok.txt"), "nothing suspicious here");
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

    Assert.Equal(2, exitCode);
    Assert.Contains("koduma.json", stderr.ToString());
    Assert.DoesNotContain("Exception", stderr.ToString());
}
```

Add to `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs`:

```csharp
[Fact]
public async Task HandleAnalyzeProject_Throws_WhenCustomPatternIdCollidesWithBuiltInRule()
{
    File.WriteAllText(Path.Combine(_root, "koduma.json"), """
    { "customPatterns": [ { "id": "SEC-SECRET-001", "name": "x", "pattern": "a" } ] }
    """);
    File.WriteAllText(Path.Combine(_root, "ok.txt"), "nothing suspicious here");

    ILanguageAnalyzer[] analyzers = { new SecretsAnalyzer() };
    IReportGenerator[] generators = { new JsonReportGenerator() };

    await Assert.ThrowsAsync<KodumaConfigException>(() => KodumaMcpServer.HandleAnalyzeProject(
        Args($$"""{"path": {{JsonSerializer.Serialize(_root)}}}"""), analyzers, generators, _root, CancellationToken.None));
}
```

(Add `using Koduma.Core.Config;` to `KodumaMcpServerTests.cs` if not already present.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Cli.Tests tests/Koduma.Mcp.Tests`
Expected: FAIL — today both entry points call the single-arg `LoadOrDefault`, so a colliding `customPatterns` entry is silently accepted (no exception, exit code 0).

- [ ] **Step 3: Update the call sites**

In `AnalyzeCommand.Run` (`src/Koduma.Cli/AnalyzeCommand.cs`), change `KodumaConfigLoader.LoadOrDefault(path)` to `KodumaConfigLoader.LoadOrDefault(path, analyzers.SelectMany(a => a.Rules).Select(r => r.Id).ToArray())`.

In `KodumaMcpServer.HandleAnalyzeProject` (`src/Koduma.Mcp/KodumaMcpServer.cs:205`), make the same change using the `analyzers` parameter already in scope.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Cli.Tests tests/Koduma.Mcp.Tests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Cli/AnalyzeCommand.cs src/Koduma.Mcp/KodumaMcpServer.cs tests/Koduma.Cli.Tests/AnalyzeCommandTests.cs tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs
git commit -m "feat: reject customPatterns ids that collide with built-in rule ids"
```

---

### Task 3: Expand `SecretsAnalyzer`'s built-in catalog and merge custom patterns

**Files:**
- Modify: `src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs`

**Interfaces:**
- Consumes: `KodumaConfig.CustomPatterns` (`List<CustomPatternRule>`) from Task 1.
- Produces: `SecretsAnalyzer.Rules` now has 10 entries (`SEC-SECRET-001` through `SEC-SECRET-009`, plus `SEC-SQLI-001`), unchanged shape.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs`:

```csharp
[Fact]
public async Task AnalyzeAsync_FlagsAllEightNewBuiltInSecretPatterns()
{
    File.WriteAllText(_file, """
    var gh = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
    var slack = "xoxb-111111111111-222222222222-abcdefghijklmnopqrstuvwx";
    var google = "AIzaSyAbcdefghijklmnopqrstuvwxyz0123456";
    var stripe = "sk_live_abcdefghijklmnopqrstuvwx";
    var pk = "-----BEGIN RSA PRIVATE KEY-----";
    var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dGVzdHNpZ25hdHVyZXZhbHVl";
    var url = "https://user:pass@example.com/path";
    var azure = "AccountKey=abcdefghijklmnopqrstABCDEFGHIJKLMNOP==";
    """);

    var analyzer = new SecretsAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

    var ruleIds = result.Issues.Select(i => i.RuleId).ToHashSet();
    foreach (var expected in new[] { "SEC-SECRET-002", "SEC-SECRET-003", "SEC-SECRET-004", "SEC-SECRET-005", "SEC-SECRET-006", "SEC-SECRET-007", "SEC-SECRET-008", "SEC-SECRET-009" })
        Assert.Contains(expected, ruleIds);
    Assert.All(result.Issues, i => Assert.Equal(Category.Security, i.Category));
}

[Fact]
public async Task AnalyzeAsync_FlagsCustomPattern_WithConfiguredSeverityAndMessage()
{
    File.WriteAllText(_file, """var t = "ITOK-ABCDEF01";""");
    var config = KodumaConfig.Default with
    {
        CustomPatterns = new List<CustomPatternRule>
        {
            new("SEC-CUSTOM-001", "Internal token", "ITOK-[0-9A-F]{8}", Severity.Major, "Internal token leaked.")
        }
    };

    var analyzer = new SecretsAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, config, CancellationToken.None);

    var issue = Assert.Single(result.Issues);
    Assert.Equal("SEC-CUSTOM-001", issue.RuleId);
    Assert.Equal(Severity.Major, issue.Severity);
    Assert.Equal("Internal token leaked.", issue.Message);
    Assert.Equal(Category.Security, issue.Category);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koduma.Analyzers.Secrets.Tests --filter "FullyQualifiedName~SecretsAnalyzerTests"`
Expected: FAIL (new rule IDs don't exist yet; `CustomPatterns` not read)

- [ ] **Step 3: Add the 8 new built-in patterns and merge `config.CustomPatterns` in `src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs`**

Extend the `Patterns` tuple array with a fourth element carrying the fully-resolved message (replacing today's `ruleId == "SEC-SQLI-001" ? ... : ...` ternary in the loop body with the per-entry message), and add these 8 entries alongside the existing two:

| RuleId | Regex | Message |
|---|---|---|
| `SEC-SECRET-002` | `gh[pousr]_[A-Za-z0-9]{36}` | `Possible hardcoded secret (GitHub personal access token).` |
| `SEC-SECRET-003` | `xox[baprs]-[0-9a-zA-Z-]{10,48}` | `Possible hardcoded secret (Slack token).` |
| `SEC-SECRET-004` | `AIza[0-9A-Za-z\-_]{35}` | `Possible hardcoded secret (Google API key).` |
| `SEC-SECRET-005` | `sk_live_[0-9a-zA-Z]{24,}` | `Possible hardcoded secret (Stripe live key).` |
| `SEC-SECRET-006` | `-----BEGIN (RSA \|EC \|DSA \|OPENSSH \|PGP )?PRIVATE KEY-----` | `Possible hardcoded secret (private key).` |
| `SEC-SECRET-007` | `eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}` | `Possible hardcoded secret (JWT).` |
| `SEC-SECRET-008` | `https?://[^:/\s]+:[^@/\s]+@` | `Possible hardcoded credentials in URL.` |
| `SEC-SECRET-009` | `AccountKey=[A-Za-z0-9+/=]{20,}` | `Possible hardcoded secret (Azure connection string AccountKey).` |

Add a matching `Rule(...)` entry (`Category.Security`, `Severity.Critical`, language `"security"`, `EngineRuleId: null`) for each new ID to `SecretsAnalyzer.Rules`.

In `AnalyzeAsync`, before the file loop, compile `config.CustomPatterns` into the same per-line scanning shape as the built-in `Patterns` (id, compiled `Regex`, severity, message), and scan both sets per line — built-in matches keep `Severity.Critical` as today; custom matches use the `CustomPatternRule`'s own `Severity` and `Message`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koduma.Analyzers.Secrets.Tests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Koduma.Analyzers.Secrets/SecretsAnalyzer.cs tests/Koduma.Analyzers.Secrets.Tests/SecretsAnalyzerTests.cs
git commit -m "feat: add 8 built-in secret patterns and merge config-driven custom patterns"
```

---

### Task 4: `CS-DESIGN-001` — empty catch block

**Files:**
- Create: `src/Koduma.Analyzers.CSharp/EmptyCatchRule.cs`
- Modify: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`

**Interfaces:**
- Produces: `internal static class EmptyCatchRule { public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath); }` — same shape as `NamingRule.Evaluate`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`:

```csharp
[Fact]
public async Task AnalyzeAsync_FlagsEmptyCatchBlock_ButNotOneWithAStatement()
{
    File.WriteAllText(_file, """
    public class Example
    {
        public void M()
        {
            try { DoWork(); } catch (Exception) { }
            try { DoWork(); } catch (Exception) { Log(); }
        }
        private void DoWork() { }
        private void Log() { }
    }
    """);

    var analyzer = new CSharpAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

    var emptyCatchIssues = result.Issues.Where(i => i.RuleId == "CS-DESIGN-001").ToList();
    Assert.Single(emptyCatchIssues);
    Assert.Equal(5, emptyCatchIssues[0].Line);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests --filter "FullyQualifiedName~AnalyzeAsync_FlagsEmptyCatchBlock"`
Expected: FAIL (`CS-DESIGN-001` never raised)

- [ ] **Step 3: Implement `EmptyCatchRule.Evaluate` in `src/Koduma.Analyzers.CSharp/EmptyCatchRule.cs`**

Walk `root.DescendantNodes().OfType<CatchClauseSyntax>()`; flag each where `catchClause.Block.Statements.Count == 0`. Use `RuleId: "CS-DESIGN-001"`, `Severity.Major`, `Category.CodeSmell`, message `"Catch block is empty and silently swallows the exception."`, location from the `catch` keyword's line span — follow `NamingRule.cs`'s exact `Issue(...)` construction pattern (including `EndLine`/`EndColumn` from the same `GetLineSpan()`).

- [ ] **Step 4: Wire it into `CSharpAnalyzer`**

In `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`, add `new Rule("CS-DESIGN-001", "Empty catch block", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null)` to `Rules`, and `issues.AddRange(EmptyCatchRule.Evaluate(tree, file));` next to the existing `NamingRule.Evaluate` call in `AnalyzeAsync`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.CSharp/EmptyCatchRule.cs src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs
git commit -m "feat: add CS-DESIGN-001 empty catch block rule"
```

---

### Task 5: `CS-DESIGN-002` — too many parameters

**Files:**
- Create: `src/Koduma.Analyzers.CSharp/ParameterCountRule.cs`
- Modify: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`

**Interfaces:**
- Produces: `internal static class ParameterCountRule { public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath); }`, with `private const int MaxParameters = 7;`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AnalyzeAsync_FlagsMethodWithEightParameters_ButNotSeven()
{
    File.WriteAllText(_file, """
    public class Example
    {
        public void Seven(int a, int b, int c, int d, int e, int f, int g) { }
        public void Eight(int a, int b, int c, int d, int e, int f, int g, int h) { }
    }
    """);

    var analyzer = new CSharpAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

    var issues = result.Issues.Where(i => i.RuleId == "CS-DESIGN-002").ToList();
    Assert.Single(issues);
    Assert.Contains("Eight", issues[0].Message);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests --filter "FullyQualifiedName~AnalyzeAsync_FlagsMethodWithEightParameters"`
Expected: FAIL

- [ ] **Step 3: Implement `ParameterCountRule.Evaluate`**

Walk `root.DescendantNodes().OfType<MethodDeclarationSyntax>()`; flag where `method.ParameterList.Parameters.Count > MaxParameters`. `RuleId: "CS-DESIGN-002"`, `Severity.Major`, `Category.CodeSmell`, message `$"Method '{method.Identifier.ValueText}' has {method.ParameterList.Parameters.Count} parameters, exceeding {MaxParameters}."`, location from `method.Identifier.GetLocation().GetLineSpan()` (same pattern as `ComplexityRule.cs`).

- [ ] **Step 4: Wire it into `CSharpAnalyzer`**

Add `new Rule("CS-DESIGN-002", "Too many parameters", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null)` to `Rules`, and `issues.AddRange(ParameterCountRule.Evaluate(tree, file));` in `AnalyzeAsync`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.CSharp/ParameterCountRule.cs src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs
git commit -m "feat: add CS-DESIGN-002 too-many-parameters rule"
```

---

### Task 6: `CS-DESIGN-003` — method too long

**Files:**
- Create: `src/Koduma.Analyzers.CSharp/MethodLengthRule.cs`
- Modify: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`

**Interfaces:**
- Produces: `internal static class MethodLengthRule { public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath); }`, with `private const int MaxLines = 60;`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AnalyzeAsync_FlagsMethodOver60Lines_ButNotExactly60_NorExpressionBodiedOnes()
{
    var exactly60 = string.Join("\n", Enumerable.Range(0, 58).Select(i => $"        var a{i} = {i};"));
    var over60 = string.Join("\n", Enumerable.Range(0, 59).Select(i => $"        var b{i} = {i};"));
    File.WriteAllText(_file, $$"""
    public class Example
    {
        public int Short() => 1;
        public void AtThreshold()
        {
    {{exactly60}}
        }
        public void Long()
        {
    {{over60}}
        }
    }
    """);

    var analyzer = new CSharpAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

    var issues = result.Issues.Where(i => i.RuleId == "CS-DESIGN-003").ToList();
    Assert.Single(issues);
    Assert.Contains("Long", issues[0].Message);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests --filter "FullyQualifiedName~AnalyzeAsync_FlagsMethodOver60Lines"`
Expected: FAIL

- [ ] **Step 3: Implement `MethodLengthRule.Evaluate`**

Walk `root.DescendantNodes().OfType<MethodDeclarationSyntax>()`; skip when `method.Body is null` (expression-bodied members). For block-bodied methods, compute `lineCount = endLine - startLine + 1` from `method.Body!.GetLocation().GetLineSpan()`; flag when `lineCount > MaxLines`. `RuleId: "CS-DESIGN-003"`, `Severity.Major`, `Category.CodeSmell`, message `$"Method '{method.Identifier.ValueText}' is {lineCount} lines long, exceeding {MaxLines}."`.

- [ ] **Step 4: Wire it into `CSharpAnalyzer`**

Add `new Rule("CS-DESIGN-003", "Method too long", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null)` to `Rules`, and `issues.AddRange(MethodLengthRule.Evaluate(tree, file));` in `AnalyzeAsync`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.CSharp/MethodLengthRule.cs src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs
git commit -m "feat: add CS-DESIGN-003 method-too-long rule"
```

---

### Task 7: `CS-NAMING-002` — public class/method PascalCase

**Files:**
- Create: `src/Koduma.Analyzers.CSharp/PublicNamingRule.cs`
- Modify: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`

**Interfaces:**
- Produces: `internal static class PublicNamingRule { public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath); }`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AnalyzeAsync_FlagsNonPascalCasePublicClassAndMethod_ButNotPrivateMembers()
{
    File.WriteAllText(_file, """
    public class example
    {
        public void doWork() { }
        private void helper() { }
    }
    """);

    var analyzer = new CSharpAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

    var issues = result.Issues.Where(i => i.RuleId == "CS-NAMING-002").ToList();
    Assert.Equal(2, issues.Count);
    Assert.Contains(issues, i => i.Message.Contains("example"));
    Assert.Contains(issues, i => i.Message.Contains("doWork"));
    Assert.DoesNotContain(issues, i => i.Message.Contains("helper"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests --filter "FullyQualifiedName~AnalyzeAsync_FlagsNonPascalCasePublicClassAndMethod"`
Expected: FAIL

- [ ] **Step 3: Implement `PublicNamingRule.Evaluate`**

For `root.DescendantNodes().OfType<ClassDeclarationSyntax>()` with `Modifiers.Any(SyntaxKind.PublicKeyword)`, and for `root.DescendantNodes().OfType<MethodDeclarationSyntax>()` with `Modifiers.Any(SyntaxKind.PublicKeyword)`: flag when the identifier's first character is not an uppercase ASCII letter (`char.IsUpper(name[0])`). `RuleId: "CS-NAMING-002"`, `Severity.Minor`, `Category.CodeSmell`, message `$"Public {kind} '{name}' should be PascalCase."` where `kind` is `"class"` or `"method"`.

- [ ] **Step 4: Wire it into `CSharpAnalyzer`**

Add `new Rule("CS-NAMING-002", "Public member naming", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null)` to `Rules`, and `issues.AddRange(PublicNamingRule.Evaluate(tree, file));` in `AnalyzeAsync`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.CSharp/PublicNamingRule.cs src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs
git commit -m "feat: add CS-NAMING-002 public PascalCase naming rule"
```

---

### Task 8: `CS-SMELL-001` — leftover TODO/FIXME comment

**Files:**
- Create: `src/Koduma.Analyzers.CSharp/TodoCommentRule.cs`
- Modify: `src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs`
- Test: `tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs`

**Interfaces:**
- Produces: `internal static class TodoCommentRule { public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath); }`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AnalyzeAsync_FlagsTodoComment_ButNotTodoInsideAStringLiteral()
{
    File.WriteAllText(_file, """
    public class Example
    {
        // TODO: fix this
        public void M()
        {
            var s = "TODO list for the user";
        }
    }
    """);

    var analyzer = new CSharpAnalyzer();
    var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

    var issues = result.Issues.Where(i => i.RuleId == "CS-SMELL-001").ToList();
    Assert.Single(issues);
    Assert.Equal(3, issues[0].Line);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests --filter "FullyQualifiedName~AnalyzeAsync_FlagsTodoComment"`
Expected: FAIL

- [ ] **Step 3: Implement `TodoCommentRule.Evaluate`**

Walk `root.DescendantTrivia()` filtering to `SyntaxKind.SingleLineCommentTrivia`, `SyntaxKind.MultiLineCommentTrivia`, and `SyntaxKind.SingleLineDocumentationCommentTrivia`; flag when the trivia's text contains `"TODO"` or `"FIXME"` (case-insensitive, e.g. `trivia.ToString().Contains("TODO", StringComparison.OrdinalIgnoreCase)`). `RuleId: "CS-SMELL-001"`, `Severity.Minor`, `Category.CodeSmell`, message `"Unresolved TODO/FIXME comment left in code."`, location from `trivia.GetLocation().GetLineSpan()`.

- [ ] **Step 4: Wire it into `CSharpAnalyzer`**

Add `new Rule("CS-SMELL-001", "Leftover TODO/FIXME comment", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null)` to `Rules`, and `issues.AddRange(TodoCommentRule.Evaluate(tree, file));` in `AnalyzeAsync`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.CSharp.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Koduma.Analyzers.CSharp/TodoCommentRule.cs src/Koduma.Analyzers.CSharp/CSharpAnalyzer.cs tests/Koduma.Analyzers.CSharp.Tests/CSharpAnalyzerTests.cs
git commit -m "feat: add CS-SMELL-001 leftover TODO/FIXME comment rule"
```

---

### Task 9: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: PASS (all projects, including `Koduma.Integration.Tests`'s real AOT publish/run, which exercises `koduma analyze`/`koduma rules` end to end against the expanded catalog)

- [ ] **Step 2: Spot-check `koduma rules` lists all 17 rules**

Run: `dotnet run --project src/Koduma.Cli -- rules`
Expected: output includes all of `CS-NAMING-001`, `CS-NAMING-002`, `CS-COMPLEXITY-001`, `CS-DESIGN-001`, `CS-DESIGN-002`, `CS-DESIGN-003`, `CS-SMELL-001`, `SEC-SECRET-001`..`009`, `SEC-SQLI-001`
