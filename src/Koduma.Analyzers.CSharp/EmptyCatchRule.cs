using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class EmptyCatchRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (catchClause.Block.Statements.Count != 0)
                continue;

            var lineSpan = catchClause.CatchKeyword.GetLocation().GetLineSpan();
            yield return new Issue(
                RuleId: "CS-DESIGN-001",
                Severity: Severity.Major,
                Category: Category.CodeSmell,
                FilePath: filePath,
                Line: lineSpan.StartLinePosition.Line + 1,
                Column: lineSpan.StartLinePosition.Character + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                EndColumn: lineSpan.EndLinePosition.Character + 1,
                Message: "Catch block is empty and silently swallows the exception.",
                Language: "csharp");
        }
    }
}
