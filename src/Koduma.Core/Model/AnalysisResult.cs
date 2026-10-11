namespace Koduma.Core.Model;

public record AnalysisResult(
    string ProjectPath,
    DateTimeOffset Timestamp,
    long DurationMs,
    IReadOnlyList<IIssue> Issues,
    AnalysisMetrics Metrics,
    IReadOnlyList<AnalysisError> Errors);
