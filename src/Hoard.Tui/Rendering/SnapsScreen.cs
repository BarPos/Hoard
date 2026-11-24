using System.Globalization;
using Hoard.Core.Formatting;
using Hoard.Core.Snapshots;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    private static string Signed(long bytes) => (bytes >= 0 ? "+" : "−") + Units.Size(Math.Abs(bytes));

    /// <summary>Snapshots and what changed between two of them (plan.md §9.1; mock: examples/14-snapshots.sh).</summary>
    public static HitMap SnapsView(ICanvas c, BrowserModel m)
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
        int w = c.Width, h = c.Height;
        bool two = w >= 90;
        int lw = two ? Math.Min(40, w / 3 + 4) : w - 1;
        List<Snapshot?> rows = m.SnapRows;
        SnapshotPair? pair = m.SnapPair;
        List<FolderChange> changes = m.SnapChanges();
        long net = pair is null ? 0 : pair.Compare.Root.Size - pair.Base.Root.Size;
        if (two)
        {
            p.HLine(0, 2, lw + 1, '╭', '┬', "Snapshots", m.Snapshots is null ? "off" : $"{rows.Count - 1} saved");
            p.HLine(lw, 2, w - lw, '┬', '╮', "What changed", pair is null ? null : Signed(net), net > 0 ? t.Bad : t.Ok);
            p.HLine(0, h - 2, lw + 1, '╰', '┴', null, m.Toast, t.Info);
            p.HLine(lw, h - 2, w - lw, '┴', '╯');
            p.VLine(lw, 3, h - 5);
        }
        else
        {
            p.HLine(0, 2, w, '╭', '╮', "Snapshots", "widen the window to see what changed");
            p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);
        }
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);

        DrawSnapList(p, m, rows, hits, 2, 3, lw - 3, h - 3);
        if (two)
        {
            DrawSnapDiff(p, m, pair, changes, hits, lw + 2, 3, w - lw - 4, h - 3);
        }

        (string, string)[] keys = m.SnapDiffFocus
            ? [("↑↓", "move"), ("↵", "open folder"), ("⌫", "up"), ("f", m.SnapOnlyGrowth ? "all changes" : "only growth"), ("←", "list"), ("?", "help"), ("q", "quit")]
            : [("↑↓", "pick"), ("↵", "compare"), ("b", "set base"), ("→", "changes"), ("P", "snapshot now"), ("del", "delete"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    private static void DrawSnapList(Painter p, BrowserModel m, List<Snapshot?> rows, HitMap hits, int x, int y, int width, int bottom)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        if (m.Snapshots is null)
        {
            foreach (string line in Wrap("Snapshots are off: there's no folder to keep them in.", width))
            {
                p.Text(x, y++, line, t.Muted);
            }
            return;
        }
        m.SnapSelected = Math.Clamp(m.SnapSelected, 0, rows.Count - 1);
        int listRows = Math.Max(1, Math.Min(rows.Count, bottom - y - 7));
        int scroll = ScrollFor(m.SnapSelected, 0, rows.Count, listRows);
        for (int i = 0; i < listRows && scroll + i < rows.Count; i++)
        {
            int index = scroll + i;
            Snapshot? s = rows[index];
            int yy = y + i;
            bool sel = index == m.SnapSelected && !m.SnapDiffFocus;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, yy, width + 2, 1, new Style(t.Text, bg));
            }
            bool cmp = index == m.SnapCompare, bas = index == m.SnapBase;
            int cx = p.Text(x, yy, cmp ? "●" : bas ? "◆" : "○", cmp ? t.Accent : bas ? t.Accent2 : t.Faint, bg, TextAttr.Bold) + 1;
            string when = s is null ? "now (this scan)" : s.TakenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            cx = p.Text(cx, yy, Cells.Fit(when, 17), s is null ? t.Text : t.Muted, bg, s is null ? TextAttr.Bold : TextAttr.None);
            long size = s is null ? m.Store.Size(Hoard.Core.Model.NodeStore.Root) : s.Root.Size;
            cx = p.Text(cx, yy, Cells.Fit(Units.Size(size), 10, padLeft: true), t.Text, bg);
            string tag = cmp ? " now" : bas ? " base" : "";
            if (cmp && s is not null) tag = " compare";
            p.Text(cx, yy, Cells.Fit(tag, Math.Max(0, x + width - cx)).TrimEnd(), cmp ? t.Accent : t.Accent2, bg);
            hits.Items.Add((yy, x - 1, x + width + 1, index));
        }

        // Trend over every snapshot, oldest to newest.
        int ty = y + listRows + 1;
        var series = rows.Select(s => s is null ? (DateTimeOffset.Now, m.Store.Size(Hoard.Core.Model.NodeStore.Root)) : (s.TakenAt, s.Root.Size))
            .OrderBy(v => v.Item1).ToList();
        if (series.Count >= 2 && ty + 2 < bottom)
        {
            long lo = series.Min(v => v.Item2), hi = Math.Max(lo + 1, series.Max(v => v.Item2));
            int cx = p.Text(x, ty, "trend  ", t.Muted);
            foreach (var (_, size) in series.TakeLast(Math.Max(1, width - 30)))
            {
                cx = p.Text(cx, ty, Spark[Math.Clamp((int)((double)(size - lo) / (hi - lo) * 7.99), 0, 7)], t.Accent2);
            }
            long grew = series[^1].Item2 - series[0].Item2;
            double days = Math.Max(1 / 24.0, (series[^1].Item1 - series[0].Item1).TotalDays);
            p.Text(cx + 2, ty, Cells.Fit($"{Signed(grew)} in {(days < 2 ? $"{days * 24:0} h" : $"{days:0} days")}", Math.Max(0, x + width - cx - 2)).TrimEnd(), t.Text);
            double perDay = grew / days;
            p.Text(x, ty + 1, Cells.Fit($"rate   ≈ {Signed((long)perDay)}/day", width).TrimEnd(), t.Muted);
            if (perDay > 0 && m.Disk is { } disk && ty + 2 < bottom)
            {
                double left = disk.Free / perDay;
                p.Text(x, ty + 2, Cells.Fit(left > 3650 ? "disk full in: not at this rate" : $"disk full in ≈ {left:0} days", width).TrimEnd(), left < 60 ? t.Bad : t.Muted);
            }
        }
        else if (ty < bottom)
        {
            foreach (string line in Wrap(rows.Count < 2
                ? "No earlier snapshot yet. One is saved after every full scan; P saves one now."
                : "", width))
            {
                if (ty < bottom) p.Text(x, ty++, line, t.Muted);
            }
        }
        if (bottom - 3 > ty + 3)
        {
            p.Text(x, bottom - 3, Cells.Fit("● compare   ◆ base (b picks it)", width).TrimEnd(), t.Faint);
            p.Text(x, bottom - 2, Cells.Fit(m.Settings.AutoSnapshot ? $"saved after each scan · keeps {m.Settings.KeepSnapshots}" : "auto-snapshot is off (settings)", width).TrimEnd(), t.Faint);
            p.Text(x, bottom - 1, Cells.Fit(Shorten(m.Snapshots.Directory), width).TrimEnd(), t.Faint);
        }
    }

    private static void DrawSnapDiff(Painter p, BrowserModel m, SnapshotPair? pair, List<FolderChange> changes, HitMap hits, int x, int y, int width, int bottom)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        string spin = Spin[m.Tick % Spin.Length];
        void Say(string text, Rgb fg)
        {
            foreach (string line in Wrap(text, width))
            {
                if (y < bottom) p.Text(x, y++, line, fg);
            }
        }
        if (m.Scanning)
        {
            Say($"{spin} Changes can be compared when the scan finishes.", t.Muted);
            return;
        }
        if (m.SnapRows.Count < 2)
        {
            Say("Nothing to compare with yet. Next time you scan this folder you'll see what grew and what shrank here.", t.Muted);
            return;
        }
        if (m.SnapBase == m.SnapCompare)
        {
            Say("The base and the compared snapshot are the same: pick another with ↵ (compare) or b (base).", t.Muted);
            return;
        }
        if (m.SnapError is { } error)
        {
            Say("Couldn't read the snapshot: " + error, t.Bad);
            return;
        }
        if (pair is null)
        {
            Say($"{spin} Loading the snapshots…", t.Accent);
            return;
        }
        string where = m.SnapFolder.Length == 0 ? CrumbName(m, Hoard.Core.Model.NodeStore.Root) : CrumbName(m, Hoard.Core.Model.NodeStore.Root) + "/" + m.SnapFolder;
        string span = $"{pair.Base.TakenAt.ToLocalTime():dd MMM HH:mm} → " + (pair.Compare.File is null ? "now" : $"{pair.Compare.TakenAt.ToLocalTime():dd MMM HH:mm}");
        p.Text(x, y, Cells.Fit($"in {MiddleOut(where, width - Cells.Width(span) - 6)} · {span}", width).TrimEnd(), t.Muted);
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        const int BarHalf = 8;
        int nameW = Math.Max(8, width - 11 - (BarHalf * 2 + 3) - 8);
        c.Text(x, y + 1, Cells.Fit(Cells.Fit("CHANGE", 10, padLeft: true) + " " + Cells.Fit("shrank", BarHalf, padLeft: true) + " │ " + Cells.Fit("grew", BarHalf) + " PATH", width), heading);
        int top = y + 2, listBottom = bottom - 3;
        if (changes.Count == 0)
        {
            p.Text(x, top, Cells.Fit(m.SnapOnlyGrowth ? "Nothing grew here." : "Nothing changed here.", width).TrimEnd(), t.Ok, attr: TextAttr.Bold);
        }
        else
        {
            int visible = Math.Max(1, listBottom - top);
            m.SnapDiffSelected = Math.Clamp(m.SnapDiffSelected, 0, changes.Count - 1);
            m.SnapDiffScroll = ScrollFor(m.SnapDiffSelected, m.SnapDiffScroll, changes.Count, visible);
            long biggest = Math.Max(1, changes.Max(ch => Math.Abs(ch.Delta)));
            for (int i = 0; i < visible && m.SnapDiffScroll + i < changes.Count; i++)
            {
                int index = m.SnapDiffScroll + i;
                FolderChange ch = changes[index];
                int yy = top + i;
                bool sel = m.SnapDiffFocus && index == m.SnapDiffSelected;
                Rgb bg = sel ? t.Selection : t.Bg;
                if (sel)
                {
                    c.Fill(x - 1, yy, width + 2, 1, new Style(t.Text, bg));
                }
                bool grew = ch.Delta > 0;
                int cx = p.Text(x, yy, Cells.Fit(Signed(ch.Delta), 10, padLeft: true), grew ? t.Bad : t.Ok, bg, TextAttr.Bold) + 1;
                int cells = (int)Math.Ceiling((double)Math.Abs(ch.Delta) / biggest * BarHalf);
                string left = grew ? new string(' ', BarHalf) : new string(' ', BarHalf - cells) + new string('█', cells);
                string right = grew ? new string('█', cells) + new string(' ', BarHalf - cells) : new string(' ', BarHalf);
                cx = p.Text(cx, yy, left, t.Ok, bg);
                cx = p.Text(cx, yy, " │ ", t.Faint, bg);
                cx = p.Text(cx, yy, right, t.Bad, bg) + 1;
                string note = ch.Added ? "new" : ch.Removed ? "gone" : "";
                cx = p.Text(cx, yy, Cells.Fit(ch.Name + (ch.Path.Length > 0 ? "/" : ""), nameW), t.Text, bg);
                p.Text(cx + 1, yy, note, ch.Added ? t.Warn : t.Info, bg);
                hits.Items.Add((yy, x - 1, x + width + 1, 1000 + index)); // 1000+: rows of the diff pane
            }
        }
        long files = pair.Compare.Root.Files - pair.Base.Root.Files;
        p.Text(x, bottom - 2, Cells.Fit($"net {Signed(pair.Compare.Root.Size - pair.Base.Root.Size)} · {(files >= 0 ? "+" : "−")}{Units.Count(Math.Abs(files))} files", width).TrimEnd(), t.Text);
        p.Text(x, bottom - 1, Cells.Fit("→ then ↵ opens a folder · ⌫ up · f only growth · x exports this diff", width).TrimEnd(), t.Faint);
    }
}
