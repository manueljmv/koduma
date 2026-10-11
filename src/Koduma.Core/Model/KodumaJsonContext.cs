using System.Text.Json.Serialization;

namespace Koduma.Core.Model;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(AnalysisResult))]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(IIssue))]
[JsonSerializable(typeof(Rule))]
[JsonSerializable(typeof(Rule[]))]
[JsonSerializable(typeof(AnalysisMetrics))]
[JsonSerializable(typeof(AnalysisError))]
public partial class KodumaJsonContext : JsonSerializerContext
{
}
