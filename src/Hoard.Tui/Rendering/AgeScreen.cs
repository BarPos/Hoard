using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>Hit-map index of a heatmap cell: row and column (-1 = the name).</summary>
    public static int AgeHit(int row, int column) => row * 16 + column + 1;
    public static (int Row, int Column) AgeHitCell(int index) => (index / 16, index % 16 - 1);

    /// <summary>A size in at most <paramref name="width"/> cells: "18.2G", "338M", "18G".</summary>
    internal static string ShortSize(long bytes, int width)
    {
        if (bytes <= 0)
        {
            return "·";
        }
        string s = Units.Size(bytes); // "18.2 GB", "338 MB", "512 B"
        int space = s.IndexOf(' ');
        string number = s[..space], unit = s[(space + 1)..];
        unit = unit == "B" ? "B" : unit[..1];
        string compact = number + unit;
        if (compact.Length > width && number.Contains('.'))
        {
            compact = Math.Round(double.Parse(number, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture) + unit;
        }
        return compact;
    }

    /// <summary>How old the bytes in the open folder are (plan.md §6.6; mock: examples/07-age-heatmap.sh).</summary>
    public static HitMap AgeView(ICanvas c, BrowserModel m)
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
        int w = c.Width, h = c.Height, x = 2, inner = w - 4;
        string axis = m.AgeAxis == AgeAxis.Accessed ? "by last access · m by modified" : "by last modified · a by access";
        FileList? list = m.AgeFiles;
        string title = list?.Title ?? "Age heatmap · " + (m.Current == NodeStore.Root ? CrumbName(m, NodeStore.Root) : m.Store.Name(m.Current));
        string? note = list is not null && list.Count > FileList.Limit ? $"the {Units.Count(FileList.Limit)} biggest of {Units.Count(list.Count)}" : axis;
        p.HLine(0, 2, w, '╭', '╮', title, note);
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast ?? (list is null ? "↵ on a cell lists those files" : null), m.Toast is null ? t.Faint : t.Info);

        string spin = Spin[m.Tick % Spin.Length];
        AgeReport? report = m.Age;
        if (list is not null)
        {
            DrawFileList(p, m, list, hits, x, 3, h - 3, inner);
        }
        else if (m.Scanning)
        {
            p.Text(x, 4, Cells.Fit($"{spin} The heatmap is ready when the scan finishes. Meanwhile: browse (2) or the map (3).", inner).TrimEnd(), t.Muted);
        }
        else if (m.AgeError is { } error)
        {
            p.Text(x, 4, Cells.Fit("Couldn't work out the ages: " + error, inner).TrimEnd(), t.Bad);
        }
        else if (report is null)
        {
            p.Text(x, 4, Cells.Fit($"{spin} Working out how old everything is…", inner).TrimEnd(), t.Accent, attr: TextAttr.Bold);
        }
        else if (report.Rows.Count == 0)
        {
            p.Text(x, 4, Cells.Fit("This folder is empty. ⌫ goes up.", inner).TrimEnd(), t.Muted);
        }
        else
        {
            DrawHeatmap(p, m, report, hits, x, inner, h);
        }

        (string, string)[] keys = list is not null
            ? [("↑↓", "move"), ("↵", "show in list"), ("␣", "mark"), ("d", "delete"), ("o", "reveal"), ("←", "back"), ("?", "help"), ("q", "quit")]
            : [("↑↓←→", "cell"), ("↵", m.AgeColumn < 0 ? "open folder" : "list files"), ("⌫", "up"), ("m a", "time"), ("+ -", "rows"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    private static void DrawHeatmap(Painter p, BrowserModel m, AgeReport r, HitMap hits, int x, int inner, int h)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;

        // Columns: cursor + space, name, size, 8 cells, gap, stale bar, stale %. Narrow windows lose the bar, then the size.
        (int CellW, bool Size, int Bar)[] configs = [(7, true, 12), (7, true, 8), (7, true, 0), (7, false, 0), (5, true, 0), (5, false, 0)];
        int cellW = 5, barW = 0, nameW = 6;
        bool showSize = false;
        foreach (var (cw, size, bar) in configs)
        {
            int nw = inner - 2 - (size ? 10 : 0) - AgeBuckets.Count * cw - 1 - (bar > 0 ? bar + 1 : 0) - 5;
            (cellW, showSize, barW, nameW) = (cw, size, bar, nw);
            if (nw >= 12)
            {
                break;
            }
        }
        nameW = Math.Max(4, nameW);
        int cellsX = x + 2 + nameW + (showSize ? 10 : 0);
        int staleX = cellsX + AgeBuckets.Count * cellW + 1;

        // Heading
        int top = h >= 24 ? 4 : 3;
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        c.Text(x, top, Cells.Fit("  " + Cells.Fit("NAME", nameW) + (showSize ? Cells.Fit("SIZE", 9, padLeft: true) + " " : ""), cellsX - x), heading);
        for (int b = 0; b < AgeBuckets.Count; b++)
        {
            string label = cellW >= 6 ? AgeBuckets.Labels[b] : AgeBuckets.Labels[b].Replace(" ", "");
            c.Text(cellsX + b * cellW, top, Cells.Fit(label, cellW - 1, padLeft: true), heading);
        }
        c.Text(staleX - 1, top, Cells.Fit(barW > 0 ? " STALE > 1 YEAR" : " >1Y", (barW > 0 ? barW + 1 : 0) + 6), heading);

        // Rows: spaced out when there's room, scrolled when there isn't.
        int legendTop = h - 5;
        int region = legendTop - top - 1;
        int n = r.Rows.Count;
        int step = n * 2 <= region ? 2 : 1;
        int visible = Math.Max(1, region / step);
        m.AgeSelected = Math.Clamp(m.AgeSelected, 0, n - 1);
        int scroll = ScrollFor(m.AgeSelected, 0, n, visible);
        long max = Math.Max(1, r.Rows.Max(row => row.Bytes.Max()));
        double logMax = Math.Log(1 + max);
        for (int i = 0; i < visible && scroll + i < n; i++)
        {
            int index = scroll + i;
            AgeRow row = r.Rows[index];
            int y = top + 1 + i * step;
            bool selRow = index == m.AgeSelected;
            bool dir = s.Kind(row.Node) == NodeKind.Directory;
            bool nameSel = selRow && m.AgeColumn < 0;
            Rgb nameBg = nameSel ? t.Selection : t.Bg;
            if (nameSel)
            {
                c.Fill(x, y, 2 + nameW, 1, new Style(t.Text, nameBg));
            }
            int cx = p.Text(x, y, selRow ? "►" : " ", t.Accent, nameBg) + 1;
            cx = p.Text(cx, y, Cells.Fit(s.Name(row.Node) + (dir ? "/" : ""), nameW), dir ? t.Text : t.Muted, nameBg, dir ? TextAttr.Bold : TextAttr.None);
            hits.Items.Add((y, x, cx, AgeHit(index, -1)));
            if (showSize)
            {
                p.Text(cx, y, Cells.Fit(Units.Size(row.Total), 9, padLeft: true), t.Text);
            }
            for (int b = 0; b < AgeBuckets.Count; b++)
            {
                long bytes = row.Bytes[b];
                double k = bytes > 0 ? Math.Log(1 + bytes) / logMax : 0;
                Rgb colour = t.GradientAt((double)b / AgeBuckets.Count);
                Rgb bg = bytes > 0 ? t.Track.Mix(colour, 0.2 + 0.8 * k) : t.Bg;
                Rgb fg = bytes > 0 && k > 0.55 ? Rgb.Hex(0x000000) : bytes > 0 ? t.Text : t.Faint;
                bool sel = selRow && m.AgeColumn == b;
                if (sel)
                {
                    (fg, bg) = (Rgb.Hex(0x000000), t.Accent);
                }
                int bx = cellsX + b * cellW;
                p.Text(bx, y, Cells.Fit(ShortSize(bytes, cellW - 2), cellW - 1, padLeft: true), fg, bg, sel ? TextAttr.Bold : TextAttr.None);
                hits.Items.Add((y, bx, bx + cellW, AgeHit(index, b)));
            }
            double stale = row.Total > 0 ? (double)row.Stale / row.Total : 0;
            int sx = staleX;
            if (barW > 0)
            {
                p.Bar(sx, y, barW, stale, stale > 0.5 ? t.Bad : stale > 0.25 ? t.Warn : t.Ok);
                sx += barW + 1;
            }
            p.Text(sx, y, Cells.Fit(Pct(stale), 5, padLeft: true), t.Muted);
        }
        int afterRows = top + 1 + Math.Min(visible, n - scroll) * step - (step - 1);
        if (r.MoreEntries > 0 && afterRows < legendTop)
        {
            p.Text(x + 2, afterRows, Cells.Fit($"+ {Units.Count(r.MoreEntries)} smaller {(r.MoreEntries == 1 ? "entry" : "entries")} · {Units.Size(r.MoreBytes)} · + shows more rows", inner - 2).TrimEnd(), t.Faint);
        }

        // Legend and summary
        p.HLine(0, legendTop, inner + 4, '├', '┤', "Legend");
        int lx = p.Text(x, legendTop + 1, "bytes  less ", t.Muted);
        Rgb mid = t.GradientAt(0.5);
        for (int i = 0; i < 8; i++)
        {
            lx = p.Text(lx, legendTop + 1, "  ", t.Text, t.Track.Mix(mid, 0.2 + 0.8 * i / 7.0));
        }
        lx = p.Text(lx + 1, legendTop + 1, "more", t.Muted) + 4;
        if (lx + 30 < x + inner)
        {
            lx = p.Text(lx, legendTop + 1, "columns ", t.Muted);
            for (int b = 0; b < AgeBuckets.Count; b++)
            {
                lx = p.Text(lx, legendTop + 1, "█", t.GradientAt((double)b / AgeBuckets.Count));
            }
            p.Text(lx + 1, legendTop + 1, "fresh → stale", t.Muted);
        }
        long total = Math.Max(1, r.Total.Total);
        string verb = r.Axis == AgeAxis.Accessed ? "opened" : "modified";
        string folder = r.Folder == NodeStore.Root ? CrumbName(m, NodeStore.Root) : s.Name(r.Folder);
        string summary = $"{Units.Size(r.Total.Stale)} ({Pct((double)r.Total.Stale / total)} of {folder}) hasn't been {verb} for over a year.";
        AgeRow? oldest = r.Rows.Where(row => row.Bytes[^1] > 0).MaxBy(row => row.Bytes[^1]);
        if (oldest is not null)
        {
            summary += $"  ► {s.Name(oldest.Node)}: {Units.Size(oldest.Bytes[^1])} untouched 3y+";
        }
        p.Text(x, legendTop + 2, Cells.Fit(summary, inner).TrimEnd(), t.Text);
    }
}
