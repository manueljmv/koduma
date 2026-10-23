using System.Text.Json;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

/// <summary>Normalizes the JSON <c>npm audit --json</c> prints into Koduma issues.</summary>
public static class NpmAuditMapper
{
    public static List<IIssue> Map(string json, string lockFilePath)
    {
        var issues = new List<IIssue>();
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("vulnerabilities", out var vulnerabilities) || vulnerabilities.ValueKind != JsonValueKind.Object)
            return issues;

        foreach (var package in vulnerabilities.EnumerateObject())
        {
            var packageName = package.Name;

            if (!package.Value.TryGetProperty("via", out var via) || via.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in via.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var source = entry.TryGetProperty("source", out var sourceElement) ? sourceElement.GetInt32() : 0;
                var title = entry.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var url = entry.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                var severity = entry.TryGetProperty("severity", out var severityElement) ? severityElement.GetString() : null;

                var ghsaId = AdvisoryUrl.ExtractGhsaId(url);
                var ruleId = ghsaId is not null ? $"VULN-{ghsaId}" : $"VULN-NPM-{packageName}-{source}";

                issues.Add(new Issue(
                    RuleId: ruleId,
                    Severity: VulnerabilitySeverityMapper.Map(severity),
                    Category: Category.Security,
                    FilePath: lockFilePath,
                    Line: 1,
                    Column: 1,
                    EndLine: 1,
                    EndColumn: 1,
                    Message: $"{packageName} has a {severity} severity vulnerability ({title}): {url}",
                    Language: "javascript"));
            }
        }

        return issues;
    }
}
