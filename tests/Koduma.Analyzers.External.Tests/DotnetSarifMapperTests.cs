using Koduma.Analyzers.External;
using Koduma.Core.Model;
using Xunit;

public class DotnetSarifMapperTests
{
    private const string Sarif = """
    {
      "version": "2.1.0",
      "runs": [
        {
          "tool": {
            "driver": {
              "name": "Microsoft (R) Visual C# Compiler",
              "rules": [
                { "id": "CA2100", "properties": { "category": "Security" } },
                { "id": "CA2000", "properties": { "category": "Reliability" } },
                { "id": "CA1822", "properties": { "category": "Performance" } }
              ]
            }
          },
          "results": [
            {
              "ruleId": "CA2100", "level": "warning", "message": { "text": "Review SQL queries for security vulnerabilities" },
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///repo/Db.cs" },
                "region": { "startLine": 10, "startColumn": 5, "endLine": 10, "endColumn": 20 } } } ]
            },
            {
              "ruleId": "CA2000", "level": "warning", "message": { "text": "Dispose objects before losing scope" },
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///repo/Db.cs" }, "region": { "startLine": 3, "startColumn": 1 } } } ]
            },
            {
              "ruleId": "CA1822", "level": "note", "message": { "text": "Member does not access instance data" },
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///repo/Util.cs" }, "region": { "startLine": 7, "startColumn": 9 } } } ]
            },
            {
              "ruleId": "CS0103", "level": "error", "message": { "text": "The name 'x' does not exist" },
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///repo/Util.cs" }, "region": { "startLine": 8, "startColumn": 1 } } } ]
            },
            {
              "ruleId": "CA1031", "level": "warning", "message": { "text": "Do not catch general exception types" },
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///repo/Util.cs" }, "region": { "startLine": 9, "startColumn": 1 } } } ]
            },
            {
              "ruleId": "CA1822", "level": "note", "message": { "text": "suppressed" },
              "suppressions": [ { "kind": "inSource" } ],
              "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///repo/Util.cs" }, "region": { "startLine": 20, "startColumn": 1 } } } ]
            },
            { "ruleId": "CA1014", "level": "note", "message": { "text": "Mark assemblies with CLSCompliant" } }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Map_NormalizesRuleIdSeverityCategoryAndPosition()
    {
        var issues = DotnetSarifMapper.Map(Sarif);

        var security = Assert.Single(issues, i => i.RuleId == "NET-CA2100");
        Assert.Equal(Severity.Major, security.Severity);
        Assert.Equal(Category.Security, security.Category);
        Assert.Equal(10, security.Line);
        Assert.Equal(5, security.Column);
        Assert.Equal(10, security.EndLine);
        Assert.Equal(20, security.EndColumn);
        Assert.Equal("csharp", security.Language);
        Assert.EndsWith("Db.cs", security.FilePath);

        Assert.Equal(Category.Bug, Assert.Single(issues, i => i.RuleId == "NET-CA2000").Category);

        var note = Assert.Single(issues, i => i.RuleId == "NET-CA1822" && i.Line == 7);
        Assert.Equal(Severity.Minor, note.Severity);
        Assert.Equal(Category.CodeSmell, note.Category);
        Assert.Equal(7, note.EndLine);   // missing end position falls back to the start

        var compilerError = Assert.Single(issues, i => i.RuleId == "NET-CS0103");
        Assert.Equal(Severity.Critical, compilerError.Severity);
        Assert.Equal(Category.Bug, compilerError.Category);
    }

    [Fact]
    public void Map_SkipsNativelyCoveredSuppressedAndLocationlessResults()
    {
        var issues = DotnetSarifMapper.Map(Sarif);

        Assert.DoesNotContain(issues, i => i.RuleId == "NET-CA1031");   // covered by CS-DESIGN-004
        Assert.DoesNotContain(issues, i => i.RuleId == "NET-CA1014");   // no source location
        Assert.DoesNotContain(issues, i => i.RuleId == "NET-CA1822" && i.Line == 20);   // suppressed in source
        Assert.Equal(4, issues.Count);
    }

    [Fact]
    public void Map_ReturnsNothing_WhenThereAreNoRuns()
    {
        Assert.Empty(DotnetSarifMapper.Map("""{ "version": "2.1.0" }"""));
    }
}
