# Koduma Windows Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Koduma's CLI, MCP server, and test suite portable to Windows, without reopening the already-hardened MCP symlink-canonicalization algorithm.

**Architecture:** Three small, independent fixes: (1) resolve the correct ESLint executable name per platform, (2) make the string-handling helpers around the MCP root-containment check platform-portable (not the containment algorithm itself), (3) make the end-to-end integration test publish for whatever platform it's actually running on.

**Tech Stack:** .NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-12-koduma-windows-compat-design.md`

## Global Constraints

- Target framework for every project: `net10.0` (unchanged).
- Do not modify `Canonicalize`'s walking logic, the 40-hop cap, or the raw-`LinkTarget`-based symlink resolution in `src/Koduma.Mcp/KodumaMcpServer.cs` — that code is final after 4 rounds of adversarial security review. Only the small string-handling helpers (`SplitSegments`, `GetRoot`, `IsWithinRoot`) change, and only for platform portability.
- No actual Windows machine is available to run or verify anything in this plan empirically. Every fix is implemented and verified by code inspection, hand-tracing, and (where the underlying behavior doesn't depend on a real Windows filesystem) automated tests that assert the INTENDED platform-conditional behavior. Say so plainly in each task where a test can't meaningfully exercise the Windows-only path from this environment — don't write a test that looks like Windows coverage but isn't.
- No new CI infrastructure — none exists in this repo and none is being added here.
- Touch only what each task names.

## Review Focus

- A future contributor running the CLI on Windows with ESLint installed via npm would otherwise silently get "eslint could not be run" for every JS/TS analysis — covered by Task 1.
- A `--root` value and an actual resolved path differing only in case on a real Windows (case-insensitive) filesystem would otherwise be wrongly rejected as "outside the configured root" — covered by Task 2's `IsWithinRoot` fix (verified by a platform-conditional test, not a real case-insensitive filesystem, since none is available here).
- The integration test silently failing to publish or to find the published binary by name on any platform other than Linux x64 — covered by Task 3.
- A raw symlink target containing the platform's alternate directory separator would otherwise be treated as one unsplit segment instead of being walked component-by-component — covered by Task 2's `SplitSegments`/`GetRoot` fix (acknowledged as not meaningfully testable from this Linux environment, since `AltDirectorySeparatorChar` equals `DirectorySeparatorChar` on Linux).
- Windows's requirement of Developer Mode/admin rights to create symlinks silently breaking ~8 existing tests' fixture setup (not their assertions) on a Windows machine, with no explanation pointing at the real cause — covered by Task 3's documentation note.

---

### Task 1: ESLint executable resolution on Windows

**Files:**
- Modify: `src/Koduma.Analyzers.External/EslintAnalyzer.cs`
- Create: `src/Koduma.Analyzers.External/AssemblyInfo.cs`
- Create: `tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj` (modify — see Step 1)
- Test: `tests/Koduma.Analyzers.External.Tests/EslintAnalyzerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `EslintAnalyzer.ResolveEslintExecutable(bool isWindows) -> string` (internal static) — used only by this task's test, no other task depends on it.

- [ ] **Step 1: Confirm `InternalsVisibleTo` wiring**

`tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj` already references `src/Koduma.Analyzers.External/Koduma.Analyzers.External.csproj` (check with `cat` to confirm before proceeding — if it doesn't, add `<ProjectReference Include="..\..\src\Koduma.Analyzers.External\Koduma.Analyzers.External.csproj" />`). Create `src/Koduma.Analyzers.External/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Koduma.Analyzers.External.Tests")]
```

- [ ] **Step 2: Write the failing test**

Add to `tests/Koduma.Analyzers.External.Tests/EslintAnalyzerTests.cs` (create the file if it doesn't exist yet — check first, since this project may only have `ExternalProcessRunnerTests.cs`/`EslintOutputMapperTests.cs` from earlier tasks):

```csharp
using Koduma.Analyzers.External;
using Xunit;

public class EslintAnalyzerTests
{
    [Theory]
    [InlineData(true, "eslint.cmd")]
    [InlineData(false, "eslint")]
    public void ResolveEslintExecutable_PicksCmdShimOnWindowsOnly(bool isWindows, string expected)
    {
        Assert.Equal(expected, EslintAnalyzer.ResolveEslintExecutable(isWindows));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj --filter ResolveEslintExecutable_PicksCmdShimOnWindowsOnly`
Expected: FAIL to compile — `EslintAnalyzer.ResolveEslintExecutable` doesn't exist yet.

- [ ] **Step 4: Implement the resolver**

In `src/Koduma.Analyzers.External/EslintAnalyzer.cs`, add the resolver method and replace the hardcoded `EslintExecutable` constant/literal. Change:

```csharp
public class EslintAnalyzer : ILanguageAnalyzer
{
    private readonly ExternalProcessRunner _runner;

    public EslintAnalyzer(ExternalProcessRunner runner) => _runner = runner;
```

to:

```csharp
public class EslintAnalyzer : ILanguageAnalyzer
{
    internal static string ResolveEslintExecutable(bool isWindows) => isWindows ? "eslint.cmd" : "eslint";

    private static readonly string EslintExecutable = ResolveEslintExecutable(OperatingSystem.IsWindows());

    private readonly ExternalProcessRunner _runner;

    public EslintAnalyzer(ExternalProcessRunner runner) => _runner = runner;
```

Then change the line that calls the runner (currently `await _runner.RunAsync("eslint", arguments, projectPath);`) to:

```csharp
        var (exitCode, stdOut, stdErr) = await _runner.RunAsync(EslintExecutable, arguments, projectPath);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Analyzers.External.Tests/Koduma.Analyzers.External.Tests.csproj`
Expected: PASS (both new cases, plus all pre-existing tests in this project unaffected — `ExternalProcessRunnerTests`/`EslintOutputMapperTests` don't reference the executable name).

- [ ] **Step 6: Run the full solution test suite**

Run: `dotnet test koduma.sln`
Expected: all tests pass. On this Linux machine, `EslintExecutable` resolves to `"eslint"` exactly as before, so no behavior change for any test or the real binary on this platform — only the Windows branch (untestable here) changes.

- [ ] **Step 7: Commit**

```bash
git add src/Koduma.Analyzers.External tests/Koduma.Analyzers.External.Tests
git commit -m "feat: resolve eslint.cmd on Windows instead of the bare eslint executable name"
```

---

### Task 2: Portable path handling in the MCP root-containment check

**Files:**
- Modify: `src/Koduma.Mcp/KodumaMcpServer.cs`
- Test: `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `IsWithinRoot` changes from `private` to `internal` (already covered by the existing `InternalsVisibleTo("Koduma.Mcp.Tests")` in `src/Koduma.Mcp/AssemblyInfo.cs` — no new assembly wiring needed). No other task depends on this.

- [ ] **Step 1: Write the failing test**

Add to `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs` (keep all existing tests in the file unchanged):

```csharp
    [Fact]
    public void IsWithinRoot_CaseSensitivityMatchesPlatform()
    {
        var result = KodumaMcpServer.IsWithinRoot("/ROOT/sub", "/root");

        Assert.Equal(OperatingSystem.IsWindows(), result);
    }
```

This asserts the platform-conditional behavior directly: on this Linux machine, `OperatingSystem.IsWindows()` is `false`, so the test expects `IsWithinRoot` to also return `false` (case-sensitive rejection) — pinning today's Linux behavior while also correctly describing the intended Windows behavior if this same test ever runs there.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj --filter IsWithinRoot_CaseSensitivityMatchesPlatform`
Expected: FAIL to compile — `IsWithinRoot` is currently `private`, not accessible from the test assembly even with `InternalsVisibleTo` (internal, not private, is what `InternalsVisibleTo` exposes).

- [ ] **Step 3: Make the three helpers platform-portable**

In `src/Koduma.Mcp/KodumaMcpServer.cs`, replace the three helper methods. Find:

```csharp
    private static string GetRoot(string path)
    {
        var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
    }

    private static IEnumerable<string> SplitSegments(string path)
    {
        var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
        return path.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsWithinRoot(string candidate, string fullRoot)
    {
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return candidate == fullRoot || candidate.StartsWith(prefix, StringComparison.Ordinal);
    }
```

Replace with:

```csharp
    private static string GetRoot(string path)
    {
        var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
    }

    private static IEnumerable<string> SplitSegments(string path)
    {
        var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
        return path.Substring(root.Length).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    internal static bool IsWithinRoot(string candidate, string fullRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return string.Equals(candidate, fullRoot, comparison) || candidate.StartsWith(prefix, comparison);
    }
```

Do not change anything else in this file — `Canonicalize`, `ResolveAndValidatePath`, and every other method stay exactly as they are.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Koduma.Mcp.Tests/Koduma.Mcp.Tests.csproj`
Expected: PASS, including every pre-existing test in this file (the four rounds of symlink-escape/chained-symlink/trailing-separator/control tests) — this step is the regression check that the portability change didn't alter behavior on Linux, since `AltDirectorySeparatorChar` equals `DirectorySeparatorChar` here and the comparison mode is unchanged (`Ordinal`) on this platform.

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test koduma.sln`
Expected: all tests pass.

- [ ] **Step 6: Self-review note on test coverage limits**

In your report for this task, explicitly state: the `SplitSegments`/`GetRoot` change (splitting on the alternate separator) has no way to be exercised by an automated test from this Linux environment, since `Path.AltDirectorySeparatorChar` equals `Path.DirectorySeparatorChar` on Linux — the fix is correct by inspection (confirmed by reading `Path.Combine`/`Path.GetPathRoot`'s documented cross-platform contracts), not by a passing test that would fail without it. Do not write a test that merely restates this and claims it as coverage.

- [ ] **Step 7: Commit**

```bash
git add src/Koduma.Mcp/KodumaMcpServer.cs tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs
git commit -m "fix: make MCP root-containment path helpers platform-portable (Windows separators and case-insensitivity)"
```

---

### Task 3: Integration test portability + Windows symlink-test documentation

**Files:**
- Modify: `tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs`
- Modify: `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs`
- Modify: `tests/Koduma.Core.Tests/FileDiscoveryTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing consumed by other tasks.

- [ ] **Step 1: Make the integration test publish for the current platform's RID**

In `tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs`, add the needed `using` and change the publish `Arguments`. Add near the top:

```csharp
using System.Runtime.InteropServices;
```

Change:

```csharp
            Arguments = $"publish \"{Path.Combine(_repoRoot, "src", "Koduma.Cli", "Koduma.Cli.csproj")}\" -c Release -r linux-x64 --self-contained -o \"{_publishDir}\"",
```

to:

```csharp
            Arguments = $"publish \"{Path.Combine(_repoRoot, "src", "Koduma.Cli", "Koduma.Cli.csproj")}\" -c Release -r {RuntimeInformation.RuntimeIdentifier} --self-contained -o \"{_publishDir}\"",
```

- [ ] **Step 2: Make the published-binary lookup platform-aware**

In the same file, change:

```csharp
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunKoduma(params string[] args)
    {
        var binary = Path.Combine(_publishDir, "Koduma.Cli");
```

to:

```csharp
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunKoduma(params string[] args)
    {
        var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var binary = Path.Combine(_publishDir, "Koduma.Cli" + exeSuffix);
```

- [ ] **Step 3: Run the integration test suite to verify no regression on this platform**

Run: `dotnet test tests/Koduma.Integration.Tests/Koduma.Integration.Tests.csproj`
Expected: PASS — on this Linux machine, `RuntimeInformation.RuntimeIdentifier` resolves to the same `linux-x64` (or the exact matching Linux RID for this machine) as before, and `OperatingSystem.IsWindows()` is `false` so no `.exe` suffix is added — behavior identical to before the change.

- [ ] **Step 4: Document the Windows symlink-creation requirement**

Add a one-line comment at the top of the test class body (not a multi-line block, not restating what the code does) in BOTH files that create real symlinks in their fixtures.

In `tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs`, directly above the class declaration:

```csharp
// These tests create real symlinks; on Windows this requires Developer Mode or admin rights.
public class KodumaMcpServerTests : IDisposable
```

In `tests/Koduma.Core.Tests/FileDiscoveryTests.cs`, directly above the class declaration (check the exact current class name/signature first and preserve it, only adding the comment line above it):

```csharp
// These tests create real symlinks; on Windows this requires Developer Mode or admin rights.
public class FileDiscoveryTests : IDisposable
```

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test koduma.sln`
Expected: all tests pass, no behavior change on this platform.

- [ ] **Step 6: Commit**

```bash
git add tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs tests/Koduma.Mcp.Tests/KodumaMcpServerTests.cs tests/Koduma.Core.Tests/FileDiscoveryTests.cs
git commit -m "fix: make integration test publish/run portable across platforms; document Windows symlink test requirement"
```
