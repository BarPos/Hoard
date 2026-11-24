using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>Settings with a live theme chooser (plan.md §7.6; mock: examples/16-settings.sh).</summary>
    public static HitMap SettingsView(ICanvas c, BrowserModel m)
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
        Header(p, m.View, "settings", hits);
        string where = m.SettingsPath is { } path ? "saved to " + Shorten(path) : "not saved (no settings file in use)";
        p.Text(1, 1, "⌂", t.Accent);
        p.Text(3, 1, "▸ Settings", t.Accent, attr: TextAttr.Bold);
        p.Text(Math.Max(15, c.Width - Cells.Width(where) - 1), 1, Cells.Fit(where, Math.Max(0, c.Width - 16)).TrimEnd(), t.Muted);

        int w = c.Width, h = c.Height;
        bool two = w >= 90;
        int lw = two ? Math.Min(56, w / 2) : w - 1;
        if (two)
        {
            p.HLine(0, 2, lw + 1, '╭', '┬', "Settings");
            p.HLine(lw, 2, w - lw, '┬', '╮', "Themes", $"{Theme.All.Length} · ←→ on Theme picks one");
            p.HLine(0, h - 2, lw + 1, '╰', '┴', null, m.Toast, t.Info);
            p.HLine(lw, h - 2, w - lw, '┴', '╯');
            p.VLine(lw, 3, h - 5);
        }
        else
        {
            p.HLine(0, 2, w, '╭', '╮', "Settings");
            p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);
        }
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);

        // Settings list, grouped, scrolled if the window is short.
        SettingsItem[] items = SettingsItems.All;
        m.SettingsRow = Math.Clamp(m.SettingsRow, 0, m.SettingsRowCount - 1);
        var lines = new List<(string? Section, int Item)>();
        string? last = null;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Section != last)
            {
                if (last is not null)
                {
                    lines.Add((null, -1));
                }
                lines.Add((items[i].Section, -1));
                last = items[i].Section;
            }
            lines.Add((null, i));
        }
        // Exclude patterns: one row each, then "+ add pattern".
        lines.Add((null, -1));
        lines.Add(("Exclude patterns", -1));
        for (int i = 0; i <= m.Settings.Exclude.Count; i++)
        {
            lines.Add((null, items.Length + i));
        }
        int x = 2, width = lw - 3;
        int top = 3, noteRows = 2, visible = Math.Max(1, h - 3 - top - noteRows);
        int selLine = lines.FindIndex(l => l.Item == m.SettingsRow);
        int scroll = ScrollFor(selLine, 0, lines.Count, visible);
        const int LabelW = 18;
        for (int r = 0; r < visible && scroll + r < lines.Count; r++)
        {
            var (section, item) = lines[scroll + r];
            int y = top + r;
            if (section is not null)
            {
                p.Text(x, y, section, t.Accent, attr: TextAttr.Bold);
                continue;
            }
            if (item < 0)
            {
                continue;
            }
            bool sel = item == m.SettingsRow;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            if (item >= items.Length)
            {
                int pi = item - items.Length;
                int px = p.Text(x, y, sel ? "►" : " ", t.Accent, bg) + 1;
                if (pi < m.Settings.Exclude.Count)
                {
                    px = p.Text(px, y, "⊘ ", t.Faint, bg);
                    p.Text(px, y, Cells.Fit(m.Settings.Exclude[pi], Math.Max(1, x + width - px)).TrimEnd(), t.Text, bg);
                }
                else if (m.PatternInput is { } typed)
                {
                    p.Text(px, y, Cells.Fit("+ " + typed + "▏", Math.Max(1, x + width - px)).TrimEnd(), t.Text, t.Bg, TextAttr.Bold);
                }
                else
                {
                    p.Text(px, y, "+ add pattern…", sel ? t.Accent : t.Muted, bg);
                }
                hits.Items.Add((y, x - 1, x + width + 1, item));
                continue;
            }
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx + 1, y, Cells.Fit(items[item].Label, LabelW), t.Text, bg);
            string value = items[item].Value(m);
            cx = p.Text(cx, y, "◂ ", sel ? t.Accent : t.Faint, bg);
            cx = p.Text(cx, y, Cells.Fit(value, Math.Max(1, x + width - cx - 2)).TrimEnd(), sel ? t.Text : t.Accent2, bg, TextAttr.Bold);
            p.Text(cx, y, " ▸", sel ? t.Accent : t.Faint, bg);
            hits.Items.Add((y, x - 1, x + width + 1, item));
        }
        // What the highlighted setting does.
        bool patterns = m.SettingsRow >= items.Length;
        string note = !patterns ? items[m.SettingsRow].Note
            : m.PatternInput is not null ? "gitignore-style: *.tmp · build/ · ~/Library/Caches · //Volumes/** · ↵ adds, esc cancels"
            : m.SettingsRow - items.Length < m.Settings.Exclude.Count ? "del removes this pattern · R rescans with the new patterns"
            : "↵ adds a pattern · X in the list excludes a folder · .hoardignore files work too";
        p.Text(x, h - 4, Cells.Fit(note, width).TrimEnd(), t.Muted);
        p.Text(x, h - 3, Cells.Fit("patterns apply from the next scan · R rescans now", width).TrimEnd(), t.Faint);

        if (two)
        {
            DrawThemePreviews(p, m, lw + 2, 3, w - lw - 4, h - 3);
        }

        Footer(p, [("↑↓", "move"), ("←→", "change"), ("t", "next theme"), ("esc", "close"), ("?", "help"), ("q", "quit")], ", opens settings");
        Overlays(p, m);
        return hits;
    }

    /// <summary>
    /// Every theme drawn in its own colours, so choosing one doesn't take guesswork. With more
    /// themes than fit, the list scrolls to keep the one in use in view.
    /// </summary>
    private static void DrawThemePreviews(Painter p, BrowserModel m, int x, int y, int width, int bottom)
    {
        ICanvas c = p.Canvas;
        Theme[] all = Theme.All;
        int avail = Math.Max(2, bottom - y);
        int per = avail >= all.Length * 3 ? 3 : 2;
        int visible = Math.Max(1, Math.Min(all.Length, avail / per));
        int current = Math.Max(0, Array.IndexOf(all, m.Theme));
        int first = Math.Clamp(current - visible / 2, 0, all.Length - visible);
        for (int i = 0; i < visible; i++)
        {
            Theme th = all[first + i];
            int yy = y + i * per;
            bool on = th == m.Theme;
            c.Fill(x - 1, yy, width + 2, 2, new Style(th.Text, th.Bg));
            int cx = p.Text(x, yy, on ? "●" : "○", on ? th.Accent : th.Faint, th.Bg, TextAttr.Bold);
            cx = p.Text(cx + 1, yy, "◆ ", th.Accent, th.Bg);
            for (int k = 0; k < 5; k++)
            {
                cx = p.Text(cx, yy, "HOARD"[k].ToString(), th.GradientAt(k / 6.0), th.Bg, TextAttr.Bold);
            }
            cx = p.Text(cx + 2, yy, th.Name, th.Text, th.Bg, TextAttr.Bold);
            string about = on ? "in use · " + th.Description : th.Description;
            p.Text(cx + 2, yy, Cells.Fit(about, Math.Max(0, x + width - cx - 2)).TrimEnd(), th.Muted, th.Bg);
            // A size bar and the status colours, as they appear in the app.
            int barW = Math.Max(6, Math.Min(24, width - 30));
            new Painter(c, th).Bar(x + 2, yy + 1, barW, 0.7);
            int sx = x + 2 + barW + 2;
            foreach (var (label, colour) in new[] { ("ok", th.Ok), ("review", th.Warn), ("old", th.Bad), ("info", th.Info) })
            {
                if (sx + label.Length + 2 >= x + width)
                {
                    break;
                }
                sx = p.Text(sx, yy + 1, "■", colour, th.Bg);
                sx = p.Text(sx + 1, yy + 1, label, th.Muted, th.Bg) + 2;
            }
        }
        if (first > 0)
        {
            p.Text(x + width - 1, y, "↑", p.Theme.Faint);
        }
        if (first + visible < all.Length)
        {
            p.Text(x + width - 1, Math.Min(bottom - 1, y + visible * per - 1), "↓", p.Theme.Faint);
        }
    }
}
