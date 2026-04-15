using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Implements <see cref="IAnalysisProgress"/> using Spectre.Console's Live display.
    /// Active tasks are sorted by most-recently-updated so that any task receiving a stage
    /// change floats to the top of the visible area, even when more projects are in flight
    /// than the terminal can display at once.
    /// All methods except <see cref="WrapAsync"/> may be called concurrently from multiple tasks.
    /// </summary>
    public sealed class SpectreConsoleProgress : IAnalysisProgress
    {
        private record struct TaskEntry(string Name, string Stage, DateTime StartTime, DateTime LastChanged);

        private readonly ConcurrentDictionary<string, TaskEntry> _active =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _names =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _startTimes =
            new(StringComparer.OrdinalIgnoreCase);
        // Console writes (above the live area) must all come from the ticker thread to avoid
        // interleaving with ctx.Refresh(), which causes Spectre.Console to corrupt its cursor
        // position and crash the ticker. Concurrent callers enqueue an action; the ticker
        // drains the queue before each Refresh().
        private readonly ConcurrentQueue<Action> _pendingWrites = new();
        private LiveDisplayContext? _ctx;
        private int _tick;
        private int _total;
        private int _completed;

        // Dots spinner frames — same set used by Spectre.Console's built-in SpinnerColumn.
        private static readonly string[] SpinnerFrames =
            ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

        public Task WrapAsync(int totalProjects, Func<Task> action)
        {
            _total = totalProjects;
            return AnsiConsole.Live(new ActiveTasksRenderable(this))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    _ctx = ctx;
                    using var cts = new CancellationTokenSource();
                    var ticker = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            await Task.Delay(80).ConfigureAwait(false);
                            Interlocked.Increment(ref _tick);
                            if (!cts.IsCancellationRequested)
                            {
                                while (_pendingWrites.TryDequeue(out var write))
                                {
                                    write();
                                }
                                ctx.Refresh();
                            }
                        }
                    });
                    try
                    {
                        await action();
                    }
                    finally
                    {
                        await cts.CancelAsync();
                        await ticker;
                    }
                });
        }

        public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit)
        {
            _names[projectFilePath] = projectName;
            var now = DateTime.UtcNow;
            _startTimes[projectFilePath] = now;
            _active[projectFilePath] = new TaskEntry(projectName, isCacheHit ? "Cached" : "Building", now, now);
        }

        public void OnBuildCompleted(string projectFilePath)
        {
            UpdateEntry(projectFilePath, "Loading");
        }

        public void OnStageChanged(string projectFilePath, string projectName, string stage)
        {
            UpdateEntry(projectFilePath, stage);
        }

        public void OnProjectCompleted(string projectFilePath, int tripleCount)
        {
            Interlocked.Increment(ref _completed);
            _active.TryRemove(projectFilePath, out _);
            var name = _names.TryGetValue(projectFilePath, out var n) ? n : projectFilePath;
            var elapsed = DateTime.UtcNow - (_startTimes.TryGetValue(projectFilePath, out var st) ? st : DateTime.UtcNow);
            var markup =
                $"[green]✓[/] [bold]{Markup.Escape(name)}[/]  " +
                $"[dim]{FormatElapsed(elapsed)}[/]  {tripleCount} triples";
            _pendingWrites.Enqueue(() => AnsiConsole.MarkupLine(markup));
        }

        public void OnProjectSkipped(string projectFilePath, string filename, string reason)
        {
            Interlocked.Increment(ref _completed);
            _active.TryRemove(projectFilePath, out _);
            var markup = $"[yellow]Skipped[/] {Markup.Escape(filename)} ({Markup.Escape(reason)})";
            _pendingWrites.Enqueue(() => AnsiConsole.MarkupLine(markup));
        }

        public void OnGroupingError(string projectName, IReadOnlyList<Triple> triples)
        {
            var capturedTriples = triples.ToList();
            _pendingWrites.Enqueue(() =>
            {
                AnsiConsole.MarkupLine($"[red]Error[/] grouping triples for {Markup.Escape(projectName)}. Dumping detail:");
                AnsiConsole.WriteLine("[");
                var first = true;
                foreach (var triple in capturedTriples)
                {
                    if (!first)
                    {
                        AnsiConsole.WriteLine(",");
                    }
                    AnsiConsole.Write(new Text($$"""{ "triple": {{ triple.ToInspection()}} }"""));
                    first = false;
                }
                if (capturedTriples.Count > 0)
                {
                    AnsiConsole.WriteLine("");
                }
                AnsiConsole.WriteLine("]");
            });
        }

        private void UpdateEntry(string projectFilePath, string stage)
        {
            if (_active.TryGetValue(projectFilePath, out var entry))
            {
                _active[projectFilePath] = entry with { Stage = stage, LastChanged = DateTime.UtcNow };
            }
        }

        private string GetSpinnerFrame()
            => SpinnerFrames[Math.Abs(_tick) % SpinnerFrames.Length];

        private (int completed, int remaining) GetCounts()
        {
            var completed = Volatile.Read(ref _completed);
            return (completed, Math.Max(0, _total - completed));
        }

        private (List<TaskEntry> entries, int paddingRows) GetSortedEntries()
        {
            // Reserve 1 row for the summary line and 1 for breathing room.
            var terminalHeight = AnsiConsole.Console.Profile.Height;
            var maxContentRows = Math.Max(1, terminalHeight - 2);
            var entries = _active.Values.OrderByDescending(e => e.LastChanged).Take(maxContentRows).ToList();
            // Pad above the active entries so the summary line sits at the bottom of the terminal.
            var paddingRows = Math.Max(0, maxContentRows - entries.Count);
            return (entries, paddingRows);
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";

        /// <summary>
        /// Live-renderable that rebuilds on every <see cref="LiveDisplayContext.Refresh"/> call,
        /// always placing the most recently updated task at the top.
        /// </summary>
        private sealed class ActiveTasksRenderable : IRenderable
        {
            private readonly SpectreConsoleProgress _owner;

            public ActiveTasksRenderable(SpectreConsoleProgress owner) => _owner = owner;

            public Measurement Measure(RenderOptions options, int maxWidth) => new(0, maxWidth);

            public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
            {
                var frame = _owner.GetSpinnerFrame();
                var (entries, paddingRows) = _owner.GetSortedEntries();
                var (completed, remaining) = _owner.GetCounts();

                // Blank lines above the active-task block push it toward the bottom of the terminal.
                for (var i = 0; i < paddingRows; i++)
                {
                    yield return Segment.LineBreak;
                }

                foreach (var entry in entries)
                {
                    var elapsed = DateTime.UtcNow - entry.StartTime;
                    var line = new Markup(
                        $"[green]{frame}[/] {entry.Stage,-9} {Markup.Escape(entry.Name)}  [dim]{FormatElapsed(elapsed)}[/]");
                    foreach (var seg in ((IRenderable)line).Render(options, maxWidth))
                    {
                        yield return seg;
                    }
                    yield return Segment.LineBreak;
                }

                var summary = new Markup($"[dim]{completed} done · {remaining} remaining[/]");
                foreach (var seg in ((IRenderable)summary).Render(options, maxWidth))
                {
                    yield return seg;
                }
                yield return Segment.LineBreak;
            }
        }
    }
}
