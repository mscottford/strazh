using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Implements <see cref="IAnalysisProgress"/> using Spectre.Console's Progress display.
    /// Each active project is a <see cref="ProgressTask"/>; completed and skipped lines are
    /// written via <see cref="AnsiConsole.MarkupLine"/> which Progress's render hook intercepts,
    /// clears the spinner area, writes the line to the terminal scrollback, and re-renders the
    /// spinners below — leaving the terminal state clean.
    /// All methods except <see cref="WrapAsync"/> may be called concurrently from multiple tasks.
    /// </summary>
    public sealed class SpectreConsoleProgress : IAnalysisProgress
    {
        private readonly ConcurrentDictionary<string, string> _names =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, BuildStageLabel> _buildLabels =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _startTimes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ProgressTask> _tasks =
            new(StringComparer.OrdinalIgnoreCase);
        private volatile ProgressContext? _ctx;
        private int _completed;
        private int _total;

        public async Task WrapAsync(int totalProjects, Func<Task> action)
        {
            _total = totalProjects;
            await AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns(
                    new SpinnerColumn(),
                    new TaskDescriptionColumn { Alignment = Justify.Left }
                )
                .StartAsync(async ctx =>
                {
                    _ctx = ctx;
                    await action();
                    _ctx = null;
                });
        }

        public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit, BuildStageLabel buildLabel)
        {
            _names[projectFilePath] = projectName;
            _buildLabels[projectFilePath] = buildLabel;
            _startTimes[projectFilePath] = DateTime.UtcNow;
            if (_ctx is { } ctx)
            {
                var stage = isCacheHit ? "Cached" : buildLabel.ToString();
                var task = ctx.AddTask($"{stage,-9} {Markup.Escape(projectName)}");
                _tasks[projectFilePath] = task;
            }
        }

        public void OnBuildCompleted(string projectFilePath)
        {
            if (!_tasks.TryGetValue(projectFilePath, out var task))
            {
                return;
            }
            // Building-pass entries are pre-work: silently finish them rather than
            // transitioning to "Loading" (they have no load or analysis step).
            if (_buildLabels.TryGetValue(projectFilePath, out var label) && label == BuildStageLabel.Building)
            {
                task.StopTask();
            }
            else
            {
                var name = _names.TryGetValue(projectFilePath, out var n) ? n : projectFilePath;
                task.Description = $"{"Loading",-9} {Markup.Escape(name)}";
            }
        }

        public void OnStageChanged(string projectFilePath, string projectName, string stage)
        {
            if (_tasks.TryGetValue(projectFilePath, out var task))
            {
                task.Description = $"{stage,-9} {Markup.Escape(projectName)}";
            }
        }

        public void OnProjectCompleted(string projectFilePath, int tripleCount)
        {
            Interlocked.Increment(ref _completed);
            if (_tasks.TryGetValue(projectFilePath, out var task))
            {
                task.StopTask();
            }
            var name = _names.TryGetValue(projectFilePath, out var n) ? n : projectFilePath;
            var elapsed = DateTime.UtcNow - (_startTimes.TryGetValue(projectFilePath, out var st) ? st : DateTime.UtcNow);
            AnsiConsole.MarkupLine(
                $"[green]✓[/] [bold]{Markup.Escape(name)}[/]  " +
                $"[dim]{FormatElapsed(elapsed)}[/]  {tripleCount} triples");
        }

        public void OnProjectSkipped(string projectFilePath, string filename, string reason)
        {
            Interlocked.Increment(ref _completed);
            if (_tasks.TryGetValue(projectFilePath, out var task))
            {
                task.StopTask();
            }
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

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
    }
}
