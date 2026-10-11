using System.Net;
using System.Text;
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class HtmlReportGenerator : IReportGenerator
{
    public string Format => "html";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Koduma report</title>");
        sb.AppendLine("<style>body{font-family:sans-serif} table{border-collapse:collapse;width:100%} td,th{border:1px solid #ccc;padding:4px}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>Koduma report — {WebUtility.HtmlEncode(result.ProjectPath)}</h1>");
        sb.AppendLine($"<p>{result.Metrics.FilesAnalyzed} files, {result.Metrics.LinesOfCode} LOC, {result.Issues.Count} issues</p>");
        sb.AppendLine("<table><tr><th>Severity</th><th>Rule</th><th>File</th><th>Line</th><th>Message</th></tr>");

        foreach (var issue in result.Issues)
        {
            sb.AppendLine("<tr>" +
                $"<td>{WebUtility.HtmlEncode(issue.Severity.ToString())}</td>" +
                $"<td>{WebUtility.HtmlEncode(issue.RuleId)}</td>" +
                $"<td>{WebUtility.HtmlEncode(issue.FilePath)}</td>" +
                $"<td>{issue.Line}</td>" +
                $"<td>{WebUtility.HtmlEncode(issue.Message)}</td>" +
                "</tr>");
        }

        sb.AppendLine("</table>");

        if (result.Errors.Count > 0)
        {
            sb.AppendLine("<h2>Analysis errors</h2>");
            sb.AppendLine("<p>These languages were not fully analyzed:</p>");
            sb.AppendLine("<ul>");
            foreach (var error in result.Errors)
                sb.AppendLine($"<li><strong>{WebUtility.HtmlEncode(error.Language)}</strong>: {WebUtility.HtmlEncode(error.Message)}</li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</body></html>");
        writer.Write(sb.ToString());
    }
}
