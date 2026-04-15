using System.Collections.Generic;
using System.Threading.Tasks;
using Strazh.Database;
using Strazh.Domain;

namespace Strazh.Tests;

/// <summary>
/// Test double for <see cref="ITripleStore"/> that collects triples in memory
/// so tests can assert on what the analysis pipeline produced without requiring
/// a live Neo4j connection.
/// </summary>
public class InMemoryTripleStore : ITripleStore
{
    private readonly List<Triple> _triples = [];
    private readonly object _lock = new();

    public IReadOnlyList<Triple> Triples
    {
        get
        {
            lock (_lock)
            {
                return _triples.ToArray();
            }
        }
    }

    public Task EnsureIndexesAsync() => Task.CompletedTask;

    public Task DeleteAllAsync()
    {
        lock (_lock)
        {
            _triples.Clear();
        }
        return Task.CompletedTask;
    }

    public Task InsertAsync(IList<Triple> triples)
    {
        lock (_lock)
        {
            _triples.AddRange(triples);
        }
        return Task.CompletedTask;
    }
}
