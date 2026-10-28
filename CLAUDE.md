# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Koduma is a multi-language code quality engine (SonarQube-like), written in .NET, distributed as a single Native AOT binary with two faces:
- **CLI**: `koduma analyze ./repo`, `koduma rules`, for interactive/CI use.
- **MCP server**: `koduma mcp-serve --root <path>` (stdio), so agents can invoke analysis as a tool.

Both share the same in-process `Koduma.Core` — no subprocess boundary between CLI and MCP.

v1 scope is analysis-only: no persistence, no history between runs, no web service/UI. Supported languages: C# (native Roslyn rules, plus the .NET analyzers via a `dotnet build` subprocess) and JS/TS (via external ESLint subprocess). The plugin architecture supports more languages; none beyond these two are implemented.

## Commands

```bash
dotnet build                                    # build whole solution
dotnet test                                      # run all tests
dotnet test tests/Koduma.Core.Tests              # run one test project
dotnet test --filter "FullyQualifiedName~AnalysisOrchestratorTests"  # run matching tests
dotnet run --project src/Koduma.Cli -- analyze ./some-repo --format table
dotnet run --project src/Koduma.Cli -- rules
dotnet run --project src/Koduma.Cli -- mcp-serve --root ./some-repo
```

Target framework is `net10.0`. `Koduma.Cli` publishes with `PublishAot=true` and `InvariantGlobalization=true`; the integration test (`tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs`) does a real `dotnet publish` of the AOT binary, so it is slow and requires the native build toolchain (Visual Studio "Desktop development with C++" on Windows, Xcode CLT on macOS).

## Architecture

```
src/
├── Koduma.Core/               # Domain + orchestration. No external deps, knows nothing about Roslyn or processes.
├── Koduma.Analyzers.CSharp/   # Roslyn-based in-proc plugin
├── Koduma.Analyzers.External/ # Generic plugin: shells out to external tools (ESLint; `dotnet build` for the .NET analyzers) and normalizes their output
├── Koduma.Analyzers.Secrets/  # Hardcoded-secret detection (SAST)
├── Koduma.Reporting/          # Table / Json / Html / Sarif generators
├── Koduma.Cli/                # Entry point (Program.cs dispatches analyze / rules / mcp-serve)
└── Koduma.Mcp/                # MCP tool surface over Koduma.Core, embedded in the same binary
```

- `Koduma.Core` defines `ILanguageAnalyzer.AnalyzeAsync(files, config) -> IReadOnlyList<IIssue>`, `IIssue`, `AnalysisResult`/`AnalysisMetrics`, discovery (`FileDiscovery`), and `AnalysisOrchestrator` (groups files by language, dispatches to each plugin in parallel, aggregates). Adding a language = implementing `ILanguageAnalyzer` and registering it in `Program.cs`'s `analyzers` array — the orchestrator needs no changes.
- Flow: **Discovery** (respects `.gitignore` + optional `.kodumaignore`) → **Dispatch** by language → **Normalization** to `IIssue` per plugin → **Aggregation** into one `AnalysisResult` → **Reporting** (one or more formats at once).
- **Partial degradation**: if one language's analyzer throws (e.g. ESLint missing), that language is recorded as `AnalysisError` in the result; the run continues for other languages. Config errors (bad `koduma.json`) abort before analysis starts, exit code `2`. Exit codes: `0` clean, `1` has Blocker/Critical issues, `2` execution/config error.
- Rule config lives in optional `koduma.json` at repo root (`rules: { "RULE-ID": "off" | { "severity": "..." } }`, `exclude: [globs]`). Missing file → sensible per-language defaults. `KODUMA_CONFIG` may point to a global `koduma.json` that the project's file is layered on (project wins per rule/pattern id, excludes are concatenated); it is deliberately env-only, never an implicit home/parent file, so hidden files cannot change results.
- MCP tools (`Koduma.Mcp/KodumaMcpServer.cs`): `analyze_project`, `list_rules`, `get_capabilities` — each call is self-contained, no state between calls, matching the no-history design.

## Security-sensitive code — do not casually modify

- **`KodumaMcpServer.Canonicalize`** (path/symlink resolution restricting MCP access to `--root`) has been through 4 rounds of adversarial review. Do not restructure the resolution loop itself. Portability fixes to its helpers (`SplitSegments`, `GetRoot`, `IsWithinRoot`) are fine and already applied: segment splitting handles both separators, and `IsWithinRoot` uses `OrdinalIgnoreCase` on Windows / `Ordinal` on Linux.
- **`ExternalProcessRunner`** (invokes ESLint) deliberately rejects any argument containing cmd.exe special characters (`& | ^ % < > ( ) " ! \r \n \0`) before routing through `cmd.exe` on Windows for `.cmd`/`.bat` shims — this is a defense against "BatBadBut"-style shell injection, not an oversight. It also resolves `cmd.exe` via `ComSpec` (never the bare name) and checks the composed command-line length against cmd.exe's ~8191-char limit before running, turning a potential silent truncation/failure into a reported `AnalysisError`. See `docs/superpowers/specs/2026-10-12-koduma-windows-compat-design.md` for the full threat-model reasoning (including explicitly accepted residual risks) before changing this file.
- **`DotnetBuildAnalyzer`** runs `dotnet build` on the analyzed solution/projects and parses the SARIF error log (`NET-*` rule ids, via `DotnetSarifMapper`). It injects its `ErrorLog` setting through a temp `CustomBeforeMicrosoftCommonTargets` file rather than touching the repo, and drops findings already covered by a native rule (`NativeEquivalents`). Like ESLint it executes the analyzed repo's own build logic; this is accepted behavior. `KODUMA_SKIP_DOTNET_BUILD=1` disables it. The SARIF/MSBuild interplay was written without a .NET SDK available to run it, so verify it against a real solution before relying on it.
- Running `koduma analyze` on an untrusted repo already implies executing that repo's `eslint.config.js` and its plugins (ESLint analyzer runs without `--no-config-lookup`) — this is accepted, pre-existing, cross-platform behavior, not something to silently harden.

## Design docs

Full specs and plans live under `docs/superpowers/specs/` and `docs/superpowers/plans/` — read the relevant one before large changes to orchestration, the MCP surface, or Windows/cross-platform handling rather than re-deriving intent from code alone.
