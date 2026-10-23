using System.Text.Json;
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

/// <summary>Reports known-vulnerable npm packages via <c>npm audit --json</c>.</summary>
public class NpmAuditAnalyzer : ILanguageAnalyzer
{
    /// <summary>Set to any non-empty value to skip this analyzer (it queries npm's advisory feed over the network).</summary>
    public const string DisableEnvironmentVariable = "KODUMA_SKIP_NPM_AUDIT";

    internal static string ResolveNpmExecutable(bool isWindows) => isWindows ? "npm.cmd" : "npm";

    private static readonly string NpmExecutable = ResolveNpmExecutable(OperatingSystem.IsWindows());

    private readonly ExternalProcessRunner _runner;

    public NpmAuditAnalyzer(ExternalProcessRunner runner) => _runner = runner;

    public string Language => "javascript";
    public IReadOnlyCollection<string> FileExtensions => new[] { ".json" };
    public bool AppliesToAllFiles => false;

    public IReadOnlyList<Rule> Rules { get; } = Array.Empty<Rule>();

    public async Task<LanguageAnalysisResult> AnalyzeAsync(string projectPath, IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var empty = new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: null);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableEnvironmentVariable)))
            return empty;

        var lockFiles = FindLockFiles(filePaths);
        if (lockFiles.Count == 0)
            return empty;

        var issues = new List<IIssue>();
        foreach (var lockFile in lockFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(lockFile)!;
            var (exitCode, stdOut, stdErr) = await _runner.RunAsync(NpmExecutable, new[] { "audit", "--json" }, directory);

            if (exitCode == -1)
                return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: $"npm could not be run: {stdErr}");

            if (exitCode != 0 && exitCode != 1)
                return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: $"npm audit exited with code {exitCode}: {stdErr}");

            try
            {
                using var document = JsonDocument.Parse(stdOut);
                if (!document.RootElement.TryGetProperty("vulnerabilities", out _))
                    return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0),
                        Error: $"npm audit produced no 'vulnerabilities' field for {lockFile} (offline, registry error, or unsupported npm version?): {Tail(stdOut)}");

                issues.AddRange(NpmAuditMapper.Map(stdOut, lockFile));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0),
                    Error: $"npm audit produced unreadable output for {lockFile}: {ex.Message}");
            }
        }

        return new LanguageAnalysisResult(issues, new AnalysisMetrics(0, 0, 0, 0), Error: null);
    }

    private static IReadOnlyList<string> FindLockFiles(IReadOnlyList<string> filePaths)
    {
        var byDirectory = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in filePaths)
        {
            var name = Path.GetFileName(file);
            if (name is not ("package-lock.json" or "npm-shrinkwrap.json"))
                continue;

            var directory = Path.GetDirectoryName(file)!;
            if (!byDirectory.ContainsKey(directory))
                byDirectory[directory] = file;
        }

        return byDirectory.Values.ToList();
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "..." + trimmed[^400..];
    }
}
