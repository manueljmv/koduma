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

        Assert.Equal("/repo/pkg", runner.LastWorkingDirectory);
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
