using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-010: a well-known disposable resource created without a 'using' declaration/statement, so it leaks if an exception is thrown before it is disposed.</summary>
internal static class DisposableNotUsingRule
{
    private static readonly HashSet<string> DisposableTypes = new(StringComparer.Ordinal)
    {
        "SqlConnection", "SqlCommand", "OleDbConnection", "OdbcConnection",
        "StreamReader", "StreamWriter", "FileStream", "BinaryReader", "BinaryWriter",
    };

    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var statement in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                continue;

            foreach (var declarator in statement.Declaration.Variables)
            {
                var typeName = declarator.Initializer?.Value switch
                {
                    ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax id } => id.Identifier.ValueText,
                    ObjectCreationExpressionSyntax { Type: QualifiedNameSyntax q } => q.Right.Identifier.ValueText,
                    _ => null,
                };

                if (typeName is null || !DisposableTypes.Contains(typeName))
                    continue;

                yield return IssueFactory.At(
                    "CS-BUG-010", Severity.Major, Category.Bug, filePath, declarator.GetLocation(),
                    $"'{typeName}' is disposable but '{declarator.Identifier.ValueText}' is not declared with 'using'; the resource may leak if an exception is thrown.");
            }
        }
    }
}
