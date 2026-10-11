using Koduma.Core.Model;

namespace Koduma.Reporting;

public interface IReportGenerator
{
    string Format { get; }
    void Write(AnalysisResult result, TextWriter writer);
}
