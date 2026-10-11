using Koduma.Analyzers.CSharp;
using Koduma.Cli;
using Koduma.Core.Analysis;
using Xunit;

public class RulesCommandTests
{
    [Fact]
    public void Run_ListsEachAnalyzersRules()
    {
        ILanguageAnalyzer[] analyzers = { new CSharpAnalyzer() };
        using var stdout = new StringWriter();

        var exitCode = RulesCommand.Run(analyzers, stdout);

        Assert.Equal(0, exitCode);
        Assert.Contains("CS-NAMING-001", stdout.ToString());
        Assert.Contains("CS-COMPLEXITY-001", stdout.ToString());
    }
}
