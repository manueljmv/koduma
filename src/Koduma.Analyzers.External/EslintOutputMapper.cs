using System.Text.Json;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

public static class EslintOutputMapper
{
    public static List<IIssue> Map(string eslintJson)
    {
        var issues = new List<IIssue>();
        using var document = JsonDocument.Parse(eslintJson);

        foreach (var fileResult in document.RootElement.EnumerateArray())
        {
            var filePath = fileResult.GetProperty("filePath").GetString() ?? "";

            foreach (var message in fileResult.GetProperty("messages").EnumerateArray())
            {
                var eslintSeverity = message.GetProperty("severity").GetInt32();
                var line = message.TryGetProperty("line", out var l) ? l.GetInt32() : 1;
                var column = message.TryGetProperty("column", out var c) ? c.GetInt32() : 1;
                var endLine = message.TryGetProperty("endLine", out var el) ? el.GetInt32() : line;
                var endColumn = message.TryGetProperty("endColumn", out var ec) ? ec.GetInt32() : column;
                var ruleId = message.TryGetProperty("ruleId", out var r) ? r.GetString() ?? "unknown" : "unknown";

                issues.Add(new Issue(
                    RuleId: $"JS-{ruleId}",
                    Severity: eslintSeverity == 2 ? Severity.Major : Severity.Minor,
                    Category: Category.CodeSmell,
                    FilePath: filePath,
                    Line: line,
                    Column: column,
                    EndLine: endLine,
                    EndColumn: endColumn,
                    Message: message.GetProperty("message").GetString() ?? "",
                    Language: "javascript"));
            }
        }

        return issues;
    }
}
