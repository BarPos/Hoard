using System.Globalization;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>The finder over the whole scan (plan.md §7.5; mock: examples/12-search.sh).</summary>
    private static void SearchOverlay(Painter p, BrowserModel m, SearchBox box)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        int w = Math.Min(c.Width - 4, 96), h = Math.Min(c.Height - 2, 24);
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, (c.Height - h) / 2);
        SearchResult? r = m.SearchResults;
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        string note = box.Query.Length == 0 ? "fuzzy · type to search"
            : r is null ? Spin[m.Tick % Spin.Length] + " searching…"
            : $"fuzzy · {Units.Count(r.Total)} {(r.Total == 1 ? "match" : "matches")} · {r.Took.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms{(m.SearchRunning ? " " + Spin[m.Tick % Spin.Length] : "")}";
        p.HLine(x0, y0, w, '╭', '╮', "Find in " + CrumbName(m, NodeStore.Root), note);
        for (int i = 1; i < h - 1; i++)
        {
            p.Text(x0, y0 + i, "│", t.Border, t.Panel);
            p.Text(x0 + w - 1, y0 + i, "│", t.Border, t.Panel);
        }
        p.HLine(x0, y0 + 3, w, '├', '┤');
        p.HLine(x0, y0 + h - 3, w, '├', '┤');
        p.HLine(x0, y0 + h - 1, w, '╰', '╯');
        int x = x0 + 2, inner = w - 4;

        int qx = p.Text(x, y0 + 1, "/ ", t.Accent, t.Panel, TextAttr.Bold);
        p.Text(qx, y0 + 1, Cells.Fit(box.Query + "▏", inner - 2).TrimEnd(), t.Text, t.Panel, TextAttr.Bold);
        string kind = box.Kind switch { SearchKind.Folders => "folders", SearchKind.Files => "files", _ => "any" };
        string size = box.MinSize == 0 ? "any" : "≥ " + Units.Size(box.MinSize);
        int fx = p.Text(x, y0 + 2, $"[type: {kind}]", box.Kind == SearchKind.Any ? t.Muted : t.Accent2, t.Panel) + 1;
        fx = p.Text(fx, y0 + 2, $"[size: {size}]", box.MinSize == 0 ? t.Muted : t.Accent2, t.Panel) + 2;
        p.Text(fx, y0 + 2, Cells.Fit("tab type · ^s size", Math.Max(0, x + inner - fx)).TrimEnd(), t.Faint, t.Panel);

        int top = y0 + 4, rows = h - 7;
        if (box.Query.Length == 0)
        {
            p.Text(x, top, Cells.Fit("Letters in order, anywhere in the path: “nmod mono” finds monolith/node_modules.", inner).TrimEnd(), t.Muted, t.Panel);
        }
        else if (r is { Hits.Count: 0 } && !m.SearchRunning)
        {
            p.Text(x, top, Cells.Fit("Nothing matches. Fewer letters, or tab / ^s to widen the filters.", inner).TrimEnd(), t.Muted, t.Panel);
        }
        else if (r is not null)
        {
            int count = r.Hits.Count;
            box.Selected = Math.Clamp(box.Selected, 0, Math.Max(0, count - 1));
            int visible = Math.Max(1, rows - (r.Total > count || count > rows ? 1 : 0));
            box.Scroll = ScrollFor(box.Selected, box.Scroll, count, visible);
            string[] words = FuzzySearch.Words(box.Query);
            for (int i = 0; i < visible && box.Scroll + i < count; i++)
            {
                int index = box.Scroll + i;
                int id = r.Hits[index].Node;
                int y = top + i;
                bool sel = index == box.Selected;
                Rgb bg = sel ? t.Selection : t.Panel;
                c.Fill(x - 1, y, inner + 2, 1, new Style(t.Text, bg));
                int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
                cx = p.Text(cx, y, m.IsMarked(id) ? "●" : " ", t.Warn, bg) + 1;
                bool dir = s.Kind(id) == NodeKind.Directory;
                string rel = s.RelativePath(id) + (dir ? "/" : "");
                int pathW = inner - 3 - 11;
                // Highlight the matched letters; long paths keep their end.
                var hitsAt = new List<int>();
                FuzzySearch.ScoreAll(s.RelativePath(id), words, s.RelativePath(id).LastIndexOf('/') + 1, hitsAt);
                var set = new HashSet<int>(hitsAt);
                int skip = Math.Max(0, rel.Length - pathW);
                if (skip > 0)
                {
                    cx = p.Text(cx, y, "…", t.Faint, bg);
                    skip++;
                }
                for (int k = skip; k < rel.Length && cx < x + 3 + pathW; k++)
                {
                    bool on = set.Contains(k);
                    cx = p.Text(cx, y, rel[k].ToString(), on ? t.Accent : dir ? t.Text : t.Muted, bg, on ? TextAttr.Bold : TextAttr.None);
                }
                p.Text(x + inner - 10, y, Cells.Fit(Units.Size(s.Size(id)), 10, padLeft: true), t.Text, bg);
            }
            if (r.Total > visible)
            {
                p.Text(x + 2, top + visible, Cells.Fit(r.Total > count ? $"… {Units.Count(r.Total - visible)} more (the best {count} can be scrolled)" : $"… {count - visible} more below", inner - 2).TrimEnd(), t.Faint, t.Panel);
            }
        }
        p.Text(x, y0 + h - 2, Cells.Fit("↵ jump · ^t mark · ^a mark all · ^f filter this list instead · esc close", inner).TrimEnd(), t.Muted, t.Panel);
    }
}

