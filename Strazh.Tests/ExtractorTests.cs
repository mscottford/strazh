using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    [Fact]
    public void AnalyzeTree_StampsDeclaredNodesWithTheirFileCommit()
    {
        using var repo = new TempGitRepo();
        var srcDir = Path.Combine(repo.Root, "src");
        Directory.CreateDirectory(srcDir);
        var filePath = Path.Combine(srcDir, "Widget.cs");
        File.WriteAllText(filePath, Source);

        var triples = AnalyzeClasses(filePath);

        // The declared class, its declared method, and the file all carry the repo's HEAD commit.
        var widget = triples.Select(t => t.NodeA).OfType<ClassNode>().First(c => c.Name == "Widget");
        Assert.Equal(repo.Head, widget.CommitSha);

        var compute = triples.Select(t => t.NodeB).OfType<MethodNode>().First(m => m.Name == "Compute");
        Assert.Equal(repo.Head, compute.CommitSha);

        var file = triples.Select(t => t.NodeB).OfType<FileNode>().First(f => f.Name == "Widget.cs");
        Assert.Equal(repo.Head, file.CommitSha);

        // The folders on the file's chain are versioned by the same commit as the file.
        var srcFolder = triples.Select(t => t.NodeB).OfType<FolderNode>().First(f => f.Name == "src");
        Assert.Equal(repo.Head, srcFolder.CommitSha);

        // FROM_COMMIT links the declared class to a Commit node carrying that sha.
        Assert.Contains(triples, t =>
            t.Relationship.Type == "FROM_COMMIT"
            && t.NodeA is ClassNode { Name: "Widget" }
            && t.NodeB is CommitNode commit && commit.Sha == repo.Head);

        // ...and each folder on the chain is linked to that commit too.
        Assert.Contains(triples, t =>
            t.Relationship.Type == "FROM_COMMIT"
            && t.NodeA is FolderNode { Name: "src" }
            && t.NodeB is CommitNode commit && commit.Sha == repo.Head);
    }

    [Fact]
    public void AnalyzeTree_OutsideGitRepository_LeavesNodesUnversioned()
    {
        // The default synthetic path is not under a git repo, so nodes degrade to unversioned
        // (null commitSha) and no FROM_COMMIT is emitted — the graph still builds.
        var triples = AnalyzeClasses();

        var widget = triples.Select(t => t.NodeA).OfType<ClassNode>().First(c => c.Name == "Widget");
        Assert.Null(widget.CommitSha);
        Assert.DoesNotContain(triples, t => t.Relationship.Type == "FROM_COMMIT");
    }

    // A throwaway git repository with a single seed commit, for exercising the file-path -> commit
    // resolution end to end. git init (no remote), so the commit has a sha but no origin repo name.
    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string Head { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), $"strazh-extractor-git-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Run("init", "--initial-branch=main");
            Run("config", "user.email", "test@strazh.invalid");
            Run("config", "user.name", "Strazh Test");
            File.WriteAllText(Path.Combine(Root, "seed.txt"), "seed");
            Run("add", "seed.txt");
            Run("commit", "-m", "seed");
            Head = Exec(capture: true, "rev-parse", "HEAD");
        }

        private void Run(params string[] args) => Exec(capture: false, args);

        private string Exec(bool capture, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
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
            return capture ? stdout.Trim() : stdout;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
