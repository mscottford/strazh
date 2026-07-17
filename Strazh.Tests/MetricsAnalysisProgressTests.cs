using System;
using System.IO;
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
}
