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
        new Rule("CS-BUG-001", "Async void method", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-002", "Blocking call on a task inside async method", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-003", "Rethrowing caught exception with 'throw ex'", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-004", "Lock on this, typeof or string literal", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-005", "Identical operands on both sides of operator", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-006", "Empty statement as control-flow body", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-DESIGN-004", "Catch of general Exception without rethrow", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
        new Rule("CS-DESIGN-005", "Type too long", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-DESIGN-006", "Control flow nested too deeply", Category.CodeSmell, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-SMELL-002", "Public mutable static field", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
        new Rule("CS-SEC-001", "Weak cryptographic algorithm", Category.Security, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-SEC-002", "Insecure deserialization", Category.Security, Severity.Critical, "csharp", EngineRuleId: null),
        new Rule("CS-SEC-003", "TLS certificate validation disabled", Category.Security, Severity.Critical, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-007", "Division or modulo by literal zero", Category.Bug, Severity.Critical, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-008", "Self-assignment", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-009", "Equals/GetHashCode override pair incomplete", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-BUG-010", "Disposable resource not declared with 'using'", Category.Bug, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-DESIGN-007", "Switch statement without default case", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
        new Rule("CS-SMELL-003", "Commented-out code", Category.CodeSmell, Severity.Minor, "csharp", EngineRuleId: null),
        new Rule("CS-SEC-004", "Hardcoded insecure http:// URL", Category.Security, Severity.Major, "csharp", EngineRuleId: null),
        new Rule("CS-SEC-005", "Command built via concatenation/interpolation", Category.Security, Severity.Critical, "csharp", EngineRuleId: null),
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
            issues.AddRange(AsyncVoidRule.Evaluate(tree, file));
            issues.AddRange(SyncOverAsyncRule.Evaluate(tree, file));
            issues.AddRange(RethrowExceptionRule.Evaluate(tree, file));
            issues.AddRange(LockOnSharedObjectRule.Evaluate(tree, file));
            issues.AddRange(IdenticalOperandsRule.Evaluate(tree, file));
            issues.AddRange(EmptyStatementBodyRule.Evaluate(tree, file));
            issues.AddRange(GenericCatchRule.Evaluate(tree, file));
            issues.AddRange(ClassSizeRule.Evaluate(tree, file));
            issues.AddRange(NestingDepthRule.Evaluate(tree, file));
            issues.AddRange(PublicMutableStaticRule.Evaluate(tree, file));
            issues.AddRange(WeakCryptographyRule.Evaluate(tree, file));
            issues.AddRange(InsecureDeserializationRule.Evaluate(tree, file));
            issues.AddRange(CertificateValidationRule.Evaluate(tree, file));
            issues.AddRange(DivisionByZeroRule.Evaluate(tree, file));
            issues.AddRange(SelfAssignmentRule.Evaluate(tree, file));
            issues.AddRange(EqualsHashCodeRule.Evaluate(tree, file));
            issues.AddRange(DisposableNotUsingRule.Evaluate(tree, file));
            issues.AddRange(SwitchWithoutDefaultRule.Evaluate(tree, file));
            issues.AddRange(CommentedOutCodeRule.Evaluate(tree, file));
            issues.AddRange(InsecureHttpUrlRule.Evaluate(tree, file));
            issues.AddRange(CommandInjectionRule.Evaluate(tree, file));

            var (complexityIssues, complexity) = ComplexityRule.Evaluate(tree, file);
            issues.AddRange(complexityIssues);
            totalComplexity += complexity;
        }

        var metrics = new AnalysisMetrics(totalLoc, totalComplexity, DuplicatedLines: 0, FilesAnalyzed: filePaths.Count);
        return Task.FromResult(new LanguageAnalysisResult(issues, metrics, Error: null));
    }
}
