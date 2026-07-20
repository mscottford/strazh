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
/// End-to-end: runs Analyzer.Analyze against the SystemUnderTest solution placed inside a REAL
/// git repository (git init + one commit), and verifies the commit-scoping wiring — every analyzed
/// project is stamped with the repo's HEAD commit and linked to a Commit node via FROM_COMMIT.
/// Unlike AnalyzerSubmoduleTests (which stamps a fake .git with no HEAD), a real repo is required
/// so GitHelper.GetCommit can resolve an actual sha.
///
/// Requires git on PATH and the ability to build SystemUnderTest (as the other Analyzer tests do).
/// </summary>
public class AnalyzerCommitScopingTests
{
    [Fact]
    public async Task Analyze_RealGitRepo_VersionsProjectsAndLinksThemToTheirCommit()
    {
        using var fixture = new RealGitSolutionFixture();

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

        var projects = triples.Select(t => t.NodeA).OfType<ProjectNode>()
            .Concat(triples.Select(t => t.NodeB).OfType<ProjectNode>())
            .ToList();

        // At least one real project was versioned with the repo's HEAD commit...
        Assert.Contains(projects, p => p.CommitSha == fixture.Head);
        // ...and nothing was stamped with a different sha (a node is either HEAD-versioned or,
        // for anything the resolver couldn't attribute, left unversioned — never mis-versioned).
        Assert.All(projects, p => Assert.True(p.CommitSha == null || p.CommitSha == fixture.Head));

        // A Commit node exists for HEAD and a project links to it via FROM_COMMIT.
        Assert.Contains(triples, t =>
            t.Relationship.Type == "FROM_COMMIT"
            && t.NodeA is ProjectNode
            && t.NodeB is CommitNode commit && commit.Sha == fixture.Head);

        // The structural Solution and Folder nodes are versioned by HEAD and linked to the
        // Commit node too, so the same solution/folder at another commit is a distinct subgraph.
        Assert.Contains(triples, t =>
            t.Relationship.Type == "FROM_COMMIT"
            && t.NodeA is SolutionNode solution && solution.CommitSha == fixture.Head
            && t.NodeB is CommitNode commit && commit.Sha == fixture.Head);
        Assert.Contains(triples, t =>
            t.Relationship.Type == "FROM_COMMIT"
            && t.NodeA is FolderNode folder && folder.CommitSha == fixture.Head
            && t.NodeB is CommitNode commit && commit.Sha == fixture.Head);
    }

    /// <summary>
    /// Copies the checked-in SystemUnderTest fixture into a scratch directory and turns it into a
    /// real git repository with a single seed commit. Exposes the .sln path and the HEAD sha.
    /// </summary>
    private sealed class RealGitSolutionFixture : IDisposable
    {
        public string Root { get; }
        public string SolutionPath { get; }
        public string Head { get; }

        public RealGitSolutionFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"strazh-realgit-{Guid.NewGuid():N}");
            var source = Path.Combine(GetThisDir(), "..", "SystemUnderTest");
            CopyDirectory(source, Root);

            Run("init", "--initial-branch=main");
            Run("config", "user.email", "test@strazh.invalid");
            Run("config", "user.name", "Strazh Test");
            Run("add", "-A");
            Run("commit", "-m", "seed SystemUnderTest");
            Head = Exec(capture: true, "rev-parse", "HEAD");

            SolutionPath = Path.Combine(Root, "SystemUnderTest.sln");
        }

        private void Run(params string[] args) => Exec(capture: false, args);

        private string Exec(bool capture, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
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
