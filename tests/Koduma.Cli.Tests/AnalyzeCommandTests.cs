using Koduma.Analyzers.Secrets;
using Koduma.Cli;
using Koduma.Core.Analysis;
using Koduma.Reporting;
using Xunit;

public class AnalyzeCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-cli-" + Guid.NewGuid());

    public AnalyzeCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ILanguageAnalyzer[] Analyzers() => new ILanguageAnalyzer[] { new SecretsAnalyzer() };
    private static IReportGenerator[] Generators() => new IReportGenerator[] { new TableReportGenerator(), new JsonReportGenerator() };

    [Fact]
    public async Task Run_ReturnsZero_WhenNoBlockerOrCriticalIssues()
    {
        File.WriteAllText(Path.Combine(_root, "ok.txt"), "nothing suspicious here");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_ReturnsOne_WhenBlockerOrCriticalIssuesFound()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "password = \"SuperSecret123!\"");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Run_ReturnsTwo_AndPrintsCleanMessage_WhenConfigIsInvalid()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), "{ not valid");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(new[] { "analyze", _root }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("koduma.json", stderr.ToString());
        Assert.DoesNotContain("Exception", stderr.ToString());
    }

    [Fact]
    public async Task Run_ReturnsTwo_AndPrintsCleanMessage_WhenFormatIsUnrecognized()
    {
        File.WriteAllText(Path.Combine(_root, "ok.txt"), "nothing suspicious here");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await AnalyzeCommand.Run(
            new[] { "analyze", _root, "--format", "bogus" }, Analyzers(), Generators(), stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("bogus", stderr.ToString());
        Assert.DoesNotContain("Exception", stderr.ToString());
    }
}
