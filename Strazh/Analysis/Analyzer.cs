using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Microsoft.CodeAnalysis;
using Strazh.Domain;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.Workspaces;
using System.Collections.Generic;
using System;
using System.Collections.Immutable;
using Strazh.Database;
using static Strazh.Analysis.AnalyzerConfig;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
                ? new AnalyzerManager(config.Solution, managerOptions)
                : new AnalyzerManager(managerOptions);

            var projectAnalyzers = (config.IsSolutionBased
                ? manager.Projects.Values
                : config.Projects.Select(x => manager.GetProject(x))).ToList();

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
            // was dropped by the build pipeline and gets a static fallback node after the stream.
            var emittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                            OnProjectSkipped = (path, filename, reason) => progress.OnProjectSkipped(path, filename, reason)
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
                            triples.AddRange(GetSolutionAndRepositoryTriples(manager, capturedEntry));
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
            });

            // Fallback: any project the build pipeline dropped (build failed, binlog unreadable,
            // timed out, unsupported, or unloadable) never reached the stream. Emit a minimal node
            // for each from its static project file so no project is silently missing from the graph.
            if (config.Tier == Tiers.All || config.Tier == Tiers.Project)
            {
                foreach (var analyzer in projectAnalyzers)
                {
                    try
                    {
                        if (emittedPaths.Contains(analyzer.ProjectFile.Path))
                        {
                            continue;
                        }
                        var triples = BuildProjectTriples(analyzer)
                            .GroupBy(x => x.ToString()).Select(g => g.First()).ToList();
                        await store.InsertAsync(triples);
                    }
                    catch
                    {
                        // The project file itself could not be read — nothing to record for it.
                    }
                }
            }

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
            public Action<string, string, string>? OnProjectSkipped { get; init; }
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
                            options.Callbacks.OnProjectSkipped?.Invoke(primaryResult.ProjectFilePath, Path.GetFileName(primaryResult.ProjectFilePath), "could not load into workspace");
                            continue;
                        }
                    }
                    if (project is null)
                    {
                        // AddToWorkspace returns null for project types not supported by Roslyn
                        // (e.g. F# projects, native projects). Skip them — they cannot be analyzed.
                        options.Callbacks.OnProjectSkipped?.Invoke(primaryResult.ProjectFilePath, Path.GetFileName(primaryResult.ProjectFilePath), "unsupported project type");
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
                // path due to a hash-prefix collision). Fall through to a fresh build.
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
            for (var attempt = 1; attempt <= MaxBuildAttempts; attempt++)
            {
                stage.Callbacks.OnBuildStarted?.Invoke(state.Identity.Path, state.Identity.Name, false, stage.BuildLabel);
                var outcome = await BuildWithTimeoutAsync(state.Project, CreateBuildOptions(stage.BuildLogDirectory, state.Identity.Path, binlogPath: state.BinlogPath));
                if (outcome.TimedOut)
                {
                    SignalBuildNotCompleted(state.Identity, reason: "build timed out", stage);
                    return null;
                }
                // Read from the binlog rather than the pipe result. The pipe uses
                // MsBuildPipeLogger, which may not support the event types emitted by
                // the SDK's MSBuild version, leaving the pipe result empty even on a
                // successful build. The binlog is written natively by MSBuild and read
                // by StructuredLogger, which handles the current format regardless of
                // version skew.
                var binlogExists = File.Exists(state.BinlogPath);
                var tfmResults = binlogExists ? await TryAnalyzeBinlogAsync(stage.Manager, state.BinlogPath) : [];
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
                    var reason = binlogExists ? "build log could not be read" : "build failed";
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
                    SignalBuildNotCompleted(identity, reason: "build failed", stage);
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
                stage.Callbacks.OnProjectSkipped?.Invoke(identity.Path, identity.FileName, reason);
            }
        }

        // If a build hangs (e.g. Android/MAUI projects waiting on SDK tools not present in the
        // environment), WhenAny returns after the timeout and we skip the project. The underlying
        // Task.Run continues to hold its thread until the process eventually exits or is reaped
        // when the parent process terminates — that is acceptable.
        private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
        private const int MaxBuildAttempts = 3;
        // MD5 produces 32 hex chars; 8 is enough to make collisions negligible across a solution.
        private const int HashPrefixLength = 8;

        // Reads the binlog, retrying once after a short delay if the first attempt yields no
        // results. The MSBuild child process closes its stdout pipe (causing p.Build() to return)
        // before the BinaryLogger's file write is guaranteed to be fully flushed to disk. A single
        // retry covers the common case where the OS buffers have not yet been committed.
        // Returns all TFM results from the binlog (one per target framework for multi-target projects).
        private static async Task<IReadOnlyList<IAnalyzerResult>> TryAnalyzeBinlogAsync(IAnalyzerManager manager, string binlogPath)
        {
            IReadOnlyList<IAnalyzerResult> results;
            try
            {
                results = RealTfmResults(manager.Analyze(binlogPath));
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            if (results.Count > 0)
            {
                return results;
            }
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                return RealTfmResults(manager.Analyze(binlogPath));
            }
            catch (FileNotFoundException)
            {
                return [];
            }
        }

        private readonly record struct BuildOutcome(IReadOnlyList<IAnalyzerResult>? Results, bool TimedOut);

        private static async Task<BuildOutcome> BuildWithTimeoutAsync(IProjectAnalyzer project, EnvironmentOptions opts)
        {
            var buildTask = Task.Run<IReadOnlyList<IAnalyzerResult>?>(() =>
            {
                var results = RealTfmResults(project.Build(opts));
                return results.Count > 0 ? results : null;
            });
            if (await Task.WhenAny(buildTask, Task.Delay(BuildTimeout)).ConfigureAwait(false) != buildTask)
            {
                return new BuildOutcome(Results: null, TimedOut: true);
            }
            return new BuildOutcome(await buildTask.ConfigureAwait(false), TimedOut: false);
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
            if (!string.IsNullOrEmpty(manager.SolutionFilePath))
            {
                Microsoft.CodeAnalysis.SolutionInfo solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, manager.SolutionFilePath);
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
            IAnalyzerManager manager,
            (Project project, IAnalyzerResult result) entry)
        {
            var triples = new List<Triple>();

            var solutionRoot = GetRoot(manager.SolutionFilePath);
            var solutionRootNode = new FolderNode(solutionRoot, solutionRoot);

            var solutionName = GetSolutionName(manager.SolutionFilePath);
            var solutionNode = new SolutionNode(solutionName);
            triples.Add(new TripleIncludedIn(solutionNode, solutionRootNode));

            // Host repository root folder. Naming uses the natural repo name from origin
            // (last segment of "owner/repo") rather than the local clone's directory
            // basename, so PKs stay stable regardless of where the user cloned the repo.
            var solutionRepoName = GitHelper.GetRepositoryName(manager.SolutionFilePath);
            var solutionGitRoot = GitHelper.FindGitRoot(manager.SolutionFilePath);
            var solutionRepoFolder = solutionRepoName != null
                ? AttachRepoRoot(triples, solutionRepoName)
                : null;

            // Submodule mount points declared in the host repo's .gitmodules: a
            // Folder(kind=Submodule) at "<host>/<mountPath>", INCLUDED_IN both the host's
            // repo-root folder and the referenced Repository node.
            if (solutionRepoFolder != null)
            {
                foreach (var sub in GitHelper.GetSubmodules(manager.SolutionFilePath))
                {
                    var mountPath = sub.MountPath.Replace('\\', '/');
                    var mountFolder = new FolderNode(
                        $"{solutionRepoFolder.Name}/{mountPath}",
                        Path.GetFileName(mountPath),
                        FolderKind.Submodule);
                    triples.Add(new TripleIncludedIn(mountFolder, solutionRepoFolder));
                    triples.Add(new TripleIncludedIn(mountFolder, new RepositoryNode(sub.RepositoryName)));
                }
            }

            // The project's own git root determines its repository. For a project living
            // inside a submodule, this is the submodule's git root — not the solution's.
            var projectPath = entry.Item1.FilePath;
            var projectGitRoot = projectPath != null ? GitHelper.FindGitRoot(projectPath) : null;
            var projectRepoName = projectPath != null ? GitHelper.GetRepositoryName(projectPath) : null;
            var projectIsInSubmodule = projectGitRoot != null
                && solutionGitRoot != null
                && !projectGitRoot.Equals(solutionGitRoot, StringComparison.OrdinalIgnoreCase);

            var projectRoot = projectPath != null ? GetRoot(projectPath) : null;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                var projectRootNode = new FolderNode(projectRoot, projectRoot);
                // Submodule project → attach to its own repo's root folder. Host project →
                // attach to the solution root (existing behavior). If we can't identify
                // the submodule's repo, leave the folder un-attached rather than misattributing.
                var parentFolder = projectIsInSubmodule
                    ? (projectRepoName != null ? AttachRepoRoot(triples, projectRepoName) : null)
                    : solutionRootNode;
                if (parentFolder != null
                    && !projectRoot.Equals(parentFolder.Name, StringComparison.OrdinalIgnoreCase))
                {
                    triples.Add(new TripleIncludedIn(projectRootNode, parentFolder));
                }
            }

            var projectNode = new ProjectNode(GetProjectName(entry.Item1.Name));
            triples.Add(new TripleContains(solutionNode, projectNode));

            return triples;
        }

        // Emits Folder(<natural-repo-name>) -INCLUDED_IN-> Repository(<owner/repo>) and
        // returns the folder. The folder name comes from the last segment of the repo
        // name, so PKs are stable regardless of local clone directory naming.
        private static FolderNode AttachRepoRoot(List<Triple> triples, string repoName)
        {
            var folderName = Path.GetFileName(repoName);
            var folder = new FolderNode(folderName, folderName);
            triples.Add(new TripleIncludedIn(folder, new RepositoryNode(repoName)));
            return folder;
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
                var syntaxTreeRoot = compilation.SyntaxTrees.Where(x => !x.FilePath.Contains("obj"));
                foreach (var st in syntaxTreeRoot)
                {
                    var sem = compilation.GetSemanticModel(st);
                    Extractor.AnalyzeTree<InterfaceDeclarationSyntax>(triples, st, sem, rootNode);
                    Extractor.AnalyzeTree<ClassDeclarationSyntax>(triples, st, sem, rootNode);
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
            var rootNode = new FolderNode(root, root);

            var analyzer = result.Analyzer ?? result.Manager?.GetProject(path);
            var targetFrameworks = analyzer?.ProjectFile?.TargetFrameworks ?? Array.Empty<string>();
            if (targetFrameworks.Length == 0 && !string.IsNullOrEmpty(result.TargetFramework))
            {
                targetFrameworks = new[] { result.TargetFramework };
            }

            var projectNode = new ProjectNode(projectName, projectName, targetFrameworks, exists: true, buildFailed: !result.Succeeded);
            triples.Add(new TripleIncludedIn(projectNode, rootNode));
            result.ProjectReferences.ToList().ForEach(x =>
            {
                var refName = GetProjectName(x);
                triples.Add(new TripleDependsOnProject(projectNode, new ProjectNode(refName, refName, null, File.Exists(x))));
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
            var rootNode = new FolderNode(root, root);

            var projectNode = new ProjectNode(projectName, projectName, projectFile.TargetFrameworks, exists: true, buildFailed: true);
            triples.Add(new TripleIncludedIn(projectNode, rootNode));
            foreach (var package in projectFile.PackageReferences)
            {
                var version = string.IsNullOrEmpty(package.Version) ? "none" : package.Version;
                triples.Add(new TripleDependsOnPackage(projectNode, new PackageNode(package.Name, package.Name, version)));
            }
            return triples;
        }

        private static string GetSolutionName(string fullName)
            => fullName.Split(Path.DirectorySeparatorChar).Last().Replace(".sln", "");

        private static string GetProjectName(string fullName)
            => fullName.Split(Path.DirectorySeparatorChar).Last().Replace(".csproj", "");

        private static string GetRoot(string filePath)
            => filePath.Split(Path.DirectorySeparatorChar).Reverse().Skip(1).FirstOrDefault();

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
            public ProjectAnalyzer? Analyzer => inner.Analyzer;
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
            public string? GetProperty(string name) => inner.GetProperty(name);
        }
    }
}