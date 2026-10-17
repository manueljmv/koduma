using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-004: locking on <c>this</c>, a <c>typeof</c> expression or a string literal (objects other code can also lock on).</summary>
internal static class LockOnSharedObjectRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var lockStatement in root.DescendantNodes().OfType<LockStatementSyntax>())
        {
            var target = lockStatement.Expression switch
            {
                ThisExpressionSyntax => "this",
                TypeOfExpressionSyntax => "a typeof expression",
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => "a string literal",
                _ => null,
            };

            if (target is null)
                continue;

            yield return IssueFactory.At(
                "CS-BUG-004", Severity.Major, Category.Bug, filePath, lockStatement.LockKeyword.GetLocation(),
                $"Locking on {target} can deadlock because other code may lock on the same object. Lock on a private readonly object instead.");
        }
    }
}
