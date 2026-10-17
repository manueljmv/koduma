using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SEC-002: deserializers that can instantiate attacker-chosen types (BinaryFormatter and friends, Json.NET <c>TypeNameHandling</c> other than None).</summary>
internal static class InsecureDeserializationRule
{
    private static readonly HashSet<string> DangerousFormatters = new(StringComparer.Ordinal)
    {
        "BinaryFormatter", "NetDataContractSerializer", "SoapFormatter", "LosFormatter", "ObjectStateFormatter",
    };

    private static readonly HashSet<string> DangerousTypeNameHandling = new(StringComparer.Ordinal)
    {
        "All", "Auto", "Objects", "Arrays",
    };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var node in root.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax creation)
            {
                var typeName = creation.Type switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                    _ => null,
                };

                if (typeName is not null && DangerousFormatters.Contains(typeName))
                {
                    yield return IssueFactory.At(
                        "CS-SEC-002", Severity.Critical, Category.Security, filePath, creation.GetLocation(),
                        $"'{typeName}' is insecure against untrusted data and can lead to remote code execution. Use a safe serializer such as System.Text.Json.");
                }
            }
            else if (node is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "TypeNameHandling" } } access
                     && DangerousTypeNameHandling.Contains(access.Name.Identifier.ValueText))
            {
                yield return IssueFactory.At(
                    "CS-SEC-002", Severity.Critical, Category.Security, filePath, access.GetLocation(),
                    $"'TypeNameHandling.{access.Name.Identifier.ValueText}' lets the payload choose which types are instantiated during deserialization. Use TypeNameHandling.None.");
            }
        }
    }
}
