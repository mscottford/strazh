namespace Strazh.Analysis
{
    /// <summary>
    /// Identifies the build stage shown in the progress UI during an
    /// <see cref="IAnalysisProgress.OnBuildStarted"/> notification.
    /// </summary>
    public enum BuildStageLabel
    {
        /// <summary>
        /// The dependency-discovery pass. Runs full MSBuild invocations to populate the
        /// binlog cache and <c>.deps</c> sidecars. This is the pass that actually compiles
        /// the project; subsequent runs replay the cached binlog instead of rebuilding.
        /// </summary>
        Building,

        /// <summary>
        /// The knowledge-graph pass. Replays the binlog cache (or runs a fresh MSBuild build
        /// if the cache is stale) then loads the results into the Roslyn workspace for triple
        /// extraction, grouping, and insertion into the graph database.
        /// </summary>
        Graphing,
    }
}
