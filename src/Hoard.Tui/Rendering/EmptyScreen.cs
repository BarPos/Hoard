using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    private static (string Text, Rgb Bg) Badge(Theme t, ZeroSafety s) => s switch
    {
        ZeroSafety.Safe => (" SAFE    ", t.Ok),
        ZeroSafety.Keep => (" KEEP    ", t.Faint),
        _ => (" CAREFUL ", t.Warn),
    };

    /// <summary>●, ◐ or ○: all, some or none of these are marked.</summary>
    private static string MarkState(BrowserModel m, List<int> nodes)
    {
        int marked = nodes.Count(m.IsMarked);
        return marked == 0 ? "○" : marked == nodes.Count ? "●" : "◐";
    }

    /// <summary>Empty folders and zero-byte files (plan.md §6.4; mock: examples/11-empty.sh).</summary>
    public static HitMap EmptyView(ICanvas c, BrowserModel m)
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
        EmptyReport? r = m.Empty;
        bool side = w >= 110 && r is not null;
        int lw = side ? w - 43 : w - 1;
        string note = r is null ? "" : $"{Plural(r.TotalFolders, "empty folder")} · {Plural(r.ZeroFileCount, "zero-byte file")}";
        if (side)
        {
            p.HLine(0, 2, lw + 1, '╭', '┬', "Empty", note);
            p.HLine(lw, 2, w - lw, '┬', '╮', "Selected");
            p.HLine(0, h - 2, lw + 1, '╰', '┴', null, m.Toast, t.Info);
            p.HLine(lw, h - 2, w - lw, '┴', '╯');
            p.VLine(lw, 3, h - 5);
        }
        else
        {
            p.HLine(0, 2, w, '╭', '╮', "Empty", note);
            p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);
        }
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        int x = 2, width = lw - 3;
        string spin = Spin[m.Tick % Spin.Length];

        if (m.Scanning)
        {
            p.Text(x, 4, Cells.Fit($"{spin} Ready when the scan finishes. Meanwhile: browse (2) or the map (3).", width).TrimEnd(), t.Muted);
        }
        else if (m.EmptyError is { } error)
        {
            p.Text(x, 4, Cells.Fit("Couldn't look for empty folders: " + error, width).TrimEnd(), t.Bad);
        }
        else if (r is null)
        {
            p.Text(x, 4, Cells.Fit($"{spin} Looking for empty folders and zero-byte files…", width).TrimEnd(), t.Accent, attr: TextAttr.Bold);
        }
        else
        {
            int kx = p.Text(x, 3, r.IgnoredKeepFiles ? ".gitkeep / .keep don't count as content" : ".gitkeep / .keep count as content", t.Muted);
            kx = p.Text(kx + 2, 3, "K", t.Accent, attr: TextAttr.Bold);
            p.Text(kx + 1, 3, Cells.Fit("changes · .DS_Store never counts · git, apps and installed software are left alone", Math.Max(0, x + width - kx - 1)).TrimEnd(), t.Faint);
            List<EmptyRow> rows = m.EmptyRows();
            if (rows.Count == 0)
            {
                p.Text(x, 5, Cells.Fit("No empty folders or zero-byte files here. Tidy!", width).TrimEnd(), t.Ok, attr: TextAttr.Bold);
            }
            else
            {
                DrawEmptyRows(p, m, r, rows, hits, x, 4, h - 4, width);
                if (side)
                {
                    DrawEmptyDetail(p, m, r, rows[Math.Clamp(m.EmptySelected, 0, rows.Count - 1)], lw + 2, 3, w - lw - 4, h - 3);
                }
            }
            int ax = p.Text(x, h - 3, "A", t.Accent, attr: TextAttr.Bold);
            p.Text(ax + 1, h - 3, Cells.Fit("marks every empty folder and the SAFE files · they take no space, but removing them declutters", Math.Max(0, width - 2)).TrimEnd(), t.Muted);
        }

        (string, string)[] keys = [("↑↓", "move"), ("→←", "expand"), ("␣", "mark"), ("A", "mark all"), ("K", "keep files"), ("d", "delete"), ("↵", "show in list"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    private static void DrawEmptyRows(Painter p, BrowserModel m, EmptyReport r, List<EmptyRow> rows, HitMap hits, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        // Display lines: section headings (not selectable) and rows.
        var lines = new List<(string? Heading, int Row)>();
        bool folderHead = false, zeroHead = false;
        for (int i = 0; i < rows.Count; i++)
        {
            bool zero = rows[i].Kind is EmptyRowKind.ZeroGroup or EmptyRowKind.ZeroFile;
            if (!zero && !folderHead)
            {
                lines.Add(($"Empty folders · {Units.Count(r.TotalFolders)}", -1));
                folderHead = true;
            }
            if (zero && !zeroHead)
            {
                if (folderHead)
                {
                    lines.Add((null, -1));
                }
                lines.Add(($"Zero-byte files · {Units.Count(r.ZeroFileCount)}", -1));
                zeroHead = true;
            }
            lines.Add((null, i));
        }
        m.EmptySelected = Math.Clamp(m.EmptySelected, 0, rows.Count - 1);
        int visible = Math.Max(1, bottom - top);
        int selLine = lines.FindIndex(l => l.Row == m.EmptySelected);
        // Keep the heading above the first row in view when scrolled to the top.
        m.EmptyScroll = ScrollFor(selLine, m.EmptyScroll, lines.Count, visible);
        if (m.EmptySelected == 0)
        {
            m.EmptyScroll = 0;
        }
        bool showWhere = width >= 80;
        const int CountW = 18, WhereW = 22, BadgeW = 9;
        int nameW = width - 4 - CountW - (showWhere ? WhereW : 0) - BadgeW - 1;
        for (int li = 0; li < visible && m.EmptyScroll + li < lines.Count; li++)
        {
            var (heading, ri) = lines[m.EmptyScroll + li];
            int y = top + li;
            if (heading is not null)
            {
                p.Text(x, y, heading, t.Accent, attr: TextAttr.Bold);
                continue;
            }
            if (ri < 0)
            {
                continue;
            }
            EmptyRow row = rows[ri];
            bool sel = ri == m.EmptySelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            int cx = x;
            switch (row.Kind)
            {
                case EmptyRowKind.FolderGroup:
                {
                    List<int> nodes = m.EmptyRowNodes(row);
                    bool open = m.EmptyExpanded.Contains("f:" + row.Group);
                    cx = p.Text(cx, y, open ? "▾" : "▸", t.Accent, bg) + 1;
                    cx = p.Text(cx, y, MarkState(m, nodes), t.Warn, bg) + 1;
                    string name = row.Group == NodeStore.Root ? "at the top of " + CrumbName(m, NodeStore.Root) : s.Name(row.Group) + "/";
                    cx = p.Text(cx, y, Cells.Fit(name, nameW), t.Text, bg, TextAttr.Bold);
                    p.Text(cx, y, Cells.Fit(Plural(nodes.Sum(m.EmptyCount), "empty folder"), CountW, padLeft: true), t.Muted, bg);
                    break;
                }
                case EmptyRowKind.Folder:
                {
                    bool marked = m.IsMarked(row.Item);
                    cx = p.Text(cx + 2, y, marked ? "●" : "□", marked ? t.Warn : t.Faint, bg) + 1;
                    string rel = s.RelativePath(row.Item);
                    if (row.Group != NodeStore.Root)
                    {
                        string topRel = s.RelativePath(row.Group);
                        rel = rel.Length > topRel.Length ? rel[(topRel.Length + 1)..] : "(the whole folder)";
                    }
                    int nested = m.EmptyCount(row.Item) - 1;
                    string label = MiddleOut(rel + "/", nameW - 2);
                    cx = p.Text(cx, y, Cells.Fit(label, nameW - 2), marked ? t.Warn : t.Text, bg);
                    if (nested > 0)
                    {
                        p.Text(cx, y, Cells.Fit($"+ {Units.Count(nested)} inside", CountW, padLeft: true), t.Faint, bg);
                    }
                    break;
                }
                case EmptyRowKind.ZeroGroup:
                {
                    ZeroGroup g = r.Zero[row.Group];
                    List<int> nodes = m.EmptyRowNodes(row);
                    bool open = m.EmptyExpanded.Contains("z:" + g.Pattern);
                    cx = p.Text(cx, y, open ? "▾" : "▸", t.Accent, bg) + 1;
                    cx = p.Text(cx, y, g.Safety == ZeroSafety.Keep ? "⊘" : MarkState(m, nodes), g.Safety == ZeroSafety.Keep ? t.Faint : t.Warn, bg) + 1;
                    cx = p.Text(cx, y, Cells.Fit(g.Pattern, nameW), t.Text, bg, TextAttr.Bold);
                    cx = p.Text(cx, y, Cells.Fit(Plural(nodes.Count, "file"), CountW, padLeft: true), t.Muted, bg) + 1;
                    if (showWhere)
                    {
                        cx = p.Text(cx + 1, y, Cells.Fit(g.Where, WhereW - 2), t.Faint, bg) + 1;
                    }
                    var (text, badgeBg) = Badge(t, g.Safety);
                    p.Text(cx, y, text, Theme.On(badgeBg), badgeBg, TextAttr.Bold);
                    break;
                }
                case EmptyRowKind.ZeroFile:
                {
                    bool marked = m.IsMarked(row.Item);
                    cx = p.Text(cx + 2, y, marked ? "●" : "·", marked ? t.Warn : t.Faint, bg) + 1;
                    p.Text(cx, y, Cells.Fit(MiddleOut(s.RelativePath(row.Item), width - 5), width - 5).TrimEnd(), marked ? t.Warn : t.Text, bg);
                    break;
                }
            }
            hits.Items.Add((y, x - 1, x + width + 1, ri));
        }
    }

    private static void DrawEmptyDetail(Painter p, BrowserModel m, EmptyReport r, EmptyRow row, int x, int y, int width, int bottom)
    {
        Theme t = p.Theme;
        NodeStore s = m.Store;
        var lines = new List<(string Text, Rgb Fg, TextAttr Attr)>();
        void Add(string text, Rgb fg, TextAttr attr = TextAttr.None) => lines.Add((text, fg, attr));
        switch (row.Kind)
        {
            case EmptyRowKind.FolderGroup:
                Add(row.Group == NodeStore.Root ? CrumbName(m, NodeStore.Root) : s.Name(row.Group) + "/", t.Accent, TextAttr.Bold);
                Add($"{Plural(m.EmptyRowNodes(row).Count, "empty folder")} at the outermost level,", t.Text);
                Add($"{Plural(m.EmptyRowNodes(row).Sum(m.EmptyCount), "empty folder")} counting nested ones.", t.Text);
                Add("", t.Text);
                Add("→ shows them · ␣ marks them all", t.Muted);
                break;
            case EmptyRowKind.Folder:
                Add(s.Name(row.Item) + "/", t.Accent, TextAttr.Bold);
                Add(Shorten(m.FullPath(row.Item)), t.Muted);
                Add("", t.Text);
                int nested = m.EmptyCount(row.Item) - 1;
                Add(nested > 0 ? $"Holds {Plural(nested, "empty folder")} and nothing else." : "Holds nothing.", t.Text);
                Add("Modified " + Ago(m.Now - s.MTime(row.Item)) + ".", t.Text);
                Add("", t.Text);
                Add("Some programs expect their folders to exist; they usually recreate them.", t.Faint);
                break;
            case EmptyRowKind.ZeroGroup:
            {
                ZeroGroup g = r.Zero[row.Group];
                Add(g.Pattern, t.Accent, TextAttr.Bold);
                Add($"{Plural(m.EmptyRowNodes(row).Count, "file")}, mostly in {g.Where}", t.Muted);
                Add("", t.Text);
                Add(g.Safety switch { ZeroSafety.Safe => "Safe to remove.", ZeroSafety.Keep => "Kept: can't be marked.", _ => "Be careful." },
                    g.Safety switch { ZeroSafety.Safe => t.Ok, ZeroSafety.Keep => t.Faint, _ => t.Warn }, TextAttr.Bold);
                Add(char.ToUpperInvariant(g.Why[0]) + g.Why[1..] + ".", t.Text);
                break;
            }
            case EmptyRowKind.ZeroFile:
                Add(s.Name(row.Item), t.Accent, TextAttr.Bold);
                Add(Shorten(m.FullPath(row.Item)), t.Muted);
                Add("", t.Text);
                Add("0 bytes · modified " + Ago(m.Now - s.MTime(row.Item)), t.Text);
                Add("", t.Text);
                Add(r.Zero[row.Group].Why, t.Faint);
                break;
        }
        int yy = y;
        foreach (var (text, fg, attr) in lines)
        {
            // Wrap long lines by words.
            foreach (string part in Wrap(text, width))
            {
                if (yy >= bottom)
                {
                    return;
                }
                p.Text(x, yy++, part, fg, attr: attr);
            }
        }
    }

    internal static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0)
        {
            yield return "";
            yield break;
        }
        var line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && Cells.Width(line.ToString()) + 1 + Cells.Width(word) > width)
            {
                yield return Cells.Fit(line.ToString(), width).TrimEnd();
                line.Clear();
            }
            if (line.Length > 0)
            {
                line.Append(' ');
            }
            line.Append(word);
        }
        if (line.Length > 0)
        {
            yield return Cells.Fit(line.ToString(), width).TrimEnd();
        }
    }
}
