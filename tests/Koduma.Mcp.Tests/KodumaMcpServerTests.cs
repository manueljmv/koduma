using System.Text.Json;
using Koduma.Analyzers.Secrets;
using Koduma.Core.Analysis;
using Koduma.Mcp;
using Koduma.Reporting;
using Xunit;

// These tests create real symlinks; on Windows this requires Developer Mode or admin rights.
public class KodumaMcpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-mcp-" + Guid.NewGuid());
    private readonly List<string> _extraTempDirs = new();
    private readonly List<string> _extraSymlinks = new();

    public KodumaMcpServerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        foreach (var dir in _extraTempDirs)
            Directory.Delete(dir, recursive: true);
        foreach (var symlink in _extraSymlinks)
            File.Delete(symlink);
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
    public void ResolveAndValidatePath_Throws_WhenSymlinkIsAnIntermediatePathComponent()
    {
        var outside = Path.Combine(Path.GetTempPath(), "koduma-mcp-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(outside);
        _extraTempDirs.Add(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");

        var linkPath = Path.Combine(_root, "escape-link");
        Directory.CreateSymbolicLink(linkPath, outside);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath(Path.Combine("escape-link", "secret.txt"), _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    [Fact]
    public void ResolveAndValidatePath_Allows_DefaultPath_WhenRootItselfIsASymlink()
    {
        var realRootTarget = Path.Combine(Path.GetTempPath(), "koduma-mcp-realroot-" + Guid.NewGuid());
        Directory.CreateDirectory(realRootTarget);
        _extraTempDirs.Add(realRootTarget);

        var symlinkedRoot = Path.Combine(Path.GetTempPath(), "koduma-mcp-symlinked-root-" + Guid.NewGuid());
        Directory.CreateSymbolicLink(symlinkedRoot, realRootTarget);
        _extraSymlinks.Add(symlinkedRoot);

        var resolved = KodumaMcpServer.ResolveAndValidatePath(".", symlinkedRoot);

        Assert.Equal(Path.GetFullPath(symlinkedRoot), resolved);
    }

    [Fact]
    public void ResolveAndValidatePath_Throws_WhenChainedSymlinksEscapeRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "koduma-mcp-outside-" + Guid.NewGuid());
        var outsideSubdirectory = Path.Combine(outside, "x");
        Directory.CreateDirectory(outsideSubdirectory);
        _extraTempDirs.Add(outside);
        File.WriteAllText(Path.Combine(outsideSubdirectory, "secret.txt"), "x");

        // l2 -> /outside (absolute, escapes root)
        var l2 = Path.Combine(_root, "l2");
        Directory.CreateSymbolicLink(l2, outside);

        // l1 -> l2/x (relative, resolves through l2 which is itself a symlink)
        var l1 = Path.Combine(_root, "l1");
        Directory.CreateSymbolicLink(l1, Path.Combine("l2", "x"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath(Path.Combine("l1", "secret.txt"), _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    private string CreateOutsideWithDeepAndSecret()
    {
        var outside = Path.Combine(Path.GetTempPath(), "koduma-mcp-outside-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(outside, "deep"));
        Directory.CreateDirectory(Path.Combine(outside, "y"));
        File.WriteAllText(Path.Combine(outside, "y", "secret.txt"), "x");
        _extraTempDirs.Add(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "l2"), Path.Combine(outside, "deep"));
        return outside;
    }

    [Fact]
    public void ResolveAndValidatePath_Throws_WhenDirectorySymlinkTargetDotDotFollowsAnotherSymlink()
    {
        CreateOutsideWithDeepAndSecret();
        Directory.CreateSymbolicLink(Path.Combine(_root, "a"), "l2/../y");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath(Path.Combine("a", "secret.txt"), _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    [Fact]
    public void ResolveAndValidatePath_Throws_WhenFileSymlinkTargetDotDotFollowsAnotherSymlink()
    {
        CreateOutsideWithDeepAndSecret();
        File.CreateSymbolicLink(Path.Combine(_root, "lf"), "l2/../y/secret.txt");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            KodumaMcpServer.ResolveAndValidatePath("lf", _root));

        Assert.Contains("outside the configured root", ex.Message);
    }

    [Fact]
    public void ResolveAndValidatePath_Allows_RelativeSymlinkWithDotDotStayingWithinRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "target.txt"), "x");
        File.CreateSymbolicLink(Path.Combine(_root, "link"), "sub/../sub/target.txt");

        var resolved = KodumaMcpServer.ResolveAndValidatePath("link", _root);

        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "link"), resolved);
    }

    [Fact]
    public void ResolveAndValidatePath_Allows_SubdirectoryPath_WhenRootIsFilesystemRoot()
    {
        var filesystemRoot = Path.GetPathRoot(_root)!;

        var resolved = KodumaMcpServer.ResolveAndValidatePath(_root, filesystemRoot);

        Assert.Equal(Path.GetFullPath(_root), resolved);
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

    [Fact]
    public void IsWithinRoot_CaseSensitivityMatchesPlatform()
    {
        var result = KodumaMcpServer.IsWithinRoot("/ROOT/sub", "/root");

        Assert.Equal(OperatingSystem.IsWindows(), result);
    }
}
