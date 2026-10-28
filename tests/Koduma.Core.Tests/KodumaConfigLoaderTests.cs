using System.Text.Json;
using Koduma.Core.Config;
using Koduma.Core.Model;
using Xunit;

public class KodumaConfigLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "koduma-config-" + Guid.NewGuid());

    public KodumaConfigLoaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void LoadOrDefault_ReturnsDefault_WhenNoFileExists()
    {
        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

        Assert.Empty(config.Rules);
        Assert.Empty(config.Exclude);
    }

    private string WriteGlobal(string json)
    {
        var path = Path.Combine(_root, "global", "koduma.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LoadOrDefault_UsesGlobalConfig_WhenProjectHasNone()
    {
        var global = WriteGlobal("""{ "rules": { "CS-NAMING-001": "off" }, "exclude": ["**/ace/**"] }""");

        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>(), global);

        Assert.False(config.Rules["CS-NAMING-001"].Enabled);
        Assert.Equal(new[] { "**/ace/**" }, config.Exclude);
    }

    [Fact]
    public void LoadOrDefault_MergesGlobalAndProjectConfig_ProjectWinsPerId()
    {
        var global = WriteGlobal("""
        {
          "rules": { "CS-NAMING-001": "off", "CS-DESIGN-003": "off" },
          "exclude": ["**/ace/**"],
          "customPatterns": [
            { "id": "CUSTOM-A", "name": "global a", "pattern": "a" },
            { "id": "CUSTOM-B", "name": "global b", "pattern": "b" }
          ]
        }
        """);
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        {
          "rules": { "CS-NAMING-001": { "severity": "minor" } },
          "exclude": ["**/vendor/**"],
          "customPatterns": [ { "id": "CUSTOM-B", "name": "project b", "pattern": "bb" } ]
        }
        """);

        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>(), global);

        Assert.Null(config.Rules["CS-NAMING-001"].Enabled);
        Assert.Equal(Severity.Minor, config.Rules["CS-NAMING-001"].Severity);
        Assert.False(config.Rules["CS-DESIGN-003"].Enabled);
        Assert.Equal(new[] { "**/ace/**", "**/vendor/**" }, config.Exclude);
        Assert.Equal(new[] { "global a", "project b" }, config.CustomPatterns.OrderBy(p => p.Id).Select(p => p.Name));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenGlobalConfigPathDoesNotExist()
    {
        var missing = Path.Combine(_root, "missing.json");

        var ex = Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>(), missing));

        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void LoadOrDefault_NamesGlobalConfigFile_WhenItIsInvalid()
    {
        var global = WriteGlobal("""{ "rules": { "CS-NAMING-001": "maybe" } }""");

        var ex = Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>(), global));

        Assert.Contains(global, ex.Message);
    }

    [Fact]
    public void LoadOrDefault_ParsesOffStringAndSeverityObjectOverrides()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        {
          "rules": {
            "CS-NAMING-001": "off",
            "SEC-SECRET-001": { "severity": "blocker" }
          },
          "exclude": ["**/bin/**"]
        }
        """);

        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

        Assert.False(config.Rules["CS-NAMING-001"].Enabled);
        Assert.Equal(Severity.Blocker, config.Rules["SEC-SECRET-001"].Severity);
        Assert.Contains("**/bin/**", config.Exclude);
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenJsonIsMalformed()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), "{ not valid json");

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenExcludeEntryIsNotAString()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "exclude": [1] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenExcludeIsNotAnArray()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "exclude": "**/bin/**" }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenRuleOverrideShapeIsInvalid()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "rules": { "CS-NAMING-001": 123 } }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_ParsesCustomPatterns_WithDefaultsForSeverityAndMessage()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [
            { "id": "SEC-CUSTOM-001", "name": "Internal token", "pattern": "ITOK-[0-9A-F]{8}" }
        ] }
        """);

        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

        var rule = Assert.Single(config.CustomPatterns);
        Assert.Equal("SEC-CUSTOM-001", rule.Id);
        Assert.Equal(Severity.Critical, rule.Severity);
        Assert.Equal("Possible Internal token leak.", rule.Message);
    }

    [Fact]
    public void LoadOrDefault_ParsesCustomPatterns_WithExplicitSeverityAndMessage()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [
            { "id": "SEC-CUSTOM-001", "name": "Internal token", "pattern": "ITOK-[0-9A-F]{8}",
              "severity": "major", "message": "Internal token leaked." }
        ] }
        """);

        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

        var rule = Assert.Single(config.CustomPatterns);
        Assert.Equal(Severity.Major, rule.Severity);
        Assert.Equal("Internal token leaked.", rule.Message);
    }

    [Fact]
    public void LoadOrDefault_ReturnsEmptyCustomPatterns_WhenKeyIsAbsent()
    {
        var config = KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>());

        Assert.Empty(config.CustomPatterns);
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenCustomPatternIsMissingId()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [ { "name": "x", "pattern": "a" } ] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenCustomPatternIsMissingPattern()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [ { "id": "SEC-CUSTOM-001", "name": "x" } ] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenCustomPatternRegexIsInvalid()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [ { "id": "SEC-CUSTOM-001", "name": "x", "pattern": "[unterminated" } ] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenCustomPatternIdsCollideWithEachOther()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [
            { "id": "SEC-CUSTOM-001", "name": "x", "pattern": "a" },
            { "id": "SEC-CUSTOM-001", "name": "y", "pattern": "b" }
        ] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, Array.Empty<string>()));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenCustomPatternIdCollidesWithAnyKnownBuiltInRuleId()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "customPatterns": [ { "id": "CS-NAMING-001", "name": "x", "pattern": "a" } ] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root, new[] { "CS-NAMING-001" }));
    }

    [Fact]
    public void ParseRulesConfig_ParsesOffStringAndSeverityObject()
    {
        using var document = JsonDocument.Parse("""
        {
          "CS-NAMING-001": "off",
          "SEC-SECRET-001": { "severity": "blocker" }
        }
        """);

        var rules = KodumaConfigLoader.ParseRulesConfig(document.RootElement);

        Assert.False(rules["CS-NAMING-001"].Enabled);
        Assert.Equal(Severity.Blocker, rules["SEC-SECRET-001"].Severity);
    }

    [Fact]
    public void ParseRulesConfig_Throws_WhenARuleShapeIsInvalid()
    {
        using var document = JsonDocument.Parse("""{ "CS-NAMING-001": 123 }""");

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.ParseRulesConfig(document.RootElement));
    }
}
