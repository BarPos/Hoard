using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class FuzzySearchTests
{
    [Theory]
    [InlineData("dev/monolith/node_modules", "nmod", true)]
    [InlineData("dev/monolith/node_modules", "xyz", false)]
    [InlineData("Movies/holiday.MOV", "mov", true)] // any case
    [InlineData("abc", "cba", false)]                 // letters in order
    public void Matches_letters_in_order(string path, string word, bool matches) =>
        Assert.Equal(matches, FuzzySearch.Score(path, word, path.LastIndexOf('/') + 1) >= 0);

    [Fact]
    public void Prefers_word_starts_runs_and_names()
    {
        int Score(string p, string w) => FuzzySearch.Score(p, w, p.LastIndexOf('/') + 1);
        Assert.True(Score("src/node_modules", "nm") > Score("src/gnomish", "nm"));          // segment starts
        Assert.True(Score("a/monolith", "mono") > Score("a/m_o_n_o", "mono"));               // a run
        Assert.True(Score("music/x.txt", "x") < Score("music/x", "x") || Score("deep/x", "x") > Score("x/deep", "x")); // name beats folder
    }

    [Fact]
    public void Every_word_must_match_and_positions_are_reported()
    {
        var at = new List<int>();
        Assert.True(FuzzySearch.ScoreAll("dev/monolith/node_modules", ["nmod", "mono"], 13, at) > 0);
        Assert.Contains(4, at); // the m of monolith
        Assert.Equal(-1, FuzzySearch.ScoreAll("dev/monolith/node_modules", ["nmod", "zzz"], 13));
    }

    [Fact]
    public void Searches_the_whole_scan_best_first_with_filters()
    {
        using var t = new TempTree();
        t.File("dev/monolith/node_modules/react/index.js", 50_000);
        t.File("dev/website/node_modules/x.js", 2_000);
        t.File("Movies/node-mod-notes.txt", 100);
        t.File("unrelated/file.bin", 10);
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        var s = r.Store;

        SearchResult all = FuzzySearch.Search(s, "nmod");
        Assert.True(all.Total >= 3);
        Assert.Equal("node_modules", s.Name(all.Hits[0].Node)); // a folder named that beats deeper matches

        SearchResult folders = FuzzySearch.Search(s, "nmod", SearchKind.Folders);
        Assert.All(folders.Hits, h => Assert.Equal(Hoard.Core.Model.NodeKind.Directory, s.Kind(h.Node)));
        SearchResult big = FuzzySearch.Search(s, "node", SearchKind.Files, minSize: 10_000);
        Assert.Equal(["dev/monolith/node_modules/react/index.js"], big.Hits.Select(h => s.RelativePath(h.Node)));
        Assert.Empty(FuzzySearch.Search(s, "").Hits);
        Assert.Equal(2, FuzzySearch.Search(s, "nmod", limit: 2).Hits.Count);
    }
}
