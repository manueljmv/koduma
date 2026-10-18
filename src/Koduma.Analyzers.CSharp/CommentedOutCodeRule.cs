using System.Text.RegularExpressions;
using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SMELL-003: a line comment whose text reads like an executable C# statement rather than prose, suggesting dead code left commented out instead of deleted.</summary>
internal static class CommentedOutCodeRule
{
    private static readonly Regex CodeLikePattern = new(
        @"^(?:(?:var|int|string|bool|double|float|long|public|private|protected|internal|static|void|if|for|foreach|while|return|throw|new)\b.*;|[A-Za-z_][\w.\[\]]*\s*=[^=].*;)$",
        RegexOptions.Compiled);

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
                continue;

            var text = trivia.ToString().TrimStart('/').Trim();
            if (!CodeLikePattern.IsMatch(text))
                continue;

            yield return IssueFactory.At(
                "CS-SMELL-003", Severity.Minor, Category.CodeSmell, filePath, trivia.GetLocation(),
                "This comment looks like commented-out code; delete it instead of leaving dead code in the file.");
        }
    }
}
