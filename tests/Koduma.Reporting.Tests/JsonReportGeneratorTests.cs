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
}
