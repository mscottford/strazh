using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Strazh.Analysis;
using Xunit;

namespace Strazh.Tests;

/// <summary>
/// Covers the staging of MSBuild binary logs. A binlog only reaches the cache once its build has
/// succeeded and the log has been read back, because an abandoned build — a timeout above all — leaves
/// a truncated log behind: written straight to the cache path it would be treated as a cache hit by the
/// next run, fail to replay, and force a rebuild that times out and truncates it again.
/// </summary>
public class AnalyzerBinlogStagingTests : IDisposable
{
    private readonly string _cacheDirectory;

    public AnalyzerBinlogStagingTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"strazh-staging-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    private string CachePath(string name = "App.csproj_ABCD1234.binlog") =>
        Path.Combine(_cacheDirectory, name);

    [Fact]
    public void StagedLogsAreKeptOutOfTheCacheDirectoryItself()
    {
        // Anything sitting at a cache path is replayed as a cache hit, so a build in progress must not
        // be written there — it goes in a dot-prefixed subdirectory instead.
        var staging = Analyzer.GetStagingBinlogPath(CachePath(), attempt: 1);

        Assert.Equal(Analyzer.GetStagingDirectory(_cacheDirectory), Path.GetDirectoryName(staging));
        Assert.NotEqual(CachePath(), staging);
    }

    [Fact]
    public void StagedLogsStillEndInBinlog()
    {
        // MSBuild's binary logger rejects any other extension (MSB1029), so the staging path cannot be
        // marked out by a suffix of its own — only by the directory it lives in.
        Assert.EndsWith(".binlog", Analyzer.GetStagingBinlogPath(CachePath(), attempt: 1));
    }

