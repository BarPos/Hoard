using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>Files with identical contents (plan.md §6.2; mock: examples/08-duplicates.sh).</summary>
    public static HitMap DupesView(ICanvas c, BrowserModel m)
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
        BrowserModel.DupResult? d = m.Dupes;
        List<(int Group, int File)> rows = m.DupRows();
        int groups = rows.Count(r => r.File < 0);
        string note = d is null ? "same size → same start → same contents" : $"{Plural(groups, "group")} · {Units.Size(m.DupReclaimable())} can be freed · most wasted first";
        p.HLine(0, 2, w, '╭', '╮', "Duplicate files", note);
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        bool footer = h >= 20 && d is not null && groups > 0;
        if (footer)
        {
            p.HLine(0, h - 5, w, '├', '┤', "Keep rule");
        }
        string? status = d is null ? null : $"{Units.Count(d.Report.Candidates)} same-size files compared · ≥ {Units.Size(d.Report.MinSize)}";
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast ?? status, m.Toast is null ? t.Faint : t.Info);

        string spin = Spin[m.Tick % Spin.Length];
        if (m.Scanning)
        {
            p.Text(x, 4, Cells.Fit($"{spin} Duplicates can be looked for when the scan finishes.", inner).TrimEnd(), t.Muted);
        }
        else if (m.DupesError is { } error)
        {
            p.Text(x, 4, Cells.Fit("The duplicate search failed: " + error, inner).TrimEnd(), t.Bad);
        }
        else if (!m.DupesStarted)
        {
            p.Text(x, 4, Cells.Fit("Looking for duplicates reads the files, which can take a while on a big disk.", inner).TrimEnd(), t.Muted);
            int kx = p.Text(x, 5, "↵", t.Accent, attr: TextAttr.Bold);
            p.Text(kx + 1, 5, "starts the search", t.Muted);
        }
        else if (d is null)
        {
            DrawDupProgress(p, m, x, 4, inner);
        }
        else if (groups == 0)
        {
            p.Text(x, 4, Cells.Fit($"No duplicate files of {Units.Size(d.Report.MinSize)} or more.", inner).TrimEnd(), t.Ok, attr: TextAttr.Bold);
            p.Text(x, 5, Cells.Fit("Hard links (one file, several names) aren't duplicates: they take no extra space.", inner).TrimEnd(), t.Muted);
        }
        else
        {
            DrawDupRows(p, m, d, rows, hits, x, 3, footer ? h - 5 : h - 2, inner);
            if (footer)
            {
                DrawKeepRule(p, m, x, h - 4, inner);
            }
        }

        (string, string)[] keys = [("↑↓", "move"), ("→←", "expand"), ("␣", "mark"), ("K", "keep rule"), ("a", "auto"), ("d", "delete"), ("↵", "show in list"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    private static void DrawDupProgress(Painter p, BrowserModel m, int x, int y, int width)
    {
        Theme t = p.Theme;
        DupProgress pr = m.DupWork;
        string spin = Spin[m.Tick % Spin.Length];
        string what = pr.Stage switch
        {
            0 => "Grouping files by size…",
            1 => $"Reading the start of {Plural(pr.FilesTotal, "same-size file")}…",
            _ => $"Comparing {Plural(pr.FilesTotal, "possible duplicate")} in full…",
        };
        p.Text(x, y, Cells.Fit($"{spin} {what}", width).TrimEnd(), t.Accent, attr: TextAttr.Bold);
        double f = pr.BytesTotal > 0 ? Math.Min(1, (double)pr.BytesDone / pr.BytesTotal) : 0;
        int barW = Math.Max(10, Math.Min(60, width - 8));
        p.Bar(x, y + 2, barW, f);
        p.Text(x + barW + 2, y + 2, $"{(int)(f * 100),3}%", t.Text);
        p.Text(x, y + 3, Cells.Fit($"step {pr.Stage + 1} of 3 · {Units.Count(pr.FilesDone)} of {Units.Count(pr.FilesTotal)} files · {Units.Size(pr.BytesDone)} of {Units.Size(pr.BytesTotal)} read", width).TrimEnd(), t.Muted);
        p.Text(x, y + 5, Cells.Fit("Only files with a same-size partner are read, and most only in part. You can keep using the other tabs.", width).TrimEnd(), t.Faint);
    }

    private static void DrawDupRows(Painter p, BrowserModel m, BrowserModel.DupResult d, List<(int Group, int File)> rows, HitMap hits, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        int visible = Math.Max(1, bottom - top);
        m.DupSelected = Math.Clamp(m.DupSelected, 0, rows.Count - 1);
        m.DupScroll = ScrollFor(m.DupSelected, m.DupScroll, rows.Count, visible);
        long now = m.Now;
        bool showDate = width >= 80;
        for (int i = 0; i < visible && m.DupScroll + i < rows.Count; i++)
        {
            int index = m.DupScroll + i;
            var (gi, file) = rows[index];
            DupGroup g = d.Report.Groups[gi];
            int y = top + i;
            bool sel = index == m.DupSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            if (file < 0)
            {
                List<int> files = m.DupFiles(g);
                int keep = m.DupKeeper(g);
                long frees = files.Where(id => id != keep).Sum(m.DupFrees);
                bool shared = files.Any(id => m.DupFrees(id) < s.Size(id) / 2);
                int cx = p.Text(x, y, m.DupExpanded.Contains(gi) ? "▾" : "▸", t.Accent, bg) + 1;
                // Narrow windows get the short forms; the name takes what's left.
                bool roomy = width >= 90;
                string copiesText = roomy ? $"{Units.Size(g.Size)} × {files.Count} copies" : $"× {files.Count}";
                string freesText = (roomy ? "frees " : "") + Units.Size(frees);
                string cloneText = shared ? (roomy ? "⧉ APFS clones" : "⧉") : "";
                int freesW = roomy ? 17 : 9;
                int fixedW = Cells.Width(copiesText) + 2 + freesW + 1 + (shared ? Cells.Width(cloneText) + 1 : 0);
                int nameW = Math.Max(6, width - 2 - fixedW);
                cx = p.Text(cx, y, Cells.Fit(s.Name(files[0]), nameW), t.Text, bg, TextAttr.Bold);
                cx = p.Text(cx, y, copiesText, t.Muted, bg) + 2;
                cx = p.Text(cx, y, Cells.Fit(freesText, freesW, padLeft: !roomy), frees > 0 ? t.Accent : t.Faint, bg, TextAttr.Bold) + 1;
                if (shared)
                {
                    p.Text(cx, y, cloneText, t.Info, bg);
                }
            }
            else
            {
                List<int> files = m.DupFiles(g);
                int keep = m.DupKeeper(g);
                bool marked = m.IsMarked(file);
                (string tag, Rgb fg) = marked ? ("✓ del ", t.Warn) : file == keep ? ("★ keep", t.Ok) : ("· ", t.Faint);
                int cx = p.Text(x + 4, y, Cells.Fit(tag, 7), fg, bg, TextAttr.Bold);
                int pathW = Math.Max(10, width - 6 - 7 - (showDate ? 26 : 0));
                cx = p.Text(cx, y, Cells.Fit(MiddleOut(Shorten(m.FullPath(file)), pathW), pathW), marked ? t.Warn : t.Text, bg);
                if (showDate)
                {
                    string extra = file == files.OrderBy(id => s.MTime(id)).First() ? "oldest" : file == files.OrderByDescending(id => s.MTime(id)).First() ? "newest" : "";
                    if (m.DupFrees(file) < s.Size(file) / 2)
                    {
                        extra = "shares blocks";
                    }
                    cx = p.Text(cx + 1, y, Date(s.MTime(file)), t.Muted, bg);
                    p.Text(cx + 2, y, Cells.Fit(extra, 13).TrimEnd(), t.Faint, bg);
                }
            }
            hits.Items.Add((y, x - 1, x + width + 1, index));
        }
    }

    private static void DrawKeepRule(Painter p, BrowserModel m, int x, int y, int width)
    {
        Theme t = p.Theme;
        int cx = x;
        var rules = new List<(DupKeep, string)> { (DupKeep.Oldest, "oldest"), (DupKeep.Newest, "newest"), (DupKeep.ShortestPath, "shortest path") };
        if (m.DupKeepFolder is { } kf)
        {
            rules.Add((DupKeep.InsideFolder, "in " + System.IO.Path.GetFileName(kf)));
        }
        foreach (var (rule, label) in rules)
        {
            bool on = m.DupKeep == rule;
            cx = p.Text(cx, y, $"[{label}]", on ? Rgb.Hex(0x000000) : t.Muted, on ? t.Accent : t.Bg, on ? TextAttr.Bold : TextAttr.None) + 1;
        }
        int kx = p.Text(cx + 1, y, "K", t.Accent, attr: TextAttr.Bold);
        kx = p.Text(kx + 1, y, "changes · ", t.Muted);
        kx = p.Text(kx, y, "a", t.Accent, attr: TextAttr.Bold);
        p.Text(kx + 1, y, Cells.Fit("selects every copy but the kept one · f on a copy: keep that folder's", Math.Max(0, x + width - kx - 1)).TrimEnd(), t.Muted);

        var marked = m.Dupes is { } d ? d.Report.Groups.SelectMany(m.DupFiles).Where(m.IsMarked).ToList() : [];
        string line = marked.Count == 0 ? "Nothing selected yet · ␣ ticks a copy, ␣ on a group ticks all but the kept one"
            : $"{Units.Count(marked.Count)} {(marked.Count == 1 ? "copy" : "copies")} selected → frees {Units.Size(marked.Sum(m.DupFrees))} · d compares them byte for byte, then asks";
        p.Text(x, y + 1, Cells.Fit(line, width).TrimEnd(), marked.Count == 0 ? t.Faint : t.Text);
    }
}
