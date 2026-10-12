using System.Text.Json;
using Koduma.Analyzers.Secrets;
using Koduma.Core.Analysis;
using Koduma.Mcp;
using Koduma.Reporting;
using Xunit;

public class KodumaMcpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-mcp-" + Guid.NewGuid());
    private readonly List<string> _extraTempDirs = new();

    public KodumaMcpServerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        foreach (var dir in _extraTempDirs)
            Directory.Delete(dir, recursive: true);
    }

    private static IReadOnlyDictionary<string, JsonElement> Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void ResolveAndValidatePath_Throws_WhenPathEscapesRoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath("../outside", _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    [Fact]
    public void ResolveAndValidatePath_Allows_PathWithinRoot()
    {
        var resolved = KodumaMcpServer.ResolveAndValidatePath(".", _root);

        Assert.Equal(Path.GetFullPath(_root), resolved);
    }

    [Fact]
    public void ResolveAndValidatePath_Throws_WhenSymlinkInsideRootEscapesIt()
    {
        var outside = Path.Combine(Path.GetTempPath(), "koduma-mcp-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        _extraTempDirs.Add(outside);

        var linkPath = Path.Combine(_root, "escape-link");
        Directory.CreateSymbolicLink(linkPath, outside);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath("escape-link", _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    [Fact]
    public void ResolveAndValidatePath_Allows_SubdirectoryPath_WhenRootHasTrailingSeparator()
    {
        var subdirectory = Path.Combine(_root, "subdir");
        Directory.CreateDirectory(subdirectory);
        var rootWithTrailingSeparator = _root + Path.DirectorySeparatorChar;

        var resolved = KodumaMcpServer.ResolveAndValidatePath("subdir", rootWithTrailingSeparator);

        Assert.Equal(Path.GetFullPath(subdirectory), resolved);
    }

    [Fact]
    public async Task HandleAnalyzeProject_UsesRequestedFormat_InsteadOfJson()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "password = \"SuperSecret123!\"");

        ILanguageAnalyzer[] analyzers = { new SecretsAnalyzer() };
        IReportGenerator[] generators = { new TableReportGenerator(), new JsonReportGenerator() };

        var output = await KodumaMcpServer.HandleAnalyzeProject(
            Args($$"""{"path": "{{_root}}", "format": "table"}"""), analyzers, generators, _root, CancellationToken.None);

        Assert.Contains("[Critical] SEC-SECRET-001", output);
        Assert.DoesNotContain("\"Severity\"", output);
    }

    [Fact]
    public async Task HandleAnalyzeProject_AppliesRulesConfigOverride()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "password = \"SuperSecret123!\"");

        ILanguageAnalyzer[] analyzers = { new SecretsAnalyzer() };
        IReportGenerator[] generators = { new JsonReportGenerator() };

        var output = await KodumaMcpServer.HandleAnalyzeProject(
            Args($$$"""{"path": "{{{_root}}}", "rules_config": {"SEC-SECRET-001": "off"}}"""), analyzers, generators, _root, CancellationToken.None);

        using var document = JsonDocument.Parse(output);
        Assert.Equal(0, document.RootElement.GetProperty("Issues").GetArrayLength());
    }
}
