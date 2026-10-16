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

    [Fact]
    public async Task AnalyzeAsync_DoesNotFlagOrdinaryUiStringsContainingSqlVerbs()
    {
        File.WriteAllText(_file, """
        var confirm = "Delete " + item.Name + "?";
        var cmd = `update ${pkg}`;
        var prompt = "Select a file: " + path;
        """);

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        Assert.DoesNotContain(result.Issues, i => i.RuleId == "SEC-SQLI-001");
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsAllEightNewBuiltInSecretPatterns()
    {
        File.WriteAllText(_file, """
        var gh = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var slack = "xoxb-111111111111-222222222222-abcdefghijklmnopqrstuvwx";
        var google = "AIzaSyAbcdefghijklmnopqrstuvwxyz0123456";
        var stripe = "sk_live_abcdefghijklmnopqrstuvwx";
        var pk = "-----BEGIN RSA PRIVATE KEY-----";
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dGVzdHNpZ25hdHVyZXZhbHVl";
        var url = "https://user:pass@example.com/path";
        var azure = "AccountKey=abcdefghijklmnopqrstABCDEFGHIJKLMNOP==";
        """);

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, KodumaConfig.Default, CancellationToken.None);

        var ruleIds = result.Issues.Select(i => i.RuleId).ToHashSet();
        foreach (var expected in new[] { "SEC-SECRET-002", "SEC-SECRET-003", "SEC-SECRET-004", "SEC-SECRET-005", "SEC-SECRET-006", "SEC-SECRET-007", "SEC-SECRET-008", "SEC-SECRET-009" })
            Assert.Contains(expected, ruleIds);
        Assert.All(result.Issues, i => Assert.Equal(Category.Security, i.Category));
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsCustomPattern_WithConfiguredSeverityAndMessage()
    {
        File.WriteAllText(_file, """var t = "ITOK-ABCDEF01";""");
        var config = KodumaConfig.Default with
        {
            CustomPatterns = new List<CustomPatternRule>
            {
                new("SEC-CUSTOM-001", "Internal token", "ITOK-[0-9A-F]{8}", Severity.Major, "Internal token leaked.")
            }
        };

        var analyzer = new SecretsAnalyzer();
        var result = await analyzer.AnalyzeAsync(Path.GetDirectoryName(_file)!, new[] { _file }, config, CancellationToken.None);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("SEC-CUSTOM-001", issue.RuleId);
        Assert.Equal(Severity.Major, issue.Severity);
        Assert.Equal("Internal token leaked.", issue.Message);
        Assert.Equal(Category.Security, issue.Category);
    }
}
