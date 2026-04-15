using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    public void GetAnalysisContext_IncludesProjectsAddedAsTransitiveReferences()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var manager = new AnalyzerManager(solutionPath);

        var context = Analyzer.GetAnalysisContext(manager);

        var projectFileNames = context.Projects
            .Select(p => Path.GetFileName(p.Item2.ProjectFilePath))
            .ToList();

        Assert.Contains("Strazh.Tests.ProjectA.csproj", projectFileNames);
        Assert.Contains("Strazh.Tests.ProjectB.csproj", projectFileNames);
    }

    // [CallerFilePath] gives the compile-time absolute path of this source file.
    // From Strazh.Tests/AnalyzerTests.cs, two levels up reaches the repo root.
    private static string GetRepoRoot([CallerFilePath] string callerFile = "") =>
        Path.GetFullPath(Path.Combine(callerFile, "../.."));
}
