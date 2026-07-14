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
}
