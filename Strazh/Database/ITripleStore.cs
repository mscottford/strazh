using System.Collections.Generic;
using System.Threading.Tasks;
using Strazh.Domain;

namespace Strazh.Database
{
    /// <summary>
    /// Abstracts the backing store that receives triples produced by the analysis pipeline.
    /// The production implementation writes to Neo4j; a test double can collect triples
    /// in memory for assertion without requiring a live database connection.
    /// </summary>
    public interface ITripleStore
    {
        /// <summary>
        /// Ensures that the store is ready to accept triples (e.g. creates indexes in Neo4j).
        /// Called once before any <see cref="InsertAsync"/> calls.
        /// </summary>
        Task EnsureIndexesAsync();

        /// <summary>
        /// Removes all existing data from the store.
        /// Called at most once, before any <see cref="InsertAsync"/> calls.
        /// </summary>
        Task DeleteAllAsync();

        /// <summary>
        /// Persists a batch of deduplicated triples for a single project.
        /// May be called concurrently from multiple tasks.
        /// </summary>
        Task InsertAsync(IList<Triple> triples);
    }
}
