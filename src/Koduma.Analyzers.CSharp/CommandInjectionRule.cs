using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-SEC-005: a Process.Start/ProcessStartInfo argument built via string concatenation or interpolation, letting unsanitized input inject extra arguments or commands.</summary>
internal static class CommandInjectionRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var node in root.DescendantNodes())
        {
            ArgumentListSyntax? arguments = node switch
            {
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Start" } access } invocation
                    when SimpleName(access.Expression) == "Process" => invocation.ArgumentList,
                ObjectCreationExpressionSyntax creation when TypeName(creation.Type) == "ProcessStartInfo" => creation.ArgumentList,
                _ => null,
            };

            if (arguments is null)
                continue;

            foreach (var argument in arguments.Arguments)
            {
                if (argument.Expression is not (InterpolatedStringExpressionSyntax or BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }))
                    continue;

                yield return IssueFactory.At(
                    "CS-SEC-005", Severity.Critical, Category.Security, filePath, argument.GetLocation(),
                    "Command or argument built via string concatenation/interpolation; unsanitized input could inject extra arguments or commands.");
                break;
            }
        }
    }

    private static string? TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        _ => null,
    };

    private static string? SimpleName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };
}
