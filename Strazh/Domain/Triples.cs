namespace Strazh.Domain
{
    public abstract class Triple(Node nodeA, Node nodeB, Relationship relationship) : IInspectable
    {
        public Node NodeA { get; set; } = nodeA;

        public Node NodeB { get; set; } = nodeB;

        public Relationship Relationship { get; set; } = relationship;

        public override string ToString()
            => $"MERGE (a:{NodeA.Label} {{ pk: \"{NodeA.Pk}\" }}) ON CREATE SET {NodeA.Set("a")} ON MATCH SET {NodeA.Set("a")} MERGE (b:{NodeB.Label} {{ pk: \"{NodeB.Pk}\" }}) ON CREATE SET {NodeB.Set("b")} ON MATCH SET {NodeB.Set("b")} MERGE (a)-[:{Relationship.Type}]->(b);";

        public string ToInspection() => 
            $$"""{"NodeA": {{NodeA.Inspect()}}, "NodeB": {{NodeB.Inspect()}}, "Relationship": {{Relationship.Inspect()}} }""";
    }

    // Structure

    public class TripleDependsOnProject(
        ProjectNode projectA,
        ProjectNode projectB) : Triple(projectA, projectB, new DependsOnRelationship());

    public class TripleDependsOnPackage(
        ProjectNode projectA,
        PackageNode packageB) : Triple(projectA, packageB, new DependsOnRelationship());

    public class TripleIncludedIn : Triple
    {
        public TripleIncludedIn(
            SolutionNode solution,
            FolderNode folder)
            : base(solution, folder, new IncludedInRelationship())
        {
        }

        public TripleIncludedIn(
            ProjectNode contentA,
            FolderNode contentB)
            : base(contentA, contentB, new IncludedInRelationship())
        { }

        public TripleIncludedIn(
            FolderNode contentA,
            FolderNode contentB)
            : base(contentA, contentB, new IncludedInRelationship())
        { }

        public TripleIncludedIn(
            FileNode contentA,
            FolderNode contentB)
            : base(contentA, contentB, new IncludedInRelationship())
        { }

    }
    
    public class TripleContains(
        SolutionNode solution,
        ProjectNode project) : Triple(solution, project, new ContainsRelationship());

    public class TripleDeclaredAt(
        TypeNode typeA,
        FileNode fileB) : Triple(typeA, fileB, new DeclaredAtRelationship());

    // Code

    public class TripleInvoke(
        MethodNode methodA,
        MethodNode methodB) : Triple(methodA, methodB, new InvokeRelationship());

    public class TripleHave(
        TypeNode typeA,
        MethodNode methodB) : Triple(typeA, methodB, new HaveRelationship());

    public class TripleConstruct(
        MethodNode methodA,
        ClassNode classB) : Triple(methodA, classB, new ConstructRelationship());

    public class TripleOfType : Triple
    {
        public TripleOfType(
            ClassNode classA,
            TypeNode typeB)
            : base(classA, typeB, new OfTypeRelationship())
        { }

        public TripleOfType(
            InterfaceNode interfaceA,
            InterfaceNode interfaceB)
            : base(interfaceA, interfaceB, new OfTypeRelationship())
        { }
    }

    // Version provenance

    // A versioned node (Project / File / Class / Interface / Method / Folder / Solution) and the
    // commit it came from.
    public class TripleFrom(
        Node node,
        CommitNode commit) : Triple(node, commit, new FromRelationship());

    // A repository and a commit that belongs to it. Emitted for every commit the analysis records,
    // so a node's repository is reachable via (node)-[:FROM]->(:Commit)<-[:HAS]-(:Repository).
    public class TripleHas(
        RepositoryNode repository,
        CommitNode commit) : Triple(repository, commit, new HasRelationship());

    // A submodule mount-point folder and the submodule's checked-out (pinned) commit.
    public class TriplePins(
        FolderNode folder,
        CommitNode commit) : Triple(folder, commit, new PinsRelationship());
}