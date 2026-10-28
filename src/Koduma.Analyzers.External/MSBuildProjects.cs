using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Koduma.Analyzers.External;

/// <summary>
/// Distinguishes SDK-style projects from legacy (non-SDK) ones such as .NET Framework/SharePoint projects, which the .NET CLI cannot
/// evaluate because they import Visual Studio-only targets.
/// </summary>
internal static class MSBuildProjects
{
    private static readonly Regex SolutionProject = new(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.csproj)""", RegexOptions.Multiline);
    private static readonly Regex SlnxProject = new(@"<Project\s[^>]*\bPath=""([^""]+\.csproj)""");

    /// <summary>The target itself for a project; the existing C# projects it lists for a .sln/.slnx.</summary>
    public static IReadOnlyList<string> GetProjectFiles(string target)
    {
        if (target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return new[] { target };

        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var pattern = target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? SlnxProject : SolutionProject;
        return pattern.Matches(File.ReadAllText(target))
            .Select(m => Path.GetFullPath(Path.Combine(directory, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .ToList();
    }

    // An unreadable project is treated as SDK-style so it keeps going through 'dotnet', which reports the real problem.
    public static bool IsSdkStyleProject(string projectFile)
    {
        try
        {
            var root = XDocument.Load(projectFile).Root;
            return root is null
                || root.Attribute("Sdk") is not null
                || root.Descendants().Any(e => e.Name.LocalName == "Sdk" || (e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null));
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool ContainsNonSdkProject(string target) => GetProjectFiles(target).Any(p => !IsSdkStyleProject(p));
}
