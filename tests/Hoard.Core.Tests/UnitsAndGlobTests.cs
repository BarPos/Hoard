using Hoard.Core.Filters;
using Hoard.Core.Formatting;

namespace Hoard.Core.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(4_000, "4.0 KB")]
    [InlineData(212_000_000, "212 MB")]
    [InlineData(31_200_000_000, "31.2 GB")]
    [InlineData(999_960, "1.0 MB")]
    [InlineData(412_600_000_000, "412.6 GB")]
    [InlineData(142_800_000_000, "142.8 GB")]
    [InlineData(999_960_000, "1.0 GB")]
    [InlineData(6_900_000_000_000, "6.9 TB")]
    public void Si_sizes(long bytes, string expected) => Assert.Equal(expected, Units.Size(bytes));

    [Theory]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1_073_741_824, "1.0 GiB")]
    public void Iec_sizes(long bytes, string expected) => Assert.Equal(expected, Units.Size(bytes, iec: true));

    [Fact]
    public void Counts_are_grouped() => Assert.Equal("1 284 312", Units.Count(1_284_312));
}

public class GlobTests
{
    private static bool X(string pattern, string rel, bool dir = false, string? full = null) =>
        new GlobMatcher([pattern], "/Users/you").IsExcluded(rel, full ?? "/scan/" + rel, dir);

    [Theory]
    [InlineData("node_modules", "a/b/node_modules", true)]
    [InlineData("node_modules", "node_modules", true)]
    [InlineData("*.log", "x/y/debug.log", true)]
    [InlineData("*.log", "x/y/debug.log.txt", false)]
    [InlineData("/build", "build", true)]
    [InlineData("/build", "app/build", false)]
    [InlineData("app/*/cache", "app/one/cache", true)]
    [InlineData("app/*/cache", "app/one/two/cache", false)]
    [InlineData("app/**/cache", "app/one/two/cache", true)]
    [InlineData("app/**/cache", "app/cache", true)]
    [InlineData("**/.git/objects", "proj/.git/objects", true)]
    [InlineData("f?o", "foo", true)]
    [InlineData("f?o", "fooo", false)]
    public void Patterns(string pattern, string rel, bool expected) => Assert.Equal(expected, X(pattern, rel));

    [Fact]
    public void Trailing_slash_matches_directories_only()
    {
        Assert.True(X("build/", "build", dir: true));
        Assert.False(X("build/", "build", dir: false));
    }

    [Fact]
    public void Home_and_absolute_patterns_use_the_full_path()
    {
        Assert.True(X("~/Library/Mobile Documents", "Library/Mobile Documents", true, "/Users/you/Library/Mobile Documents"));
        Assert.True(X("//System/Volumes/**", "Volumes/Data", true, "/System/Volumes/Data"));
        Assert.False(X("//System/Volumes/**", "System/Volumes/Data", true, "/scan/System/Volumes/Data"));
    }

    [Fact]
    public void Last_match_wins_and_negation_reincludes()
    {
        var m = new GlobMatcher(["*.log", "!keep.log"]);
        Assert.True(m.IsExcluded("a.log", "/a.log", false));
        Assert.False(m.IsExcluded("keep.log", "/keep.log", false));
    }
}
