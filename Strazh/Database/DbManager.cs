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
        private const string CONNECTION = "neo4j://localhost:7687";

        public static async Task DeleteData(CredentialsConfig credentials)
        {
            if (credentials == null)
            {
                throw new ArgumentException($"Please, provide credentials.");
            }
            Console.WriteLine($"Deleting graph data of \"{credentials.Database}\" database...");
            await using var driver = GraphDatabase.Driver(CONNECTION, AuthTokens.Basic(credentials.User, credentials.Password));
            await using var session = driver.AsyncSession(o => o.WithDatabase(credentials.Database));
            await session.RunAsync("MATCH (n) DETACH DELETE n;");
            Console.WriteLine($"Deleting graph data of \"{credentials.Database}\" database complete.");
        }

        public static async Task InsertData(IList<Triple> triples, CredentialsConfig credentials)
        {
            if (credentials == null)
            {
                throw new ArgumentException($"Please, provide credentials.");
            }
            Console.WriteLine($"Code Knowledge Graph use \"{credentials.Database}\" Neo4j database.");
            await using var driver = GraphDatabase.Driver(CONNECTION, AuthTokens.Basic(credentials.User, credentials.Password));
            await using var session = driver.AsyncSession(o => o.WithDatabase(credentials.Database));
            try
            {
                Console.WriteLine($"Processing {triples.Count} triples...");
                foreach (var triple in triples)
                {
                    await session.RunAsync(triple.ToString());
                }
                Console.WriteLine($"Processing {triples.Count} triples complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}