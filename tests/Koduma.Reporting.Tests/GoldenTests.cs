using Koduma.Reporting;
using Xunit;

public class GoldenTests
{
    [Fact]
    public void TableReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new TableReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.table.txt", writer.ToString());
    }

    [Fact]
    public void JsonReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new JsonReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.json", writer.ToString());
    }

    [Fact]
    public void HtmlReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new HtmlReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.html", writer.ToString());
    }

    [Fact]
    public void SarifReportGenerator_MatchesGoldenFile()
    {
        using var writer = new StringWriter();
        new SarifReportGenerator().Write(GoldenFixture.SampleResult(), writer);
        GoldenFile.AssertMatches("sample.sarif.json", writer.ToString());
    }
}
