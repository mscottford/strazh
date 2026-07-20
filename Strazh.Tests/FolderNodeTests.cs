using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class FolderNodeTests
{
    /// <summary>
    /// Two folders sharing a path but differing in <see cref="FolderKind"/> must be
    /// distinct graph nodes. A Submodule mount-point at <c>host/Core</c> and a
    /// hypothetical Regular folder at the same path must not collide on MERGE.
    /// </summary>
    [Fact]
    public void Pk_DiffersByKindWhenFullNameMatches()
    {
        var regular = new FolderNode("host/Core", "Core");
        var submodule = new FolderNode("host/Core", "Core", FolderKind.Submodule);

        Assert.NotEqual(regular.Pk, submodule.Pk);
    }

    [Fact]
    public void Pk_StableAcrossInstancesForSameInputs()
    {
        var a = new FolderNode("host/Core", "Core", FolderKind.Submodule);
        var b = new FolderNode("host/Core", "Core", FolderKind.Submodule);

        Assert.Equal(a.Pk, b.Pk);
    }

    [Fact]
    public void Set_OmitsKindForRegularFolders()
    {
        var folder = new FolderNode("foo", "foo");

        Assert.DoesNotContain("kind", folder.Set("f"));
    }

    [Fact]
    public void Set_WritesSubmoduleKindLiteral()
    {
        var folder = new FolderNode("host/Core", "Core", FolderKind.Submodule);

        Assert.Contains("f.kind = \"Submodule\"", folder.Set("f"));
    }

    [Fact]
    public void Properties_OmitsKindForRegularAndWritesForSubmodule()
    {
        Assert.DoesNotContain("kind", new FolderNode("foo", "foo").Properties().Keys);
        Assert.Equal("Submodule",
            new FolderNode("host/Core", "Core", FolderKind.Submodule).Properties()["kind"]);
    }

    // A folder present at two source commits must be two distinct nodes, and the commit sha
    // composes with (rather than replaces) the kind that already participates in identity.
    [Fact]
    public void Pk_FoldsInCommitShaAlongsideKind()
    {
        var regularAtA = new FolderNode("repo/src", "src", commitSha: "sha1");
        var regularAtB = new FolderNode("repo/src", "src", commitSha: "sha2");
        var regularUnversioned = new FolderNode("repo/src", "src");
        var submoduleAtA = new FolderNode("repo/src", "src", FolderKind.Submodule, "sha1");

        Assert.NotEqual(regularAtA.Pk, regularAtB.Pk);
        Assert.NotEqual(regularUnversioned.Pk, regularAtA.Pk);
        Assert.NotEqual(regularAtA.Pk, submoduleAtA.Pk);
        Assert.Equal(regularAtA.Pk, new FolderNode("repo/src", "src", commitSha: "sha1").Pk);
    }

    [Fact]
    public void CommitSha_EmittedWhenPresent_OmittedWhenNull()
    {
        var versioned = new FolderNode("repo/src", "src", commitSha: "abc123");
        Assert.Contains("f.commitSha = \"abc123\"", versioned.Set("f"));
        Assert.Equal("abc123", versioned.Properties()["commitSha"]);

        var plain = new FolderNode("repo/src", "src");
        Assert.DoesNotContain("commitSha", plain.Set("f"));
        Assert.DoesNotContain("commitSha", plain.Properties().Keys);
    }
}
