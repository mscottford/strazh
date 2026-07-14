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
    public void Properties_ProjectNodeFlagsOnlyWhenNotable()
    {
        var normal = new ProjectNode("Repo.Lib", "Repo.Lib").Properties();
        Assert.DoesNotContain("exists", normal.Keys);
        Assert.DoesNotContain("buildFailed", normal.Keys);

        var dangling = new ProjectNode("Repo.Lib", "Repo.Lib", exists: false, buildFailed: true).Properties();
        Assert.Equal(false, dangling["exists"]);
        Assert.Equal(true, dangling["buildFailed"]);
    }
}
