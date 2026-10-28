using System.Text.RegularExpressions;
using Koduma.Core.Config;

namespace Koduma.Core.Discovery;

public class FileDiscovery : IFileDiscovery
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".pdf", ".zip", ".dll", ".exe", ".so", ".dylib"
    };

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    // A glob '[...]' / '[!...]' after Regex.Escape; an unclosed '[' stays a literal.
    private static readonly Regex CharacterClass = new(@"\\\[(!?)([^/\]]+)\]", RegexOptions.Compiled);

    private sealed record IgnoreScope(string BaseDirectory, List<Regex> Patterns);

    public IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config)
    {
        var gitignorePatterns = LoadIgnorePatterns(Path.Combine(rootPath, ".gitignore"));
        var kodumaIgnorePatterns = LoadIgnorePatterns(Path.Combine(rootPath, ".kodumaignore"));
        var allPatterns = gitignorePatterns.Concat(kodumaIgnorePatterns).Concat(config.Exclude).ToList();
        var scopes = new List<IgnoreScope> { new(rootPath, allPatterns.Select(GlobToRegex).ToList()) };
        scopes.AddRange(EnclosingRepositoryScopes(rootPath));

        var result = new List<string>();
        CollectFiles(rootPath, rootPath, scopes, result);
        return result;
    }

    // When the root is a subfolder of a git repository, the .gitignore files of the folders above it (up to the repository root) still apply.
    private static IEnumerable<IgnoreScope> EnclosingRepositoryScopes(string rootPath)
    {
        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        if (IsRepositoryRoot(root))
            return Array.Empty<IgnoreScope>();

        var scopes = new List<IgnoreScope>();
        for (var directory = root.Parent; directory is not null; directory = directory.Parent)
        {
            var patterns = LoadIgnorePatterns(Path.Combine(directory.FullName, ".gitignore"));
            if (patterns.Count > 0)
                scopes.Add(new IgnoreScope(directory.FullName, patterns.Select(GlobToRegex).ToList()));

            if (IsRepositoryRoot(directory))
                return scopes;
        }

        return Array.Empty<IgnoreScope>();
    }

    private static bool IsRepositoryRoot(DirectoryInfo directory) =>
        Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"));

    private static bool IsIgnored(string file, List<IgnoreScope> scopes) =>
        scopes.Any(scope =>
        {
            var relative = Path.GetRelativePath(scope.BaseDirectory, file).Replace('\\', '/');
            return scope.Patterns.Any(r => r.IsMatch(relative));
        });

    private static void CollectFiles(string rootPath, string directory, List<IgnoreScope> scopes, List<string> result)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EnumerationOptions))
        {
            if (entry.LinkTarget is not null)
                continue;

            if (entry is DirectoryInfo subdirectory)
            {
                CollectFiles(rootPath, subdirectory.FullName, scopes, result);
                continue;
            }

            var file = entry.FullName;
            var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');

            if (relative.Split('/').Contains(".git"))
                continue;

            if (IsIgnored(file, scopes))
                continue;

            if (BinaryExtensions.Contains(Path.GetExtension(file)))
                continue;

            if (((FileInfo)entry).Length > MaxFileSizeBytes)
                continue;

            result.Add(file);
        }
    }

    private static List<string> LoadIgnorePatterns(string path)
    {
        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .SelectMany(IgnoreLineToGlobs)
            .ToList();
    }

    private static IEnumerable<string> IgnoreLineToGlobs(string line)
    {
        var directoryOnly = line.EndsWith('/');
        var pattern = line.TrimEnd('/');
        // gitignore: a slash at the start or middle anchors the pattern to the root; otherwise it matches at any depth.
        var anchored = pattern.Contains('/');
        pattern = pattern.TrimStart('/');
        if (pattern.Length == 0)
            yield break;

        if (!anchored)
            pattern = "**/" + pattern;

        yield return pattern + "/**";
        if (!directoryOnly)
            yield return pattern;
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob)
            .Replace("\\*\\*/", "(.*/)?")
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*");
        pattern = CharacterClass.Replace(pattern, m => (m.Groups[1].Value == "!" ? "[^" : "[") + m.Groups[2].Value + "]");
        return new Regex("^" + pattern + "$", RegexOptions.Compiled);
    }
}
