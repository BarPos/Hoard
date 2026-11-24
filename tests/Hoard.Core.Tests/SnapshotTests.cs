using Hoard.Core.Scanning;
using Hoard.Core.Snapshots;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class SnapshotTests
{
    private static Snapshot Take(TempTree t, DateTimeOffset at)
    {
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        return Snapshot.FromStore(r.Store, r.RootPath, at);
    }

    [Fact]
    public void Saves_and_loads_folder_totals()
    {
        using var t = new TempTree();
        t.File("a/one.bin", 50_000);
        t.File("a/b/two.bin", 20_000);
        t.File("ünïcode/三.txt", 1_000);
        Snapshot s = Take(t, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        string file = t.P("snaps/x.hsnap");
        s.Save(file);

        Snapshot back = Snapshot.Load(file);
        Assert.Equal(s.RootPath, back.RootPath);
        Assert.Equal(s.TakenAt, back.TakenAt);
        Assert.Equal(s.Folders.Count, back.Folders.Count);
        foreach (var (path, totals) in s.Folders)
        {
            Assert.Equal(totals, back.Folders[path]);
        }
        Assert.Contains("a/b", back.Folders.Keys);
        Assert.Contains("ünïcode", back.Folders.Keys);
        Snapshot header = Snapshot.Load(file, headerOnly: true);
        Assert.Equal(s.Root, header.Root);
        Assert.Single(header.Folders);
    }

    [Fact]
    public void Diff_shows_what_grew_shrank_appeared_and_went()
    {
        using var t = new TempTree();
        t.File("grows/a.bin", 10_000);
        t.File("shrinks/a.bin", 90_000);
        t.File("goes/a.bin", 30_000);
        t.File("same/a.bin", 5_000);
        Snapshot before = Take(t, DateTimeOffset.Now.AddDays(-1));
        t.File("grows/b.bin", 400_000);
        File.Delete(t.P("shrinks/a.bin"));
        t.File("shrinks/small.bin", 1_000);
        Directory.Delete(t.P("goes"), true);
        t.File("new/n.bin", 70_000);
        Snapshot after = Take(t, DateTimeOffset.Now);

        List<FolderChange> changes = SnapshotDiff.Children(before, after, "");
        Assert.Equal("grows", changes[0].Name);
        Assert.True(changes[0].Delta > 390_000);
        Assert.True(changes.Single(c => c.Name == "shrinks").Delta < 0);
        Assert.True(changes.Single(c => c.Name == "new").Added);
        Assert.True(changes.Single(c => c.Name == "goes").Removed);
        Assert.DoesNotContain(changes, c => c.Name == "same");
        Assert.Equal(["b.bin"], Directory.GetFiles(t.P("grows")).Select(Path.GetFileName).Where(n => n == "b.bin"));
    }

    [Fact]
    public void The_store_lists_per_folder_and_keeps_only_the_newest()
    {
        using var t = new TempTree();
        t.File("x.bin", 1_000);
        var store = new SnapshotStore(t.P("store"), keep: 2);
        for (int i = 0; i < 3; i++)
        {
            store.Save(Take(t, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + i * 3600)));
        }
        List<Snapshot> list = store.List(t.Root);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].TakenAt > list[1].TakenAt);
        Assert.Empty(store.List("/some/other/folder"));
        store.Delete(list[0]);
        Assert.Single(store.List(t.Root));
        File.WriteAllText(Path.Combine(store.Directory, SnapshotStore.RootTag(t.Root) + "-broken.hsnap"), "nope");
        Assert.Single(store.List(t.Root)); // damaged files are skipped
    }
}
