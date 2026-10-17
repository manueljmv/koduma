using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-DESIGN-005: type declarations longer than 500 lines.</summary>
internal static class ClassSizeRule
{
    private const int MaxLines = 500;

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var span = type.GetLocation().GetLineSpan();
            var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            if (lines <= MaxLines)
                continue;

            yield return IssueFactory.At(
                "CS-DESIGN-005", Severity.Major, Category.CodeSmell, filePath, type.Identifier.GetLocation(),
                $"Type '{type.Identifier.ValueText}' is {lines} lines long, exceeding {MaxLines}. Consider splitting its responsibilities.");
        }
    }
}
