using System.Text.Json;
using System.Text.Json.Nodes;
using Koduma.Core.Model;

namespace Koduma.Reporting;

public class SarifReportGenerator : IReportGenerator
{
    public string Format => "sarif";

    public void Write(AnalysisResult result, TextWriter writer)
    {
        var results = new JsonArray();
        foreach (var issue in result.Issues)
        {
            results.Add(new JsonObject
            {
                ["ruleId"] = issue.RuleId,
                ["level"] = MapLevel(issue.Severity),
                ["message"] = new JsonObject { ["text"] = issue.Message },
                ["locations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject { ["uri"] = issue.FilePath },
                            ["region"] = new JsonObject
                            {
                                ["startLine"] = issue.Line,
                                ["startColumn"] = issue.Column,
                                ["endLine"] = issue.EndLine,
                                ["endColumn"] = issue.EndColumn,
                            }
                        }
                    }
                }
            });
        }

        var sarif = new JsonObject
        {
            ["version"] = "2.1.0",
            ["$schema"] = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject { ["name"] = "koduma", ["version"] = "0.1.0" }
                    },
                    ["results"] = results
                }
            }
        };

        writer.Write(sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string MapLevel(Severity severity) => severity switch
    {
        Severity.Blocker or Severity.Critical => "error",
        Severity.Major => "warning",
        _ => "note",
    };
}
