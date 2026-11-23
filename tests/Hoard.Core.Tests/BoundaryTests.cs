using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

/// <summary>Wraps the real probe and pretends some paths live on another device or share an inode.</summary>
internal sealed class FakeProbe(IFileProbe inner) : IFileProbe
{
    public Dictionary<string, ulong> DeviceOverride { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (ulong Dev, ulong Ino)> IdentityOverride { get; } = new(StringComparer.Ordinal);

    public string Name => "fake(" + inner.Name + ")";
    public bool IsExact => inner.IsExact;

    public bool TryGetInfo(string path, out EntryInfo info, out int errno)
    {
        if (!inner.TryGetInfo(path, out info, out errno))
        {
            return false;
        }
        if (DeviceOverride.TryGetValue(path, out ulong dev))
        {
            info = info with { Device = dev };
        }
        if (IdentityOverride.TryGetValue(path, out var id))
        {
            info = info with { Device = id.Dev, Inode = id.Ino };
        }
        return true;
    }
}

public class BoundaryTests
{
    private static readonly IFileProbe Real = FileProbes.ForCurrentPlatform();

    private static int Child(NodeStore s, int parent, string name) =>
        s.Children(parent).Single(id => s.Name(id) == name);

    [Fact]
    public void Other_file_system_is_a_mount_point_and_not_entered()
    {
        if (!Real.IsExact) return;
        using var t = new TempTree();
        t.File("local/a.bin", 10_000);
        t.File("mnt/b.bin", 50_000);
        var probe = new FakeProbe(Real);
        probe.DeviceOverride[t.P("mnt")] = 999_999;

        ScanResult r = new Scanner(probe).Scan(t.Root);
        int mnt = Child(r.Store, NodeStore.Root, "mnt");
        Assert.Equal(NodeKind.MountPoint, r.Store.Kind(mnt));
        Assert.Equal(1, r.Store.Files(NodeStore.Root));

        ScanResult crossed = new Scanner(probe, new ScanOptions { CrossFileSystems = true }).Scan(t.Root);
        Assert.Equal(2, crossed.Store.Files(NodeStore.Root));
    }

    [Fact]
    public void Same_directory_reached_twice_is_counted_once()
    {
        if (!Real.IsExact) return;
        using var t = new TempTree();
        t.File("one/data.bin", 30_000);
        t.File("two/data.bin", 30_000);
        Assert.True(Real.TryGetInfo(t.P("one"), out EntryInfo one, out _));
        var probe = new FakeProbe(Real);
        probe.IdentityOverride[t.P("two")] = (one.Device, one.Inode); // like a firmlink or bind mount

        ScanResult r = new Scanner(probe).Scan(t.Root);
        var kinds = new[] { "one", "two" }.Select(n => r.Store.Kind(Child(r.Store, NodeStore.Root, n))).ToList();
        Assert.Single(kinds, NodeKind.Alias);
        Assert.Single(kinds, NodeKind.Directory);
        Assert.Equal(1, r.Store.Files(NodeStore.Root));
    }

    [Fact]
    public void Listed_alias_paths_are_skipped_deterministically()
    {
        using var t = new TempTree();
        t.File("visible/a.bin", 10_000);
        t.File("Data/visible/a.bin", 10_000);
        var opts = new ScanOptions { AliasPaths = [t.P("Data/visible")] };
        for (int run = 0; run < 3; run++)
        {
            ScanResult r = new Scanner(Real, opts).Scan(t.Root);
            int data = Child(r.Store, NodeStore.Root, "Data");
            Assert.Equal(NodeKind.Alias, r.Store.Kind(Child(r.Store, data, "visible")));
            Assert.Equal(NodeKind.Directory, r.Store.Kind(Child(r.Store, NodeStore.Root, "visible")));
            Assert.Equal(1, r.Store.Files(NodeStore.Root));
        }
    }

    [Fact]
    public void Hard_link_is_charged_to_the_alphabetically_first_path()
    {
        if (!Real.IsExact) return;
        using var t = new TempTree();
        string original = t.File("zeta/f.bin", 100_000);
        t.Dir("alpha");
        t.Dir("mid");
        TempTree.Run("ln", original, t.P("mid/f.bin"));
        TempTree.Run("ln", original, t.P("alpha/f.bin"));

        for (int run = 0; run < 5; run++)
        {
            ScanResult r = new Scanner(Real, new ScanOptions { Threads = 8 }).Scan(t.Root);
            NodeStore s = r.Store;
            Assert.True(s.Size(Child(s, NodeStore.Root, "alpha")) > s.Size(Child(s, NodeStore.Root, "zeta")));
            int alphaFile = Child(s, Child(s, NodeStore.Root, "alpha"), "f.bin");
            Assert.True((s.Flags(alphaFile) & NodeFlags.HardLinkPrimary) != 0);
            Assert.Equal(2, r.HardLinkDuplicates);
        }
    }

