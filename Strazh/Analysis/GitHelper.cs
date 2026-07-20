using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// One entry in a repository's <c>.gitmodules</c> file: the path where the submodule
    /// is mounted inside the host repo, and the <c>owner/repo</c> name of the repository
    /// the submodule references (with relative URLs resolved against the host's origin).
    /// </summary>
    public sealed record Submodule(string MountPath, string RepositoryName);

    /// <summary>
    /// The HEAD commit of a checked-out git repository (or submodule): the commit sha plus the
    /// metadata used to identify and date that version in the graph. Dates are ISO-8601 strict
    /// (<c>%aI</c>/<c>%cI</c>) so they sort lexicographically.
    /// </summary>
    public sealed record CommitInfo(
        string Sha,
        string AuthoredDate,
        string CommittedDate,
        string Author,
        string AuthorEmail,
        string Subject);

    public static class GitHelper
    {
        // The HEAD commit is the same for every path inside one working tree, so memoize by git
        // root. The cache is keyed on the resolved root path (not the caller's path) and holds
        // failures (null) too, so a non-repo or unreadable root is probed at most once. Concurrent
        // because analysis resolves shas from parallel per-project tasks.
        private static readonly ConcurrentDictionary<string, CommitInfo?> CommitCache = new();

        // Resolving a symbol's commit runs per source reference (potentially millions of times), and
        // each call would otherwise walk the directory tree looking for .git. Memoize the walk by
        // directory so repeated lookups within a tree are dictionary hits.
        private static readonly ConcurrentDictionary<string, string?> GitRootCache = new();

        // Origin repo name is stable per git root and read from config on disk; cache it since it is
        // now looked up per declared file / per project when building Commit nodes.
        private static readonly ConcurrentDictionary<string, string?> RepoNameCache = new();

        /// <summary>
        /// The <see cref="CommitNode"/> for the git repository nearest to <paramref name="startPath"/>
        /// — its HEAD commit (sha + dates + author + subject) tagged with the origin <c>owner/repo</c>.
        /// Returns <c>null</c> when the path is not inside a readable git repository.
        /// </summary>
        public static CommitNode? GetCommitNode(string startPath)
        {
            var commit = GetCommit(startPath);
            if (commit == null)
            {
                return null;
            }
            return new CommitNode(
                commit.Sha,
                GetRepositoryName(startPath) ?? "",
                commit.AuthoredDate,
                commit.CommittedDate,
                commit.Author,
                commit.Subject);
        }

        /// <summary>
        /// The HEAD commit of the git repository nearest to <paramref name="startPath"/>. For a
        /// submodule checkout this is the pinned commit (its detached HEAD); for a regular clone it
        /// is the branch tip. Returns <c>null</c> when the path is not inside a git repository or the
        /// commit cannot be read. Memoized per git root.
        /// </summary>
        public static CommitInfo? GetCommit(string startPath)
        {
            var gitRoot = FindGitRootCached(startPath);
            if (gitRoot == null)
            {
                return null;
            }
            return CommitCache.GetOrAdd(gitRoot, ReadHeadCommit);
        }

        // FindGitRoot memoized by the starting directory (its result — the resolved root — is already
        // an absolute path, so it doubles as the CommitCache key).
        private static string? FindGitRootCached(string startPath)
        {
            var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
            if (dir == null)
            {
                return FindGitRoot(startPath);
            }
            return GitRootCache.GetOrAdd(Path.GetFullPath(dir), resolvedDir =>
            {
                var root = FindGitRoot(resolvedDir);
                return root == null ? null : Path.GetFullPath(root);
            });
        }

        // Field indices into the NUL-separated `git show` output. They must match the order of the
        // placeholders in CommitFormat below (%H %aI %cI %an %ae %s); ExpectedFieldCount guards that
        // every field is present before any is read.
        private const int ShaField = 0;
        private const int AuthoredDateField = 1;
        private const int CommittedDateField = 2;
        private const int AuthorField = 3;
        private const int AuthorEmailField = 4;
        private const int SubjectField = 5;
        private const int ExpectedFieldCount = 6;

        // NUL-separated (%x00) so an author name or subject line can never be mistaken for a field
        // boundary; the subject (%s) is a single line and comes last.
        private const string CommitFormat = "--format=%H%x00%aI%x00%cI%x00%an%x00%ae%x00%s";

        // Reads HEAD's commit metadata via a single `git show`. Shelling out lets git resolve HEAD
        // through loose refs, packed-refs, and detached submodule heads uniformly — no plumbing-file
        // parsing here.
        private static CommitInfo? ReadHeadCommit(string gitRoot)
        {
            var output = RunGit(gitRoot, "show", "-s", "--no-patch", CommitFormat, "HEAD");
            if (output == null)
            {
                return null;
            }
            var parts = output.Split('\0');
            if (parts.Length < ExpectedFieldCount)
            {
                return null;
            }
            var sha = parts[ShaField].Trim();
            return sha.Length == 0
                ? null
                : new CommitInfo(
                    Sha: sha,
                    AuthoredDate: parts[AuthoredDateField].Trim(),
                    CommittedDate: parts[CommittedDateField].Trim(),
                    Author: parts[AuthorField],
                    AuthorEmail: parts[AuthorEmailField],
                    Subject: parts[SubjectField].Trim());
        }

        // Runs `git` in <paramref name="workingDir"/> and returns stdout on success, or null on a
        // non-zero exit or if git is unavailable. stderr is drained (and discarded) so the child
        // never blocks on a full pipe; a missing/failed git is a soft failure — the caller degrades
        // to an unversioned node rather than aborting the analysis.
        private static string? RunGit(string workingDir, params string[] args)
        {
            try
            {
                var startInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }
                var stdout = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 ? stdout : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Walks up from <paramref name="startPath"/> until a directory containing a
        /// <c>.git</c> entry (a directory for a regular repo, a file for a submodule
        /// checkout) is found, then returns that directory's full path.
        /// Returns <c>null</c> when the path is not inside a git repository.
        /// </summary>
        public static string? FindGitRoot(string startPath)
        {
            var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return dir;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        /// <summary>
        /// Returns the repository name in <c>owner/repo</c> form by reading the URL of the
        /// <c>origin</c> remote from the git config nearest to <paramref name="startPath"/>.
        /// Follows <c>.git</c> files used by submodule checkouts to their real git directory.
        /// Returns <c>null</c> when the git root or the remote URL cannot be determined.
        /// </summary>
        public static string? GetRepositoryName(string startPath)
        {
            var gitRoot = FindGitRootCached(startPath);
            if (gitRoot == null)
            {
                return null;
            }
            return RepoNameCache.GetOrAdd(gitRoot, root =>
            {
                var url = ReadOriginUrl(root);
                return url == null ? null : ParseRepoName(url);
            });
        }

        /// <summary>
        /// Reads <c>.gitmodules</c> at the git root nearest to <paramref name="startPath"/>
        /// and returns one entry per declared submodule. Relative URLs (<c>./</c> /
        /// <c>../</c>) are resolved against the host's <c>origin</c> URL using git's own
        /// rules; absolute URLs are parsed directly. Entries whose URL cannot be resolved
        /// to an <c>owner/repo</c> form are skipped. Returns an empty list when there is
        /// no <c>.gitmodules</c> file.
        /// </summary>
        public static IReadOnlyList<Submodule> GetSubmodules(string startPath)
        {
            var gitRoot = FindGitRoot(startPath);
            if (gitRoot == null)
            {
                return [];
            }

            var entries = GitModulesParser.ParseFile(Path.Combine(gitRoot, ".gitmodules"));
            if (entries.Count == 0)
            {
                return [];
            }

            var parentUrl = ReadOriginUrl(gitRoot);
            var results = new List<Submodule>();
            foreach (var entry in entries)
            {
                var resolved = ResolveSubmoduleUrl(entry.Url, parentUrl);
                if (resolved == null)
                {
                    continue;
                }
                var repoName = ParseRepoName(resolved);
                if (repoName == null)
                {
                    continue;
                }
                results.Add(new Submodule(entry.Path, repoName));
            }
            return results;
        }

        // Resolves the directory holding the shared git metadata (config in particular) for
        // the repository rooted at gitRoot. For a regular repo this is <gitRoot>/.git. For a
        // submodule checkout, .git is a file containing "gitdir: <relative-path>" pointing
        // into the parent's .git/modules/<name>/ directory. For a linked worktree, .git is a
        // file pointing at a per-worktree gitdir that holds only HEAD/index and a "commondir"
        // pointer back to the shared .git directory — that shared directory is followed so
        // config (and thus origin) is found.
        private static string? ResolveGitDir(string gitRoot)
        {
            var gitPath = Path.Combine(gitRoot, ".git");
            string gitDir;
            if (Directory.Exists(gitPath))
            {
                gitDir = gitPath;
            }
            else if (File.Exists(gitPath))
            {
                var content = File.ReadAllText(gitPath).Trim();
                const string prefix = "gitdir:";
                if (!content.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
                var target = content[prefix.Length..].Trim();
                if (!Path.IsPathRooted(target))
                {
                    target = Path.GetFullPath(Path.Combine(gitRoot, target));
                }
                if (!Directory.Exists(target))
                {
                    return null;
                }
                gitDir = target;
            }
            else
            {
                return null;
            }

            // A linked worktree's gitdir shares config/objects/refs with the main repo via a
            // "commondir" pointer (relative to the gitdir when not absolute). Follow it so
            // config lookups resolve to the shared directory rather than the worktree's own.
            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
            {
                var commonDir = File.ReadAllText(commonDirFile).Trim();
                if (!Path.IsPathRooted(commonDir))
                {
                    commonDir = Path.GetFullPath(Path.Combine(gitDir, commonDir));
                }
                if (Directory.Exists(commonDir))
                {
                    gitDir = commonDir;
                }
            }

            return gitDir;
        }

        private static string? ReadOriginUrl(string gitRoot)
        {
            var gitDir = ResolveGitDir(gitRoot);
            if (gitDir == null)
            {
                return null;
            }
            var configPath = Path.Combine(gitDir, "config");
            if (!File.Exists(configPath))
            {
                return null;
            }

            var inOrigin = false;
            foreach (var line in File.ReadAllLines(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed == "[remote \"origin\"]")
                {
                    inOrigin = true;
                    continue;
                }
                if (inOrigin && trimmed.StartsWith('['))
                {
                    break;
                }
                if (!inOrigin)
                {
                    continue;
                }
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0)
                {
                    continue;
                }
                if (trimmed[..eqIdx].Trim() == "url")
                {
                    return trimmed[(eqIdx + 1)..].Trim();
                }
            }
            return null;
        }

        // Resolves a relative submodule URL (./foo, ../foo, ../../foo, …) against the
        // host repo's origin URL using System.Uri.
        //
        // Trailing-slash trick: appending "/" to the parent URL makes Uri treat the
        // parent's last segment (the repo name) as a directory, so ".." goes up from
        // the repo level — matching git's submodule URL resolution rule, where the
        // repo-name segment is itself stripped by the first "..".
        //
        // scp-style SSH URLs (git@host:path) are not URIs proper; Uri doesn't grok them.
        // We round-trip them through the equivalent ssh:// form just for resolution.
        private static string? ResolveSubmoduleUrl(string submoduleUrl, string? parentUrl)
        {
            if (Uri.TryCreate(submoduleUrl, UriKind.Absolute, out _) || IsScpLikeSsh(submoduleUrl))
            {
                return submoduleUrl;
            }
            if (parentUrl == null)
            {
                return null;
            }

            var parentAsUri = AsAbsoluteUri(parentUrl);
            if (parentAsUri == null)
            {
                return null;
            }

            var baseWithTrailingSlash = new Uri(parentAsUri.AbsoluteUri + "/");
            if (!Uri.TryCreate(baseWithTrailingSlash, submoduleUrl, out var resolved))
            {
                return null;
            }

            return IsScpLikeSsh(parentUrl) ? ToScpForm(resolved) : resolved.AbsoluteUri;
        }

        // Detects scp-like SSH syntax: user@host:path with no "://" — git@github.com:Org/Repo.git.
        // Distinguishing rule: a ':' appears before any '/', and the string has no scheme delimiter.
        private static bool IsScpLikeSsh(string url)
        {
            if (url.Contains("://", StringComparison.Ordinal))
            {
                return false;
            }
            var colon = url.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }
            var firstSlash = url.IndexOf('/');
            return firstSlash < 0 || firstSlash > colon;
        }

        private static Uri? AsAbsoluteUri(string url)
        {
            if (IsScpLikeSsh(url))
            {
                var colon = url.IndexOf(':');
                var hostPart = url[..colon];
                var path = url[(colon + 1)..];
                return Uri.TryCreate($"ssh://{hostPart}/{path}", UriKind.Absolute, out var u) ? u : null;
            }
            return Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs : null;
        }

        // Renders an ssh:// Uri back into scp-like form: ssh://git@github.com/Org/Repo.git
        // → git@github.com:Org/Repo.git.
        private static string ToScpForm(Uri sshUri)
        {
            var userInfo = string.IsNullOrEmpty(sshUri.UserInfo) ? "" : sshUri.UserInfo + "@";
            var path = sshUri.AbsolutePath.TrimStart('/');
            return $"{userInfo}{sshUri.Host}:{path}";
        }

        // Extracts owner/repo from a git remote URL. Uses System.Uri for HTTP(S)/SSH URIs;
        // scp-like SSH is handled by splitting on the ':' separator. The trailing ".git"
        // suffix is stripped if present.
        private static string? ParseRepoName(string url)
        {
            try
            {
                string path;
                if (IsScpLikeSsh(url))
                {
                    var colon = url.IndexOf(':');
                    path = url[(colon + 1)..];
                }
                else
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        return null;
                    }
                    path = uri.AbsolutePath;
                }
                path = path.Trim('/');
                const string gitSuffix = ".git";
                if (path.EndsWith(gitSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^gitSuffix.Length];
                }
                return path.Length == 0 ? null : path;
            }
            catch
            {
                return null;
            }
        }
    }
}
