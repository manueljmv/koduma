using Koduma.Core.Model;
using Koduma.Reporting;
using Xunit;

public class TableReportGeneratorTests
{
    [Fact]
    public void Write_IncludesEachIssueAndSummaryCounts_EvenWhenEmpty()
    {
        var emptyResult = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Array.Empty<AnalysisError>());
        using var writer = new StringWriter();
        new TableReportGenerator().Write(emptyResult, writer);

        Assert.Contains("0 issues", writer.ToString());

        var issue = new Issue("CS-NAMING-001", Severity.Major, Category.CodeSmell, "src/A.cs", 1, 1, 1, 1, "bad name", "csharp");
        var result = new AnalysisResult("/repo", DateTimeOffset.UtcNow, 1, new IIssue[] { issue }, new AnalysisMetrics(10, 1, 0, 1), Array.Empty<AnalysisError>());
        using var writer2 = new StringWriter();
        new TableReportGenerator().Write(result, writer2);

        Assert.Contains("CS-NAMING-001", writer2.ToString());
        Assert.Contains("src/A.cs", writer2.ToString());
    }
}
