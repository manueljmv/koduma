using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-DESIGN-004: catching the general <c>Exception</c> (or using a bare <c>catch</c>) without rethrowing. Empty blocks are CS-DESIGN-001.</summary>
internal static class GenericCatchRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (catchClause.Block.Statements.Count == 0 || catchClause.Filter is not null)
                continue;

            if (!IsGeneral(catchClause.Declaration))
                continue;

            var rethrows = catchClause.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax);
            if (rethrows)
                continue;

            yield return IssueFactory.At(
                "CS-DESIGN-004", Severity.Minor, Category.CodeSmell, filePath, catchClause.CatchKeyword.GetLocation(),
                "Catching the general Exception without rethrowing can hide unexpected errors. Catch specific exception types.");
        }
    }

    private static bool IsGeneral(CatchDeclarationSyntax? declaration)
    {
        if (declaration is null)
            return true;

        var typeName = declaration.Type.ToString();
        return typeName is "Exception" or "System.Exception";
    }
}
