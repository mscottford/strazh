using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Implements <see cref="IAnalysisProgress"/> using Spectre.Console's Progress display.
    /// A single <see cref="ProgressTask"/> holds a multi-line description that is rebuilt
    /// every 80 ms by a ticker, always showing the most-recently-updated projects at the top
    /// and capping the panel height to leave room for completed-project lines in the scrollback.
    /// Completed and skipped lines are written via <see cref="AnsiConsole.MarkupLine"/>, which
    /// Progress's render hook intercepts, clears the panel, writes the line to the terminal
    /// scrollback, and re-renders the panel below — leaving the terminal state clean.
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
                .Columns(new TaskDescriptionColumn { Alignment = Justify.Left })
                .StartAsync(async ctx =>
                {
                    var panelTask = ctx.AddTask(BuildDescription());
                    using var cts = new CancellationTokenSource();
                    var ticker = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            await Task.Delay(80).ConfigureAwait(false);
                            Interlocked.Increment(ref _tick);
                            panelTask.Description = BuildDescription();
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

        private string BuildDescription()
        {
            var frame = SpinnerFrames[Math.Abs(_tick) % SpinnerFrames.Length];
            // Reserve rows for completed-project lines so they remain visible above the panel.
            var maxContentRows = Math.Max(1, AnsiConsole.Console.Profile.Height - 5);
            var entries = _active.Values
                .OrderByDescending(e => e.LastChanged)
                .Take(maxContentRows)
                .ToList();
            var hidden = Math.Max(0, _active.Count - entries.Count);
            var completed = Volatile.Read(ref _completed);
            var remaining = Math.Max(0, _total - completed);

            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                var elapsed = DateTime.UtcNow - entry.StartTime;
                sb.Append($"[green]{frame}[/] {entry.Stage,-9} {Markup.Escape(entry.Name)}  [dim]{FormatElapsed(elapsed)}[/]\n");
            }
            if (hidden > 0)
            {
                sb.Append($"[dim]  … {hidden} more running[/]\n");
            }
            sb.Append($"[dim]{completed} done · {remaining} remaining[/]");
            return sb.ToString();
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
    }
}
