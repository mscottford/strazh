using Neo4j.Driver;
using System;
using System.Linq;
using System.Threading.Tasks;
using Strazh.Domain;
using System.Collections.Generic;
using static Strazh.Analysis.AnalyzerConfig;

namespace Strazh.Database
{
    public static class DbManager
    {
        // Rows per write transaction. Neo4j's bulk-import guidance targets 10k-100k updates
        // per transaction; each row here is ~5-6 updates (two node MERGEs + property sets +
        // one relationship MERGE), so 10k rows lands in the middle of that window while
        // keeping any single transaction's memory bounded for very large projects.
        private const int BatchSize = 10_000;
        // All node labels that carry a pk property used as the MERGE key.
        // A uniqueness constraint implicitly creates a B-tree index, turning each
        // MERGE (n:Label { pk: "..." }) from a full label scan into an index lookup.
        private static readonly string[] NodeLabels =
            ["Class", "Interface", "Method", "File", "Folder", "Solution", "Project", "Package", "Repository"];

        public static async Task EnsureIndexes(CredentialsConfig credentials, string neo4jUrl)
        {
            if (credentials == null)
            {
                throw new ArgumentException($"Please, provide credentials.");
            }
            Console.WriteLine("Ensuring Neo4j uniqueness constraints and indexes...");
            await using var driver = GraphDatabase.Driver(neo4jUrl, AuthTokens.Basic(credentials.User, credentials.Password));
            await using var session = driver.AsyncSession(o => o.WithDatabase(credentials.Database));
            foreach (var label in NodeLabels)
            {
                await session.RunAsync(
                    $"CREATE CONSTRAINT {label.ToLowerInvariant()}_pk IF NOT EXISTS FOR (n:{label}) REQUIRE n.pk IS UNIQUE");
            }
            Console.WriteLine("Neo4j uniqueness constraints and indexes ready.");
        }

        public static async Task DeleteData(CredentialsConfig credentials, string neo4jUrl)
        {
            if (credentials == null)
            {
                throw new ArgumentException($"Please, provide credentials.");
            }
            Console.WriteLine($"Deleting graph data of \"{credentials.Database}\" database...");
            await using var driver = GraphDatabase.Driver(neo4jUrl, AuthTokens.Basic(credentials.User, credentials.Password));
            await using var session = driver.AsyncSession(o => o.WithDatabase(credentials.Database));
            await session.RunAsync("MATCH (n) DETACH DELETE n;");
            Console.WriteLine($"Deleting graph data of \"{credentials.Database}\" database complete.");
        }

        public static async Task InsertData(IList<Triple> triples, CredentialsConfig credentials, string neo4jUrl)
        {
            if (credentials == null)
            {
                throw new ArgumentException($"Please, provide credentials.");
            }
            if (triples == null || triples.Count == 0)
            {
                return;
            }

            await using var driver = GraphDatabase.Driver(neo4jUrl, AuthTokens.Basic(credentials.User, credentials.Password));
            await using var session = driver.AsyncSession(o => o.WithDatabase(credentials.Database));

            // Group triples by query shape — (source label, target label, relationship type) —
            // so each group becomes a single parameterized UNWIND ... MERGE run over a batch of
            // rows. This replaces the old path of one auto-commit transaction per triple, which
            // paid a round-trip and a durable commit for every node and defeated the query plan
            // cache (each triple was a distinct interpolated string). Labels and relationship
            // types can't be parameters in Cypher, so they are interpolated — but they come from
            // a small fixed vocabulary (a dozen or so shapes), so the planner caches a plan per
            // shape and reuses it across every batch. All actual values travel as parameters.
            //
            // Separate MERGEs for each node and the relationship (rather than one combined
            // pattern) follow Neo4j's bulk-import guidance and let each MERGE use its node's
            // pk uniqueness constraint as an index-backed lookup. "SET n += row.x" merges only
            // the properties present on that node, preserving MERGE's don't-clobber semantics
            // for reference-only nodes exactly as the previous ON CREATE / ON MATCH SET did.
            foreach (var group in triples.GroupBy(t => (t.NodeA.Label, t.NodeB.Label, t.Relationship.Type)))
            {
                var (labelA, labelB, relType) = group.Key;
                var cypher =
                    $"UNWIND $rows AS row " +
                    $"MERGE (a:{labelA} {{ pk: row.a.pk }}) SET a += row.a " +
                    $"MERGE (b:{labelB} {{ pk: row.b.pk }}) SET b += row.b " +
                    $"MERGE (a)-[:{relType}]->(b)";

                var rows = group
                    .Select(t => (object)new Dictionary<string, object>
                    {
                        ["a"] = t.NodeA.Properties(),
                        ["b"] = t.NodeB.Properties(),
                    })
                    .ToList();

                for (var offset = 0; offset < rows.Count; offset += BatchSize)
                {
                    var batch = rows.GetRange(offset, Math.Min(BatchSize, rows.Count - offset));
                    // ExecuteWriteAsync wraps the batch in a managed transaction that retries
                    // on transient errors (deadlocks, leader switches) instead of silently
                    // dropping data the way the previous catch-and-continue did. ConsumeAsync
                    // drains the result so the write completes (and any error surfaces) before
                    // the managed transaction commits.
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync(cypher, new { rows = batch });
                        await cursor.ConsumeAsync();
                    });
                }
            }
        }
    }
}