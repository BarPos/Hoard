using System.Globalization;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    // Categorical colours for top-level blocks (plan.md §7.3), as in examples/05-treemap.sh.
    private static readonly Rgb[] MapColours = [.. new[] { 214, 39, 170, 114, 209, 75, 141, 68, 180, 110, 167, 150 }.Select(Rgb.Xterm)];
    private static readonly Rgb[] MapGreys = [.. new[] { 252, 247, 243, 250, 245, 241, 249, 244 }.Select(Rgb.Xterm)];
    private static readonly Rgb Black = Rgb.Hex(0x000000), White = Rgb.Hex(0xffffff);

    private static Rgb Contrast(Rgb bg) => 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B > 140 ? Black : White;

    /// <summary>The treemap area for a canvas of this size (inside the box, above the info line).</summary>
    public static Cell MapArea(int width, int height) => new(2, 3, width - 4, height - 7);

    /// <summary>Lays out the treemap for the current folder exactly as <see cref="Map"/> draws it.</summary>
    public static List<MapBlock> MapLayout(BrowserModel m, Cell area) =>
        Treemap.Layout(m.Store.Children(m.Current), m.SizeOf,
            id => m.Store.Kind(id) == NodeKind.Directory ? m.Store.Children(id) : [],
            area, m.MapDepth);

    public static HitMap Map(ICanvas c, BrowserModel m)
    {
        var hits = new HitMap();
        Theme t = m.Theme;
        if (new Layout(c.Width, c.Height).TooSmall)
        {
            TooSmall(c, t);
            return hits;
        }
        var p = new Painter(c, t);
        p.Clear();
        Top(p, m, hits, showSort: false);
        NodeStore s = m.Store;
        int w = c.Width, h = c.Height;

        string name = CrumbName(m, m.Current);
        p.HLine(0, 2, w, '╭', '╮', "Treemap · " + name, $"area = {(m.Apparent ? "apparent" : "on-disk")} size · depth {m.MapDepth}");
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        p.HLine(0, h - 4, w, '├', '┤');
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);

        Cell area = MapArea(w, h);
        List<MapBlock> blocks = MapLayout(m, area);
        hits.Blocks.AddRange(blocks);
        Rgb[] palette = t.Name == "mono" ? MapGreys : MapColours;
        int? selected = m.SelectedNode;

        if (blocks.Count == 0)
        {
            string why = m.IsPending(m.Current) ? Spin[m.Tick % Spin.Length] + " Still scanning this folder…"
                : s.ChildCount(m.Current) == 0 ? "Nothing to map: this folder is empty."
                : "Nothing to map: everything here is 0 bytes.";
            p.Text(area.X + 2, area.Y + 1, Cells.Fit(why, area.W - 4).TrimEnd(), t.Muted);
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            DrawBlock(p, m, blocks[i], palette[i % palette.Length], blocks[i].Node == selected, top: true);
        }

        // Info line about the selected block.
        int y = h - 3;
        if (selected is int sel && blocks.Any(b => b.Node == sel))
        {
            bool dir = s.Kind(sel) == NodeKind.Directory;
            long size = m.SizeOf(sel);
            long parent = Math.Max(1, m.SizeOf(m.Current));
            string info = $"{Units.Size(size)} · {(100.0 * size / parent).ToString("0.0", CultureInfo.InvariantCulture)}% of {name}"
                          + (dir ? $" · {Units.Count(s.Files(sel))} files" : "")
                          + (m.IsGrowing(sel) ? " · still scanning" : "");
            // The breadcrumbs already say where we are, so the info line names just the block.
            string hint = dir ? "↵ zoom in" : "";
            int room = w - 4 - (hint.Length > 0 ? Cells.Width(hint) + 2 : 0);
            int x = p.Text(2, y, "► ", t.Accent);
            string label = Cells.Fit(s.Name(sel) + (dir ? "/" : ""), Math.Max(6, Math.Min(32, room - 4 - Cells.Width(info)))).TrimEnd();
            x = p.Text(x, y, label, t.Accent, attr: TextAttr.Bold);
            p.Text(x + 2, y, Cells.Fit(info, Math.Max(0, room - (x + 2) + 2)).TrimEnd(), t.Text);
            if (hint.Length > 0)
            {
                p.Text(w - 2 - Cells.Width(hint), y, hint, t.Muted);
            }
        }
        else
        {
            p.Text(2, y, Cells.Fit("Use the arrow keys or click to pick a block.", w - 4).TrimEnd(), t.Muted);
        }

        (string, string)[] keys = w >= 80
            ? [("←↑↓→", "move"), ("↵", "zoom in"), ("⌫", "zoom out"), ("+ -", "depth"), ("tab", "browse"), ("t", "theme"), ("?", "help"), ("q", "quit")]
            : [("←↑↓→", "move"), ("↵ ⌫", "zoom"), ("?", "help"), ("q", "quit")];
        string spin = Spin[m.Tick % Spin.Length];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    private static void DrawBlock(Painter p, BrowserModel m, MapBlock b, Rgb colour, bool selected, bool top)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        Cell r = b.Rect;
        // One cell of gap on the right and bottom separates neighbours (unless the block is tiny).
        int w = r.W > 1 ? r.W - 1 : r.W;
        int h = r.H > 1 ? r.H - 1 : r.H;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        if (b.IsGroup)
        {
            c.Fill(r.X, r.Y, w, h, new Style(t.Muted, t.Track));
            if (w >= 4)
            {
                c.Text(r.X + 1, r.Y, Cells.Fit($"+{Units.Count(b.GroupCount)} smaller", w - 1).TrimEnd(), new Style(t.Muted, t.Track));
            }
            if (w >= 4 && h >= 2)
            {
                c.Text(r.X + 1, r.Y + 1, Cells.Fit(Units.Size(b.Size), w - 1).TrimEnd(), new Style(t.Faint, t.Track));
            }
            return;
        }

        string name = s.Name(b.Node) + (m.IsGrowing(b.Node) ? " " + Spin[m.Tick % Spin.Length] : "");
        string mark = (selected ? "► " : "") + (m.IsMarked(b.Node) ? "● " : "");
        if (b.Children.Count > 0)
        {
            // A parent: a header bar, then its children laid out inside.
            Rgb head = selected ? t.Accent : colour.Mix(Black, 0.45);
            Rgb fg = selected ? Black : White;
            c.Fill(r.X, r.Y, w, 1, new Style(fg, head));
            c.Text(r.X, r.Y, Cells.Fit(" " + mark + name + " · " + Units.Size(b.Size), w).TrimEnd(), new Style(fg, head, TextAttr.Bold));
            c.Fill(r.X, r.Y + 1, w, h - 1, new Style(t.Text, colour.Mix(Black, 0.7)));
            for (int i = 0; i < b.Children.Count; i++)
            {
                double f = 0.10 + 0.08 * (i % 3);
                Rgb shade = i % 2 == 0 ? colour.Mix(Black, f) : colour.Mix(White, f);
                DrawBlock(p, m, b.Children[i], shade, false, top: false);
            }
            return;
        }

        Rgb bg = selected ? colour.Mix(White, 0.45) : colour;
        Rgb text = Contrast(bg);
        c.Fill(r.X, r.Y, w, h, new Style(text, bg));
        // Labels only where they can say something; tiny blocks stay clean (hover/select shows details).
        const int MinLabel = 6;
        if (w >= MinLabel)
        {
            c.Text(r.X + 1, r.Y, Cells.Fit(mark + name, w - 1).TrimEnd(), new Style(text, bg, TextAttr.Bold));
        }
        if (w >= MinLabel && h >= 2)
        {
            c.Text(r.X + 1, r.Y + 1, Cells.Fit(Units.Size(b.Size), w - 1).TrimEnd(), new Style(text, bg));
        }
        if (selected && w < MinLabel)
        {
            c.Text(r.X, r.Y, "►", new Style(text, bg, TextAttr.Bold));
        }
    }
}
