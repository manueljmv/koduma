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

    public IReadOnlyList<Rule> Rules { get; } = new[]
    {
        new Rule("SEC-SECRET-001", "Hardcoded secret", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-002", "GitHub personal access token", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-003", "Slack token", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-004", "Google API key", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-005", "Stripe live key", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-006", "Private key header", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-007", "JWT", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-008", "Credentials in URL", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SECRET-009", "Azure connection string", Category.Security, Severity.Critical, "security", EngineRuleId: null),
        new Rule("SEC-SQLI-001", "Possible SQL injection", Category.Security, Severity.Critical, "security", EngineRuleId: null),
    };

    private static readonly (string RuleId, Regex Pattern, string Message)[] Patterns =
    {
        ("SEC-SECRET-001", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "Possible hardcoded secret (AWS access key)."),
        ("SEC-SECRET-001", new Regex(@"password\s*=\s*""[^""]{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase), "Possible hardcoded secret (password)."),
        ("SEC-SECRET-002", new Regex("gh[pousr]_[A-Za-z0-9]{36}", RegexOptions.Compiled), "Possible hardcoded secret (GitHub personal access token)."),
        ("SEC-SECRET-003", new Regex("xox[baprs]-[0-9a-zA-Z-]{10,48}", RegexOptions.Compiled), "Possible hardcoded secret (Slack token)."),
        ("SEC-SECRET-004", new Regex(@"AIza[0-9A-Za-z\-_]{35}", RegexOptions.Compiled), "Possible hardcoded secret (Google API key)."),
        ("SEC-SECRET-005", new Regex("sk_live_[0-9a-zA-Z]{24,}", RegexOptions.Compiled), "Possible hardcoded secret (Stripe live key)."),
        ("SEC-SECRET-006", new Regex("-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled), "Possible hardcoded secret (private key)."),
        ("SEC-SECRET-007", new Regex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled), "Possible hardcoded secret (JWT)."),
        ("SEC-SECRET-008", new Regex(@"https?://[^:/\s]+:[^@/\s]+@", RegexOptions.Compiled), "Possible hardcoded credentials in URL."),
        ("SEC-SECRET-009", new Regex("AccountKey=[A-Za-z0-9+/=]{20,}", RegexOptions.Compiled), "Possible hardcoded secret (Azure connection string AccountKey)."),
        ("SEC-SQLI-001", new Regex(
            @"(SELECT\b[^\n]*\bFROM\b|INSERT\s+INTO\b|UPDATE\b[^\n]*\bSET\b|DELETE\s+FROM\b|DROP\s+(TABLE|DATABASE)\b)[^\n]*(?:[""']\s*\+|\$\{|#\{)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "Possible SQL injection."),
    };

    public Task<LanguageAnalysisResult> AnalyzeAsync(string projectPath, IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var issues = new List<IIssue>();
        var customPatterns = config.CustomPatterns
            .Select(c => (c.Id, Pattern: new Regex(c.Pattern), c.Severity, c.Message))
            .ToList();

        foreach (var file in filePaths)
        {
            var lines = File.ReadAllLines(file);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var (ruleId, pattern, message) in Patterns)
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
                        Message: message,
                        Language: "security"));
                }

                foreach (var (id, pattern, severity, message) in customPatterns)
                {
                    var match = pattern.Match(lines[lineIndex]);
                    if (!match.Success)
                        continue;

                    issues.Add(new Issue(
                        RuleId: id,
                        Severity: severity,
                        Category: Category.Security,
                        FilePath: file,
                        Line: lineIndex + 1,
                        Column: match.Index + 1,
                        EndLine: lineIndex + 1,
                        EndColumn: match.Index + match.Length + 1,
                        Message: message,
                        Language: "security"));
                }
            }
        }

        return Task.FromResult(new LanguageAnalysisResult(issues, new AnalysisMetrics(0, 0, 0, filePaths.Count), Error: null));
    }
}
