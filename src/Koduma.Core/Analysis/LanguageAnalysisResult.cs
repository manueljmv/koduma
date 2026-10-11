using Koduma.Core.Model;

namespace Koduma.Core.Analysis;

public record LanguageAnalysisResult(IReadOnlyList<IIssue> Issues, AnalysisMetrics Metrics, string? Error);
