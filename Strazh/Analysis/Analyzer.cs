using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Microsoft.CodeAnalysis;
using Strazh.Domain;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Workspaces;
using System.Collections.Generic;
using System;
using System.Collections.Immutable;
using Strazh.Database;
using static Strazh.Analysis.AnalyzerConfig;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Strazh.Analysis
{
    public static class Analyzer
    {
        public static async Task Analyze(AnalyzerConfig config, IAnalysisProgress progress, ITripleStore store)
        {
            Console.WriteLine($"Setup analyzer...");

            Directory.CreateDirectory(config.BuildLogDirectory);
            var buildalyzerLogPath = Path.Combine(config.BuildLogDirectory, $"buildalyzer-{DateTime.UtcNow:yyyy-MM-ddTHHmmssZ}.log");
            using var buildalyzerLog = TextWriter.Synchronized(new StreamWriter(buildalyzerLogPath, append: false));
            var managerOptions = new AnalyzerManagerOptions { LogWriter = buildalyzerLog };
            var manager = config.IsSolutionBased
                ? new AnalyzerManager(IOPath.Parse(config.Solution), managerOptions)
                : new AnalyzerManager(managerOptions);

            // Directory mode: discover every solution and project beneath the directory so they
            // are analyzed in a single build/load pass instead of the caller making separate
            // per-solution and project-sweep runs. The projects flow through the normal
            // project-based pipeline below (so loose projects not in any solution are still
            // captured); the solutions are recorded afterwards to add Solution + CONTAINS edges.
            var directorySolutions = config.IsDirectoryBased
                ? DiscoverFiles(config.Directory, "*.sln")
                : Array.Empty<string>();
            var directoryProjects = config.IsDirectoryBased
                ? DiscoverFiles(config.Directory, "*.csproj")
                : Array.Empty<string>();

            if (config.IsDirectoryBased)
            {
                Console.WriteLine(
                    $"Scanning \"{config.Directory}\": found {directorySolutions.Length} solution/s and {directoryProjects.Length} project/s.");
            }

            var projectAnalyzers = (config.IsSolutionBased
                ? manager.Projects.Values
                : (config.IsDirectoryBased ? directoryProjects : config.Projects)
                    .Select(x => manager.GetProject(IOPath.Parse(x)))).ToList();

            Console.WriteLine($"Analyzer ready to analyze {projectAnalyzers.Count} project/s.");

            // Delete graph data upfront before parallel analysis begins, so that no project
            // races against the delete.
            if (config.IsDelete)
            {
                await store.DeleteAllAsync();
            }

            // Ensure uniqueness constraints (and their implicit indexes) exist for every node
            // label before any MERGE operations run. Without indexes, each MERGE does a full
            // label scan and performance degrades linearly as the database grows.
            await store.EnsureIndexesAsync();

            var workspace = CreateWorkspace(manager);

            // Limit concurrent store connections to one per logical processor.
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
            var analysisTasks = new List<Task>();

            // Project file paths that reached the stream (were built and loaded). Anything left out
            // was dropped by the build pipeline and gets a fallback node after the stream.
            var emittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Projects that built but could not be loaded into the workspace, keyed by project file
            // path. These carry a succeeded result, so they are represented from it (with references,
            // not flagged buildFailed) rather than from the static project file. Populated from the
            // sequential Load-stage foreach below, so a plain dictionary is safe.
            var builtButNotLoaded = new Dictionary<string, IAnalyzerResult>(StringComparer.OrdinalIgnoreCase);

            // Stream projects through the Build → Load pipeline and launch an Analyze + Insert
            // task for each one as soon as it becomes available, without waiting for all
            // projects to finish building and loading first.
            await progress.WrapAsync(projectAnalyzers.Count, async () =>
            {
                await foreach (var entry in StreamProjectsAsync(manager, workspace,
                    new AnalysisOptions(
                        new StreamCallbacks
                        {
                            OnBuildStarted = (path, name, isCacheHit, buildLabel) => progress.OnBuildStarted(path, name, isCacheHit, buildLabel),
                            OnBuildCompleted = path => progress.OnBuildCompleted(path),
                            OnLoadStarted = path => progress.OnStageChanged(path, GetProjectName(path), "Loading"),
                            OnProjectDeferred = (path, filename, reason) => progress.OnProjectDeferred(path, filename, reason),
                            OnProjectWarning = (path, filename, reason) => progress.OnProjectWarning(path, filename, reason),
                            OnProjectBuiltButNotLoaded = result => builtButNotLoaded[result.ProjectFilePath] = result
                        },
                        config.CacheDirectory,
                        config.NoCache,
                        config.BuildLogDirectory)))
                {
                    var capturedEntry = entry;
                    var projectPath = capturedEntry.Item2.ProjectFilePath;
                    emittedPaths.Add(projectPath);
                    var projectDisplayName = GetProjectName(capturedEntry.Item1.Name);

                    progress.OnStageChanged(projectPath, projectDisplayName, "Analyzing");

                    analysisTasks.Add(Task.Run(async () =>
                    {
                        var triples = new List<Triple>();

                        if (config.IsSolutionBased)
                        {
                            triples.AddRange(GetSolutionAndRepositoryTriples(
                                manager.Solution!.Path.ToString(),
                                capturedEntry.Item1.FilePath,
                                GetProjectName(capturedEntry.Item1.Name)));
                        }

                        var projectTriples = await AnalyzeProject(capturedEntry, config.Tier);
                        triples.AddRange(projectTriples);

                        progress.OnStageChanged(projectPath, projectDisplayName, "Grouping");
                        try
                        {
                            triples = triples.GroupBy(x => x.ToString()).Select(x => x.First()).OrderBy(x => x.NodeA.Label)
                                .ToList();
                        }
                        catch (Exception)
                        {
                            progress.OnGroupingError(projectDisplayName, triples);
                            throw;
                        }

                        progress.OnStageChanged(projectPath, projectDisplayName, "Inserting");
                        await semaphore.WaitAsync();
                        try
                        {
                            await store.InsertAsync(triples);
                        }
                        finally
                        {
                            semaphore.Release();
                        }

                        progress.OnProjectCompleted(projectPath, triples.Count);
                    }));
                }

                await Task.WhenAll(analysisTasks);

                // Now drain the deferred queue: every project the stream dropped (build failed /
                // timed out / unreadable log / not loadable) is still pending work in the display.
                // Represent each so none is silently missing — from its succeeded result if it
                // built but could not be loaded (references preserved, not flagged buildFailed), or
                // from its static project file otherwise (flagged buildFailed). Running inside the
                // progress scope keeps these visible as work being completed rather than lost.
                if (config.Tier == Tiers.All || config.Tier == Tiers.Project)
                {
                    await RecordDroppedProjectsAsync(projectAnalyzers, emittedPaths, builtButNotLoaded, store, progress);
                }
            });

            workspace.Dispose();

            // Directory mode: now that every project has been analyzed, add the solution-level
            // structure. Membership is parsed from each .sln (no build needed); the projects it
            // lists already exist as nodes from the stream above, so this only MERGEs the
            // Solution, CONTAINS, and repository/folder triples on top.
            if (config.IsDirectoryBased)
            {
                await RecordSolutionsAsync(directorySolutions, managerOptions, store);
            }
        }

        // Records the Solution, CONTAINS, and repository/folder triples for every solution found
        // during a directory scan. Membership comes from parsing each .sln — no build — so the
        // projects it lists get a CONTAINS edge exactly as a per-solution run would emit. The
        // project/package/code triples themselves come from the project stream, so this only
        // layers the solution structure on top.
        private static async Task RecordSolutionsAsync(
            IEnumerable<string> solutionPaths,
            AnalyzerManagerOptions managerOptions,
            ITripleStore store)
        {
            foreach (var solutionPath in solutionPaths)
            {
                var solutionName = GetSolutionName(solutionPath);

                // Let Buildalyzer parse the solution, but keep going if it can't. A directory
                // scan sweeps up every .sln, including ones MSBuild's solution parser rejects —
                // e.g. a legacy solution that still references a .vcproj. Rather than aborting the
                // whole run (the old per-solution-process flow only tolerated this because each
                // solution ran in its own process), record the Solution node flagged buildFailed
                // so it is still represented — its membership just couldn't be analyzed — and move
                // on to the next solution.
                IAnalyzerManager solutionManager;
                try
                {
                    solutionManager = new AnalyzerManager(IOPath.Parse(solutionPath), managerOptions);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Recording solution \"{solutionName}\" as not analyzed: {ex.Message}");
                    var solutionRoot = GetRoot(solutionPath);
                    // Buildalyzer failed to parse the solution, but git resolution is independent, so
                    // the Solution/Folder are still versioned by (and linked to) the repo's HEAD commit.
                    var solutionCommit = GitHelper.GetCommitNode(solutionPath);
                    var solutionSha = solutionCommit?.Sha;
                    var solutionNode = new SolutionNode(solutionName, buildFailed: true, commitSha: solutionSha);
                    var solutionRootNode = new FolderNode(solutionRoot, solutionRoot, solutionSha);
                    var fallbackTriples = new List<Triple>
                    {
                        new TripleIncludedIn(solutionNode, solutionRootNode),
                    };
                    AddFromCommit(fallbackTriples, solutionNode, solutionCommit);
                    AddFromCommit(fallbackTriples, solutionRootNode, solutionCommit);
                    await store.InsertAsync(fallbackTriples);
                    continue;
                }

                var triples = new List<Triple>();
                foreach (var project in solutionManager.Projects.Values)
                {
                    var projectPath = project.ProjectFile.Path;
                    triples.AddRange(GetSolutionAndRepositoryTriples(
                        solutionPath, projectPath, GetProjectName(projectPath)));
                }
                triples = triples.GroupBy(x => x.ToString()).Select(g => g.First()).ToList();
                Console.WriteLine(
                    $"Recording solution \"{solutionName}\" ({solutionManager.Projects.Count} project/s).");
                await store.InsertAsync(triples);
            }
        }

        // Enumerates files matching a pattern beneath root, skipping build output (bin/obj) so a
        // project's compiled copies under obj/ are not mistaken for source projects.
        private static string[] DiscoverFiles(string root, string pattern)
            => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(path => !IsInBuildOutput(root, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static bool IsInBuildOutput(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            return Regex.IsMatch(relative, @"(^|/)(bin|obj)/");
        }

        // Records every project the streaming pipeline dropped (deferred): projects that never
        // reached the stream because their build failed / timed out / produced an unreadable log,
        // or that built but could not be loaded into the workspace. Each is represented from its
        // build result when one is available (references preserved, not flagged buildFailed) or
        // from its static project file otherwise (flagged buildFailed), so none is silently missing.
        // Intended to run inside the progress scope so deferred projects are seen completed, not lost.
        public static async Task RecordDroppedProjectsAsync(
            IEnumerable<IProjectAnalyzer?> projectAnalyzers,
            HashSet<string> emittedPaths,
            IReadOnlyDictionary<string, IAnalyzerResult> builtButNotLoaded,
            ITripleStore store,
            IAnalysisProgress progress)
        {
            foreach (var analyzer in projectAnalyzers)
            {
                if (analyzer is null)
                {
                    continue; // GetProject returned null for an unresolvable path
                }
                string path;
                try
                {
                    path = analyzer.ProjectFile.Path;
                }
                catch
                {
                    continue; // cannot even identify this project
                }
                if (emittedPaths.Contains(path))
                {
                    continue;
                }

                var filename = Path.GetFileName(path);
                var displayName = GetProjectName(path);
                builtButNotLoaded.TryGetValue(path, out var builtResult);
                try
                {
                    progress.OnStageChanged(path, displayName, "Recording");
                    var triples = BuildDroppedProjectTriples(analyzer, builtResult)
                        .GroupBy(x => x.ToString()).Select(g => g.First()).ToList();
                    await store.InsertAsync(triples);
                    progress.OnProjectRecordedFromFallback(path, filename, triples.Count, buildFailed: builtResult == null);
                }
                catch
                {
                    // Even the fallback could not represent it (e.g. the project file itself is
                    // unreadable) — report it as a genuine terminal skip.
                    progress.OnProjectSkipped(path, filename, "could not be recorded from fallback");
                }
            }
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
            await foreach (var item in StreamProjectsAsync(manager, workspace,
                new AnalysisOptions(Callbacks: default, cacheDirectory, NoCache: false, BuildLogDirectory: null)))
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
        private readonly struct StreamCallbacks
        {
            public Action<string, string, bool, BuildStageLabel>? OnBuildStarted { get; init; }
            public Action<string>? OnBuildCompleted { get; init; }

            // Raised when the Load stage actually begins adding a project to the Roslyn workspace,
            // as opposed to the project merely sitting built-and-queued. Lets progress separate
            // real load time (AddToWorkspace, which is sequential and can trigger inline builds)
            // from the queue wait after its build finished.
            public Action<string>? OnLoadStarted { get; init; }
            // A project that could not be analyzed normally (build failed / timed out / unreadable
            // log / not loadable into the workspace). It is not terminal: the project stays pending
            // work and is represented from fallback data after the stream.
            public Action<string, string, string>? OnProjectDeferred { get; init; }

            // A non-terminal, self-recovered issue worth surfacing (e.g. a corrupt cached binlog
            // discarded and rebuilt). Purely informational — the project's normal events follow.
            public Action<string, string, string>? OnProjectWarning { get; init; }

            // A project whose build succeeded but which could not be loaded into the Roslyn
            // workspace (unsupported project type, or a dangling reference that breaks workspace
            // resolution). Its result still carries Project-tier facts, so it is represented from
            // the result rather than being dropped to the static fallback.
            public Action<IAnalyzerResult>? OnProjectBuiltButNotLoaded { get; init; }
        }

        private readonly record struct AnalysisOptions(
            StreamCallbacks Callbacks,
            string? CacheDirectory,
            bool NoCache,
            string? BuildLogDirectory);

        private readonly record struct CacheContext(
            string Directory,
            HashSet<string> ProjectsNeedingRebuild);

        // Carries the full context for one build stage (Building or PostBuild).
        private readonly record struct BuildStageContext(
            IAnalyzerManager Manager,
            CacheContext? Cache,
            StreamCallbacks Callbacks,
            BuildStageLabel BuildLabel,
            bool IsScanPass,
            string? BuildLogDirectory);

        private readonly record struct ProjectIdentity(string Path, string Name, string FileName);

        // Bundles the three values needed to build and track one project within a stage.
        private readonly record struct ProjectBuildState(
            IProjectAnalyzer Project,
            ProjectIdentity Identity,
            string BinlogPath);

        private static async IAsyncEnumerable<(Project, IAnalyzerResult)> StreamProjectsAsync(
            IAnalyzerManager manager, AdhocWorkspace workspace, AnalysisOptions options = default)
        {
            if (options.BuildLogDirectory != null)
            {
                Directory.CreateDirectory(options.BuildLogDirectory);
            }

            HashSet<string>? projectsNeedingRebuild = null;
            if (options.CacheDirectory != null)
            {
                Directory.CreateDirectory(options.CacheDirectory);
                projectsNeedingRebuild = options.NoCache
                    ? manager.Projects.Values
                        .Select(p => p.ProjectFile.Path)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : ComputeProjectsNeedingRebuild(manager.Projects.Values, options.CacheDirectory);
            }

            // Building pass: if any project is missing a .deps sidecar the dependency graph is
            // incomplete, so we cannot reliably detect transitive staleness or know which
            // projects are safe to build in parallel without their dependencies present.
            // Run full MSBuild invocations for all projects to populate the binlog cache and
            // .deps files, then re-derive the rebuild set from the now-complete graph before
            // the PostBuild pass. On warm-cache runs all .deps files exist and this is skipped.
            if (options.CacheDirectory != null && manager.Projects.Values.Any(
                    p => !File.Exists(GetBinlogCachePath(options.CacheDirectory, p) + ".deps")))
            {
                await RunBuildStageAsync(new BuildStageContext(
                    manager, new CacheContext(options.CacheDirectory, projectsNeedingRebuild!),
                    options.Callbacks, BuildLabel: BuildStageLabel.Building, IsScanPass: true, options.BuildLogDirectory));
                // Re-derive the rebuild set without the noCache override — the Building pass
                // just built everything fresh, so nothing should be considered stale.
                projectsNeedingRebuild = ComputeProjectsNeedingRebuild(manager.Projects.Values, options.CacheDirectory);
            }

            // PostBuild pass: replays the binlog cache for each project (or runs a fresh MSBuild
            // build if the cache is stale), then loads the results into the Roslyn workspace for
            // triple extraction, grouping, and insertion into the graph database. Each project may
            // target multiple frameworks; results from all TFMs are analyzed independently.
            // A project is considered successful if at least one TFM produces a result.
            // After a Building pass this is mostly cache replays (fast); on warm-cache runs it
            // rebuilds only the stale subset.
            CacheContext? mainCache = options.CacheDirectory != null
                ? new CacheContext(options.CacheDirectory, projectsNeedingRebuild!)
                : null;
            IReadOnlyList<IAnalyzerResult>?[] results = await RunBuildStageAsync(new BuildStageContext(
                manager, mainCache, options.Callbacks, BuildLabel: BuildStageLabel.PostBuild, IsScanPass: false, options.BuildLogDirectory));

            // Load stage: add each completed result to the workspace and yield immediately,
            // so analysis can begin on each project without waiting for all to be loaded.
            // Results are sorted in dependency order so every project's references are already
            // in the workspace when AddToWorkspace runs, preventing it from triggering redundant
            // MSBuild builds for references it cannot find there yet.
            //
            // For multi-target projects all TFMs share the same ProjectId (derived from the file
            // path), so only the first TFM result is used to add the project to the workspace.
            // Each TFM result is then yielded with that shared workspace project so that
            // TFM-specific package/project references are each captured as triples.
            //
            // After AddToWorkspace, the Roslyn Project object carries the authoritative project
            // references (resolved by MSBuild and stored in the workspace). We patch any
            // IAnalyzerResult whose ProjectReferences is empty (e.g. from binlog replay) with
            // these paths, then overwrite the deps sidecar so subsequent staleness checks work.
            var binlogPathMap = options.CacheDirectory != null
                ? manager.Projects.Values.ToDictionary(
                    p => p.ProjectFile.Path,
                    p => GetBinlogCachePath(options.CacheDirectory, p),
                    StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (var tfmGroup in TopologicalSort(results))
            {
                var primaryResult = tfmGroup.First(r => r.Succeeded);
                options.Callbacks.OnLoadStarted?.Invoke(primaryResult.ProjectFilePath);

                var existingProject = workspace.CurrentSolution.Projects
                    .FirstOrDefault(p => p.FilePath == primaryResult.ProjectFilePath);
                if (existingProject is null)
                {
                    // AddToWorkspace with addProjectReferences: true eagerly adds referenced projects
                    // into the workspace. Those will be picked up via existingProject on their own
                    // iteration below.
                    Project? project;
                    try
                    {
                        project = primaryResult.AddToWorkspace(workspace, true);
                    }
                    catch (Exception)
                    {
                        // AddToWorkspace adds this project to the workspace, then eagerly resolves its
                        // <ProjectReference>s; a reference to a file not on disk (a dangling/stale
                        // reference) makes that resolution throw AFTER the project itself is already
                        // added. Recover the already-added project so it still lands in the graph — its
                        // Project-tier triples (target frameworks, package/project references) come from
                        // the IAnalyzerResult and its own source stays analyzable; only the workspace's
                        // resolved reference links (which strazh does not use to build triples) are lost.
                        project = workspace.CurrentSolution.Projects
                            .FirstOrDefault(p => p.FilePath == primaryResult.ProjectFilePath);
                        if (project is null)
                        {
                            // The build succeeded, so the result carries Project-tier facts (target
                            // frameworks, package/project references) even though code analysis is
                            // impossible without the workspace project. Represent it from the result.
                            options.Callbacks.OnProjectBuiltButNotLoaded?.Invoke(primaryResult);
                            options.Callbacks.OnProjectDeferred?.Invoke(primaryResult.ProjectFilePath, Path.GetFileName(primaryResult.ProjectFilePath), "could not load into workspace");
                            continue;
                        }
                    }
                    if (project is null)
                    {
                        // AddToWorkspace returns null for project types not supported by Roslyn
                        // (e.g. F# projects, native projects). The build still succeeded, so
                        // represent the project from its result before skipping code analysis.
                        options.Callbacks.OnProjectBuiltButNotLoaded?.Invoke(primaryResult);
                        options.Callbacks.OnProjectDeferred?.Invoke(primaryResult.ProjectFilePath, Path.GetFileName(primaryResult.ProjectFilePath), "unsupported project type");
                        continue;
                    }
                    var patchedGroup = PatchProjectRefsFromWorkspace(tfmGroup, project, workspace);
                    if (binlogPathMap != null && binlogPathMap.TryGetValue(primaryResult.ProjectFilePath, out var bp))
                    {
                        WriteDepsFile(bp, patchedGroup.First(r => r.Succeeded).ProjectReferences.ToList());
                    }
                    foreach (var result in patchedGroup)
                    {
                        yield return (project, result);
                    }
                }
                else
                {
                    // Already in the workspace because an earlier project pulled it in as a
                    // transitive reference. Still include it so it gets CONTAINS triples and
                    // is analyzed — just reuse the workspace Project object already there.
                    var patchedGroup = PatchProjectRefsFromWorkspace(tfmGroup, existingProject, workspace);
                    if (binlogPathMap != null && binlogPathMap.TryGetValue(primaryResult.ProjectFilePath, out var bp))
                    {
                        WriteDepsFile(bp, patchedGroup.First(r => r.Succeeded).ProjectReferences.ToList());
                    }
                    foreach (var result in patchedGroup)
                    {
                        yield return (existingProject, result);
                    }
                }
            }
        }

        // Sorts build results so that every project appears after all of its project
        // references that are also in the result set. Without this ordering,
        // AddToWorkspace(addProjectReferences: true) encounters references not yet in
        // the workspace and calls analyzer.Build() on them — bypassing our binlog cache
        // and running full, sequential in-process MSBuild evaluations for each one.
        // A DFS post-order traversal over the dependency graph produces the correct order.
        //
        // For multi-target projects, all TFMs share the same file path and project
        // references, so the first TFM result is used for dependency ordering and the
        // full group is emitted together.
        private static IReadOnlyList<IReadOnlyList<IAnalyzerResult>> TopologicalSort(IReadOnlyList<IAnalyzerResult>?[] results)
        {
            var byPath = results
                .Where(r => r is not null)
                .ToDictionary(r => r!.First(x => x.Succeeded).ProjectFilePath, r => r!, StringComparer.OrdinalIgnoreCase);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sorted = new List<IReadOnlyList<IAnalyzerResult>>(byPath.Count);

            void Visit(IReadOnlyList<IAnalyzerResult> tfmGroup)
            {
                var primaryResult = tfmGroup.First(r => r.Succeeded);
                if (!visited.Add(primaryResult.ProjectFilePath))
                {
                    return;
                }
                foreach (var refPath in primaryResult.ProjectReferences)
                {
                    if (byPath.TryGetValue(refPath, out var refGroup))
                    {
                        Visit(refGroup);
                    }
                }
                sorted.Add(tfmGroup);
            }

            foreach (var tfmGroup in byPath.Values)
            {
                Visit(tfmGroup);
            }

            return sorted;
        }

        // Runs all MSBuild builds in parallel (capped at logical-processor count) and returns
        // one result slot per project (null for skipped/failed projects).
        //
        // buildLabel is forwarded to OnBuildStarted so the progress UI can distinguish
        // Building (dependency-discovery pass) from PostBuild (post-build analysis pass).
        //
        // isScanPass suppresses OnProjectSkipped for failures: scan failures are pre-work
        // noise that will be reported properly by the main pass. OnBuildCompleted is called
        // instead so the active-display entry is cleared.
        private static Task<IReadOnlyList<IAnalyzerResult>?[]> RunBuildStageAsync(BuildStageContext stage)
        {
            var buildSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
            return Task.WhenAll(
                stage.Manager.Projects.Values.Select(project => Task.Run<IReadOnlyList<IAnalyzerResult>?>(async () =>
                {
                    await buildSemaphore.WaitAsync();
                    try
                    {
                        return await BuildSingleProjectAsync(project, stage);
                    }
                    finally
                    {
                        buildSemaphore.Release();
                    }
                })));
        }

        private static async Task<IReadOnlyList<IAnalyzerResult>?> BuildSingleProjectAsync(
            IProjectAnalyzer project, BuildStageContext stage)
        {
            var identity = new ProjectIdentity(
                project.ProjectFile.Path,
                GetProjectName(project.ProjectFile.Name),
                Path.GetFileName(project.ProjectFile.Path));
            if (stage.Cache != null)
            {
                return await BuildProjectWithCacheAsync(project, identity, stage);
            }
            return await BuildProjectWithoutCacheAsync(project, identity, stage);
        }

        private static async Task<IReadOnlyList<IAnalyzerResult>?> BuildProjectWithCacheAsync(
            IProjectAnalyzer project, ProjectIdentity identity, BuildStageContext stage)
        {
            var binlogPath = GetBinlogCachePath(stage.Cache!.Value.Directory, project);
            var state = new ProjectBuildState(project, identity, binlogPath);
            var cached = TryReplayCachedBinlog(state, stage);
            if (cached != null)
            {
                return cached;
            }
            return await RunFreshBuildLoopAsync(state, stage);
        }

        // Attempts to replay a cached binlog. Returns the results on success, or null if the
        // project needs a fresh build (stale, missing, or the replay produced no succeeded results).
        private static IReadOnlyList<IAnalyzerResult>? TryReplayCachedBinlog(
            ProjectBuildState state, BuildStageContext stage)
        {
            if (stage.Cache!.Value.ProjectsNeedingRebuild.Contains(state.Identity.Path) || !File.Exists(state.BinlogPath))
            {
                return null;
            }
            stage.Callbacks.OnBuildStarted?.Invoke(state.Identity.Path, state.Identity.Name, true, stage.BuildLabel);
            IReadOnlyList<IAnalyzerResult> cached;
            try
            {
                cached = RealTfmResults(stage.Manager.Analyze(state.BinlogPath));
            }
            catch (FileNotFoundException)
            {
                // File.Exists passed but the file was deleted by a concurrent task between the
                // check and the read (e.g. another project's retry loop sharing the same binlog
                // path due to a hash-prefix collision). Report it, then fall through to a fresh build.
                stage.Callbacks.OnProjectWarning?.Invoke(
                    state.Identity.Path, state.Identity.FileName,
                    "cached build log vanished mid-read (concurrent rebuild) — rebuilding");
                return null;
            }
            catch (EndOfStreamException)
            {
                // Truncated/corrupt cached binlog left over from a prior run. Unlike a freshly-built
                // binlog this one is not still being written, so re-reading it would not help.
                // Report it, then discard it and fall through to a fresh build.
                stage.Callbacks.OnProjectWarning?.Invoke(
                    state.Identity.Path, state.Identity.FileName,
                    "cached build log was unreadable (truncated) — rebuilding");
                return null;
            }
            if (cached.Any(r => r.Succeeded))
            {
                // Project references are patched from the Roslyn workspace in the
                // Load stage, which also overwrites the deps sidecar. Nothing to
                // do here beyond signalling completion.
                stage.Callbacks.OnBuildCompleted?.Invoke(state.Identity.Path);
                return cached;
            }
            return null; // Stale or corrupted binlog — caller falls through to a fresh build.
        }

        // Runs up to MaxBuildAttempts fresh MSBuild invocations, deleting a bad binlog between
        // retries. Returns results on the first successful attempt, or null after all attempts fail.
        private static async Task<IReadOnlyList<IAnalyzerResult>?> RunFreshBuildLoopAsync(
            ProjectBuildState state, BuildStageContext stage)
        {
            string? lastBuildError = null;
            for (var attempt = 1; attempt <= MaxBuildAttempts; attempt++)
            {
                stage.Callbacks.OnBuildStarted?.Invoke(state.Identity.Path, state.Identity.Name, false, stage.BuildLabel);
                var outcome = await BuildWithTimeoutAsync(state.Project, CreateBuildOptions(stage.BuildLogDirectory, state.Identity.Path, binlogPath: state.BinlogPath));
                if (outcome.TimedOut)
                {
                    SignalBuildNotCompleted(state.Identity, reason: "build timed out", stage);
                    return null;
                }
                if (outcome.BuildError != null)
                {
                    lastBuildError = outcome.BuildError;
                }
                // Read from the binlog rather than the pipe result. The pipe uses
                // MsBuildPipeLogger, which may not support the event types emitted by
                // the SDK's MSBuild version, leaving the pipe result empty even on a
                // successful build. The binlog is written natively by MSBuild and read
                // by StructuredLogger, which handles the current format regardless of
                // version skew.
                var binlogExists = File.Exists(state.BinlogPath);
                var tfmResults = binlogExists ? await TryAnalyzeBinlogAsync(stage.Manager, state.BinlogPath, stage, state.Identity) : [];
                if (tfmResults.Any(r => r.Succeeded))
                {
                    // Write an initial deps sidecar with whatever project references
                    // MSBuild returned. Binlog replay often leaves this empty (MSBuild
                    // 17.14+ does not emit ProjectReferences into the replay). The Load
                    // stage patches from the Roslyn workspace and overwrites the file
                    // with the correct paths so subsequent staleness checks are accurate.
                    WriteDepsFile(state.BinlogPath, tfmResults.First(r => r.Succeeded).ProjectReferences.ToList());
                    stage.Callbacks.OnBuildCompleted?.Invoke(state.Identity.Path);
                    return tfmResults;
                }
                else if (attempt < MaxBuildAttempts)
                {
                    if (binlogExists)
                    {
                        File.Delete(state.BinlogPath);
                    }
                }
                else
                {
                    var reason = binlogExists
                        ? "build log could not be read"
                        : lastBuildError != null
                            ? $"build failed: {SummarizeBuildError(lastBuildError)}"
                            : "build failed";
                    SignalBuildNotCompleted(state.Identity, reason, stage);
                    return null;
                }
            }
            return null; // unreachable — loop always returns
        }

        private static async Task<IReadOnlyList<IAnalyzerResult>?> BuildProjectWithoutCacheAsync(
            IProjectAnalyzer project, ProjectIdentity identity, BuildStageContext stage)
        {
            for (var attempt = 1; attempt <= MaxBuildAttempts; attempt++)
            {
                stage.Callbacks.OnBuildStarted?.Invoke(identity.Path, identity.Name, false, stage.BuildLabel);
                var outcome = await BuildWithTimeoutAsync(project, CreateBuildOptions(stage.BuildLogDirectory, identity.Path));
                if (outcome.TimedOut)
                {
                    SignalBuildNotCompleted(identity, reason: "build timed out", stage);
                    return null;
                }
                if (outcome.Results != null && outcome.Results.Any(r => r.Succeeded))
                {
                    stage.Callbacks.OnBuildCompleted?.Invoke(identity.Path);
                    return outcome.Results;
                }
                if (attempt == MaxBuildAttempts)
                {
                    var reason = outcome.BuildError != null
                        ? $"build failed: {SummarizeBuildError(outcome.BuildError)}"
                        : "build failed";
                    SignalBuildNotCompleted(identity, reason, stage);
                    return null;
                }
            }
            return null; // unreachable — loop always returns
        }

        private static void SignalBuildNotCompleted(ProjectIdentity identity, string reason, BuildStageContext stage)
        {
            if (stage.IsScanPass)
            {
                stage.Callbacks.OnBuildCompleted?.Invoke(identity.Path);
            }
            else
            {
                stage.Callbacks.OnProjectDeferred?.Invoke(identity.Path, identity.FileName, reason);
            }
        }

        // Reduces a build exception message to a single trimmed line, capped in length, so a deferred
        // project's reason stays readable in the progress output and logs.
        private static string SummarizeBuildError(string message)
        {
            var firstLine = message.Split('\n', 2)[0].Trim();
            return firstLine.Length > 120 ? firstLine[..120] + "…" : firstLine;
        }

        // If a build hangs (e.g. Android/MAUI projects waiting on SDK tools not present in the
        // environment), WhenAny returns after the timeout and we skip the project. The underlying
        // Task.Run continues to hold its thread until the process eventually exits or is reaped
        // when the parent process terminates — that is acceptable.
        private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
        private const int MaxBuildAttempts = 3;
        // A just-built binlog may not be fully flushed when project.Build() returns; re-read it a few
        // times with a short delay before giving up and rebuilding (see TryAnalyzeBinlogAsync).
        private const int MaxBinlogReadAttempts = 3;
        private static readonly TimeSpan BinlogReadRetryDelay = TimeSpan.FromMilliseconds(500);
        // MD5 produces 32 hex chars; 8 is enough to make collisions negligible across a solution.
        private const int HashPrefixLength = 8;

        // Reads a freshly-built binlog, retrying the read after a short delay while it is not yet
        // usable. MSBuild's child process closes its stdout pipe (so project.Build() returns) before
        // the BinaryLogger is guaranteed to have flushed the whole file to disk, so an immediate
        // replay can yield no results, run off the end of a partial file (EndOfStreamException), or
        // momentarily not find the file at all (FileNotFoundException — e.g. a concurrent retry
        // sharing the binlog path deleted it mid-rebuild). All three are transient — the write
        // finishes, or the sibling rebuild completes, moments later — so we re-read the same binlog
        // rather than treating it as a failed build. Once the read retries are exhausted we return []
        // so the caller rebuilds; if the reads kept throwing, that last exception is surfaced via a
        // project warning (shown in the progress display and written to the file log) rather than
        // swallowed. Returns all TFM results (one per target framework).
        private static async Task<IReadOnlyList<IAnalyzerResult>> TryAnalyzeBinlogAsync(
            IAnalyzerManager manager, string binlogPath, BuildStageContext stage, ProjectIdentity identity)
        {
            string? lastReadError = null;
            for (var attempt = 1; attempt <= MaxBinlogReadAttempts; attempt++)
            {
                try
                {
                    var results = RealTfmResults(manager.Analyze(binlogPath));
                    if (results.Count > 0)
                    {
                        return results;
                    }
                    // Zero results without an error usually means the binlog's tail has not flushed
                    // yet; retry. An empty-but-readable log is a normal outcome (handled by a rebuild),
                    // so it is not surfaced as a warning.
                }
                catch (FileNotFoundException exception)
                {
                    // Momentarily absent: a concurrent retry sharing this binlog path (hash-prefix
                    // collision) may have deleted it mid-rebuild. Retry in case it reappears.
                    lastReadError = $"binlog not found ({exception.Message})";
                }
                catch (EndOfStreamException exception)
                {
                    // Truncated binlog: the BinaryLogger has not finished writing. Retry after a delay.
                    lastReadError = $"binlog truncated ({exception.Message})";
                }
                if (attempt < MaxBinlogReadAttempts)
                {
                    await Task.Delay(BinlogReadRetryDelay).ConfigureAwait(false);
                }
            }
            // Read retries exhausted. If the reads kept throwing, surface the last exception (progress
            // warning + file log) instead of swallowing it; the caller still falls back to a rebuild.
            if (lastReadError != null)
            {
                stage.Callbacks.OnProjectWarning?.Invoke(
                    identity.Path, identity.FileName,
                    $"could not read build log after {MaxBinlogReadAttempts} attempts — {lastReadError}; rebuilding");
            }
            return [];
        }

        private readonly record struct BuildOutcome(IReadOnlyList<IAnalyzerResult>? Results, bool TimedOut, string? BuildError = null);

        private static async Task<BuildOutcome> BuildWithTimeoutAsync(IProjectAnalyzer project, EnvironmentOptions opts)
        {
            string? buildError = null;
            var buildTask = Task.Run<IReadOnlyList<IAnalyzerResult>?>(() =>
            {
                try
                {
                    var results = RealTfmResults(project.Build(opts));
                    return results.Count > 0 ? results : null;
                }
                catch (Exception exception)
                {
                    // Buildalyzer throws for a project it cannot build at all — e.g. "Could not find
                    // build environment" when the SDK/workload for that project type (Xamarin/MAUI or
                    // an older toolset) is not installed. Record the message and return no results so
                    // the project is retried and then deferred like any other build failure, instead
                    // of the exception propagating and aborting the whole analysis run.
                    buildError = exception.Message;
                    return null;
                }
            });
            if (await Task.WhenAny(buildTask, Task.Delay(BuildTimeout)).ConfigureAwait(false) != buildTask)
            {
                return new BuildOutcome(Results: null, TimedOut: true);
            }
            // buildTask has completed, so the catch block's write to buildError is visible here.
            return new BuildOutcome(await buildTask.ConfigureAwait(false), TimedOut: false, BuildError: buildError);
        }

        // Filters out entries that don't correspond to a real target-framework build.
        //
        // When building via p.Build(), MSBuild emits an outer evaluation result (TargetFramework="")
        // plus one inner result per TFM (TargetFramework="netstandard2.1", etc.). For single-TFM
        // projects the outer result also has Succeeded=true, so filtering by Succeeded alone
        // returns two results per project instead of one.
        //
        // When replaying a binlog via manager.Analyze(), the TargetFramework property is not
        // populated even for real TFM builds (it comes from an MSBuild property that isn't
        // captured in the replay), so filtering by non-empty TargetFramework removes everything.
        //
        // Solution: prefer results with non-empty TargetFramework (inner TFM builds from p.Build()),
        // and fall back to all Succeeded results only when none have a non-empty TargetFramework
        // (the binlog-replay case). The Succeeded filter also handles multi-target projects where
        // some TFMs fail and others succeed.
        private static IReadOnlyList<IAnalyzerResult> RealTfmResults(IEnumerable<IAnalyzerResult> results)
        {
            var succeeded = results.Where(r => r.Succeeded).ToList();
            var innerTfm = succeeded.Where(r => !string.IsNullOrEmpty(r.TargetFramework)).ToList();
            return innerTfm.Count > 0 ? innerTfm : succeeded;
        }

        private static EnvironmentOptions CreateBuildOptions(string? buildLogDirectory, string projectFilePath, string? binlogPath = null)
        {
            var opts = new EnvironmentOptions();
            opts.Arguments.Add("/nodeReuse:false");
            if (binlogPath != null)
            {
                // MSBuild writes the binlog directly in the child process. This avoids running the
                // BinaryLogger on the host-side pipe, which can cause deserialization errors when the
                // SDK's MSBuild version is newer than the MsBuildPipeLogger the host uses.
                opts.Arguments.Add($"\"/bl:{binlogPath}\"");
            }
            if (buildLogDirectory != null)
            {
                var displayName = GetProjectName(Path.GetFileName(projectFilePath));
                var bytes = MD5.HashData(Encoding.UTF8.GetBytes(projectFilePath));
                var hash = Convert.ToHexString(bytes)[..HashPrefixLength];
                var logFile = Path.Combine(buildLogDirectory, $"{displayName}_{hash}.log");
                opts.Arguments.Add($"\"/fileLoggerParameters:LogFile={logFile};Verbosity=normal;Append\"");
            }
            return opts;
        }

        private static AdhocWorkspace CreateWorkspace(IAnalyzerManager manager)
        {
            var workspace = new AdhocWorkspace();
            if (manager.Solution is { } solution && solution.Path.HasValue)
            {
                Microsoft.CodeAnalysis.SolutionInfo solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, solution.Path.ToString());
                workspace.AddSolution(solutionInfo);
            }
            return workspace;
        }

        private static string GetBinlogCachePath(string cacheDirectory, IProjectAnalyzer project)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(project.ProjectFile.Path));
            var hash = Convert.ToHexString(bytes)[..HashPrefixLength];
            return Path.Combine(cacheDirectory, $"{project.ProjectFile.Name}_{hash}.binlog");
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

        // Writes the project references as a sidecar next to the binlog. The list comes from
        // either the build result (preferred: fully MSBuild-evaluated) or a project-file parse
        // fallback. Subsequent runs read this instead of re-evaluating the project file.
        private static void WriteDepsFile(string binlogPath, IReadOnlyList<string> projectReferences) =>
            File.WriteAllLines(binlogPath + ".deps", projectReferences);

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

        // Builds all triples for a solution and its associated git repository for a
        // single project entry. Called once per project inside the parallel analysis tasks.
        //
        // Each project is attributed to its OWN git repository (which may be a submodule
        // of the solution's repo), not the solution's. This keeps the graph stable across
        // scans: scanning a submodule directly and scanning a host repo that includes it
        // both produce identical Folder PKs and Repository associations for the submodule's
        // contents, so a folder containing a submodule project is never tied to the host repo.
        private static IList<Triple> GetSolutionAndRepositoryTriples(
            string solutionFilePath,
            string? projectFilePath,
            string projectName)
        {
            var triples = new List<Triple>();

            // The solution and every folder are versioned by the HEAD commit of the repo that
            // physically contains them, so the same solution/folder checked out at two commits
            // becomes two distinct nodes (matching how projects, files, and code are versioned).
            var solutionCommit = GitHelper.GetCommitNode(solutionFilePath);
            var solutionSha = solutionCommit?.Sha;

            var solutionRoot = GetRoot(solutionFilePath);
            var solutionRootNode = new FolderNode(solutionRoot, solutionRoot, solutionSha);

            var solutionName = GetSolutionName(solutionFilePath);
            var solutionNode = new SolutionNode(solutionName, commitSha: solutionSha);
            triples.Add(new TripleIncludedIn(solutionNode, solutionRootNode));
            AddFromCommit(triples, solutionNode, solutionCommit);
            AddFromCommit(triples, solutionRootNode, solutionCommit);

            // Host repository root folder. Naming uses the natural repo name from origin
            // (last segment of "owner/repo") rather than the local clone's directory
            // basename, so PKs stay stable regardless of where the user cloned the repo.
            var solutionRepoName = GitHelper.GetRepositoryName(solutionFilePath);
            var solutionGitRoot = GitHelper.FindGitRoot(solutionFilePath);
            var solutionRepoFolder = solutionRepoName != null
                ? AttachRepoRoot(triples, solutionRepoName, solutionCommit)
                : null;

            // Submodule mount points declared in the host repo's .gitmodules: a
            // Folder(kind=Submodule) at "<host>/<mountPath>", INCLUDED_IN both the host's
            // repo-root folder and the referenced Repository node.
            if (solutionRepoFolder != null)
            {
                foreach (var sub in GitHelper.GetSubmodules(solutionFilePath))
                {
                    var mountPath = sub.MountPath.Replace('\\', '/');

                    // The mount point is a gitlink in the HOST tree, so the folder itself belongs to
                    // the host's commit: INCLUDED_IN the host folder tree and FROM the host
                    // commit (which HAS-attributes it to the host repository).
                    var mountFolder = new FolderNode(
                        $"{solutionRepoFolder.Name}/{mountPath}",
                        Path.GetFileName(mountPath),
                        FolderKind.Submodule,
                        solutionSha);
                    triples.Add(new TripleIncludedIn(mountFolder, solutionRepoFolder));
                    AddFromCommit(triples, mountFolder, solutionCommit);

                    // The mount directory's HEAD is the submodule's pinned (detached) commit. The
                    // folder PINS it, and HAS attributes that commit to the submodule's own
                    // repository — so the submodule repo is discovered via the pinned commit rather
                    // than a direct folder→repository edge.
                    var pinnedCommit = solutionGitRoot != null
                        ? GitHelper.GetCommitNode(Path.Combine(solutionGitRoot, sub.MountPath))
                        : null;
                    if (pinnedCommit != null)
                    {
                        triples.Add(new TriplePins(mountFolder, pinnedCommit));
                        AddRepositoryOwns(triples, pinnedCommit);
                    }
                }
            }

            // The project's own git root determines its repository. For a project living
            // inside a submodule, this is the submodule's git root — not the solution's.
            var projectPath = projectFilePath;
            var projectCommit = projectPath != null ? GitHelper.GetCommitNode(projectPath) : null;
            var projectSha = projectCommit?.Sha;
            var projectGitRoot = projectPath != null ? GitHelper.FindGitRoot(projectPath) : null;
            var projectRepoName = projectPath != null ? GitHelper.GetRepositoryName(projectPath) : null;
            var projectIsInSubmodule = projectGitRoot != null
                && solutionGitRoot != null
                && !projectGitRoot.Equals(solutionGitRoot, StringComparison.OrdinalIgnoreCase);

            var projectRoot = projectPath != null ? GetRoot(projectPath) : null;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                var projectRootNode = new FolderNode(projectRoot, projectRoot, projectSha);
                // Submodule project → attach to its own repo's root folder. Host project →
                // attach to the solution root (existing behavior). If we can't identify
                // the submodule's repo, leave the folder un-attached rather than misattributing.
                var parentFolder = projectIsInSubmodule
                    ? (projectRepoName != null ? AttachRepoRoot(triples, projectRepoName, projectCommit) : null)
                    : solutionRootNode;
                if (parentFolder != null
                    && !projectRoot.Equals(parentFolder.Name, StringComparison.OrdinalIgnoreCase))
                {
                    triples.Add(new TripleIncludedIn(projectRootNode, parentFolder));
                    AddFromCommit(triples, projectRootNode, projectCommit);
                }
            }

            // Version the CONTAINS target by the project's commit so it MERGEs onto the same node
            // BuildProjectTriples produces (which also carries the target frameworks and FROM).
            var projectNode = new ProjectNode(projectName, projectName, commitSha: projectSha);
            triples.Add(new TripleContains(solutionNode, projectNode));

            return triples;
        }

        // Creates the repo-root folder (named by the repo's short name so its pk is stable
        // regardless of the local clone directory) versioned by, and linked via FROM to,
        // the repo's HEAD commit when one could be resolved. No direct folder→repository edge is
        // emitted: the folder's repository is discovered through its commit (Repository-[:HAS]->Commit,
        // recorded by AddFromCommit).
        private static FolderNode AttachRepoRoot(List<Triple> triples, string repoName, CommitNode? commit)
        {
            var folderName = Path.GetFileName(repoName);
            var folder = new FolderNode(folderName, folderName, commit?.Sha);
            AddFromCommit(triples, folder, commit);
            return folder;
        }

        // Links a versioned node to the commit it was seen in and records that commit under its
        // repository, so the node's repository is reachable transitively (node -FROM-> Commit
        // <-HAS- Repository). A path outside a readable git repo yields a null commit and the node
        // stays unversioned with no edges.
        private static void AddFromCommit(List<Triple> triples, Node node, CommitNode? commit)
        {
            if (commit == null)
            {
                return;
            }
            triples.Add(new TripleFrom(node, commit));
            AddRepositoryOwns(triples, commit);
        }

        // Records Repository-[:HAS]->Commit. Commits with no resolvable origin (a repo with no
        // remote) carry an empty repo name and get no HAS edge, rather than a dangling empty-named
        // Repository node.
        private static void AddRepositoryOwns(List<Triple> triples, CommitNode commit)
        {
            if (!string.IsNullOrEmpty(commit.Repo))
            {
                triples.Add(new TripleHas(new RepositoryNode(commit.Repo), commit));
            }
        }

        private static async Task<IList<Triple>> AnalyzeProject((Project project, IAnalyzerResult projectAnalyzerResult) item, Tiers mode)
        {
            var triples = new List<Triple>();
            if (mode == Tiers.All || mode == Tiers.Project)
            {
                triples.AddRange(BuildProjectTriples(item.projectAnalyzerResult));
            }

            if (item.project.SupportsCompilation
                && (mode == Tiers.All || mode == Tiers.Code))
            {
                var root = GetRoot(item.project.FilePath);
                var rootNode = new FolderNode(root, root);
                var compilation = await item.project.GetCompilationAsync();
                if (compilation != null)
                {
                    var syntaxTreeRoot = compilation.SyntaxTrees.Where(x => !x.FilePath.Contains("obj"));
                    foreach (var st in syntaxTreeRoot)
                    {
                        var sem = compilation.GetSemanticModel(st);
                        Extractor.AnalyzeTree<InterfaceDeclarationSyntax>(triples, st, sem, rootNode);
                        Extractor.AnalyzeTree<ClassDeclarationSyntax>(triples, st, sem, rootNode);
                    }
                }
            }

            return triples;
        }

        // Builds the Project-tier triples (the project node, its folder, and its project/package
        // dependency edges) purely from an IAnalyzerResult, so a project can be represented from
        // whatever Buildalyzer collected even when its build did not succeed (Buildalyzer still
        // reports target frameworks and references). Target frameworks come from the static project
        // file (the result's analyzer, else the manager) — replay-proof, since
        // IAnalyzerResult.TargetFramework is empty on binlog cache replays; the single result TFM is
        // a last-resort fallback. Reference targets whose .csproj is missing are flagged exists=false,
        // and a non-succeeded build marks the node buildFailed.
        public static IList<Triple> BuildProjectTriples(IAnalyzerResult result)
        {
            var triples = new List<Triple>();
            var path = result.ProjectFilePath;
            var projectName = GetProjectName(path);
            var root = GetRoot(path);

            // The project (and its root folder) is versioned by its own git root's HEAD, so copies
            // of the same project at different commits become distinct nodes. FROM is emitted
            // here (the guaranteed per-project path) so it fires exactly once per represented project.
            var projectCommit = GitHelper.GetCommitNode(path);
            var projectSha = projectCommit?.Sha;
            var rootNode = new FolderNode(root, root, projectSha);

            var analyzer = result.Analyzer ?? result.Manager?.GetProject(IOPath.Parse(path));
            var targetFrameworks = analyzer?.ProjectFile?.TargetFrameworks ?? Array.Empty<string>();
            if (targetFrameworks.Length == 0 && !string.IsNullOrEmpty(result.TargetFramework))
            {
                targetFrameworks = new[] { result.TargetFramework };
            }

            // buildFailed is not set from a single result: only succeeded results reach this path
            // (RealTfmResults forwards only succeeded builds), and marking it here would mismark a
            // multi-target project when one TFM fails but another succeeds. buildFailed is decided
            // per project by the fallback sweep, which runs only when no build succeeded at all.
            var projectNode = new ProjectNode(projectName, projectName, targetFrameworks, commitSha: projectSha);
            triples.Add(new TripleIncludedIn(projectNode, rootNode));
            AddFromCommit(triples, projectNode, projectCommit);
            AddFromCommit(triples, rootNode, projectCommit);
            result.ProjectReferences.ToList().ForEach(x =>
            {
                var refName = GetProjectName(x);
                // Version the reference target by the referenced project's own commit, so it MERGEs
                // onto the same node that project produces when it is itself analyzed.
                var refSha = GitHelper.GetCommit(x)?.Sha;
                triples.Add(new TripleDependsOnProject(projectNode, new ProjectNode(refName, refName, null, File.Exists(x), commitSha: refSha)));
            });
            result.PackageReferences.ToList().ForEach(x =>
            {
                var version = x.Value.Values.FirstOrDefault(v => v.Contains(".")) ?? "none";
                triples.Add(new TripleDependsOnPackage(projectNode, new PackageNode(x.Key, x.Key, version)));
            });
            return triples;
        }
        
        // Builds minimal Project-tier triples from a project's static project file, for a project the
        // build pipeline dropped (no IAnalyzerResult — e.g. an unreadable binlog or a timed-out build).
        // Buildalyzer still parses target frameworks and package references from the file; project
        // references aren't available without a build. The node is flagged buildFailed.
        public static IList<Triple> BuildProjectTriples(IProjectAnalyzer analyzer)
        {
            var projectFile = analyzer.ProjectFile;
            var triples = new List<Triple>();
            var projectName = GetProjectName(projectFile.Path);
            var root = GetRoot(projectFile.Path);

            var projectCommit = GitHelper.GetCommitNode(projectFile.Path);
            var rootNode = new FolderNode(root, root, projectCommit?.Sha);
            var projectNode = new ProjectNode(projectName, projectName, projectFile.TargetFrameworks, exists: true, buildFailed: true, commitSha: projectCommit?.Sha);
            triples.Add(new TripleIncludedIn(projectNode, rootNode));
            AddFromCommit(triples, projectNode, projectCommit);
            AddFromCommit(triples, rootNode, projectCommit);
            foreach (var package in projectFile.PackageReferences)
            {
                var version = string.IsNullOrEmpty(package.Version) ? "none" : package.Version;
                triples.Add(new TripleDependsOnPackage(projectNode, new PackageNode(package.Name, package.Name, version)));
            }
            return triples;
        }

        // Represents a project the streaming pipeline did not fully analyze. If the project built
        // but could not be loaded into the workspace, its succeeded result is available and carries
        // its references, so it is represented from that (and is not a build failure). Otherwise
        // (build failed, timed out, or an unreadable build log) only the static project file remains,
        // and the project is flagged buildFailed.
        public static IList<Triple> BuildDroppedProjectTriples(IProjectAnalyzer? analyzer, IAnalyzerResult? builtButNotLoadedResult)
            => builtButNotLoadedResult != null
                ? BuildProjectTriples(builtButNotLoadedResult)
                // No result means the project never built, so the caller always supplies the analyzer.
                : BuildProjectTriples(analyzer!);

        private static string GetSolutionName(string fullName)
            => fullName.Split(Path.DirectorySeparatorChar).Last().Replace(".sln", "");

        private static string GetProjectName(string fullName)
            => fullName.Split(Path.DirectorySeparatorChar).Last().Replace(".csproj", "");

        private static string GetRoot(string? filePath)
            => (filePath ?? "").Split(Path.DirectorySeparatorChar).Reverse().Skip(1).FirstOrDefault() ?? "";

        // If any result in the group already has project references, the group is returned
        // unchanged. Otherwise, Roslyn's project reference graph (populated by AddToWorkspace)
        // is used to derive the absolute .csproj paths and wrap each result so that graph
        // triples and topological sort are correct on cached runs.
        private static IReadOnlyList<IAnalyzerResult> PatchProjectRefsFromWorkspace(
            IReadOnlyList<IAnalyzerResult> tfmGroup, Project roslynProject, AdhocWorkspace workspace)
        {
            if (tfmGroup.Any(r => r.ProjectReferences.Any()))
            {
                return tfmGroup;
            }
            var refPaths = roslynProject.ProjectReferences
                .Select(pr => workspace.CurrentSolution.GetProject(pr.ProjectId)?.FilePath)
                .OfType<string>()
                .ToList();
            if (refPaths.Count == 0)
            {
                return tfmGroup;
            }
            return tfmGroup
                .Select(r => (IAnalyzerResult)new AnalyzerResultWithProjectRefs(r, refPaths))
                .ToList();
        }

        // Wraps an IAnalyzerResult and overrides ProjectReferences with a caller-supplied list.
        // Used to attach workspace-derived project reference paths to binlog results, which do
        // not populate ProjectReferences during replay (MSBuild 17.14+ / .NET 10 SDK).
        private sealed class AnalyzerResultWithProjectRefs(IAnalyzerResult inner, IReadOnlyList<string> projectReferences) : IAnalyzerResult
        {
            public IEnumerable<string> ProjectReferences => projectReferences;

            // All other members delegate to the wrapped result.
            public ProjectAnalyzer Analyzer => inner.Analyzer;
            public IReadOnlyDictionary<string, IProjectItem[]> Items => inner.Items;
            public AnalyzerManager Manager => inner.Manager;
            public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageReferences => inner.PackageReferences;
            public string ProjectFilePath => inner.ProjectFilePath;
            public Guid ProjectGuid => inner.ProjectGuid;
            public IReadOnlyDictionary<string, string> Properties => inner.Properties;
            public string[] References => inner.References;
            public ImmutableDictionary<string, ImmutableArray<string>> ReferenceAliases => inner.ReferenceAliases;
            public string[] AnalyzerReferences => inner.AnalyzerReferences;
            public string[] SourceFiles => inner.SourceFiles;
            public bool Succeeded => inner.Succeeded;
            public string TargetFramework => inner.TargetFramework;
            public string[] PreprocessorSymbols => inner.PreprocessorSymbols;
            public string[] AdditionalFiles => inner.AdditionalFiles;
            public string Command => inner.Command;
            public string CompilerFilePath => inner.CompilerFilePath;
            public string[] CompilerArguments => inner.CompilerArguments;
            public string GetProperty(string name) => inner.GetProperty(name);
        }
    }
}