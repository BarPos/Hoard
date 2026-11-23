using System.Globalization;
using Hoard.Core.Actions;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

/// <summary>Where clickable things ended up on the last frame.</summary>
public sealed class HitMap
{
    public List<(int X0, int X1, int Node)> Crumbs { get; } = [];
    /// <summary>Tab labels in the header, for clicks.</summary>
    public List<(int X0, int X1, ViewKind View)> Tabs { get; } = [];
    /// <summary>Top-level treemap blocks (Map view only).</summary>
    public List<MapBlock> Blocks { get; } = [];
}

/// <summary>Draws whole screens. Pure functions of state → canvas, so they can be snapshot-tested.</summary>
public static partial class Screens
{
    private static string Ico(NodeKind k) => k switch
    {
        NodeKind.Directory => "▸",
        NodeKind.Symlink => "@",
        NodeKind.MountPoint => "↧",
        NodeKind.Alias => "↺",
        NodeKind.Excluded => "⊘",
        _ => "·",
    };

    public static string Ago(long seconds)
    {
        double days = seconds / 86400.0;
        return days switch
        {
            < 1 => "today",
            < 2 => "yesterday",
            < 31 => $"{(int)days} days ago",
            < 365 => $"{(int)(days / 30.4)} months ago",
            _ => (days / 365.25).ToString("0.0", CultureInfo.InvariantCulture) + " years ago",
        };
    }

    private static Rgb AgeColour(Theme t, long ageSeconds) =>
        ageSeconds < 90 * 86400 ? t.Ok : ageSeconds < 365 * 86400 ? t.Warn : t.Bad;

    // ── chrome ───────────────────────────────────────────────────────────────

    private static readonly (ViewKind View, string Key, string Label)[] TabList =
        [(ViewKind.Browse, "2", "Browse"), (ViewKind.Map, "3", "Map"), (ViewKind.Junk, "7", "Junk")];

