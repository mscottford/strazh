using System;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Strazh.Analysis;

namespace Strazh
{
    public class Program
    {

        public static async Task<int> Main(params string[] args)
        {
            var rootCommand = new RootCommand();

            var optionCredentials = new Option<string>("--credentials", "-c")
            {
                Description = "required information in format `dbname:user:password` to connect to Neo4j Database",
                Required = true
            };
            rootCommand.Options.Add(optionCredentials);

            var optionMode = new Option<string>("--tier", "-t")
            {
                Description = "optional flag as `project` or `code` or 'all' (default `all`) selected tier to scan in a codebase"
            };
            rootCommand.Options.Add(optionMode);

            var optionDelete = new Option<string>("--delete", "-d")
            {
                Description = "optional flag as `true` or `false` or no flag (default `true`) to delete data in graph before execution"
            };
            rootCommand.Options.Add(optionDelete);

            var optionSolution = new Option<string>("--solution", "-s")
            {
                Description = "optional absolute path to only one `.sln` file (can't be used together with -p / --projects)"
            };
            rootCommand.Options.Add(optionSolution);

            var optionProjects = new Option<string[]>("--projects", "-p")
            {
                Description = "optional list of absolute path to one or many `.csproj` files (can't be used together with -s / --solution)",
                AllowMultipleArgumentsPerToken = true
            };
            rootCommand.Options.Add(optionProjects);

            var optionCache = new Option<string>("--cache")
            {
                Description = "optional path to a directory where MSBuild binary logs are cached; defaults to the platform application data folder (e.g. ~/Library/Application Support/strazh/cache on macOS)"
            };
            rootCommand.Options.Add(optionCache);

            rootCommand.SetAction(async (ParseResult parseResult, CancellationToken token) =>
            {
                await BuildKnowledgeGraph(
                    parseResult.GetValue(optionCredentials),
                    parseResult.GetValue(optionMode),
                    parseResult.GetValue(optionDelete),
                    parseResult.GetValue(optionSolution),
                    parseResult.GetValue(optionProjects),
                    parseResult.GetValue(optionCache));
            });

            return await rootCommand.Parse(args).InvokeAsync();
        }

        private static async Task BuildKnowledgeGraph(string credentials, string tier, string delete, string solution, string[] projects, string? cacheDirectory = null)
        {
            try
            {
                var config = new AnalyzerConfig(
                       credentials,
                       tier,
                       delete,
                       solution,
                       projects,
                       cacheDirectory
                   );
                if (!config.IsValid)
                {
                    Console.WriteLine("Please submit only one thing: `--solution` (-s) or `--projects` (-p)");
                    return;
                }
                var isNeo4jReady = await Healthcheck.IsNeo4jReady();
                if (!isNeo4jReady)
                {
                    Console.WriteLine("Strazh failed to start. There is no Neo4j instance ready to use.");
                    return;
                }

                Console.WriteLine($"Brewing a Code Knowledge Graph of tier \"{config.Tier}\".");
                await Analyzer.Analyze(config);
                Console.WriteLine("Code Knowledge Graph created.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
