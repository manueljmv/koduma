using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-DESIGN-006: control-flow statements nested more than 4 levels deep inside a method. <c>else if</c> chains do not add depth.</summary>
internal static class NestingDepthRule
{
    private const int MaxDepth = 4;

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (method.Body is null)
                continue;

            SyntaxNode? deepest = null;
            var deepestLevel = 0;

            foreach (var node in method.Body.DescendantNodes().Where(CountsAsNesting))
            {
                var level = node.AncestorsAndSelf().TakeWhile(a => a != method.Body).Count(CountsAsNesting);
                if (level > deepestLevel)
                {
                    deepestLevel = level;
                    deepest = node;
                }
            }

            if (deepest is null || deepestLevel <= MaxDepth)
                continue;

            yield return IssueFactory.At(
                "CS-DESIGN-006", Severity.Major, Category.CodeSmell, filePath, deepest.GetFirstToken().GetLocation(),
                $"Control flow is nested {deepestLevel} levels deep (max {MaxDepth}). Extract methods or use early returns.");
        }
    }

    private static bool CountsAsNesting(SyntaxNode node) => node switch
    {
        IfStatementSyntax ifStatement => ifStatement.Parent is not ElseClauseSyntax,
        ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            or SwitchStatementSyntax or TryStatementSyntax => true,
        _ => false,
    };
}
