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
}
