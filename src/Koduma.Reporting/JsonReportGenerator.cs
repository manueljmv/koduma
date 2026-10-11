using System.Text.Json;
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class JsonReportGenerator : IReportGenerator
{
    public string Format => "json";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        var json = JsonSerializer.Serialize(result, KodumaJsonContext.Default.AnalysisResult);
        writer.Write(json);
    }
}
