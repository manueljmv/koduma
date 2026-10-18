using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-009: a type overrides only one of Equals(object)/GetHashCode(). Leaving the other at its default breaks hashing/equality contracts (e.g. equal instances landing in different hash-set buckets).</summary>
internal static class EqualsHashCodeRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (type is InterfaceDeclarationSyntax)
                continue;

            var methods = type.Members.OfType<MethodDeclarationSyntax>().ToList();
            var hasEquals = methods.Any(IsEqualsOverride);
            var hasGetHashCode = methods.Any(IsGetHashCodeOverride);

            if (hasEquals && !hasGetHashCode)
                yield return IssueFactory.At(
                    "CS-BUG-009", Severity.Major, Category.Bug, filePath, type.Identifier.GetLocation(),
                    $"'{type.Identifier.ValueText}' overrides Equals(object) but not GetHashCode().");
            else if (hasGetHashCode && !hasEquals)
                yield return IssueFactory.At(
                    "CS-BUG-009", Severity.Major, Category.Bug, filePath, type.Identifier.GetLocation(),
                    $"'{type.Identifier.ValueText}' overrides GetHashCode() but not Equals(object).");
        }
    }

    private static bool IsEqualsOverride(MethodDeclarationSyntax m) =>
        m.Identifier.ValueText == "Equals" && m.Modifiers.Any(SyntaxKind.OverrideKeyword) && m.ParameterList.Parameters.Count == 1;

    private static bool IsGetHashCodeOverride(MethodDeclarationSyntax m) =>
        m.Identifier.ValueText == "GetHashCode" && m.Modifiers.Any(SyntaxKind.OverrideKeyword) && m.ParameterList.Parameters.Count == 0;
}
