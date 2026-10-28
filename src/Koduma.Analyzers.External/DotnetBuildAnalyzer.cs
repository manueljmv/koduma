using System.Security;
using System.Text.Json;
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Analyzers.External;

/// <summary>
/// Runs the real .NET compiler and the .NET analyzers (<c>Microsoft.CodeAnalysis.NetAnalyzers</c>, shipped with the SDK) by building the
/// analyzed solution/projects with <c>dotnet build</c>, then normalizes the SARIF error log Roslyn emits. Rule ids are reported as
/// <c>NET-CAxxxx</c> / <c>NET-CSxxxx</c>. Needs the .NET SDK on PATH; when it is missing the run degrades to an <see cref="LanguageAnalysisResult.Error"/>.
/// </summary>
public class DotnetBuildAnalyzer : ILanguageAnalyzer
{
    /// <summary>Set to any non-empty value to skip this analyzer (it builds the analyzed repo, which writes bin/obj).</summary>
    public const string DisableEnvironmentVariable = "KODUMA_SKIP_DOTNET_BUILD";

    private readonly ExternalProcessRunner _runner;

    public DotnetBuildAnalyzer(ExternalProcessRunner runner) => _runner = runner;

    public string Language => "dotnet";
    public IReadOnlyCollection<string> FileExtensions => new[] { ".cs" };
    public bool AppliesToAllFiles => false;

    // The catalog is whatever the installed SDK ships and the repo's AnalysisMode enables, so it cannot be enumerated in advance.
    public IReadOnlyList<Rule> Rules { get; } = Array.Empty<Rule>();

