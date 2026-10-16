using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using Koduma.Reporting;

namespace Koduma.Cli;

public static class AnalyzeCommand
{
    public static async Task<int> Run(
        string[] args,
        IReadOnlyList<ILanguageAnalyzer> analyzers,
        IReadOnlyList<IReportGenerator> generators,
        TextWriter stdout,
        TextWriter stderr)
    {
        var (path, formats, failOnText) = ParseArguments(args);

        var failOnThreshold = Severity.Critical;
        if (failOnText is not null && (!Enum.TryParse(failOnText, ignoreCase: true, out failOnThreshold) || !Enum.IsDefined(failOnThreshold)))
        {
            stderr.WriteLine($"Unknown --fail-on severity '{failOnText}'.");
            return 2;
        }

        var unknownFormat = formats.FirstOrDefault(f => generators.All(g => g.Format != f));
        if (unknownFormat is not null)
        {
            stderr.WriteLine($"Unknown format '{unknownFormat}'.");
            return 2;
        }

        if (!Directory.Exists(path))
        {
            stderr.WriteLine($"Path not found or not a directory: {path}");
            return 2;
        }

        KodumaConfig config;
        try
        {
            config = KodumaConfigLoader.LoadOrDefault(path, analyzers.SelectMany(a => a.Rules).Select(r => r.Id).ToArray());
        }
        catch (KodumaConfigException ex)
        {
            stderr.WriteLine($"Invalid koduma.json: {ex.Message}");
            return 2;
        }

        var orchestrator = new AnalysisOrchestrator(analyzers, new FileDiscovery());
        var result = await orchestrator.AnalyzeAsync(path, config);

        foreach (var format in formats)
        {
            generators.First(g => g.Format == format).Write(result, stdout);
            stdout.WriteLine();
        }

        var hasThresholdIssue = result.Issues.Any(i => i.Severity >= failOnThreshold);
        return hasThresholdIssue ? 1 : 0;
    }

    private static (string Path, List<string> Formats, string? FailOn) ParseArguments(string[] args)
    {
        string? path = null;
        var formats = new List<string> { "table" };
        string? failOn = null;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--format")
            {
                if (i + 1 < args.Length)
                    formats = args[++i].Split(',').ToList();
                continue;
            }

            if (args[i] == "--fail-on")
            {
                if (i + 1 < args.Length)
                    failOn = args[++i];
                continue;
            }

            if (args[i].StartsWith("--"))
                continue;

            path ??= args[i];
        }

        return (path ?? ".", formats, failOn);
    }
}
