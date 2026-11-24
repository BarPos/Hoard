using System.Globalization;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>Three-row block digits for the big reclaimable number.</summary>
    private static readonly Dictionary<char, string[]> BigFont = new()
    {
        ['0'] = ["█▀█", "█ █", "▀▀▀"],
        ['1'] = ["▀█ ", " █ ", "▀▀▀"],
        ['2'] = ["▀▀█", "█▀▀", "▀▀▀"],
        ['3'] = ["▀▀█", " ▀█", "▀▀▀"],
        ['4'] = ["█ █", "▀▀█", "  ▀"],
        ['5'] = ["█▀▀", "▀▀█", "▀▀▀"],
        ['6'] = ["█▀▀", "█▀█", "▀▀▀"],
        ['7'] = ["▀▀█", "  █", "  ▀"],
        ['8'] = ["█▀█", "█▀█", "▀▀▀"],
        ['9'] = ["█▀█", "▀▀█", "▀▀▀"],
        ['.'] = [" ", " ", "▀"],
        [','] = [" ", " ", "▀"],
    };

    /// <summary>Width of <paramref name="text"/> in the big font (one column between glyphs).</summary>
    internal static int BigWidth(string text) =>
        text.Sum(ch => BigFont.TryGetValue(ch, out string[]? g) ? g[0].Length + 1 : 2) - 1;

    /// <summary>Draws digits three rows tall with the theme gradient running left to right.</summary>
    internal static int BigText(Painter p, int x, int y, string text)
    {
        int total = Math.Max(1, BigWidth(text));
        int cx = x;
        foreach (char ch in text)
        {
            if (!BigFont.TryGetValue(ch, out string[]? g))
            {
                cx += 2;
                continue;
            }
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < g[row].Length; col++)
                {
                    char glyph = g[row][col];
                    if (glyph != ' ')
                    {
                        p.Text(cx + col, y + row, glyph.ToString(), p.Theme.GradientAt((double)(cx + col - x) / total), attr: TextAttr.Bold);
                    }
                }
            }
            cx += g[0].Length + 1;
        }
        return cx - 1;
    }

    private static string Pct(double f) => (f * 100).ToString(f >= 0.1 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The start screen: what can be freed, the disk, the biggest folders, what it's made of (plan.md §7.2; mock: examples/03-overview.sh).</summary>
    public static HitMap Overview(ICanvas c, BrowserModel m)
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
        int lw = two ? w / 2 : w - 1; // x of the middle border (or the right border when narrow)
        int avail = h - 6;
        int topRows = Math.Clamp(avail * 6 / 10, 5, 12);
        int mid = 3 + topRows;
        string rootName = CrumbName(m, NodeStore.Root);

        // Frame
        if (two)
        {
            p.HLine(0, 2, lw + 1, '╭', '┬', "Reclaimable");
            p.HLine(lw, 2, w - lw, '┬', '╮', "Disk" + (m.Disk is { } dk ? " · " + dk.Name : ""));
            p.HLine(0, mid, lw + 1, '├', '┼', "Biggest in " + rootName);
            p.HLine(lw, mid, w - lw, '┼', '┤', "By type");
            p.HLine(0, h - 2, lw + 1, '╰', '┴', null, m.Toast, t.Info);
            p.HLine(lw, h - 2, w - lw, '┴', '╯');
            p.VLine(lw, 3, topRows);
            p.VLine(lw, mid + 1, h - 3 - mid);
        }
        else
        {
            p.HLine(0, 2, w, '╭', '╮', "Reclaimable", "wider window: disk & types");
            p.HLine(0, mid, w, '├', '┤', "Biggest in " + rootName);
            p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);
        }
        p.VLine(0, 3, topRows);
        p.VLine(0, mid + 1, h - 3 - mid);
        p.VLine(w - 1, 3, topRows);
        p.VLine(w - 1, mid + 1, h - 3 - mid);

        int leftW = lw - 3;
        long reclaim = DrawReclaimable(p, m, 2, 3, leftW, topRows);
        DrawTopEntries(p, m, hits, 2, mid + 1, leftW, h - 3 - mid);
        if (two)
        {
            int rx = lw + 2, rw = w - lw - 4;
            DrawDisk(p, m, rx, 3, rw, topRows, reclaim);
            DrawTypes(p, m, rx, mid + 1, rw, h - 3 - mid);
        }

        (string, string)[] keys = w >= 80
            ? [("↑↓", "move"), ("↵", "browse"), ("tab", "next view"), ("J", "junk"), ("O", "big & old"), ("t", "theme"), ("?", "help"), ("q", "quit")]
            : [("↑↓", "move"), ("↵", "browse"), ("J", "junk"), ("O", "old"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    /// <returns>Bytes that could be freed (0 until known).</returns>
    private static long DrawReclaimable(Painter p, BrowserModel m, int x, int y, int width, int rows)
    {
        Theme t = p.Theme;
        NodeStore s = m.Store;
        string spin = Spin[m.Tick % Spin.Length];
        void Line(int row, string text, Rgb fg, TextAttr attr = TextAttr.None)
        {
            if (row < rows)
            {
                p.Text(x, y + row, Cells.Fit(text, width).TrimEnd(), fg, attr: attr);
            }
        }
        if (m.Scanning)
        {
            Line(1, $"{spin} Adds up when the scan finishes.", t.Muted);
            Line(2, "Meanwhile: browse (2) or the map (3).", t.Faint);
            return 0;
        }
        if (m.JunkError is { } error)
        {
            Line(1, "The junk search failed: " + error, t.Bad);
            return 0;
        }
        JunkReport? junk = m.Junk;
        if (junk is null)
        {
            long total = Math.Max(1, s.Dirs(NodeStore.Root));
            double f = Math.Min(1, (double)m.JunkWork.FoldersChecked / total);
            Line(1, $"{spin} Looking for junk…", t.Accent, TextAttr.Bold);
            if (rows > 2)
            {
                int barW = Math.Max(6, Math.Min(30, width - 6));
                p.Bar(x, y + 2, barW, f);
                p.Text(x + barW + 1, y + 2, $"{(int)(f * 100),3}%", t.Text);
            }
            return 0;
        }

        long found = junk.Total(s), safe = junk.SafeTotal(s);
        BigOldReport? bigOld = m.BigOld;
        long old = bigOld is null ? 0 : m.BigOldBeyondJunk(); // files inside junk count once
        long dupes = m.DupesBeyondOthers();                   // … and so do copies already counted above
        long reclaim = found + old + dupes;
        long scanned = Math.Max(1, s.Size(NodeStore.Root));
        string size = Units.Size(reclaim);
        int space = size.LastIndexOf(' ');
        string number = space > 0 ? size[..space] : size, unit = space > 0 ? size[(space + 1)..] : "";
        int row0;
        if (rows >= 9 && BigWidth(number) + 1 + unit.Length <= width - 2)
        {
            int end = BigText(p, x + 1, y + 1, number);
            p.Text(end + 2, y + 3, unit, t.Text, attr: TextAttr.Bold);
            Line(4, " could be freed in " + CrumbName(m, NodeStore.Root), t.Muted);
            row0 = 6;
        }
        else
        {
            int ex = p.Text(x, y, Cells.Fit(size, width), t.Accent, attr: TextAttr.Bold);
            p.Text(ex + 1, y, Cells.Fit("could be freed", Math.Max(0, width - (ex - x) - 1)).TrimEnd(), t.Muted);
            row0 = 1;
        }

        // One line per source of reclaimable space (duplicates join when that tab exists).
        int barWidth = Math.Clamp(width - 16 - 11, 0, 16);
        bool details = rows - row0 >= 6; // room for a note under junk and big & old, the dupes line and the keys
        void Source(int row, string label, long bytes, Rgb dot, string? loading = null)
        {
            if (row >= rows)
            {
                return;
            }
            int cx = p.Text(x, y + row, "●", dot);
            cx = p.Text(cx + 1, y + row, Cells.Fit(label, 14), t.Text, attr: TextAttr.Bold);
            if (loading is not null)
            {
                p.Text(cx + 1, y + row, Cells.Fit(loading, Math.Max(0, x + width - cx - 1)).TrimEnd(), t.Muted);
                return;
            }
            if (barWidth >= 4)
            {
                p.Bar(cx + 1, y + row, barWidth, (double)bytes / scanned);
                cx += barWidth + 1;
            }
            p.Text(cx, y + row, Cells.Fit(Units.Size(bytes), 10, padLeft: true), t.Text, attr: TextAttr.Bold);
        }
        int r = row0;
        Source(r++, "Known junk", found, t.Accent2);
        if (details)
        {
            Line(r++, $"  {Units.Size(safe)} safe · {Units.Size(found - safe)} to review", t.Muted);
        }
        Source(r++, "Big & old", old, t.Warn, bigOld is null ? Spin[m.Tick % Spin.Length] + " looking…" : null);
        if (details && bigOld is not null)
        {
            BigOldOptions o = bigOld.Options;
            Line(r++, $"  {Plural(bigOld.Items.Count, "file")} ≥ {Units.Size(o.MinSize)}, untouched {BrowserModel.AgeWords(o.MinAgeDays)}+", t.Muted);
        }
        string? dupState = !m.DupesStarted ? "D looks for them"
            : m.DupesRunning ? $"{Spin[m.Tick % Spin.Length]} comparing… {(m.DupWork.BytesTotal > 0 ? 100 * m.DupWork.BytesDone / m.DupWork.BytesTotal : 0)}%"
            : null;
        Source(r++, "Duplicates", dupes, t.Info, dupState);
        if (rows - 1 >= r + (details ? 1 : 0))
        {
            int ky = y + rows - 1;
            int kx = p.Text(x + 1, ky, "J", t.Accent, attr: TextAttr.Bold);
            kx = p.Text(kx + 1, ky, found > 0 ? "clean junk" : "no junk", t.Muted) + 3;
            kx = p.Text(kx, ky, "O", t.Accent, attr: TextAttr.Bold);
            kx = p.Text(kx + 1, ky, "big & old", t.Muted) + 3;
            kx = p.Text(kx, ky, "D", t.Accent, attr: TextAttr.Bold);
            p.Text(kx + 1, ky, Cells.Fit("dupes", Math.Max(0, x + width - kx - 1)).TrimEnd(), t.Muted);
        }
        return reclaim;
    }

    private static void DrawDisk(Painter p, BrowserModel m, int x, int y, int width, int rows, long reclaim)
    {
        Theme t = p.Theme;
        NodeStore s = m.Store;
        if (m.Disk is not { } disk)
        {
            p.Text(x, y + 1, Cells.Fit("Disk size unknown for this folder.", width).TrimEnd(), t.Muted);
            return;
        }
        const int LabelW = 14;
        int barW = Math.Max(4, width - LabelW - 8);
        var lines = new List<(bool Gap, Action<int> Draw)>();
        void BarLine(string label, double f, Rgb? colour = null) => lines.Add((false, yy =>
        {
            p.Text(x, yy, Cells.Fit(label, LabelW), t.Text);
            p.Bar(x + LabelW, yy, barW, f, colour);
            p.Text(x + LabelW + barW, yy, Cells.Fit(Pct(f), 7, padLeft: true), t.Text, attr: TextAttr.Bold);
        }));
        void Note(string text, Rgb fg) => lines.Add((false, yy => p.Text(x, yy, Cells.Fit(text, width).TrimEnd(), fg)));
        void Gap() => lines.Add((true, _ => { }));

        double total = Math.Max(1, disk.Total);
        BarLine("used now", disk.Used / total);
        Note($"{Units.Size(disk.Used)} of {Units.Size(disk.Total)} · {Units.Size(disk.Free)} free", t.Muted);
        if (reclaim > 0)
        {
            Gap();
            long after = Math.Max(0, disk.Used - reclaim);
            BarLine("after cleanup", after / total, t.Ok);
            Note($"{Units.Size(after)} · frees {Units.Size(reclaim)} once the Trash is emptied", t.Muted);
        }
        Gap();
        long scanned = s.Size(NodeStore.Root);
        string name = CrumbName(m, NodeStore.Root);
        BarLine(Cells.Fit(name, LabelW - 1).TrimEnd(), disk.Used > 0 ? Math.Min(1, (double)scanned / disk.Used) : 0, t.Accent2);
        Note($"{Units.Size(scanned)} of the {Units.Size(disk.Used)} used{(m.Scanning ? " (so far)" : "")}", t.Muted);
        if (m.Clones is { Files: > 0 } clones)
        {
            Gap();
            Note($"⧉ {Units.Count(clones.Files)} files share blocks with APFS clones · up to {Units.Size(clones.SharedBytes)} counted twice", t.Info);
        }
        if (m.HardLinkDuplicates > 0)
        {
            Gap();
            long saved = m.Session.Result?.HardLinkBytesSaved ?? 0;
            Note($"≡ {Units.Count(m.HardLinkDuplicates)} hard links counted once · −{Units.Size(saved)}", t.Info);
        }

        // Short of room: drop the spacing first, then whatever is last. A blank first row when it fits.
        bool dropGaps = lines.Count + 1 > rows;
        int row = lines.Count(l => !l.Gap) + 1 <= rows ? 1 : 0;
        foreach (var (gap, draw) in lines)
        {
            if (gap && dropGaps)
            {
                continue;
            }
            if (row >= rows)
            {
                break;
            }
            draw(y + row++);
        }
    }

    private static void DrawTopEntries(Painter p, BrowserModel m, HitMap hits, int x, int y, int width, int rows)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        List<int> top = m.TopEntries();
        if (top.Count == 0)
        {
            string why = m.Scanning ? Spin[m.Tick % Spin.Length] + " Still scanning…" : "This folder is empty.";
            p.Text(x, y, Cells.Fit(why, width).TrimEnd(), t.Muted);
            m.OverviewShown = 1;
            return;
        }
        int shown = top.Count <= rows ? top.Count : Math.Max(1, rows - 1);
        m.OverviewShown = shown;
        m.OverviewSelected = Math.Clamp(m.OverviewSelected, 0, shown - 1);
        long biggest = Math.Max(1, m.SizeOf(top[0]));
        int barW = Math.Clamp(width - 4 - 16 - 11, 0, 24);
        int nameW = width - 4 - 10 - (barW > 0 ? barW + 2 : 0);
        for (int i = 0; i < shown; i++)
        {
            int id = top[i];
            int yy = y + i;
            bool sel = i == m.OverviewSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, yy, width + 2, 1, new Style(t.Text, bg));
            }
            bool dir = s.Kind(id) == NodeKind.Directory;
            int cx = p.Text(x, yy, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx, yy, m.IsGrowing(id) ? Spin[(m.Tick + i) % Spin.Length] : "■", t.GradientAt((double)i / Math.Max(1, shown)), bg);
            cx = p.Text(cx + 1, yy, Cells.Fit(s.Name(id) + (dir ? "/" : ""), nameW), dir ? t.Text : t.Muted, bg, dir ? TextAttr.Bold : TextAttr.None);
            cx = p.Text(cx, yy, Cells.Fit(Units.Size(m.SizeOf(id)), 10, padLeft: true), t.Text, bg) + 2;
            if (barW > 0)
            {
                p.Bar(cx, yy, barW, (double)m.SizeOf(id) / biggest);
            }
            hits.Items.Add((yy, x - 1, x + width + 1, i));
        }
        if (shown < top.Count)
        {
            p.Text(x + 2, y + shown, Cells.Fit($"+ {Units.Count(top.Count - shown)} more · ↵ opens in the browser", width - 2).TrimEnd(), t.Faint);
        }
    }

    private static void DrawTypes(Painter p, BrowserModel m, int x, int y, int width, int rows)
    {
        Theme t = p.Theme;
        string spin = Spin[m.Tick % Spin.Length];
        if (m.Scanning)
        {
            p.Text(x, y, Cells.Fit($"{spin} Adds up when the scan finishes.", width).TrimEnd(), t.Muted);
            return;
        }
        if (m.TypesError is { } error)
        {
            p.Text(x, y, Cells.Fit("Couldn't add up file types: " + error, width).TrimEnd(), t.Bad);
            return;
        }
        if (m.Types is not { } report)
        {
            p.Text(x, y, Cells.Fit($"{spin} Adding up file types…", width).TrimEnd(), t.Accent, attr: TextAttr.Bold);
            return;
        }
        if (report.Categories.Count == 0)
        {
            p.Text(x, y, Cells.Fit("No files here.", width).TrimEnd(), t.Muted);
            return;
        }
        int listRows = Math.Max(1, rows - 1);
        var cats = report.Categories.Select(cat => (Label: FileTypes.Label(cat.Category), cat.Bytes)).ToList();
        if (cats.Count > listRows)
        {
            int keep = listRows - 1;
            cats = [.. cats.Take(keep), ("everything else", cats.Skip(keep).Sum(cc => cc.Bytes))];
        }
        double total = Math.Max(1, report.TotalBytes);
        const int LabelW = 16;
        // ■ + space, label, size (9) + gap, bar, pct (6)
        int barW = Math.Max(0, Math.Min(20, width - 2 - LabelW - 11 - 6));
        for (int i = 0; i < cats.Count; i++)
        {
            var (label, bytes) = cats[i];
            int yy = y + i;
            int cx = p.Text(x, yy, "■", t.GradientAt((double)i / cats.Count));
            cx = p.Text(cx + 1, yy, Cells.Fit(label, LabelW), t.Text);
            cx = p.Text(cx, yy, Cells.Fit(Units.Size(bytes), 9, padLeft: true), t.Text, attr: TextAttr.Bold) + 2;
            if (barW > 0)
            {
                p.Bar(cx, yy, barW, bytes / total);
                cx += barW;
            }
            p.Text(cx, yy, Cells.Fit(Pct(bytes / total), 6, padLeft: true), t.Muted);
        }
        if (rows > cats.Count)
        {
            // The Empty tab's counts when ready (they leave installed software and app insides alone).
            string counts = m.Empty is { } e
                ? $"{Plural(e.TotalFolders, "empty folder")} · {Plural(e.ZeroFileCount, "zero-byte file")} · E lists them"
                : $"{Plural(report.EmptyFolders, "empty folder")} · {Plural(report.ZeroByteFiles, "zero-byte file")}";
            p.Text(x, y + rows - 1, Cells.Fit(counts, width).TrimEnd(), t.Faint);
        }
    }
}
