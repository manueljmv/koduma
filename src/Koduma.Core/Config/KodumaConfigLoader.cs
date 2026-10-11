using System.Text.Json;
using System.Text.Json.Nodes;
using Koduma.Core.Model;

namespace Koduma.Core.Config;

public static class KodumaConfigLoader
{
    public static KodumaConfig LoadOrDefault(string projectPath)
    {
        var path = Path.Combine(projectPath, "koduma.json");
        if (!File.Exists(path))
            return KodumaConfig.Default;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new KodumaConfigException($"koduma.json is not valid JSON: {ex.Message}", ex);
        }

        var rules = new Dictionary<string, RuleOverride>();
        if (root?["rules"] is JsonObject rulesObject)
        {
            foreach (var (ruleId, value) in rulesObject)
            {
                rules[ruleId] = ParseRuleOverride(ruleId, value);
            }
        }

        var exclude = new List<string>();
        if (root?["exclude"] is JsonArray excludeArray)
        {
            foreach (var entry in excludeArray)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var pattern))
                    exclude.Add(pattern);
                else
                    throw new KodumaConfigException($"exclude: expected a string entry, got '{entry?.ToJsonString() ?? "null"}'.");
            }
        }
        else if (root?["exclude"] is { } nonArrayExclude)
        {
            throw new KodumaConfigException($"exclude: expected an array, got '{nonArrayExclude.ToJsonString()}'.");
        }

        return new KodumaConfig(rules, exclude);
    }

    private static RuleOverride ParseRuleOverride(string ruleId, JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
                return new RuleOverride(Enabled: false, Severity: null);

            throw new KodumaConfigException($"Rule '{ruleId}': unsupported string value '{text}', expected \"off\".");
        }

        if (value is JsonObject obj)
        {
            Severity? severity = null;
            if (obj["severity"] is JsonValue severityValue && severityValue.TryGetValue<string>(out var severityText))
            {
                if (!Enum.TryParse<Severity>(severityText, ignoreCase: true, out var parsed))
                    throw new KodumaConfigException($"Rule '{ruleId}': unknown severity '{severityText}'.");
                severity = parsed;
            }

            return new RuleOverride(Enabled: null, Severity: severity);
        }

        throw new KodumaConfigException($"Rule '{ruleId}': expected \"off\" or an object with a \"severity\" field.");
    }
}
