using System;
using System.Collections.Generic;
using System.IO;

namespace Strazh.Analysis
{
    /// <summary>
    /// One entry in a repository's <c>.gitmodules</c> file: the path where the submodule
    /// is mounted inside the host repo, and the <c>owner/repo</c> name of the repository
    /// the submodule references (with relative URLs resolved against the host's origin).
    /// </summary>
    public sealed record Submodule(string MountPath, string RepositoryName);

    public static class GitHelper
    {
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
            var gitRoot = FindGitRoot(startPath);
            if (gitRoot == null)
            {
                return null;
            }
            var url = ReadOriginUrl(gitRoot);
            return url == null ? null : ParseRepoName(url);
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

        // Resolves the directory holding the real git metadata (config, HEAD, etc.) for the
        // repository rooted at gitRoot. For a regular repo this is <gitRoot>/.git. For a
        // submodule checkout, .git is a file containing "gitdir: <relative-path>" pointing
        // into the parent's .git/modules/<name>/ directory.
        private static string? ResolveGitDir(string gitRoot)
        {
            var gitPath = Path.Combine(gitRoot, ".git");
            if (Directory.Exists(gitPath))
            {
                return gitPath;
            }
            if (!File.Exists(gitPath))
            {
                return null;
            }

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
            return Directory.Exists(target) ? target : null;
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
