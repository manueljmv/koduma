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
        var config = KodumaConfigLoader.LoadOrDefault(_root);

        Assert.Empty(config.Rules);
        Assert.Empty(config.Exclude);
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

        var config = KodumaConfigLoader.LoadOrDefault(_root);

        Assert.False(config.Rules["CS-NAMING-001"].Enabled);
        Assert.Equal(Severity.Blocker, config.Rules["SEC-SECRET-001"].Severity);
        Assert.Contains("**/bin/**", config.Exclude);
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenJsonIsMalformed()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), "{ not valid json");

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenExcludeEntryIsNotAString()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "exclude": [1] }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenExcludeIsNotAnArray()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "exclude": "**/bin/**" }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root));
    }

    [Fact]
    public void LoadOrDefault_Throws_WhenRuleOverrideShapeIsInvalid()
    {
        File.WriteAllText(Path.Combine(_root, "koduma.json"), """
        { "rules": { "CS-NAMING-001": 123 } }
        """);

        Assert.Throws<KodumaConfigException>(() => KodumaConfigLoader.LoadOrDefault(_root));
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
