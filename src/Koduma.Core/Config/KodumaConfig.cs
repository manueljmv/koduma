namespace Koduma.Core.Config;

public record KodumaConfig(
    Dictionary<string, object> Rules,
    List<string> Exclude)
{
    public static KodumaConfig Default { get; } = new(new(), new());
}
