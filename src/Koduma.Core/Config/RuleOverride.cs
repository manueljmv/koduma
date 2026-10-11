using Koduma.Core.Model;

namespace Koduma.Core.Config;

public record RuleOverride(bool? Enabled, Severity? Severity);
