using Koduma.Core.Model;

namespace Koduma.Reporting;

public class TableReportGenerator : IReportGenerator
{
    public string Format => "table";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        writer.WriteLine($"Koduma analysis of {result.ProjectPath} ({result.DurationMs} ms)");
        writer.WriteLine($"Files analyzed: {result.Metrics.FilesAnalyzed}, LOC: {result.Metrics.LinesOfCode}");
        writer.WriteLine();

        foreach (var issue in result.Issues)
        {
            writer.WriteLine($"[{issue.Severity}] {issue.RuleId} {issue.FilePath}:{issue.Line}:{issue.Column} — {issue.Message}");
        }

        foreach (var error in result.Errors)
        {
            writer.WriteLine($"[ERROR] {error.Language}: {error.Message}");
        }

        writer.WriteLine();
        writer.WriteLine($"{result.Issues.Count} issues, {result.Errors.Count} errors");
    }
}
