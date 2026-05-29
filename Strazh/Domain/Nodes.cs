using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Strazh.Domain
{
    public abstract class Node : IInspectable
    {
        public abstract string Label { get; }

        public virtual string FullName { get; }

        public virtual string Name { get; }

        /// <summary>
        /// Primary Key used to compare Matching of nodes on MERGE operation
        /// </summary>
        public virtual string Pk { get; protected set; }

        public Node(string fullName, string name)
        {
            FullName = fullName;
            Name = name;
            SetPrimaryKey();
        }

        protected virtual void SetPrimaryKey()
        {
            Pk = DeterministicHash(FullName);
        }

        // string.GetHashCode() is randomized per-process in .NET Core and later, so using it
        // as a Neo4j pk causes every run to generate different values for the same node,
        // defeating MERGE and creating duplicate nodes. MD5 gives a stable, collision-resistant
        // identifier across runs. (This is not a security use — stability is all that matters.)
        protected static string DeterministicHash(string value)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes);
        }

        public virtual string Set(string node) => 
            $"{node}.pk = \"{Pk}\", {node}.fullName = \"{FullName}\", {node}.name = \"{Name}\"";

        public string ToInspection() =>
            $$"""{ "Pk": {{Pk.Inspect()}}, "Label": {{Label.Inspect()}}, "FullName": {{FullName.Inspect()}}, "Name": {{Name.Inspect()}} }""";
    }

    // Code

    public abstract class CodeNode(string fullName, string name, string[] modifiers = null) : Node(fullName, name)
    {
        public string Modifiers { get; } = modifiers == null ? "" : string.Join(", ", modifiers);

        public override string Set(string node)
            => $"{base.Set(node)}{(string.IsNullOrEmpty(Modifiers) ? "" : $", {node}.modifiers = \"{Modifiers}\"")}";
    }

    public abstract class TypeNode(string fullName, string name, string[] modifiers = null)
        : CodeNode(fullName, name, modifiers);

    public class ClassNode(string fullName, string name, string[] modifiers = null)
        : TypeNode(fullName, name, modifiers)
    {
        public override string Label { get; } = "Class";
    }

    public class InterfaceNode(string fullName, string name, string[] modifiers = null)
        : TypeNode(fullName, name, modifiers)
    {
        public override string Label { get; } = "Interface";
    }

    public class MethodNode : CodeNode
    {
        public MethodNode(string fullName, string name, (string name, string type)[] args, string returnType, string[] modifiers = null)
            : base(fullName, name, modifiers)
        {
            Arguments = string.Join(", ", args.Select(x => $"{x.type} {x.name}"));
            ReturnType = returnType;
            SetPrimaryKey();
        }

        public override string Label { get; } = "Method";

        public string Arguments { get; }

        public string ReturnType { get; }

        public override string Set(string node)
            => $"{base.Set(node)}, {node}.returnType = \"{ReturnType}\", {node}.arguments = \"{Arguments}\"";

        protected override void SetPrimaryKey()
        {
            Pk = DeterministicHash($"{FullName}{Arguments}{ReturnType}");
        }
    }

    // Structure

    public class FileNode(string fullName, string name) : Node(fullName, name)
    {
        public override string Label { get; } = "File";
    }

    public enum FolderKind
    {
        Regular,
        Submodule,
    }

    public class FolderNode : Node
    {
        public FolderNode(string fullName, string name)
            : this(fullName, name, FolderKind.Regular) { }

        public FolderNode(string fullName, string name, FolderKind kind)
            : base(fullName, name)
        {
            Kind = kind;
            SetPrimaryKey();
        }

        public override string Label { get; } = "Folder";

        public FolderKind Kind { get; }

        // Kind is part of the PK so two folders at the same path with different kinds
        // (e.g. a Regular folder created by the file chain vs. a Submodule mount-point
        // node) are distinct nodes in the graph rather than colliding on MERGE.
        protected override void SetPrimaryKey()
        {
            Pk = DeterministicHash($"{FullName}|{Kind}");
        }

        // Only write kind when non-default to keep the property set clean on regular folders.
        public override string Set(string node)
            => Kind == FolderKind.Regular
                ? base.Set(node)
                : $"{base.Set(node)}, {node}.kind = \"{Kind}\"";
    }

    public class SolutionNode(string name) : Node(name, name)
    {
        public override string Label => "Solution";
    }

    public class ProjectNode(string fullName, string name) : Node(fullName, name)
    {
        public ProjectNode(string name)
            : this(name, name) { }

        public override string Label { get; } = "Project";
    }

    public class RepositoryNode(string name) : Node(name, name)
    {
        public override string Label { get; } = "Repository";
    }

    public class PackageNode : Node
    {
        public PackageNode(string fullName, string name, string version)
            : base(fullName, name)
        {
            Version = version;
            SetPrimaryKey();
        }

        public override string Label { get; } = "Package";

        public string Version { get; }

        public override string Set(string node)
            => $"{base.Set(node)}, {node}.version = \"{Version}\"";

        protected override void SetPrimaryKey()
        {
            Pk = DeterministicHash($"{FullName}{Version}");
        }
    }
}