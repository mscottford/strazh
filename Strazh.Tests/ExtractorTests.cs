using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Strazh.Analysis;
using Strazh.Domain;
using Xunit;

namespace Strazh.Tests;

public class ExtractorTests
{
    private const string Source = """
        namespace Demo
        {
            public interface IThing { }

            public class Helper
            {
                public void Ping() { }
            }

            public class Widget : IThing
            {
                public int Compute(string input)
                {
                    var helper = new Helper();
                    helper.Ping();
                    return input.Length;
                }
            }
        }
        """;

    // Compiles Source with a synthetic file path under a "repo" root folder and returns
    // the triples produced by analyzing class declarations.
    private static IList<Triple> AnalyzeClasses(string filePath = "/repo/src/Widget.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: filePath);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>();
        var compilation = CSharpCompilation.Create(
            "Demo",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sem = compilation.GetSemanticModel(tree);

        var triples = new List<Triple>();
        Extractor.AnalyzeTree<ClassDeclarationSyntax>(triples, tree, sem, new FolderNode("repo", "repo"));
        return triples;
    }

    [Fact]
    public void AnalyzeTree_DeclaresClassAtItsFile()
    {
        var triples = AnalyzeClasses();

        Assert.Contains(triples, t =>
            t is TripleDeclaredAt
            && t.NodeA is ClassNode { Name: "Widget" }
            && t.NodeB is FileNode { Name: "Widget.cs" });
    }

    [Fact]
    public void AnalyzeTree_RecordsClassImplementingInterface()
    {
        var triples = AnalyzeClasses();

        Assert.Contains(triples, t =>
            t is TripleOfType
            && t.NodeA is ClassNode { Name: "Widget" }
            && t.NodeB is InterfaceNode { Name: "IThing" });
    }

    [Fact]
    public void AnalyzeTree_RecordsMethodsOnType()
    {
        var triples = AnalyzeClasses();

        var have = Assert.Single(triples.Where(t =>
            t is TripleHave
            && t.NodeA is ClassNode { Name: "Widget" }
            && t.NodeB is MethodNode { Name: "Compute" }));
        var method = (MethodNode)have.NodeB;
        Assert.Equal("string input", method.Arguments);
        Assert.Equal("int", method.ReturnType);
    }

    [Fact]
    public void AnalyzeTree_RecordsObjectConstructionAndInvocationInsideMethod()
    {
        var triples = AnalyzeClasses();

        Assert.Contains(triples, t =>
            t is TripleConstruct
            && t.NodeA is MethodNode { Name: "Compute" }
            && t.NodeB is ClassNode { Name: "Helper" });
        Assert.Contains(triples, t =>
            t is TripleInvoke
            && t.NodeA is MethodNode { Name: "Compute" }
            && t.NodeB is MethodNode { Name: "Ping" });
    }

    [Fact]
    public void AnalyzeTree_BuildsFolderChainFromRepoRoot()
    {
        var triples = AnalyzeClasses();

        // File is included in its immediate folder, which chains up to the repo root.
        Assert.Contains(triples, t =>
            t is TripleIncludedIn
            && t.NodeA is FileNode { Name: "Widget.cs" }
            && t.NodeB is FolderNode { Name: "src" });
    }
}
