using Neo4j.Driver;
using System;
using System.Threading.Tasks;
using Strazh.Domain;
using System.Collections.Generic;
using static Strazh.Analysis.AnalyzerConfig;

namespace Strazh.Database
{
    public static class DbManager
    {
        // All node labels that carry a pk property used as the MERGE key.
        // A uniqueness constraint implicitly creates a B-tree index, turning each
        // MERGE (n:Label { pk: "..." }) from a full label scan into an index lookup.
        private static readonly string[] NodeLabels =
            ["Class", "Interface", "Method", "File", "Folder", "Solution", "Project", "Package"];

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
            await using var driver = GraphDatabase.Driver(neo4jUrl, AuthTokens.Basic(credentials.User, credentials.Password));
            await using var session = driver.AsyncSession(o => o.WithDatabase(credentials.Database));
            try
            {
                foreach (var triple in triples)
                {
                    await session.RunAsync(triple.ToString());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}