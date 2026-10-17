using Koduma.Core.Model;
using Microsoft.CodeAnalysis;

namespace Koduma.Analyzers.CSharp;

internal static class IssueFactory
{
    public static Issue At(string ruleId, Severity severity, Category category, string filePath, Location location, string message)
    {
        var lineSpan = location.GetLineSpan();
        return new Issue(
            RuleId: ruleId,
            Severity: severity,
            Category: category,
            FilePath: filePath,
            Line: lineSpan.StartLinePosition.Line + 1,
            Column: lineSpan.StartLinePosition.Character + 1,
            EndLine: lineSpan.EndLinePosition.Line + 1,
            EndColumn: lineSpan.EndLinePosition.Character + 1,
            Message: message,
            Language: "csharp");
    }
}
