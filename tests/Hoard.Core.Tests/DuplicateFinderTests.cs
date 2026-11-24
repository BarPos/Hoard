using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class DuplicateFinderTests
{
    private static ScanResult Scan(TempTree t) => new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
    private static string PathOf(ScanResult r, int id) => Path.Combine(r.RootPath, r.Store.RelativePath(id));

    private static void Copy(TempTree t, string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(t.P(to))!);
        File.Copy(t.P(from), t.P(to));
    }

    [Fact]
    public void Finds_identical_files_and_ignores_same_size_different_content()
    {
        using var t = new TempTree();
        t.File("a/movie.mov", 300_000);
        Copy(t, "a/movie.mov", "b/movie copy.mov");
        Copy(t, "a/movie.mov", "c/again.mov");
        t.File("d/data.bin", 200_000);
        Copy(t, "d/data.bin", "e/data.bin");
        // Same size and same first 64 KB, different end: not a duplicate.
        byte[] bytes = File.ReadAllBytes(t.P("d/data.bin"));
        bytes[^1] ^= 0xFF;
        Directory.CreateDirectory(t.P("f"));
        File.WriteAllBytes(t.P("f/almost.bin"), bytes);
        t.File("g/small.txt", 500); // below the minimum size
        Copy(t, "g/small.txt", "h/small.txt");
        ScanResult r = Scan(t);
        var progress = new DupProgress();

        DupReport report = DuplicateFinder.Find(r.Store, id => PathOf(r, id), minSize: 100_000, progress: progress);

        Assert.Equal(2, report.Groups.Count);
        DupGroup first = report.Groups[0]; // 2 × 300 KB wasted beats 1 × 200 KB
        Assert.Equal(["a/movie.mov", "b/movie copy.mov", "c/again.mov"], first.Files.Select(r.Store.RelativePath));
        Assert.Equal(300_000, first.Size);
        Assert.Equal(["d/data.bin", "e/data.bin"], report.Groups[1].Files.Select(r.Store.RelativePath));
        Assert.Equal(32, first.Hash.Length); // 128-bit hex
        Assert.Equal(2, progress.Stage);
        Assert.Equal(progress.FilesTotal, progress.FilesDone);
    }

    [Fact]
    public void Copies_inside_installed_software_are_left_alone()
    {
        using var t = new TempTree();
        t.File("brew/Cellar/tex/1.0/fonts.map", 150_000);
        Copy(t, "brew/Cellar/tex/1.0/fonts.map", "brew/Cellar/tex/1.0/fonts-t1.map");
        Copy(t, "brew/Cellar/tex/1.0/fonts.map", "Tool.app/Contents/Resources/fonts.map");
        ScanResult r = Scan(t);
        Assert.Empty(DuplicateFinder.Find(r.Store, id => PathOf(r, id), minSize: 100_000).Groups);
    }

    [Fact]
    public void Hard_link_extras_are_not_duplicates()
    {
        using var t = new TempTree();
        t.File("one.bin", 150_000);
        TempTree.Run("ln", t.P("one.bin"), t.P("two.bin"));
        ScanResult r = Scan(t);
        Assert.Empty(DuplicateFinder.Find(r.Store, id => PathOf(r, id), minSize: 100_000).Groups);
    }

    [Fact]
    public void Apfs_clones_report_how_little_is_their_own()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var t = new TempTree();
        t.File("orig.bin", 2_000_000);
        t.File("plain.bin", 2_000_000);
        TempTree.Run("cp", "-c", t.P("orig.bin"), t.P("clone.bin"));
        ISharedBlocks blocks = SharedBlocks.ForCurrentPlatform();
        long? plain = blocks.PrivateBytes(t.P("plain.bin"));
        long? clone = blocks.PrivateBytes(t.P("clone.bin"));
        Assert.NotNull(plain);
        Assert.True(plain >= 2_000_000, $"plain file private {plain}");
        Assert.True(clone < 100_000, $"clone private {clone}");
        Assert.Null(blocks.PrivateBytes(t.P("missing.bin")));
    }

    [Fact]
    public void The_cache_means_unchanged_files_are_not_read_again()
    {
        using var t = new TempTree();
        t.File("a.bin", 300_000);
        Copy(t, "a.bin", "b.bin");
        ScanResult r = Scan(t);
        var cache = new HashCache(t.P("cache/hashes.tsv"));
        var first = new DupProgress();
        Assert.Single(DuplicateFinder.Find(r.Store, id => PathOf(r, id), minSize: 100_000, progress: first, cache: cache).Groups);
        Assert.True(File.Exists(t.P("cache/hashes.tsv")));

        // A fresh cache object reads the file back; hashes come from it, so results are the same.
        var again = new HashCache(t.P("cache/hashes.tsv"));
        Assert.NotNull(again.Get(PathOf(r, r.Store.Children(Hoard.Core.Model.NodeStore.Root).First()), 300_000,
            r.Store.MTime(r.Store.Children(Hoard.Core.Model.NodeStore.Root).First()), full: true));
        Assert.Single(DuplicateFinder.Find(r.Store, id => PathOf(r, id), minSize: 100_000, cache: again).Groups);
        // A different size or time means the entry doesn't count.
        Assert.Null(again.Get(PathOf(r, r.Store.Children(Hoard.Core.Model.NodeStore.Root).First()), 300_001, 0, full: true));
    }

    [Fact]
    public void Byte_compare_catches_a_file_that_changed()
    {
        using var t = new TempTree();
        t.File("a.bin", 2_500_000);
        Copy(t, "a.bin", "b.bin");
        Assert.True(DuplicateFinder.SameBytes(t.P("a.bin"), t.P("b.bin")));
        byte[] b = File.ReadAllBytes(t.P("b.bin"));
        b[2_000_000] ^= 1;
        File.WriteAllBytes(t.P("b.bin"), b);
        Assert.False(DuplicateFinder.SameBytes(t.P("a.bin"), t.P("b.bin")));
        Assert.False(DuplicateFinder.SameBytes(t.P("a.bin"), t.P("missing.bin")));
    }
}
