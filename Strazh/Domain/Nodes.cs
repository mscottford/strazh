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

        public Node(string fullName, string name, string? commitSha = null)
        {
            FullName = fullName;
            Name = name;
            CommitSha = commitSha;
            SetPrimaryKey();
        }

        /// <summary>
        /// The commit sha of the source version this node belongs to, or null for version-agnostic
        /// structural nodes (folders, solutions, repositories, commits). When set, it is folded into
        /// the pk so the same logical node checked out at two different commits becomes two distinct
        /// graph nodes rather than colliding on MERGE.
        /// </summary>
        public string? CommitSha { get; }

        // Prefixes a node's identity string with its commit sha (when versioned) so otherwise
        // identical logical nodes from different checked-out commits get distinct pks. A sha is
        // fixed-format hex, so the first space unambiguously delimits it from the key that follows.
        protected string ScopedKey(string key)
            => string.IsNullOrEmpty(CommitSha) ? key : $"{CommitSha} {key}";

        // Escapes a string for embedding in a double-quoted Cypher literal on the raw Set() path
        // (used by inspection/tests). Free-text values (a commit author or subject) can contain
        // quotes or backslashes; the production write path binds them as parameters instead.
        protected static string EscapeForCypher(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace('\n', ' ').Replace('\r', ' ');

        protected virtual void SetPrimaryKey()
        {
            Pk = DeterministicHash(ScopedKey(FullName));
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

        public virtual string Set(string node)
        {
            var set = $"{node}.pk = \"{Pk}\", {node}.fullName = \"{FullName}\", {node}.name = \"{Name}\"";
            if (!string.IsNullOrEmpty(CommitSha))
            {
                set += $", {node}.commitSha = \"{CommitSha}\"";
            }
            return set;
        }

        /// <summary>
        /// The node's properties as a parameter map, used by the batched, parameterized
        /// UNWIND write path (<c>SET n += map</c>). Mirrors <see cref="Set"/> exactly: the
        /// same properties, gated by the same conditions, so both paths write identical graphs.
        /// Keeping optional properties out of the map (rather than writing null) preserves the
        /// "don't clobber" MERGE semantics — a reference-only node simply omits them.
        /// </summary>
        public virtual IDictionary<string, object> Properties()
        {
            var properties = new Dictionary<string, object>
            {
                ["pk"] = Pk,
                ["fullName"] = FullName,
                ["name"] = Name,
            };
            if (!string.IsNullOrEmpty(CommitSha))
            {
                properties["commitSha"] = CommitSha;
            }
            return properties;
        }

        public string ToInspection() =>
            $$"""{ "Pk": {{Pk.Inspect()}}, "Label": {{Label.Inspect()}}, "FullName": {{FullName.Inspect()}}, "Name": {{Name.Inspect()}} }""";
    }

    // Code

    public abstract class CodeNode(string fullName, string name, string[]? modifiers = null, string? commitSha = null)
        : Node(fullName, name, commitSha)
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

    public abstract class TypeNode(string fullName, string name, string[]? modifiers = null, string? commitSha = null)
        : CodeNode(fullName, name, modifiers, commitSha);

    public class ClassNode(string fullName, string name, string[]? modifiers = null, string? commitSha = null)
        : TypeNode(fullName, name, modifiers, commitSha)
    {
        public override string Label { get; } = "Class";
    }

    public class InterfaceNode(string fullName, string name, string[]? modifiers = null, string? commitSha = null)
        : TypeNode(fullName, name, modifiers, commitSha)
    {
        public override string Label { get; } = "Interface";
    }

    public class MethodNode : CodeNode
    {
        public MethodNode(string fullName, string name, (string name, string type)[] args, string returnType, string[]? modifiers = null, string? commitSha = null)
            : base(fullName, name, modifiers, commitSha)
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
            Pk = DeterministicHash(ScopedKey($"{FullName}{Arguments}{ReturnType}"));
        }
    }

    // Structure

    public class FileNode(string fullName, string name, string? commitSha = null) : Node(fullName, name, commitSha)
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
        public FolderNode(string fullName, string name, string? commitSha = null)
            : this(fullName, name, FolderKind.Regular, commitSha) { }

        public FolderNode(string fullName, string name, FolderKind kind, string? commitSha = null)
            : base(fullName, name, commitSha)
        {
            Kind = kind;
            SetPrimaryKey();
        }

        public override string Label { get; } = "Folder";

        public FolderKind Kind { get; }

        // Kind is part of the PK so two folders at the same path with different kinds
        // (e.g. a Regular folder created by the file chain vs. a Submodule mount-point
        // node) are distinct nodes in the graph rather than colliding on MERGE. The commit
        // sha (via ScopedKey) also participates, so a folder present at two source commits
        // becomes two distinct nodes rather than the later scan clobbering the earlier one.
        protected override void SetPrimaryKey()
        {
            Pk = DeterministicHash(ScopedKey($"{FullName}|{Kind}"));
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

    public class SolutionNode(string name, bool buildFailed = false, string? commitSha = null)
        : Node(name, name, commitSha)
    {
        public override string Label => "Solution";

        /// <summary>True when the solution file could not be parsed/loaded — e.g. it references
        /// a project type MSBuild no longer supports (a legacy .vcproj) — so its membership could
        /// not be analyzed. buildFailed is not part of the pk, so a flagged node MERGEs onto the
        /// same Solution as an unflagged one of the same name (and commit).</summary>
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

    public class ProjectNode(string fullName, string name, string[]? targetFrameworks = null, bool exists = true, bool buildFailed = false, string? commitSha = null)
        : Node(fullName, name, commitSha)
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
            Pk = DeterministicHash(ScopedKey($"{FullName}{Version}"));
        }
    }

    /// <summary>
    /// A git commit that a set of code/structure nodes was analyzed from. Its identity is the sha;
    /// versioned nodes point at it via FROM_COMMIT, and a repository points at the commit it pins a
    /// submodule to via PINS. Carries the metadata needed to identify and date the version.
    /// </summary>
    public class CommitNode : Node
    {
        public CommitNode(
            string sha,
            string repo,
            string authoredDate,
            string committedDate,
            string author,
            string subject)
            : base(sha, sha)
        {
            Sha = sha;
            Repo = repo;
            AuthoredDate = authoredDate;
            CommittedDate = committedDate;
            Author = author;
            Subject = subject;
        }

        public override string Label { get; } = "Commit";

        public string Sha { get; }

        public string Repo { get; }

        public string AuthoredDate { get; }

        public string CommittedDate { get; }

        public string Author { get; }

        public string Subject { get; }

        public override string Set(string node)
            => $"{base.Set(node)}, {node}.sha = \"{Sha}\", {node}.repo = \"{EscapeForCypher(Repo)}\""
             + $", {node}.authoredDate = \"{AuthoredDate}\", {node}.committedDate = \"{CommittedDate}\""
             + $", {node}.author = \"{EscapeForCypher(Author)}\", {node}.subject = \"{EscapeForCypher(Subject)}\"";

        public override IDictionary<string, object> Properties()
        {
            var properties = base.Properties();
            properties["sha"] = Sha;
            properties["repo"] = Repo;
            properties["authoredDate"] = AuthoredDate;
            properties["committedDate"] = CommittedDate;
            properties["author"] = Author;
            properties["subject"] = Subject;
            return properties;
        }
    }
}