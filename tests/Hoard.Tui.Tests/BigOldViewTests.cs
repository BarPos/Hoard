using Hoard.Core.Analysis;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class BigOldViewTests(Fixture f) : IClassFixture<Fixture>
{
    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null)
        {
            View = ViewKind.BigOld,
            BigOldOptions = new BigOldOptions(50_000, 182, AgeAxis.Modified),
        };
        m.WaitForBigOld();
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void Lists_big_old_files_with_filters_and_details()
    {
        var (m, _) = Make();
        MemoryCanvas c = Render(m);
        string s = c.ToString();
        Assert.Contains("Big+Old", c.Row(0));
        Assert.Contains("Big & old", c.Row(2));
        Assert.Contains("2 files", c.Row(2));
        Assert.Contains("[size ≥ 50.0 KB]", c.Row(3));
        Assert.Contains("[not modified for 6 months+]", c.Row(3));
        // 100 KB × 800 days beats 300 KB × 200 days
        Assert.Equal(["big/three.bin", "big/two.bin"], m.BigOldItems().Select(m.Store.RelativePath));
        int first = Enumerable.Range(0, c.Height).First(y => c.Row(y).Contains('►'));
        Assert.Contains("three.bin", c.Row(first));
        Assert.Contains("Selected file", s);
        Assert.Contains("o reveal in Finder", s);
    }

    [Fact]
    public void Filters_change_with_the_panel()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar('f'), 10);
        Assert.True(m.FilterOpen);
        Assert.Contains("Filters", Render(m).ToString());
        ctl.Apply(Cmd.Down, 10);   // untouched for
        ctl.Apply(Cmd.Right, 10);  // 6 months → 1 year
        Assert.Equal(365, m.BigOldOptions.MinAgeDays);
        ctl.Apply(Cmd.Down, 10);
        ctl.Apply(Cmd.Right, 10);  // modified → opened
        Assert.Equal(AgeAxis.Accessed, m.BigOldOptions.Axis);
        ctl.Apply(Cmd.Escape, 10);
        Assert.False(m.FilterOpen);
        m.WaitForBigOld();
        Assert.Equal(365, m.BigOld!.Options.MinAgeDays);
        Assert.Contains("not opened for 1 year+", Render(m).Row(3));
    }

    [Fact]
    public void Sort_mark_and_jump_to_the_browser()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.SortNext, 10); // by size
        Assert.Equal(BigOldSort.Size, m.BigOldSort);
        Assert.Equal("big/two.bin", m.Store.RelativePath(m.BigOldItems()[0]));
        ctl.Apply(Cmd.Mark, 10);
        Assert.True(m.IsMarked(m.BigOldItems()[0]));
        Assert.Contains("●", Render(m).ToString());
        ctl.Apply(Cmd.Up, 10);
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal(ViewKind.Browse, m.View);
        Assert.Equal("two.bin", m.Store.Name(m.SelectedNode!.Value));
    }

    [Fact]
    public void Nothing_found_says_how_to_loosen_the_filters()
    {
        var (m, _) = Make();
        m.BigOldOptions = new BigOldOptions(5_000_000_000, 365, AgeAxis.Modified);
        m.WaitForBigOld();
        Assert.Contains("f loosens the filters", Render(m).ToString());
    }

    [Fact]
    public void Overview_counts_big_and_old()
    {
        var (m, _) = Make();
        m.WaitForJunk();
        m.View = ViewKind.Overview;
        string s = Render(m, 110, 30).ToString();
        Assert.Contains("Big & old", s);
        Assert.Contains("2 files ≥ 50.0 KB, untouched 6 months+", s);
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        var (m, ctl) = Make();
        foreach (bool panel in new[] { false, true })
        {
            m.FilterOpen = panel;
            foreach (int w in new[] { 60, 80, 100, 140 })
            {
                foreach (int h in new[] { 16, 20, 30 })
                {
                    MemoryCanvas c = Render(m, w, h);
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
}
