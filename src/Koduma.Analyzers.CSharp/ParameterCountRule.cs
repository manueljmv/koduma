using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class ParameterCountRule
{
    private const int MaxParameters = 7;

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var count = method.ParameterList.Parameters.Count;
            if (count <= MaxParameters)
                continue;

            var lineSpan = method.Identifier.GetLocation().GetLineSpan();
            yield return new Issue(
                RuleId: "CS-DESIGN-002",
                Severity: Severity.Major,
                Category: Category.CodeSmell,
                FilePath: filePath,
                Line: lineSpan.StartLinePosition.Line + 1,
                Column: lineSpan.StartLinePosition.Character + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                EndColumn: lineSpan.EndLinePosition.Character + 1,
                Message: $"Method '{method.Identifier.ValueText}' has {count} parameters, exceeding {MaxParameters}.",
                Language: "csharp");
        }
    }
}