    [Fact]
    public void Firmlink_list_only_yields_aliases_whose_twin_is_inside_the_scan()
    {
        using var t = new TempTree();
        string list = t.P("firmlinks");
        File.WriteAllLines(list, ["/Users\tUsers", "/usr/local\tusr/local", "bogus line", "/Library\tLibrary"]);

        Assert.Equal(
            ["/System/Volumes/Data/Users", "/System/Volumes/Data/usr/local", "/System/Volumes/Data/Library"],
            MacFirmlinks.AliasesFor("/", list));
        Assert.Empty(MacFirmlinks.AliasesFor("/System/Volumes", list));    // /Users itself isn't scanned
        Assert.Empty(MacFirmlinks.AliasesFor("/usr", list));               // the Data side isn't scanned
        Assert.Empty(MacFirmlinks.AliasesFor("/", t.P("missing-file")));
    }
}

public class PortableProbeTests
{
    [Fact]
    public void Reports_apparent_sizes_and_kinds()
    {
        using var t = new TempTree();
        string f = t.File("d/file.bin", 12_345);
        File.CreateSymbolicLink(t.P("link"), t.P("d"));
        var p = new PortableProbe();

        Assert.False(p.IsExact);
        Assert.True(p.TryGetInfo(f, out EntryInfo fi, out _));
        Assert.Equal(EntryType.File, fi.Type);
        Assert.Equal(12_345, fi.Apparent);
        Assert.Equal(12_345, fi.Allocated);
        Assert.True(p.TryGetInfo(t.P("d"), out EntryInfo di, out _));
        Assert.Equal(EntryType.Directory, di.Type);
        Assert.True(p.TryGetInfo(t.P("link"), out EntryInfo li, out _));
        Assert.Equal(EntryType.Symlink, li.Type);
        Assert.False(p.TryGetInfo(t.P("nope"), out _, out int errno));
        Assert.Equal(2, errno);
    }

    [Fact]
    public void Scanner_works_with_it_and_counts_every_path()
    {
        using var t = new TempTree();
        string original = t.File("a/f.bin", 1000);
        t.Dir("b");
        TempTree.Run("ln", original, t.P("b/f.bin"));
        File.CreateSymbolicLink(t.P("loop"), t.Root);

        ScanResult r = new Scanner(new PortableProbe()).Scan(t.Root);
        Assert.False(r.ExactSizes);
        Assert.Equal(2000, r.Store.Apparent(NodeStore.Root)); // no inode info: both links counted
        Assert.Equal(0, r.HardLinkDuplicates);
        Assert.Equal(3, r.Store.Files(NodeStore.Root));        // 2 files + the symlink
    }
}

public class LiveTotalsTests
{
    /// <summary>Blocks lookups inside one folder until released.</summary>
    private sealed class GatedProbe(IFileProbe inner, string gated) : IFileProbe
    {
        public ManualResetEventSlim Gate { get; } = new(false);
        public string Name => inner.Name;
        public bool IsExact => inner.IsExact;
        public bool TryGetInfo(string path, out EntryInfo info, out int errno)
        {
            if (path.StartsWith(gated + "/", StringComparison.Ordinal)) Gate.Wait(TimeSpan.FromSeconds(10));
            return inner.TryGetInfo(path, out info, out errno);
        }
    }

    [Fact]
    public async Task Totals_grow_while_scanning_and_match_at_the_end()
    {
        using var t = new TempTree();
        t.File("done/a.bin", 40_000);
        t.File("held/b.bin", 90_000);
        t.File("held/deeper/c.bin", 10_000);
        var probe = new GatedProbe(FileProbes.ForCurrentPlatform(), t.P("held"));
        ScanSession session = new Scanner(probe).Start(t.Root);
        NodeStore s = session.Store;

        int Find(string name) => s.Children(NodeStore.Root).FirstOrDefault(id => s.Name(id) == name, -1);
        Assert.True(SpinWait.SpinUntil(() => Find("done") >= 0 && (s.Flags(Find("done")) & NodeFlags.Pending) == 0, 10_000));
        int held = Find("held");
        Assert.True((s.Flags(held) & NodeFlags.Pending) != 0);
        Assert.Empty(s.Children(held));          // not published yet
        Assert.Equal(1, s.Files(NodeStore.Root)); // only done/a.bin so far
        Assert.False(session.IsComplete);
        Assert.True(s.IsGrowing(NodeStore.Root));
        Assert.True(s.IsGrowing(held));
        Assert.False(s.IsGrowing(Find("done")));

        probe.Gate.Set();
        ScanResult r = await session.Completion;
        Assert.Equal(3, s.Files(NodeStore.Root));
        Assert.Equal(2, s.Files(held));
        Assert.False(s.IsGrowing(NodeStore.Root));
        Assert.All(Enumerable.Range(0, s.Count), id => Assert.Equal(0, s.Incomplete(id)));
        Assert.Equal(NodeFlags.None, s.Flags(held) & NodeFlags.Pending);
        string du = TempTree.Output("du", "-sk", t.Root).Split('\t')[0];
        if (probe.IsExact)
        {
            Assert.Equal(long.Parse(du, System.Globalization.CultureInfo.InvariantCulture) * 1024, r.Store.Size(NodeStore.Root));
        }
    }
}
