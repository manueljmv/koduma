using Koduma.Core.Model;

namespace Koduma.Core.Config;

public record KodumaConfig(
    Dictionary<string, RuleOverride> Rules,
    List<string> Exclude,
    List<CustomPatternRule> CustomPatterns)
{
    public static KodumaConfig Default { get; } = new(new(), new(), new());
}
