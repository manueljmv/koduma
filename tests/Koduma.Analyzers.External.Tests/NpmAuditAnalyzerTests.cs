using Koduma.Analyzers.External;
using Koduma.Core.Config;
using Xunit;

public class NpmAuditAnalyzerTests
{
    private sealed class FakeRunner : ExternalProcessRunner
    {
        private readonly (int ExitCode, string StdOut, string StdErr) _result;

        public FakeRunner((int, string, string) result) => _result = result;

        public List<IReadOnlyList<string>> Calls { get; } = new();
        public string? LastWorkingDirectory { get; private set; }

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Calls.Add(arguments);
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(_result);
        }
    }

    /// <summary>Returns a different fixed result per call (by working directory), for monorepo-style tests.</summary>
    private sealed class PerDirectoryFakeRunner : ExternalProcessRunner
    {
        private readonly Dictionary<string, (int ExitCode, string StdOut, string StdErr)> _resultsByDirectory;

        public PerDirectoryFakeRunner(Dictionary<string, (int, string, string)> resultsByDirectory) => _resultsByDirectory = resultsByDirectory;

        public List<string> WorkingDirectories { get; } = new();

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            WorkingDirectories.Add(workingDirectory!);
            return Task.FromResult(_resultsByDirectory[workingDirectory!]);
        }
    }

    [Theory]
    [InlineData(true, "npm.cmd")]
    [InlineData(false, "npm")]
    public void ResolveNpmExecutable_PicksCmdShimOnWindowsOnly(bool isWindows, string expected)
    {
        Assert.Equal(expected, NpmAuditAnalyzer.ResolveNpmExecutable(isWindows));
    }

    [Fact]
    public async Task Analyze_DoesNothing_WhenNoLockFileAmongDiscoveredFiles()
    {
        var runner = new FakeRunner((0, "", ""));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Issues);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Analyze_RunsNpmAuditInLockFileDirectory_AndMapsOutput()
    {
        const string json = """
        { "vulnerabilities": { "lodash": { "severity": "critical", "via": [
          { "source": 1106918, "title": "Prototype Pollution in lodash",
            "url": "https://github.com/advisories/GHSA-jf85-cpcp-j695", "severity": "critical" }
        ] } } }
        """;
        var runner = new FakeRunner((1, json, ""));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/pkg/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.Equal(Path.GetDirectoryName("/repo/pkg/package-lock.json"), runner.LastWorkingDirectory);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("/repo/pkg/package-lock.json", issue.FilePath);
        Assert.Equal("VULN-GHSA-jf85-cpcp-j695", issue.RuleId);
    }

    [Fact]
    public async Task Analyze_TreatsExitZero_AsClean()
    {
        var runner = new FakeRunner((0, """{ "vulnerabilities": {} }""", ""));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenExitCodeIsFatal()
    {
        var runner = new FakeRunner((2, "", "config error"));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("config error", result.Error);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenProcessCannotStart()
    {
        var runner = new FakeRunner((-1, "", "An error occurred trying to start process 'npm'"));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("An error occurred trying to start process 'npm'", result.Error);
    }

    [Fact]
    public async Task Analyze_DeduplicatesLockFilesByDirectory()
    {
        var runner = new FakeRunner((0, """{ "vulnerabilities": {} }""", ""));

        await new NpmAuditAnalyzer(runner).AnalyzeAsync(
            "/repo", new[] { "/repo/package-lock.json", "/repo/npm-shrinkwrap.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenOutputHasNoVulnerabilitiesField()
    {
        // npm audit failing offline / registry error / npm 6's old shape: no top-level "vulnerabilities" key.
        // Must not be mistaken for a clean (zero-vulnerability) run.
        const string offlineErrorJson = """{ "message": "request to https://registry.npmjs.org/ failed", "error": { "code": "ENOTFOUND" } }""";
        var runner = new FakeRunner((1, offlineErrorJson, ""));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsError_OnMalformedJson()
    {
        var runner = new FakeRunner((0, "{ this is not valid json", ""));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenAdvisoryFieldHasUnexpectedType()
    {
        const string wrongTypeJson = """
        { "vulnerabilities": { "lodash": { "severity": "critical", "via": [
          { "source": "not-a-number", "title": "t", "url": "https://github.com/advisories/GHSA-aaaa-aaaa-aaaa", "severity": "critical" }
        ] } } }
        """;
        var runner = new FakeRunner((1, wrongTypeJson, ""));

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_AnalyzesEveryLockFileDirectory_InMonorepo()
    {
        const string vulnerableJson = """
        { "vulnerabilities": { "lodash": { "severity": "critical", "via": [
          { "source": 1, "title": "t", "url": "https://github.com/advisories/GHSA-aaaa-aaaa-aaaa", "severity": "critical" }
        ] } } }
        """;
        const string cleanJson = """{ "vulnerabilities": {} }""";
        var runner = new PerDirectoryFakeRunner(new Dictionary<string, (int, string, string)>
        {
            [Path.GetDirectoryName("/repo/pkg-a/package-lock.json")!] = (1, vulnerableJson, ""),
            [Path.GetDirectoryName("/repo/pkg-b/package-lock.json")!] = (0, cleanJson, ""),
        });

        var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync(
            "/repo", new[] { "/repo/pkg-a/package-lock.json", "/repo/pkg-b/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, runner.WorkingDirectories.Count);
        Assert.Contains(Path.GetDirectoryName("/repo/pkg-a/package-lock.json"), runner.WorkingDirectories);
        Assert.Contains(Path.GetDirectoryName("/repo/pkg-b/package-lock.json"), runner.WorkingDirectories);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("/repo/pkg-a/package-lock.json", issue.FilePath);
    }

    [Fact]
    public async Task Analyze_ReturnsEmpty_WhenDisableEnvironmentVariableIsSet()
    {
        var runner = new FakeRunner((0, "", ""));

        Environment.SetEnvironmentVariable(NpmAuditAnalyzer.DisableEnvironmentVariable, "1");
        try
        {
            var result = await new NpmAuditAnalyzer(runner).AnalyzeAsync("/repo", new[] { "/repo/package-lock.json" }, KodumaConfig.Default, CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Empty(result.Issues);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NpmAuditAnalyzer.DisableEnvironmentVariable, null);
        }
    }
}
