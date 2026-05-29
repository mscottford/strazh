using System;
using System.IO;
using Strazh.Analysis;
using Xunit;

namespace Strazh.Tests;

public class GitHelperTests
{
    [Fact]
    public void FindGitRoot_RecognizesDotGitDirectory()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");

        var found = GitHelper.FindGitRoot(tempRepo.Root);

        Assert.Equal(tempRepo.Root, found);
    }

    /// <summary>
    /// A checked-out git submodule stores its .git as a file (not a directory) whose
    /// content points at the real git directory under the parent repo. FindGitRoot must
    /// recognize that file so that paths inside the submodule resolve to the submodule's
    /// root rather than walking up into the parent repo.
    /// </summary>
    [Fact]
    public void FindGitRoot_RecognizesDotGitFile_InsideSubmoduleCheckout()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");
        var subRoot = tempRepo.AddSubmoduleCheckout("Core", "https://github.com/Org/child.git");

        var found = GitHelper.FindGitRoot(subRoot);

        Assert.Equal(subRoot, found);
    }

    [Fact]
    public void GetRepositoryName_FromRegularRepo_ReturnsOriginOwnerSlashRepo()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");

        Assert.Equal("Org/parent", GitHelper.GetRepositoryName(tempRepo.Root));
    }

    [Fact]
    public void GetRepositoryName_FromScpStyleOrigin_ReturnsOwnerSlashRepo()
    {
        using var tempRepo = TempRepo.WithOrigin("git@github.com:Org/parent.git");

        Assert.Equal("Org/parent", GitHelper.GetRepositoryName(tempRepo.Root));
    }

    [Fact]
    public void GetRepositoryName_InsideSubmoduleCheckout_FollowsGitdirPointer()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");
        var subRoot = tempRepo.AddSubmoduleCheckout("Core", "https://github.com/Org/child.git");

        Assert.Equal("Org/child", GitHelper.GetRepositoryName(subRoot));
        // Parent still resolves correctly.
        Assert.Equal("Org/parent", GitHelper.GetRepositoryName(tempRepo.Root));
    }

    [Fact]
    public void GetSubmodules_NoGitmodulesFile_ReturnsEmpty()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");

        Assert.Empty(GitHelper.GetSubmodules(tempRepo.Root));
    }

    [Fact]
    public void GetSubmodules_RelativeUrl_ResolvedAgainstHttpsOrigin()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");
        tempRepo.WriteGitmodules("""
            [submodule "Core"]
                path = Core
                url = ../child.git
            """);

        var subs = GitHelper.GetSubmodules(tempRepo.Root);

        Assert.Single(subs);
        Assert.Equal("Core", subs[0].MountPath);
        Assert.Equal("Org/child", subs[0].RepositoryName);
    }

    [Fact]
    public void GetSubmodules_RelativeUrl_ResolvedAgainstScpStyleOrigin()
    {
        using var tempRepo = TempRepo.WithOrigin("git@github.com:Org/parent.git");
        tempRepo.WriteGitmodules("""
            [submodule "Core"]
                path = Core
                url = ../child.git
            """);

        var subs = GitHelper.GetSubmodules(tempRepo.Root);

        Assert.Single(subs);
        Assert.Equal("Org/child", subs[0].RepositoryName);
    }

    [Fact]
    public void GetSubmodules_DoubleDotRelativeUrl_CrossesOrgBoundary()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");
        tempRepo.WriteGitmodules("""
            [submodule "X"]
                path = X
                url = ../../OtherOrg/foo.git
            """);

        var subs = GitHelper.GetSubmodules(tempRepo.Root);

        Assert.Single(subs);
        Assert.Equal("OtherOrg/foo", subs[0].RepositoryName);
    }

    [Fact]
    public void GetSubmodules_AbsoluteHttpsUrl_ReturnedAsIs()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");
        tempRepo.WriteGitmodules("""
            [submodule "X"]
                path = X
                url = https://github.com/Other/foo.git
            """);

        var subs = GitHelper.GetSubmodules(tempRepo.Root);

        Assert.Equal("Other/foo", subs[0].RepositoryName);
    }

    [Fact]
    public void GetSubmodules_AbsoluteScpUrl_ReturnedAsIs()
    {
        using var tempRepo = TempRepo.WithOrigin("https://github.com/Org/parent.git");
        tempRepo.WriteGitmodules("""
            [submodule "X"]
                path = X
                url = git@github.com:Other/foo.git
            """);

        var subs = GitHelper.GetSubmodules(tempRepo.Root);

        Assert.Equal("Other/foo", subs[0].RepositoryName);
    }

    /// <summary>
    /// Creates a temp directory containing a fake git repo with an <c>origin</c> remote,
    /// suitable for exercising <see cref="GitHelper"/> without invoking git itself. Disposal
    /// removes the directory tree.
    /// </summary>
    private sealed class TempRepo : IDisposable
    {
        public string Root { get; }

        private TempRepo(string root)
        {
            Root = root;
        }

        public static TempRepo WithOrigin(string originUrl)
        {
            var root = Path.Combine(Path.GetTempPath(), $"strazh-githelper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(
                Path.Combine(root, ".git", "config"),
                $"[remote \"origin\"]\n\turl = {originUrl}\n");
            return new TempRepo(root);
        }

        /// <summary>
        /// Creates a fake submodule checkout at <paramref name="mountPath"/> with a
        /// <c>.git</c> file pointing at <c>{parent}/.git/modules/{name}</c>, where the
        /// submodule's own config holds its origin URL. Returns the submodule's root.
        /// </summary>
        public string AddSubmoduleCheckout(string mountPath, string submoduleOriginUrl)
        {
            var subGitDir = Path.Combine(Root, ".git", "modules", mountPath);
            Directory.CreateDirectory(subGitDir);
            File.WriteAllText(
                Path.Combine(subGitDir, "config"),
                $"[remote \"origin\"]\n\turl = {submoduleOriginUrl}\n");

            var subRoot = Path.Combine(Root, mountPath);
            Directory.CreateDirectory(subRoot);
            // gitdir points from the submodule's root back into the parent's modules dir.
            File.WriteAllText(
                Path.Combine(subRoot, ".git"),
                $"gitdir: ../.git/modules/{mountPath}");
            return subRoot;
        }

        public void WriteGitmodules(string content)
            => File.WriteAllText(Path.Combine(Root, ".gitmodules"), content);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
