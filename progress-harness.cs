#!/usr/bin/env dotnet
// Standalone harness that drives SpectreConsoleProgress with a simulated analysis workload,
// so the live-display teardown (leftover spinner rows / orphaned summary / blank padding after
// the run ends) can be reproduced and iterated on without running the full ~90s pipeline.
//
// It mimics the real phases: a wide concurrent build phase (which grows the panel toward the
// terminal height), a sequential load/analyze/insert phase whose completed lines scroll the
// terminal, a few deferred-then-recorded projects, and a metrics-style block printed to the
// console AFTER the progress display tears down — the exact moment the artifacts show up.
//
//   dotnet run progress-harness.cs [--total N] [--concurrency N] [--min ms] [--max ms]
//
#:project Strazh/Strazh.csproj

using Strazh.Analysis;

var total = 165;
var concurrency = 20;
var minDelay = 10;
var maxDelay = 130;
// Lines of output printed before the live display starts. Real strazh prints ~9 setup lines
// (Neo4j ready, Brewing, Scanning, Deleting, Ensuring indexes, …) first, which pushes the progress
// panel to the bottom of the viewport — where completed-line writes scroll the terminal and stale
// frames get orphaned. Set this to reproduce that; --preamble 0 mimics the (clean) harness default.
var preamble = 9;
for (var i = 0; i + 1 < args.Length; i++)
{
    switch (args[i])
    {
        case "--total":
            total = int.Parse(args[i + 1]);
            break;
        case "--concurrency":
            concurrency = int.Parse(args[i + 1]);
            break;
        case "--min":
            minDelay = int.Parse(args[i + 1]);
            break;
        case "--max":
            maxDelay = int.Parse(args[i + 1]);
            break;
        case "--preamble":
            preamble = int.Parse(args[i + 1]);
            break;
    }
}

var random = new Random(20260720);
int Jitter(int floor, int ceiling) => random.Next(floor, Math.Max(floor + 1, ceiling));
string ProjectPath(int index) => $"/repo/Sample{index:D3}/Sample.Project.Number{index:D3}.csproj";
string ProjectName(int index) => $"Sample.Project.Number{index:D3}";
string FileName(int index) => Path.GetFileName(ProjectPath(index));
bool IsDeferred(int index) => index % 40 == 13; // a couple of build-failed projects, like a real run

IAnalysisProgress progress = new SpectreConsoleProgress();

// Preamble printed before the live display starts (see --preamble above), to reproduce strazh
// starting its progress panel near the bottom of the viewport.
for (var line = 0; line < preamble; line++)
{
    Console.WriteLine($"Setup line {line + 1} — printed before the live progress display starts");
}

await progress.WrapAsync(total, async () =>
{
    // Single concurrent pipeline: each project runs build -> load -> analyze -> insert -> complete,
    // throttled so ~concurrency are active at once. This keeps the panel tall (many visible active
    // rows) WHILE completed projects print scrollback lines — the overlap of a tall live region with
    // terminal scrolling that the real run produces and that orphans rows on teardown.
    using var throttle = new SemaphoreSlim(concurrency);
    var work = Enumerable.Range(0, total).Select(async index =>
    {
        await throttle.WaitAsync();
        try
        {
            var path = ProjectPath(index);
            var name = ProjectName(index);
            progress.OnBuildStarted(path, name, isCacheHit: index % 7 == 0, BuildStageLabel.PostBuild);
            await Task.Delay(Jitter(minDelay, maxDelay));
            progress.OnBuildCompleted(path);
            if (IsDeferred(index))
            {
                progress.OnProjectDeferred(path, FileName(index), "build failed");
                await Task.Delay(Jitter(minDelay, maxDelay));
                progress.OnProjectRecordedFromFallback(path, FileName(index), Jitter(1, 10), buildFailed: true);
                return;
            }
            progress.OnStageChanged(path, name, "Loading");
            await Task.Delay(Jitter(minDelay / 2, maxDelay / 2));
            progress.OnStageChanged(path, name, "Analyzing");
            await Task.Delay(Jitter(minDelay / 2, maxDelay / 2));
            progress.OnStageChanged(path, name, "Inserting");
            await Task.Delay(Jitter(minDelay / 2, maxDelay / 2));
            progress.OnProjectCompleted(path, Jitter(3, 220));
        }
        finally
        {
            throttle.Release();
        }
    }).ToArray();
    await Task.WhenAll(work);
});

// Simulate the metrics summary the real pipeline prints AFTER the progress display tears down.
// If teardown is clean, these lines appear immediately below the last completed line with no
// orphaned summary row and no block of blank padding in between.
Console.WriteLine("Analysis metrics (SIMULATED) — should sit directly under the last completed line");
Console.WriteLine("  Total time per stage (summed across projects):");
Console.WriteLine("    Waiting        1234.5s");
Console.WriteLine("    Building        678.9s");
Console.WriteLine("    Inserting        42.1s");
