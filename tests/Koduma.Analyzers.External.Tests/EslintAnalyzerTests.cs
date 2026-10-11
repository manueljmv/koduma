using Koduma.Analyzers.External;
using Koduma.Core.Config;
using Xunit;

public class EslintAnalyzerTests
{
    private sealed class FakeRunner : ExternalProcessRunner
    {
        private readonly (int ExitCode, string StdOut, string StdErr) _result;

        public FakeRunner((int ExitCode, string StdOut, string StdErr) result) => _result = result;

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments)
            => Task.FromResult(_result);
    }

    private static Task<Koduma.Core.Analysis.LanguageAnalysisResult> AnalyzeWith((int, string, string) result)
        => new EslintAnalyzer(new FakeRunner(result)).AnalyzeAsync(new[] { "/repo/app.js" }, KodumaConfig.Default, CancellationToken.None);

    [Fact]
    public async Task Analyze_ReportsError_WhenEslintExitsWithFatalCode()
    {
        var result = await AnalyzeWith((2, string.Empty, "Oops! Something went wrong"));

        Assert.NotNull(result.Error);
        Assert.Contains("exited with code 2", result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsNoError_WhenEslintExitsOneWithEmptyOutput()
    {
        var result = await AnalyzeWith((1, string.Empty, string.Empty));

        Assert.Null(result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Analyze_ReportsNoError_WhenEslintExitsZeroWithEmptyOutput()
    {
        var result = await AnalyzeWith((0, string.Empty, string.Empty));

        Assert.Null(result.Error);
        Assert.Empty(result.Issues);
    }
}
