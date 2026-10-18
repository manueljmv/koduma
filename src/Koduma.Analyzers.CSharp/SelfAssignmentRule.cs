using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-008: a variable, field or property assigned to itself, almost always a copy/paste mistake for an assignment that was meant to involve something else.</summary>
internal static class SelfAssignmentRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                continue;

            // `new Point { X = X }`: the right-hand X commonly binds to a differently-scoped parameter/local, not the member being set.
            if (assignment.Parent is InitializerExpressionSyntax)
                continue;

            if (assignment.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                continue;

            if (!SyntaxFactory.AreEquivalent(assignment.Left, assignment.Right))
                continue;

            yield return IssueFactory.At(
                "CS-BUG-008", Severity.Major, Category.Bug, filePath, assignment.GetLocation(),
                $"'{assignment.Left}' is assigned to itself; this is likely a mistake.");
        }
    }
}
