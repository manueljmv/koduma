using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

public class EndToEndAnalyzeTests : IDisposable
{
    private readonly string _repoRoot = FindRepoRoot();
    private readonly string _publishDir;
    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), "koduma-e2e-" + Guid.NewGuid());

    public EndToEndAnalyzeTests()
    {
        _publishDir = Path.Combine(_repoRoot, "src", "Koduma.Cli", "bin", "e2e-publish");
        Directory.CreateDirectory(_fixtureRoot);

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

        var publish = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{Path.Combine(_repoRoot, "src", "Koduma.Cli", "Koduma.Cli.csproj")}\" -c Release -r {rid} --self-contained -o \"{_publishDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        publish.WaitForExit();
        if (publish.ExitCode != 0)
            throw new InvalidOperationException("publish failed: " + publish.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        Directory.Delete(_fixtureRoot, recursive: true);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "koduma.sln")))
            dir = Directory.GetParent(dir)!.FullName;
        return dir;
    }

    [Fact]
    public async Task Analyze_EmptyRepo_ExitsZero_AndProducesValidReportsInEveryFormat()
    {
        var (exitCode, stdOut, stdErr) = await RunKoduma("analyze", _fixtureRoot, "--format", "table,json,html,sarif");

        Assert.Equal(0, exitCode);
        Assert.Contains("0 issues", stdOut);
        Assert.Contains("\"issues\"", stdOut.ToLowerInvariant());
        Assert.Contains("<!DOCTYPE html>", stdOut);
        Assert.Contains("\"version\": \"2.1.0\"", stdOut);
        Assert.Empty(stdErr);
    }

    [Fact]
    public async Task Analyze_RepoWithSecretAndBadCSharpNaming_ExitsOne_AndReportsBothIssues()
    {
        File.WriteAllText(Path.Combine(_fixtureRoot, "Example.cs"), """
        public class Example
        {
            private int badlyNamedField;
        }
        """);
        File.WriteAllText(Path.Combine(_fixtureRoot, "secret.txt"), "password = \"SuperSecret123!\"");

        var (exitCode, stdOut, _) = await RunKoduma("analyze", _fixtureRoot, "--format", "json");

        Assert.Equal(1, exitCode);
        Assert.Contains("CS-NAMING-001", stdOut);
        Assert.Contains("SEC-SECRET-001", stdOut);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunKoduma(params string[] args)
    {
        var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var binary = Path.Combine(_publishDir, "Koduma.Cli" + exeSuffix);
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stdOut = await process.StandardOutput.ReadToEndAsync();
        var stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdOut, stdErr);
    }
}
