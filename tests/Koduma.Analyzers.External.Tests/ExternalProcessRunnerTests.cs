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
