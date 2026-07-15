using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Neo4j.Driver;
using Strazh.Database;
using Strazh.Domain;
using Testcontainers.Neo4j;
using Xunit;
using static Strazh.Analysis.AnalyzerConfig;

namespace Strazh.Tests;

/// <summary>
/// Owns a disposable Neo4j container for the lifetime of the test class. Using a
/// throwaway container (rather than a shared instance) keeps these tests from
/// polluting a database being used for real analysis, and gives each run a clean
/// graph. If Docker is not available the fixture records why and the tests skip
/// rather than fail, so the suite stays green in environments without Docker.
/// </summary>
public sealed class Neo4jContainerFixture : IAsyncLifetime
{
    // Pinned to the same server line the project analyzes against so the uniqueness
    // constraint / MERGE syntax the write path emits is exercised on a matching engine.
    // Overridable so CI can pin or vary the image without a code change.
    private static string Image =>
        Environment.GetEnvironmentVariable("STRAZH_TEST_NEO4J_IMAGE") ?? "neo4j:2026.03.1";

    public string User => "neo4j";
    public string Password => "testpassword";
    public string Database => "neo4j";

    private Neo4jContainer? _container;

    public string? Uri { get; private set; }
    public string? SkipReason { get; private set; }
    public bool Available => Uri is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new Neo4jBuilder(Image)
                .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
                .Build();
            await _container.StartAsync();
            Uri = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            // Most commonly: no Docker daemon reachable. Record it so tests can skip.
            SkipReason = $"Could not start a Neo4j test container (is Docker available?): {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// End-to-end tests that exercise <see cref="DbManager.InsertData"/> against a real
/// Neo4j instance. These cover the parts of the batched, parameterized UNWIND write
/// path that no in-memory double can: that the driver accepts a nested map parameter
/// carrying a native list (a project's <c>targetFrameworks</c>), that
/// <c>MERGE (n { pk: row.x.pk }) SET n += row.x</c> parses and runs, and — most
/// importantly — that <c>SET +=</c> preserves MERGE's don't-clobber semantics: a
/// reference-only node merged after a fully-populated one must not wipe the optional
/// properties it omits.
/// </summary>
public class Neo4jWritePathIntegrationTests(Neo4jContainerFixture fixture) : IClassFixture<Neo4jContainerFixture>
{
    private CredentialsConfig Credentials => new($"{fixture.Database}:{fixture.User}:{fixture.Password}");

    [SkippableFact]
    public async Task InsertData_PersistsEveryNodeShapeAndPreservesDontClobber()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason ?? "Neo4j container unavailable");

        // Unique per run so the test is isolated from anything else in the graph and
        // so cleanup deletes only what it created.
        var prefix = $"STRAZHTEST_{Guid.NewGuid():N}_";

        var sln = new SolutionNode($"{prefix}App.sln");
        var projWithTfms = new ProjectNode($"{prefix}App", $"{prefix}App", new[] { "net8.0", "net472" });
        // Same pk as projWithTfms (pk derives from fullName), no TFMs — a reference-only
        // occurrence. Merged AFTER the populated one, it must not clear targetFrameworks.
        var projReference = new ProjectNode($"{prefix}App", $"{prefix}App");
        var package = new PackageNode($"{prefix}Newtonsoft.Json", $"{prefix}Newtonsoft.Json", "13.0.1");
        var submodule = new FolderNode($"{prefix}Core", $"{prefix}Core", FolderKind.Submodule);
        var repository = new RepositoryNode($"{prefix}my-repo");
        var klass = new ClassNode($"{prefix}Ns.Foo", "Foo", new[] { "public", "sealed" });
        var file = new FileNode($"{prefix}Foo.cs", "Foo.cs");

        var triples = new List<Triple>
        {
            new TripleContains(sln, projWithTfms),
            new TripleDependsOnPackage(projWithTfms, package),
            new TripleIncludedIn(submodule, repository),
            new TripleDeclaredAt(klass, file),
            new TripleContains(sln, projReference), // don't-clobber probe, merged last
        };

        var uri = fixture.Uri!;
        await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(fixture.User, fixture.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(fixture.Database));

        await DbManager.InsertData(triples, Credentials, uri);

        // targetFrameworks round-trips as a native Neo4j list...
        var tfm = await ScalarAsync(session,
            $"MATCH (p:Project {{fullName:'{prefix}App'}}) RETURN p.targetFrameworks AS v");
        Assert.Equal(
            new[] { "net8.0", "net472" },
            tfm.As<List<object>>().Select(x => x.ToString()).ToArray());

        // ...and the reference-only re-MERGE left exactly one Project node, undisturbed.
        var projectCount = await ScalarAsync(session,
            $"MATCH (:Solution {{fullName:'{prefix}App.sln'}})-[:CONTAINS]->(p:Project {{fullName:'{prefix}App'}}) RETURN count(p) AS v");
        Assert.Equal(1L, projectCount.As<long>());

        var version = await ScalarAsync(session,
            $"MATCH (:Project {{fullName:'{prefix}App'}})-[:DEPENDS_ON]->(pkg:Package) RETURN pkg.version AS v");
        Assert.Equal("13.0.1", version.As<string>());

        var kind = await ScalarAsync(session,
            $"MATCH (f:Folder {{fullName:'{prefix}Core'}})-[:INCLUDED_IN]->(:Repository) RETURN f.kind AS v");
        Assert.Equal("Submodule", kind.As<string>());

        var modifiers = await ScalarAsync(session,
            $"MATCH (c:Class {{fullName:'{prefix}Ns.Foo'}})-[:DECLARED_AT]->(:File) RETURN c.modifiers AS v");
        Assert.Equal("public, sealed", modifiers.As<string>());
    }

    private static async Task<object> ScalarAsync(IAsyncSession session, string cypher)
    {
        var cursor = await session.RunAsync(cypher);
        var records = await cursor.ToListAsync();
        Assert.NotEmpty(records);
        return records[0]["v"];
    }
}
