using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    public static string TypeSymbol(TypeCategory c) => c switch
    {
        TypeCategory.Video => "▷",
        TypeCategory.Images => "▣",
        TypeCategory.Audio => "♪",
        TypeCategory.Archives => "▦",
        TypeCategory.DiskImages => "◎",
        TypeCategory.Documents => "≡",
        TypeCategory.Fonts => "ƒ",
        TypeCategory.Code => "»",
        TypeCategory.Apps => "◆",
        TypeCategory.Databases => "◫",
        _ => "·",
    };

    /// <summary>What the files of a category are called in a sentence ("list 12 fonts").</summary>
    private static string FilesNoun(TypeCategory c) => c switch
    {
        TypeCategory.Video => "video files",
        TypeCategory.Images => "images",
        TypeCategory.Audio => "audio files",
        TypeCategory.Archives => "archives",
        TypeCategory.DiskImages => "disk images",
        TypeCategory.Documents => "documents",
        TypeCategory.Fonts => "fonts",
        TypeCategory.Code => "code & text files",
        TypeCategory.Apps => "apps & binaries",
        TypeCategory.Databases => "databases",
        _ => "other files",
    };

    private static string ListHint(long count, string noun) =>
        count == 1 ? "show this file" : $"list the {Units.Count(count)} {noun}, biggest first";

    /// <summary>Each category keeps one colour on the screen: by its rank, biggest = start of the gradient.</summary>
    private static Rgb TypeColour(Theme t, TypeReport r, TypeCategory c)
    {
        int rank = r.Categories.FindIndex(x => x.Category == c);
        return t.GradientAt(rank < 0 ? 0.99 : (double)rank / Math.Max(1, r.Categories.Count));
    }

    /// <summary>Keeps the selection inside the visible window; returns the new scroll offset.</summary>
    private static int ScrollFor(int selected, int scroll, int count, int visible)
    {
        if (selected < scroll) scroll = selected;
        if (selected >= scroll + visible) scroll = selected - visible + 1;
        return Math.Clamp(scroll, 0, Math.Max(0, count - visible));
    }

    /// <summary>What the disk is made of (plan.md §6.5; mock: examples/06-filetypes.sh).</summary>
    public static HitMap TypesView(ICanvas c, BrowserModel m)
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
        bool compact = h < 24;
        int barY = compact ? 3 : 4, legendY = barY + 1, divY = compact ? legendY + 1 : legendY + 2;
        TypeReport? report = m.Scanning ? null : m.Types;
        FileList? list = m.TypeFiles;
        bool split = w >= 100 && list is null && !m.TypesByExtension && report is not null;
        int lw = split ? w - 37 : w - 1;

        p.HLine(0, 2, w, '╭', '╮', "What is your disk made of?", m.TypesByExtension ? "by extension · e by category" : "by category · e by extension");
        p.VLine(0, 3, h - 5);
        p.VLine(w - 1, 3, h - 5);
        string section = list is not null ? list.Title + " · biggest first" : m.TypesByExtension ? "Extensions" : "Categories";
        string? sectionNote = list is not null && list.Count > FileList.Limit ? $"the {Units.Count(FileList.Limit)} biggest of {Units.Count(list.Count)}" : null;
        if (split)
        {
            p.HLine(0, divY, lw + 1, '├', '┬', section);
            p.HLine(lw, divY, w - lw, '┬', '┤', "Top extensions");
            p.HLine(0, h - 2, lw + 1, '╰', '┴', null, m.Toast, t.Info);
            p.HLine(lw, h - 2, w - lw, '┴', '╯');
            p.VLine(lw, divY + 1, h - 3 - divY);
        }
        else
        {
            p.HLine(0, divY, w, '├', '┤', section, sectionNote);
            p.HLine(0, h - 2, w, '╰', '╯', null, m.Toast, t.Info);
        }

        int x = 2, inner = w - 4;
        string spin = Spin[m.Tick % Spin.Length];
        if (report is null)
        {
            string msg = m.Scanning ? $"{spin} The breakdown is ready when the scan finishes. Meanwhile: browse (2) or the map (3)."
                : m.TypesError is { } e ? "Couldn't add up file types: " + e
                : $"{spin} Adding up file types…";
            p.Text(x, barY, Cells.Fit(msg, inner).TrimEnd(), m.TypesError is null ? t.Muted : t.Bad);
        }
        else
        {
            DrawTypeBar(p, report, x, barY, legendY, inner);
            int top = divY + 1, bottom = h - 3; // rows top..bottom-1 for the table, bottom for the hint
            if (list is not null)
            {
                DrawFileList(p, m, list, hits, x, top, bottom, inner);
            }
            else if (m.TypesByExtension)
            {
                DrawExtensionTable(p, m, report, hits, x, top, bottom, inner);
            }
            else
            {
                DrawCategoryTable(p, m, report, hits, x, top, bottom, lw - 3);
                if (split)
                {
                    DrawTopExtensions(p, report, lw + 2, top, bottom, w - lw - 4);
                }
            }
        }

        (string, string)[] keys = list is not null
            ? [("↑↓", "move"), ("↵", "show in list"), ("␣", "mark"), ("d", "delete"), ("o", "reveal"), ("←", "back"), ("?", "help"), ("q", "quit")]
            : [("↑↓", "move"), ("↵", "list files"), ("e", m.TypesByExtension ? "by category" : "by extension"), ("tab", "next view"), ("?", "help"), ("q", "quit")];
        Footer(p, keys, FooterStatus(m));
        Overlays(p, m);
        return hits;
    }

    /// <summary>A 100% stacked bar of the categories, with a legend under it.</summary>
    private static void DrawTypeBar(Painter p, TypeReport r, int x, int y, int legendY, int width)
    {
        Theme t = p.Theme;
        double total = Math.Max(1, r.TotalBytes);
        double cum = 0;
        int cell = 0;
        foreach (TypeTotal cat in r.Categories)
        {
            cum += cat.Bytes / total;
            int end = (int)Math.Round(cum * width);
            for (; cell < end && cell < width; cell++)
            {
                p.Text(x + cell, y, "█", TypeColour(t, r, cat.Category));
            }
        }
        for (; cell < width; cell++)
        {
            p.Text(x + cell, y, " ", t.Text, t.Track);
        }
        int lx = x;
        foreach (TypeTotal cat in r.Categories)
        {
            string label = FileTypes.Label(cat.Category);
            int need = 2 + Cells.Width(label) + 2;
            if (lx + need > x + width - 2)
            {
                p.Text(lx, legendY, "…", t.Faint);
                break;
            }
            lx = p.Text(lx, legendY, TypeSymbol(cat.Category), TypeColour(t, r, cat.Category), attr: TextAttr.Bold);
            lx = p.Text(lx + 1, legendY, label, t.Muted) + 2;
        }
    }

    private static void DrawCategoryTable(Painter p, BrowserModel m, TypeReport r, HitMap hits, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        const int LabelW = 16;
        int fixedW = 3 + LabelW + 10 + 2 + 6 + 9;
        int barW = Math.Clamp(width - fixedW - 1, 0, 30);
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        string head = "   " + Cells.Fit("TYPE", LabelW) + Cells.Fit("SIZE", 10, padLeft: true) + "  "
                      + (barW > 0 ? Cells.Fit("SHARE", barW) : "") + Cells.Fit("%", 6, padLeft: true) + Cells.Fit("FILES", 9, padLeft: true);
        c.Text(x, top, Cells.Fit(head, width), heading);

        int visible = Math.Max(1, bottom - top - 1);
        int count = r.Categories.Count;
        m.TypesSelected = Math.Clamp(m.TypesSelected, 0, Math.Max(0, count - 1));
        m.TypesScroll = ScrollFor(m.TypesSelected, m.TypesScroll, count, visible);
        long biggest = Math.Max(1, r.Categories.Count > 0 ? r.Categories[0].Bytes : 1);
        double total = Math.Max(1, r.TotalBytes);
        for (int row = 0; row < visible && m.TypesScroll + row < count; row++)
        {
            int index = m.TypesScroll + row;
            TypeTotal cat = r.Categories[index];
            int y = top + 1 + row;
            bool sel = index == m.TypesSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx, y, TypeSymbol(cat.Category), TypeColour(t, r, cat.Category), bg, TextAttr.Bold);
            cx = p.Text(cx + 1, y, Cells.Fit(FileTypes.Label(cat.Category), LabelW), t.Text, bg, TextAttr.Bold);
            cx = p.Text(cx, y, Cells.Fit(Units.Size(cat.Bytes), 10, padLeft: true), t.Text, bg) + 2;
            if (barW > 0)
            {
                p.Bar(cx, y, barW, (double)cat.Bytes / biggest, TypeColour(t, r, cat.Category));
                cx += barW;
            }
            cx = p.Text(cx, y, Cells.Fit(Pct(cat.Bytes / total), 6, padLeft: true), t.Muted, bg);
            p.Text(cx, y, Cells.Fit(Units.Count(cat.Files), 9, padLeft: true), t.Faint, bg);
            hits.Items.Add((y, x - 1, x + width + 1, index));
        }
        if (count > 0)
        {
            TypeTotal cur = r.Categories[m.TypesSelected];
            int hx = p.Text(x, bottom, "↵", t.Accent, attr: TextAttr.Bold);
            p.Text(hx + 1, bottom, Cells.Fit(ListHint(cur.Files, FilesNoun(cur.Category)), width - 2).TrimEnd(), t.Muted);
        }
    }

    private static void DrawTopExtensions(Painter p, TypeReport r, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        p.Canvas.Text(x, top, Cells.Fit("  " + Cells.Fit("EXT", 11) + Cells.Fit("SIZE", 10, padLeft: true) + Cells.Fit("FILES", 10, padLeft: true), width), heading);
        int rows = bottom - top - 1;
        for (int i = 0; i < rows && i < r.Extensions.Count; i++)
        {
            ExtensionTotal e = r.Extensions[i];
            int y = top + 1 + i;
            int cx = p.Text(x, y, "■", TypeColour(t, r, e.Category));
            cx = p.Text(cx + 1, y, Cells.Fit(ExtLabel(e.Extension), 11), t.Text);
            cx = p.Text(cx, y, Cells.Fit(Units.Size(e.Bytes), 10, padLeft: true), t.Text, attr: TextAttr.Bold);
            p.Text(cx, y, Cells.Fit(Units.Count(e.Files), 10, padLeft: true), t.Faint);
        }
        int kx = p.Text(x, bottom, "e", t.Accent, attr: TextAttr.Bold);
        p.Text(kx + 1, bottom, Cells.Fit("list every extension", width - 2).TrimEnd(), t.Muted);
    }

    private static string ExtLabel(string ext) => ext == FileTypes.NoExtension ? ext : "." + ext;

    private static void DrawExtensionTable(Painter p, BrowserModel m, TypeReport r, HitMap hits, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        const int ExtW = 12, CatW = 16;
        bool showCat = width >= 70;
        int fixedW = 3 + ExtW + (showCat ? CatW : 0) + 10 + 2 + 6 + 9;
        int barW = Math.Clamp(width - fixedW - 1, 0, 48);
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        string head = "   " + Cells.Fit("EXT", ExtW) + (showCat ? Cells.Fit("TYPE", CatW) : "") + Cells.Fit("SIZE", 10, padLeft: true) + "  "
                      + (barW > 0 ? Cells.Fit("SHARE", barW) : "") + Cells.Fit("%", 6, padLeft: true) + Cells.Fit("FILES", 9, padLeft: true);
        c.Text(x, top, Cells.Fit(head, width), heading);

        int visible = Math.Max(1, bottom - top - 1);
        int count = r.Extensions.Count;
        m.TypesSelected = Math.Clamp(m.TypesSelected, 0, Math.Max(0, count - 1));
        m.TypesScroll = ScrollFor(m.TypesSelected, m.TypesScroll, count, visible);
        long biggest = Math.Max(1, count > 0 ? r.Extensions[0].Bytes : 1);
        double total = Math.Max(1, r.TotalBytes);
        for (int row = 0; row < visible && m.TypesScroll + row < count; row++)
        {
            int index = m.TypesScroll + row;
            ExtensionTotal e = r.Extensions[index];
            int y = top + 1 + row;
            bool sel = index == m.TypesSelected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            Rgb colour = TypeColour(t, r, e.Category);
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx, y, "■", colour, bg);
            cx = p.Text(cx + 1, y, Cells.Fit(ExtLabel(e.Extension), ExtW), t.Text, bg, TextAttr.Bold);
            if (showCat)
            {
                cx = p.Text(cx, y, Cells.Fit(FileTypes.Label(e.Category), CatW), t.Muted, bg);
            }
            cx = p.Text(cx, y, Cells.Fit(Units.Size(e.Bytes), 10, padLeft: true), t.Text, bg) + 2;
            if (barW > 0)
            {
                p.Bar(cx, y, barW, (double)e.Bytes / biggest, colour);
                cx += barW;
            }
            cx = p.Text(cx, y, Cells.Fit(Pct(e.Bytes / total), 6, padLeft: true), t.Muted, bg);
            p.Text(cx, y, Cells.Fit(Units.Count(e.Files), 9, padLeft: true), t.Faint, bg);
            hits.Items.Add((y, x - 1, x + width + 1, index));
        }
        if (count > 0)
        {
            ExtensionTotal cur = r.Extensions[m.TypesSelected];
            int hx = p.Text(x, bottom, "↵", t.Accent, attr: TextAttr.Bold);
            string noun = cur.Extension == FileTypes.NoExtension ? "files without an extension" : ExtLabel(cur.Extension) + " files";
            p.Text(hx + 1, bottom, Cells.Fit(ListHint(cur.Files, noun), width - 2).TrimEnd(), t.Muted);
        }
    }

    private static void DrawFileList(Painter p, BrowserModel m, FileList list, HitMap hits, int x, int top, int bottom, int width)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        NodeStore s = m.Store;
        List<int>? items = list.Items(s);
        if (items is null)
        {
            string msg = list.Job.Error is { } e ? "Couldn't list the files: " + e : $"{Spin[m.Tick % Spin.Length]} Finding the biggest files…";
            p.Text(x, top + 1, Cells.Fit(msg, width).TrimEnd(), list.Job.Error is null ? t.Muted : t.Bad);
            return;
        }
        bool showAge = width >= 70;
        int pathW = width - 3 - 10 - (showAge ? 18 : 0);
        var heading = new Style(t.Muted, t.Bg, TextAttr.Underline);
        c.Text(x, top, Cells.Fit("   " + Cells.Fit("PATH", pathW) + Cells.Fit("SIZE ▼", 10, padLeft: true) + (showAge ? "  MODIFIED" : ""), width), heading);
        if (items.Count == 0)
        {
            p.Text(x + 2, top + 1, Cells.Fit("None left: they were all deleted.", width - 2).TrimEnd(), t.Muted);
            return;
        }
        int visible = Math.Max(1, bottom - top - 1);
        list.Selected = Math.Clamp(list.Selected, 0, items.Count - 1);
        list.Scroll = ScrollFor(list.Selected, list.Scroll, items.Count, visible);
        long now = m.Now;
        for (int row = 0; row < visible && list.Scroll + row < items.Count; row++)
        {
            int index = list.Scroll + row;
            int id = items[index];
            int y = top + 1 + row;
            bool sel = index == list.Selected;
            Rgb bg = sel ? t.Selection : t.Bg;
            if (sel)
            {
                c.Fill(x - 1, y, width + 2, 1, new Style(t.Text, bg));
            }
            bool marked = m.IsMarked(id);
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx, y, marked ? "●" : " ", t.Warn, bg) + 1;
            // The end of the path (the file name) matters most, so long paths lose their middle.
            cx = p.Text(cx, y, Cells.Fit(MiddleOut(s.RelativePath(id), pathW), pathW), marked ? t.Warn : t.Text, bg);
            cx = p.Text(cx, y, Cells.Fit(Units.Size(s.Size(id)), 10, padLeft: true), t.Text, bg, TextAttr.Bold) + 2;
            if (showAge)
            {
                long age = now - s.MTime(id);
                p.Text(cx, y, Cells.Fit(Ago(age), 16).TrimEnd(), AgeColour(t, age), bg);
            }
            hits.Items.Add((y, x - 1, x + width + 1, index));
        }
        int hx = p.Text(x, bottom, "↵", t.Accent, attr: TextAttr.Bold);
        p.Text(hx + 1, bottom, Cells.Fit("shows it in the list · ␣ marks · d deletes the marked (or this one) · ← back", width - 2).TrimEnd(), t.Muted);
    }

    /// <summary>Shortens a path to fit by cutting from the middle: "a/b/…/x/file.mov".</summary>
    internal static string MiddleOut(string path, int width)
    {
        if (Cells.Width(path) <= width || width < 8)
        {
            return path;
        }
        string name = Path.GetFileName(path);
        if (Cells.Width(name) + 2 >= width)
        {
            return "…" + name[^Math.Min(name.Length, width - 1)..];
        }
        // Fit ends the folder part with "…"; then the file name follows after a slash.
        int room = width - Cells.Width(name) - 1;
        return Cells.Fit(path[..^name.Length], room).TrimEnd() + "/" + name;
    }
}
