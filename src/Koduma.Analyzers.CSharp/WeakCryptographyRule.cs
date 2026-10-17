using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SEC-001: broken or weak cryptographic algorithms (MD5, SHA1, DES, 3DES, RC2).</summary>
internal static class WeakCryptographyRule
{
    private static readonly HashSet<string> WeakImplementations = new(StringComparer.Ordinal)
    {
        "MD5CryptoServiceProvider", "MD5Cng", "SHA1CryptoServiceProvider", "SHA1Managed", "SHA1Cng",
        "DESCryptoServiceProvider", "TripleDESCryptoServiceProvider", "TripleDESCng", "RC2CryptoServiceProvider",
    };

    private static readonly HashSet<string> WeakFactories = new(StringComparer.Ordinal)
    {
        "MD5", "SHA1", "DES", "TripleDES", "RC2",
    };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var node in root.DescendantNodes())
        {
            string? algorithm = null;

            if (node is ObjectCreationExpressionSyntax creation)
            {
                var typeName = SimpleName(creation.Type);
                if (typeName is not null && WeakImplementations.Contains(typeName))
                    algorithm = typeName;
            }
            else if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access })
            {
                var method = access.Name.Identifier.ValueText;
                var receiver = access.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null,
                };

                if ((method is "Create" or "HashData") && receiver is not null && WeakFactories.Contains(receiver))
                    algorithm = receiver;
            }

            if (algorithm is null)
                continue;

            yield return IssueFactory.At(
                "CS-SEC-001", Severity.Major, Category.Security, filePath, node.GetLocation(),
                $"'{algorithm}' is a weak cryptographic algorithm. Use SHA-256 or stronger for hashing and AES for encryption.");
        }
    }

    private static string? SimpleName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        _ => null,
    };
}
