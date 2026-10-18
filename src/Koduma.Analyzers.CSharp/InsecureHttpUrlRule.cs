using System.Text.RegularExpressions;
using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SEC-004: a hardcoded "http://" URL, sent in the clear and vulnerable to interception/tampering. Loopback addresses and common XML namespace/schema hosts are excluded, since those are identifiers rather than endpoints that get fetched.</summary>
internal static class InsecureHttpUrlRule
{
    private static readonly Regex HttpUrlPattern = new(
        @"^http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] NamespaceHosts = { "schemas.", "w3.org", "xmlsoap.org", "purl.org" };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var value = literal.Token.ValueText;
            if (!HttpUrlPattern.IsMatch(value))
                continue;

            if (NamespaceHosts.Any(host => value.Contains(host, StringComparison.OrdinalIgnoreCase)))
                continue;

            yield return IssueFactory.At(
                "CS-SEC-004", Severity.Major, Category.Security, filePath, literal.GetLocation(),
                "Hardcoded 'http://' URL; traffic to it is unencrypted. Use 'https://' instead.");
        }
    }
}
