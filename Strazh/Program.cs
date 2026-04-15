using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Strazh.Analysis;
using Strazh.Database;

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

            var optionNoCache = new Option<bool>("--no-cache")
            {
                Description = "when set, rebuilds all projects and writes fresh cache entries without reading any existing cached binlogs"
            };
            rootCommand.Options.Add(optionNoCache);

            var optionBuildLogDir = new Option<string>("--build-log-dir")
            {
                Description = "optional path to a directory where per-project MSBuild build logs are written; defaults to the platform application data folder (e.g. ~/Library/Application Support/strazh/logs on macOS)"
            };
            rootCommand.Options.Add(optionBuildLogDir);

            var optionNeo4jUrl = new Option<string>("--neo4j-url", "-u")
            {
                Description = "optional connection URL for the Neo4j database (default `neo4j://localhost:7687`)"
            };
            rootCommand.Options.Add(optionNeo4jUrl);

            rootCommand.SetAction(async (ParseResult parseResult, CancellationToken token) =>
            {
                await BuildKnowledgeGraph(new AnalyzerConfig.Options(
                    Credentials: parseResult.GetValue(optionCredentials),
                    Tier: parseResult.GetValue(optionMode),
                    Delete: parseResult.GetValue(optionDelete),
                    Solution: parseResult.GetValue(optionSolution),
                    Projects: parseResult.GetValue(optionProjects),
                    CacheDirectory: parseResult.GetValue(optionCache),
                    NoCache: parseResult.GetValue(optionNoCache),
                    BuildLogDirectory: parseResult.GetValue(optionBuildLogDir),
                    Neo4jUrl: parseResult.GetValue(optionNeo4jUrl)
                ));
            });

            return await rootCommand.Parse(args).InvokeAsync();
        }

        private static async Task BuildKnowledgeGraph(AnalyzerConfig.Options options)
        {
            try
            {
                var config = new AnalyzerConfig(options);
                if (!config.IsValid)
                {
                    Console.WriteLine("Please submit only one thing: `--solution` (-s) or `--projects` (-p)");
                    return;
                }
                var isNeo4jReady = await Healthcheck.IsNeo4jReady(config.Neo4jUrl);
                if (!isNeo4jReady)
                {
                    Console.WriteLine("Strazh failed to start. There is no Neo4j instance ready to use.");
                    return;
                }

                Console.WriteLine($"Brewing a Code Knowledge Graph of tier \"{config.Tier}\".");
                var runLogPath = Path.Combine(
                    config.BuildLogDirectory!,
                    $"strazh-run-{DateTime.UtcNow:yyyy-MM-ddTHHmmssZ}.log");
                using var fileProgress = new FileAnalysisProgress(runLogPath);
                var progress = new CompositeAnalysisProgress(new SpectreConsoleProgress(), fileProgress);
                var store = new Neo4jTripleStore(config.Credentials, config.Neo4jUrl);
                await Analyzer.Analyze(config, progress, store);
                Console.WriteLine("Code Knowledge Graph created.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
