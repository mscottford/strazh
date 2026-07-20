using System;
using System.Diagnostics;
using System.IO;
using Strazh.Analysis;
using Xunit;

namespace Strazh.Tests;

/// <summary>
/// End-to-end tests that exercise <see cref="GitHelper"/> against a real submodule
/// checkout produced by the <c>git</c> CLI: two bare repos are created in a temp
/// location, one declares the other as a submodule via a relative URL, and the
/// parent is cloned recursively. The resulting layout (a <c>.git</c> file inside the
/// submodule that points at <c>.git/modules/&lt;name&gt;/</c> on the parent) is what
/// the helper has to navigate in production — these tests verify that navigation
/// against the actual format git emits rather than a hand-crafted approximation.
///
/// Requires <c>git</c> on PATH (version recent enough to support
/// <c>--initial-branch</c> and <c>protocol.file.allow</c>).
/// </summary>
public class GitHelperIntegrationTests
{
    [Fact]
    public void RealSubmoduleCheckout_FullAttributionAndResolution()
    {
        using var ws = new GitWorkspace();

        // Two bare repos under an Org/ directory so relative URL resolution produces
        // a sensible owner/repo-shaped name when parsed.
        var bareCore = ws.InitBare("Org/infra-dotnet-core.git");
        var bareParent = ws.InitBare("Org/infra-dotnet-personalize.git");

        // Seed the core repo with one commit so it can be added as a submodule.
        var coreSeed = ws.Clone(bareCore, "core-seed");
        ws.Write(coreSeed, "README.md", "core");
        ws.Git(coreSeed, "add", "README.md");
        ws.Git(coreSeed, "commit", "-m", "init");
        ws.Git(coreSeed, "push", "origin", "HEAD:refs/heads/main");

        // Seed the parent with one commit, then add core as a submodule via a relative
        // URL. The parent's origin URL is the file:// path of bareParent, so
        // ../infra-dotnet-core.git resolves to bareCore — matching real-world git
        // semantics where submodule URLs are stored relative to the host's origin.
        var parentSeed = ws.Clone(bareParent, "parent-seed");
        ws.Write(parentSeed, "README.md", "parent");
        ws.Git(parentSeed, "add", "README.md");
        ws.Git(parentSeed, "commit", "-m", "init");
        ws.Git(parentSeed, "-c", "protocol.file.allow=always",
            "submodule", "add", "../infra-dotnet-core.git", "Core");
        ws.Git(parentSeed, "commit", "-m", "add Core submodule");
        ws.Git(parentSeed, "push", "origin", "HEAD:refs/heads/main");

        // Final clone with submodules populated — this is the layout the helper sees
        // in production.
        var final = ws.CloneRecursive(bareParent, "final");

        // The submodule's .git is a file, not a directory — this is the case the
        // FindGitRoot fix has to handle for paths inside a submodule.
        var submoduleDotGit = Path.Combine(final, "Core", ".git");
        Assert.True(File.Exists(submoduleDotGit), ".git inside the submodule must be a file");
        Assert.False(Directory.Exists(submoduleDotGit), ".git inside the submodule must not be a directory");

        // FindGitRoot stops at the submodule root for paths inside it, and at the
        // parent for paths outside.
        Assert.Equal(
            Path.Combine(final, "Core"),
            GitHelper.FindGitRoot(Path.Combine(final, "Core", "README.md")));
        Assert.Equal(
            final,
            GitHelper.FindGitRoot(Path.Combine(final, "README.md")));

        // GetRepositoryName follows the .git gitdir pointer into the parent's
        // .git/modules/Core/config — returning the submodule's own origin URL,
        // NOT the parent's. This is the attribution invariant the production code
        // depends on so submodule projects aren't tied to the host repo.
        var subRepo = GitHelper.GetRepositoryName(Path.Combine(final, "Core", "README.md"));
        var parentRepo = GitHelper.GetRepositoryName(Path.Combine(final, "README.md"));
        Assert.NotNull(subRepo);
        Assert.NotNull(parentRepo);
        Assert.NotEqual(parentRepo, subRepo);
        Assert.EndsWith("Org/infra-dotnet-core", subRepo);
        Assert.EndsWith("Org/infra-dotnet-personalize", parentRepo);

        // GetSubmodules reads .gitmodules from the parent and resolves the relative
        // url ../infra-dotnet-core.git against the parent's origin URL.
        var subs = GitHelper.GetSubmodules(final);
        Assert.Single(subs);
        Assert.Equal("Core", subs[0].MountPath);
        Assert.EndsWith("Org/infra-dotnet-core", subs[0].RepositoryName);
    }

    [Fact]
    public void GetCommit_RegularClone_ReturnsHeadShaAndMetadata()
    {
        using var ws = new GitWorkspace();
        var bare = ws.InitBare("Org/solo.git");
        var work = ws.Clone(bare, "work");
        ws.Write(work, "a.cs", "class A {}");
        ws.Git(work, "add", "a.cs");
        ws.Git(work, "commit", "-m", "add A");
        var head = ws.RevParse(work);

        var commit = GitHelper.GetCommit(Path.Combine(work, "a.cs"));
        Assert.NotNull(commit);
        Assert.Equal(head, commit!.Sha);
        Assert.Equal("add A", commit.Subject);
        Assert.Equal("Strazh Test", commit.Author);
        Assert.Equal("test@strazh.invalid", commit.AuthorEmail);
        Assert.False(string.IsNullOrWhiteSpace(commit.CommittedDate));

        var node = GitHelper.GetCommitNode(Path.Combine(work, "a.cs"));
        Assert.NotNull(node);
        Assert.Equal(head, node!.Sha);
        Assert.EndsWith("Org/solo", node.Repo);
    }

