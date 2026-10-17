using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-006: a stray semicolon used as the body of <c>if</c>, <c>else</c> or <c>foreach</c> (e.g. <c>if (x);</c>).</summary>
internal static class EmptyStatementBodyRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var empty in root.DescendantNodes().OfType<EmptyStatementSyntax>())
        {
            var owner = empty.Parent switch
            {
                IfStatementSyntax => "if",
                ElseClauseSyntax => "else",
                ForEachStatementSyntax => "foreach",
                _ => null,
            };

            if (owner is null)
                continue;

            yield return IssueFactory.At(
                "CS-BUG-006", Severity.Major, Category.Bug, filePath, empty.SemicolonToken.GetLocation(),
                $"The body of this '{owner}' is an empty statement (stray ';'), so it does nothing.");
        }
    }
}
