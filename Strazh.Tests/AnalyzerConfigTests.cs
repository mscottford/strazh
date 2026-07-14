using System;
using System.IO;
using Strazh.Analysis;
using Xunit;

namespace Strazh.Tests;

public class AnalyzerConfigTests
{
    private static AnalyzerConfig.Options BaseOptions(
        string? credentials = "db:user:pass",
        string? tier = "all",
        string? delete = "false",
        string? solution = "none",
        string[]? projects = null,
        string? directory = null)
        => new(
            Credentials: credentials!,
            Tier: tier!,
            Delete: delete!,
            Solution: solution!,
            Projects: projects ?? Array.Empty<string>(),
            Directory: directory);

    [Fact]
    public void Credentials_ValidTriplet_IsParsedIntoParts()
    {
        var creds = new AnalyzerConfig.CredentialsConfig("mydb:neo4j:secret");

        Assert.Equal("mydb", creds.Database);
        Assert.Equal("neo4j", creds.User);
        Assert.Equal("secret", creds.Password);
    }

    [Fact]
    public void Credentials_Empty_YieldsNonNullEmptyParts()
    {
        var creds = new AnalyzerConfig.CredentialsConfig("");

        Assert.Equal("", creds.Database);
        Assert.Equal("", creds.User);
        Assert.Equal("", creds.Password);
    }

    [Fact]
    public void Credentials_Malformed_YieldsNonNullEmptyParts()
    {
        var creds = new AnalyzerConfig.CredentialsConfig("only:two");

        Assert.Equal("", creds.Database);
        Assert.Equal("", creds.User);
        Assert.Equal("", creds.Password);
    }

    [Theory]
    [InlineData("project", AnalyzerConfig.Tiers.Project)]
    [InlineData("code", AnalyzerConfig.Tiers.Code)]
    [InlineData("all", AnalyzerConfig.Tiers.All)]
    [InlineData("anything-else", AnalyzerConfig.Tiers.All)]
    public void Tier_IsMappedFromOptionString(string tier, AnalyzerConfig.Tiers expected)
    {
        var config = new AnalyzerConfig(BaseOptions(tier: tier));

        Assert.Equal(expected, config.Tier);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("anything", true)]
    public void IsDelete_IsTrueUnlessExplicitlyFalse(string delete, bool expected)
    {
        var config = new AnalyzerConfig(BaseOptions(delete: delete));

        Assert.Equal(expected, config.IsDelete);
    }

    [Fact]
    public void Solution_None_IsTreatedAsEmpty()
    {
        var config = new AnalyzerConfig(BaseOptions(solution: "none"));

        Assert.Equal("", config.Solution);
        Assert.False(config.IsSolutionBased);
    }

    [Fact]
    public void Directory_None_IsTreatedAsEmpty()
    {
        var config = new AnalyzerConfig(BaseOptions(directory: "none"));

        Assert.Equal("", config.Directory);
        Assert.False(config.IsDirectoryBased);
    }

    [Fact]
    public void Directory_WhenProvided_IsResolvedToAbsolutePathAndDirectoryBased()
    {
        var config = new AnalyzerConfig(BaseOptions(directory: "some/dir"));

        Assert.True(config.IsDirectoryBased);
        Assert.True(Path.IsPathRooted(config.Directory));
    }

    [Theory]
    // exactly one source → valid
    [InlineData("app.sln", false, false, true)]
    [InlineData("none", true, false, true)]
    [InlineData("none", false, true, true)]
    // zero or more than one source → invalid
    [InlineData("none", false, false, false)]
    [InlineData("app.sln", true, false, false)]
    [InlineData("app.sln", false, true, false)]
    [InlineData("none", true, true, false)]
    [InlineData("app.sln", true, true, false)]
    public void IsValid_RequiresExactlyOneSource(string solution, bool hasProjects, bool hasDirectory, bool expected)
    {
        var config = new AnalyzerConfig(BaseOptions(
            solution: solution,
            projects: hasProjects ? new[] { "a.csproj" } : null,
            directory: hasDirectory ? "some/dir" : null));

        Assert.Equal(expected, config.IsValid);
    }

    [Fact]
    public void Neo4jUrl_DefaultsToLocalhostWhenNotProvided()
    {
        var config = new AnalyzerConfig(BaseOptions());

        Assert.Equal("neo4j://localhost:7687", config.Neo4jUrl);
    }

    [Fact]
    public void CacheAndBuildLogDirectories_AreNonNullAbsolutePaths()
    {
        var config = new AnalyzerConfig(BaseOptions());

        Assert.True(Path.IsPathRooted(config.CacheDirectory));
        Assert.True(Path.IsPathRooted(config.BuildLogDirectory));
    }

    [Fact]
    public void NullCliValues_AreToleratedAndFallBackToDefaults()
    {
        var config = new AnalyzerConfig(new AnalyzerConfig.Options(
            Credentials: null,
            Tier: null,
            Delete: null,
            Solution: null,
            Projects: null));

        Assert.Equal(AnalyzerConfig.Tiers.All, config.Tier);
        Assert.True(config.IsDelete);
        Assert.Equal("", config.Solution);
        Assert.Empty(config.Projects);
    }
}
