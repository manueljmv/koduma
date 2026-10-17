using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-005: both sides of a binary operator are the same expression (almost always a copy/paste bug).</summary>
internal static class IdenticalOperandsRule
{
    private static readonly SyntaxKind[] CheckedOperators =
    {
        SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression,
        SyntaxKind.LessThanExpression, SyntaxKind.LessThanOrEqualExpression,
        SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanOrEqualExpression,
        SyntaxKind.LogicalAndExpression, SyntaxKind.LogicalOrExpression,
        SyntaxKind.SubtractExpression, SyntaxKind.DivideExpression, SyntaxKind.ModuloExpression,
        SyntaxKind.ExclusiveOrExpression, SyntaxKind.BitwiseAndExpression, SyntaxKind.BitwiseOrExpression,
        SyntaxKind.CoalesceExpression,
    };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!CheckedOperators.Any(kind => binary.IsKind(kind)))
                continue;

            if (binary.Left is LiteralExpressionSyntax || HasSideEffects(binary.Left))
                continue;

            if (!SyntaxFactory.AreEquivalent(binary.Left, binary.Right))
                continue;

            yield return IssueFactory.At(
                "CS-BUG-005", Severity.Major, Category.Bug, filePath, binary.OperatorToken.GetLocation(),
                $"Both operands of '{binary.OperatorToken.ValueText}' are identical ('{binary.Left}'); this is likely a copy/paste error.");
        }
    }

    private static bool HasSideEffects(SyntaxNode expression) =>
        expression.DescendantNodesAndSelf().Any(n =>
            n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                or AssignmentExpressionSyntax or AwaitExpressionSyntax
            || n.IsKind(SyntaxKind.PreIncrementExpression) || n.IsKind(SyntaxKind.PreDecrementExpression)
            || n.IsKind(SyntaxKind.PostIncrementExpression) || n.IsKind(SyntaxKind.PostDecrementExpression));
}
