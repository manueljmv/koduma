using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class MethodLengthRule
{
    private const int MaxLines = 60;

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body is null)
                continue;

            var bodyLineSpan = method.Body.GetLocation().GetLineSpan();
            var lineCount = bodyLineSpan.EndLinePosition.Line - bodyLineSpan.StartLinePosition.Line + 1;
            if (lineCount <= MaxLines)
                continue;

            var lineSpan = method.Identifier.GetLocation().GetLineSpan();
            yield return new Issue(
                RuleId: "CS-DESIGN-003",
                Severity: Severity.Major,
                Category: Category.CodeSmell,
                FilePath: filePath,
                Line: lineSpan.StartLinePosition.Line + 1,
                Column: lineSpan.StartLinePosition.Character + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                EndColumn: lineSpan.EndLinePosition.Character + 1,
                Message: $"Method '{method.Identifier.ValueText}' is {lineCount} lines long, exceeding {MaxLines}.",
                Language: "csharp");
        }
    }
}
