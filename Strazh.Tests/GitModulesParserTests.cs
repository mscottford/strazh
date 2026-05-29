using Strazh.Analysis;
using Xunit;

namespace Strazh.Tests;

public class GitModulesParserTests
{
    [Fact]
    public void Parse_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(GitModulesParser.Parse(""));
    }

    [Fact]
    public void Parse_SingleSubmodule_ReturnsOneEntryVerbatim()
    {
        var content = "[submodule \"Core\"]\n\tpath = Core\n\turl = ../infra-dotnet-core.git\n";

        var result = GitModulesParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("Core", result[0].Name);
        Assert.Equal("Core", result[0].Path);
        Assert.Equal("../infra-dotnet-core.git", result[0].Url);
    }

    [Fact]
    public void Parse_MultipleSubmodules_ReturnsAllEntriesInOrder()
    {
        var content = """
            [submodule "Core"]
                path = Core
                url = ../infra-dotnet-core.git
            [submodule "Vendor/Foo"]
                path = vendor/foo
                url = https://github.com/Org/foo.git
            """;

        var result = GitModulesParser.Parse(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("Core", result[0].Name);
        Assert.Equal("Vendor/Foo", result[1].Name);
        Assert.Equal("vendor/foo", result[1].Path);
        Assert.Equal("https://github.com/Org/foo.git", result[1].Url);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        var content = """
            # leading comment
            ; alternate comment style

            [submodule "Core"]
                # inside section
                path = Core
                url = ../core.git
            """;

        var result = GitModulesParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("Core", result[0].Path);
    }

    [Fact]
    public void Parse_IgnoresOtherSectionsAndUnknownKeys()
    {
        var content = """
            [core]
                bare = false
            [submodule "Core"]
                path = Core
                url = ../core.git
                update = checkout
                branch = main
            """;

        var result = GitModulesParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("Core", result[0].Path);
        Assert.Equal("../core.git", result[0].Url);
    }

    [Fact]
    public void Parse_TolerantOfKeyValueSpacing()
    {
        var content = """
            [submodule "Core"]
                path=Core
                url   =   ../core.git
            """;

        var result = GitModulesParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("Core", result[0].Path);
        Assert.Equal("../core.git", result[0].Url);
    }

    [Fact]
    public void Parse_SubmoduleMissingPath_IsSkipped()
    {
        var content = """
            [submodule "Core"]
                url = ../core.git
            [submodule "Good"]
                path = good
                url = ../good.git
            """;

        var result = GitModulesParser.Parse(content);

        Assert.Single(result);
        Assert.Equal("Good", result[0].Name);
    }

    [Fact]
    public void Parse_SubmoduleMissingUrl_IsSkipped()
    {
        var content = """
            [submodule "Core"]
                path = Core
            """;

        Assert.Empty(GitModulesParser.Parse(content));
    }
}