    [Fact]
    public void EachAttemptStagesToItsOwnFile()
    {
        // A retry must not write over the previous attempt's log, which the previous MSBuild may still
        // be holding open.
        var first = Analyzer.GetStagingBinlogPath(CachePath(), attempt: 1);
        var second = Analyzer.GetStagingBinlogPath(CachePath(), attempt: 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void StagingIsPerProject()
    {
        var app = Analyzer.GetStagingBinlogPath(CachePath("App.csproj_ABCD1234.binlog"), attempt: 1);
        var api = Analyzer.GetStagingBinlogPath(CachePath("Api.csproj_99887766.binlog"), attempt: 1);

        Assert.NotEqual(app, api);
    }

    [Fact]
    public void PublishingMovesTheStagedLogOntoTheCachePath()
    {
        Analyzer.PrepareStagingDirectory(_cacheDirectory);
        var staging = Analyzer.GetStagingBinlogPath(CachePath(), attempt: 1);
        File.WriteAllText(staging, "a complete log");

        Analyzer.PublishBinlogToCache(staging, CachePath());

        Assert.Equal("a complete log", File.ReadAllText(CachePath()));
        Assert.False(File.Exists(staging), "the staged copy should not be left behind");
    }

    [Fact]
    public void PublishingReplacesAnEarlierCacheEntry()
    {
        Analyzer.PrepareStagingDirectory(_cacheDirectory);
        File.WriteAllText(CachePath(), "the previous log");
        var staging = Analyzer.GetStagingBinlogPath(CachePath(), attempt: 1);
        File.WriteAllText(staging, "the rebuilt log");

        Analyzer.PublishBinlogToCache(staging, CachePath());

        Assert.Equal("the rebuilt log", File.ReadAllText(CachePath()));
    }

    [Fact]
    public void PreparingCreatesTheStagingDirectory()
    {
        Analyzer.PrepareStagingDirectory(_cacheDirectory);

        Assert.True(Directory.Exists(Analyzer.GetStagingDirectory(_cacheDirectory)));
    }

    [Fact]
    public void PreparingSweepsLongAbandonedLeftoversButNotRecentOnes()
    {
        // A build killed while MSBuild still holds its staged log open (Windows refuses the delete)
        // leaves the file behind; the sweep clears it out on a later run, while leaving alone anything
        // a concurrently-running analysis may still be writing.
        Analyzer.PrepareStagingDirectory(_cacheDirectory);
        var abandoned = Analyzer.GetStagingBinlogPath(CachePath("Old.csproj_11112222.binlog"), attempt: 1);
        var current = Analyzer.GetStagingBinlogPath(CachePath("Live.csproj_33334444.binlog"), attempt: 1);
        File.WriteAllText(abandoned, "truncated");
        File.WriteAllText(current, "in progress");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - Analyzer.StagingLeftoverMaxAge - TimeSpan.FromHours(1));

        Analyzer.PrepareStagingDirectory(_cacheDirectory);

        Assert.False(File.Exists(abandoned), "a long-abandoned staged log should be swept");
        Assert.True(File.Exists(current), "a recently-written staged log may still be in use");
    }

    [Fact]
    public void PreparingLeavesRealCacheEntriesAlone()
    {
        File.WriteAllText(CachePath(), "a cached log");
        File.SetLastWriteTimeUtc(CachePath(), DateTime.UtcNow - TimeSpan.FromDays(30));

        Analyzer.PrepareStagingDirectory(_cacheDirectory);

        Assert.True(File.Exists(CachePath()), "the sweep must only touch staged files");
    }

    [Fact]
    public void DeletingAFileThatIsAlreadyGoneIsNotAnError()
    {
        // Cleanup runs on paths that may never have been created (a build that failed before writing).
        Analyzer.TryDeleteFile(Path.Combine(_cacheDirectory, "never-written.binlog"));
    }

    /// <summary>
    /// The invariant, over a real analysis with real MSBuild builds: every log the run leaves in the
    /// cache can be read back in full, and no staged log is left behind. A binlog is a gzip stream, so a
    /// truncated one — the state a timed-out build leaves on disk — fails to decompress.
    /// </summary>
    [Fact]
    public async Task ARealRunLeavesOnlyCompleteLogsInTheCache()
    {
        var solutionPath = Path.Combine(GetRepoRoot(), "SystemUnderTest", "SystemUnderTest.sln");
        var logDirectory = Path.Combine(_cacheDirectory, "build-logs");
        var config = new AnalyzerConfig(new AnalyzerConfig.Options(
            Credentials: "db:user:pass",
            Tier: "project",
            Delete: "false",
            Solution: solutionPath,
            Projects: Array.Empty<string>(),
            CacheDirectory: _cacheDirectory,
            BuildLogDirectory: logDirectory));

        await Analyzer.Analyze(config, new NullAnalysisProgress(), new InMemoryTripleStore());

        var cached = Directory.GetFiles(_cacheDirectory, "*.binlog");
        Assert.NotEmpty(cached);
        foreach (var binlog in cached)
        {
            AssertIsCompleteGzipStream(binlog);
        }
        Assert.Empty(Directory.GetFiles(Analyzer.GetStagingDirectory(_cacheDirectory)));
    }

    // The abandoned-build case itself is not exercised here: a build that outruns its timeout keeps
    // running after the analysis stops waiting for it, so forcing one in-process leaves MSBuild writing
    // to Buildalyzer's logger after the test host has torn it down, which aborts the run. What keeps a
    // truncated log out of the cache is structural instead — PublishBinlogToCache is the only thing that
    // ever writes a cache path, and it runs only once a log has been built and read back.

    private static void AssertIsCompleteGzipStream(string path)
    {
        using var file = File.OpenRead(path);
        using var decompressed = new GZipStream(file, CompressionMode.Decompress);
        try
        {
            decompressed.CopyTo(Stream.Null);
        }
        catch (Exception truncated) when (truncated is InvalidDataException or EndOfStreamException)
        {
            Assert.Fail($"{Path.GetFileName(path)} was cached truncated: {truncated.Message}");
        }
    }

    private static string GetRepoRoot([CallerFilePath] string callerFile = "") =>
        Path.GetFullPath(Path.Combine(callerFile, "../.."));
}
