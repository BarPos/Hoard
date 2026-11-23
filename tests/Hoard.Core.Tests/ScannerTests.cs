using System.Globalization;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class ScannerTests
{
    private static readonly IFileProbe Probe = FileProbes.ForCurrentPlatform();

    private static ScanResult Scan(string root, ScanOptions? o = null, CancellationToken ct = default) =>
        new Scanner(Probe, o).Scan(root, ct);

    private static int Child(NodeStore s, int parent, string name) =>
        s.Children(parent).Single(id => s.Name(id) == name);

    [Fact]
    public void Counts_files_dirs_and_dotfiles()
    {
        using var t = new TempTree();
        t.File("a.txt", 1000);
        t.File(".hidden", 2000);
        t.File("sub/b.bin", 5000);
        t.File("sub/.git/config", 300);
        t.Dir("empty");

        ScanResult r = Scan(t.Root);
        NodeStore s = r.Store;

        Assert.Equal(4, s.Files(NodeStore.Root));
        Assert.Equal(3, s.Dirs(NodeStore.Root)); // sub, sub/.git, empty
        Assert.Equal(1000 + 2000 + 5000 + 300, s.Children(NodeStore.Root).Sum(id => SumApparentFiles(s, id)));
        Assert.True((s.Flags(Child(s, NodeStore.Root, ".hidden")) & NodeFlags.Hidden) != 0);
        Assert.Empty(r.Errors);
    }

    private static long SumApparentFiles(NodeStore s, int id) =>
        s.Kind(id) == NodeKind.File ? s.Apparent(id) : s.Children(id).Sum(c => SumApparentFiles(s, c));

    [Fact]
    public void Empty_root_finishes()
    {
        using var t = new TempTree();
        ScanResult r = Scan(t.Root, new ScanOptions { Threads = 8 });
        Assert.Equal(1, r.Store.Count);
        Assert.Equal(0, r.Store.Files(NodeStore.Root));
    }

    [Fact]
    public void Symlinked_root_is_resolved()
    {
        using var t = new TempTree();
        t.File("real/a.bin", 4000);
        string link = Path.Combine(Path.GetTempPath(), "hoard-link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateSymbolicLink(link, t.P("real"));
        try
        {
            ScanResult r = Scan(link);
            Assert.Equal(1, r.Store.Files(NodeStore.Root));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void Missing_root_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Scan("/definitely/not/here/" + Guid.NewGuid()));
    }

    [Fact]
    public void Hard_links_are_counted_once()
    {
        if (!Probe.IsExact) return;
        using var t = new TempTree();
        string original = t.File("x/big.bin", 200_000);
        t.Dir("y");
        TempTree.Run("ln", original, t.P("y/link1.bin"));
        TempTree.Run("ln", original, t.P("y/link2.bin"));

        ScanResult r = Scan(t.Root);
        NodeStore s = r.Store;

        long fileBytes = new FileInfo(original).Length;
        Assert.Equal(2, r.HardLinkDuplicates);
        Assert.InRange(s.Apparent(NodeStore.Root) - DirsApparent(s), fileBytes, fileBytes);
        Assert.Equal(3, s.Files(NodeStore.Root));
    }

    private static long DirsApparent(NodeStore s)
    {
        long total = s.Apparent(NodeStore.Root);
        long files = 0;
        for (int i = 1; i < s.Count; i++)
        {
            if (s.Kind(i) == NodeKind.File) files += s.Apparent(i);
        }
        return total - files;
    }

    [Fact]
    public void Symlinks_are_not_followed_even_in_a_loop()
    {
        using var t = new TempTree();
        t.File("real/data.bin", 50_000);
        File.CreateSymbolicLink(t.P("real/loop"), t.Root);
        File.CreateSymbolicLink(t.P("toreal"), t.P("real"));

        ScanResult r = Scan(t.Root);
        NodeStore s = r.Store;

        Assert.Equal(NodeKind.Symlink, s.Kind(Child(s, NodeStore.Root, "toreal")));
        Assert.Equal(-1, s.FirstChild(Child(s, NodeStore.Root, "toreal")));
        Assert.Equal(3, s.Files(NodeStore.Root)); // data.bin + 2 symlinks
    }

    [Fact]
    public void Unreadable_directory_is_reported_and_scan_completes()
    {
        if (Environment.UserName == "root") return; // root can read anything
        using var t = new TempTree();
        t.File("ok/fine.txt", 100);
        t.File("locked/secret.txt", 100);
        File.SetUnixFileMode(t.P("locked"), UnixFileMode.None);
        try
        {
            ScanResult r = Scan(t.Root);
            Assert.Single(r.Errors);
            Assert.EndsWith("locked", r.Errors[0].Path);
            Assert.True((r.Store.Flags(Child(r.Store, NodeStore.Root, "locked")) & NodeFlags.Error) != 0);
            Assert.Equal(1, r.Store.Files(NodeStore.Root));
        }
        finally
        {
            File.SetUnixFileMode(t.P("locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Sparse_file_uses_less_disk_than_its_length()
    {
        if (!Probe.IsExact) return;
        using var t = new TempTree();
        string path = t.P("sparse.img");
        using (var fs = new FileStream(path, FileMode.Create))
        {
            fs.Seek(200_000_000, SeekOrigin.Begin);
            fs.WriteByte(1);
        }
        ScanResult r = Scan(t.Root);
        int id = Child(r.Store, NodeStore.Root, "sparse.img");
        Assert.Equal(200_000_001, r.Store.Apparent(id));
        Assert.True(r.Store.Size(id) < 10_000_000, $"allocated {r.Store.Size(id)}");
    }

    [Fact]
    public void Unicode_names_survive()
    {
        using var t = new TempTree();
        t.File("zdjęcia/żółw 🐢.jpg", 1234);
        ScanResult r = Scan(t.Root);
        int dir = Child(r.Store, NodeStore.Root, "zdjęcia");
        Assert.Equal("żółw 🐢.jpg", r.Store.Name(r.Store.FirstChild(dir)));
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Excludes_skip_matching_entries()
    {
        using var t = new TempTree();
        t.File("app/node_modules/x/index.js", 10_000);
        t.File("app/src/main.js", 500);
        t.File("app/build/out.o", 7_000);
        ScanResult r = Scan(t.Root, new ScanOptions { Excludes = ["node_modules", "/app/build/"] });
        NodeStore s = r.Store;
        int app = Child(s, NodeStore.Root, "app");
        Assert.Equal(NodeKind.Excluded, s.Kind(Child(s, app, "node_modules")));
        Assert.Equal(NodeKind.Excluded, s.Kind(Child(s, app, "build")));
        Assert.Equal(1, s.Files(NodeStore.Root));
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        using var t = new TempTree();
        for (int i = 0; i < 200; i++) t.File($"d{i}/f.txt", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ScanResult r = Scan(t.Root, ct: cts.Token);
        Assert.True(r.Cancelled);
    }

    [Fact]
    public void Matches_du_on_a_generated_tree()
    {
        if (!Probe.IsExact) return;
        using var t = new TempTree();
        var rnd = new Random(42);
        for (int i = 0; i < 300; i++)
        {
            t.File($"d{i % 17}/e{i % 5}/f{i}.bin", rnd.Next(0, 40_000));
        }
        ScanResult r = Scan(t.Root);
        string du = TempTree.Output("du", "-sk", t.Root).Split('\t')[0];
        long duBytes = long.Parse(du, CultureInfo.InvariantCulture) * 1024;
        Assert.Equal(duBytes, r.Store.Size(NodeStore.Root));
    }

    [Fact]
    public void Matches_stat_for_a_real_file()
    {
        if (!OperatingSystem.IsMacOS() || !Probe.IsExact) return;
        using var t = new TempTree();
        string f = t.File("s.bin", 123_457);
        string[] st = TempTree.Output("stat", "-f", "%z %b %i %l %d %m %a", f).Trim().Split(' ');
        Assert.True(Probe.TryGetInfo(f, out EntryInfo info, out _));
        Assert.Equal(long.Parse(st[0], CultureInfo.InvariantCulture), info.Apparent);
        Assert.Equal(long.Parse(st[1], CultureInfo.InvariantCulture) * 512, info.Allocated);
        Assert.Equal(ulong.Parse(st[2], CultureInfo.InvariantCulture), info.Inode);
        Assert.Equal(uint.Parse(st[3], CultureInfo.InvariantCulture), info.LinkCount);
        Assert.Equal(ulong.Parse(st[4], CultureInfo.InvariantCulture), info.Device);
        Assert.Equal(long.Parse(st[5], CultureInfo.InvariantCulture), info.MTime);
        Assert.Equal(long.Parse(st[6], CultureInfo.InvariantCulture), info.ATime);
    }
}
