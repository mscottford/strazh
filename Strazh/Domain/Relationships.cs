namespace Strazh.Domain
{
    public abstract class Relationship : IInspectable
    {
        public abstract string Type { get; }
        
        public string ToInspection() => $$"""{ "Type": {{Type.Inspect()}} }""";
    }

    public class HaveRelationship : Relationship
    {
        public override string Type => "HAVE";
    }

    public class InvokeRelationship : Relationship
    {
        public override string Type => "INVOKE";
    }

    public class ConstructRelationship : Relationship
    {
        public override string Type => "CONSTRUCT";
    }

    public class OfTypeRelationship : Relationship
    {
        public override string Type => "OF_TYPE";
    }

    public class DeclaredAtRelationship : Relationship
    {
        public override string Type => "DECLARED_AT";
    }

    public class IncludedInRelationship : Relationship
    {
        public override string Type => "INCLUDED_IN";
    }

    public class DependsOnRelationship : Relationship
    {
        public override string Type => "DEPENDS_ON";
    }

    public class ContainsRelationship : Relationship
    {
        public override string Type => "CONTAINS";
    }

    // Links a versioned code/structure node to the git commit it was analyzed from.
    public class FromRelationship : Relationship
    {
        public override string Type => "FROM";
    }

    // Links a repository to a commit that belongs to it. This is the only edge a Repository
    // node participates in: a node's owning repository is discovered transitively as
    // (node)-[:FROM]->(:Commit)<-[:HAS]-(:Repository).
    public class HasRelationship : Relationship
    {
        public override string Type => "HAS";
    }

    // Links a folder to a commit it pins — a submodule mount point and the submodule's
    // checked-out (pinned) commit.
    public class PinsRelationship : Relationship
    {
        public override string Type => "PINS";
    }
}