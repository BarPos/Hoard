using Hoard.Core.Model;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class TreemapLayoutTests
{
    private static List<MapBlock> Flat(IReadOnlyList<long> sizes, Cell area, double minCells = 0) =>
        Treemap.Layout(Enumerable.Range(0, sizes.Count), i => sizes[i], _ => [], area, 1);

    [Fact]
    public void Blocks_tile_the_area_without_overlap()
    {
        long[] sizes = [500, 300, 120, 80, 60, 40, 30, 20, 10, 5];
        var area = new Cell(2, 3, 96, 20);
        List<MapBlock> blocks = Flat(sizes, area);
        var seen = new HashSet<(int, int)>();
        foreach (MapBlock b in blocks)
        {
            for (int x = b.Rect.X; x < b.Rect.X + b.Rect.W; x++)
            for (int y = b.Rect.Y; y < b.Rect.Y + b.Rect.H; y++)
            {
                Assert.True(area.Contains(x, y), $"cell ({x},{y}) outside the area");
                Assert.True(seen.Add((x, y)), $"cell ({x},{y}) covered twice");
            }
        }
        Assert.Equal(area.W * area.H, seen.Count); // no holes either
    }

    [Fact]
    public void Areas_are_roughly_proportional_and_biggest_first()
    {
        long[] sizes = [600, 300, 100];
        List<MapBlock> blocks = Flat(sizes, new Cell(0, 0, 100, 30));
        Assert.Equal([0, 1, 2], blocks.Select(b => b.Node));
        double total = 100 * 30;
        for (int i = 0; i < 3; i++)
        {
            double share = blocks[i].Rect.W * blocks[i].Rect.H / total;
            Assert.InRange(share, sizes[i] / 1000.0 - 0.04, sizes[i] / 1000.0 + 0.04);
        }
    }

    [Fact]
    public void Tiny_items_are_merged_into_one_group()
    {
        var sizes = new List<long> { 1_000_000 };
        sizes.AddRange(Enumerable.Repeat(10L, 500));
        List<MapBlock> blocks = Flat(sizes, new Cell(0, 0, 80, 20));
        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[1].IsGroup);
        Assert.Equal(500, blocks[1].GroupCount);
    }

    [Fact]
    public void Zero_sized_entries_are_left_out()
    {
        Assert.Single(Flat([100, 0, 0], new Cell(0, 0, 40, 10)));
        Assert.Empty(Flat([0, 0], new Cell(0, 0, 40, 10)));
    }

    [Fact]
    public void Neighbour_moves_in_the_asked_direction()
    {
        // Left half one block, right half split top/bottom.
        var left = new MapBlock(1, 10, new Cell(0, 0, 10, 10), 0, 1, []);
        var rightTop = new MapBlock(2, 5, new Cell(10, 0, 10, 5), 0, 1, []);
        var rightBottom = new MapBlock(3, 5, new Cell(10, 5, 10, 5), 0, 1, []);
        List<MapBlock> blocks = [left, rightTop, rightBottom];
        Assert.Equal(1, Treemap.Neighbour(blocks, 0, 1, 0) is int a ? blocks[a].Node - 1 : -1);
        Assert.Equal(2, Treemap.Neighbour(blocks, 1, 0, 1));
        Assert.Equal(0, Treemap.Neighbour(blocks, 2, -1, 0));
        Assert.Null(Treemap.Neighbour(blocks, 0, -1, 0));
    }
}

public class MapViewTests(Fixture f) : IClassFixture<Fixture>
{
    private static (MemoryCanvas Canvas, HitMap Hits) Render(BrowserModel m, int w = 100, int h = 26)
    {
        var c = new MemoryCanvas(w, h);
        HitMap hits = Screens.Browser(c, m);
        return (c, hits);
    }

