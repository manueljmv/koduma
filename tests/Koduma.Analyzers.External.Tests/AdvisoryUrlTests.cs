using Koduma.Analyzers.External;
using Xunit;

public class AdvisoryUrlTests
{
    [Theory]
    [InlineData("https://github.com/advisories/GHSA-fvqr-27wr-82fm", "GHSA-fvqr-27wr-82fm")]
    [InlineData("https://github.com/advisories/GHSA-5crp-9r3c-p9vr", "GHSA-5crp-9r3c-p9vr")]
    public void ExtractGhsaId_FindsIdInGitHubAdvisoryUrl(string url, string expected)
    {
        Assert.Equal(expected, AdvisoryUrl.ExtractGhsaId(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/not-an-advisory")]
    public void ExtractGhsaId_ReturnsNull_WhenNoGhsaIdPresent(string? url)
    {
        Assert.Null(AdvisoryUrl.ExtractGhsaId(url));
    }
}
