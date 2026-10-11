namespace Koduma.Core.Model;

public record Issue(
    string RuleId,
    Severity Severity,
    Category Category,
    string FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Message,
    string Language) : IIssue;
