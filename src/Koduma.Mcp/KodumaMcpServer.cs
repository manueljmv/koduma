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

    // Known limitation: vulnerable to TOCTOU (a symlink swapped between this check
    // and the actual file access) and to /proc/<pid>/root mount-namespace magic
    // links. Both require kernel-level resolution (openat2/O_PATH) that .NET
    // doesn't expose; accepted for this tool's local, single-user, stdio-only
    // threat model.
    internal static string ResolveAndValidatePath(string path, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path, fullRoot);

        var canonicalRoot = Canonicalize(fullRoot);
        var canonicalPath = Canonicalize(fullPath);

        if (!IsWithinRoot(canonicalPath, canonicalRoot))
            throw new InvalidOperationException($"Path '{path}' is outside the configured root '{root}'.");

        return fullPath;
    }

    private static string Canonicalize(string path)
    {
        var remaining = new Stack<string>(SplitSegments(path).Reverse());
        var resolved = GetRoot(path);
        var linkHops = 0;

        while (remaining.Count > 0)
        {
            var segment = remaining.Pop();
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var candidate = Path.Combine(resolved, segment);

            string? linkTarget = null;
            if (File.Exists(candidate))
                linkTarget = new FileInfo(candidate).LinkTarget;
            else if (Directory.Exists(candidate))
                linkTarget = new DirectoryInfo(candidate).LinkTarget;

            if (linkTarget is not null)
            {
                if (++linkHops > 40)
                    throw new InvalidOperationException($"Too many levels of symbolic links resolving '{path}'.");

                // Raw target text: '..' must be applied after earlier segments are themselves resolved, not collapsed lexically.
                if (Path.IsPathRooted(linkTarget))
                    resolved = GetRoot(linkTarget);
                foreach (var seg in SplitSegments(linkTarget).Reverse())
                    remaining.Push(seg);
                continue;
            }

            resolved = candidate;
        }

        return resolved;
    }

    private static string GetRoot(string path)
    {
        var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
    }

    private static IEnumerable<string> SplitSegments(string path)
    {
        var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
        return path.Substring(root.Length).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    internal static bool IsWithinRoot(string candidate, string fullRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return string.Equals(candidate, fullRoot, comparison) || candidate.StartsWith(prefix, comparison);
    }

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

        var config = KodumaConfigLoader.LoadOrDefault(fullPath, analyzers.SelectMany(a => a.Rules).Select(r => r.Id).ToArray());
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
