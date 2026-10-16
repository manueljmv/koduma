using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Koduma.Core.Model;

namespace Koduma.Core.Config;

public static class KodumaConfigLoader
{
    public static KodumaConfig LoadOrDefault(string projectPath, IReadOnlyCollection<string> knownRuleIds)
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

        var customPatterns = ParseCustomPatterns(root?["customPatterns"], knownRuleIds);

        return new KodumaConfig(rules, exclude, customPatterns);
    }

    private static List<CustomPatternRule> ParseCustomPatterns(JsonNode? node, IReadOnlyCollection<string> knownRuleIds)
    {
        var result = new List<CustomPatternRule>();
        if (node is null)
            return result;

        if (node is not JsonArray array)
            throw new KodumaConfigException($"customPatterns: expected an array, got '{node.ToJsonString()}'.");

        var seenIds = new HashSet<string>();
        foreach (var entry in array)
        {
            if (entry is not JsonObject obj)
                throw new KodumaConfigException($"customPatterns: expected an object entry, got '{entry?.ToJsonString() ?? "null"}'.");

            var id = ReadRequiredString(obj, "id");
            var name = ReadRequiredString(obj, "name");
            var pattern = ReadRequiredString(obj, "pattern");

            if (!seenIds.Add(id))
                throw new KodumaConfigException($"customPatterns: duplicate id '{id}'.");
            if (knownRuleIds.Contains(id))
                throw new KodumaConfigException($"customPatterns: id '{id}' collides with a built-in rule id.");

            try
            {
                _ = new Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                throw new KodumaConfigException($"customPatterns: entry '{id}' has an invalid pattern: {ex.Message}", ex);
            }

            var severity = Severity.Critical;
            if (obj["severity"] is JsonValue severityValue && severityValue.TryGetValue<string>(out var severityText))
            {
                if (!Enum.TryParse<Severity>(severityText, ignoreCase: true, out severity))
                    throw new KodumaConfigException($"customPatterns: entry '{id}' has unknown severity '{severityText}'.");
            }

            var message = obj["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var messageText)
                ? messageText
                : $"Possible {name} leak.";

            result.Add(new CustomPatternRule(id, name, pattern, severity, message));
        }

        return result;
    }

    private static string ReadRequiredString(JsonObject obj, string field)
    {
        if (obj[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            return text;

        throw new KodumaConfigException($"customPatterns: entry is missing a required non-empty \"{field}\".");
    }

    public static Dictionary<string, RuleOverride> ParseRulesConfig(JsonElement rulesElement)
    {
        var rules = new Dictionary<string, RuleOverride>();
        foreach (var property in rulesElement.EnumerateObject())
        {
            var node = JsonNode.Parse(property.Value.GetRawText());
            rules[property.Name] = ParseRuleOverride(property.Name, node);
        }
        return rules;
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
