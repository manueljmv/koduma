using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

public class EslintAnalyzer : ILanguageAnalyzer
{
    private readonly ExternalProcessRunner _runner;

    public EslintAnalyzer(ExternalProcessRunner runner) => _runner = runner;

    public string Language => "javascript";
    public IReadOnlyCollection<string> FileExtensions => new[] { ".js", ".jsx", ".ts", ".tsx" };
    public bool AppliesToAllFiles => false;

    public IReadOnlyList<Rule> Rules { get; } = Array.Empty<Rule>(); // ESLint's own rule set is dynamic/user-configured, not enumerable in advance.

    public async Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--format", "json" };
        arguments.AddRange(filePaths);

        var (exitCode, stdOut, stdErr) = await _runner.RunAsync("eslint", arguments);

        if (exitCode == -1)
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: $"eslint could not be run: {stdErr}");

        // ESLint exits 1 when it finds lint errors, which is expected, not a failure.
        // Any other nonzero code is a fatal ESLint error (e.g. broken config) and must not look like a clean run.
        if (exitCode != 0 && exitCode != 1)
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: $"eslint exited with code {exitCode}: {stdErr}");

        if (string.IsNullOrWhiteSpace(stdOut))
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, filePaths.Count), Error: null);

        var issues = EslintOutputMapper.Map(stdOut);
        return new LanguageAnalysisResult(issues, new AnalysisMetrics(0, 0, 0, filePaths.Count), Error: null);
    }
}
