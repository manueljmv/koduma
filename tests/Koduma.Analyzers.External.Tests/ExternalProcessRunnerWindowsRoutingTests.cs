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

        Assert.Equal("cmd.exe", executable);
        Assert.Equal(new[] { "/d", "/s", "/c", "\"\"eslint.cmd\" \"--format\" \"json\"\"" }, arguments);
    }
}
