using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class PublicNamingRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!classDecl.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            if (TryCreateIssue(classDecl.Identifier, "class", filePath, out var issue))
                yield return issue;
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
                continue;

            if (TryCreateIssue(method.Identifier, "method", filePath, out var issue))
                yield return issue;
        }
    }

    private static bool TryCreateIssue(SyntaxToken identifier, string kind, string filePath, out Issue issue)
    {
        var name = identifier.ValueText;
        if (name.Length > 0 && char.IsUpper(name[0]))
        {
            issue = null!;
            return false;
        }

        var lineSpan = identifier.GetLocation().GetLineSpan();
        issue = new Issue(
            RuleId: "CS-NAMING-002",
            Severity: Severity.Minor,
            Category: Category.CodeSmell,
            FilePath: filePath,
            Line: lineSpan.StartLinePosition.Line + 1,
            Column: lineSpan.StartLinePosition.Character + 1,
            EndLine: lineSpan.EndLinePosition.Line + 1,
            EndColumn: lineSpan.EndLinePosition.Character + 1,
            Message: $"Public {kind} '{name}' should be PascalCase.",
            Language: "csharp");
        return true;
    }
}
