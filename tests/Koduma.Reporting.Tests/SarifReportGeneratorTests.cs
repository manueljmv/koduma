using System.Text.Json;
using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class SarifReportGeneratorTests
{
    [Fact]
    public void Write_ProducesValidSarifWithOneResultPerIssue()
    {
        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "src/A.cs", 1, 1, 1, 1, "bad name", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new SarifReportGenerator().Write(result, writer);
        using var document = JsonDocument.Parse(writer.ToString());

        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("CS-NAMING-001", results[0].GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Write_ReportsAnalysisErrors_AsToolExecutionNotifications()
    {
        var errors = new[] { new AnalysisError("javascript", "eslint could not be run") };
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), errors);
        using var writer = new StringWriter();

        new SarifReportGenerator().Write(result, writer);
        using var document = JsonDocument.Parse(writer.ToString());

        var invocation = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0];
        Assert.False(invocation.GetProperty("executionSuccessful").GetBoolean());
        var notification = invocation.GetProperty("toolExecutionNotifications")[0];
        Assert.Equal("error", notification.GetProperty("level").GetString());
        var text = notification.GetProperty("message").GetProperty("text").GetString();
        Assert.Contains("javascript", text);
        Assert.Contains("eslint could not be run", text);
    }

    [Fact]
    public void Write_UsesProjectRelativeUris_AndListsRulesInDriver()
    {
        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "/repo/src/My File.cs", 1, 1, 1, 1, "bad name", "csharp");
        var duplicate = issue with { Line = 5 };
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue, duplicate }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new SarifReportGenerator().Write(result, writer);
        using var document = JsonDocument.Parse(writer.ToString());
        var run = document.RootElement.GetProperty("runs")[0];

        var uri = run.GetProperty("results")[0].GetProperty("locations")[0]
            .GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString()!;
        Assert.Equal("src/My%20File.cs", uri);
        Assert.False(uri.StartsWith('/'));
        Assert.DoesNotMatch("^[A-Za-z]:", uri);

        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal("CS-NAMING-001", rules[0].GetProperty("id").GetString());
    }
}
