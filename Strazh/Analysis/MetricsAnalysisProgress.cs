using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// An <see cref="IAnalysisProgress"/> that records per-project, per-stage timings so a run can
    /// be analyzed after the fact to find where the time actually goes (build vs. queue wait vs.
    /// the sequential Load stage vs. analyze/group/insert). It draws no UI; it composes alongside
    /// the console progress via <see cref="CompositeAnalysisProgress"/>.
    ///
    /// On completion it writes a machine-readable JSONL report — one object per project with the
    /// milliseconds spent in each stage — to <c>metricsPath</c>, and prints a short summary (total
    /// wall clock, aggregate time per stage, slowest projects) so improvement targets are obvious.
    ///
    /// Durations come from the gaps between consecutive stage-transition events, so the "Loading"
    /// figure reflects real time in <c>AddToWorkspace</c> only because the pipeline raises a
    /// dedicated load-start event — otherwise queue wait and load time would be conflated.
    ///
    /// A project's wall clock is split into the time it spent being worked on and the time it spent
    /// parked (see <see cref="NonWorkingStages"/>); rankings use the former, since the latter says more
    /// about the pipeline's shape than about the project.
    /// </summary>
    public sealed class MetricsAnalysisProgress : IAnalysisProgress
    {
        private sealed class Timeline
        {
            public string Name = "";
            public string Outcome = "in-progress";
            public int Triples;
            public readonly List<(string Stage, DateTime At)> Events = new();
        }

        private readonly string _metricsPath;
        private readonly object _lock = new();
        private readonly Dictionary<string, Timeline> _timelines =
            new(StringComparer.OrdinalIgnoreCase);
        private DateTime _runStart;
        private DateTime _runEnd;

        public MetricsAnalysisProgress(string metricsPath) => _metricsPath = metricsPath;

        public async Task WrapAsync(int totalProjects, Func<Task> action)
        {
            _runStart = DateTime.UtcNow;
            try
            {
                await action();
            }
            finally
            {
                _runEnd = DateTime.UtcNow;
                // Write whatever was collected even if the run threw, so a failed run is still
                // analyzable. A reporting failure must never mask the original error.
                try { WriteReport(); }
                catch (Exception ex) { Console.WriteLine($"Failed to write metrics report: {ex.Message}"); }
            }
        }

        public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit, BuildStageLabel buildLabel)
            => Record(projectFilePath, projectName, isCacheHit ? "Cached" : buildLabel.ToString());

        // Build finished; the project is now queued for the sequential Load stage.
        public void OnBuildCompleted(string projectFilePath)
            => Record(projectFilePath, null, "Waiting");

        public void OnStageChanged(string projectFilePath, string projectName, string stage)
            => Record(projectFilePath, projectName, stage);

        public void OnProjectCompleted(string projectFilePath, int tripleCount)
            => Finish(projectFilePath, null, "completed", tripleCount, "Completed");

        public void OnProjectSkipped(string projectFilePath, string filename, string reason)
            => Finish(projectFilePath, filename, "skipped", triples: 0, "Skipped");

        public void OnProjectDeferred(string projectFilePath, string filename, string reason)
            => Record(projectFilePath, filename, "deferred");

        // Informational only; not a timing stage, so it does not affect the metrics timeline.
        public void OnProjectWarning(string projectFilePath, string filename, string reason) { }

        public void OnProjectRecordedFromFallback(string projectFilePath, string filename, int tripleCount, bool buildFailed)
            => Finish(projectFilePath, filename, buildFailed ? "recorded (build failed)" : "recorded", tripleCount, "Recorded");

        public void OnGroupingError(string projectName, IReadOnlyList<Triple> triples) { }

        private void Record(string path, string? name, string stage)
        {
            lock (_lock)
            {
                var timeline = Get(path, name);
                timeline.Events.Add((stage, DateTime.UtcNow));
            }
        }

        private void Finish(string path, string? name, string outcome, int triples, string terminalStage)
        {
            lock (_lock)
            {
                var timeline = Get(path, name);
                timeline.Events.Add((terminalStage, DateTime.UtcNow));
                timeline.Outcome = outcome;
                timeline.Triples = triples;
            }
        }

        private Timeline Get(string path, string? name)
        {
            if (!_timelines.TryGetValue(path, out var timeline))
            {
                timeline = new Timeline { Name = name ?? Path.GetFileNameWithoutExtension(path) };
                _timelines[path] = timeline;
            }
            else if (!string.IsNullOrEmpty(name))
            {
                timeline.Name = name;
            }
            return timeline;
        }

        // Sums the time spent in each stage for one project. Each event's duration is the gap
        // until the next event; the terminal event closes the final working span and has no
        // duration of its own. Stages that occur more than once (e.g. a Building pass and a
        // PostBuild pass) are summed under their label.
        private static Dictionary<string, double> StageMillis(Timeline timeline)
        {
            var ordered = timeline.Events.OrderBy(e => e.At).ToList();
            var spans = new Dictionary<string, double>();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var ms = (ordered[i + 1].At - ordered[i].At).TotalMilliseconds;
                spans[ordered[i].Stage] = spans.GetValueOrDefault(ordered[i].Stage) + ms;
            }
            return spans;
        }

        /// <summary>
        /// Stages during which nothing is being done to a project — it is only parked, waiting for a
        /// later stage to reach it. Both spans can run far longer than any real work:
        /// <list type="bullet">
        /// <item><description><c>Waiting</c> — the Load stage is sequential and cannot start until every
        /// build has finished, so a project that built early waits for all the others.</description></item>
        /// <item><description><c>deferred</c> — a project the build pipeline dropped is parked until the
        /// fallback pass records it, which happens after the whole stream has drained.</description></item>
        /// </list>
        /// Counting either as time spent on the project would rank the pipeline's shape instead of the
        /// projects, hiding the ones that are genuinely slow.
        /// </summary>
        private static readonly string[] NonWorkingStages = ["Waiting", "deferred"];

        private static bool IsWorking(string stage) => !NonWorkingStages.Contains(stage);

        private static double WorkingMillis(Dictionary<string, double> spans) =>
            spans.Where(s => IsWorking(s.Key)).Sum(s => s.Value);

        private static double WaitingMillis(Dictionary<string, double> spans) =>
            spans.Where(s => !IsWorking(s.Key)).Sum(s => s.Value);

        private void WriteReport()
        {
            List<Row> rows;
            lock (_lock)
            {
                rows = _timelines
                    .Select(kv =>
                    {
                        var events = kv.Value.Events;
                        var total = events.Count > 1
                            ? (events.Max(e => e.At) - events.Min(e => e.At)).TotalMilliseconds
                            : 0.0;
                        var spans = StageMillis(kv.Value);
                        return new Row(kv.Key, kv.Value, spans, total, WorkingMillis(spans), WaitingMillis(spans));
                    })
                    .ToList();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_metricsPath)!);
            using (var writer = new StreamWriter(_metricsPath, append: false))
            {
                foreach (var row in rows.OrderByDescending(r => r.WorkingMs))
                {
                    var record = new
                    {
                        project = row.Path,
                        name = row.Timeline.Name,
                        outcome = row.Timeline.Outcome,
                        triples = row.Timeline.Triples,
                        totalMs = Math.Round(row.TotalMs, 1),
                        workingMs = Math.Round(row.WorkingMs, 1),
                        waitingMs = Math.Round(row.WaitingMs, 1),
                        stagesMs = row.Spans.ToDictionary(s => s.Key, s => Math.Round(s.Value, 1)),
                    };
                    writer.WriteLine(JsonSerializer.Serialize(record));
                }
            }

            PrintSummary(rows);
        }

        // One project's timings: the wall clock from its first to its last event, split into the time
        // it spent being worked on and the time it spent queued.
        private readonly record struct Row(
            string Path,
            Timeline Timeline,
            Dictionary<string, double> Spans,
            double TotalMs,
            double WorkingMs,
            double WaitingMs);

        private void PrintSummary(List<Row> rows)
        {
            var wall = (_runEnd - _runStart).TotalSeconds;
            Console.WriteLine();
            Console.WriteLine($"Analysis metrics ({rows.Count} project/s, {wall:F1}s wall clock) — {_metricsPath}");

            // Which stage dominates across the whole run — the first place to look for wins.
            var byStage = new Dictionary<string, double>();
            foreach (var row in rows)
            {
                foreach (var (stage, ms) in row.Spans)
                {
                    byStage[stage] = byStage.GetValueOrDefault(stage) + ms;
                }
            }
            Console.WriteLine("  Total time per stage (summed across projects):");
            foreach (var (stage, ms) in byStage.OrderByDescending(s => s.Value))
            {
                Console.WriteLine($"    {stage,-12} {ms / 1000.0,8:F1}s");
            }

            // Ranked by working time, not wall clock: a project can sit in the Load queue — or parked
            // after being deferred — for longer than any of its real work took, and including that would
            // rank the pipeline's shape rather than the projects. The parked time is still shown per
            // project so it is not lost.
            Console.WriteLine("  Slowest projects (by time spent working, excluding queued/deferred time):");
            foreach (var row in rows.OrderByDescending(r => r.WorkingMs).Take(10))
            {
                var breakdown = string.Join(", ", row.Spans
                    .Where(s => IsWorking(s.Key))
                    .OrderByDescending(s => s.Value)
                    .Take(3)
                    .Select(s => $"{s.Key} {s.Value / 1000.0:F1}s"));
                Console.WriteLine(
                    $"    {row.WorkingMs / 1000.0,8:F1}s  {row.Timeline.Name}  [{breakdown}]"
                    + $" (+{row.WaitingMs / 1000.0:F1}s queued/deferred)");
            }
        }
    }
}
