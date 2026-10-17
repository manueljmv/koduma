using Koduma.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koduma.Analyzers.CSharp;

/// <summary>CS-BUG-003: <c>throw ex;</c> inside a catch block resets the stack trace; use <c>throw;</c>.</summary>
internal static class RethrowExceptionRule
{
    public static IEnumerable<IIssue> Evaluate(SyntaxTree tree, string filePath)
    {
        var root = tree.GetCompilationUnitRoot();

        foreach (var throwStatement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            if (throwStatement.Expression is not IdentifierNameSyntax thrown)
                continue;

            var enclosingCatch = throwStatement.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
            var caught = enclosingCatch?.Declaration?.Identifier.ValueText;
            if (string.IsNullOrEmpty(caught) || caught != thrown.Identifier.ValueText)
                continue;

            yield return IssueFactory.At(
                "CS-BUG-003", Severity.Major, Category.Bug, filePath, throwStatement.GetLocation(),
                $"'throw {caught};' resets the stack trace of the caught exception. Use 'throw;' to rethrow it unchanged.");
        }
    }
}
