using Koduma.Analyzers.External;
using Koduma.Core.Config;
using Xunit;

public class DotnetBuildAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-dotnet-tests-" + Guid.NewGuid());

    public DotnetBuildAnalyzerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Plays the role of 'dotnet build': finds the injected targets file, reads where it wants the error log, and writes a SARIF there.</summary>
    private sealed class FakeDotnetRunner : ExternalProcessRunner
    {
        private readonly int _exitCode;
        private readonly string? _sarif;
        private readonly string _stdErr;

        public FakeDotnetRunner(int exitCode, string? sarif, string stdErr = "")
        {
            _exitCode = exitCode;
            _sarif = sarif;
            _stdErr = stdErr;
        }

        public List<IReadOnlyList<string>> Calls { get; } = new();
        public string? LastWorkingDirectory { get; private set; }

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Assert.Equal("dotnet", executable);
            Calls.Add(arguments);
            LastWorkingDirectory = workingDirectory;

            if (_sarif is not null)
            {
                var targetsFile = arguments.Single(a => a.StartsWith("-p:CustomBeforeMicrosoftCommonTargets=", StringComparison.Ordinal))
                    .Substring("-p:CustomBeforeMicrosoftCommonTargets=".Length);
                var content = File.ReadAllText(targetsFile);
                var start = content.IndexOf("<ErrorLog>", StringComparison.Ordinal) + "<ErrorLog>".Length;
                var directory = content.Substring(start, content.IndexOf("$(MSBuildProjectName)", StringComparison.Ordinal) - start);
                File.WriteAllText(Path.Combine(directory, "Proj-net10.0.sarif"), _sarif);
            }

            return Task.FromResult((_exitCode, string.Empty, _stdErr));
        }
    }

    private static string SarifFor(params (string Path, int Line)[] results)
    {
        var items = string.Join(",", results.Select(r => $$"""
            { "ruleId": "CA1822", "level": "note", "message": { "text": "m" },
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{new Uri(r.Path).AbsoluteUri}}" },
                "region": { "startLine": {{r.Line}}, "startColumn": 1 } } } ] }
            """));
        return $$"""{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "rules": [] } }, "results": [ {{items}} ] } ] }""";
    }

    [Fact]
    public void FindBuildTargets_PrefersTopLevelSolution()
    {
        var solution = Write("App.sln");
        var source = Write("src/App/Program.cs");
        Write("src/App/App.csproj");

        var targets = DotnetBuildAnalyzer.FindBuildTargets(_root, new[] { source });

        Assert.Equal(new[] { solution }, targets);
    }

    [Fact]
    public void FindBuildTargets_FallsBackToNearestProjectOfEachFile_WithoutDuplicates()
    {
        var a1 = Write("A/One.cs");
        var a2 = Write("A/Sub/Two.cs");
        var b1 = Write("B/Three.cs");
        var projectA = Write("A/A.csproj");
        var projectB = Write("B/B.csproj");

        var targets = DotnetBuildAnalyzer.FindBuildTargets(_root, new[] { a1, a2, b1 });

        Assert.Equal(new[] { projectA, projectB }, targets);
    }

    [Fact]
    public async Task Analyze_DoesNothing_WhenThereIsNothingToBuild()
    {
        var source = Write("Loose.cs");
        var runner = new FakeDotnetRunner(0, null);

        var result = await new DotnetBuildAnalyzer(runner).AnalyzeAsync(_root, new[] { source }, KodumaConfig.Default, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Issues);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Analyze_RunsBuildWithAnalyzersEnabled_InProjectDirectory_AndMapsKnownFilesOnly()
    {
        var source = Write("src/Program.cs");
        var generated = Write("obj/Debug/Generated.cs");
        Write("src/App.csproj");
        var runner = new FakeDotnetRunner(1, SarifFor((source, 4), (source, 4), (generated, 1)));

        var result = await new DotnetBuildAnalyzer(runner).AnalyzeAsync(_root, new[] { source }, KodumaConfig.Default, CancellationToken.None);

        Assert.Null(result.Error);   // exit code 1 is normal when the code has findings or errors
        var issue = Assert.Single(result.Issues);   // duplicate collapsed, file outside the discovered set dropped
        Assert.Equal("NET-CA1822", issue.RuleId);
        Assert.Equal(4, issue.Line);

        var arguments = Assert.Single(runner.Calls);
        Assert.Equal("build", arguments[0]);
        Assert.Contains("--no-incremental", arguments);
        Assert.Contains("-p:EnableNETAnalyzers=true", arguments);
        Assert.Contains("-p:TreatWarningsAsErrors=false", arguments);
        Assert.Equal(_root, runner.LastWorkingDirectory);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenDotnetCannotBeRun()
    {
        var source = Write("src/Program.cs");
        Write("src/App.csproj");
        var runner = new FakeDotnetRunner(-1, null, "An error occurred trying to start process 'dotnet'");

        var result = await new DotnetBuildAnalyzer(runner).AnalyzeAsync(_root, new[] { source }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("dotnet build", result.Error);
        Assert.Contains("trying to start process", result.Error);
        Assert.Empty(result.Issues);
    }
}
