using System.Text.RegularExpressions;

namespace Koduma.Analyzers.External;

internal static class AdvisoryUrl
{
    private static readonly Regex GhsaPattern = new(
        @"GHSA-[A-Za-z0-9]{4,}-[A-Za-z0-9]{4,}-[A-Za-z0-9]{4,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? ExtractGhsaId(string? url)
    {
        var match = GhsaPattern.Match(url ?? "");
        return match.Success ? match.Value : null;
    }
}
