using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Strazh.Analysis;
using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

/// <summary>
/// Verifies that Analyzer.Analyze emits the new submodule-mount-point triples when
/// the host repo's .gitmodules declares submodules. Builds a temp fixture by copying
/// SystemUnderTest into a scratch directory and stamping a fake git layout
/// (.git/config + .gitmodules) on top — that way Analyze runs against a real
/// solution but a controllable git state.
/// </summary>
public class AnalyzerSubmoduleTests
{
    [Fact]
    public async Task Analyze_HostWithGitmodules_EmitsSubmoduleMountFolderAndRepositoryLink()
    {
        using var fixture = new SolutionFixture(
            originUrl: "https://github.com/TestOrg/host-repo.git",
            gitmodules: """
                [submodule "Core"]
                    path = Core
                    url = ../core-repo.git
                """);

        var store = new InMemoryTripleStore();
        await Analyzer.Analyze(
            new AnalyzerConfig(new AnalyzerConfig.Options(
                Credentials: "",
                Tier: "project",
                Delete: "false",
                Solution: fixture.SolutionPath,
                Projects: Array.Empty<string>())),
            NullAnalysisProgress.Instance,
            store);

        var triples = store.Triples;

        // A Submodule-kind Folder exists at "<host-repo-folder>/Core".
        var mountFolder = triples
            .Select(t => t.NodeA)
            .OfType<FolderNode>()
            .Concat(triples.Select(t => t.NodeB).OfType<FolderNode>())
            .FirstOrDefault(f => f.Kind == FolderKind.Submodule && f.Name == "Core");
        Assert.NotNull(mountFolder);
        Assert.Equal("host-repo/Core", mountFolder.FullName);

        // It's INCLUDED_IN the referenced Repository (the submodule's resolved owner/repo).
        Assert.Contains(triples, t =>
            t.NodeA is FolderNode a && a.Pk == mountFolder.Pk
            && t.NodeB is RepositoryNode r && r.FullName == "TestOrg/core-repo"
            && t.Relationship.Type == "INCLUDED_IN");

        // It's also INCLUDED_IN the host's repo-root folder, so the host's tree can
        // reach the mount point via folder traversal.
        Assert.Contains(triples, t =>
            t.NodeA is FolderNode a && a.Pk == mountFolder.Pk
            && t.NodeB is FolderNode b && b.Name == "host-repo" && b.Kind == FolderKind.Regular
            && t.Relationship.Type == "INCLUDED_IN");
    }

    [Fact]
    public async Task Analyze_HostWithoutGitmodules_EmitsNoSubmoduleKindFolders()
    {
        using var fixture = new SolutionFixture(
            originUrl: "https://github.com/TestOrg/host-repo.git",
            gitmodules: null);

        var store = new InMemoryTripleStore();
        await Analyzer.Analyze(
            new AnalyzerConfig(new AnalyzerConfig.Options(
                Credentials: "",
                Tier: "project",
                Delete: "false",
                Solution: fixture.SolutionPath,
                Projects: Array.Empty<string>())),
            NullAnalysisProgress.Instance,
            store);

        var anySubmoduleFolder = store.Triples
            .Select(t => t.NodeA)
            .Concat(store.Triples.Select(t => t.NodeB))
            .OfType<FolderNode>()
            .Any(f => f.Kind == FolderKind.Submodule);

        Assert.False(anySubmoduleFolder);
    }

    /// <summary>
    /// Copies the checked-in SystemUnderTest fixture to a scratch directory and stamps
    /// a fake .git layout on top (config with origin URL, optional .gitmodules).
    /// Exposes the path to the copied .sln so tests can run Analyze against a
    /// controllable git state without touching the real repository.
    /// </summary>
    private sealed class SolutionFixture : IDisposable
    {
        public string Root { get; }
        public string SolutionPath { get; }

        public SolutionFixture(string originUrl, string? gitmodules)
        {
            Root = Path.Combine(Path.GetTempPath(), $"strazh-fixture-{Guid.NewGuid():N}");
            // Copy SystemUnderTest contents under a "host-repo" subdir so the git root
            // basename matches what the host repo's name would naturally produce.
            var hostRepo = Path.Combine(Root, "host-repo");
            var sourceFixture = Path.Combine(GetThisDir(), "..", "SystemUnderTest");
            CopyDirectory(sourceFixture, hostRepo);

            // Fake .git/config with the requested origin URL.
            Directory.CreateDirectory(Path.Combine(hostRepo, ".git"));
            File.WriteAllText(
                Path.Combine(hostRepo, ".git", "config"),
                $"[remote \"origin\"]\n\turl = {originUrl}\n");

            if (gitmodules != null)
            {
                File.WriteAllText(Path.Combine(hostRepo, ".gitmodules"), gitmodules);
            }

            SolutionPath = Path.Combine(hostRepo, "SystemUnderTest.sln");
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, destination));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                // Skip bin/obj — these contain build outputs that will be regenerated
                // and can cause path-length / permission issues during copy.
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }
                File.Copy(file, file.Replace(source, destination), overwrite: true);
            }
        }

        private static string GetThisDir([CallerFilePath] string callerFile = "")
            => Path.GetDirectoryName(callerFile)!;
    }
}
