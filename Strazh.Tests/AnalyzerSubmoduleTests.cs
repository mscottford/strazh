using System;
using System.Diagnostics;
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

        // A Submodule-kind Folder exists at "<host-repo-folder>/Core" (discovered from .gitmodules,
        // which does not require a checkout — so this holds even though the fake fixture has no HEAD).
        var mountFolder = triples
            .Select(t => t.NodeA)
            .OfType<FolderNode>()
            .Concat(triples.Select(t => t.NodeB).OfType<FolderNode>())
            .FirstOrDefault(f => f.Kind == FolderKind.Submodule && f.Name == "Core");
        Assert.NotNull(mountFolder);
        Assert.Equal("host-repo/Core", mountFolder.FullName);

        // It's INCLUDED_IN the host's repo-root folder, so the host's tree can reach the mount
        // point via folder traversal.
        Assert.Contains(triples, t =>
            t.NodeA is FolderNode a && a.Pk == mountFolder.Pk
            && t.NodeB is FolderNode b && b.Name == "host-repo" && b.Kind == FolderKind.Regular
            && t.Relationship.Type == "INCLUDED_IN");

        // No folder is linked directly to a Repository anymore — a folder's repository is
        // discovered transitively through its commit (folder -FROM-> Commit <-HAS- Repository).
        Assert.DoesNotContain(triples, t =>
            t.NodeB is RepositoryNode && t.Relationship.Type == "INCLUDED_IN");
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
    /// End-to-end against a REAL submodule checkout: a host repo (containing the SystemUnderTest
    /// solution) pins a core repo as a submodule at "Core". After analysis the submodule mount
    /// folder must PINS the core repo's pinned commit, and that commit must be owned by the core
    /// repository via HAS — so the submodule's repository is reachable through the pinned commit
    /// even though no folder links to it directly.
    /// </summary>
    [Fact]
    public async Task Analyze_RealSubmodule_PinsMountFolderToCommitOwnedByItsRepository()
    {
        using var fixture = new RealSubmoduleFixture();

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

        // The submodule mount folder PINS the core repo's pinned commit.
        var pin = triples.FirstOrDefault(t =>
            t.Relationship.Type == "PINS"
            && t.NodeA is FolderNode { Kind: FolderKind.Submodule }
            && t.NodeB is CommitNode c && c.Sha == fixture.PinnedCoreSha);
        Assert.NotNull(pin);

        // That pinned commit is attributed to the core repository (not the host) via HAS.
        var pinnedCommit = (CommitNode)pin.NodeB;
        Assert.EndsWith("Org/core", pinnedCommit.Repo);
        Assert.Contains(triples, t =>
            t.Relationship.Type == "HAS"
            && t.NodeA is RepositoryNode r && r.FullName == pinnedCommit.Repo
            && t.NodeB is CommitNode c && c.Sha == fixture.PinnedCoreSha);
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

    /// <summary>
    /// Builds a real host repo that pins a real core repo as a submodule at "Core", using the git
    /// CLI over file:// remotes (the same layout <see cref="GitHelperIntegrationTests"/> uses). The
    /// host contains the SystemUnderTest solution so <see cref="Analyzer.Analyze"/> has projects to
    /// build. Exposes the solution path and the pinned core commit sha.
    ///
    /// Requires git on PATH and the ability to build SystemUnderTest (as the other Analyzer tests do).
    /// </summary>
    private sealed class RealSubmoduleFixture : IDisposable
    {
        public string Root { get; }
        public string SolutionPath { get; }
        public string PinnedCoreSha { get; }

        public RealSubmoduleFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"strazh-realsub-{Guid.NewGuid():N}");
            var bareRoot = Path.Combine(Root, "bare");
            var workRoot = Path.Combine(Root, "work");
            Directory.CreateDirectory(bareRoot);
            Directory.CreateDirectory(workRoot);

            var bareCore = Path.Combine(bareRoot, "Org", "core.git");
            var bareHost = Path.Combine(bareRoot, "Org", "host.git");
            Directory.CreateDirectory(bareCore);
            Directory.CreateDirectory(bareHost);
            Git(bareRoot, "init", "--bare", "--initial-branch=main", bareCore);
            Git(bareRoot, "init", "--bare", "--initial-branch=main", bareHost);

            // Seed the core repo with one commit so it can be pinned as a submodule.
            var coreSeed = Clone(workRoot, bareCore, "core-seed");
            File.WriteAllText(Path.Combine(coreSeed, "README.md"), "core");
            Git(coreSeed, "add", "-A");
            Git(coreSeed, "commit", "-m", "core v1");
            Git(coreSeed, "push", "origin", "HEAD:refs/heads/main");

            // Seed the host with the SystemUnderTest solution, then pin core as a submodule via a
            // relative URL that resolves against the host's origin (matching real git semantics).
            var hostSeed = Clone(workRoot, bareHost, "host-seed");
            CopyDirectory(Path.Combine(GetThisDir(), "..", "SystemUnderTest"), hostSeed);
            Git(hostSeed, "add", "-A");
            Git(hostSeed, "commit", "-m", "seed SystemUnderTest");
            Git(hostSeed, "-c", "protocol.file.allow=always",
                "submodule", "add", "../core.git", "Core");
            Git(hostSeed, "commit", "-m", "add Core submodule");
            Git(hostSeed, "push", "origin", "HEAD:refs/heads/main");

            // Final recursive clone: the layout the analyzer sees, with Core checked out at the pin.
            var final = Path.Combine(workRoot, "final");
            Git(workRoot, "-c", "protocol.file.allow=always",
                "clone", "--recursive", FileUrl(bareHost), final);

            PinnedCoreSha = Exec(Path.Combine(final, "Core"), capture: true, "rev-parse", "HEAD");
            SolutionPath = Path.Combine(final, "SystemUnderTest.sln");
        }

        private static string Clone(string workRoot, string bareRepoPath, string workdirName)
        {
            var dest = Path.Combine(workRoot, workdirName);
            Git(workRoot, "clone", FileUrl(bareRepoPath), dest);
            Git(dest, "config", "user.email", "test@strazh.invalid");
            Git(dest, "config", "user.name", "Strazh Test");
            return dest;
        }

        private static string FileUrl(string path) => "file://" + path.Replace('\\', '/');

        private static void Git(string workingDir, params string[] args) => Exec(workingDir, capture: false, args);

        private static string Exec(string workingDir, bool capture, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} failed (exit={p.ExitCode})\nstderr: {stderr}");
            }
            return capture ? stdout.Trim() : stdout;
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
                // Best-effort cleanup; .git objects can be read-only on some platforms.
            }
        }
    }
}
