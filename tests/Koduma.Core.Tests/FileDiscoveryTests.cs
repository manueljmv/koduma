using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Xunit;

public class FileDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-discovery-" + Guid.NewGuid());
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "koduma-outside-" + Guid.NewGuid());

    public FileDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

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

    [Fact]
    public async Task DiscoverFiles_DoesNotFollowSymlinks()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.cs"), "class Secret {}");
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P {}");
        File.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);

        var discovery = Task.Run(() => new FileDiscovery().DiscoverFiles(_root, KodumaConfig.Default));
        var completed = await Task.WhenAny(discovery, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(discovery, completed);
        var files = await discovery;
        Assert.DoesNotContain(files, f => f.EndsWith("secret.cs", StringComparison.Ordinal));
        Assert.Single(files);
        Assert.EndsWith("Program.cs", files[0], StringComparison.Ordinal);
    }
}
