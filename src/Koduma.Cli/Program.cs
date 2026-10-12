using Koduma.Analyzers.CSharp;
using Koduma.Analyzers.External;
using Koduma.Analyzers.Secrets;
using Koduma.Cli;
using Koduma.Core.Analysis;
using Koduma.Reporting;

ILanguageAnalyzer[] analyzers =
{
    new CSharpAnalyzer(),
    new EslintAnalyzer(new ExternalProcessRunner()),
    new SecretsAnalyzer(),
};

IReportGenerator[] generators =
{
    new TableReportGenerator(),
    new JsonReportGenerator(),
    new HtmlReportGenerator(),
    new SarifReportGenerator(),
};

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: koduma <analyze|rules|mcp-serve> [options]");
    return 2;
}

return args[0] switch
{
    "analyze" => await AnalyzeCommand.Run(args, analyzers, generators, Console.Out, Console.Error),
    "rules" => RulesCommand.Run(analyzers, Console.Out),
    "mcp-serve" => await RunMcpServe(analyzers, generators, args),
    _ => Unknown(args[0]),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    return 2;
}

static async Task<int> RunMcpServe(ILanguageAnalyzer[] analyzers, IReportGenerator[] generators, string[] args)
{
    var root = ParseRoot(args) ?? Directory.GetCurrentDirectory();
    await Koduma.Mcp.KodumaMcpServer.RunAsync(analyzers, generators, root, CancellationToken.None);
    return 0;
}

static string? ParseRoot(string[] args)
{
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--root" && i + 1 < args.Length)
            return args[i + 1];
    }

    return null;
}
