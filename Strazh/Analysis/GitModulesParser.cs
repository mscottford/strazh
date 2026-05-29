using System.Collections.Generic;
using System.IO;

namespace Strazh.Analysis
{
    /// <summary>
    /// One submodule declaration parsed from a <c>.gitmodules</c> file. The URL is
    /// returned verbatim — relative URLs (<c>./</c>, <c>../</c>) are NOT resolved here.
    /// Resolution against the host repo's origin happens in <see cref="GitHelper"/>.
    /// </summary>
    public sealed record GitModuleEntry(string Name, string Path, string Url);

    /// <summary>
    /// Parser for git's <c>.gitmodules</c> file format. Recognizes
    /// <c>[submodule "name"]</c> sections with <c>path</c> and <c>url</c> keys; other
    /// sections, unknown keys, blank lines, and <c>#</c>/<c>;</c> comments are ignored.
    /// Tolerant of <c>key=value</c> and <c>key = value</c> spacing.
    /// </summary>
    public sealed class GitModulesParser
    {
        private readonly List<GitModuleEntry> _results = new();
        private string? _name;
        private string? _path;
        private string? _url;
        private bool _inSubmodule;

        public static IReadOnlyList<GitModuleEntry> Parse(string content)
        {
            var parser = new GitModulesParser();
            parser.ParseLines(content);
            return parser._results;
        }

        public static IReadOnlyList<GitModuleEntry> ParseFile(string path)
            => File.Exists(path) ? Parse(File.ReadAllText(path)) : [];

        private void ParseLines(string content)
        {
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }
                if (line.StartsWith('['))
                {
                    Flush();
                    const string head = "[submodule \"";
                    const string tail = "\"]";
                    if (line.StartsWith(head, System.StringComparison.Ordinal)
                        && line.EndsWith(tail, System.StringComparison.Ordinal))
                    {
                        _inSubmodule = true;
                        _name = line[head.Length..^tail.Length];
                    }
                    else
                    {
                        _inSubmodule = false;
                    }
                    continue;
                }
                if (!_inSubmodule)
                {
                    continue;
                }
                var eqIdx = line.IndexOf('=');
                if (eqIdx < 0)
                {
                    continue;
                }
                var key = line[..eqIdx].Trim();
                var value = line[(eqIdx + 1)..].Trim();
                switch (key)
                {
                    case "path":
                        _path = value;
                        break;
                    case "url":
                        _url = value;
                        break;
                }
            }
            Flush();
        }

        private void Flush()
        {
            if (_inSubmodule && _name != null && _path != null && _url != null)
            {
                _results.Add(new GitModuleEntry(_name, _path, _url));
            }
            _name = null;
            _path = null;
            _url = null;
        }
    }
}