public static partial class Screens
{
    /// <summary>The command palette: type a few letters of what you want, ↵ does it.</summary>
    private static void PaletteOverlay(Painter p, BrowserModel m, PaletteBox box)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        List<PaletteEntry> found = box.Matches();
        int w = Math.Min(c.Width - 4, 64), h = Math.Min(c.Height - 2, Math.Max(8, Math.Min(found.Count, 14) + 6));
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, (c.Height - h) / 3);
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        p.HLine(x0, y0, w, '╭', '╮', "Do something", $"{found.Count} of {PaletteBox.All.Length}");
        for (int i = 1; i < h - 1; i++)
        {
            p.Text(x0, y0 + i, "│", t.Border, t.Panel);
            p.Text(x0 + w - 1, y0 + i, "│", t.Border, t.Panel);
        }
        p.HLine(x0, y0 + 2, w, '├', '┤');
        p.HLine(x0, y0 + h - 1, w, '╰', '╯');
        int x = x0 + 2, inner = w - 4;
        int qx = p.Text(x, y0 + 1, ": ", t.Accent, t.Panel, TextAttr.Bold);
        p.Text(qx, y0 + 1, Cells.Fit(box.Query + "▏", inner - 2).TrimEnd(), t.Text, t.Panel, TextAttr.Bold);
        int rows = h - 5;
        box.Selected = Math.Clamp(box.Selected, 0, Math.Max(0, found.Count - 1));
        int scroll = ScrollFor(box.Selected, 0, found.Count, rows);
        if (found.Count == 0)
        {
            p.Text(x, y0 + 3, "Nothing by that name. ? lists every key.", t.Muted, t.Panel);
        }
        for (int i = 0; i < rows && scroll + i < found.Count; i++)
        {
            PaletteEntry e = found[scroll + i];
            int y = y0 + 3 + i;
            bool sel = scroll + i == box.Selected;
            Rgb bg = sel ? t.Selection : t.Panel;
            c.Fill(x - 1, y, inner + 2, 1, new Style(t.Text, bg));
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg) + 1;
            p.Text(cx, y, Cells.Fit(e.Name, inner - 10), t.Text, bg);
            p.Text(x + inner - 7, y, Cells.Fit(e.Key, 7, padLeft: true), t.Accent, bg, TextAttr.Bold);
        }
        p.Text(x, y0 + h - 2, Cells.Fit("type to narrow · ↑↓ pick · ↵ do it · esc close", inner).TrimEnd(), t.Faint, t.Panel);
    }
}
