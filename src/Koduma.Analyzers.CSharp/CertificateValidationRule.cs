using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SEC-003: TLS certificate validation disabled (callbacks that always return true, or DangerousAcceptAnyServerCertificateValidator).</summary>
internal static class CertificateValidationRule
{
    private static readonly HashSet<string> CallbackProperties = new(StringComparer.Ordinal)
    {
        "ServerCertificateValidationCallback", "ServerCertificateCustomValidationCallback", "RemoteCertificateValidationCallback",
    };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var node in root.DescendantNodes())
        {
            if (node is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "DangerousAcceptAnyServerCertificateValidator")
            {
                yield return CreateIssue(filePath, access);
            }
            else if (node is AssignmentExpressionSyntax assignment
                     && PropertyName(assignment.Left) is { } property
                     && CallbackProperties.Contains(property)
                     && assignment.Right is AnonymousFunctionExpressionSyntax function
                     && AlwaysReturnsTrue(function.Body))
            {
                yield return CreateIssue(filePath, assignment);
            }
        }
    }

    private static IIssue CreateIssue(string filePath, SyntaxNode node) =>
        IssueFactory.At(
            "CS-SEC-003", Severity.Critical, Category.Security, filePath, node.GetLocation(),
            "TLS certificate validation is disabled, which exposes connections to man-in-the-middle attacks. Validate the certificate chain instead.");

    private static string? PropertyName(ExpressionSyntax left) => left switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private static bool AlwaysReturnsTrue(SyntaxNode body)
    {
        if (body is LiteralExpressionSyntax literal)
            return literal.IsKind(SyntaxKind.TrueLiteralExpression);

        if (body is BlockSyntax block)
        {
            var returns = block.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
            return returns.Count > 0
                && returns.All(r => r.Expression is LiteralExpressionSyntax value && value.IsKind(SyntaxKind.TrueLiteralExpression));
        }

        return false;
    }
}
