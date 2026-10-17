using System.Text.Json;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

/// <summary>Normalizes the SARIF v2.1 error log that Roslyn writes during <c>dotnet build</c> (compiler + .NET/NuGet analyzers) into Koduma issues.</summary>
public static class DotnetSarifMapper
{
    // Diagnostics Koduma already reports with its own native rule; listing both would double-count the same finding.
    private static readonly HashSet<string> NativeEquivalents = new(StringComparer.Ordinal)
    {
        "CA1031",                                   // general exception caught            -> CS-DESIGN-004
        "CA2200",                                   // rethrow to preserve stack details   -> CS-BUG-003
        "CA2002",                                   // do not lock on weakly identified    -> CS-BUG-004
        "CA2211",                                   // non-constant fields not visible     -> CS-SMELL-002
        "CA5350", "CA5351",                         // weak cryptographic algorithms       -> CS-SEC-001
        "CA2300", "CA2301", "CA2302", "CA2305",     // insecure deserializers              -> CS-SEC-002
        "CA2310", "CA2311", "CA2312", "CA2315",
        "CA2326",                                   // TypeNameHandling other than None    -> CS-SEC-002
        "CA5359",                                   // certificate validation disabled     -> CS-SEC-003
    };

    public static List<IIssue> Map(string sarifJson)
    {
        var issues = new List<IIssue>();
        using var document = JsonDocument.Parse(sarifJson);

        if (!document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return issues;

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                continue;

            var ruleCategories = ReadRuleCategories(run);

            foreach (var result in results.EnumerateArray())
            {
                var issue = MapResult(result, ruleCategories);
                if (issue is not null)
                    issues.Add(issue);
            }
        }

        return issues;
    }

    private static Dictionary<string, string> ReadRuleCategories(JsonElement run)
    {
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!run.TryGetProperty("tool", out var tool)
            || !tool.TryGetProperty("driver", out var driver)
            || !driver.TryGetProperty("rules", out var rules)
            || rules.ValueKind != JsonValueKind.Array)
            return categories;

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("id", out var id) && id.GetString() is { } ruleId
                && rule.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("category", out var category) && category.GetString() is { } categoryName)
                categories[ruleId] = categoryName;
        }

        return categories;
    }

    private static Issue? MapResult(JsonElement result, Dictionary<string, string> ruleCategories)
    {
        if (!result.TryGetProperty("ruleId", out var ruleIdElement) || ruleIdElement.GetString() is not { Length: > 0 } rawRuleId)
            return null;

        if (NativeEquivalents.Contains(rawRuleId))
            return null;

        if (result.TryGetProperty("suppressions", out var suppressions)
            && suppressions.ValueKind == JsonValueKind.Array && suppressions.GetArrayLength() > 0)
            return null;

        // SARIF's default level, when absent, is "warning".
        var level = result.TryGetProperty("level", out var levelElement) ? levelElement.GetString() ?? "warning" : "warning";
        Severity severity;
        switch (level)
        {
            case "error": severity = Severity.Critical; break;
            case "warning": severity = Severity.Major; break;
            case "note": severity = Severity.Minor; break;
            default: return null;
        }

        if (!TryReadLocation(result, out var filePath, out var line, out var column, out var endLine, out var endColumn))
            return null;

        var message = result.TryGetProperty("message", out var messageElement) && messageElement.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : string.Empty;

        ruleCategories.TryGetValue(rawRuleId, out var sarifCategory);

        return new Issue(
            RuleId: $"NET-{rawRuleId}",
            Severity: severity,
            Category: ToCategory(rawRuleId, sarifCategory, level),
            FilePath: filePath,
            Line: line,
            Column: column,
            EndLine: endLine,
            EndColumn: endColumn,
            Message: message,
            Language: "csharp");
    }

    private static Category ToCategory(string ruleId, string? sarifCategory, string level)
    {
        if (string.Equals(sarifCategory, "Security", StringComparison.OrdinalIgnoreCase))
            return Category.Security;

        if (string.Equals(sarifCategory, "Reliability", StringComparison.OrdinalIgnoreCase))
            return Category.Bug;

        // Compiler errors (CSxxxx) mean the code does not build, which is a defect rather than a style smell.
        if (ruleId.StartsWith("CS", StringComparison.Ordinal) && level == "error")
            return Category.Bug;

        return Category.CodeSmell;
    }

    private static bool TryReadLocation(JsonElement result, out string filePath, out int line, out int column, out int endLine, out int endColumn)
    {
        filePath = string.Empty;
        line = column = endLine = endColumn = 1;

        if (!result.TryGetProperty("locations", out var locations)
            || locations.ValueKind != JsonValueKind.Array
            || locations.GetArrayLength() == 0)
            return false;

        var location = locations[0];
        if (!location.TryGetProperty("physicalLocation", out var physical)
            || !physical.TryGetProperty("artifactLocation", out var artifact)
            || !artifact.TryGetProperty("uri", out var uriElement)
            || uriElement.GetString() is not { Length: > 0 } uri)
            return false;

        filePath = ToLocalPath(uri);

        if (physical.TryGetProperty("region", out var region))
        {
            line = ReadInt(region, "startLine", 1);
            column = ReadInt(region, "startColumn", 1);
            endLine = ReadInt(region, "endLine", line);
            endColumn = ReadInt(region, "endColumn", column);
        }

        return true;
    }

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static string ToLocalPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;
}
