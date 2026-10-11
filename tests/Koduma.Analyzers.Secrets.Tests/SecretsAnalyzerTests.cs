using Koduma.Analyzers.Secrets;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class SecretsAnalyzerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "koduma-secret-" + Guid.NewGuid() + ".txt");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public async Task AnalyzeAsync_FlagsHardcodedAwsKeyAndPassword_ButNotOrdinaryCode()
    {
        File.WriteAllText(_file, """
        var ordinary = "hello world";
        var key = "AKIAABCDEFGHIJKLMNOP";
        var password = "SuperSecret123!";
        """);

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, i => Assert.Equal("SEC-SECRET-001", i.RuleId));
        Assert.All(result.Issues, i => Assert.Equal(Category.Security, i.Category));
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsStringConcatenatedAndInterpolatedSql_ButNotFixedQueries()
    {
        File.WriteAllText(_file, """
        var ok = "SELECT fixed_value FROM t";
        var risky1 = "SELECT * FROM users WHERE id = " + userId;
        var risky2 = `SELECT * FROM t WHERE x = ${x}`;
        """);

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        var sqliIssues = result.Issues.Where(i => i.RuleId == "SEC-SQLI-001").ToList();
        Assert.Equal(2, sqliIssues.Count);
        Assert.All(sqliIssues, i => Assert.Equal(Category.Security, i.Category));
        Assert.Contains(sqliIssues, i => i.Line == 2);
        Assert.Contains(sqliIssues, i => i.Line == 3);
    }
}
