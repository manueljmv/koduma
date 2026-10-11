using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Xunit;

public class FileDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-discovery-" + Guid.NewGuid());

    public FileDiscoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void DiscoverFiles_SkipsGitignoredAndExcludedAndBinaryAndOversizedFiles()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P {}");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "ignored.cs"), "class I {}");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(_root, "weird name"));
        File.WriteAllText(Path.Combine(_root, "weird name", "spéciäl.cs"), "class W {}");
        File.WriteAllBytes(Path.Combine(_root, "photo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        File.WriteAllText(Path.Combine(_root, "huge.cs"), new string('x', 11 * 1024 * 1024));

        var config = new KodumaConfig(new(), new List<string> { "**/excluded/**" });
        Directory.CreateDirectory(Path.Combine(_root, "excluded"));
        File.WriteAllText(Path.Combine(_root, "excluded", "skip.cs"), "class S {}");

        var files = new FileDiscovery().DiscoverFiles(_root, config);

        var relative = files.Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).ToHashSet();

        Assert.Contains("Program.cs", relative);
        Assert.Contains("weird name/spéciäl.cs", relative);
        Assert.DoesNotContain("bin/ignored.cs", relative);
        Assert.DoesNotContain("excluded/skip.cs", relative);
        Assert.DoesNotContain("photo.png", relative);
        Assert.DoesNotContain("huge.cs", relative);
    }
}
