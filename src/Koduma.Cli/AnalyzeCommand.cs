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
        var path = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : ".";
        var formats = ParseFormats(args);

        KodumaConfig config;
        try
        {
            config = KodumaConfigLoader.LoadOrDefault(path);
        }
        catch (KodumaConfigException ex)
        {
            stderr.WriteLine($"Invalid koduma.json: {ex.Message}");
            return 2;
        }

        var orchestrator = new AnalysisOrchestrator(analyzers, new FileDiscovery());
        var result = await orchestrator.AnalyzeAsync(path, config);

        try
        {
            foreach (var format in formats)
            {
                var generator = generators.FirstOrDefault(g => g.Format == format)
                    ?? throw new KodumaConfigException($"Unknown format '{format}'.");
                generator.Write(result, stdout);
                stdout.WriteLine();
            }
        }
        catch (KodumaConfigException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }

        var hasBlockerOrCritical = result.Issues.Any(i => i.Severity is Severity.Blocker or Severity.Critical);
        return hasBlockerOrCritical ? 1 : 0;
    }

    private static List<string> ParseFormats(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--format" && i + 1 < args.Length)
                return args[i + 1].Split(',').ToList();
        }

        return new List<string> { "table" };
    }
}
