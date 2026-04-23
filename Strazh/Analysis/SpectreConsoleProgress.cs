using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Implements <see cref="IAnalysisProgress"/> using Spectre.Console's Progress display.
    /// Active tasks are sorted by most-recently-updated so that any task receiving a stage
    /// change floats to the top of the visible area, even when more projects are in flight
    /// than the terminal can display at once.
    /// Completed and skipped lines are written via <see cref="AnsiConsole.MarkupLine"/>;
    /// Progress's DefaultExclusivityMode render hook intercepts the call, clears the live area,
    /// writes the line to the terminal scrollback, and re-renders below.
    /// All methods except <see cref="WrapAsync"/> may be called concurrently from multiple tasks.
    /// </summary>
    public sealed class SpectreConsoleProgress : IAnalysisProgress
    {
        private record struct TaskEntry(string Name, string Stage, DateTime StartTime, DateTime LastChanged);

        private readonly ConcurrentDictionary<string, TaskEntry> _active =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _names =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, BuildStageLabel> _buildLabels =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _startTimes =
            new(StringComparer.OrdinalIgnoreCase);
        private int _tick;
        private int _total;
        private int _completed;

        private static readonly string[] SpinnerFrames =
            ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

        public async Task WrapAsync(int totalProjects, Func<Task> action)
        {
            _total = totalProjects;
            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new PanelColumn(this))
                .StartAsync(async ctx =>
                {
                    ctx.AddTask(".");
                    using var cts = new CancellationTokenSource();
                    var ticker = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            await Task.Delay(80).ConfigureAwait(false);
                            Interlocked.Increment(ref _tick);
                            StripPadding(ctx);
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

        public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit, BuildStageLabel buildLabel)
        {
            _names[projectFilePath] = projectName;
            _buildLabels[projectFilePath] = buildLabel;
            var now = DateTime.UtcNow;
            _startTimes[projectFilePath] = now;
            _active[projectFilePath] = new TaskEntry(projectName, isCacheHit ? "Cached" : buildLabel.ToString(), now, now);
        }

        public void OnBuildCompleted(string projectFilePath)
        {
            // Building-pass entries are pre-work: silently remove them rather than
            // transitioning to "Loading" (they have no load or analysis step).
            if (_buildLabels.TryGetValue(projectFilePath, out var label) && label == BuildStageLabel.Building)
            {
                _active.TryRemove(projectFilePath, out _);
            }
            else
            {
                UpdateEntry(projectFilePath, "Loading");
            }
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
            AnsiConsole.MarkupLine(
                $"[green]✓[/] [bold]{Markup.Escape(name)}[/]  " +
                $"[dim]{FormatElapsed(elapsed)}[/]  {tripleCount} triples");
        }

        public void OnProjectSkipped(string projectFilePath, string filename, string reason)
        {
            Interlocked.Increment(ref _completed);
            _active.TryRemove(projectFilePath, out _);
            string label;
            if (reason.Contains("timed out"))
            {
                label = "[yellow]Timeout[/]";
            }
            else if (reason.Contains("could not be read"))
            {
                label = "[yellow]Unreadable[/]";
            }
            else if (reason.Contains("failed"))
            {
                label = "[red]Failed[/]";
            }
            else
            {
                label = "[yellow]Skipped[/]";
            }
            AnsiConsole.MarkupLine($"{label} {Markup.Escape(filename)} ({Markup.Escape(reason)})");
        }

        public void OnGroupingError(string projectName, IReadOnlyList<Triple> triples)
        {
            AnsiConsole.MarkupLine($"[red]Error[/] grouping triples for {Markup.Escape(projectName)}. Dumping detail:");
            AnsiConsole.WriteLine("[");
            var first = true;
            foreach (var triple in triples)
            {
                if (!first)
                {
                    AnsiConsole.WriteLine(",");
                }
                AnsiConsole.Write(new Text($$"""  { "triple": {{ triple.ToInspection()}} }"""));
                first = false;
            }
            if (triples.Count > 0)
            {
                AnsiConsole.WriteLine("");
            }
            AnsiConsole.WriteLine("]");
        }

        private void UpdateEntry(string projectFilePath, string stage)
        {
            if (_active.TryGetValue(projectFilePath, out var entry))
            {
                _active[projectFilePath] = entry with { Stage = stage, LastChanged = DateTime.UtcNow };
            }
        }

        private IRenderable BuildRenderable()
        {
            var frame = SpinnerFrames[Math.Abs(_tick) % SpinnerFrames.Length];
            // Cap at 3/4 terminal height (rounded down), reserving 2 rows for the
            // overflow line and summary line that follow the project entries.
            var maxContentRows = Math.Max(1, (int)(AnsiConsole.Console.Profile.Height * 3.0 / 4.0) - 2);
            var entries = _active.Values
                .OrderByDescending(e => e.LastChanged)
                .Take(maxContentRows)
                .ToList();
            var hidden = Math.Max(0, _active.Count - entries.Count);
            var completed = Volatile.Read(ref _completed);
            var remaining = Math.Max(0, _total - completed);

            var rows = new List<IRenderable>(entries.Count + 2);
            foreach (var entry in entries)
            {
                var elapsed = DateTime.UtcNow - entry.StartTime;
                rows.Add(new Markup(
                    $"[green]{frame}[/] {entry.Stage,-9} {Markup.Escape(entry.Name)}  [dim]{FormatElapsed(elapsed)}[/]"));
            }
            if (hidden > 0)
            {
                rows.Add(new Markup($"[dim]  … {hidden} more running[/]"));
            }
            rows.Add(new Markup($"[dim]{completed} done · {remaining} remaining[/]"));
            return new Lines(rows);
        }

        // DefaultProgressRenderer.Update() wraps the task grid in Padder(0, 1) every frame,
        // adding one blank line above and below. We strip that padding after each tick so the
        // progress panel sits flush against the completion lines above it.
        private static readonly FieldInfo? _rendererField =
            typeof(ProgressContext).GetField("_renderer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo? _paddingProperty =
            typeof(Padder).GetProperty("Padding");

        private static void StripPadding(ProgressContext ctx)
        {
            try
            {
                var renderer = _rendererField?.GetValue(ctx);
                if (renderer == null) { return; }
                var liveField = renderer.GetType().GetField("_live", BindingFlags.NonPublic | BindingFlags.Instance);
                var live = liveField?.GetValue(renderer);
                if (live == null) { return; }
                var renderableField = live.GetType().GetField("_renderable", BindingFlags.NonPublic | BindingFlags.Instance);
                var renderable = renderableField?.GetValue(live);
                if (renderable is not Padder padder) { return; }
                _paddingProperty?.SetValue(padder, new Padding(0));
            }
            catch
            {
                // If the Spectre.Console internals change, skip silently rather than crashing.
            }
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";

        private sealed class PanelColumn : ProgressColumn
        {
            private readonly SpectreConsoleProgress _owner;
            internal PanelColumn(SpectreConsoleProgress owner) => _owner = owner;
            public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
                => _owner.BuildRenderable();
        }

        // Renders a list of IRenderable items, one per line, without a trailing LineBreak.
        // (Spectre.Console's Rows appends LineBreak after every child including the last,
        // which would produce an unwanted blank line at the bottom of the panel.)
        private sealed class Lines : IRenderable
        {
            private readonly IReadOnlyList<IRenderable> _rows;
            internal Lines(IReadOnlyList<IRenderable> rows) => _rows = rows;
            public Measurement Measure(RenderOptions options, int maxWidth) => new(0, maxWidth);
            public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
            {
                for (var i = 0; i < _rows.Count; i++)
                {
                    foreach (var seg in ((IRenderable)_rows[i]).Render(options, maxWidth))
                    {
                        yield return seg;
                    }
                    if (i < _rows.Count - 1)
                    {
                        yield return Segment.LineBreak;
                    }
                }
            }
        }
    }
}
