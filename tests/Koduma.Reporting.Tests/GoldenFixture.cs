using Koduma.Core.Model;

internal static class GoldenFixture
{
    public static AnalysisResult SampleResult() => new(
        ProjectPath: "/sample/project",
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        DurationMs: 42,
        Issues: new IIssue[]
        {
            new Issue("CS-NAMING-001", Severity.Minor, Category.CodeSmell, "src/Foo.cs", 10, 5, 10, 20, "Field should be camelCase.", "csharp"),
            new Issue("SEC-SECRET-001", Severity.Critical, Category.Security, "config/secrets.txt", 3, 1, 3, 30, "Possible hardcoded secret (AWS access key).", "security"),
        },
        Metrics: new AnalysisMetrics(LinesOfCode: 120, CyclomaticComplexity: 8, DuplicatedLines: 12, FilesAnalyzed: 4),
        Errors: new[] { new AnalysisError("javascript", "eslint could not be run: not found") });
}
