using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Koduma.Analyzers.CSharp;

internal static class TodoCommentRule
{
    private static readonly SyntaxKind[] CommentKinds =
    {
        SyntaxKind.SingleLineCommentTrivia,
        SyntaxKind.MultiLineCommentTrivia,
        SyntaxKind.SingleLineDocumentationCommentTrivia,
    };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var trivia in root.DescendantTrivia())
        {
            if (!CommentKinds.Contains(trivia.Kind()))
                continue;

            var text = trivia.ToString();
            if (!text.Contains("TODO", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("FIXME", StringComparison.OrdinalIgnoreCase))
                continue;

            var lineSpan = trivia.GetLocation().GetLineSpan();
            yield return new Issue(
                RuleId: "CS-SMELL-001",
                Severity: Severity.Minor,
                Category: Category.CodeSmell,
                FilePath: filePath,
                Line: lineSpan.StartLinePosition.Line + 1,
                Column: lineSpan.StartLinePosition.Character + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                EndColumn: lineSpan.EndLinePosition.Character + 1,
                Message: "Unresolved TODO/FIXME comment left in code.",
                Language: "csharp");
        }
    }
}
