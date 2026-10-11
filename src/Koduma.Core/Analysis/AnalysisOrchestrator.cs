using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;

namespace Koduma.Core.Analysis;

public class AnalysisOrchestrator
{
    private readonly IReadOnlyList<ILanguageAnalyzer> _analyzers;
    private readonly IFileDiscovery _fileDiscovery;

    public AnalysisOrchestrator(IEnumerable<ILanguageAnalyzer> analyzers, IFileDiscovery fileDiscovery)
    {
        _analyzers = analyzers.ToList();
        _fileDiscovery = fileDiscovery;
    }

    public async Task<AnalysisResult> AnalyzeAsync(string projectPath, KodumaConfig config, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var allFiles = _fileDiscovery.DiscoverFiles(projectPath, config);

        var tasks = _analyzers.Select(async analyzer =>
        {
            var filesForAnalyzer = analyzer.AppliesToAllFiles
                ? allFiles
                : allFiles.Where(f => analyzer.FileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();

            if (filesForAnalyzer.Count == 0)
                return (analyzer.Language, new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: null));

            try
            {
                var languageResult = await analyzer.AnalyzeAsync(projectPath, filesForAnalyzer, config, cancellationToken);
                return (analyzer.Language, languageResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (analyzer.Language, new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: ex.Message));
            }
        });

        var perLanguageResults = await Task.WhenAll(tasks);

        var issues = new List<IIssue>();
        var errors = new List<AnalysisError>();
        var totalLoc = 0;
        var totalComplexity = 0;
        var totalFiles = 0;

        foreach (var (language, languageResult) in perLanguageResults)
        {
            if (languageResult.Error is { } error)
            {
                errors.Add(new AnalysisError(language, error));
                continue;
            }

            issues.AddRange(languageResult.Issues);
            totalLoc += languageResult.Metrics.LinesOfCode;
            totalComplexity += languageResult.Metrics.CyclomaticComplexity;
            totalFiles += languageResult.Metrics.FilesAnalyzed;
        }

        stopwatch.Stop();

        var finalIssues = ApplyRuleOverrides(issues, config);

        return new AnalysisResult(
            ProjectPath: projectPath,
            Timestamp: DateTimeOffset.UtcNow,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Issues: finalIssues,
            Metrics: new AnalysisMetrics(totalLoc, totalComplexity, ComputeDuplicatedLines(allFiles), totalFiles),
            Errors: errors);
    }

    private static int ComputeDuplicatedLines(IReadOnlyList<string> files)
    {
        const int WindowSize = 6;
        var windowOccurrences = new Dictionary<string, List<(string File, int StartLine)>>();

        foreach (var file in files)
        {
            List<string> lines;
            try
            {
                lines = File.ReadAllLines(file)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i + WindowSize <= lines.Count; i++)
            {
                var window = string.Join("\n", lines.Skip(i).Take(WindowSize));
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(window)));
                if (!windowOccurrences.TryGetValue(hash, out var occurrences))
                    windowOccurrences[hash] = occurrences = new List<(string, int)>();
                occurrences.Add((file, i));
            }
        }

        var duplicatedLineKeys = new HashSet<(string File, int Line)>();
        foreach (var occurrences in windowOccurrences.Values.Where(o => o.Count > 1))
        {
            foreach (var (file, startLine) in occurrences)
                for (var offset = 0; offset < WindowSize; offset++)
                    duplicatedLineKeys.Add((file, startLine + offset));
        }

        return duplicatedLineKeys.Count;
    }

    private static IReadOnlyList<IIssue> ApplyRuleOverrides(List<IIssue> issues, KodumaConfig config)
    {
        var result = new List<IIssue>();

        foreach (var issue in issues)
        {
            if (!config.Rules.TryGetValue(issue.RuleId, out var @override))
            {
                result.Add(issue);
                continue;
            }

            if (@override.Enabled == false)
                continue;

            result.Add(@override.Severity is { } severity
                ? new Issue(issue.RuleId, severity, issue.Category, issue.FilePath, issue.Line, issue.Column, issue.EndLine, issue.EndColumn, issue.Message, issue.Language)
                : issue);
        }

        return result;
    }
}
