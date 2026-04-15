using System.Collections.Generic;
using System.Threading.Tasks;
using Strazh.Domain;
using static Strazh.Analysis.AnalyzerConfig;

namespace Strazh.Database
{
    /// <summary>
    /// Production <see cref="ITripleStore"/> implementation backed by Neo4j.
    /// Delegates to the static <see cref="DbManager"/> helpers.
    /// </summary>
    public class Neo4jTripleStore(CredentialsConfig credentials, string neo4jUrl) : ITripleStore
    {
        public Task EnsureIndexesAsync() =>
            DbManager.EnsureIndexes(credentials, neo4jUrl);

        public Task DeleteAllAsync() =>
            DbManager.DeleteData(credentials, neo4jUrl);

        public Task InsertAsync(IList<Triple> triples) =>
            DbManager.InsertData(triples, credentials, neo4jUrl);
    }
}
