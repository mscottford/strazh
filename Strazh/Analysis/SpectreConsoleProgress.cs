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
    /// Implements <see cref="IAnalysisProgress"/> using Spectre.Console's Progress display.
    /// A single <see cref="ProgressTask"/> is rendered by a custom <see cref="PanelColumn"/>
    /// that rebuilds a multi-line <see cref="Rows"/> renderable on every frame, always showing
    /// the most-recently-updated projects at the top and capping height to leave room for
    /// completed-project lines in the terminal scrollback.
    /// Completed and skipped lines are written via <see cref="AnsiConsole.MarkupLine"/>, which
    /// Progress's render hook intercepts, clears the panel, writes the line to the scrollback,
    /// and re-renders the panel below — leaving the terminal state clean.
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
        private DateTime _runStart = DateTime.UtcNow;
        // Active project rows shown at once. The panel is rendered at a constant height (this many
        // rows plus a status line and a summary line), padded with blanks, so Spectre's live-region
        // clear accounting stays stable — see BuildRenderable.
        private const int MaxActiveRows = 10;

        private static readonly string[] SpinnerFrames =
            ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

        public async Task WrapAsync(int totalProjects, Func<Task> action)
        {
            _total = totalProjects;
            _runStart = DateTime.UtcNow;
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
                // The build finished, but the sequential Load stage may not reach this project
                // for a while (it can't start until every build completes). Mark it "Waiting"
                // rather than "Loading" so a queued project is not shown as actively loading —
                // OnLoadStarted flips it to "Loading" when AddToWorkspace actually begins.
                UpdateEntry(projectFilePath, "Waiting");
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

        public void OnProjectDeferred(string projectFilePath, string filename, string reason)
        {
            // Not terminal: keep the project visible in the live panel as pending work (it is
            // recorded from fallback data later) rather than removing it or counting it done.
            var name = _names.TryGetValue(projectFilePath, out var n) ? n : filename;
            var now = DateTime.UtcNow;
            var start = _startTimes.TryGetValue(projectFilePath, out var st) ? st : now;
            _active[projectFilePath] = new TaskEntry(name, "deferred", start, now);
        }

        public void OnProjectWarning(string projectFilePath, string filename, string reason)
        {
            // Informational only: do not touch the completed count or the active panel — the
            // project's normal lifecycle events still follow. Printed above the live display.
            var name = _names.TryGetValue(projectFilePath, out var n) ? n : filename;
            AnsiConsole.MarkupLine($"[yellow]![/] [bold]{Markup.Escape(name)}[/] ({Markup.Escape(reason)})");
        }

        public void OnProjectRecordedFromFallback(string projectFilePath, string filename, int tripleCount, bool buildFailed)
        {
            Interlocked.Increment(ref _completed);
            _active.TryRemove(projectFilePath, out _);
            var name = _names.TryGetValue(projectFilePath, out var n) ? n : filename;
            var note = buildFailed ? "build failed" : "not loaded into workspace";
            AnsiConsole.MarkupLine(
                $"[green]✓[/] [bold]{Markup.Escape(name)}[/] [dim]recorded[/]  " +
                $"[dim]({tripleCount} triples, {note})[/]");
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

        // Renders the panel at a CONSTANT height every frame: exactly MaxActiveRows active-or-blank
        // project rows, then a status line, then the summary line. A variable height desyncs
        // Spectre's live-region clear accounting — while the terminal scrolls, height changes leave
        // stale rows and blank padding that AutoClear cannot reclaim on teardown. A fixed height
        // keeps the accounting stable so AutoClear clears the whole region cleanly. The body is
        // capped well below the viewport so completed/skipped lines stay visible in the scrollback
        // above it.
        private IRenderable BuildRenderable()
        {
            var frame = SpinnerFrames[Math.Abs(_tick) % SpinnerFrames.Length];
            var now = DateTime.UtcNow;

            // "Waiting" projects have finished building and are queued for the sequential Load
            // stage — they are idle, not working. Collapsing them into a single count keeps the
            // genuinely-active projects (building/loading/analyzing/…) visible and moving, instead
            // of dozens of identical "Waiting" lines that look frozen.
            var all = _active.Values.ToList();
            var waiting = all.Count(e => e.Stage == "Waiting");
            // Fixed body height, but never taller than the viewport allows (leaving room above for
            // the completed-project scrollback and this panel's own status + summary lines).
            var panelBody = Math.Max(1, Math.Min(AnsiConsole.Console.Profile.Height - 8, MaxActiveRows));
            var active = all
                .Where(e => e.Stage != "Waiting")
                .OrderByDescending(e => e.LastChanged)
                .Take(panelBody)
                .ToList();
            var hiddenActive = Math.Max(0, (all.Count - waiting) - active.Count);
            var completed = Volatile.Read(ref _completed);
            var remaining = Math.Max(0, _total - completed);

            var rows = new List<IRenderable>(panelBody + 2);
            for (var row = 0; row < panelBody; row++)
            {
                if (row < active.Count)
                {
                    // Time in the current stage makes progress legible: a project that just moved to
                    // a stage shows a small, growing number rather than its whole-run elapsed.
                    var entry = active[row];
                    var stageElapsed = now - entry.LastChanged;
                    rows.Add(new Markup(
                        $"[green]{frame}[/] {entry.Stage,-9} {Markup.Escape(entry.Name)}  [dim]{FormatElapsed(stageElapsed)}[/]"));
                }
                else
                {
                    rows.Add(new Markup(" ")); // blank padding row — keeps the panel height constant
                }
            }

            // Status line (always present, blank when idle): overflow-active and waiting counts.
            var statusParts = new List<string>(2);
            if (hiddenActive > 0)
            {
                statusParts.Add($"… {hiddenActive} more active");
            }
            if (waiting > 0)
            {
                statusParts.Add($"{waiting} built, waiting to load");
            }
            rows.Add(new Markup(statusParts.Count > 0 ? $"[dim]  {string.Join(" · ", statusParts)}[/]" : " "));

            // Summary line (always present).
            var pct = _total > 0 ? (int)(100.0 * completed / _total) : 0;
            rows.Add(new Markup(
                $"[dim]{completed}/{_total} done ({pct}%) · {remaining} remaining · {FormatElapsed(now - _runStart)} elapsed[/]"));
            return new Rows(rows);
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";

        /// <summary>
        /// Single-column renderer for the panel task. Returns a multi-line
        /// <see cref="Rows"/> renderable so each active project appears on its own line.
        /// </summary>
        private sealed class PanelColumn : ProgressColumn
        {
            private readonly SpectreConsoleProgress _owner;
            internal PanelColumn(SpectreConsoleProgress owner) => _owner = owner;
            public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
                => _owner.BuildRenderable();
        }
    }
}
