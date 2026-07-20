using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Buildalyzer;
using Buildalyzer.IO;
using Strazh.Analysis;
using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class AnalyzerTests
{
    /// <summary>
    /// Regression test for the bug where projects pulled into the Roslyn workspace as transitive
    /// references by an earlier project's AddToWorkspace(workspace, addProjectReferences: true)
    /// call were subsequently skipped by the deduplication check and never entered
    /// context.Projects — causing them to receive no CONTAINS triple and no analysis.
    ///
    /// The fixture solution (SystemUnderTest.sln) lists ProjectA before ProjectB.
    /// ProjectA references ProjectB, so when ProjectA is added to the workspace with
    /// addProjectReferences: true, Buildalyzer pulls ProjectB in as a reference. Without the
    /// fix, ProjectB's loop iteration finds it already in the workspace and is dropped.
    /// </summary>
    [Fact]
    public async Task GetAnalysisContext_IncludesProjectsAddedAsTransitiveReferences()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var manager = new AnalyzerManager(IOPath.Parse(solutionPath), new AnalyzerManagerOptions());

        var context = await Analyzer.GetAnalysisContext(manager);

        var projectFileNames = context.Projects
            .Select(p => Path.GetFileName(p.Item2.ProjectFilePath))
            .ToList();

        Assert.Contains("Strazh.Tests.ProjectA.csproj", projectFileNames);
        Assert.Contains("Strazh.Tests.ProjectB.csproj", projectFileNames);
    }

    /// <summary>
    /// Verifies that the first run with a cache directory (cache miss, binlog written) produces
    /// the same results as a completely uncached build.
    /// </summary>
    [Fact]
    public async Task GetAnalysisContext_FirstCachedRun_YieldsSameResultsAsUncachedBuild()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var cacheDir = Path.Combine(Path.GetTempPath(), $"strazh-test-{Guid.NewGuid():N}");
        try
        {
            var cachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(IOPath.Parse(solutionPath), new AnalyzerManagerOptions()), cacheDir);

            var uncachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(IOPath.Parse(solutionPath), new AnalyzerManagerOptions()));

            AssertAnalysisContextsAreEquivalent(uncachedContext, cachedContext);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that replaying a populated cache (cache hit, binlog replayed) produces the same
    /// results as a completely uncached build.
    /// </summary>
    [Fact]
    public async Task GetAnalysisContext_CacheHitRun_YieldsSameResultsAsUncachedBuild()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var cacheDir = Path.Combine(Path.GetTempPath(), $"strazh-test-{Guid.NewGuid():N}");
        try
        {
            // Populate the cache
            await Analyzer.GetAnalysisContext(new AnalyzerManager(IOPath.Parse(solutionPath), new AnalyzerManagerOptions()), cacheDir);

            // Replay from cache
            var cachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(IOPath.Parse(solutionPath), new AnalyzerManagerOptions()), cacheDir);

            var uncachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(IOPath.Parse(solutionPath), new AnalyzerManagerOptions()));

            AssertAnalysisContextsAreEquivalent(uncachedContext, cachedContext);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
    }

    private static void AssertAnalysisContextsAreEquivalent(
        Analyzer.AnalysisContext expected, Analyzer.AnalysisContext actual)
    {
        var expectedResults = expected.Projects
            .Select(p => p.Item2)
            .OrderBy(r => r.ProjectFilePath)
            .ToList();

        var actualResults = actual.Projects
            .Select(p => p.Item2)
            .OrderBy(r => r.ProjectFilePath)
            .ToList();

        Assert.Equal(expectedResults.Count, actualResults.Count);

        for (var i = 0; i < expectedResults.Count; i++)
        {
            var exp = expectedResults[i];
            var act = actualResults[i];

            Assert.Equal(exp.ProjectFilePath, act.ProjectFilePath);
            Assert.Equal(exp.Succeeded, act.Succeeded);
            Assert.Equal(
                exp.ProjectReferences.OrderBy(x => x).ToList(),
                act.ProjectReferences.OrderBy(x => x).ToList());
            Assert.Equal(
                exp.SourceFiles.OrderBy(x => x).ToList(),
                act.SourceFiles.OrderBy(x => x).ToList());
            Assert.Equal(
                exp.PackageReferences.Keys.OrderBy(x => x).ToList(),
                act.PackageReferences.Keys.OrderBy(x => x).ToList());
        }
    }

    /// <summary>
    /// Verifies that analyzing a solution creates a Repository node that owns the repo's HEAD
    /// commit via Repository(owner/repo) -HAS-> Commit, which is how a node's repository is
    /// discovered now (transitively through its commit) rather than by a direct folder edge.
    ///
    /// The expected repository name is read from the git remote "origin" of the
    /// containing repository, so the test passes unchanged on both the upstream
    /// repo (vladbatushkov/strazh) and any fork (e.g. mscottford/strazh).
    /// </summary>
    [Fact]
    public async Task Analyze_CreatesRepositoryNodeOwningItsHeadCommit()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var config = new AnalyzerConfig(new AnalyzerConfig.Options(
            Credentials: "",
            Tier: "project",
            Delete: "false",
            Solution: solutionPath,
            Projects: Array.Empty<string>()
        ));
        var store = new InMemoryTripleStore();

        await Analyzer.Analyze(config, NullAnalysisProgress.Instance, store);

        var triples = store.Triples;

        var expectedRepoName = GitHelper.GetRepositoryName(solutionPath);
        Assert.NotNull(expectedRepoName);

        // Repository -HAS-> Commit, and there is no direct Folder -INCLUDED_IN-> Repository edge.
        var hasTriple = triples.FirstOrDefault(t =>
            t.NodeA is RepositoryNode repo && repo.FullName == expectedRepoName &&
            t.NodeB is CommitNode &&
            t.Relationship.Type == "HAS");
        Assert.NotNull(hasTriple);

        Assert.DoesNotContain(triples, t =>
            t.NodeB is RepositoryNode && t.Relationship.Type == "INCLUDED_IN");

        // The owned commit is reachable from a folder via FROM, completing the discovery path
        // folder -FROM-> Commit <-HAS- Repository.
        var ownedCommit = (CommitNode)hasTriple.NodeB;
        Assert.Contains(triples, t =>
            t.NodeA is FolderNode && t.Relationship.Type == "FROM" &&
            t.NodeB is CommitNode commit && commit.Sha == ownedCommit.Sha);
    }

    /// <summary>
    /// Directory mode is the single-pass replacement for the old two-pass flow (a per-solution
    /// run followed by a project sweep). Pointed at a directory, one <see cref="Analyzer.Analyze"/>
    /// call must discover the solution and both projects, produce the project nodes, AND record
    /// the solution's CONTAINS edges — the union of what the two separate passes used to emit.
    /// Uses the in-memory store so no Neo4j is required.
    /// </summary>
    [Fact]
    public async Task Analyze_DirectoryMode_DiscoversSolutionAndAllProjectsInOnePass()
    {
        var directory = Path.Combine(GetRepoRoot(), "SystemUnderTest");
        var cacheDir = Path.Combine(Path.GetTempPath(), $"strazh-dir-cache-{Guid.NewGuid():N}");
        var logDir = Path.Combine(Path.GetTempPath(), $"strazh-dir-log-{Guid.NewGuid():N}");
        try
        {
            var config = new AnalyzerConfig(new AnalyzerConfig.Options(
                Credentials: "db:user:pass",
                Tier: "project",
                Delete: "false",
                Solution: "none",
                Projects: null,
                Directory: directory,
                CacheDirectory: cacheDir,
                BuildLogDirectory: logDir));

            var store = new InMemoryTripleStore();
            await Analyzer.Analyze(config, NullAnalysisProgress.Instance, store);

            var triples = store.Triples;

            // Both projects were discovered and analyzed (project nodes exist).
            var projectNames = triples
                .SelectMany(t => new[] { t.NodeA, t.NodeB })
                .OfType<ProjectNode>()
                .Select(p => p.Name)
                .ToHashSet();
            Assert.Contains("Strazh.Tests.ProjectA", projectNames);
            Assert.Contains("Strazh.Tests.ProjectB", projectNames);

            // ...and the solution's CONTAINS edges were recorded in the same run.
            var containedBySolution = triples
                .OfType<TripleContains>()
                .Where(t => t.NodeA is SolutionNode s && s.Name == "SystemUnderTest")
                .Select(t => ((ProjectNode)t.NodeB).Name)
                .ToHashSet();
            Assert.Contains("Strazh.Tests.ProjectA", containedBySolution);
            Assert.Contains("Strazh.Tests.ProjectB", containedBySolution);
        }
        finally
        {
            foreach (var dir in new[] { cacheDir, logDir })
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
                }
            }
        }
    }

    /// <summary>
    /// A directory scan sweeps up every .sln, including ones MSBuild's solution parser rejects
    /// (e.g. a legacy solution referencing a .vcproj). Such a solution must not abort the whole
    /// run — it is recorded as a Solution node flagged buildFailed so it is still represented,
    /// and analysis continues. Uses the in-memory store so no Neo4j is required.
    /// </summary>
    [Fact]
    public async Task Analyze_DirectoryMode_RecordsUnparseableSolutionAsBuildFailed_WithoutAborting()
    {
        var scanRoot = Path.Combine(Path.GetTempPath(), $"strazh-badsln-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scanRoot);
        var cacheDir = Path.Combine(Path.GetTempPath(), $"strazh-badsln-cache-{Guid.NewGuid():N}");
        var logDir = Path.Combine(Path.GetTempPath(), $"strazh-badsln-log-{Guid.NewGuid():N}");
        try
        {
            // A solution whose only project is a legacy .vcproj — MSBuild's SolutionFile parser
            // throws InvalidProjectFileException on it, which is exactly the case that used to
            // abort the entire directory run.
            await File.WriteAllTextAsync(Path.Combine(scanRoot, "Legacy.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "# Visual Studio Version 17\n" +
                "Project(\"{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}\") = \"hooks\", \"hooks.vcproj\", " +
                "\"{2E8F2E4A-1B2C-4D5E-9F00-000000000001}\"\n" +
                "EndProject\n" +
                "Global\nEndGlobal\n");

            var config = new AnalyzerConfig(new AnalyzerConfig.Options(
                Credentials: "db:user:pass",
                Tier: "project",
                Delete: "false",
                Solution: "none",
                Projects: null,
                Directory: scanRoot,
                CacheDirectory: cacheDir,
                BuildLogDirectory: logDir));

            var store = new InMemoryTripleStore();

            // Must not throw despite the unparseable solution.
            await Analyzer.Analyze(config, NullAnalysisProgress.Instance, store);

            var legacy = store.Triples
                .SelectMany(t => new[] { t.NodeA, t.NodeB })
                .OfType<SolutionNode>()
                .FirstOrDefault(s => s.Name == "Legacy");
            Assert.NotNull(legacy);
            Assert.True(legacy!.BuildFailed);
        }
        finally
        {
            foreach (var dir in new[] { scanRoot, cacheDir, logDir })
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
                }
            }
        }
    }

    /// <summary>
    /// End-to-end check that a real analysis emits a per-project metrics report — the same
    /// pipeline the CLI runs, so the Load-stage timings are real rather than scripted. Confirms
    /// the report has a line per analyzed project with per-stage timings.
    /// </summary>
    [Fact]
    public async Task Analyze_WritesPerProjectMetricsReport()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var cacheDir = Path.Combine(Path.GetTempPath(), $"strazh-metrics-e2e-cache-{Guid.NewGuid():N}");
        var logDir = Path.Combine(Path.GetTempPath(), $"strazh-metrics-e2e-log-{Guid.NewGuid():N}");
        var metricsPath = Path.Combine(Path.GetTempPath(), $"strazh-metrics-e2e-{Guid.NewGuid():N}.jsonl");
        try
        {
            var config = new AnalyzerConfig(new AnalyzerConfig.Options(
                Credentials: "db:user:pass",
                Tier: "project",
                Delete: "false",
                Solution: solutionPath,
                Projects: Array.Empty<string>(),
                CacheDirectory: cacheDir,
                BuildLogDirectory: logDir));

            await Analyzer.Analyze(config, new MetricsAnalysisProgress(metricsPath), new InMemoryTripleStore());

            Assert.True(File.Exists(metricsPath), "metrics report should be written");
            var lines = await File.ReadAllLinesAsync(metricsPath);
            Assert.NotEmpty(lines);

            // At least one analyzed project reports per-stage timings and a completed outcome.
            var sawCompletedWithStages = false;
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                Assert.False(string.IsNullOrEmpty(root.GetProperty("name").GetString()));
                if (root.GetProperty("outcome").GetString() == "completed"
                    && root.GetProperty("stagesMs").EnumerateObject().Any())
                {
                    sawCompletedWithStages = true;
                }
            }
            Assert.True(sawCompletedWithStages, "expected at least one completed project with stage timings");
        }
        finally
        {
            foreach (var dir in new[] { cacheDir, logDir })
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
                }
            }
            if (File.Exists(metricsPath))
            {
                File.Delete(metricsPath);
            }
        }
    }

    // [CallerFilePath] gives the compile-time absolute path of this source file.
    // From Strazh.Tests/AnalyzerTests.cs, two levels up reaches the repo root.
    private static string GetRepoRoot([CallerFilePath] string callerFile = "") =>
        Path.GetFullPath(Path.Combine(callerFile, "../.."));
}
