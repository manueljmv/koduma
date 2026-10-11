using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class HtmlReportGeneratorTests
{
    [Fact]
    public void Write_ProducesSelfContainedHtml_EscapingUserContent()
    {
        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "src/<A>.cs", 1, 1, 1, 1, "<script>alert(1)</script>", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();

        new HtmlReportGenerator().Write(result, writer);
        var html = writer.ToString();

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
