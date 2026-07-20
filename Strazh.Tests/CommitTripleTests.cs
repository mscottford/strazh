using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class CommitTripleTests
{
    [Fact]
    public void TripleFrom_LinksVersionedNodeToItsCommit()
    {
        var project = new ProjectNode("Repo.Lib", "Repo.Lib", commitSha: "abc123");
        var commit = new CommitNode("abc123", "Org/repo", "d1", "d2", "Author", "subject");

        var triple = new TripleFrom(project, commit);

        Assert.Equal("FROM", triple.Relationship.Type);
        Assert.Same(project, triple.NodeA);
        Assert.Same(commit, triple.NodeB);
        Assert.Equal("Commit", triple.NodeB.Label);
    }

    [Fact]
    public void TripleFrom_LinksSolutionAndFolderToTheirCommit()
    {
        var commit = new CommitNode("abc123", "Org/repo", "d1", "d2", "Author", "subject");

        var solutionTriple = new TripleFrom(new SolutionNode("App", commitSha: "abc123"), commit);
        Assert.Equal("FROM", solutionTriple.Relationship.Type);
        Assert.Equal("Solution", solutionTriple.NodeA.Label);
        Assert.Equal("Commit", solutionTriple.NodeB.Label);

        var folderTriple = new TripleFrom(
            new FolderNode("repo/src", "src", commitSha: "abc123"), commit);
        Assert.Equal("FROM", folderTriple.Relationship.Type);
        Assert.Equal("Folder", folderTriple.NodeA.Label);
        Assert.Equal("Commit", folderTriple.NodeB.Label);
    }

    [Fact]
    public void TriplePins_LinksSubmoduleMountFolderToPinnedCommit()
    {
        var mountFolder = new FolderNode("host/Core", "Core", FolderKind.Submodule, commitSha: "hostsha");
        var pinned = new CommitNode("abc123", "Org/submodule", "d1", "d2", "Author", "subject");

        var triple = new TriplePins(mountFolder, pinned);

        Assert.Equal("PINS", triple.Relationship.Type);
        Assert.Equal("Folder", triple.NodeA.Label);
        Assert.Equal("Commit", triple.NodeB.Label);
    }

    [Fact]
    public void TripleHas_LinksRepositoryToItsCommit()
    {
        var repository = new RepositoryNode("Org/repo");
        var commit = new CommitNode("abc123", "Org/repo", "d1", "d2", "Author", "subject");

        var triple = new TripleHas(repository, commit);

        Assert.Equal("HAS", triple.Relationship.Type);
        Assert.Equal("Repository", triple.NodeA.Label);
        Assert.Equal("Commit", triple.NodeB.Label);
    }
}
