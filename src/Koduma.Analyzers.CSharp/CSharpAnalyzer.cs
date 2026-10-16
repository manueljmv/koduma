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
        new Rule("CS-DESIGN-001", "Empty catch block", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-DESIGN-002", "Too many parameters", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-DESIGN-003", "Method too long", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-NAMING-002", "Public member naming", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
        new Rule("CS-SMELL-001", "Leftover TODO/FIXME comment", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
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
            issues.AddRange(EmptyCatchRule.Evaluate(tree, file));
            issues.AddRange(ParameterCountRule.Evaluate(tree, file));
            issues.AddRange(MethodLengthRule.Evaluate(tree, file));
            issues.AddRange(PublicNamingRule.Evaluate(tree, file));
            issues.AddRange(TodoCommentRule.Evaluate(tree, file));

            var (complexityIssues, complexity) = ComplexityRule.Evaluate(tree, file);
            issues.AddRange(complexityIssues);
            totalComplexity += complexity;
        }

        var metrics = new AnalysisMetrics(totalLoc, totalComplexity, DuplicatedLines: 0, FilesAnalyzed: filePaths.Count);
        return Task.FromResult(new LanguageAnalysisResult(issues, metrics, Error: null));
    }
}
