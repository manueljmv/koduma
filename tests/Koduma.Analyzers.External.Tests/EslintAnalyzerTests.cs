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

    private sealed class RecordingRunner : ExternalProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Calls.Add(arguments);
            var file = arguments[2];
            var json = $$"""[{"filePath":{{System.Text.Json.JsonSerializer.Serialize(file)}},"messages":[{"ruleId":"no-unused-vars","severity":2,"message":"m","line":1,"column":1}]}]""";
            return Task.FromResult((1, json, string.Empty));
        }
    }

    [Fact]
    public async Task Analyze_MergesResultsOfEveryBatch_WhenFilesExceedCmdLimitOnWindows()
    {
        var files = Enumerable.Range(0, 500).Select(i => $"C:\\repo\\src\\some\\deeply\\nested\\folder\\file-{i}-{new string('a', 40)}.js").ToArray();
        var runner = new RecordingRunner();

        var result = await new EslintAnalyzer(runner).AnalyzeAsync("C:\\repo", files, KodumaConfig.Default, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(files, runner.Calls.SelectMany(c => c.Skip(2)));
        Assert.Equal(runner.Calls.Count, result.Issues.Count);
        Assert.Equal(files.Length, result.Metrics.FilesAnalyzed);
        if (OperatingSystem.IsWindows())
            Assert.True(runner.Calls.Count > 1);
        else
            Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(true, "eslint.cmd")]
    [InlineData(false, "eslint")]
    public void ResolveEslintExecutable_PicksCmdShimOnWindowsOnly(bool isWindows, string expected)
    {
        Assert.Equal(expected, EslintAnalyzer.ResolveEslintExecutable(isWindows));
    }
}
