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

    public IReadOnlyList<Rule> Rules { get; } = new[]
    {
        new Rule("CS-NAMING-001", "Private field naming", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
        new Rule("CS-COMPLEXITY-001", "Cyclomatic complexity", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
    };

    public Task<LanguageAnalysisResult> AnalyzeAsync(string projectPath, IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
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
