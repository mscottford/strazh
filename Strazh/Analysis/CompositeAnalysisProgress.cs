using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Strazh.Domain;

namespace Strazh.Analysis
{
    /// <summary>
    /// Fans out every <see cref="IAnalysisProgress"/> notification to any number of
    /// implementations. For <see cref="WrapAsync"/>, implementations are nested in the
    /// order supplied so that the first implementation is the outermost wrapper — any UI
    /// framework context it establishes (e.g. Spectre.Console's live display) is active
    /// when subsequent implementations' <c>WrapAsync</c> calls run.
    /// </summary>
    public sealed class CompositeAnalysisProgress : IAnalysisProgress
    {
        private readonly IAnalysisProgress[] _implementations;

        public CompositeAnalysisProgress(params IAnalysisProgress[] implementations)
        {
            _implementations = implementations;
        }

        public Task WrapAsync(int totalProjects, Func<Task> action)
        {
            // Build the nested chain from right to left so the first implementation
            // is the outermost wrapper.
            var current = action;
            for (var i = _implementations.Length - 1; i >= 0; i--)
            {
                var impl = _implementations[i];
                var next = current;
                current = () => impl.WrapAsync(totalProjects, next);
            }
            return current();
        }

        public void OnBuildStarted(string projectFilePath, string projectName, bool isCacheHit)
        {
            foreach (var impl in _implementations)
            {
                impl.OnBuildStarted(projectFilePath, projectName, isCacheHit);
            }
        }

        public void OnBuildCompleted(string projectFilePath)
        {
            foreach (var impl in _implementations)
            {
                impl.OnBuildCompleted(projectFilePath);
            }
        }

        public void OnStageChanged(string projectFilePath, string projectName, string stage)
        {
            foreach (var impl in _implementations)
            {
                impl.OnStageChanged(projectFilePath, projectName, stage);
            }
        }

        public void OnProjectCompleted(string projectFilePath, int tripleCount)
        {
            foreach (var impl in _implementations)
            {
                impl.OnProjectCompleted(projectFilePath, tripleCount);
            }
        }

        public void OnProjectSkipped(string projectFilePath, string filename, string reason)
        {
            foreach (var impl in _implementations)
            {
                impl.OnProjectSkipped(projectFilePath, filename, reason);
            }
        }

        public void OnGroupingError(string projectName, IReadOnlyList<Triple> triples)
        {
            foreach (var impl in _implementations)
            {
                impl.OnGroupingError(projectName, triples);
            }
        }
    }
}
