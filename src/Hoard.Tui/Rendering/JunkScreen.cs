using System.Globalization;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>Known junk with a reclaimable summary (plan.md §6.1, §6.7; mock: examples/09-junk.sh).</summary>
    public static HitMap JunkView(ICanvas c, BrowserModel m)
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

        p.HLine(0, 2, w, '╭', '╮', "Known junk · " + CrumbName(m, NodeStore.Root), "SAFE = recreated automatically");
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        p.HLine(0, h - 5, w, '├', '┤', "Reclaimable");
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);

        JunkReport? report = m.Junk;
        int x = 2, inner = w - 4;
        if (report is null && m.Scanning)
        {
            p.Text(x, 4, $"{Spin[m.Tick % Spin.Length]} The junk report is ready when the scan finishes.", t.Muted);
            p.Text(x, 5, "Meanwhile you can browse (2) or look at the map (3).", t.Faint);
        }
        else if (m.JunkError is { } error)
        {
            p.Text(x, 4, Cells.Fit("The junk search failed: " + error, inner).TrimEnd(), t.Bad);
        }
        else if (report is null)
        {
            // Searching in the background: show how far along it is instead of freezing.
            long total = Math.Max(1, s.Dirs(NodeStore.Root));
            long done = Math.Min(total, m.JunkWork.FoldersChecked);
            double f = (double)done / total;
            p.Text(x, 4, $"{Spin[m.Tick % Spin.Length]} Looking for junk…", t.Accent, attr: TextAttr.Bold);
            int barW = Math.Max(10, Math.Min(60, inner - 14));
            p.Bar(x, 6, barW, f);
            p.Text(x + barW + 2, 6, $"{(int)(f * 100),3}%", t.Text);
            p.Text(x, 7, Cells.Fit($"{Units.Count(done)} of {Units.Count(total)} folders checked · you can keep using the other views", inner).TrimEnd(), t.Muted);
        }
        else if (report.Categories.All(cat => cat.Count(s) == 0))
        {
            p.Text(x, 4, Cells.Fit("No known junk here.", inner).TrimEnd(), t.Ok, attr: TextAttr.Bold);
            p.Text(x, 5, Cells.Fit("Hoard looks for build outputs, dependency folders, caches, logs and old installers.", inner).TrimEnd(), t.Muted);
        }
        else
        {
            DrawJunkRows(p, m, report, x, inner, 3, h - 5);
        }

        // Summary
        if (report is not null)
        {
            long found = report.Total(s), safe = report.SafeTotal(s);
            var (items, selected) = m.JunkSelection();
            int y = h - 4;
            int sx = p.Text(x, y, Units.Size(selected), t.Accent, attr: TextAttr.Bold);
            sx = p.Text(sx + 1, y, $"selected ({Units.Count(items.Count)} items) of", t.Muted);
            sx = p.Text(sx + 1, y, Units.Size(found), t.Text, attr: TextAttr.Bold);
            p.Text(sx + 1, y, Cells.Fit($"found · {Units.Size(safe)} safe · {Units.Size(found - safe)} to review", Math.Max(0, w - sx - 4)).TrimEnd(), t.Muted);
            int barW = Math.Max(10, Math.Min(60, inner - 30));
            p.Bar(x, y + 1, barW, found > 0 ? (double)selected / found : 0, t.Ok);
            p.Text(x + barW + 2, y + 1, Cells.Fit("c moves the selection to the Trash (asks first)", Math.Max(0, inner - barW - 2)).TrimEnd(), t.Faint);
        }

        (string, string)[] keys = w >= 80
            ? [("↑↓", "move"), ("→", "expand"), ("␣", "tick"), ("A", "all safe"), ("c", "clean"), ("i", "why"), ("↵", "show"), ("tab", "browse"), ("?", "help"), ("q", "quit")]
            : [("↑↓", "move"), ("␣", "tick"), ("c", "clean"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    private static void DrawJunkRows(Painter p, BrowserModel m, JunkReport report, int x, int inner, int top, int bottom)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        List<(int Cat, int Item)> rows = m.JunkRows();
        m.JunkSelected = Math.Clamp(m.JunkSelected, 0, Math.Max(0, rows.Count - 1));
        int visible = Math.Max(1, bottom - top - 1);
        if (m.JunkSelected < m.JunkScroll) m.JunkScroll = m.JunkSelected;
        if (m.JunkSelected >= m.JunkScroll + visible) m.JunkScroll = m.JunkSelected - visible + 1;
        m.JunkScroll = Math.Clamp(m.JunkScroll, 0, Math.Max(0, rows.Count - visible));

        long total = Math.Max(1, report.Total(s));
        bool wide = inner >= 96;
        int barW = inner >= 80 ? 12 : 0;
        // checkbox+space, arrow+space, count, size + gap, bar + gap, badge + gap
        int fixedW = 4 + 2 + 11 + 12 + (barW > 0 ? barW + 2 : 0) + 9;
        const int NameMin = 30;
        int whyW = wide ? Math.Clamp(inner - fixedW - NameMin - 1, 0, 48) : 0;
        if (whyW < 12)
        {
            whyW = 0;
        }
        int nameW = inner - fixedW - (whyW > 0 ? whyW + 1 : 0);
        long now = m.Now;

        for (int r = 0; r < visible && m.JunkScroll + r < rows.Count; r++)
        {
            int index = m.JunkScroll + r;
            var (ci, ii) = rows[index];
            JunkCategory cat = report.Categories[ci];
            int y = top + 1 + r;
            bool sel = index == m.JunkSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, inner + 2, 1, new Style(t.Text, bg));
            }
            if (ii < 0)
            {
                bool on = m.JunkChecked.Contains(cat.Rule.Id);
                bool partial = on && cat.Items.Any(m.JunkSkipped.Contains);
                long size = cat.Size(s);
                int cx = p.Text(x, y, on ? (partial ? "[-]" : "[✓]") : "[ ]", on ? t.Ok : t.Faint, bg, TextAttr.Bold);
                cx = p.Text(cx + 1, y, m.JunkExpanded.Contains(cat.Rule.Id) ? "▾" : "▸", t.Accent, bg);
                cx = p.Text(cx + 1, y, Cells.Fit(cat.Rule.Name, nameW), t.Text, bg, TextAttr.Bold);
                cx = p.Text(cx, y, Cells.Fit(Plural(cat.Count(s), "item"), 11, padLeft: true), t.Faint, bg);
                cx = p.Text(cx, y, Cells.Fit(Units.Size(size), 10, padLeft: true), t.Text, bg, TextAttr.Bold) + 2;
                if (barW > 0)
                {
                    p.Bar(cx, y, barW, (double)size / total, t.Accent2);
                    cx += barW + 2;
                }
                bool safe = cat.Rule.Safety == Safety.Safe;
                cx = p.Text(cx, y, safe ? " SAFE   " : " REVIEW ", Theme.On(safe ? t.Ok : t.Warn), safe ? t.Ok : t.Warn, TextAttr.Bold) + 1;
                if (whyW > 0)
                {
                    p.Text(cx, y, Cells.Fit(cat.Rule.Why, whyW).TrimEnd(), t.Muted, bg);
                }
            }
            else
            {
                int node = cat.Items[ii];
                bool included = m.IsJunkItemIncluded(cat, node);
                int cx = p.Text(x + 4, y, included ? "✓" : "·", included ? t.Ok : t.Faint, bg);
                // Relative to the scanned folder (shown in the title): the end of the path is what matters.
                string path = s.RelativePath(node);
                string age = "modified " + Ago(now - s.MTime(node));
                int pathW = Math.Max(10, inner - 6 - 10 - 2 - (wide ? 24 : 0));
                cx = p.Text(cx + 1, y, Cells.Fit(path, pathW), t.Text, bg);
                cx = p.Text(cx, y, Cells.Fit(Units.Size(s.Size(node)), 10, padLeft: true), t.Text, bg) + 2;
                if (wide)
                {
                    p.Text(cx, y, Cells.Fit(age, 22).TrimEnd(), AgeColour(t, now - s.MTime(node)), bg);
                }
            }
        }
    }
}
