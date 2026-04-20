using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Receives progress notifications from the analysis pipeline.
    /// Implementations are free to present events however they choose —
    /// Spectre.Console spinners, plain Console.WriteLine, or a no-op for tests.
    /// All methods except <see cref="WrapAsync"/> may be called concurrently
    /// from multiple tasks.
    /// </summary>
    public interface IAnalysisProgress
    {
        /// <summary>
        /// Wraps the entire analysis pipeline. The implementation is responsible
        /// for calling <paramref name="action"/> exactly once.
        /// This is the integration point for UI frameworks that require an outer
        /// context (e.g. <c>AnsiConsole.Progress().StartAsync()</c>) to be
        /// established before any progress notifications are raised.
        /// </summary>
        /// <param name="totalProjects">Total number of projects in this run.</param>
        /// <param name="action">The pipeline body to execute.</param>
        Task WrapAsync(int totalProjects, Func<Task> action);

        /// <summary>
        /// Raised immediately before the MSBuild design-time build (or binlog
        /// cache replay) begins for the specified project.
        /// </summary>
        /// <param name="projectFilePath">Absolute path to the .csproj file.</param>
        /// <param name="projectName">Display name (filename without extension).</param>
        /// <param name="isCacheHit">
        /// <c>true</c> when a cached binlog will be replayed;
        /// <c>false</c> when a full MSBuild invocation will run.
        /// </param>
        /// <param name="buildLabel">
        /// Short label for the build stage shown in the progress UI.
        /// Use <c>"Scanning"</c> for the dependency-discovery pass and
        /// <c>"Building"</c> (default) for the main build pass.
        /// </param>
        void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit, string buildLabel = "Building");

        /// <summary>
        /// Raised after the build or cache replay has finished and the result is
        /// about to be loaded into the Roslyn workspace.
        /// </summary>
        /// <param name="projectFilePath">Absolute path to the .csproj file.</param>
        void OnBuildCompleted(string projectFilePath);

        /// <summary>
        /// Raised each time the active processing stage changes for a project.
        /// Expected values for <paramref name="stage"/> are <c>"Analyzing"</c>,
        /// <c>"Grouping"</c>, and <c>"Inserting"</c>, but implementations must
        /// tolerate arbitrary strings.
        /// </summary>
        /// <param name="projectFilePath">Absolute path to the .csproj file.</param>
        /// <param name="projectName">Display name (filename without extension).</param>
        /// <param name="stage">Short human-readable label for the current stage.</param>
        void OnStageChanged(string projectFilePath, string projectName, string stage);

        /// <summary>
        /// Raised after a project has been fully analyzed and all its triples
        /// inserted into the database.
        /// </summary>
        /// <param name="projectFilePath">Absolute path to the .csproj file.</param>
        /// <param name="tripleCount">Number of triples inserted for this project.</param>
        void OnProjectCompleted(string projectFilePath, int tripleCount);

        /// <summary>
        /// Raised when a project cannot be loaded into the Roslyn workspace because
        /// its type is not supported (e.g. F# or native projects).
        /// </summary>
        /// <param name="projectFilePath">Absolute path to the project file.</param>
        /// <param name="filename">Filename of the skipped project (e.g. MyLib.fsproj).</param>
        /// <param name="reason">Human-readable explanation of why it was skipped.</param>
        void OnProjectSkipped(string projectFilePath, string filename, string reason);

        /// <summary>
        /// Raised when triple deduplication (GroupBy) throws an exception.
        /// The implementation should surface the triple list for diagnosis.
        /// The pipeline will rethrow the exception after this method returns.
        /// </summary>
        /// <param name="projectName">Display name of the project whose grouping failed.</param>
        /// <param name="triples">Raw (ungrouped) triples at the time of failure.</param>
        void OnGroupingError(string projectName, IReadOnlyList<Triple> triples);
    }
}
