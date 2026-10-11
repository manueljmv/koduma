using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using Xunit;

public class AnalysisOrchestratorTests
{
    private class FakeDiscovery : IFileDiscovery
    {
        public IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config) =>
            new[] { "/repo/a.cs", "/repo/b.js", "/repo/c.cs" };
    }

    private class FakeAnalyzer : ILanguageAnalyzer
    {
        private readonly Func<IReadOnlyList<string>, LanguageAnalysisResult> _handler;
        public FakeAnalyzer(string language, IReadOnlyCollection<string> extensions, bool appliesToAll, Func<IReadOnlyList<string>, LanguageAnalysisResult> handler)
        {
            Language = language;
            FileExtensions = extensions;
            AppliesToAllFiles = appliesToAll;
            _handler = handler;
        }

        public string Language { get; }
        public IReadOnlyCollection<string> FileExtensions { get; }
        public bool AppliesToAllFiles { get; }
        public IReadOnlyList<Rule> Rules => Array.Empty<Rule>();

        public Task<LanguageAnalysisResult> AnalyzeAsync(string projectPath, IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken ct) =>
            Task.FromResult(_handler(filePaths));
    }

    [Fact]
    public async Task AnalyzeAsync_DispatchesFilesByExtension_AndAggregatesIssuesAndMetrics()
    {
        var csIssue = new Issue("CS-X-001", Severity.Major, Category.CodeSmell, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var csAnalyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, files =>
        {
            Assert.Equal(new[] { "/repo/a.cs", "/repo/c.cs" }, files);
            return new LanguageAnalysisResult(new IIssue[] { csIssue }, new AnalysisMetrics(10, 1, 0, 2), Error: null);
        });

        var allFilesAnalyzer = new FakeAnalyzer("security", Array.Empty<string>(), appliesToAll: true, files =>
        {
            Assert.Equal(3, files.Count);
            return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: null);
        });

        var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { csAnalyzer, allFilesAnalyzer }, new FakeDiscovery());

        var result = await orchestrator.AnalyzeAsync("/repo", KodumaConfig.Default);

        Assert.Single(result.Issues);
        Assert.Equal(10, result.Metrics.LinesOfCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsAnalysisError_ButKeepsOtherLanguagesResults()
    {
        var failingAnalyzer = new FakeAnalyzer("javascript", new[] { ".js" }, appliesToAll: false, _ =>
            new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: "eslint not found on PATH"));

        var okIssue = new Issue("CS-X-001", Severity.Minor, Category.CodeSmell, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var okAnalyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, _ =>
            new LanguageAnalysisResult(new IIssue[] { okIssue }, new AnalysisMetrics(5, 1, 0, 2), Error: null));

        var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { failingAnalyzer, okAnalyzer }, new FakeDiscovery());

        var result = await orchestrator.AnalyzeAsync("/repo", KodumaConfig.Default);

        Assert.Single(result.Errors);
        Assert.Equal("javascript", result.Errors[0].Language);
        Assert.Single(result.Issues);
    }

    [Fact]
    public async Task AnalyzeAsync_AppliesRuleOverrides_DroppingOffRulesAndReplacingSeverity()
    {
        var offIssue = new Issue("CS-NAMING-001", Severity.Minor, Category.CodeSmell, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var severityIssue = new Issue("SEC-SECRET-001", Severity.Critical, Category.Security, "/repo/a.cs", 2, 1, 2, 1, "y", "csharp");
        var analyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, _ =>
            new LanguageAnalysisResult(new IIssue[] { offIssue, severityIssue }, new AnalysisMetrics(0, 0, 0, 1), Error: null));

        var config = new KodumaConfig(
            new Dictionary<string, RuleOverride>
            {
                ["CS-NAMING-001"] = new RuleOverride(Enabled: false, Severity: null),
                ["SEC-SECRET-001"] = new RuleOverride(Enabled: null, Severity: Severity.Blocker),
            },
            new List<string>());

        var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { analyzer }, new FakeDiscovery());

        var result = await orchestrator.AnalyzeAsync("/repo", config);

        Assert.Single(result.Issues);
        Assert.Equal("SEC-SECRET-001", result.Issues[0].RuleId);
        Assert.Equal(Severity.Blocker, result.Issues[0].Severity);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsAnalysisError_WhenAnalyzerThrows_AndKeepsOtherLanguagesResults()
    {
        var throwingAnalyzer = new FakeAnalyzer("javascript", new[] { ".js" }, appliesToAll: false, _ =>
            throw new KeyNotFoundException("line missing"));

        var okIssue = new Issue("CS-X-001", Severity.Minor, Category.CodeSmell, "/repo/a.cs", 1, 1, 1, 1, "x", "csharp");
        var okAnalyzer = new FakeAnalyzer("csharp", new[] { ".cs" }, appliesToAll: false, _ =>
            new LanguageAnalysisResult(new IIssue[] { okIssue }, new AnalysisMetrics(5, 1, 0, 2), Error: null));

        var orchestrator = new AnalysisOrchestrator(new ILanguageAnalyzer[] { throwingAnalyzer, okAnalyzer }, new FakeDiscovery());

        var result = await orchestrator.AnalyzeAsync("/repo", KodumaConfig.Default);

        var error = Assert.Single(result.Errors);
        Assert.Equal("javascript", error.Language);
        Assert.Contains("line missing", error.Message);
        Assert.Single(result.Issues);
        Assert.Equal(5, result.Metrics.LinesOfCode);
    }
}
