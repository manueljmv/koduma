using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SMELL-002: public static fields that are neither <c>readonly</c> nor <c>const</c> (global mutable state).</summary>
internal static class PublicMutableStaticRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var fieldDeclaration in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            bool Has(SyntaxKind kind) => fieldDeclaration.Modifiers.Any(m => m.IsKind(kind));

            if (!Has(SyntaxKind.PublicKeyword) || !Has(SyntaxKind.StaticKeyword) || Has(SyntaxKind.ReadOnlyKeyword) || Has(SyntaxKind.ConstKeyword))
                continue;

            foreach (var variable in fieldDeclaration.Declaration.Variables)
            {
                yield return IssueFactory.At(
                    "CS-SMELL-002", Severity.Minor, Category.CodeSmell, filePath, variable.Identifier.GetLocation(),
                    $"Public static field '{variable.Identifier.ValueText}' is mutable global state. Make it readonly or expose it through a property.");
            }
        }
    }
}
