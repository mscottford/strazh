using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Strazh.Analysis;
using Strazh.Domain;

namespace Strazh.Tests;

/// <summary>
/// No-op implementation of <see cref="IAnalysisProgress"/> for tests that drive
/// the full analysis pipeline but don't need progress reporting.
/// </summary>
public class NullAnalysisProgress : IAnalysisProgress
{
    public static readonly NullAnalysisProgress Instance = new();

    public Task WrapAsync(int totalProjects, Func<Task> action) => action();

    public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit) { }
    public void OnBuildCompleted(string projectFilePath) { }
    public void OnStageChanged(string projectFilePath, string projectName, string stage) { }
    public void OnProjectCompleted(string projectFilePath, int tripleCount) { }
    public void OnProjectSkipped(string projectFilePath, string filename, string reason) { }
    public void OnGroupingError(string projectName, IReadOnlyList<Triple> triples) { }
}
