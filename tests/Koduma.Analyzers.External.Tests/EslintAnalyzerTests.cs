using Koduma.Analyzers.External;
using Koduma.Core.Config;
using Xunit;

public class EslintAnalyzerTests
{
    private sealed class FakeRunner : ExternalProcessRunner
    {
        private readonly (int ExitCode, string StdOut, string StdErr) _result;

        public FakeRunner((int ExitCode, string StdOut, string StdErr) result) => _result = result;

        public string? LastWorkingDirectory { get; private set; }

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            LastWorkingDirectory = workingDirectory;
            return Task.FromResult(_result);
        }
    }

    private static Task<Koduma.Core.Analysis.LanguageAnalysisResult> AnalyzeWith((int, string, string) result)
        => new EslintAnalyzer(new FakeRunner(result)).AnalyzeAsync("/repo", new[] { "/repo/app.js" }, KodumaConfig.Default, CancellationToken.None);

    [Fact]
    public async Task Analyze_ReportsError_WhenEslintExitsWithFatalCode()
    {
        var result = await AnalyzeWith((2, string.Empty, "Oops! Something went wrong"));

        Assert.NotNull(result.Error);
        Assert.Contains("exited with code 2", result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenEslintExitsOneWithEmptyOutput()
    {
        var result = await AnalyzeWith((1, string.Empty, string.Empty));

        Assert.NotNull(result.Error);
        Assert.Contains("no output", result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenEslintExitsZeroWithEmptyOutput()
    {
        var result = await AnalyzeWith((0, string.Empty, string.Empty));

        Assert.NotNull(result.Error);
        Assert.Contains("no output", result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_RunsEslintInProjectDirectory()
    {
        var runner = new FakeRunner((0, string.Empty, string.Empty));

        await new EslintAnalyzer(runner).AnalyzeAsync("/some/project", new[] { "/some/project/app.js" }, KodumaConfig.Default, CancellationToken.None);

        Assert.Equal("/some/project", runner.LastWorkingDirectory);
    }

    [Theory]
    [InlineData(true, "eslint.cmd")]
    [InlineData(false, "eslint")]
    public void ResolveEslintExecutable_PicksCmdShimOnWindowsOnly(bool isWindows, string expected)
    {
        Assert.Equal(expected, EslintAnalyzer.ResolveEslintExecutable(isWindows));
    }
}
