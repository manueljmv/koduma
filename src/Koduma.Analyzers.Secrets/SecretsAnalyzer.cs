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
    };

    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("AWS access key", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("Hardcoded password", new Regex(@"password\s*=\s*""[^""]{8,}""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    public Task<LanguageAnalysisResult> AnalyzeAsync(string projectPath, IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
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
