using System.Globalization;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    private static readonly string[] Spark = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    /// <summary>
    /// Expected total when the scan covers a whole volume (its used space), so the bar can show a
    /// real percentage; null for a folder, where the size can't be known in advance.
    /// </summary>
    private static long? ExpectedBytes(BrowserModel m) =>
        m.Disk is { } d && (d.Mount.TrimEnd('/') == m.RootPath.TrimEnd('/') || m.RootPath == "/") && d.Used > 0 ? d.Used : null;

    /// <summary>The scan in progress, full screen (plan.md §5.1; mock: examples/02-scanning.sh). b or ↵ skips to the results.</summary>
    public static HitMap ScanningView(ICanvas c, BrowserModel m)
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
        int w = c.Width, h = c.Height, x = 4, inner = w - 8;
        string spin = Spin[m.Tick % Spin.Length];
        p.HLine(0, 2, w, '╭', '╮', "Scanning " + CrumbName(m, NodeStore.Root),
            (m.Session.ExactSizes ? "on-disk size" : "apparent size") + " · hard links once");
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast ?? (m.Tip is { } tip ? "tip: " + tip : "results are browsable while scanning: b"), m.Toast is null ? t.Faint : t.Info);

        int bottom = h - 5; // rows above the notices
        int y = h >= 24 ? 4 : 3;

        // The big number: bytes found so far.
        long bytes = s.Size(NodeStore.Root);
        string size = Units.Size(bytes);
        int space = size.LastIndexOf(' ');
        string number = size[..space], unit = size[(space + 1)..];
        long? expected = ExpectedBytes(m);
        if (h >= 20 && BigWidth(number) + 4 + unit.Length < inner)
        {
            int end = BigText(p, x, y, number);
            p.Text(end + 2, y + 2, unit, t.Text, attr: TextAttr.Bold);
            int sx = Math.Max(end + 8, x + 30);
            if (sx + 20 < x + inner)
            {
                p.Text(sx, y, "found so far", t.Muted);
                if (expected is long e)
                {
                    int ex = p.Text(sx, y + 1, "≈ " + Units.Size(e), t.Text, attr: TextAttr.Bold);
                    p.Text(ex + 1, y + 1, Cells.Fit("in use on this disk", Math.Max(0, x + inner - ex - 1)).TrimEnd(), t.Muted);
                }
            }
            y += 4;
        }
        else
        {
            int ex = p.Text(x, y, size, t.Accent, attr: TextAttr.Bold);
            p.Text(ex + 1, y, "found so far", t.Muted);
            y += 2;
        }

        // Progress: a percentage when the total is known, otherwise a sweep that shows it's alive.
        int barW = Math.Max(10, Math.Min(78, inner - 6));
        if (expected is long total)
        {
            double f = Math.Min(0.99, (double)bytes / total);
            p.Bar(x, y, barW, f);
            p.Text(x + barW + 1, y, $"{(int)(f * 100),3}%", t.Accent, attr: TextAttr.Bold);
        }
        else
        {
            int seg = Math.Max(4, barW / 6);
            int pos = m.Tick % (2 * (barW - seg));
            if (pos >= barW - seg)
            {
                pos = 2 * (barW - seg) - pos;
            }
            for (int i = 0; i < barW; i++)
            {
                bool on = i >= pos && i < pos + seg;
                p.Text(x + i, y, on ? "█" : " ", t.GradientAt((double)i / barW), t.Track);
            }
            p.Text(x + barW + 1, y, spin, t.Accent);
        }
        y += 1;

        // Counters
        double secs = Math.Max(0.1, m.Elapsed.TotalSeconds);
        double rate = m.ScanRates.Count > 0 ? m.ScanRates[^1] : m.Session.Progress.Files / secs;
        if (y < bottom)
        {
            int cx = p.Text(x, y, Units.Count(m.Session.Progress.Files), t.Text, attr: TextAttr.Bold);
            cx = p.Text(cx + 1, y, "files  ", t.Muted);
            cx = p.Text(cx, y, Units.Count(m.Session.Progress.Directories), t.Text);
            cx = p.Text(cx + 1, y, "folders  ", t.Muted);
            cx = p.Text(cx, y, RateText(rate), t.Text);
            cx = p.Text(cx + 1, y, "files/s  ", t.Muted);
            cx = p.Text(cx, y, "elapsed ", t.Muted);
            p.Text(cx, y, m.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s", t.Text);
            y += 1;
        }

        // Throughput sparkline
        if (y < bottom && m.ScanRates.Count > 1)
        {
            int cx = p.Text(x, y, "throughput ", t.Muted);
            int room = Math.Max(0, Math.Min(m.ScanRates.Count, inner - 30));
            var recent = m.ScanRates.Skip(m.ScanRates.Count - room).ToList();
            double peak = Math.Max(1, m.ScanRates.Max());
            foreach (double r in recent)
            {
                cx = p.Text(cx, y, Spark[Math.Clamp((int)(r / peak * 7.99), 0, 7)], t.Accent2);
            }
            p.Text(cx + 2, y, $"peak {RateText(peak)}/s", t.Faint);
            y += 1;
        }

        // Biggest so far
        if (y + 3 < bottom)
        {
            y += 1;
            p.HLine(0, y, w, '├', '┤', "Biggest so far", $"{Units.Count(s.Children(NodeStore.Root).Count(m.IsGrowing))} still growing");
            y += 1;
            List<int> top = m.TopEntries();
            long biggest = Math.Max(1, top.Count > 0 ? m.SizeOf(top[0]) : 1);
            int nameW = Math.Max(10, inner - 4 - 10 - 2 - 24);
            for (int i = 0; i < top.Count && y < bottom; i++, y++)
            {
                int id = top[i];
                bool growing = m.IsGrowing(id);
                int cx = p.Text(x - 2, y, growing ? Spin[(m.Tick + i) % Spin.Length] : "✓", growing ? t.Accent : t.Ok);
                bool dir = s.Kind(id) == NodeKind.Directory;
                cx = p.Text(cx + 1, y, Cells.Fit(s.Name(id) + (dir ? "/" : ""), nameW), dir ? t.Text : t.Muted);
                cx = p.Text(cx, y, Cells.Fit(Units.Size(m.SizeOf(id)), 10, padLeft: true), t.Text, attr: TextAttr.Bold) + 2;
                p.Bar(cx, y, Math.Min(24, Math.Max(0, x + inner - cx)), (double)m.SizeOf(id) / biggest);
            }
        }

        // Notices
        p.HLine(0, h - 5, w, '├', '┤', "Notices");
        long errors = m.ErrorCount;
        p.Text(x - 2, h - 4, errors > 0 ? "!" : "✓", errors > 0 ? t.Warn : t.Ok);
        p.Text(x, h - 4, Cells.Fit(errors > 0 ? $"{Plural(errors, "folder")} couldn't be read so far (permission denied)" : "everything readable so far", inner).TrimEnd(), t.Muted);
        int kx = p.Text(x - 2, h - 3, "b", t.Accent, attr: TextAttr.Bold);
        p.Text(kx + 1, h - 3, Cells.Fit("or ↵ shows the results now; they keep filling in while the scan runs", inner).TrimEnd(), t.Muted);

        Footer(p, [("b ↵", "results now"), ("1-5 7", "open a tab"), ("t", "theme"), ("?", "help"), ("q", "quit")], $"{spin} scanning");
        Overlays(p, m);
        return hits;
    }

    private static string RateText(double perSecond) => perSecond >= 10_000
        ? (perSecond / 1000).ToString("0", CultureInfo.InvariantCulture) + " k"
        : Units.Count((long)perSecond);
}
