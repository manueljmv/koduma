using Koduma.Analyzers.External;
using Koduma.Core.Model;
using Xunit;

public class EslintOutputMapperTests
{
    [Fact]
    public void Map_TranslatesEslintJsonToIssues()
    {
        const string eslintJson = """
        [
          {
            "filePath": "/repo/app.js",
            "messages": [
              { "ruleId": "no-unused-vars", "severity": 2, "message": "unused var", "line": 3, "column": 5, "endLine": 3, "endColumn": 10 },
              { "ruleId": "no-console", "severity": 1, "message": "no console", "line": 7, "column": 1, "endLine": 7, "endColumn": 8 }
            ]
          }
        ]
        """;

        var issues = EslintOutputMapper.Map(eslintJson);

        Assert.Equal(2, issues.Count);
        Assert.Equal(Severity.Major, issues[0].Severity);
        Assert.Equal(Severity.Minor, issues[1].Severity);
        Assert.Equal("javascript", issues[0].Language);
        Assert.Equal("/repo/app.js", issues[0].FilePath);
    }
}
