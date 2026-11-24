using Hoard.Core.Model;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class OverviewTests(Fixture f) : IClassFixture<Fixture>
{
    private static readonly DiskSpace TestDisk = new("test disk", "/", 1_000_000_000, 400_000_000);

    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => TestDisk) { View = ViewKind.Overview };
        m.WaitForJunk();
        m.WaitForTypes();
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 110, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void Shows_reclaimable_disk_biggest_entries_and_types()
    {
        var (m, _) = Make();
        MemoryCanvas c = Render(m);
        string s = c.ToString();
        Assert.Contains("1 Overview", c.Row(0));
        Assert.Contains("Reclaimable", c.Row(2));
        Assert.Contains("Disk · test disk", c.Row(2));
        Assert.Contains("Biggest in", s);
        Assert.Contains("By type", s);
        Assert.Contains("used now", s);
        Assert.Contains("60%", s); // 600 MB of 1 GB in use
        Assert.Contains("no junk", s); // no junk in this tree
        Assert.Contains("Big & old", s);
        Assert.Contains("1 empty folder", s); // the fixture's empty/
        Assert.Contains("Apps & binaries", s); // .bin files
        int firstEntry = Enumerable.Range(0, c.Height).First(y => c.Row(y).Contains("►", StringComparison.Ordinal));
        Assert.Contains("big/", c.Row(firstEntry));
    }

    [Fact]
    public void Enter_opens_the_highlighted_entry_in_the_browser()
    {
        var (m, ctl) = Make();
        Render(m);
        ctl.Apply(Cmd.Down, 10);
        Assert.Equal(1, m.OverviewSelected);
        ctl.Apply(Cmd.Up, 10);
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal(ViewKind.Browse, m.View);
        Assert.Equal("big", m.Store.Name(m.Current));
    }

    [Fact]
    public void Number_keys_and_tab_go_round_all_views()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar('2'), 10);
        Assert.Equal(ViewKind.Browse, m.View);
        ctl.Apply(BrowserController.FromChar('1'), 10);
        Assert.Equal(ViewKind.Overview, m.View);
        var seen = new List<ViewKind>();
        for (int i = 0; i < 10; i++)
        {
            ctl.Apply(Cmd.NextView, 10);
            seen.Add(m.View);
        }
        Assert.Equal([ViewKind.Browse, ViewKind.Map, ViewKind.Types, ViewKind.Age, ViewKind.Dupes, ViewKind.Junk, ViewKind.BigOld, ViewKind.Empty, ViewKind.Snaps, ViewKind.Overview], seen);
    }

    [Fact]
    public void Clicking_selects_and_double_click_opens()
    {
        var (m, ctl) = Make();
        var c = new MemoryCanvas(110, 30);
        HitMap hits = Screens.Browser(c, m);
        var second = hits.Items.Single(i => i.Index == 1);
        Assert.True(ctl.Click(second.X0 + 3, second.Y, false, new Layout(110, 30), hits));
        Assert.Equal(1, m.OverviewSelected);
        ctl.Click(second.X0 + 3, second.Y, true, new Layout(110, 30), hits);
        Assert.Equal(ViewKind.Browse, m.View);
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        var (m, _) = Make();
        foreach (int w in new[] { 60, 72, 89, 90, 100, 140, 200 })
        {
            foreach (int h in new[] { 16, 20, 24, 30, 50 })
            {
                MemoryCanvas c = Render(m, w, h);
                Assert.Contains("Biggest in", c.ToString());
                Assert.Contains("q quit", c.Row(h - 1));
                Assert.True(m.OverviewShown >= 1);
                for (int y = 3; y < h - 2; y++)
                {
                    string row = c.Row(y).TrimEnd();
                    Assert.True(row.EndsWith('│') || row.EndsWith('┤'), $"{w}×{h} row {y} spills over the border: {row}");
                }
            }
        }
    }

    [Fact]
    public void Unknown_disk_is_said_plainly()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Overview };
        Assert.Contains("Disk size unknown", Render(m).ToString());
    }
}
