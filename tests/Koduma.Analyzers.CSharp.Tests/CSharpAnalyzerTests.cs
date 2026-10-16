using Koduma.Analyzers.CSharp;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class CSharpAnalyzerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "koduma-cs-" + Guid.NewGuid() + ".cs");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task AnalyzeAsync_FlagsBadFieldNaming_AndHighComplexityMethod()
    {
        File.WriteAllText(_file, """
        public class Example
        {
            private int badlyNamedField;

            public void Complex(int x)
            {
                if (x == 1) { }
                else if (x == 2) { }
                else if (x == 3) { }
                else if (x == 4) { }
                else if (x == 5) { }
                else if (x == 6) { }
                else if (x == 7) { }
                else if (x == 8) { }
                else if (x == 9) { }
                else if (x == 10) { }
                else if (x == 11) { }
            }
        }
        """);

        var analyzer = new CSharpAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        Assert.Contains(result.Issues, i => i.RuleId == "CS-NAMING-001" && i.Message.Contains("badlyNamedField"));
        Assert.Contains(result.Issues, i => i.RuleId == "CS-COMPLEXITY-001" && i.Message.Contains("Complex"));
        Assert.True(result.Metrics.LinesOfCode > 0);
        Assert.True(result.Metrics.CyclomaticComplexity >= 11);
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsEmptyCatchBlock_ButNotOneWithAStatement()
    {
        File.WriteAllText(_file, """
        public class Example
        {
            public void M()
            {
                try { DoWork(); } catch (Exception) { }
                try { DoWork(); } catch (Exception) { Log(); }
            }
            private void DoWork() { }
            private void Log() { }
        }
        """);

        var analyzer = new CSharpAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        var emptyCatchIssues = result.Issues.Where(i => i.RuleId == "CS-DESIGN-001").ToList();
        Assert.Single(emptyCatchIssues);
        Assert.Equal(5, emptyCatchIssues[0].Line);
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsMethodWithEightParameters_ButNotSeven()
    {
        File.WriteAllText(_file, """
        public class Example
        {
            public void Seven(int a, int b, int c, int d, int e, int f, int g) { }
            public void Eight(int a, int b, int c, int d, int e, int f, int g, int h) { }
        }
        """);

        var analyzer = new CSharpAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        var issues = result.Issues.Where(i => i.RuleId == "CS-DESIGN-002").ToList();
        Assert.Single(issues);
        Assert.Contains("Eight", issues[0].Message);
    }
}
