namespace Koduma.Core.Model;

public record Rule(
    string Id,
    string Name,
    Category Category,
    Severity DefaultSeverity,
    string Language,
    string? EngineRuleId);
