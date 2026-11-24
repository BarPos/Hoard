using System.Globalization;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    private static string BigOldSortName(BigOldSort s) => s switch
    {
        BigOldSort.Size => "size",
        BigOldSort.Age => "age",
        BigOldSort.Path => "path",
        _ => "size × age",
    };

    private static string Date(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, unix)).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Years(long seconds) =>
        (Math.Floor(seconds / (365.25 * 86400) * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture) + "y";

    /// <summary>Big files nobody has touched for a long time (plan.md §6.3; mock: examples/10-big-old.sh).</summary>
    public static HitMap BigOldView(ICanvas c, BrowserModel m)
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
        int w = c.Width, h = c.Height, x = 2, inner = w - 4;
        BigOldReport? report = m.BigOld;
        BigOldOptions o = m.BigOldOptions;
        List<int> items = m.BigOldItems();
        string note = report is null ? "sorted by " + BigOldSortName(m.BigOldSort)
            : $"{Plural(items.Count, "file")} · {Units.Size(items.Sum(id => s.Size(id)))} · sorted by {BigOldSortName(m.BigOldSort)}";
        p.HLine(0, 2, w, '╭', '╮', "Big & old", note);
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        bool detail = h >= 20;
        int listBottom = detail ? h - 5 : h - 2; // exclusive
        if (detail)
        {
            p.HLine(0, h - 5, w, '├', '┤', "Selected file");
        }
        string verb = o.Axis == AgeAxis.Accessed ? "opened" : "modified";
        string? footNote = report is { AccessTimesLookUnused: true } && o.Axis == AgeAxis.Accessed
            ? "access times aren't kept on this disk, so “opened” falls back to modified"
            : o.Axis == AgeAxis.Accessed ? "macOS updates access times lazily: a rough guide" : null;
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast ?? footNote, m.Toast is null ? t.Faint : t.Info);

        // Filter chips
        int cx = x;
        foreach (string chip in new[] { $"size ≥ {Units.Size(o.MinSize)}", $"not {verb} for {BrowserModel.AgeWords(o.MinAgeDays)}+" })
        {
            cx = p.Text(cx, 3, "[" + chip + "]", t.Accent2, attr: TextAttr.Bold) + 1;
        }
        int fx = p.Text(cx + 1, 3, "f", t.Accent, attr: TextAttr.Bold);
        p.Text(fx + 1, 3, Cells.Fit("changes the filters", Math.Max(0, x + inner - fx - 1)).TrimEnd(), t.Muted);

        string spin = Spin[m.Tick % Spin.Length];
        if (m.Scanning)
        {
            p.Text(x, 5, Cells.Fit($"{spin} The list is ready when the scan finishes. Meanwhile: browse (2) or the map (3).", inner).TrimEnd(), t.Muted);
        }
        else if (m.BigOldError is { } error)
        {
            p.Text(x, 5, Cells.Fit("Couldn't look for big & old files: " + error, inner).TrimEnd(), t.Bad);
        }
        else if (report is null)
        {
            p.Text(x, 5, Cells.Fit($"{spin} Looking for big files nobody has touched…", inner).TrimEnd(), t.Accent, attr: TextAttr.Bold);
        }
        else if (items.Count == 0)
        {
            p.Text(x, 5, Cells.Fit($"Nothing of {Units.Size(o.MinSize)} or more that hasn't been {verb} for {BrowserModel.AgeWords(o.MinAgeDays)}.", inner).TrimEnd(), t.Ok, attr: TextAttr.Bold);
            p.Text(x, 6, Cells.Fit("f loosens the filters (a smaller size or a shorter time).", inner).TrimEnd(), t.Muted);
        }
        else
        {
            DrawBigOldList(p, m, report, items, hits, x, 4, listBottom, inner);
            if (detail)
            {
                DrawBigOldDetail(p, m, items[Math.Clamp(m.BigOldSelected, 0, items.Count - 1)], x, h - 4, inner);
            }
        }

        (string, string)[] keys = [("↑↓", "move"), ("␣", "mark"), ("d", "delete"), ("f", "filters"), ("s", "sort"), ("o", "reveal"), ("↵", "show in list"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        if (m.FilterOpen)
        {
            FilterBox(p, m);
        }
        return hits;
    }

    private static void DrawBigOldList(Painter p, BrowserModel m, BigOldReport r, List<int> items, HitMap hits, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        AgeAxis axis = r.Options.Axis;
        int barW = width >= 90 ? 12 : 0;
        bool showDate = width >= 64;
        // cursor+mark+space, size, gap, date + gap, bar + gap, years + gap, path
        int pathX = 3 + 9 + 2 + (showDate ? 12 : 0) + (barW > 0 ? barW + 1 : 0) + 5 + 2;
        int pathW = Math.Max(10, width - pathX);
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        string sizeHead = m.BigOldSort == BigOldSort.Size ? "SIZE ▼" : "SIZE";
        string head = "   " + Cells.Fit(sizeHead, 9, padLeft: true) + "  "
                      + (showDate ? Cells.Fit(axis == AgeAxis.Accessed ? "LAST OPENED" : "MODIFIED", 12) : "")
                      + (barW > 0 ? Cells.Fit(m.BigOldSort == BigOldSort.Age ? "AGE ▼" : "AGE", barW + 1) : "")
                      + Cells.Fit("", 5) + "  " + (m.BigOldSort == BigOldSort.Path ? "PATH ▲" : "PATH");
        c.Text(x, top, Cells.Fit(head, width), heading);

        int visible = Math.Max(1, bottom - top - 1);
        m.BigOldSelected = Math.Clamp(m.BigOldSelected, 0, items.Count - 1);
        m.BigOldScroll = ScrollFor(m.BigOldSelected, m.BigOldScroll, items.Count, visible);
        for (int row = 0; row < visible && m.BigOldScroll + row < items.Count; row++)
        {
            int index = m.BigOldScroll + row;
            int id = items[index];
            int y = top + 1 + row;
            bool sel = index == m.BigOldSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            bool marked = m.IsMarked(id);
            long age = r.Now - BigOld.Touched(s, id, axis);
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx, y, marked ? "●" : " ", t.Warn, bg) + 1;
            cx = p.Text(cx, y, Cells.Fit(Units.Size(s.Size(id)), 9, padLeft: true), t.Text, bg, TextAttr.Bold) + 2;
            if (showDate)
            {
                cx = p.Text(cx, y, Cells.Fit(Date(BigOld.Touched(s, id, axis)), 12), t.Muted, bg);
            }
            if (barW > 0)
            {
                p.Bar(cx, y, barW, Math.Min(1, age / (5 * 365.25 * 86400)), AgeColour(t, age));
                cx += barW + 1;
            }
            cx = p.Text(cx, y, Cells.Fit(Years(age), 5, padLeft: true), AgeColour(t, age), bg) + 2;
            p.Text(cx, y, Cells.Fit(MiddleOut(Shorten(m.FullPath(id)), pathW), pathW).TrimEnd(), marked ? t.Warn : t.Text, bg);
            hits.Items.Add((y, x - 1, x + width + 1, index));
        }
    }

    private static void DrawBigOldDetail(Painter p, BrowserModel m, int id, int x, int y, int width)
    {
        Theme t = p.Theme;
        NodeStore s = m.Store;
        string name = s.Name(id);
        int cx = p.Text(x, y, Cells.Fit(name, Math.Min(Cells.Width(name), width / 2)).TrimEnd(), t.Accent, attr: TextAttr.Bold);
        string info = $"  {FileTypes.Label(FileTypes.CategoryOf(name)).ToLowerInvariant()} · {Units.Size(s.Size(id))} · modified {Date(s.MTime(id))} · opened {Date(Math.Max(s.ATime(id), s.MTime(id)))}";
        p.Text(cx, y, Cells.Fit(info, Math.Max(0, x + width - cx)).TrimEnd(), t.Muted);
        int kx = x;
        foreach (var (k, d) in new[] { ("o", "reveal in Finder"), ("↵", "show in the list"), ("y", "copy path"), ("␣", "mark"), ("d", "delete") })
        {
            if (kx + Cells.Width(k) + Cells.Width(d) + 4 > x + width)
            {
                break;
            }
            kx = p.Text(kx, y + 1, k, t.Accent, attr: TextAttr.Bold);
            kx = p.Text(kx + 1, y + 1, d, t.Muted) + 3;
        }
    }

    /// <summary>The filter panel: ↑↓ choose a setting, ←→ change it.</summary>
    private static void FilterBox(Painter p, BrowserModel m)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        BigOldOptions o = m.BigOldOptions;
        (string Label, string Value)[] rows =
        [
            ("Size at least", Units.Size(o.MinSize)),
            ("Untouched for", BrowserModel.AgeWords(o.MinAgeDays) + " or more"),
            ("Based on", o.Axis == AgeAxis.Accessed ? "last opened" : "last modified"),
        ];
        int w = Math.Min(c.Width - 4, 46), h = rows.Length + 5;
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, (c.Height - h) / 2);
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        p.HLine(x0, y0, w, '╭', '╮', "Filters");
        for (int i = 1; i < h - 1; i++)
        {
            p.Text(x0, y0 + i, "│", t.Border, t.Panel);
            p.Text(x0 + w - 1, y0 + i, "│", t.Border, t.Panel);
        }
        p.HLine(x0, y0 + h - 1, w, '╰', '╯');
        for (int i = 0; i < rows.Length; i++)
        {
            bool sel = i == m.FilterRow;
            int y = y0 + 2 + i;
            Rgb bg = sel ? t.Selection : t.Panel;
            c.Fill(x0 + 2, y, w - 4, 1, new Style(t.Text, bg));
            int cx = p.Text(x0 + 2, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx + 1, y, Cells.Fit(rows[i].Label, 15), t.Text, bg);
            cx = p.Text(cx, y, "◂ ", sel ? t.Accent : t.Faint, bg);
            cx = p.Text(cx, y, rows[i].Value, t.Text, bg, TextAttr.Bold);
            p.Text(cx, y, " ▸", sel ? t.Accent : t.Faint, bg);
        }
        p.Text(x0 + 3, y0 + h - 2, Cells.Fit("↑↓ choose · ←→ change · ↵ or esc done", w - 6).TrimEnd(), t.Muted, t.Panel);
    }
}
