using System.Text.Json;
using Koduma.Core.Model;
using Xunit;

public class ModelSerializationTests
{
    [Fact]
    public void AnalysisResult_RoundTrips_ThroughSourceGeneratedContext()
    {
        var issue = new Issue(
            RuleId: "CS-NAMING-001",
            Severity: Severity.Major,
            Category: Category.CodeSmell,
            FilePath: "src/Foo.cs",
            Line: 10,
            Column: 5,
            EndLine: 10,
            EndColumn: 20,
            Message: "Field should be camelCase with leading underscore.",
            Language: "csharp");

        var result = new AnalysisResult(
            ProjectPath: "/repo",
            Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DurationMs: 42,
            Issues: new IIssue[] { issue },
            Metrics: new AnalysisMetrics(LinesOfCode: 100, CyclomaticComplexity: 3, DuplicatedLines: 0, FilesAnalyzed: 1),
            Errors: Array.Empty<AnalysisError>());

        var json = JsonSerializer.Serialize(result, KodumaJsonContext.Default.AnalysisResult);
        var roundTripped = JsonSerializer.Deserialize(json, KodumaJsonContext.Default.AnalysisResult);

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped!.Issues);
        Assert.Equal("CS-NAMING-001", roundTripped.Issues[0].RuleId);
        Assert.Equal(Severity.Major, roundTripped.Issues[0].Severity);
        Assert.Equal(100, roundTripped.Metrics.LinesOfCode);
    }
}
