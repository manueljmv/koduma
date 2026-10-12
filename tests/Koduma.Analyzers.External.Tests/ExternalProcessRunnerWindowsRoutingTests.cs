using Koduma.Analyzers.External;
using Xunit;

public class ExternalProcessRunnerWindowsRoutingTests
{
    [Theory]
    [InlineData("eslint.cmd", true)]
    [InlineData("ESLINT.CMD", true)]
    [InlineData("tool.bat", true)]
    [InlineData("eslint", false)]
    public void IsCmdShim_DetectsCmdAndBatFiles(string executable, bool expected)
    {
        Assert.Equal(expected, ExternalProcessRunner.IsCmdShim(executable));
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a^b")]
    [InlineData("a%b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a(b")]
    [InlineData("a)b")]
    [InlineData("a\"b")]
    [InlineData("a!b")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public void FindUnsafeArgument_ReturnsArgumentContainingDenylistedCharacter(string unsafeArg)
    {
        var arguments = new[] { "--format", "json", unsafeArg };

        Assert.Equal(unsafeArg, ExternalProcessRunner.FindUnsafeArgument(arguments));
    }

    [Fact]
    public void FindUnsafeArgument_ReturnsNull_WhenAllArgumentsAreSafe()
    {
        var arguments = new[] { "--format", "json", "file.js" };

        Assert.Null(ExternalProcessRunner.FindUnsafeArgument(arguments));
    }

    [Fact]
    public void RerouteThroughCmd_WrapsExecutableAndArgumentsInCmdCommandLine()
    {
        var (executable, arguments) = ExternalProcessRunner.RerouteThroughCmd("eslint.cmd", new[] { "--format", "json" });

        Assert.EndsWith("cmd.exe", executable);
        Assert.Equal(new[] { "/d", "/s", "/c", "\"\"eslint.cmd\" \"--format\" \"json\"\"" }, arguments);
    }

    [Fact]
    public void ResolveCmdExecutable_FallsBackToSystemCmdExe_WhenComSpecUnset()
    {
        var original = Environment.GetEnvironmentVariable("ComSpec");
        try
        {
            Environment.SetEnvironmentVariable("ComSpec", null);

            var resolved = ExternalProcessRunner.ResolveCmdExecutable();

            Assert.False(string.IsNullOrEmpty(resolved));
            Assert.EndsWith("cmd.exe", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ComSpec", original);
        }
    }

    [Fact]
    public void ResolveCmdExecutable_FallsBackToSystemCmdExe_WhenComSpecEmpty()
    {
        var original = Environment.GetEnvironmentVariable("ComSpec");
        try
        {
            Environment.SetEnvironmentVariable("ComSpec", "");

            var resolved = ExternalProcessRunner.ResolveCmdExecutable();

            Assert.False(string.IsNullOrEmpty(resolved));
            Assert.EndsWith("cmd.exe", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ComSpec", original);
        }
    }

    [Fact]
    public void ResolveCmdExecutable_ReturnsComSpecValueExactly_WhenSet()
    {
        var original = Environment.GetEnvironmentVariable("ComSpec");
        try
        {
            Environment.SetEnvironmentVariable("ComSpec", @"X:\sentinel\cmd.exe");

            Assert.Equal(@"X:\sentinel\cmd.exe", ExternalProcessRunner.ResolveCmdExecutable());

            var (executable, _) = ExternalProcessRunner.RerouteThroughCmd("eslint.cmd", new[] { "--format", "json" });
            Assert.Equal(@"X:\sentinel\cmd.exe", executable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ComSpec", original);
        }
    }

    [Fact]
    public void CheckCommandLineLength_ReturnsNull_WhenCommandLineWithinLimit()
    {
        var arguments = new[] { "--format", "json" };
        var (_, shellArgs) = ExternalProcessRunner.RerouteThroughCmd("eslint.cmd", arguments);
        var fullArguments = string.Join(" ", shellArgs);

        var error = ExternalProcessRunner.CheckCommandLineLength(arguments, fullArguments);

        Assert.Null(error);
    }

    [Fact]
    public void CheckCommandLineLength_ReturnsError_WhenCommandLineExceedsCmdLimit()
    {
        var arguments = Enumerable.Range(0, 200)
            .Select(i => $"C:\\repo\\src\\some\\deeply\\nested\\folder\\file-{i}-{new string('a', 40)}.js")
            .ToArray();
        var (_, shellArgs) = ExternalProcessRunner.RerouteThroughCmd("eslint.cmd", arguments);
        var fullArguments = string.Join(" ", shellArgs);
        Assert.True(fullArguments.Length > ExternalProcessRunner.CmdCommandLineLimit);

        var error = ExternalProcessRunner.CheckCommandLineLength(arguments, fullArguments);

        Assert.NotNull(error);
        Assert.Contains("8191", error);
        Assert.Contains("200", error);
    }
}
