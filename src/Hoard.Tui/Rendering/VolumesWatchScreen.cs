using System.Globalization;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    private static string VolumeIcon(VolumeKind k) => k switch
    {
        VolumeKind.Network => "≋",
        VolumeKind.External => "⇄",
        VolumeKind.Removable => "▤",
        _ => "▣",
    };

    private static string VolumeNote(Volume v) => v.Kind switch
    {
        VolumeKind.Network => "network share · scans are slower",
        VolumeKind.External => "external disk",
        VolumeKind.Removable => "removable" + (v.Format.Contains("fat", StringComparison.OrdinalIgnoreCase) ? " · no hard links on " + v.Format : ""),
        _ when v.System => "system volume · shares the startup disk's space",
        _ => "internal disk" + (v.Format.Equals("apfs", StringComparison.OrdinalIgnoreCase) ? " · APFS volumes in a container share its free space" : ""),
    };

    /// <summary>Mounted volumes; pick one to scan (plan.md §7.2; mock: examples/01-volumes.sh).</summary>
    public static HitMap VolumesView(ICanvas c, BrowserModel m)
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
        List<Volume> list = m.VolumeList;
        Header(p, m.View, $"{list.Count} volume{(list.Count == 1 ? "" : "s")}", hits);
        p.Text(1, 1, "⌂", t.Accent);
        p.Text(3, 1, "▸ Volumes", t.Accent, attr: TextAttr.Bold);
        string hint = "pick a volume or folder to scan";
        p.Text(Math.Max(14, c.Width - Cells.Width(hint) - 1), 1, hint, t.Muted);
        int w = c.Width, h = c.Height, x = 2, inner = w - 4;
        p.HLine(0, 2, w, '╭', '╮', "Mounted volumes", "fullest first");
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        p.HLine(0, h - 5, w, '├', '┤', "Shortcuts");
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast ?? "hoard /path skips this screen", m.Toast is null ? t.Faint : t.Info);

        bool roomy = h >= 22;
        int step = roomy ? 4 : 2;
        int visible = Math.Max(1, (h - 5 - 3) / step);
        m.VolumeSelected = Math.Clamp(m.VolumeSelected, 0, Math.Max(0, list.Count - 1));
        int scroll = ScrollFor(m.VolumeSelected, 0, list.Count, visible);
        if (list.Count == 0)
        {
            p.Text(x, 4, "No volumes found.", t.Muted);
        }
        for (int i = 0; i < visible && scroll + i < list.Count; i++)
        {
            Volume v = list[scroll + i];
            int y = 3 + i * step + (roomy ? 1 : 0);
            bool sel = scroll + i == m.VolumeSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, inner + 2, roomy ? 3 : 2, new Style(t.Text, bg));
            }
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg) + 1;
            cx = p.Text(cx, y, VolumeIcon(v.Kind), t.Accent2, bg, TextAttr.Bold) + 2;
            int nameW = Math.Max(10, Math.Min(26, inner / 4));
            cx = p.Text(cx, y, Cells.Fit(v.Name, nameW), t.Text, bg, TextAttr.Bold);
            string where = $"{v.Format} · {v.Mount}";
            string of = $"{Units.Size(v.Used)} of {Units.Size(v.Total)}";
            p.Text(cx, y, Cells.Fit(where, Math.Max(0, x + inner - cx - Cells.Width(of) - 2)).TrimEnd(), t.Muted, bg);
            p.Text(x + inner - Cells.Width(of), y, of, t.Text, bg);
            int barW = Math.Max(10, inner - 30);
            p.Bar(x + 5, y + 1, barW, v.UsedFraction, v.UsedFraction > 0.9 ? t.Bad : null);
            p.Text(x + 6 + barW, y + 1, Cells.Fit(v.UsedFraction.ToString("0.0%", CultureInfo.InvariantCulture) + "  " + Units.Size(v.Free) + " free", Math.Max(0, inner - barW - 6)).TrimEnd(), t.Text, bg);
            if (roomy)
            {
                p.Text(x + 5, y + 2, Cells.Fit(VolumeNote(v), inner - 5).TrimEnd(), t.Faint, bg);
            }
            hits.Items.Add((y, x - 1, x + inner + 1, scroll + i));
            hits.Items.Add((y + 1, x - 1, x + inner + 1, scroll + i));
        }

        int sy = h - 4;
        if (m.PathInput is { } typed)
        {
            int px = p.Text(x, sy, "path: ", t.Accent, attr: TextAttr.Bold);
            p.Text(px, sy, Cells.Fit(typed + "▏", inner - 6).TrimEnd(), t.Text, attr: TextAttr.Bold);
            p.Text(x, sy + 1, "↵ scans it · esc cancels · ~ is your home folder", t.Muted);
        }
        else
        {
            int kx = p.Text(x, sy, "~", t.Accent, attr: TextAttr.Bold);
            kx = p.Text(kx + 1, sy, "home folder    ", t.Muted);
            kx = p.Text(kx, sy, "p", t.Accent, attr: TextAttr.Bold);
            kx = p.Text(kx + 1, sy, "type a path    ", t.Muted);
            kx = p.Text(kx, sy, "H", t.Accent, attr: TextAttr.Bold);
            p.Text(kx + 1, sy, m.ShowSystemVolumes ? "hide system volumes" : "show system volumes (Preboot, VM, Data…)", t.Muted);
            p.Text(x, sy + 1, Cells.Fit($"now scanning: {Shorten(m.RootPath)}", inner).TrimEnd(), t.Faint);
        }
        long free = list.Where(v => !v.System).Sum(v => v.Free);
        Footer(p, [("↑↓", "select"), ("↵", "scan"), ("~", "home"), ("p", "path"), ("esc", "back"), ("?", "help"), ("q", "quit")], $"free total {Units.Size(free)}");
        Overlays(p, m);
        return hits;
    }

    /// <summary>Live changes while watching (plan.md §9.3; mock: examples/17-watch.sh).</summary>
    public static HitMap WatchView(ICanvas c, BrowserModel m)
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
        WatchSession? ws = m.Watching;
        string live = ws is null ? "stopped" : ws.Paused ? "❚❚ paused" : (m.Tick / 6 % 2 == 0 ? "● LIVE" : "○ LIVE");
        p.HLine(0, 2, w, '╭', '╮', "Watch " + CrumbName(m, NodeStore.Root), ws is null ? null : $"{live} · since {ws.Started:HH:mm:ss} · {Units.Count(ws.EventCount)} events", ws?.Paused == true ? t.Warn : t.Ok);
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast ?? (ws?.Error is { } err ? "watcher: " + err : "only changed folders are re-read"), m.Toast is null ? t.Faint : t.Info);
        if (ws is null)
        {
            p.Text(x, 4, "Not watching. w starts.", t.Muted);
            Footer(p, [("w", "watch"), ("?", "help"), ("q", "quit")], "");
            return hits;
        }

        // Top entries: size now, change since watching started, recent activity.
        var top = m.TopEntries().Take(Math.Max(3, (h - 12) / 2)).ToList();
        int y = 3;
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        int nameW = Math.Max(10, Math.Min(30, inner - 46));
        c.Text(x, y++, Cells.Fit("  " + Cells.Fit("FOLDER", nameW) + Cells.Fit("SIZE", 10, padLeft: true) + Cells.Fit("Δ SINCE START", 16, padLeft: true) + "   ACTIVITY (2 min)", inner), heading);
        DateTime now = DateTime.Now;
        foreach (int id in top)
        {
            long delta = m.Store.Size(id) - (ws.Baseline.TryGetValue(id, out long b) ? b : 0);
            int cx = p.Text(x, y, "■", t.GradientAt((double)top.IndexOf(id) / Math.Max(1, top.Count)));
            cx = p.Text(cx + 1, y, Cells.Fit(m.Store.Name(id), nameW), t.Text);
            cx = p.Text(cx, y, Cells.Fit(Units.Size(m.Store.Size(id)), 10, padLeft: true), t.Text);
            cx = p.Text(cx, y, Cells.Fit(delta == 0 ? "·" : Signed(delta), 16, padLeft: true), delta > 0 ? t.Bad : delta < 0 ? t.Ok : t.Faint, attr: delta != 0 ? TextAttr.Bold : TextAttr.None) + 3;
            if (ws.Activity.TryGetValue(id, out var times))
            {
                var buckets = new int[8];
                foreach (DateTime at in times)
                {
                    int bi = 7 - (int)((now - at).TotalSeconds / 15);
                    if (bi >= 0)
                    {
                        buckets[bi]++;
                    }
                }
                int max = Math.Max(1, buckets.Max());
                foreach (int n in buckets)
                {
                    cx = p.Text(cx, y, n == 0 ? " " : Spark[Math.Clamp(n * 7 / max, 0, 7)], t.Accent2);
                }
            }
            y++;
        }

        // The change feed, newest first.
        y++;
        p.HLine(0, y, w, '├', '┤', "Change feed", "newest first");
        int top0 = y + 1, rows = h - 3 - top0;
        if (ws.Feed.Count == 0)
        {
            p.Text(x, top0, Cells.Fit(ws.Pending.Count > 0 ? $"{Spin[m.Tick % Spin.Length]} reading {ws.Pending.Count} changed folder(s)…" : "No changes yet. Save, copy or delete something inside to see it here.", inner).TrimEnd(), t.Muted);
        }
        m.WatchSelected = Math.Clamp(m.WatchSelected, 0, Math.Max(0, ws.Feed.Count - 1));
        int scroll = ScrollFor(m.WatchSelected, 0, ws.Feed.Count, Math.Max(1, rows));
        for (int i = 0; i < rows && scroll + i < ws.Feed.Count; i++)
        {
            WatchChange ch = ws.Feed[scroll + i];
            int yy = top0 + i;
            bool sel = scroll + i == m.WatchSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, yy, inner + 2, 1, new Style(t.Text, bg));
            }
            int cx = p.Text(x, yy, ch.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), t.Faint, bg) + 2;
            cx = p.Text(cx, yy, Cells.Fit(Signed(ch.Delta), 10, padLeft: true), ch.Delta > 0 ? t.Bad : t.Ok, bg, TextAttr.Bold) + 2;
            p.Text(cx, yy, Cells.Fit(MiddleOut(Shorten(ch.Path), x + inner - cx), x + inner - cx).TrimEnd(), t.Text, bg);
            hits.Items.Add((yy, x - 1, x + inner + 1, scroll + i));
        }
        long net = ws.Feed.Sum(f => f.Delta);
        Footer(p, [("↑↓", "move"), ("↵", "open folder"), ("p", ws.Paused ? "resume" : "pause"), ("c", "clear"), ("esc", "stop"), ("?", "help"), ("q", "quit")],
            ws.Feed.Count == 0 ? "" : "net " + Signed(net));
        Overlays(p, m);
        return hits;
    }
}
