using System;
using System.Collections.Generic;
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
        public virtual string Pk { get; protected set; } = "";

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

        /// <summary>
        /// The node's properties as a parameter map, used by the batched, parameterized
        /// UNWIND write path (<c>SET n += map</c>). Mirrors <see cref="Set"/> exactly: the
        /// same properties, gated by the same conditions, so both paths write identical graphs.
        /// Keeping optional properties out of the map (rather than writing null) preserves the
        /// "don't clobber" MERGE semantics — a reference-only node simply omits them.
        /// </summary>
        public virtual IDictionary<string, object> Properties()
            => new Dictionary<string, object>
            {
                ["pk"] = Pk,
                ["fullName"] = FullName,
                ["name"] = Name,
            };

        public string ToInspection() =>
            $$"""{ "Pk": {{Pk.Inspect()}}, "Label": {{Label.Inspect()}}, "FullName": {{FullName.Inspect()}}, "Name": {{Name.Inspect()}} }""";
    }

    // Code

    public abstract class CodeNode(string fullName, string name, string[]? modifiers = null) : Node(fullName, name)
    {
        public string Modifiers { get; } = modifiers == null ? "" : string.Join(", ", modifiers);

        public override string Set(string node)
            => $"{base.Set(node)}{(string.IsNullOrEmpty(Modifiers) ? "" : $", {node}.modifiers = \"{Modifiers}\"")}";

        public override IDictionary<string, object> Properties()
        {
            var p = base.Properties();
            if (!string.IsNullOrEmpty(Modifiers))
            {
                p["modifiers"] = Modifiers;
            }
            return p;
        }
    }

    public abstract class TypeNode(string fullName, string name, string[]? modifiers = null)
        : CodeNode(fullName, name, modifiers);

    public class ClassNode(string fullName, string name, string[]? modifiers = null)
        : TypeNode(fullName, name, modifiers)
    {
        public override string Label { get; } = "Class";
    }

    public class InterfaceNode(string fullName, string name, string[]? modifiers = null)
        : TypeNode(fullName, name, modifiers)
    {
        public override string Label { get; } = "Interface";
    }

    public class MethodNode : CodeNode
    {
        public MethodNode(string fullName, string name, (string name, string type)[] args, string returnType, string[]? modifiers = null)
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

        public override IDictionary<string, object> Properties()
        {
            var p = base.Properties();
            p["returnType"] = ReturnType;
            p["arguments"] = Arguments;
            return p;
        }

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

        public override IDictionary<string, object> Properties()
        {
            var p = base.Properties();
            if (Kind != FolderKind.Regular)
            {
                p["kind"] = Kind.ToString();
            }
            return p;
        }
    }

    public class SolutionNode(string name, bool buildFailed = false) : Node(name, name)
    {
        public override string Label => "Solution";

        /// <summary>True when the solution file could not be parsed/loaded — e.g. it references
        /// a project type MSBuild no longer supports (a legacy .vcproj) — so its membership could
        /// not be analyzed. buildFailed is not part of the pk, so a flagged node MERGEs onto the
        /// same Solution as an unflagged one of the same name.</summary>
        public bool BuildFailed { get; } = buildFailed;

        // Emitted only when notable, so normally-loaded solutions stay clean.
        public override string Set(string node)
            => BuildFailed ? $"{base.Set(node)}, {node}.buildFailed = true" : base.Set(node);

        public override IDictionary<string, object> Properties()
        {
            var p = base.Properties();
            if (BuildFailed)
            {
                p["buildFailed"] = true;
            }
            return p;
        }
    }

    public class ProjectNode(string fullName, string name, string[]? targetFrameworks = null, bool exists = true, bool buildFailed = false)
        : Node(fullName, name)
    {
        public ProjectNode(string name)
            : this(name, name) { }

        public override string Label { get; } = "Project";

        /// <summary>Target framework(s) the project builds for, stored as a native Neo4j list.</summary>
        public string[] TargetFrameworks { get; } =
            (targetFrameworks ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();

        /// <summary>False when the project's .csproj does not exist on disk (a dangling reference).</summary>
        public bool Exists { get; } = exists;

        /// <summary>True when the project was represented from a build that did not succeed
        /// (its facts come from whatever Buildalyzer collected rather than a clean build).</summary>
        public bool BuildFailed { get; } = buildFailed;

        // targetFrameworks is emitted only when known (a project also appears as a reference
        // target created without TFMs, and those MERGEs must not clobber the analyzed value).
        // exists / buildFailed are emitted only when notable, so normal projects stay clean.
        public override string Set(string node)
        {
            var set = base.Set(node);
            if (TargetFrameworks.Length > 0)
            {
                set += $", {node}.targetFrameworks = [{string.Join(", ", TargetFrameworks.Select(t => $"\"{t}\""))}]";
            }
            if (!Exists)
            {
                set += $", {node}.exists = false";
            }
            if (BuildFailed)
            {
                set += $", {node}.buildFailed = true";
            }
            return set;
        }

        public override IDictionary<string, object> Properties()
        {
            var p = base.Properties();
            if (TargetFrameworks.Length > 0)
            {
                p["targetFrameworks"] = TargetFrameworks;
            }
            if (!Exists)
            {
                p["exists"] = false;
            }
            if (BuildFailed)
            {
                p["buildFailed"] = true;
            }
            return p;
        }
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

        public override IDictionary<string, object> Properties()
        {
            var p = base.Properties();
            p["version"] = Version;
            return p;
        }

        protected override void SetPrimaryKey()
        {
            Pk = DeterministicHash($"{FullName}{Version}");
        }
    }
}