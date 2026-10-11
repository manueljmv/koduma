using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

internal static class NamingRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.PrivateKeyword))
                continue;

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                if (name.StartsWith('_'))
                    continue;

                var lineSpan = variable.GetLocation().GetLineSpan();
                yield return new Issue(
                    RuleId: "CS-NAMING-001",
                    Severity: Severity.Minor,
                    Category: Category.CodeSmell,
                    FilePath: filePath,
                    Line: lineSpan.StartLinePosition.Line + 1,
                    Column: lineSpan.StartLinePosition.Character + 1,
                    EndLine: lineSpan.EndLinePosition.Line + 1,
                    EndColumn: lineSpan.EndLinePosition.Character + 1,
                    Message: $"Private field '{name}' should be named with a leading underscore.",
                    Language: "csharp");
            }
        }
    }
}
