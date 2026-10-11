using Koduma.Core.Analysis;

namespace Koduma.Cli;

public static class RulesCommand
{
    public static int Run(IReadOnlyList<ILanguageAnalyzer> analyzers, TextWriter stdout)
    {
        foreach (var analyzer in analyzers)
        {
            foreach (var rule in analyzer.Rules)
            {
                stdout.WriteLine($"{rule.Id}\t{rule.Language}\t{rule.Category}\t{rule.DefaultSeverity}\t{rule.Name}");
            }
        }

        return 0;
    }
}
