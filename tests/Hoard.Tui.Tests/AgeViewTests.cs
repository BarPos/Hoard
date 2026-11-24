using Hoard.Core.Analysis;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class AgeViewTests(Fixture f) : IClassFixture<Fixture>
{
    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Age };
        m.WaitForAge();
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void Shows_the_heatmap_of_the_open_folder()
    {
        var (m, _) = Make();
        MemoryCanvas c = Render(m);
        string s = c.ToString();
        Assert.Contains("5 Age", c.Row(0));
        Assert.Contains("Age heatmap", c.Row(2));
        Assert.Contains("by last modified", c.Row(2));
        Assert.Contains("< 1w", s);
        Assert.Contains("3y +", s);
        Assert.Contains("STALE > 1 YEAR", s);
        Assert.Contains("big/", s);
        Assert.Contains("hasn't been modified for over a year", s);
        AgeRow big = m.Age!.Rows[0];
        Assert.Equal("big", m.Store.Name(big.Node));
        Assert.True(big.Bytes[0] > 0 && big.Bytes[4] > 0 && big.Bytes[6] > 0); // 2, 200 and 800 days old
    }

    [Fact]
    public void Enter_on_a_cell_lists_its_files_and_on_the_name_opens_the_folder()
    {
        var (m, ctl) = Make();
        Assert.Equal(AgeBuckets.Count - 1, m.AgeColumn);
        ctl.Apply(Cmd.Left, 10); // < 3y: big/three.bin (800 days)
        ctl.Apply(Cmd.Open, 10);
        Assert.NotNull(m.AgeFiles);
        m.AgeFiles!.Job.Wait();
        Assert.Equal(["big/three.bin"], m.AgeFiles.Items(m.Store)!.Select(m.Store.RelativePath));
        Assert.Contains("modified 2–3 years ago", Render(m).Row(2));
        ctl.Apply(Cmd.Escape, 10);
        Assert.Null(m.AgeFiles);

        for (int i = 0; i < 10; i++)
        {
            ctl.Apply(Cmd.Left, 10);
        }
        Assert.Equal(-1, m.AgeColumn);
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal("big", m.Store.Name(m.Current));
        Assert.Equal(ViewKind.Age, m.View);
        m.WaitForAge();
        Assert.Contains("one.bin", Render(m).ToString());
        ctl.Apply(Cmd.Back, 10);
        Assert.Equal(Hoard.Core.Model.NodeStore.Root, m.Current);
    }

    [Fact]
    public void Axis_and_row_count_can_change()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar('a'), 10);
        Assert.Equal(AgeAxis.Accessed, m.AgeAxis);
        m.WaitForAge();
        Assert.Equal(AgeAxis.Accessed, m.Age!.Axis);
        Assert.Contains("by last access", Render(m).Row(2));
        ctl.Apply(BrowserController.FromChar('m'), 10);
        Assert.Equal(AgeAxis.Modified, m.AgeAxis);
        ctl.Apply(Cmd.DepthDown, 10);
        Assert.Equal(6, m.AgeRowLimit);
        m.WaitForAge();
        Assert.Equal(6, m.Age!.Rows.Count);
        Assert.Contains("+ 1 smaller entry ·", Render(m).ToString());
    }

    [Fact]
    public void Cells_are_clickable()
    {
        var (m, ctl) = Make();
        var c = new MemoryCanvas(120, 30);
        HitMap hits = Screens.Browser(c, m);
        var cell = hits.Items.Single(i => i.Index == Screens.AgeHit(0, 0));
        ctl.Click(cell.X0 + 1, cell.Y, false, new Layout(120, 30), hits);
        Assert.Equal((0, 0), (m.AgeSelected, m.AgeColumn));
        ctl.Click(cell.X0 + 1, cell.Y, true, new Layout(120, 30), hits);
        Assert.NotNull(m.AgeFiles);
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        var (m, _) = Make();
        foreach (int w in new[] { 60, 70, 80, 99, 120, 160 })
        {
            foreach (int h in new[] { 16, 20, 24, 40 })
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

    [Theory]
    [InlineData(18_200_000_000, 5, "18.2G")]
    [InlineData(18_200_000_000, 3, "18G")]
    [InlineData(338_000_000, 5, "338M")]
    [InlineData(0, 5, "·")]
    public void Cell_sizes_are_compact(long bytes, int width, string expected) =>
        Assert.Equal(expected, Screens.ShortSize(bytes, width));
}
