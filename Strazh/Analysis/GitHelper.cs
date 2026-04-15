using System;
using System.IO;

namespace Strazh.Analysis
{
    public static class GitHelper
    {
        /// <summary>
        /// Walks up from <paramref name="startPath"/> until a directory containing a
        /// <c>.git</c> subdirectory is found, then returns that directory's full path.
        /// Returns <c>null</c> when the path is not inside a git repository.
        /// </summary>
        public static string? FindGitRoot(string startPath)
        {
            var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    return dir;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        /// <summary>
        /// Returns the repository name in <c>owner/repo</c> form by reading the URL of the
        /// <c>origin</c> remote from the <c>.git/config</c> nearest to
        /// <paramref name="startPath"/>. Returns <c>null</c> when the git root or the
        /// remote URL cannot be determined.
        /// </summary>
        public static string? GetRepositoryName(string startPath)
        {
            var gitRoot = FindGitRoot(startPath);
            if (gitRoot == null)
            {
                return null;
            }

            var configPath = Path.Combine(gitRoot, ".git", "config");
            if (!File.Exists(configPath))
            {
                return null;
            }

            var lines = File.ReadAllLines(configPath);
            var inOrigin = false;
            foreach (var line in lines)
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
                if (inOrigin && trimmed.StartsWith("url = ", StringComparison.Ordinal))
                {
                    var url = trimmed["url = ".Length..].Trim();
                    return ParseRepoName(url);
                }
            }
            return null;
        }

        private static string? ParseRepoName(string url)
        {
            try
            {
                // SSH format: git@github.com:owner/repo.git
                if (url.Contains('@') && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var colonIdx = url.IndexOf(':');
                    if (colonIdx < 0)
                    {
                        return null;
                    }
                    var path = url[(colonIdx + 1)..].TrimEnd('/');
                    if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        path = path[..^4];
                    }
                    return path;
                }

                // HTTPS format: https://github.com/owner/repo.git
                var uri = new Uri(url);
                var httpPath = uri.AbsolutePath.TrimStart('/').TrimEnd('/');
                if (httpPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    httpPath = httpPath[..^4];
                }
                return httpPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
