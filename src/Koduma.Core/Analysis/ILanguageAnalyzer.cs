using Koduma.Core.Config;
using Koduma.Core.Model;

namespace Koduma.Core.Analysis;

public interface ILanguageAnalyzer
{
    string Language { get; }
    IReadOnlyCollection<string> FileExtensions { get; }
    bool AppliesToAllFiles { get; }
    IReadOnlyList<Rule> Rules { get; }

    Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken);
}
