using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class ComplexityRule
{
    private const int Threshold = 10;

    public static (IEnumerable<IIssue> Issues, int TotalComplexity) Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();
        var issues = new List<IIssue>();
        var total = 0;

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var complexity = 1 + method.DescendantNodes().Count(n =>
                n is IfStatementSyntax or ForStatementSyntax or WhileStatementSyntax or
                     CaseSwitchLabelSyntax or ConditionalExpressionSyntax or CatchClauseSyntax);

            total += complexity;

            if (complexity > Threshold)
            {
                var lineSpan = method.Identifier.GetLocation().GetLineSpan();
                issues.Add(new Issue(
                    RuleId: "CS-COMPLEXITY-001",
                    Severity: Severity.Major,
                    Category: Category.CodeSmell,
                    FilePath: filePath,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Column: lineSpan.StartLinePosition.Character + 1,
                    EndLine: lineSpan.EndLinePosition.Line + 1,
                    EndColumn: lineSpan.EndLinePosition.Character + 1,
                    Message: $"Method '{method.Identifier.ValueText}' has cyclomatic complexity {complexity}, exceeding {Threshold}.",
                    Language: "csharp"));
            }
        }

        return (issues, total);
    }
}
