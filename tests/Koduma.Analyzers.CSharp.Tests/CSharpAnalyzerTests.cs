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
}
