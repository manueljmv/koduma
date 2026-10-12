using System.Text.Json;
using Koduma.Core.Analysis;
using Koduma.Core.Config;
using Koduma.Core.Discovery;
using Koduma.Core.Model;
using Koduma.Reporting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Koduma.Mcp;

public static class KodumaMcpServer
{
    public static async Task RunAsync(
        IReadOnlyList<ILanguageAnalyzer> analyzers,
        IReadOnlyList<IReportGenerator> generators,
        string root,
        CancellationToken cancellationToken)
    {
        var tools = new List<Tool>
        {
            new()
            {
                Name = "analyze_project",
                Description = "Analyzes a project path for code smells, metrics and security issues.",
                InputSchema = ParseSchema(
                    """{"type":"object","properties":{"path":{"type":"string"},"format":{"type":"string"},"rules_config":{"type":"object"}},"required":["path"]}"""),
            },
            new()
            {
                Name = "list_rules",
                Description = "Lists the rules Koduma can check, optionally filtered by language.",
                InputSchema = ParseSchema(
                    """{"type":"object","properties":{"language":{"type":"string"}}}"""),
            },
            new()
            {
                Name = "get_capabilities",
                Description = "Lists the languages and categories Koduma supports.",
                InputSchema = ParseSchema("""{"type":"object"}"""),
            },
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "koduma", Version = "0.1.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        };

        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools });

        options.Handlers.CallToolHandler = async (request, ct) =>
        {
            var arguments = new Dictionary<string, JsonElement>(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());

            try
            {
                string payload = request.Params?.Name switch
                {
                    "analyze_project" => await HandleAnalyzeProject(arguments, analyzers, generators, root, ct),
                    "list_rules" => JsonSerializer.Serialize(
                        HandleListRules(arguments, analyzers),
                        KodumaJsonContext.Default.RuleArray),
                    "get_capabilities" => JsonSerializer.Serialize(
                        HandleGetCapabilities(analyzers),
                        McpJsonContext.Default.McpCapabilities),
                    _ => throw new InvalidOperationException($"Unknown tool '{request.Params?.Name}'."),
                };

                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new TextContentBlock { Text = payload } },
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (KodumaConfigException ex)
            {
                return ToolError($"Invalid koduma.json: {ex.Message}");
            }
            catch (Exception ex)
            {
                return ToolError($"Tool '{request.Params?.Name}' failed: {ex.Message}");
            }
        };

        await using var transport = new StdioServerTransport("koduma");
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken);
    }

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CallToolResult ToolError(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
    };

    internal static string ResolveAndValidatePath(string path, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path, fullRoot);

        var resolved = (File.Exists(fullPath) || Directory.Exists(fullPath))
            ? File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName
            : null;

        if (!IsWithinRoot(fullPath, fullRoot) || (resolved is not null && !IsWithinRoot(resolved, fullRoot)))
            throw new InvalidOperationException($"Path '{path}' is outside the configured root '{root}'.");

        return fullPath;
    }

    private static bool IsWithinRoot(string candidate, string fullRoot) =>
        candidate == fullRoot || candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static async Task<string> HandleAnalyzeProject(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyList<ILanguageAnalyzer> analyzers,
        IReadOnlyList<IReportGenerator> generators,
        string root,
        CancellationToken ct)
    {
        var path = arguments.TryGetValue("path", out var pathElement) ? pathElement.GetString()! : ".";
        var fullPath = ResolveAndValidatePath(path, root);

        var format = arguments.TryGetValue("format", out var formatElement) ? formatElement.GetString()! : "json";
        var generator = generators.FirstOrDefault(g => g.Format == format)
            ?? throw new InvalidOperationException($"Unknown format '{format}'.");

        var config = KodumaConfigLoader.LoadOrDefault(fullPath);
        if (arguments.TryGetValue("rules_config", out var rulesConfigElement))
        {
            var overriddenRules = KodumaConfigLoader.ParseRulesConfig(rulesConfigElement);
            config = config with { Rules = overriddenRules };
        }

        var orchestrator = new AnalysisOrchestrator(analyzers, new FileDiscovery());
        var result = await orchestrator.AnalyzeAsync(fullPath, config, ct);

        using var writer = new StringWriter();
        generator.Write(result, writer);
        return writer.ToString();
    }

    private static Rule[] HandleListRules(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyList<ILanguageAnalyzer> analyzers)
    {
        var language = arguments.TryGetValue("language", out var languageElement) ? languageElement.GetString() : null;
        return analyzers
            .SelectMany(a => a.Rules)
            .Where(r => language is null || r.Language == language)
            .ToArray();
    }

    private static McpCapabilities HandleGetCapabilities(IReadOnlyList<ILanguageAnalyzer> analyzers) => new(
        Languages: analyzers.Select(a => a.Language).ToArray(),
        Categories: Enum.GetNames<Category>());
}
