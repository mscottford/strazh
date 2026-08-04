using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Strazh.Analysis;
using Xunit;

namespace Strazh.Tests;

public class MetricsAnalysisProgressTests
{
    /// <summary>
    /// Drives one project through a scripted lifecycle with deliberate delays and asserts the
    /// JSONL report attributes time to the right stage — in particular that the Load stage
    /// (from the dedicated load-start event) is recorded separately from the queue wait that
    /// precedes it, which is the whole point of collecting these metrics.
    /// </summary>
    [Fact]
    public async Task WritesPerStageTimings_SeparatingLoadFromQueueWait()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"strazh-metrics-test-{Guid.NewGuid():N}.jsonl");
        const string path = "/repo/App/App.csproj";
        try
        {
            var metrics = new MetricsAnalysisProgress(metricsPath);

            await metrics.WrapAsync(1, async () =>
            {
                metrics.OnBuildStarted(path, "App", isCacheHit: false, BuildStageLabel.PostBuild);
                await Task.Delay(30);
                metrics.OnBuildCompleted(path);               // -> Waiting (queued)
                await Task.Delay(30);
                metrics.OnStageChanged(path, "App", "Loading"); // load actually begins
                await Task.Delay(150);                          // the slow part
                metrics.OnStageChanged(path, "App", "Analyzing");
                await Task.Delay(10);
                metrics.OnStageChanged(path, "App", "Grouping");
                await Task.Delay(10);
                metrics.OnStageChanged(path, "App", "Inserting");
                await Task.Delay(10);
                metrics.OnProjectCompleted(path, 42);
            });

            var lines = await File.ReadAllLinesAsync(metricsPath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal("App", root.GetProperty("name").GetString());
            Assert.Equal("completed", root.GetProperty("outcome").GetString());
            Assert.Equal(42, root.GetProperty("triples").GetInt32());
            Assert.True(root.GetProperty("totalMs").GetDouble() > 0);

            var stages = root.GetProperty("stagesMs");
            double Stage(string s) => stages.GetProperty(s).GetDouble();

            // Every stage in the scripted lifecycle is present and distinct.
            foreach (var stage in new[] { "PostBuild", "Waiting", "Loading", "Analyzing", "Grouping", "Inserting" })
            {
                Assert.True(stages.TryGetProperty(stage, out _), $"expected stage '{stage}' in report");
            }

            // Load time is isolated from the wait that preceded it, and is clearly the largest
            // span (its scripted delay dwarfs the others). Generous margins keep this robust.
            Assert.True(Stage("Loading") > Stage("Waiting"), "Loading should exceed the queue wait");
            Assert.True(Stage("Loading") > Stage("Analyzing"), "Loading should exceed Analyzing");
            Assert.True(Stage("Loading") > 80, "Loading span should reflect its ~150ms delay");
        }
        finally
        {
            if (File.Exists(metricsPath))
            {
                File.Delete(metricsPath);
            }
        }
    }

    /// <summary>
    /// A project that finishes building early sits in the sequential Load queue until every other build
    /// completes, so its wall clock says more about the queue than about the project. The report
    /// separates the two, and ranks by the working time — otherwise the slowest-projects list is just a
    /// list of whoever built first.
    /// </summary>
    [Fact]
    public async Task ReportsWorkingTimeSeparatelyFromQueueWait()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"strazh-metrics-test-{Guid.NewGuid():N}.jsonl");
        const string quickBuildLongWait = "/repo/Waited/Waited.csproj";
        const string slowBuild = "/repo/Slow/Slow.csproj";
        try
        {
            var metrics = new MetricsAnalysisProgress(metricsPath);

            await metrics.WrapAsync(2, async () =>
            {
                // Builds in almost no time, then waits a long while for the Load stage.
                metrics.OnBuildStarted(quickBuildLongWait, "Waited", isCacheHit: false, BuildStageLabel.PostBuild);
                await Task.Delay(10);
                metrics.OnBuildCompleted(quickBuildLongWait);        // -> Waiting (queued)

                // Genuinely slow to build, and barely waits at all.
                metrics.OnBuildStarted(slowBuild, "Slow", isCacheHit: false, BuildStageLabel.PostBuild);
                await Task.Delay(200);
                metrics.OnBuildCompleted(slowBuild);
                metrics.OnStageChanged(slowBuild, "Slow", "Loading");
                await Task.Delay(10);
                metrics.OnProjectCompleted(slowBuild, 7);

                metrics.OnStageChanged(quickBuildLongWait, "Waited", "Loading");
                await Task.Delay(10);
                metrics.OnProjectCompleted(quickBuildLongWait, 3);
            });

            var records = (await File.ReadAllLinesAsync(metricsPath))
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToList();
            double Working(string name) => records.Single(r => r.GetProperty("name").GetString() == name)
                .GetProperty("workingMs").GetDouble();
            double Waiting(string name) => records.Single(r => r.GetProperty("name").GetString() == name)
                .GetProperty("waitingMs").GetDouble();
            double Total(string name) => records.Single(r => r.GetProperty("name").GetString() == name)
                .GetProperty("totalMs").GetDouble();

            // The waiter's wall clock is inflated by the queue; its working time is not.
            Assert.True(Waiting("Waited") > Working("Waited"),
                "the queued project should have spent longer waiting than working");
            Assert.True(Total("Waited") > Working("Waited"),
                "total time should still include the wait");
            Assert.True(Working("Slow") > Working("Waited"),
                "the project that actually took time to build should have the greater working time");

            // The slow builder barely queued at all, so its two figures are close together.
            Assert.True(Waiting("Slow") < Working("Slow"));

            // The report is ordered by working time, so the genuinely slow project comes first.
            Assert.Equal("Slow", records[0].GetProperty("name").GetString());
        }
        finally
        {
            if (File.Exists(metricsPath))
            {
                File.Delete(metricsPath);
            }
        }
    }

    /// <summary>
    /// A deferred project is parked until the fallback pass records it, which happens only after the
    /// whole stream has drained — so that span can cover most of the run and says nothing about the
    /// project. It counts as parked time, not work.
    /// </summary>
    [Fact]
    public async Task TimeSpentDeferredIsNotCountedAsWork()
    {
        var metricsPath = Path.Combine(Path.GetTempPath(), $"strazh-metrics-test-{Guid.NewGuid():N}.jsonl");
        const string deferred = "/repo/Dropped/Dropped.csproj";
        try
        {
            var metrics = new MetricsAnalysisProgress(metricsPath);

            await metrics.WrapAsync(1, async () =>
            {
                metrics.OnBuildStarted(deferred, "Dropped", isCacheHit: false, BuildStageLabel.PostBuild);
                await Task.Delay(10);
                metrics.OnProjectDeferred(deferred, "Dropped.csproj", "build timed out");
                await Task.Delay(200);   // parked until the fallback pass, long after the build attempt
                metrics.OnProjectRecordedFromFallback(deferred, "Dropped.csproj", tripleCount: 1, buildFailed: true);
            });

            using var doc = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(metricsPath)));
            var root = doc.RootElement;
            var working = root.GetProperty("workingMs").GetDouble();
            var waiting = root.GetProperty("waitingMs").GetDouble();
            var deferredSpan = root.GetProperty("stagesMs").GetProperty("deferred").GetDouble();

            Assert.True(deferredSpan > 100, "the scripted deferral should dominate this project's wall clock");
            Assert.True(waiting >= deferredSpan, "deferred time should be counted as parked, not as work");
            Assert.True(working < deferredSpan, $"working time ({working}ms) should exclude the deferral");
            // The split is exhaustive: every millisecond is either work or parked time. Tolerance covers
            // the rounding each figure gets on its way into the report.
            Assert.Equal(root.GetProperty("totalMs").GetDouble(), working + waiting, tolerance: 1.0);
        }
        finally
        {
            if (File.Exists(metricsPath))
            {
                File.Delete(metricsPath);
            }
        }
    }
}