    private static void Header(Painter p, ViewKind active, string right, HitMap? hits = null)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        c.Fill(0, 0, c.Width, 1, new Style(t.Text, t.Bar));
        int x = p.Text(1, 0, "◆ ", t.Accent, t.Bar);
        string logo = "HOARD";
        for (int i = 0; i < logo.Length; i++)
        {
            x = p.Text(x, 0, logo[i].ToString(), t.GradientAt(i / 6.0), t.Bar, TextAttr.Bold);
        }
        x = p.Text(x + 1, 0, "│", t.Faint, t.Bar);
        foreach (var (view, key, label) in TabList)
        {
            int x0 = x + 1;
            bool on = view == active;
            x = p.Text(x0, 0, " " + key + " ", on ? Rgb.Hex(0x000000) : t.Faint, on ? t.Accent : t.Bar);
            x = p.Text(x, 0, label + " ", on ? Rgb.Hex(0x000000) : t.Muted, on ? t.Accent : t.Bar, on ? TextAttr.Bold : TextAttr.None);
            hits?.Tabs.Add((x0, x, view));
        }
        int rw = Cells.Width(right);
        if (c.Width - rw - 1 > x + 1)
        {
            p.Text(c.Width - rw - 1, 0, right, t.Text, t.Bar);
        }
    }

    /// <summary>
    /// Key hints, most useful first. The last two hints (help and quit) are always shown;
    /// the others are dropped from the end when space runs out, then the right-hand status.
    /// </summary>
    private static void Footer(Painter p, IReadOnlyList<(string Key, string Desc)> keys, string right)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        int y = c.Height - 1;
        c.Fill(0, y, c.Width, 1, new Style(t.Text, t.Bar));
        static int Need((string Key, string Desc) k) => Cells.Width(k.Key) + 1 + Cells.Width(k.Desc) + 2;
        int essential = Math.Min(2, keys.Count);
        var shown = keys.Skip(keys.Count - essential).ToList();
        int used = 1 + shown.Sum(Need);
        int rw = Cells.Width(right);
        if (used + rw + 2 > c.Width)
        {
            right = "";
            rw = 0;
        }
        int limit = c.Width - rw - 2;
        var optional = new List<(string, string)>();
        foreach (var k in keys.Take(keys.Count - essential))
        {
            if (used + Need(k) > limit)
            {
                break;
            }
            optional.Add(k);
            used += Need(k);
        }
        int x = 1;
        foreach (var (key, desc) in optional.Concat(shown))
        {
            x = p.Text(x, y, key, t.Accent, t.Bar, TextAttr.Bold);
            x = p.Text(x + 1, y, desc, t.Muted, t.Bar) + 2;
        }
        if (rw > 0 && c.Width - rw - 1 > 0)
        {
            p.Text(c.Width - rw - 1, y, right, t.Text, t.Bar);
        }
    }

    public static void TooSmall(ICanvas c, Theme t)
    {
        var p = new Painter(c, t);
        p.Clear();
        string[] lines =
        [
            "◆ HOARD",
            "",
            "Please make the window a bit bigger.",
            $"Now {c.Width}×{c.Height}, needs at least {Layout.MinWidth}×{Layout.MinHeight}.",
        ];
        int y0 = Math.Max(0, c.Height / 2 - lines.Length / 2);
        for (int i = 0; i < lines.Length; i++)
        {
            string s = Cells.Fit(lines[i], c.Width).TrimEnd();
            p.Text(Math.Max(0, (c.Width - Cells.Width(s)) / 2), y0 + i, s, i == 0 ? t.Accent : i == 2 ? t.Text : t.Muted);
        }
    }

    private static readonly string[] Spin = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // ── browser ──────────────────────────────────────────────────────────────

    public static HitMap Browser(ICanvas c, BrowserModel m)
    {
        var hits = new HitMap();
        Theme t = m.Theme;
        var layout = new Layout(c.Width, c.Height);
        if (layout.TooSmall)
        {
            TooSmall(c, t);
            return hits;
        }
        if (m.View == ViewKind.Map)
        {
            return Map(c, m);
        }
        if (m.View == ViewKind.Junk)
        {
            return JunkView(c, m);
        }
        var p = new Painter(c, t);
        p.Clear();
        NodeStore s = m.Store;
        string spin = Spin[m.Tick % Spin.Length];
        string sortName = SortName(m);
        string arrow = m.Descending ? "▼" : "▲";
        Top(p, m, hits, showSort: true);

        // Box(es)
        int lw = layout.ListWidth;
        string title = m.Current == NodeStore.Root ? Shorten(m.RootPath) : s.Name(m.Current);
        string count = $"{m.Rows.Count} items";
        if (layout.ShowDetails)
        {
            p.HLine(0, layout.BoxTop, lw + 4, '╭', '┬', title, count);
            p.HLine(lw + 3, layout.BoxTop, layout.DetailsWidth + 4, '┬', '╮', "Details");
            p.HLine(0, layout.BoxBottom, lw + 4, '╰', '┴', null, m.Toast, t.Info);
            p.HLine(lw + 3, layout.BoxBottom, layout.DetailsWidth + 4, '┴', '╯', null, "o reveal · y copy path");
        }
        else
        {
            p.HLine(0, layout.BoxTop, c.Width, '╭', '╮', title, count);
            p.HLine(0, layout.BoxBottom, c.Width, '╰', '╯', null, m.Toast, t.Info);
        }
        int boxRows = layout.BoxBottom - layout.BoxTop - 1;
        p.VLine(0, layout.BoxTop + 1, boxRows);
        p.VLine(c.Width - 1, layout.BoxTop + 1, boxRows);
        if (layout.ShowDetails)
        {
            p.VLine(lw + 3, layout.BoxTop + 1, boxRows);
        }

        DrawList(p, m, layout, sortName, arrow);
        if (layout.ShowDetails)
        {
            DrawDetails(p, m, layout);
        }

        (string, string)[] keys = c.Width >= 80
            ? [("↑↓", "move"), ("→", "open"), ("←", "up"), ("␣", "mark"), ("d", "delete"), ("tab", "map"), ("s", "sort"), ("z", "size mode"), ("t", "theme"), ("o", "reveal"), ("?", "help"), ("q", "quit")]
            : [("↑↓", "move"), ("→←", "in/out"), ("␣", "mark"), ("d", "delete"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    /// <summary>Right side of the footer: marks win, then scanning, then notes about the scan.</summary>
    private static string Plural(long n, string word) => $"{Units.Count(n)} {word}{(n == 1 ? "" : "s")}";

    private static string FooterStatus(BrowserModel m)
    {
        var (count, size) = m.MarkedSummary();
        if (count > 0)
        {
            return $"● {count} marked · {Units.Size(size)}";
        }
        return m.Scanning ? $"{Spin[m.Tick % Spin.Length]} scanning… q stops"
            : m.ErrorCount > 0 ? $"! {Units.Count(m.ErrorCount)} unreadable"
            : m.HardLinkDuplicates > 0 ? $"≡ {Units.Count(m.HardLinkDuplicates)} hard links once" : "";
    }

    private static void Overlays(Painter p, BrowserModel m)
    {
        if (m.Dialog is { } d)
        {
            DeleteDialogBox(p, d);
        }
        else if (m.HelpOpen)
        {
            Help(p);
        }
    }

    private static void DeleteDialogBox(Painter p, DeleteDialog d)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        DeletePlan plan = d.Plan;
        int w = Math.Min(c.Width - 4, 78);
        int listRows = Math.Clamp(c.Height - 16, 1, 7);
        var lines = new List<(string Text, Rgb Fg, TextAttr Attr)>();
        int shown = Math.Min(plan.Items.Count, listRows);
        for (int i = 0; i < shown; i++)
        {
            DeleteItem it = plan.Items[i];
            bool done = d.Stage == DialogStage.Running && i < d.Done;
            string files = it.Kind == NodeKind.Directory ? Plural(it.Files, "file") : "";
            string size = Units.Size(it.Size);
            int nameW = Math.Max(8, w - 8 - 10 - 14);
            lines.Add(($"{(done ? "✓" : it.Kind == NodeKind.Directory ? "▸" : "·")} {Cells.Fit(Shorten(it.Path), nameW)}{Cells.Fit(size, 10, padLeft: true)}  {Cells.Fit(files, 12)}",
                done ? t.Ok : t.Text, TextAttr.None));
        }
        if (plan.Items.Count > shown)
        {
            lines.Add(($"  + {plan.Items.Count - shown} more", t.Muted, TextAttr.None));
        }
        foreach (var (path, reason) in plan.Refused.Take(3))
        {
            lines.Add(($"⊘ {Shorten(path)}: won't delete, {reason}", t.Warn, TextAttr.None));
        }
        lines.Add(("", t.Text, TextAttr.None));
        lines.Add((plan.IsEmpty ? "Nothing here can be deleted."
            : $"Total {Units.Size(plan.TotalSize)} · {Plural(plan.TotalFiles, "file")}", t.Text, TextAttr.Bold));

        string title = d.Stage == DialogStage.Confirm || d.Permanent && d.Stage == DialogStage.Running
            ? $"Delete {plan.Items.Count} item{(plan.Items.Count == 1 ? "" : "s")} permanently?"
            : $"Move {plan.Items.Count} item{(plan.Items.Count == 1 ? "" : "s")} to the {d.TrashName}?";
        int h = lines.Count + 7;
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, (c.Height - h) / 2);
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        Rgb frame = d.Stage == DialogStage.Confirm ? t.Bad : t.Border;
        c.Fill(x0, y0, w, 1, new Style(frame, t.Panel), '─');
        c.Text(x0, y0, "╭", new Style(frame, t.Panel));
        c.Text(x0 + w - 1, y0, "╮", new Style(frame, t.Panel));
        c.Text(x0 + 2, y0, " " + Cells.Fit(title, w - 6).TrimEnd() + " ", new Style(d.Stage == DialogStage.Confirm ? t.Bad : t.Accent, t.Panel, TextAttr.Bold));
        for (int i = 1; i < h - 1; i++)
        {
            c.Text(x0, y0 + i, "│", new Style(frame, t.Panel));
            c.Text(x0 + w - 1, y0 + i, "│", new Style(frame, t.Panel));
        }
        c.Fill(x0, y0 + h - 1, w, 1, new Style(frame, t.Panel), '─');
        c.Text(x0, y0 + h - 1, "╰", new Style(frame, t.Panel));
        c.Text(x0 + w - 1, y0 + h - 1, "╯", new Style(frame, t.Panel));

        int y = y0 + 2;
        foreach (var (text, fg, attr) in lines)
        {
            c.Text(x0 + 3, y++, Cells.Fit(text, w - 6).TrimEnd(), new Style(fg, t.Panel, attr));
        }
        y++;
        switch (d.Stage)
        {
            case DialogStage.Running:
            {
                int n = Math.Max(1, plan.Items.Count);
                c.Text(x0 + 3, y, d.Permanent ? "deleting… " : "moving… ", new Style(t.Muted, t.Panel));
                var barP = new Painter(c, t);
                barP.Bar(x0 + 14, y, Math.Max(10, w - 26), (double)d.Done / n);
                c.Text(x0 + w - 10, y, $"{d.Done}/{n}", new Style(t.Text, t.Panel));
                break;
            }
            case DialogStage.Confirm:
            {
                string prompt = $"Type {d.ConfirmWord} to confirm: ";
                c.Text(x0 + 3, y, Cells.Fit(prompt, w - 6).TrimEnd(), new Style(t.Text, t.Panel, TextAttr.Bold));
                int fx = x0 + 3 + Math.Min(Cells.Width(prompt), w - 20);
                int fw = Math.Max(8, x0 + w - 3 - fx);
                c.Fill(fx, y, fw, 1, new Style(t.Text, t.Bg));
                c.Text(fx, y, Cells.Fit(d.Typed + "▏", fw).TrimEnd(), new Style(d.Typed == d.ConfirmWord ? t.Ok : t.Text, t.Bg, TextAttr.Bold));
                c.Text(x0 + 3, y + 2, Cells.Fit(d.Hint ?? "This can't be undone. ↵ deletes · esc goes back", w - 6).TrimEnd(),
                    new Style(d.Hint is null ? t.Muted : t.Warn, t.Panel));
                break;
            }
            default:
            {
                string[] labels = [$" Move to {d.TrashName} ", " Delete permanently ", " Cancel "];
                int bx = x0 + 3;
                for (int i = 0; i < 3; i++)
                {
                    bool focus = d.Choice == i, enabled = d.IsEnabled(i);
                    string label = (focus ? "►" : " ") + labels[i];
                    Style st = focus ? new Style(Rgb.Hex(0x000000), i == DeleteDialog.PermanentChoice ? t.Bad : t.Accent, TextAttr.Bold)
                        : new Style(enabled ? (i == DeleteDialog.PermanentChoice ? t.Bad : t.Text) : t.Faint, t.Panel);
                    c.Text(bx, y, label, st);
                    bx += Cells.Width(label) + 3;
                }
                string note = !d.TrashAvailable ? $"The {d.TrashName} isn't supported on this system yet."
                    : "←→ choose · ↵ confirm · esc cancel";
                c.Text(x0 + 3, y + 2, Cells.Fit(note, w - 6).TrimEnd(), new Style(t.Muted, t.Panel));
                break;
            }
        }
    }

    private static string SortName(BrowserModel m) =>
        m.Sort switch { SortField.Name => "name", SortField.Files => "files", SortField.Modified => "modified", _ => "size" };

    /// <summary>Header bar and breadcrumbs, shared by every view.</summary>
    private static void Top(Painter p, BrowserModel m, HitMap hits, bool showSort)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        string spin = Spin[m.Tick % Spin.Length];
        string sortName = SortName(m);
        string arrow = m.Descending ? "▼" : "▲";
        string secs = m.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        Header(p, m.View, m.Scanning
            ? $"{spin} scanning · {Units.Count(s.Files(NodeStore.Root))} files · {secs}"
            : $"{Units.Count(s.Files(NodeStore.Root))} files · {secs}", hits);

        // Breadcrumbs: the scan root as one segment, then each folder below it.
        long curSize = m.SizeOf(m.Current);
        long rootSize = m.SizeOf(NodeStore.Root);
        string crumbRight = $"{Units.Size(curSize)} · {(rootSize > 0 ? 100.0 * curSize / rootSize : 0).ToString("0.0", CultureInfo.InvariantCulture)}% of scan{(showSort ? $" · sort {sortName} {arrow}" : "")} · {(m.Apparent ? "apparent" : m.Session.ExactSizes ? "on-disk" : "apparent*")}";
        var chain = new List<int>();
        for (int n = m.Current; n != NodeStore.None; n = s.Parent(n))
        {
            chain.Insert(0, n);
        }
        int crumbLimit = c.Width - (c.Width >= 90 ? Cells.Width(crumbRight) + 3 : 1);
        int cx = p.Text(1, 1, "⌂", t.Accent);
        // Always keep the scan root and the current folder; drop middle segments if it doesn't fit.
        int skip = 1;
        int Needed(int from) => Cells.Width(CrumbName(m, chain[0])) + 3 + chain.Skip(from).Sum(id => Cells.Width(CrumbName(m, id)) + 3);
        while (skip < chain.Count - 1 && cx + Needed(skip) + 4 > crumbLimit)
        {
            skip++;
        }
        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0 && i < skip)
            {
                if (i == 1)
                {
                    cx = p.Text(cx + 1, 1, "▸ …", t.Faint);
                }
                continue;
            }
            int id = chain[i];
            cx = p.Text(cx + 1, 1, "▸", t.Faint);
            string name = Cells.Fit(CrumbName(m, id), Math.Max(4, crumbLimit - cx - 2)).TrimEnd();
            int x0 = cx + 1;
            bool last = i == chain.Count - 1;
            cx = p.Text(x0, 1, name, last ? t.Accent : t.Text, attr: last ? TextAttr.Bold : TextAttr.None);
            hits.Crumbs.Add((x0, cx, id));
        }
        if (c.Width >= 90)
        {
            p.Text(c.Width - Cells.Width(crumbRight) - 1, 1, crumbRight, t.Muted);
        }

    }

    private static string CrumbName(BrowserModel m, int id)
    {
        if (id != NodeStore.Root)
        {
            return m.Store.Name(id);
        }
        string root = Shorten(m.RootPath);
        return Cells.Width(root) <= 28 ? root : "…/" + Path.GetFileName(root.TrimEnd('/'));
    }

    public static string Shorten(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal)))
        {
            return "~" + path[home.Length..];
        }
        return path;
    }

    private static void DrawList(Painter p, BrowserModel m, Layout l, string sortName, string arrow)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        int x = l.ListX;

        string Col(string label, string field) => sortName == field ? label + " " + arrow : label;
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        string head = "    " + Cells.Fit(Col("NAME", "name"), l.NameWidth) + Cells.Fit(Col("SIZE", "size"), 9, padLeft: true) + "  "
                      + (l.BarWidth > 0 ? Cells.Fit("USAGE", l.BarWidth) + " " : "") + Cells.Fit("%", 6, padLeft: true)
                      + (l.ShowAge ? " " + (sortName == "modified" ? arrow : "A") : "  ");
        c.Text(x, l.HeadingRow, Cells.Fit(head, l.ListWidth), heading);

        if (m.Rows.Count == 0)
        {
            string why = m.IsPending(m.Current) ? Spin[m.Tick % Spin.Length] + " Still scanning this folder…"
                : (s.Flags(m.Current) & NodeFlags.Error) != 0 ? "This folder couldn't be read (permission denied)." : "This folder is empty.";
            p.Text(x + 2, l.FirstRow + 1, Cells.Fit(why, l.ListWidth - 2).TrimEnd(), t.Muted);
            p.Text(x + 2, l.FirstRow + 2, Cells.Fit("Press ← to go back up.", l.ListWidth - 2).TrimEnd(), t.Faint);
            return;
        }

        int visible = l.VisibleRows;
        if (m.Selected < m.Scroll)
        {
            m.Scroll = m.Selected;
        }
        if (m.Selected >= m.Scroll + visible)
        {
            m.Scroll = m.Selected - visible + 1;
        }
        m.Scroll = Math.Clamp(m.Scroll, 0, Math.Max(0, m.Rows.Count - visible));

        long parentSize = Math.Max(1, m.SizeOf(m.Current));
        long now = m.Now;
        for (int row = 0; row < visible && m.Scroll + row < m.Rows.Count; row++)
        {
            int index = m.Scroll + row;
            int id = m.Rows[index];
            int y = l.FirstRow + row;
            bool sel = index == m.Selected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, l.ListWidth + 2, 1, new Style(t.Text, bg));
            }
            NodeKind kind = s.Kind(id);
            bool dir = kind == NodeKind.Directory;
            long size = m.SizeOf(id);
            double frac = (double)size / parentSize;

            int cx = x;
            cx = p.Text(cx, y, sel ? "►" : " ", t.Accent, bg);
            cx = m.IsMarked(id) ? p.Text(cx, y, "●", t.Accent2, bg)
                : p.Text(cx, y, (s.Flags(id) & NodeFlags.Error) != 0 ? "!" : " ", t.Warn, bg);
            bool pending = dir && m.IsGrowing(id);
            cx = p.Text(cx, y, pending ? Spin[(m.Tick + index) % Spin.Length] : Ico(kind), dir ? t.Accent : kind is NodeKind.File ? t.Muted : t.Info, bg);
            cx = p.Text(cx + 1, y, Cells.Fit(s.Name(id), l.NameWidth),
                kind is NodeKind.Directory or NodeKind.File ? t.Text : t.Muted, bg, dir ? TextAttr.Bold : TextAttr.None);

            string note = kind switch
            {
                NodeKind.MountPoint => "other disk",
                NodeKind.Alias => "counted elsewhere",
                NodeKind.Excluded => "excluded",
                NodeKind.Symlink => "symlink",
                _ => "",
            };
            cx = p.Text(cx, y, Cells.Fit(note.Length > 0 && size == 0 ? "—" : Units.Size(size), 9, padLeft: true), t.Text, bg) + 2;
            if (l.BarWidth > 0)
            {
                if (note.Length > 0)
                {
                    p.Text(cx, y, Cells.Fit(note, l.BarWidth), t.Faint, bg);
                }
                else
                {
                    p.Bar(cx, y, l.BarWidth, frac);
                }
                cx += l.BarWidth + 1;
            }
            cx = p.Text(cx, y, Cells.Fit((frac * 100).ToString("0.0", CultureInfo.InvariantCulture), 6, padLeft: true), t.Muted, bg);
            if (l.ShowAge)
            {
                p.Text(cx + 1, y, "●", AgeColour(t, now - s.MTime(id)), bg);
            }
        }
        if (m.Rows.Count > visible)
        {
            // Scroll indicator on the pane's right edge.
            int barX = x + l.ListWidth;
            int thumb = Math.Max(1, visible * visible / m.Rows.Count);
            int pos = (int)((long)m.Scroll * (visible - thumb) / Math.Max(1, m.Rows.Count - visible));
            for (int i = 0; i < visible; i++)
            {
                p.Text(barX, l.FirstRow + i, i >= pos && i < pos + thumb ? "┃" : " ", t.Faint);
            }
        }
    }

    private static void DrawDetails(Painter p, BrowserModel m, Layout l)
    {
        Theme t = p.Theme;
        NodeStore s = m.Store;
        int id = m.SelectedNode ?? m.Current;
        int x = l.DetailsX, w = l.DetailsWidth, y = l.HeadingRow;
        int bottom = l.BoxBottom;
        NodeKind kind = s.Kind(id);
        bool dir = kind == NodeKind.Directory;

        void Line(string text, Rgb fg, TextAttr a = TextAttr.None)
        {
            if (y < bottom)
            {
                p.Text(x, y, Cells.Fit(text, w).TrimEnd(), fg, attr: a);
            }
            y++;
        }
        void Pair(string k, string v, Rgb? vc = null)
        {
            if (y < bottom)
            {
                p.Text(x, y, Cells.Fit(k, w), t.Muted);
                p.Text(x + w - Cells.Width(v), y, v, vc ?? t.Text);
            }
            y++;
        }

        Line((dir ? "▾ " : Ico(kind) + " ") + s.Name(id) + (dir ? "/" : ""), t.Accent, TextAttr.Bold);
        Line(Shorten(m.FullPath(id)), t.Muted);
        y++;
        Pair("Size on disk", Units.Size(s.Size(id)));
        Pair("Apparent size", Units.Size(s.Apparent(id)));
        if (dir)
        {
            Pair("Files", Units.Count(s.Files(id)));
            Pair("Folders", Units.Count(s.Dirs(id)));
        }
        long age = m.Now - s.MTime(id);
        Pair("Modified", Ago(age), AgeColour(t, age));
        string? note = dir && m.IsGrowing(id) ? "Still being scanned; numbers are growing." : kind switch
        {
            NodeKind.MountPoint => "On another disk; not scanned.",
            NodeKind.Alias => "Same folder as another path in this scan; counted there.",
            NodeKind.Excluded => "Excluded by a pattern; not scanned.",
            NodeKind.Symlink => "A symbolic link; hoard doesn't follow links.",
            _ => (s.Flags(id) & NodeFlags.HardLinkDuplicate) != 0 ? "≡ Hard link: counted at another path."
               : (s.Flags(id) & NodeFlags.HardLinkPrimary) != 0 ? "≡ Hard-linked file, counted here once."
               : (s.Flags(id) & NodeFlags.Error) != 0 ? "! Couldn't be read completely." : null,
        };
        if (note is not null)
        {
            y++;
            Line(note, note.StartsWith('!') ? t.Warn : t.Info);
        }
        if (dir && s.ChildCount(id) > 0 && y < bottom - 2)
        {
            y++;
            Line("Inside", t.Accent);
            long total = Math.Max(1, m.SizeOf(id));
            int nameW = Math.Min(14, w / 3);
            int barW = Math.Max(4, w - nameW - 6);
            foreach (int k in s.Children(id).OrderByDescending(m.SizeOf).Take(Math.Clamp(bottom - y, 0, 10)))
            {
                double f = (double)m.SizeOf(k) / total;
                p.Text(x, y, Cells.Fit(s.Name(k), nameW), t.Text);
                p.Bar(x + nameW + 1, y, barW, f, t.Info);
                p.Text(x + nameW + 2 + barW, y, Cells.Fit(((int)Math.Round(f * 100)).ToString(CultureInfo.InvariantCulture) + "%", 4, padLeft: true), t.Muted);
                y++;
            }
        }
    }

    private static void Help(Painter p)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        (string, string)[] rows =
        [
            ("tab  2 3 7", "switch view: list · treemap · junk (also J)"),
            ("↑↓  j k", "move (map: ←↑↓→ between blocks)"), ("→ ↵  l", "open folder / zoom in"), ("← ⌫  h", "up one level / zoom out"), ("g  G", "top / bottom"),
            ("PgUp PgDn", "page"), ("s  S", "sort field / reverse"), ("z", "on-disk ↔ apparent size"), ("+  -", "treemap depth"),
            ("t", "next theme"), ("o", "reveal in Finder"), ("y", "copy path"),
            ("␣  A", "mark · mark all"), ("d", "delete marked (or selected)…"), ("u", "clear marks · undo delete"),
            ("esc", "close / back"), ("q", "quit"),
            ("mouse", "click selects · double-click opens · wheel scrolls"),
        ];
        int w = Math.Min(c.Width - 4, 58), h = rows.Length + 4;
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, (c.Height - h) / 2);
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        p.HLine(x0, y0, w, '╭', '╮', "Keys", "any key closes");
        for (int i = 1; i < h - 1; i++)
        {
            p.Text(x0, y0 + i, "│", t.Border, t.Panel);
            p.Text(x0 + w - 1, y0 + i, "│", t.Border, t.Panel);
        }
        p.HLine(x0, y0 + h - 1, w, '╰', '╯');
        for (int i = 0; i < rows.Length; i++)
        {
            var (k, d) = rows[i];
            p.Text(x0 + 3, y0 + 2 + i, Cells.Fit(k, 11), t.Accent, t.Panel, TextAttr.Bold);
            p.Text(x0 + 15, y0 + 2 + i, Cells.Fit(d, w - 18).TrimEnd(), t.Text, t.Panel);
        }
    }
}
