using Hoard.Core.Model;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class TypesViewTests(Fixture f) : IClassFixture<Fixture>
{
    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Types };
        m.WaitForTypes();
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 110, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    private static List<int> WaitForList(BrowserModel m)
    {
        FileList list = m.TypeFiles!;
        list.Job.Wait();
        return list.Items(m.Store)!;
    }

    [Fact]
    public void Shows_the_stacked_bar_categories_and_top_extensions()
    {
        var (m, _) = Make();
        MemoryCanvas c = Render(m);
        string s = c.ToString();
        Assert.Contains("4 Types", c.Row(0));
        Assert.Contains("What is your disk made of?", c.Row(2));
        Assert.Contains("█", c.Row(4)); // the 100% bar
        Assert.Contains("Apps & binaries", c.Row(5)); // legend
        Assert.Contains("Categories", s);
        Assert.Contains("Top extensions", s);
        Assert.Contains(".bin", s);
        Assert.Contains("Code & text", s);
        Assert.Contains("list the 34 apps & binaries, biggest first", s);
    }

    [Fact]
    public void Enter_lists_a_categorys_files_biggest_first_and_opens_one_in_the_browser()
    {
        var (m, ctl) = Make();
        Render(m);
        ctl.Apply(Cmd.Open, 10); // Apps & binaries is the biggest category
        List<int> items = WaitForList(m);
        Assert.Equal("big/one.bin", m.Store.RelativePath(items[0]));
        Assert.Equal(34, items.Count);
        MemoryCanvas c = Render(m);
        Assert.Contains("Apps & binaries · biggest first", c.ToString());
        Assert.Contains("big/one.bin", c.ToString());

        ctl.Apply(Cmd.Down, 10);
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal(ViewKind.Browse, m.View);
        Assert.Equal("big", m.Store.Name(m.Current));
        Assert.Equal("two.bin", m.Store.Name(m.SelectedNode!.Value));
    }

    [Fact]
    public void E_groups_by_extension_and_back_closes_the_list()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar('e'), 10);
        Assert.True(m.TypesByExtension);
        MemoryCanvas c = Render(m);
        Assert.Contains("Extensions", c.ToString());
        Assert.Contains("by extension", c.Row(2));
        int txt = m.Types!.Extensions.FindIndex(e => e.Extension == "txt");
        for (int i = 0; i < txt; i++)
        {
            ctl.Apply(Cmd.Down, 10);
        }
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal(3, WaitForList(m).Count);
        Assert.Contains(".txt files", Render(m).ToString());
        ctl.Apply(Cmd.Left, 10);
        Assert.Null(m.TypeFiles);
        Assert.True(m.TypesByExtension);
    }

    [Fact]
    public void Files_in_the_list_can_be_marked()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Open, 10);
        int first = WaitForList(m)[0];
        ctl.Apply(Cmd.Mark, 10);
        Assert.True(m.IsMarked(first));
        Assert.Equal(1, m.TypeFiles!.Selected); // moves on, like the browser
        Assert.Contains("●", Render(m).ToString());
        ctl.Apply(Cmd.ClearOrUndo, 10);
        Assert.Empty(m.Marks);
    }

    [Fact]
    public void Clicks_select_rows_and_double_click_opens()
    {
        var (m, ctl) = Make();
        var c = new MemoryCanvas(110, 30);
        HitMap hits = Screens.Browser(c, m);
        var second = hits.Items.Single(i => i.Index == 1);
        ctl.Click(second.X0 + 4, second.Y, false, new Layout(110, 30), hits);
        Assert.Equal(1, m.TypesSelected);
        ctl.Click(second.X0 + 4, second.Y, true, new Layout(110, 30), hits);
        Assert.NotNull(m.TypeFiles);
    }

    [Fact]
    public void Tab_order_includes_types()
    {
        var (m, ctl) = Make();
        m.View = ViewKind.Map;
        ctl.Apply(Cmd.NextView, 10);
        Assert.Equal(ViewKind.Types, m.View);
        ctl.Apply(Cmd.NextView, 10);
        Assert.Equal(ViewKind.Age, m.View);
        ctl.Apply(BrowserController.FromChar('4'), 10);
        Assert.Equal(ViewKind.Types, m.View);
    }

    [Fact]
    public void Draws_at_every_size_in_every_mode_without_spilling_out()
    {
        var (m, ctl) = Make();
        for (int mode = 0; mode < 3; mode++)
        {
            if (mode == 1)
            {
                ctl.Apply(Cmd.Open, 10);
                WaitForList(m);
            }
            if (mode == 2)
            {
                ctl.Apply(Cmd.Left, 10);
                ctl.Apply(BrowserController.FromChar('e'), 10);
            }
            foreach (int w in new[] { 60, 75, 99, 100, 140 })
            {
                foreach (int h in new[] { 16, 23, 24, 40 })
                {
                    MemoryCanvas c = Render(m, w, h);
                    Assert.Contains("q quit", c.Row(h - 1));
                    for (int y = 3; y < h - 2; y++)
                    {
                        string row = c.Row(y).TrimEnd();
                        Assert.True(row.EndsWith('│') || row.EndsWith('┤'), $"mode {mode} {w}×{h} row {y} spills: {row}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("a/b/c/d/e/file.mov", 12, "a/b/…/file.mov")]
    [InlineData("short/file.mov", 30, "short/file.mov")]
    public void Long_paths_lose_their_middle(string path, int width, string expected)
    {
        string got = Screens.MiddleOut(path, width);
        Assert.True(Cells.Width(got) <= Math.Max(width, 14), got);
        Assert.EndsWith("file.mov", got);
        if (width >= 14) Assert.Equal(expected, got);
    }
}
