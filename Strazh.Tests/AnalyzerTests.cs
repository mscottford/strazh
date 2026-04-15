using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Buildalyzer;
using Strazh.Analysis;
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
        var manager = new AnalyzerManager(solutionPath);

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
                new AnalyzerManager(solutionPath), cacheDir);

            var uncachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(solutionPath));

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
            await Analyzer.GetAnalysisContext(new AnalyzerManager(solutionPath), cacheDir);

            // Replay from cache
            var cachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(solutionPath), cacheDir);

            var uncachedContext = await Analyzer.GetAnalysisContext(
                new AnalyzerManager(solutionPath));

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

    // [CallerFilePath] gives the compile-time absolute path of this source file.
    // From Strazh.Tests/AnalyzerTests.cs, two levels up reaches the repo root.
    private static string GetRepoRoot([CallerFilePath] string callerFile = "") =>
        Path.GetFullPath(Path.Combine(callerFile, "../.."));
}
