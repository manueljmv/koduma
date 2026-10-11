using System.Text.Json;
using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class JsonReportGeneratorTests
{
    [Fact]
    public void Write_ProducesValidJson_ParseableBackToAnalysisResult()
    {
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new JsonReportGenerator().Write(result, writer);

        var parsed = JsonSerializer.Deserialize(writer.ToString(), KodumaJsonContext.Default.AnalysisResult);
        Assert.NotNull(parsed);
        Assert.Equal("/repo", parsed!.ProjectPath);
    }

    [Fact]
    public void Write_SerializesEnumsAsStringNames_AndRoundTrips()
    {
        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.Security, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(0, 0, 0, 1), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new JsonReportGenerator().Write(result, writer);
        var json = writer.ToString();

        Assert.Contains("\"Severity\":\"Major\"", json);
        Assert.Contains("\"Category\":\"Security\"", json);
        var parsed = JsonSerializer.Deserialize(json, KodumaJsonContext.Default.AnalysisResult)!;
        Assert.Equal(Severity.Major, parsed.Issues[0].Severity);
        Assert.Equal(Category.Security, parsed.Issues[0].Category);
    }

    [Fact]
    public void RuleSerialization_UsesStringEnumNames()
    {
        var rule = new Rule("CS-NAMING-001", "Naming", Category.CodeSmell, Severity.Minor, "csharp", null);

        var json = JsonSerializer.Serialize(new[] { rule }, KodumaJsonContext.Default.RuleArray);

        Assert.Contains("\"Category\":\"CodeSmell\"", json);
        Assert.Contains("\"DefaultSeverity\":\"Minor\"", json);
    }
}
