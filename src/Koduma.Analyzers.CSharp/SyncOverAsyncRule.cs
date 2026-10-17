using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-002: blocking on a task (<c>.Wait()</c>, <c>.Result</c>, <c>.GetAwaiter().GetResult()</c>) inside an async method.</summary>
internal static class SyncOverAsyncRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
                continue;

            foreach (var node in method.DescendantNodes())
            {
                var blocking = DescribeBlockingCall(node);
                if (blocking is null)
                    continue;

                yield return IssueFactory.At(
                    "CS-BUG-002", Severity.Major, Category.Bug, filePath, node.GetLocation(),
                    $"'{blocking}' blocks a thread inside async method '{method.Identifier.ValueText}' and can deadlock. Use 'await' instead.");
            }
        }
    }

    private static string? DescribeBlockingCall(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation)
        {
            var name = access.Name.Identifier.ValueText;

            if (name == "Wait")
                return ".Wait()";

            if (name == "GetResult"
                && invocation.ArgumentList.Arguments.Count == 0
                && access.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner }
                && inner.Name.Identifier.ValueText == "GetAwaiter")
                return ".GetAwaiter().GetResult()";

            return null;
        }

        // Property access is only flagged when the receiver clearly looks like a task, to avoid hitting unrelated '.Result' members.
        if (node is MemberAccessExpressionSyntax property && property.Name.Identifier.ValueText == "Result")
        {
            if (property.Expression is InvocationExpressionSyntax)
                return ".Result";

            if (property.Expression is IdentifierNameSyntax identifier
                && identifier.Identifier.ValueText.Contains("task", StringComparison.OrdinalIgnoreCase))
                return ".Result";
        }

        return null;
    }
}
