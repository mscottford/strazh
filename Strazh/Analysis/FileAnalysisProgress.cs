using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Implements <see cref="IAnalysisProgress"/> by writing a structured, machine-readable
    /// log file that captures the exact timing and outcome of every step in the analysis
    /// pipeline. The file is useful for post-run diagnosis of failures that are not obvious
    /// from the interactive console output (e.g. whether a project was a cache hit, whether
    /// the binlog was read successfully, what order projects completed in).
    ///
    /// Log format — one entry per line:
    /// <code>
    ///   {ISO-8601-UTC} {EVENT} {key=value ...}
    /// </code>
    /// String values are double-quoted; numbers and booleans are unquoted. All methods are
    /// safe to call concurrently from multiple tasks.
    /// </summary>
    public sealed class FileAnalysisProgress : IAnalysisProgress, IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<string, DateTime> _startTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly DateTime _runStart = DateTime.UtcNow;
        private int _total;

        public FileAnalysisProgress(string logFilePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            _writer = new StreamWriter(logFilePath, append: false, System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        public async Task WrapAsync(int totalProjects, Func<Task> action)
        {
            _total = totalProjects;
            Write("RUN_STARTED", $"total={totalProjects}");
            try
            {
                await action();
            }
            finally
            {
                var elapsed = DateTime.UtcNow - _runStart;
                Write("RUN_COMPLETE", $"elapsed={FormatElapsed(elapsed)} total={_total}");
            }
        }

        public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit, string buildLabel = "Building")
        {
            var now = DateTime.UtcNow;
            _startTimes[projectFilePath] = now;
            Write("BUILD_STARTED", $"cache={isCacheHit.ToString().ToLowerInvariant()} label={Q(buildLabel)} name={Q(projectName)} path={Q(projectFilePath)}");
        }

        public void OnBuildCompleted(string projectFilePath)
        {
            var elapsed = DateTime.UtcNow - GetStart(projectFilePath);
            Write("BUILD_COMPLETE", $"elapsed={FormatElapsed(elapsed)} path={Q(projectFilePath)}");
        }

        public void OnStageChanged(string projectFilePath, string projectName, string stage)
        {
            Write("STAGE", $"stage={Q(stage)} name={Q(projectName)} path={Q(projectFilePath)}");
        }

        public void OnProjectCompleted(string projectFilePath, int tripleCount)
        {
            var elapsed = DateTime.UtcNow - GetStart(projectFilePath);
            Write("PROJECT_COMPLETE", $"triples={tripleCount} elapsed={FormatElapsed(elapsed)} path={Q(projectFilePath)}");
        }

        public void OnProjectSkipped(string projectFilePath, string filename, string reason)
        {
            var elapsed = DateTime.UtcNow - GetStart(projectFilePath);
            Write("PROJECT_SKIPPED", $"file={Q(filename)} reason={Q(reason)} elapsed={FormatElapsed(elapsed)} path={Q(projectFilePath)}");
        }

        public void OnGroupingError(string projectName, IReadOnlyList<Triple> triples)
        {
            Write("GROUPING_ERROR", $"project={Q(projectName)} triples={triples.Count}");
        }

        public void Dispose() => _writer.Dispose();

        // --- helpers ---

        private void Write(string eventName, string fields)
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {eventName} {fields}";
            lock (_lock)
            {
                _writer.WriteLine(line);
            }
        }

        private DateTime GetStart(string projectFilePath)
            => _startTimes.TryGetValue(projectFilePath, out var t) ? t : _runStart;

        // Surrounds a string value with double quotes, escaping any embedded double quotes.
        private static string Q(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

        private static string FormatElapsed(TimeSpan elapsed)
            => $"{elapsed.TotalSeconds:F3}s";
    }
}
