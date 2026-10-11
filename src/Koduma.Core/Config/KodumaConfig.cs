namespace Koduma.Core.Config;

public record KodumaConfig(
    Dictionary<string, RuleOverride> Rules,
    List<string> Exclude)
{
    public static KodumaConfig Default { get; } = new(new(), new());
}