    /// <summary>
    /// The crux of commit-scoped identity: a submodule resolves to the commit the host repo
    /// *pins*, not the submodule's advanced upstream HEAD. This is exactly the situation where a
    /// repo embeds an older copy of a shared dependency — the pinned commit must win.
    /// </summary>
    [Fact]
    public void GetCommit_Submodule_ReturnsPinnedCommitNotAdvancedUpstream()
    {
        using var ws = new GitWorkspace();
        var bareCore = ws.InitBare("Org/core.git");
        var bareParent = ws.InitBare("Org/parent.git");

        // Core v1.
        var coreSeed = ws.Clone(bareCore, "core-seed");
        ws.Write(coreSeed, "README.md", "v1");
        ws.Git(coreSeed, "add", "README.md");
        ws.Git(coreSeed, "commit", "-m", "core v1");
        ws.Git(coreSeed, "push", "origin", "HEAD:refs/heads/main");
        var coreV1 = ws.RevParse(coreSeed);

        // Parent pins core at v1.
        var parentSeed = ws.Clone(bareParent, "parent-seed");
        ws.Write(parentSeed, "README.md", "parent");
        ws.Git(parentSeed, "add", "README.md");
        ws.Git(parentSeed, "commit", "-m", "init");
        ws.Git(parentSeed, "-c", "protocol.file.allow=always",
            "submodule", "add", "../core.git", "Core");
        ws.Git(parentSeed, "commit", "-m", "add core submodule at v1");
        ws.Git(parentSeed, "push", "origin", "HEAD:refs/heads/main");

        // Core advances to v2 upstream; the parent still pins v1.
        ws.Write(coreSeed, "README.md", "v2");
        ws.Git(coreSeed, "commit", "-am", "core v2");
        ws.Git(coreSeed, "push", "origin", "HEAD:refs/heads/main");
        var coreV2 = ws.RevParse(coreSeed);
        Assert.NotEqual(coreV1, coreV2);

        var final = ws.CloneRecursive(bareParent, "final");

        // The submodule's checked-out commit is the pinned v1, not the advanced v2.
        var pinned = GitHelper.GetCommit(Path.Combine(final, "Core", "README.md"));
        Assert.NotNull(pinned);
        Assert.Equal(coreV1, pinned!.Sha);
        Assert.NotEqual(coreV2, pinned.Sha);

        // The parent's own commit differs from the submodule's.
        var parentCommit = GitHelper.GetCommit(Path.Combine(final, "README.md"));
        Assert.NotNull(parentCommit);
        Assert.NotEqual(pinned.Sha, parentCommit!.Sha);

        // The pinned commit is attributed to core's origin, not the parent's.
        var pinnedNode = GitHelper.GetCommitNode(Path.Combine(final, "Core", "README.md"));
        Assert.NotNull(pinnedNode);
        Assert.Equal(coreV1, pinnedNode!.Sha);
        Assert.EndsWith("Org/core", pinnedNode.Repo);
    }

    [Fact]
    public void GetCommit_OutsideGitRepository_ReturnsNull()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"strazh-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            Assert.Null(GitHelper.GetCommit(Path.Combine(tmp, "x.cs")));
            Assert.Null(GitHelper.GetCommitNode(Path.Combine(tmp, "x.cs")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>
    /// Owns a temp directory and shells out to <c>git</c> to populate it. Disposal
    /// removes the tree (best-effort: read-only objects under .git may resist deletion
    /// on some filesystems).
    /// </summary>
    private sealed class GitWorkspace : IDisposable
    {
        public string Root { get; }
        public string BareRoot { get; }
        public string WorkRoot { get; }

        public GitWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"strazh-git-{Guid.NewGuid():N}");
            BareRoot = Path.Combine(Root, "bare");
            WorkRoot = Path.Combine(Root, "work");
            Directory.CreateDirectory(BareRoot);
            Directory.CreateDirectory(WorkRoot);
        }

        public string InitBare(string relativePath)
        {
            var path = Path.Combine(BareRoot, relativePath);
            Directory.CreateDirectory(path);
            RunGit(BareRoot, "init", "--bare", "--initial-branch=main", path);
            return path;
        }

        public string Clone(string bareRepoPath, string workdirName)
        {
            var dest = Path.Combine(WorkRoot, workdirName);
            RunGit(WorkRoot, "clone", FileUrl(bareRepoPath), dest);
            RunGit(dest, "config", "user.email", "test@strazh.invalid");
            RunGit(dest, "config", "user.name", "Strazh Test");
            return dest;
        }

        public string CloneRecursive(string bareRepoPath, string workdirName)
        {
            var dest = Path.Combine(WorkRoot, workdirName);
            RunGit(WorkRoot, "-c", "protocol.file.allow=always",
                "clone", "--recursive", FileUrl(bareRepoPath), dest);
            return dest;
        }

        public void Write(string repoDir, string relativePath, string content)
        {
            var p = Path.Combine(repoDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }

        public void Git(string workingDir, params string[] args)
            => RunGit(workingDir, args);

        public string RevParse(string repoDir, string rev = "HEAD")
            => Capture(repoDir, "rev-parse", rev);

        // Like RunGit, but returns trimmed stdout (for reading back commit shas).
        public static string Capture(string workingDir, params string[] args)
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
            return stdout.Trim();
        }

        private static string FileUrl(string path)
            => "file://" + path.Replace('\\', '/');

        private static void RunGit(string workingDir, params string[] args)
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
                    $"git {string.Join(" ", args)} failed (exit={p.ExitCode})\nstderr: {stderr}\nstdout: {stdout}");
            }
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
                // Best-effort cleanup; .git objects can be read-only on some platforms.
            }
        }
    }
}
