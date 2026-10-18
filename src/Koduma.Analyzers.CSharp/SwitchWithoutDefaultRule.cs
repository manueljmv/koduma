using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-DESIGN-007: a 'switch' statement with no 'default' case, so an unanticipated value silently falls through instead of being handled.</summary>
internal static class SwitchWithoutDefaultRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var switchStatement in root.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            var hasDefault = switchStatement.Sections
                .SelectMany(s => s.Labels)
                .Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel));

            if (hasDefault)
                continue;

            yield return IssueFactory.At(
                "CS-DESIGN-007", Severity.Minor, Category.CodeSmell, filePath, switchStatement.SwitchKeyword.GetLocation(),
                "This 'switch' statement has no 'default' case; an unanticipated value will silently fall through.");
        }
    }
}
