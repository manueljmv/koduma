using Koduma.Core.Config;

namespace Koduma.Core.Analysis;

public interface ILanguageAnalyzer
{
    string Language { get; }
    IReadOnlyCollection<string> FileExtensions { get; }
    bool AppliesToAllFiles { get; }

    Task<LanguageAnalysisResult> AnalyzeAsync(IReadOnlyList<string> filePaths, KodumaConfig config, CancellationToken cancellationToken);
}
