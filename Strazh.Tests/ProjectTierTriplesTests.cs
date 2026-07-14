using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Buildalyzer;
using Buildalyzer.IO;
using Strazh.Analysis;
using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class ProjectTierTriplesTests
{
    // A project the build pipeline dropped entirely (unreadable binlog / timeout / unsupported)
    // has no IAnalyzerResult, but Buildalyzer's static project file still reports its target
    // frameworks and package references. That must be enough to represent it (flagged
    // buildFailed) so it is not silently missing from the graph.
    [Fact]
    public void BuildProjectTriples_FromProjectFile_EmitsBuildFailedNodeWithFrameworks()
    {
        var projectPath = Path.Combine(
            GetRepoRoot(), "SystemUnderTest", "Strazh.Tests.ProjectA", "Strazh.Tests.ProjectA.csproj");
        var analyzer = new AnalyzerManager().GetProject(IOPath.Parse(projectPath))!;

        var triples = Analyzer.BuildProjectTriples(analyzer);

        var node = triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Strazh.Tests.ProjectA");
        Assert.NotNull(node);
        Assert.Contains("netstandard2.1", node.TargetFrameworks);
        Assert.True(node.BuildFailed, "a project recovered from the static project file should be flagged buildFailed");
    }

    private static string GetRepoRoot([CallerFilePath] string callerFile = "") =>
        Path.GetFullPath(Path.Combine(callerFile, "../.."));

    // A project whose build did not succeed still carries useful information collected by
    // Buildalyzer (target frameworks, project references, package references). strazh must
    // still represent it in the graph rather than dropping it, so build-failed projects
    // (e.g. Windows-only net472 apps on a non-Windows host) are not silently missing.
    [Fact]
    public void BuildProjectTriples_FromNonSucceededResult_EmitsProjectNodeWithFrameworksAndReferences()
    {
        var result = new FakeAnalyzerResult
        {
            ProjectFilePath = Path.Combine("repo", "Core.Apps.Rdp", "Core.Apps.Rdp.csproj"),
            Succeeded = false,
            TargetFramework = "net472",
            ProjectReferences = new[]
            {
                Path.Combine("repo", "Core", "Core.csproj"),
            },
            PackageReferences = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["Litmus.RemoteAccess.Client"] = new Dictionary<string, string> { ["Version"] = "1.0.33" },
            },
        };

        var triples = Analyzer.BuildProjectTriples(result);

        var projectNode = triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Core.Apps.Rdp");
        Assert.NotNull(projectNode);
        Assert.Contains("net472", projectNode.TargetFrameworks);
        // buildFailed is a project-level fact (no TFM built) decided by the fallback sweep, not
        // per individual result — so a single non-succeeded result must not flag it here, or a
        // multi-target project with one failing TFM would be mismarked.
        Assert.False(projectNode.BuildFailed);

        Assert.Contains(triples, t =>
            t.Relationship.Type == "DEPENDS_ON" && t.NodeB is ProjectNode p && p.Name == "Core");
        Assert.Contains(triples, t =>
            t.NodeB is PackageNode pkg && pkg.Name == "Litmus.RemoteAccess.Client");
    }

    // A project that built but could not be loaded into the Roslyn workspace (an unsupported
    // project type, or — as with Core.Xmpp.SystemAndUtilityTests — a dangling project reference
    // that breaks workspace resolution) is not a build failure: its succeeded result still
    // carries target frameworks and references. It must be represented from that result, not
    // flagged buildFailed, so its references (including dangling ones) are captured.
    [Fact]
    public void BuildDroppedProjectTriples_WithBuiltButNotLoadedResult_IsNotBuildFailedAndKeepsReferences()
    {
        var result = new FakeAnalyzerResult
        {
            ProjectFilePath = Path.Combine("repo", "Core.Xmpp.SystemAndUtilityTests", "Core.Xmpp.SystemAndUtilityTests.csproj"),
            Succeeded = true,
            TargetFramework = "net472",
            ProjectReferences = new[]
            {
                Path.Combine("repo", "Core", "Core.Portable", "Core.Portable.csproj"),
            },
        };

        var triples = Analyzer.BuildDroppedProjectTriples(analyzer: null, builtButNotLoadedResult: result);

        var node = triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Core.Xmpp.SystemAndUtilityTests");
        Assert.NotNull(node);
        Assert.False(node.BuildFailed, "a project that built but failed to load is not a build failure");

        var danglingRef = triples.Select(t => t.NodeB).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Core.Portable");
        Assert.NotNull(danglingRef);
        Assert.False(danglingRef.Exists, "a reference to a .csproj not on disk is a non-existent project");
    }

    // A binlog-replay result carries no Analyzer but does carry a Manager, and its per-result
    // TargetFramework is not populated. The target frameworks must then be read from the project
    // file via the manager. Exercises the result.Manager.GetProject fallback.
    [Fact]
    public void BuildProjectTriples_WithManagerButNoAnalyzer_ReadsFrameworksFromProjectFile()
    {
        var projectPath = Path.Combine(
            GetRepoRoot(), "SystemUnderTest", "Strazh.Tests.ProjectA", "Strazh.Tests.ProjectA.csproj");
        var result = new FakeAnalyzerResult
        {
            ProjectFilePath = projectPath,
            Succeeded = true,
            TargetFramework = "",              // no per-result TFM, so the ProjectFile lookup is used
            Manager = new AnalyzerManager(),   // Analyzer stays null, so the Manager path resolves it
        };

        var triples = Analyzer.BuildProjectTriples(result);

        var node = triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Strazh.Tests.ProjectA");
        Assert.NotNull(node);
        Assert.Contains("netstandard2.1", node.TargetFrameworks);
    }

    // The fallback that records dropped projects must skip null analyzers (GetProject returns
    // null for an unresolvable path) and record a real one from its static project file, flagged
    // buildFailed, reporting it as recorded.
    [Fact]
    public async Task RecordDroppedProjectsAsync_SkipsNullAnalyzerAndRecordsRealOneAsBuildFailed()
    {
        var projectPath = Path.Combine(
            GetRepoRoot(), "SystemUnderTest", "Strazh.Tests.ProjectA", "Strazh.Tests.ProjectA.csproj");
        var analyzer = new AnalyzerManager().GetProject(IOPath.Parse(projectPath))!;
        var store = new InMemoryTripleStore();

        await Analyzer.RecordDroppedProjectsAsync(
            new IProjectAnalyzer?[] { null, analyzer },          // null exercises the guard
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), // nothing emitted -> analyzer recorded
            new Dictionary<string, IAnalyzerResult>(),             // no build result -> static project file
            store,
            NullAnalysisProgress.Instance);

        var node = store.Triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Strazh.Tests.ProjectA");
        Assert.NotNull(node);
        Assert.True(node.BuildFailed);
        Assert.Contains("netstandard2.1", node.TargetFrameworks);
    }

    // A project with no result at all (build failed, timed out, or an unreadable build log) has
    // only its static project file to go on and is flagged buildFailed.
    [Fact]
    public void BuildDroppedProjectTriples_WithNoResult_IsBuildFailedFromStaticProjectFile()
    {
        var projectPath = Path.Combine(
            GetRepoRoot(), "SystemUnderTest", "Strazh.Tests.ProjectA", "Strazh.Tests.ProjectA.csproj");
        var analyzer = new AnalyzerManager().GetProject(IOPath.Parse(projectPath))!;

        var triples = Analyzer.BuildDroppedProjectTriples(analyzer, builtButNotLoadedResult: null);

        var node = triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .FirstOrDefault(p => p.Name == "Strazh.Tests.ProjectA");
        Assert.NotNull(node);
        Assert.True(node.BuildFailed);
    }

    // Minimal IAnalyzerResult stand-in: sets only the members BuildProjectTriples reads and
    // leaves the rest at harmless defaults (no analyzer/manager, so TFMs fall back to
    // TargetFramework).
    private sealed class FakeAnalyzerResult : IAnalyzerResult
    {
        public string ProjectFilePath { get; init; } = "";
        public bool Succeeded { get; init; }
        public string TargetFramework { get; init; } = "";
        public IEnumerable<string> ProjectReferences { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageReferences { get; init; }
            = new Dictionary<string, IReadOnlyDictionary<string, string>>();

        public ProjectAnalyzer Analyzer => null!;
        public AnalyzerManager Manager { get; init; } = null!;
        public IReadOnlyDictionary<string, IProjectItem[]> Items => new Dictionary<string, IProjectItem[]>();
        public Guid ProjectGuid => Guid.Empty;
        public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>();
        public string[] References => Array.Empty<string>();
        public ImmutableDictionary<string, ImmutableArray<string>> ReferenceAliases
            => ImmutableDictionary<string, ImmutableArray<string>>.Empty;
        public string[] AnalyzerReferences => Array.Empty<string>();
        public string[] SourceFiles => Array.Empty<string>();
        public string[] PreprocessorSymbols => Array.Empty<string>();
        public string[] AdditionalFiles => Array.Empty<string>();
        public string Command => "";
        public string CompilerFilePath => "";
        public string[] CompilerArguments => Array.Empty<string>();
        public string GetProperty(string name) => null!;
    }
}
