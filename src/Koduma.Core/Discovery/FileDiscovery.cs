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

    public IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config)
    {
        var gitignorePatterns = LoadIgnorePatterns(Path.Combine(rootPath, ".gitignore"));
        var kodumaIgnorePatterns = LoadIgnorePatterns(Path.Combine(rootPath, ".kodumaignore"));
        var allPatterns = gitignorePatterns.Concat(kodumaIgnorePatterns).Concat(config.Exclude).ToList();
        var regexes = allPatterns.Select(GlobToRegex).ToList();

        var result = new List<string>();
        CollectFiles(rootPath, rootPath, regexes, result);
        return result;
    }

    private static void CollectFiles(string rootPath, string directory, List<Regex> regexes, List<string> result)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
                continue;

            if (entry is DirectoryInfo subdirectory)
            {
                CollectFiles(rootPath, subdirectory.FullName, regexes, result);
                continue;
            }

            var file = entry.FullName;
            var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');

            if (relative.Split('/').Contains(".git"))
                continue;

            if (regexes.Any(r => r.IsMatch(relative)))
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
            .Select(line => line.TrimEnd('/') + "/**")
            .ToList();
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*/", "(.*/)?")
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            + "$";
        return new Regex(pattern, RegexOptions.Compiled);
    }
}
