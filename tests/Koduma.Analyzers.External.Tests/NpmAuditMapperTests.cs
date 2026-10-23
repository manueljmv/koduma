using Koduma.Analyzers.External;
using Koduma.Core.Model;
using Xunit;

public class NpmAuditMapperTests
{
    [Fact]
    public void Map_ReturnsOneIssuePerAdvisory_UsingThatAdvisorysOwnSeverity()
    {
        const string json = """
        { "vulnerabilities": { "lodash": { "severity": "critical", "via": [
          { "source": 1106918, "title": "Prototype Pollution in lodash",
            "url": "https://github.com/advisories/GHSA-jf85-cpcp-j695", "severity": "critical" },
          { "source": 1106913, "title": "Command Injection in lodash",
            "url": "https://github.com/advisories/GHSA-35jh-r3h4-6jhm", "severity": "high" }
        ] } } }
        """;

        var issues = NpmAuditMapper.Map(json, "/repo/package-lock.json");

        Assert.Equal(2, issues.Count);
        Assert.Equal("VULN-GHSA-jf85-cpcp-j695", issues[0].RuleId);
        Assert.Equal(Severity.Blocker, issues[0].Severity);   // critical
        Assert.Equal("VULN-GHSA-35jh-r3h4-6jhm", issues[1].RuleId);
        Assert.Equal(Severity.Blocker, issues[1].Severity);   // high
        Assert.All(issues, i => Assert.Equal("/repo/package-lock.json", i.FilePath));
        Assert.All(issues, i => Assert.Equal("javascript", i.Language));
        Assert.All(issues, i => Assert.Equal(Category.Security, i.Category));
    }

    [Fact]
    public void Map_ReturnsNoIssues_ForEmptyVulnerabilitiesMap()
    {
        const string json = """{ "vulnerabilities": {} }""";
        Assert.Empty(NpmAuditMapper.Map(json, "/repo/package-lock.json"));
    }

    [Fact]
    public void Map_SkipsStringViaEntries_AndOnlyMapsAdvisoryObjects()
    {
        const string json = """
        { "vulnerabilities": {
          "pkg-a": { "severity": "high", "via": [ "pkg-b" ] },
          "pkg-b": { "severity": "high", "via": [
            { "source": 1, "title": "Issue in pkg-b", "url": "https://github.com/advisories/GHSA-cccc-cccc-cccc", "severity": "high" }
          ] }
        } }
        """;

        var issues = NpmAuditMapper.Map(json, "/repo/package-lock.json");
        Assert.Single(issues);
    }

    [Fact]
    public void Map_FallsBackToSourceBasedRuleId_WhenUrlHasNoGhsaId()
    {
        const string json = """
        { "vulnerabilities": { "lodash": { "severity": "low", "via": [
          { "source": 42, "title": "Some issue", "url": "https://example.com/x", "severity": "low" }
        ] } } }
        """;

        var issue = Assert.Single(NpmAuditMapper.Map(json, "/repo/package-lock.json"));
        Assert.Equal("VULN-NPM-lodash-42", issue.RuleId);
    }
}
