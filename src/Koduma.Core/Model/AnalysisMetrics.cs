namespace Koduma.Core.Model;

public record AnalysisMetrics(
    int LinesOfCode,
    int CyclomaticComplexity,
    int DuplicatedLines,
    int FilesAnalyzed);
