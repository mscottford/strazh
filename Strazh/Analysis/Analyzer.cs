using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Microsoft.CodeAnalysis;
using Strazh.Domain;
using Buildalyzer;
using Buildalyzer.Workspaces;
using System.Collections.Generic;
using System;
using Strazh.Database;
using static Strazh.Analysis.AnalyzerConfig;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Strazh.Analysis
{
    public static class Analyzer
    {
        public static async Task Analyze(AnalyzerConfig config)
        {
            Console.WriteLine($"Setup analyzer...");

            var manager = config.IsSolutionBased
                ? new AnalyzerManager(config.Solution)
                : new AnalyzerManager();

            var projectAnalyzers = (config.IsSolutionBased
                ? manager.Projects.Values
                : config.Projects.Select(x => manager.GetProject(x))).ToList();

            Console.WriteLine($"Analyzer ready to analyze {projectAnalyzers.Count} project/s.");

            // Delete graph data upfront before parallel analysis begins, so that no project
            // races against the delete.
            if (config.IsDelete)
            {
                await DbManager.DeleteData(config.Credentials);
            }

            var workspace = CreateWorkspace(manager);

            // Limit concurrent Neo4j connections to one per logical processor.
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
            var total = projectAnalyzers.Count;
            var tasks = new List<Task>();
            var index = 0;

            // Stream projects through the Build → Load pipeline and launch an Analyze + Insert
            // task for each one as soon as it becomes available, without waiting for all
            // projects to finish building and loading first.
            await foreach (var entry in StreamProjectsAsync(manager, workspace, config.CacheDirectory))
            {
                var capturedEntry = entry;
                var capturedIndex = index++;

                tasks.Add(Task.Run(async () =>
                {
                    var triples = new List<Triple>();

                    if (config.IsSolutionBased)
                    {
                        var solutionRoot = GetRoot(manager.SolutionFilePath);
                        var solutionRootNode = new FolderNode(solutionRoot, solutionRoot);

                        var solutionName = GetSolutionName(manager.SolutionFilePath);
                        var solutionNode = new SolutionNode(solutionName);
                        triples.Add(new TripleIncludedIn(solutionNode, solutionRootNode));

                        var projectNode = new ProjectNode(GetProjectName(capturedEntry.Item1.Name));
                        triples.Add(new TripleContains(solutionNode, projectNode));
                    }

                    Console.WriteLine($"+ [{capturedIndex + 1}/{total}] {capturedEntry.Item1.Name}: analyze - starting");
                    var projectTriples = await AnalyzeProject(capturedIndex + 1, capturedEntry, config.Tier);
                    Console.WriteLine($"+ [{capturedIndex + 1}/{total}] {capturedEntry.Item1.Name}: analyze - finished");

                    triples.AddRange(projectTriples);

                    Console.WriteLine($"+ [{capturedIndex + 1}/{total}] {capturedEntry.Item1.Name}: grouping - starting");
                    try
                    {
                        triples = triples.GroupBy(x => x.ToString()).Select(x => x.First()).OrderBy(x => x.NodeA.Label)
                            .ToList();
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Error detected. Dumping detailed logging data.");
                        Console.WriteLine("[");
                        var first = true;
                        foreach (var triple in triples)
                        {
                            if (!first)
                            {
                                Console.WriteLine(",");
                            }
                            Console.Write($$"""{ "triple": {{ triple.ToInspection()}} }""");

                            first = false;
                        }
                        if (triples.Any())
                        {
                            Console.WriteLine("");
                        }
                        Console.WriteLine("]");
                        throw;
                    }
                    Console.WriteLine($"+ [{capturedIndex + 1}/{total}] {capturedEntry.Item1.Name}: grouping - finished");

                    Console.WriteLine($"+ [{capturedIndex + 1}/{total}] {capturedEntry.Item1.Name}: insert - starting");
                    await semaphore.WaitAsync();
                    try
                    {
                        await DbManager.InsertData(triples, config.Credentials);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                    Console.WriteLine($"+ [{capturedIndex + 1}/{total}] {capturedEntry.Item1.Name}: insert - finished");
                }));
            }

            await Task.WhenAll(tasks);
            workspace.Dispose();
        }

        public class AnalysisContext(AdhocWorkspace workspace, List<(Project, IAnalyzerResult)> projects)
        {
            public AdhocWorkspace Workspace { get; } = workspace;
            public List<(Project, IAnalyzerResult)> Projects { get; } = projects;
        }
        
        // Based on https://github.com/phmonte/Buildalyzer/blob/9db3390b49dca033fd3f70439bab3a6327440a47/src/Buildalyzer.Workspaces/AnalyzerManagerExtensions.cs#L24-L60
        public static async Task<AnalysisContext> GetAnalysisContext(IAnalyzerManager manager, string? cacheDirectory = null)
        {
            if (manager is null)
            {
                throw new ArgumentNullException(nameof(manager));
            }

            var workspace = CreateWorkspace(manager);
            var projects = new List<(Project, IAnalyzerResult)>();
            await foreach (var item in StreamProjectsAsync(manager, workspace, cacheDirectory))
            {
                projects.Add(item);
            }

            return new AnalysisContext(workspace, projects);
        }

        // Runs all MSBuild design-time builds in parallel (Build stage), waits for all to
        // complete, then feeds results sequentially into the Roslyn AdhocWorkspace (Load stage),
        // yielding each (Project, IAnalyzerResult) pair immediately so the Analyze and Insert
        // stages can begin on each project without waiting for all projects to finish loading.
        //
        // Build and Load cannot be interleaved: AddToWorkspace(addProjectReferences: true) calls
        // analyzer.Build() internally for any referenced project not yet in the workspace. If
        // other builds are still in-flight when this happens, two concurrent builds of the same
        // project race and Buildalyzer's environment detection fails. Waiting for all builds to
        // complete first avoids the race while still streaming Load → Analyze.
        //
        // AdhocWorkspace is not thread-safe, so workspace mutations remain sequential.
        private static async IAsyncEnumerable<(Project, IAnalyzerResult)> StreamProjectsAsync(
            IAnalyzerManager manager, AdhocWorkspace workspace, string? cacheDirectory = null)
        {
            HashSet<string>? projectsNeedingRebuild = null;
            if (cacheDirectory != null)
            {
                Directory.CreateDirectory(cacheDirectory);
                projectsNeedingRebuild = ComputeProjectsNeedingRebuild(manager.Projects.Values, cacheDirectory);
            }

            // Build stage: run MSBuild design-time builds in parallel, capped at the number
            // of logical processors so we don't spawn an unbounded number of dotnet processes
            var buildSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
            IAnalyzerResult?[] results = await Task.WhenAll(
                manager.Projects.Values.Select(p => Task.Run<IAnalyzerResult?>(async () =>
                {
                    await buildSemaphore.WaitAsync();
                    try
                    {
                        IAnalyzerResult? result;
                        if (cacheDirectory != null)
                        {
                            var binlogPath = GetBinlogCachePath(cacheDirectory, p);
                            if (projectsNeedingRebuild!.Contains(p.ProjectFile.Path))
                            {
                                Console.WriteLine($"Build - {p.ProjectFile.Name} - starting");
                                p.AddBinaryLogger(binlogPath);
                                result = p.Build().FirstOrDefault();
                                if (result != null)
                                {
                                    WriteDepsFile(binlogPath, result);
                                }
                                Console.WriteLine($"Build - {p.ProjectFile.Name} - finished");
                            }
                            else
                            {
                                Console.WriteLine($"Build - {p.ProjectFile.Name} - cache hit");
                                result = manager.Analyze(binlogPath).FirstOrDefault();
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Build - {p.ProjectFile.Name} - starting");
                            result = p.Build().FirstOrDefault();
                            Console.WriteLine($"Build - {p.ProjectFile.Name} - finished");
                        }
                        return result;
                    }
                    finally
                    {
                        buildSemaphore.Release();
                    }
                })));

            // Load stage: add each completed result to the workspace and yield immediately,
            // so analysis can begin on each project without waiting for all to be loaded
            foreach (var result in results)
            {
                if (result is null)
                {
                    continue;
                }

                Console.WriteLine($"Load - {Path.GetFileName(result.ProjectFilePath)} - starting");
                var existingProject = workspace.CurrentSolution.Projects
                    .FirstOrDefault(p => p.FilePath == result.ProjectFilePath);
                if (existingProject is null)
                {
                    // AddToWorkspace with addProjectReferences: true eagerly adds referenced projects
                    // into the workspace. Those will be picked up via existingProject on their own
                    // iteration below.
                    var project = result.AddToWorkspace(workspace, true);
                    Console.WriteLine($"Load - {Path.GetFileName(result.ProjectFilePath)} - finished");
                    yield return (project, result);
                }
                else
                {
                    // Already in the workspace because an earlier project pulled it in as a
                    // transitive reference. Still include it so it gets CONTAINS triples and
                    // is analyzed — just reuse the workspace Project object already there.
                    Console.WriteLine($"Load - {Path.GetFileName(result.ProjectFilePath)} - finished");
                    yield return (existingProject, result);
                }
            }
        }

        private static AdhocWorkspace CreateWorkspace(IAnalyzerManager manager)
        {
            var workspace = new AdhocWorkspace();
            if (!string.IsNullOrEmpty(manager.SolutionFilePath))
            {
                SolutionInfo solutionInfo = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, manager.SolutionFilePath);
                workspace.AddSolution(solutionInfo);
            }
            return workspace;
        }

        private static string GetBinlogCachePath(string cacheDirectory, IProjectAnalyzer p)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(p.ProjectFile.Path));
            var hash = Convert.ToHexString(bytes)[..8];
            return Path.Combine(cacheDirectory, $"{p.ProjectFile.Name}_{hash}.binlog");
        }

        // Returns true if no binlog exists, or if the binlog is older than any source or
        // build file in the project directory (excluding obj/ and bin/ output folders).
        private static bool IsBinlogStale(string binlogPath, string projectFilePath)
        {
            if (!File.Exists(binlogPath))
            {
                return true;
            }

            var binlogTime = File.GetLastWriteTimeUtc(binlogPath);

            if (File.GetLastWriteTimeUtc(projectFilePath) > binlogTime)
            {
                return true;
            }

            var projectDir = Path.GetDirectoryName(projectFilePath)!;
            var trackedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".cs", ".fs", ".vb", ".props", ".targets" };

            foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (trackedExtensions.Contains(Path.GetExtension(file)) &&
                    File.GetLastWriteTimeUtc(file) > binlogTime)
                {
                    return true;
                }
            }

            return false;
        }

        // Writes the fully-evaluated project references from a build result as a sidecar next to
        // the binlog. Subsequent runs read this instead of parsing the project file, so all
        // MSBuild evaluation (conditions, imports, Directory.Build.props, etc.) is accounted for.
        private static void WriteDepsFile(string binlogPath, IAnalyzerResult result) =>
            File.WriteAllLines(binlogPath + ".deps", result.ProjectReferences);

        // Reads the deps sidecar written by a previous build. Returns empty when the file does
        // not exist (first run, or cache cleared) — the project will be stale for other reasons.
        private static IReadOnlyList<string> ReadDepsFile(string binlogPath)
        {
            var depsPath = binlogPath + ".deps";
            return File.Exists(depsPath) ? File.ReadAllLines(depsPath) : [];
        }

        // Determines which projects must be rebuilt, accounting for transitive dependencies:
        // if project B needs a rebuild and project A references B, A is also marked for rebuild.
        // The dependency graph is reconstructed from the sidecar files written by previous builds,
        // so MSBuild's own evaluation (conditions, imports, etc.) is used — not our own parsing.
        private static HashSet<string> ComputeProjectsNeedingRebuild(
            IEnumerable<IProjectAnalyzer> projects, string cacheDirectory)
        {
            var projectMap = projects.ToDictionary(p => p.ProjectFile.Path, StringComparer.OrdinalIgnoreCase);

            var depGraph = projectMap.ToDictionary(
                kvp => kvp.Key,
                kvp => ReadDepsFile(GetBinlogCachePath(cacheDirectory, kvp.Value))
                    .Where(r => projectMap.ContainsKey(r))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            var needsRebuild = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, analyzer) in projectMap)
            {
                if (IsBinlogStale(GetBinlogCachePath(cacheDirectory, analyzer), path))
                {
                    needsRebuild.Add(path);
                }
            }

            // Propagate: if a referenced project needs a rebuild, so does the referencing project
            bool changed;
            do
            {
                changed = false;
                foreach (var (path, refs) in depGraph)
                {
                    if (!needsRebuild.Contains(path) && refs.Any(r => needsRebuild.Contains(r)))
                    {
                        needsRebuild.Add(path);
                        changed = true;
                    }
                }
            } while (changed);

            return needsRebuild;
        }

        private static async Task<IList<Triple>> AnalyzeProject(int index, (Project project, IAnalyzerResult projectAnalyzerResult) item, Tiers mode)
        {
            Console.WriteLine($"Project #{index}:");
            var root = GetRoot(item.project.FilePath);
            var rootNode = new FolderNode(root, root);
            var projectName = GetProjectName(item.project.Name);
            Console.WriteLine($"Analyzing {projectName} project...");

            var triples = new List<Triple>();
            if (mode == Tiers.All || mode == Tiers.Project)
            {
                Console.WriteLine($"Analyzing Project tier...");
                var projectNode = new ProjectNode(projectName);
                triples.Add(new TripleIncludedIn(projectNode, rootNode));
                item.projectAnalyzerResult.ProjectReferences.ToList().ForEach(x =>
                {
                    var node = new ProjectNode(GetProjectName(x));
                    triples.Add(new TripleDependsOnProject(projectNode, node));
                });
                item.projectAnalyzerResult.PackageReferences.ToList().ForEach(x =>
                {
                    var version = x.Value.Values.FirstOrDefault(x => x.Contains(".")) ?? "none";
                    var node = new PackageNode(x.Key, x.Key, version);
                    triples.Add(new TripleDependsOnPackage(projectNode, node));
                });
                Console.WriteLine($"Analyzing Project tier complete.");
            }

            if (item.project.SupportsCompilation
                && (mode == Tiers.All || mode == Tiers.Code))
            {
                Console.WriteLine($"Analyzing Code tier...");
                var compilation = await item.project.GetCompilationAsync();
                var syntaxTreeRoot = compilation.SyntaxTrees.Where(x => !x.FilePath.Contains("obj"));
                foreach (var st in syntaxTreeRoot)
                {
                    var sem = compilation.GetSemanticModel(st);
                    Extractor.AnalyzeTree<InterfaceDeclarationSyntax>(triples, st, sem, rootNode);
                    Extractor.AnalyzeTree<ClassDeclarationSyntax>(triples, st, sem, rootNode);
                }
                Console.WriteLine($"Analyzing Code tier complete.");
            }

            Console.WriteLine($"Analyzing {projectName} project complete.");
            return triples;
        }
        
        private static string GetSolutionName(string fullName)
            => fullName.Split(Path.DirectorySeparatorChar).Last().Replace(".sln", "");

        private static string GetProjectName(string fullName)
            => fullName.Split(Path.DirectorySeparatorChar).Last().Replace(".csproj", "");

        private static string GetRoot(string filePath)
            => filePath.Split(Path.DirectorySeparatorChar).Reverse().Skip(1).FirstOrDefault();
    }
}