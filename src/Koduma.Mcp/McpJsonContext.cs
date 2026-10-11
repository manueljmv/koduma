using System.Text.Json.Serialization;

namespace Koduma.Mcp;

internal sealed record McpCapabilities(string[] Languages, string[] Categories);

[JsonSerializable(typeof(McpCapabilities))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
