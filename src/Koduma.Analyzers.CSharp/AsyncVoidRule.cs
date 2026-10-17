using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-001: <c>async void</c> methods that are not event handlers.</summary>
internal static class AsyncVoidRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
                continue;

            if (method.ReturnType is not PredefinedTypeSyntax returnType || !returnType.Keyword.IsKind(SyntaxKind.VoidKeyword))
                continue;

            if (LooksLikeEventHandler(method))
                continue;

            yield return IssueFactory.At(
                "CS-BUG-001", Severity.Major, Category.Bug, filePath, method.Identifier.GetLocation(),
                $"Async method '{method.Identifier.ValueText}' returns void, so its exceptions cannot be observed or awaited. Return Task instead.");
        }
    }

    // Event handlers are the one legitimate use of async void: (object sender, SomethingEventArgs e).
    private static bool LooksLikeEventHandler(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2)
            return false;

        var secondType = parameters[1].Type?.ToString() ?? string.Empty;
        return secondType.EndsWith("EventArgs", StringComparison.Ordinal);
    }
}
