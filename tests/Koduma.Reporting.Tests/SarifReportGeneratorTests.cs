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
}
