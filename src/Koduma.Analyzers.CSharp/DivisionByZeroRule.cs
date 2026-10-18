using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-007: dividing or taking the remainder of a value by a literal zero, which throws DivideByZeroException at runtime for integer/decimal arithmetic.</summary>
internal static class DivisionByZeroRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!binary.IsKind(SyntaxKind.DivideExpression) && !binary.IsKind(SyntaxKind.ModuloExpression))
                continue;

            if (binary.Right is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal || !IsZero(literal.Token.Value))
                continue;

            var op = binary.IsKind(SyntaxKind.DivideExpression) ? "Dividing" : "Taking the remainder of";
            yield return IssueFactory.At(
                "CS-BUG-007", Severity.Critical, Category.Bug, filePath, binary.GetLocation(),
                $"{op} by a literal zero always throws DivideByZeroException at runtime.");
        }
    }

    // double/float division by zero yields Infinity/NaN rather than throwing, so it is excluded here.
    private static bool IsZero(object? value) => value switch
    {
        int i => i == 0,
        long l => l == 0,
        uint ui => ui == 0,
        ulong ul => ul == 0,
        decimal m => m == 0,
        _ => false,
    };
}
