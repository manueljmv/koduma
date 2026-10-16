# Expanded rule catalog + config-driven security patterns

## Why

Today's rule catalog is minimal: 2 C# rules (naming, complexity) and 2 security
regexes (AWS key, hardcoded password, SQLi). Adding a security pattern means
editing `SecretsAnalyzer.Patterns` in code and recompiling/redeploying the AOT
binary. The user wants (a) more checks and (b) a way to add security patterns
without a recompile.

## Goals

1. `koduma.json` gains a `customPatterns` array. Each entry is merged at
   analysis time with the built-in security pattern catalog — no recompile
   needed to add an org-specific secret format.
2. Expand the built-in security pattern catalog (all regex, line-based, same
   mechanism as today). Each gets its **own** `Rule` ID (unlike the existing
   `SEC-SECRET-001`, which the AWS-key and password patterns already share) —
   so each can be individually disabled/overridden via `koduma.json` and
   shows up as its own line in `koduma rules`. The two existing patterns keep
   `SEC-SECRET-001` unchanged (renaming them would silently break anyone
   already referencing that ID in their `koduma.json`):
   - `SEC-SECRET-002` GitHub PAT: `gh[pousr]_[A-Za-z0-9]{36}`
   - `SEC-SECRET-003` Slack token: `xox[baprs]-[0-9a-zA-Z-]{10,48}`
   - `SEC-SECRET-004` Google API key: `AIza[0-9A-Za-z\-_]{35}`
   - `SEC-SECRET-005` Stripe live key: `sk_live_[0-9a-zA-Z]{24,}`
   - `SEC-SECRET-006` Private key header: `-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----`
   - `SEC-SECRET-007` JWT: `eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}`
   - `SEC-SECRET-008` Credentials in URL: `https?://[^:/\s]+:[^@/\s]+@`
   - `SEC-SECRET-009` Azure connection string: `AccountKey=[A-Za-z0-9+/=]{20,}`
3. Add 5 new C# semantic rules (Roslyn AST, same shape as `NamingRule`/`ComplexityRule`):
   - `CS-DESIGN-001` — empty `catch` block (swallowed exception)
   - `CS-DESIGN-002` — method with more than 7 parameters
   - `CS-DESIGN-003` — method longer than 60 lines
   - `CS-NAMING-002` — public class/method not PascalCase
   - `CS-SMELL-001` — `TODO`/`FIXME` comment

## Non-goals

- No generic AST rule DSL. C# semantic rules stay hand-written Roslyn code;
  only the regex/line-based security rules are config-extensible.
- No per-rule threshold config (parameter count, method length stay as code
  constants, matching the existing `ComplexityRule.Threshold` convention).
- No entropy-based/heuristic secret detection — regex only, same as today.
- No change to the ESLint path (already fully config-driven via the repo's
  own `eslint.config.js`).

## Constraints

- Must stay Native AOT / `InvariantGlobalization` compatible: JSON parsing of
  `customPatterns` goes through `KodumaConfigLoader`'s existing
  `JsonNode`/source-generated pattern, no reflection-based deserialization.
- Existing `rules: { "RULE-ID": "off" | {severity} }` override mechanism in
  `koduma.json` must keep working unchanged for both old and new rule IDs
  (it's applied generically by `AnalysisOrchestrator` on `issue.RuleId`,
  nothing to change there).

## Design decisions

- **Schema** for a `customPatterns` entry:
  ```json
  { "id": "SEC-CUSTOM-001", "name": "Internal token", "pattern": "ITOK-[0-9A-F]{32}",
    "severity": "Critical", "message": "Possible internal token leak." }
  ```
  `category` is not configurable — custom patterns are always `Security`.
  `id` and `pattern` are required (invalid regex → `KodumaConfigException` at
  load time). `severity` is optional, defaults to `Critical`. `message` is
  optional, defaults to `$"Possible {name} leak."`. A duplicate `id` within
  `customPatterns` itself, or one colliding with a built-in rule ID, is a
  `KodumaConfigException` (fail-fast, same as other config errors) — not
  silently overwritten.
- **Where it's parsed and validated**: `KodumaConfigLoader` gains parsing for
  `customPatterns` into a new `CustomPatternRule` record, stored on
  `KodumaConfig`. `Koduma.Core` has no reference to the analyzer projects, so
  it can't know the built-in security IDs by itself; instead
  `LoadOrDefault` takes the live set of known rule IDs as a parameter
  (`IReadOnlyCollection<string>`), which both `AnalyzeCommand.Run` (CLI) and
  `KodumaMcpServer.HandleAnalyzeProject` (MCP) already have on hand via
  `analyzers.SelectMany(a => a.Rules).Select(r => r.Id)` — no new dependency
  edge, and both call sites already catch `KodumaConfigException` around
  config loading, so the fail-fast/exit-2 behavior is unchanged. `customPatterns`
  is parsed only from `koduma.json`; the MCP `rules_config` call-time override
  stays severity/enabled-only, unchanged.
  `SecretsAnalyzer.AnalyzeAsync` already receives `KodumaConfig` — it merges
  `config.CustomPatterns` with its static `Patterns` array at scan time. No
  change to `ILanguageAnalyzer`.
- **Known limitation**: `koduma rules` / `list_rules` only list each
  analyzer's static `Rules` catalog (as today, where they also don't reflect
  `koduma.json` severity overrides). Custom patterns apply during `analyze`
  but won't appear in the `rules` listing — consistent with existing
  behavior, not fixed here.
- New C# rules follow the existing file-per-rule convention
  (`EmptyCatchRule.cs`, etc.), each added to `CSharpAnalyzer.Rules` and called
  from `CSharpAnalyzer.AnalyzeAsync`.

## What's left to the builder

- Exact wording of issue messages for the 5 new C# rules and 8 new patterns.
- Severity/Category assignment per new C# rule (defaults: CodeSmell/Minor for
  naming and TODO, CodeSmell/Major for empty catch, params, method length —
  matching existing severity conventions in the file).
- Test coverage per rule (unit tests in `tests/Koduma.Analyzers.CSharp.Tests`
  and `tests/Koduma.Analyzers.Secrets.Tests`, plus a config-parsing test for
  `customPatterns` in `tests/Koduma.Core.Tests`).
