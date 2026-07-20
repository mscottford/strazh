using System.Text.RegularExpressions;
using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class NodeTests
{
    [Fact]
    public void Pk_IsStableUppercaseHexHash()
    {
        var node = new ClassNode("Ns.Foo", "Foo");

        Assert.Matches(new Regex("^[0-9A-F]{32}$"), node.Pk);
        Assert.Equal(node.Pk, new ClassNode("Ns.Foo", "Foo").Pk);
    }

    [Fact]
    public void ClassNode_HasClassLabelAndCoreSetProperties()
    {
        var node = new ClassNode("Ns.Foo", "Foo");

        Assert.Equal("Class", node.Label);
        var set = node.Set("c");
        Assert.Contains("c.pk = ", set);
        Assert.Contains("c.fullName = \"Ns.Foo\"", set);
        Assert.Contains("c.name = \"Foo\"", set);
    }

    [Fact]
    public void CodeNode_OmitsModifiersWhenNull()
    {
        var node = new ClassNode("Ns.Foo", "Foo", modifiers: null);

        Assert.DoesNotContain("modifiers", node.Set("c"));
    }

    [Fact]
    public void CodeNode_WritesModifiersWhenProvided()
    {
        var node = new ClassNode("Ns.Foo", "Foo", new[] { "public", "sealed" });

        Assert.Contains("c.modifiers = \"public, sealed\"", node.Set("c"));
    }

    [Fact]
    public void InterfaceNode_HasInterfaceLabel()
    {
        Assert.Equal("Interface", new InterfaceNode("Ns.IFoo", "IFoo").Label);
    }

    [Fact]
    public void MethodNode_FormatsArgumentsAndReturnTypeInSet()
    {
        var node = new MethodNode(
            "Ns.Foo.Bar",
            "Bar",
            new[] { (name: "count", type: "int"), (name: "label", type: "string") },
            "bool");

        Assert.Equal("Method", node.Label);
        Assert.Equal("int count, string label", node.Arguments);
        Assert.Equal("bool", node.ReturnType);
        var set = node.Set("m");
        Assert.Contains("m.returnType = \"bool\"", set);
        Assert.Contains("m.arguments = \"int count, string label\"", set);
    }

    [Fact]
    public void MethodNode_PkDependsOnSignatureNotJustFullName()
    {
        var noArgs = new MethodNode("Ns.Foo.Bar", "Bar", System.Array.Empty<(string, string)>(), "void");
        var withArgs = new MethodNode("Ns.Foo.Bar", "Bar", new[] { (name: "x", type: "int") }, "void");

        Assert.NotEqual(noArgs.Pk, withArgs.Pk);
    }

    [Fact]
    public void PackageNode_WritesVersionInSet()
    {
        var node = new PackageNode("Newtonsoft.Json", "Newtonsoft.Json", "13.0.1");

        Assert.Contains("p.version = \"13.0.1\"", node.Set("p"));
    }

    [Fact]
    public void ProjectNode_OmitsTargetFrameworksWhenNull()
    {
        var node = new ProjectNode("Repo.Lib", "Repo.Lib", targetFrameworks: null);

        Assert.Empty(node.TargetFrameworks);
        Assert.DoesNotContain("targetFrameworks", node.Set("p"));
    }

    [Fact]
    public void ProjectNode_WritesTargetFrameworkListWhenProvided()
    {
        var node = new ProjectNode("Repo.Lib", "Repo.Lib", new[] { "net8.0", "net472" });

        Assert.Contains("p.targetFrameworks = [\"net8.0\", \"net472\"]", node.Set("p"));
    }

    // Properties() feeds the parameterized UNWIND write path and must stay in lockstep with
    // Set(): the same keys, gated by the same conditions, so both write identical graphs.

    [Fact]
    public void Properties_CoreKeysAlwaysPresent()
    {
        var node = new ClassNode("Ns.Foo", "Foo");

        var props = node.Properties();
        Assert.Equal(node.Pk, props["pk"]);
        Assert.Equal("Ns.Foo", props["fullName"]);
        Assert.Equal("Foo", props["name"]);
    }

    [Fact]
    public void Properties_OmitsModifiersWhenNullAndWritesWhenProvided()
    {
        Assert.DoesNotContain("modifiers", new ClassNode("Ns.Foo", "Foo").Properties().Keys);
        Assert.Equal("public, sealed",
            new ClassNode("Ns.Foo", "Foo", new[] { "public", "sealed" }).Properties()["modifiers"]);
    }

    [Fact]
    public void Properties_MethodNodeCarriesSignature()
    {
        var node = new MethodNode(
            "Ns.Foo.Bar",
            "Bar",
            new[] { (name: "count", type: "int") },
            "bool");

        var props = node.Properties();
        Assert.Equal("bool", props["returnType"]);
        Assert.Equal("int count", props["arguments"]);
    }

    [Fact]
    public void Properties_PackageNodeCarriesVersion()
    {
        Assert.Equal("13.0.1",
            new PackageNode("Newtonsoft.Json", "Newtonsoft.Json", "13.0.1").Properties()["version"]);
    }

    [Fact]
    public void Properties_ProjectNodeTargetFrameworksAsNativeList()
    {
        var withTfms = new ProjectNode("Repo.Lib", "Repo.Lib", new[] { "net8.0", "net472" });
        Assert.Equal(new[] { "net8.0", "net472" }, (string[])withTfms.Properties()["targetFrameworks"]);

        Assert.DoesNotContain("targetFrameworks",
            new ProjectNode("Repo.Lib", "Repo.Lib", targetFrameworks: null).Properties().Keys);
    }

    [Fact]
    public void SolutionNode_OmitsBuildFailedByDefault_WritesItWhenFlagged()
    {
        Assert.DoesNotContain("buildFailed", new SolutionNode("App").Set("s"));
        Assert.DoesNotContain("buildFailed", new SolutionNode("App").Properties().Keys);

        Assert.Contains("s.buildFailed = true", new SolutionNode("App", buildFailed: true).Set("s"));
        Assert.Equal(true, new SolutionNode("App", buildFailed: true).Properties()["buildFailed"]);
    }

    [Fact]
    public void SolutionNode_BuildFailedNotPartOfPk()
    {
        Assert.Equal(new SolutionNode("App").Pk, new SolutionNode("App", buildFailed: true).Pk);
    }

    [Fact]
    public void Properties_ProjectNodeFlagsOnlyWhenNotable()
    {
        var normal = new ProjectNode("Repo.Lib", "Repo.Lib").Properties();
        Assert.DoesNotContain("exists", normal.Keys);
        Assert.DoesNotContain("buildFailed", normal.Keys);

        var dangling = new ProjectNode("Repo.Lib", "Repo.Lib", exists: false, buildFailed: true).Properties();
        Assert.Equal(false, dangling["exists"]);
        Assert.Equal(true, dangling["buildFailed"]);
    }

    // Commit-scoped identity: the same logical node at two different source commits must be two
    // distinct graph nodes (distinct pk), while the same node at the same commit stays one node.

    [Fact]
    public void CommitSha_MakesPkDistinctPerCommitButStableWithinACommit()
    {
        var unversioned = new ClassNode("Ns.Foo", "Foo");
        var atA = new ClassNode("Ns.Foo", "Foo", commitSha: "aaaaaaa");
        var atB = new ClassNode("Ns.Foo", "Foo", commitSha: "bbbbbbb");

        Assert.NotEqual(unversioned.Pk, atA.Pk);
        Assert.NotEqual(atA.Pk, atB.Pk);
        // Two checkouts at the same commit dedupe onto one node.
        Assert.Equal(atA.Pk, new ClassNode("Ns.Foo", "Foo", commitSha: "aaaaaaa").Pk);
    }

    [Fact]
    public void CommitSha_ScopesEveryVersionedNodeType()
    {
        Assert.NotEqual(
            new InterfaceNode("Ns.IFoo", "IFoo").Pk,
            new InterfaceNode("Ns.IFoo", "IFoo", commitSha: "sha1").Pk);
        Assert.NotEqual(
            new FileNode("src/Foo.cs", "Foo.cs").Pk,
            new FileNode("src/Foo.cs", "Foo.cs", "sha1").Pk);
        Assert.NotEqual(
            new ProjectNode("Repo.Lib", "Repo.Lib").Pk,
            new ProjectNode("Repo.Lib", "Repo.Lib", commitSha: "sha1").Pk);
        // The structural Solution and Folder nodes are now versioned too.
        Assert.NotEqual(
            new SolutionNode("App").Pk,
            new SolutionNode("App", commitSha: "sha1").Pk);
        Assert.NotEqual(
            new FolderNode("repo/src", "src").Pk,
            new FolderNode("repo/src", "src", commitSha: "sha1").Pk);
    }

    [Fact]
    public void SolutionNode_CommitShaFoldsIntoPkButIsNotEmitted()
    {
        var atA = new SolutionNode("App", commitSha: "sha1");
        var atB = new SolutionNode("App", commitSha: "sha2");

        // Same solution at two commits -> two distinct nodes; stable within a commit.
        Assert.NotEqual(atA.Pk, atB.Pk);
        Assert.NotEqual(new SolutionNode("App").Pk, atA.Pk);
        Assert.Equal(atA.Pk, new SolutionNode("App", commitSha: "sha1").Pk);
        // buildFailed still is not part of identity, even when the node is versioned.
        Assert.Equal(atA.Pk, new SolutionNode("App", buildFailed: true, commitSha: "sha1").Pk);

        // The sha is not written as a property — it lives in the pk and the FROM edge.
        Assert.DoesNotContain("commitSha", atA.Set("s"));
        Assert.DoesNotContain("commitSha", atA.Properties().Keys);
    }

    [Fact]
    public void CommitSha_FoldsIntoMethodPkAlongsideSignature()
    {
        var args = new[] { (name: "x", type: "int") };
        var atA = new MethodNode("Ns.Foo.Bar", "Bar", args, "void", commitSha: "sha1");
        var atB = new MethodNode("Ns.Foo.Bar", "Bar", args, "void", commitSha: "sha2");
        var unversioned = new MethodNode("Ns.Foo.Bar", "Bar", args, "void");

        Assert.NotEqual(atA.Pk, atB.Pk);
        Assert.NotEqual(unversioned.Pk, atA.Pk);
    }

    // The sha lives only in the pk (identity) and is reachable via the FROM edge to the Commit
    // node — it is deliberately NOT written as a node property, even on a versioned node.
    [Fact]
    public void CommitSha_IsNeverWrittenAsAProperty()
    {
        var versioned = new ClassNode("Ns.Foo", "Foo", commitSha: "abc123");
        Assert.DoesNotContain("commitSha", versioned.Set("c"));
        Assert.DoesNotContain("commitSha", versioned.Properties().Keys);

        var plain = new ClassNode("Ns.Foo", "Foo");
        Assert.DoesNotContain("commitSha", plain.Set("c"));
        Assert.DoesNotContain("commitSha", plain.Properties().Keys);

        // ...but identity is still commit-scoped.
        Assert.NotEqual(plain.Pk, versioned.Pk);
    }

    // The Commit node itself: identity is the sha, and it carries the metadata used to date a version.

    [Fact]
    public void CommitNode_HasCommitLabelAndCarriesMetadata()
    {
        var commit = new CommitNode(
            "abc123", "Org/repo", "2026-03-01T00:00:00Z", "2026-03-02T00:00:00Z", "Jane Dev", "Fix things");

        Assert.Equal("Commit", commit.Label);
        var props = commit.Properties();
        Assert.Equal("abc123", props["sha"]);
        Assert.Equal("Org/repo", props["repo"]);
        Assert.Equal("2026-03-01T00:00:00Z", props["authoredDate"]);
        Assert.Equal("2026-03-02T00:00:00Z", props["committedDate"]);
        Assert.Equal("Jane Dev", props["author"]);
        Assert.Equal("Fix things", props["subject"]);

        var set = commit.Set("k");
        Assert.Contains("k.sha = \"abc123\"", set);
        Assert.Contains("k.committedDate = \"2026-03-02T00:00:00Z\"", set);
    }

    [Fact]
    public void CommitNode_PkIsShaOnly()
    {
        Assert.Matches(new Regex("^[0-9A-F]{32}$"), new CommitNode("abc", "r", "a", "c", "au", "s").Pk);
        // Metadata is not part of identity — same sha is the same commit node.
        Assert.Equal(
            new CommitNode("abc", "r", "a", "c", "au", "s").Pk,
            new CommitNode("abc", "other", "x", "y", "z", "w").Pk);
    }

    [Fact]
    public void CommitNode_EscapesFreeTextInRawSet()
    {
        var commit = new CommitNode("abc", "Org/repo", "d1", "d2", "Jane \"JD\" Dev", "Fix \"the\" bug\\path");
        var set = commit.Set("k");

        // A quote or backslash in free text must be escaped, not left to break the Cypher literal.
        Assert.Contains("k.author = \"Jane \\\"JD\\\" Dev\"", set);
        Assert.Contains("Fix \\\"the\\\" bug\\\\path", set);
    }
}