    public async Task<LanguageAnalysisResult> AnalyzeAsync(string projectPath, IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken)
    {
        var empty = new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0), Error: null);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableEnvironmentVariable)))
            return empty;

        var targets = FindBuildTargets(projectPath, filePaths);
        if (targets.Count == 0)
            return empty;

        var workDirectory = Directory.CreateTempSubdirectory("koduma-dotnet-");
        try
        {
            var sarifDirectory = Path.Combine(workDirectory.FullName, "sarif");
            Directory.CreateDirectory(sarifDirectory);
            var targetsFile = Path.Combine(workDirectory.FullName, "koduma.analysis.targets");
            await File.WriteAllTextAsync(targetsFile, BuildTargetsFileContent(sarifDirectory), cancellationToken);

            var failures = new List<string>();
            string? msbuild = null;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string executable;
                IReadOnlyList<string> arguments;
                if (OperatingSystem.IsWindows() && MSBuildProjects.ContainsNonSdkProject(target))
                {
                    msbuild ??= await FindVisualStudioMSBuildAsync(projectPath);
                    if (msbuild is null)
                    {
                        failures.Add($"{Path.GetFileName(target)}: non-SDK-style projects need Visual Studio MSBuild, which was not found via vswhere.");
                        continue;
                    }

                    (executable, arguments) = (msbuild, MSBuildArguments(target, targetsFile));
                }
                else
                {
                    (executable, arguments) = ("dotnet", BuildArguments(target, targetsFile));
                }

                var (exitCode, stdOut, stdErr) = await _runner.RunAsync(executable, arguments, projectPath);

                // A nonzero exit code is expected when the code has compile errors; only the absence of any SARIF output is fatal (checked below).
                if (exitCode != 0)
                    failures.Add($"{Path.GetFileName(target)}: exit code {exitCode}. {Tail(stdErr.Length > 0 ? stdErr : stdOut)}");
            }

            var sarifFiles = Directory.GetFiles(sarifDirectory, "*.sarif");
            if (sarifFiles.Length == 0)
            {
                var detail = failures.Count > 0 ? string.Join(" | ", failures) : "no error log was produced.";
                return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0),
                    Error: $"dotnet build could not be run or produced no analyzer output: {detail}");
            }

            var issues = new List<IIssue>();
            foreach (var sarifFile in sarifFiles)
            {
                try
                {
                    issues.AddRange(DotnetSarifMapper.Map(await File.ReadAllTextAsync(sarifFile, cancellationToken)));
                }
                catch (JsonException ex)
                {
                    return new LanguageAnalysisResult(Array.Empty<IIssue>(), new AnalysisMetrics(0, 0, 0, 0),
                        Error: $"dotnet build produced an unreadable error log ({Path.GetFileName(sarifFile)}): {ex.Message}");
                }
            }

            return new LanguageAnalysisResult(FilterAndDeduplicate(issues, filePaths), new AnalysisMetrics(0, 0, 0, 0), Error: null);
        }
        finally
        {
            try { workDirectory.Delete(recursive: true); }
            catch (IOException) { /* best-effort cleanup of a temp directory */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup of a temp directory */ }
        }
    }

    /// <summary>A top-level solution if there is one (single build, handles project references); otherwise the nearest project of each analyzed file.</summary>
    internal static IReadOnlyList<string> FindBuildTargets(string projectPath, IReadOnlyList<string> filePaths)
    {
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            return Array.Empty<string>();

        var solution = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        if (solution is not null)
            return new[] { solution };

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var projects = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in filePaths)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(file));
            while (directory is not null && IsWithin(directory, root, comparison))
            {
                var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                if (project is not null)
                {
                    projects.Add(project);
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        return projects.ToList();
    }

    private static bool IsWithin(string directory, string root, StringComparison comparison) =>
        string.Equals(directory, root, comparison)
        || directory.StartsWith(root + Path.DirectorySeparatorChar, comparison);

    // Injected through CustomBeforeMicrosoftCommonTargets (a global property), so the analyzed repo's own files are never modified.
    // One log per project and target framework avoids parallel/multi-targeted projects overwriting each other.
    internal static string BuildTargetsFileContent(string sarifDirectory) =>
        "<Project>\n" +
        "  <PropertyGroup>\n" +
        $"    <ErrorLog>{SecurityElement.Escape(sarifDirectory)}{Path.DirectorySeparatorChar}$(MSBuildProjectName)-$(TargetFramework).sarif,version=2.1</ErrorLog>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";

    internal static IReadOnlyList<string> BuildArguments(string target, string targetsFile) => new[]
    {
        "build", target,
        "--no-incremental",   // an up-to-date build would skip the compiler, and with it the analyzers
        "-nologo", "-v:q",
    }.Concat(AnalysisProperties(targetsFile)).ToList();

    // Visual Studio's MSBuild.exe, for legacy projects (e.g. SharePoint) that import VS-only targets the .NET SDK does not ship.
    internal static IReadOnlyList<string> MSBuildArguments(string target, string targetsFile) => new[]
    {
        target,
        "-restore",
        "-t:Rebuild",   // same reason as --no-incremental
        "-nologo", "-v:q",
    }.Concat(AnalysisProperties(targetsFile)).ToList();

    private async Task<string?> FindVisualStudioMSBuildAsync(string workingDirectory)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var (exitCode, stdOut, _) = await _runner.RunAsync(vswhere,
            new[] { "-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe" },
            workingDirectory);

        return exitCode == 0
            ? stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            : null;
    }

    private static IEnumerable<string> AnalysisProperties(string targetsFile) => new[]
    {
        // Reused worker nodes outlive the build holding its redirected stdout open, so reading the output would hang until they idle out.
        "-nodeReuse:false",
        $"-p:CustomBeforeMicrosoftCommonTargets={targetsFile}",
        "-p:RunAnalyzers=true",
        "-p:EnableNETAnalyzers=true",
        "-p:AnalysisLevel=latest",
        "-p:AnalysisMode=Recommended",
        "-p:TreatWarningsAsErrors=false",
        "-p:CodeAnalysisTreatWarningsAsErrors=false",
        "-p:EnforceCodeStyleInBuild=false",
    };

    private static List<IIssue> FilterAndDeduplicate(List<IIssue> issues, IReadOnlyList<string> filePaths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Only files Koduma discovered (so .gitignore/.kodumaignore/exclude apply; generated code under obj/ never matches).
        var included = new HashSet<string>(filePaths.Select(Path.GetFullPath), comparer);
        var seen = new HashSet<string>(comparer);
        var result = new List<IIssue>();

        foreach (var issue in issues)
        {
            if (!included.Contains(Path.GetFullPath(issue.FilePath)))
                continue;

            // The same project built for several target frameworks reports the same finding once per framework.
            if (seen.Add($"{issue.RuleId}|{issue.FilePath}|{issue.Line}|{issue.Column}|{issue.Message}"))
                result.Add(issue);
        }

        return result;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "..." + trimmed[^400..];
    }
}