    [Fact]
    public void Tab_switches_between_list_and_map()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        ctl.Apply(Cmd.NextView, 10);
        Assert.Equal(ViewKind.Map, m.View);
        var (c, hits) = Render(m);
        Assert.Contains("Treemap ·", c.Row(2));
        Assert.Contains(" 3 Map ", c.Row(0));
        Assert.NotEmpty(hits.Blocks);
        ctl.Apply(Cmd.ViewBrowse, 10);
        Assert.Equal(ViewKind.Browse, m.View);
        ctl.Apply(Cmd.ViewMap, 10);
        Assert.Equal(ViewKind.Map, m.View);
    }

    [Fact]
    public void Map_shows_the_biggest_folder_with_its_children_and_the_selection()
    {
        BrowserModel m = f.Model();
        m.View = ViewKind.Map;
        var (c, hits) = Render(m, 120, 30);
        string screen = c.ToString();
        Assert.Contains("► big", screen);             // selected parent header
        Assert.Contains("one.bin", screen);           // a child drawn inside it
        Assert.Contains("► ", c.Row(30 - 3));         // info line
        Assert.Contains("↵ zoom in", c.Row(30 - 3));
        MapBlock big = hits.Blocks.First();
        Assert.Equal("big", m.Store.Name(big.Node));
        Assert.NotEmpty(big.Children);
        for (int y = 0; y < 30; y++)
        {
            Assert.Equal(120, Cells.Width(c.Row(y)));
        }
    }

    [Fact]
    public void Arrows_move_between_blocks_and_enter_zooms()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell()) { };
        m.View = ViewKind.Map;
        ctl.LastHits = Render(m).Hits;
        int start = m.SelectedNode!.Value;
        bool moved = false;
        foreach (Cmd dir in new[] { Cmd.Right, Cmd.Down, Cmd.Left, Cmd.Up })
        {
            ctl.Apply(dir, 10);
            ctl.LastHits = Render(m).Hits;
            moved |= m.SelectedNode != start;
        }
        Assert.True(moved);

        m.SelectNode(m.Rows.First(id => m.Store.Name(id) == "big"));
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal("big", m.Store.Name(m.Current));
        Assert.Contains("Treemap · big", Render(m).Canvas.Row(2));
        ctl.Apply(Cmd.Back, 10);
        Assert.Equal(NodeStore.Root, m.Current);
    }

    [Fact]
    public void Depth_changes_only_in_the_map_and_is_clamped()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        ctl.Apply(Cmd.DepthUp, 10);
        Assert.Equal(2, m.MapDepth);
        Assert.Contains("press 3", m.Toast);
        m.View = ViewKind.Map;
        ctl.Apply(Cmd.DepthUp, 10);
        Assert.Equal(3, m.MapDepth);
        ctl.Apply(Cmd.DepthUp, 10);
        Assert.Equal(3, m.MapDepth);
        for (int i = 0; i < 5; i++) ctl.Apply(Cmd.DepthDown, 10);
        Assert.Equal(1, m.MapDepth);
        Assert.All(Render(m).Hits.Blocks, b => Assert.Empty(b.Children));
    }

    [Fact]
    public void Mouse_selects_and_double_click_zooms_tabs_switch()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        m.View = ViewKind.Map;
        var layout = new Layout(100, 26);
        HitMap hits = Render(m).Hits;
        MapBlock mid = hits.Blocks.First(b => !b.IsGroup && m.Store.Name(b.Node) == "mid");
        ctl.Click(mid.Rect.X, mid.Rect.Y, false, layout, hits);
        Assert.Equal("mid", m.Store.Name(m.SelectedNode!.Value));
        ctl.Click(mid.Rect.X, mid.Rect.Y, true, layout, hits);
        Assert.Equal("mid", m.Store.Name(m.Current));

        hits = Render(m).Hits;
        var (x0, _, _) = hits.Tabs.Single(tab => tab.View == ViewKind.Browse);
        ctl.Click(x0, 0, false, layout, hits);
        Assert.Equal(ViewKind.Browse, m.View);
    }

    [Fact]
    public void Blocks_are_filled_with_their_colours()
    {
        BrowserModel m = f.Model();
        m.View = ViewKind.Map;
        var (c, hits) = Render(m, 120, 30);
        var bgs = new HashSet<Rgb>();
        foreach (MapBlock b in hits.Blocks.Where(b => !b.IsGroup))
        {
            Rgb inside = c.StyleAt(b.Rect.X, b.Rect.Y + Math.Min(1, b.Rect.H - 1)).Bg;
            Assert.NotEqual(m.Theme.Bg, inside);
            bgs.Add(inside);
        }
        Assert.True(bgs.Count >= Math.Min(3, hits.Blocks.Count(b => !b.IsGroup)));
    }

    [Fact]
    public void Map_works_at_small_and_large_sizes()
    {
        BrowserModel m = f.Model();
        m.View = ViewKind.Map;
        foreach (var (w, h) in new[] { (60, 16), (80, 24), (160, 48) })
        {
            var (c, _) = Render(m, w, h);
            Assert.Contains("Treemap", c.Row(2));
            for (int y = 0; y < h; y++)
            {
                Assert.Equal(w, Cells.Width(c.Row(y)));
            }
        }
        Assert.Contains("make the window", Render(m, 50, 12).Canvas.ToString());
    }
}
