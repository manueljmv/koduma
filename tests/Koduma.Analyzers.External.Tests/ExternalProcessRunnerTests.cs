using Koduma.Analyzers.External;
using Xunit;

public class ExternalProcessRunnerTests
{
    [Fact]
    public async Task Run_PassesArgumentsWithSpacesAndNonAsciiCharacters_Verbatim()
    {
        if (OperatingSystem.IsWindows())
            return;

        var weirdPath = "/tmp/weird name/spéciäl.js";

        var runner = new ExternalProcessRunner();
        var (exitCode, stdOut, _) = await runner.RunAsync("echo", new[] { weirdPath });

        Assert.Equal(0, exitCode);
        Assert.Contains(weirdPath, stdOut);
    }

    [Fact]
    public async Task Run_ReturnsNonZeroExitAndStdErr_WhenExecutableDoesNotExist()
    {
        var runner = new ExternalProcessRunner();

        var (exitCode, _, _) = await runner.RunAsync("koduma-nonexistent-tool-xyz", new[] { "arg" });

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Run_ReportsCouldNotRun_WhenCmdShimDoesNotExist()
    {
        var runner = new ExternalProcessRunner();

        // Through cmd.exe a missing command exits 1, which callers like ESLint would read as "found issues".
        var (exitCode, _, stdErr) = await runner.RunAsync("koduma-nonexistent-tool-xyz.cmd", new[] { "arg" });

        Assert.Equal(-1, exitCode);
        if (OperatingSystem.IsWindows())
            Assert.Contains("not found on PATH", stdErr);
    }

    [Fact]
    public async Task BatchForCmd_BatchesRunThroughAShimThatReexpandsItsArguments()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("koduma-shim-").FullName;
        try
        {
            // Like npm's shims, the batch file expands %* again inside its own line, which shares cmd.exe's 8191-character limit.
            var shim = Path.Combine(directory, "fake-tool.cmd");
            File.WriteAllText(shim, "@echo off\r\necho %* > \"%~dp0args.txt\"\r\n");
            var files = Enumerable.Range(0, 500).Select(i => $"C:\\repo\\src\\some\\deeply\\nested\\folder\\file-{i}-{new string('a', 40)}.js").ToArray();
            var runner = new ExternalProcessRunner();

            foreach (var batch in ExternalProcessRunner.BatchForCmd(shim, new[] { "--format", "json" }, files))
            {
                var (exitCode, _, stdErr) = await runner.RunAsync(shim, batch, directory);

                Assert.True(exitCode == 0, stdErr);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Run_UsesGivenWorkingDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("koduma-cwd-").FullName;
        try
        {
            var runner = new ExternalProcessRunner();

            var (exitCode, stdOut, _) = await runner.RunAsync("pwd", Array.Empty<string>(), directory);

            Assert.Equal(0, exitCode);
            Assert.Equal(directory, stdOut.Trim());
            Assert.NotEqual(Environment.CurrentDirectory, stdOut.Trim());
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}
