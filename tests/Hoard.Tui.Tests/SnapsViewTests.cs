using Hoard.Core.Scanning;
using Hoard.Core.Snapshots;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class SnapsViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-snapsview-").FullName;
    private readonly string _store = Directory.CreateTempSubdirectory("hoard-snapstore-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_store, true); } catch { }
    }

    private void Write(string rel, int bytes)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
    }

    private BrowserModel Scan()
    {
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        session.Completion.GetAwaiter().GetResult();
        return new BrowserModel(session, null, null, _ => null) { View = ViewKind.Snaps, Snapshots = new SnapshotStore(_store) };
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void Without_snapshots_it_explains_what_will_happen()
    {
        Write("a/x.bin", 1_000);
        BrowserModel m = Scan();
        string s = Render(m).ToString();
        Assert.Contains("0 Snaps", Render(m).Row(0));
        Assert.Contains("now (this scan)", s);
        Assert.Contains("Nothing to compare with yet", s);
    }

    [Fact]
    public void P_saves_and_the_next_scan_shows_the_changes()
    {
        Write("work/a.bin", 10_000);
        Write("old/b.bin", 50_000);
        BrowserModel first = Scan();
        var ctl = new BrowserController(first, new FakeShell());
        ctl.Apply(BrowserController.FromChar('P'), 10);
        first.WaitForSnapshotSave();
        Assert.Contains("snapshot saved", first.Toast);

        Write("work/big.bin", 900_000);
        Directory.Delete(Path.Combine(_root, "old"), true);
        BrowserModel second = Scan();
        Assert.Equal(2, second.SnapRows.Count);
        second.WaitForSnapPair();
        List<FolderChange> changes = second.SnapChanges();
        Assert.Equal("work", changes[0].Name);
        Assert.True(changes.Single(c => c.Name == "old").Removed);
        MemoryCanvas c = Render(second);
        string s = c.ToString();
        Assert.Contains("What changed", c.Row(2));
        Assert.Contains("work/", s);
        Assert.Contains("gone", s);
        Assert.Contains("trend", s);

        var ctl2 = new BrowserController(second, new FakeShell());
        ctl2.Apply(Cmd.Right, 10);       // into the changes
        ctl2.Apply(Cmd.Open, 10);        // open work/
        Assert.Equal("work", second.SnapFolder);
        ctl2.Apply(Cmd.Back, 10);
        Assert.Equal("", second.SnapFolder);
        ctl2.Apply(BrowserController.FromChar('f'), 10);
        Assert.DoesNotContain(second.SnapChanges(), ch => ch.Delta < 0);
    }

    [Fact]
    public void B_sets_the_base_in_snaps()
    {
        Write("work/a.bin", 10_000);
        BrowserModel first = Scan();
        new BrowserController(first, new FakeShell()).Apply(BrowserController.FromChar('P'), 10);
        first.WaitForSnapshotSave();
        BrowserModel second = Scan();
        Assert.Equal(2, second.SnapRows.Count);
        var ctl = new BrowserController(second, new FakeShell());
        second.SnapSelected = 0;
        ctl.Apply(BrowserController.FromChar('b'), 10);
        Assert.Equal(0, second.SnapBase);
        Assert.Contains("same snapshot", second.Toast);
    }

    [Fact]
    public void Deleting_a_snapshot_takes_two_presses()
    {
        Write("a/x.bin", 1_000);
        BrowserModel m = Scan();
        m.SaveSnapshot();
        m.WaitForSnapshotSave();
        BrowserModel later = Scan();
        var ctl = new BrowserController(later, new FakeShell());
        ctl.Apply(Cmd.Down, 10);
        ctl.Apply(Cmd.Forget, 10);
        Assert.Contains("press del again", later.Toast);
        Assert.Equal(2, later.SnapRows.Count);
        ctl.Apply(Cmd.Forget, 10);
        Assert.Single(later.SnapRows);
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        Write("a/x.bin", 1_000);
        BrowserModel m = Scan();
        m.SaveSnapshot();
        m.WaitForSnapshotSave();
        Write("b/y.bin", 9_000);
        BrowserModel later = Scan();
        later.WaitForSnapPair();
        foreach (int w in new[] { 60, 89, 90, 120, 160 })
        {
            foreach (int h in new[] { 16, 20, 30 })
            {
                MemoryCanvas c = Render(later, w, h);
                Assert.Contains("q quit", c.Row(h - 1));
                for (int y = 3; y < h - 2; y++)
                {
                    string row = c.Row(y).TrimEnd();
                    Assert.True(row.EndsWith('│') || row.EndsWith('┤'), $"{w}×{h} row {y} spills: {row}");
                }
            }
        }
    }
}
