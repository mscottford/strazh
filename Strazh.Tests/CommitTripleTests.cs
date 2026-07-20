using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class CommitTripleTests
{
    [Fact]
    public void TripleFromCommit_LinksVersionedNodeToItsCommit()
    {
        var project = new ProjectNode("Repo.Lib", "Repo.Lib", commitSha: "abc123");
        var commit = new CommitNode("abc123", "Org/repo", "d1", "d2", "Author", "subject");

        var triple = new TripleFromCommit(project, commit);

        Assert.Equal("FROM_COMMIT", triple.Relationship.Type);
        Assert.Same(project, triple.NodeA);
        Assert.Same(commit, triple.NodeB);
        Assert.Equal("Commit", triple.NodeB.Label);
    }

    [Fact]
    public void TriplePins_LinksRepositoryToPinnedCommit()
    {
        var repository = new RepositoryNode("Org/host");
        var commit = new CommitNode("abc123", "Org/submodule", "d1", "d2", "Author", "subject");

        var triple = new TriplePins(repository, commit);

        Assert.Equal("PINS", triple.Relationship.Type);
        Assert.Equal("Repository", triple.NodeA.Label);
        Assert.Equal("Commit", triple.NodeB.Label);
    }
}
