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
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
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
        Assert.Contains("-nodeReuse:false", arguments);
        Assert.Contains("-p:EnableNETAnalyzers=true", arguments);
        Assert.Contains("-p:TreatWarningsAsErrors=false", arguments);
        Assert.Equal(_root, runner.LastWorkingDirectory);
    }

    private const string LegacyProject = """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"></Project>""";
    private const string SdkProject = """<Project Sdk="Microsoft.NET.Sdk"></Project>""";

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"></Project>""", true)]
    [InlineData("""<Project><Sdk Name="Microsoft.NET.Sdk" /></Project>""", true)]
    [InlineData("""<Project><Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" /></Project>""", true)]
    [InlineData("""<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"></Project>""", false)]
    [InlineData("not xml", true)]
    public void IsSdkStyleProject_DetectsSdkReference(string content, bool expected)
    {
        Assert.Equal(expected, MSBuildProjects.IsSdkStyleProject(Write("P.csproj", content)));
    }

    [Fact]
    public void ContainsNonSdkProject_LooksInsideSolutions()
    {
        Write("Legacy/Legacy.csproj", LegacyProject);
        Write("Modern/Modern.csproj", SdkProject);
        var solution = Write("App.sln", """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Legacy", "Legacy\Legacy.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Modern", "Modern\Modern.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        var modernOnly = Write("Modern.slnx", """<Solution><Project Path="Modern/Modern.csproj" /></Solution>""");

        Assert.True(MSBuildProjects.ContainsNonSdkProject(solution));
        Assert.False(MSBuildProjects.ContainsNonSdkProject(modernOnly));
        Assert.Equal(2, MSBuildProjects.GetProjectFiles(solution).Count);
    }

    private sealed class FakeMSBuildRunner : ExternalProcessRunner
    {
        public const string MSBuildPath = @"C:\VS\MSBuild\Current\Bin\MSBuild.exe";
        private readonly string _vswhereOutput;

        public FakeMSBuildRunner(string vswhereOutput) => _vswhereOutput = vswhereOutput;

        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public override Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Calls.Add((executable, arguments));
            if (executable.EndsWith("vswhere.exe", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult((0, _vswhereOutput, string.Empty));

            return Task.FromResult((0, string.Empty, string.Empty));
        }
    }

    [Fact]
    public async Task Analyze_BuildsNonSdkProjectWithVisualStudioMSBuild_OnWindows()
    {
        var source = Write("src/Program.cs");
        var project = Write("src/Legacy.csproj", LegacyProject);
        var runner = new FakeMSBuildRunner(FakeMSBuildRunner.MSBuildPath + "\r\n");

        await new DotnetBuildAnalyzer(runner).AnalyzeAsync(_root, new[] { source }, KodumaConfig.Default, CancellationToken.None);

        var build = runner.Calls.Last();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("dotnet", build.Executable);
            return;
        }

        Assert.Equal(FakeMSBuildRunner.MSBuildPath, build.Executable);
        Assert.Equal(project, build.Arguments[0]);
        Assert.Contains("-t:Rebuild", build.Arguments);
        Assert.Contains("-nodeReuse:false", build.Arguments);
        Assert.Contains(build.Arguments, a => a.StartsWith("-p:CustomBeforeMicrosoftCommonTargets=", StringComparison.Ordinal));
        Assert.Contains("-p:TreatWarningsAsErrors=false", build.Arguments);
    }

    [Fact]
    public async Task Analyze_ReportsError_WhenNonSdkProjectNeedsMSBuildButVisualStudioIsMissing_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var source = Write("src/Program.cs");
        Write("src/Legacy.csproj", LegacyProject);
        var runner = new FakeMSBuildRunner(string.Empty);

        var result = await new DotnetBuildAnalyzer(runner).AnalyzeAsync(_root, new[] { source }, KodumaConfig.Default, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("Visual Studio MSBuild", result.Error);
        Assert.Single(runner.Calls);
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
