using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Xunit;

// These tests create real symlinks; on Windows this requires Developer Mode or admin rights.
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
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Append(_root))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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

    [Fact]
    public void DiscoverFiles_SkipsInaccessibleDirectory_AndReturnsReadableFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P {}");
        File.WriteAllText(Path.Combine(_root, ".hidden.cs"), "class H {}");
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "inside.cs"), "class L {}");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var lockedIsReadable = true;
        try { Directory.EnumerateFileSystemEntries(locked).ToList(); }
        catch (UnauthorizedAccessException) { lockedIsReadable = false; }
        if (lockedIsReadable)
            return; // Running as root: permissions cannot be enforced, nothing to test.

        var files = new FileDiscovery().DiscoverFiles(_root, KodumaConfig.Default);

        var relative = files.Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).ToHashSet();
        Assert.Contains("Program.cs", relative);
        Assert.Contains(".hidden.cs", relative);
        Assert.DoesNotContain("locked/inside.cs", relative);
    }

    private HashSet<string> DiscoverRelative() =>
        new FileDiscovery().DiscoverFiles(_root, KodumaConfig.Default)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .ToHashSet();

    [Fact]
    public void DiscoverFiles_GitignoreFileGlob_ExcludesMatchesAtAnyDepth()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_root, "app.log"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "deep.log"), "x");
        File.WriteAllText(Path.Combine(_root, "sub", "keep.cs"), "class K {}");

        var relative = DiscoverRelative();

        Assert.DoesNotContain("app.log", relative);
        Assert.DoesNotContain("sub/deep.log", relative);
        Assert.Contains("sub/keep.cs", relative);
    }

    [Fact]
    public void DiscoverFiles_GitignoreRootAnchoredPattern_ExcludesOnlyTopLevel()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "/secrets\n/private\n");
        File.WriteAllText(Path.Combine(_root, "secrets"), "top-level secret file");
        Directory.CreateDirectory(Path.Combine(_root, "private"));
        File.WriteAllText(Path.Combine(_root, "private", "key.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "secrets"), "nested file");

        var relative = DiscoverRelative();

        Assert.DoesNotContain("secrets", relative);
        Assert.DoesNotContain("private/key.txt", relative);
        Assert.Contains("sub/secrets", relative);
    }

    [Fact]
    public void DiscoverFiles_GitignoreTrailingSlash_ExcludesDirectoryAtAnyDepth_ButNotSameNamedFile()
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(_root, "src", "bin"));
        File.WriteAllText(Path.Combine(_root, "src", "bin", "out.cs"), "class O {}");
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        File.WriteAllText(Path.Combine(_root, "other", "bin"), "a file named bin");

        var relative = DiscoverRelative();

        Assert.DoesNotContain("src/bin/out.cs", relative);
        Assert.Contains("other/bin", relative);
    }
}
