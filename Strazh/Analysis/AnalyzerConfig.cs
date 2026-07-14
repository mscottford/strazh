using System;
using System.IO;
using System.Linq;

namespace Strazh.Analysis
{
    public class AnalyzerConfig
    {
        public class CredentialsConfig
        {
            public string Database { get; } = "";
            public string User { get; } = "";
            public string Password { get; } = "";

            public CredentialsConfig(string? credentials)
            {
                if (!string.IsNullOrEmpty(credentials))
                {
                    var args = credentials.Split(":");
                    if (args.Length == 3)
                    {
                        Database = args[0];
                        User = args[1];
                        Password = args[2];
                    }
                }
            }
        }

        public enum Tiers : int
        {
            All = 0,
            Project = 1,
            Code = 2
        }

        public record Options(
            string? Credentials,
            string? Tier,
            string? Delete,
            string? Solution,
            string[]? Projects,
            string? Directory = null,
            string? CacheDirectory = null,
            bool NoCache = false,
            string? BuildLogDirectory = null,
            string? Neo4jUrl = null
        );

        public CredentialsConfig Credentials { get; }
        public Tiers Tier { get; }
        public string Solution { get; }
        public string[] Projects { get; }
        public string Directory { get; }
        public bool IsDelete { get; }
        public string CacheDirectory { get; }
        public bool NoCache { get; }
        public string BuildLogDirectory { get; }
        public string Neo4jUrl { get; }

        public bool IsSolutionBased => !string.IsNullOrEmpty(Solution);

        public bool IsDirectoryBased => !string.IsNullOrEmpty(Directory);

        // Exactly one analysis source must be given: a single solution, an explicit list of
        // projects, or a directory to scan for both.
        public bool IsValid =>
            ((IsSolutionBased ? 1 : 0) + (Projects.Length > 0 ? 1 : 0) + (IsDirectoryBased ? 1 : 0)) == 1;

        public AnalyzerConfig(Options options)
        {
            var solution = options.Solution == "none" ? "" : options.Solution;
            var directory = options.Directory == "none" ? "" : options.Directory;
            Credentials = new CredentialsConfig(options.Credentials);
            Tier = MapTier(options.Tier);
            IsDelete = options.Delete != "false";
            Solution = string.IsNullOrEmpty(solution) ? "" : Path.GetFullPath(solution);
            Projects = (options.Projects ?? Array.Empty<string>())
                .Select(Path.GetFullPath)
                .ToArray();
            Directory = string.IsNullOrEmpty(directory) ? "" : Path.GetFullPath(directory);
            // SpecialFolder.ApplicationData returns empty string on Linux when $HOME is unset
            // (e.g. inside certain Docker images). Fall back through UserProfile and $HOME to
            // the system temp directory so the cache path is always absolute and usable.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
                }
                appData = Path.Combine(home, ".config");
            }
            var strazhDataDir = Path.Combine(appData, "strazh");
            CacheDirectory = string.IsNullOrEmpty(options.CacheDirectory)
                ? Path.Combine(strazhDataDir, "cache")
                : options.CacheDirectory;
            BuildLogDirectory = string.IsNullOrEmpty(options.BuildLogDirectory)
                ? Path.Combine(strazhDataDir, "logs")
                : options.BuildLogDirectory;
            NoCache = options.NoCache;
            Neo4jUrl = string.IsNullOrEmpty(options.Neo4jUrl) ? "neo4j://localhost:7687" : options.Neo4jUrl;
        }

        private Tiers MapTier(string? mode)
            => (mode ?? "").ToLowerInvariant() switch
                {
                    "project" => Tiers.Project,
                    "code" => Tiers.Code,
                    _ => Tiers.All,
                };
    }
}