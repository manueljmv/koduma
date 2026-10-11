using System.Text.Json.Serialization;

namespace Koduma.Core.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Issue), "issue")]
public interface IIssue
{
    string RuleId { get; }
    Severity Severity { get; }
    Category Category { get; }
    string FilePath { get; }
    int Line { get; }
    int Column { get; }
    int EndLine { get; }
    int EndColumn { get; }
    string Message { get; }
    string Language { get; }
}
