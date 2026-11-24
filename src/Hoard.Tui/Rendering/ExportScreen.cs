using Hoard.Core.Export;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    /// <summary>The export dialog over any view (plan.md §9.2; mock: examples/15-export.sh).</summary>
    private static void ExportBox(Painter p, BrowserModel m, ExportDialog e)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        int w = Math.Min(c.Width - 2, 110), h = Math.Min(c.Height - 2, 24);
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, (c.Height - h) / 2);
        bool preview = w >= 90;
        int lw = preview ? 50 : w - 1;
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        Rgb Bg = t.Panel;
        void Frame(int x, int y, int width, char l, char r, string? title = null, string? note = null) => p.HLine(x, y, width, l, r, title, note);
        if (preview)
        {
            Frame(x0, y0, lw + 1, '╭', '┬', "Export");
            Frame(x0 + lw, y0, w - lw, '┬', '╮', "Preview", e.Format is ExportFormat.PrettyHtml or ExportFormat.Ncdu ? null : "first lines, 1 level");
            Frame(x0, y0 + h - 1, lw + 1, '╰', '┴');
            Frame(x0 + lw, y0 + h - 1, w - lw, '┴', '╯');
        }
        else
        {
            Frame(x0, y0, w, '╭', '╮', "Export");
            Frame(x0, y0 + h - 1, w, '╰', '╯');
        }
        for (int i = 1; i < h - 1; i++)
        {
            p.Text(x0, y0 + i, "│", t.Border, Bg);
            p.Text(x0 + w - 1, y0 + i, "│", t.Border, Bg);
            if (preview)
            {
                p.Text(x0 + lw, y0 + i, "│", t.Border, Bg);
            }
        }
        // Panel-coloured copies of the box lines' background.
        int x = x0 + 2, y = y0 + 2, fw = lw - 4;
        string[] labels = ["Format", "Scope", "Include", "Depth", "Min size", "Save to"];
        for (int f = 0; f < 6; f++)
        {
            bool sel = e.Field == f;
            Rgb bg = sel ? t.Selection : Bg;
            c.Fill(x - 1, y, fw + 2, 1, new Style(t.Text, bg));
            int cx = p.Text(x, y, sel ? "►" : " ", t.Accent, bg);
            cx = p.Text(cx + 1, y, Cells.Fit(labels[f], 9), t.Muted, bg);
            if (f == 5)
            {
                string shown = e.EditingPath ? e.Path + "▏" : Shorten(e.Path);
                p.Text(cx, y, Cells.Fit(MiddleOut(shown, fw - 11), fw - 11).TrimEnd(), e.EditingPath ? t.Text : t.Accent2, e.EditingPath ? t.Bg : bg, TextAttr.Bold);
            }
            else
            {
                cx = p.Text(cx, y, "◂ ", sel ? t.Accent : t.Faint, bg);
                cx = p.Text(cx, y, e.Describe(f), t.Text, bg, TextAttr.Bold);
                p.Text(cx, y, " ▸", sel ? t.Accent : t.Faint, bg);
            }
            y++;
            if (f == 0)
            {
                p.Text(x + 12, y++, Cells.Fit(Exporter.Describe(e.Format), fw - 12).TrimEnd(), t.Faint, Bg);
            }
        }
        y++;
        bool button = e.Field == 6;
        string label = e.Writing is { IsCompleted: false } ? $" {Spin[m.Tick % Spin.Length]} writing… " : " ↵ Export ";
        p.Text(x, y, label, Rgb.Hex(0x000000), button ? t.Accent : t.Accent2, TextAttr.Bold);
        p.Text(x + Cells.Width(label) + 2, y, Cells.Fit("y copies the path", fw - Cells.Width(label) - 2).TrimEnd(), t.Faint, Bg);
        y += 2;
        if (e.Error is { } err)
        {
            foreach (string line in Wrap("Couldn't export: " + err, fw))
            {
                if (y < y0 + h - 2) p.Text(x, y++, line, t.Bad, Bg);
            }
        }
        string hint = e.EditingPath ? "type the path · ↵ done" : e.Field == 5 ? "↵ edits the path" : "↑↓ field · ←→ change · ↵ export · esc close";
        p.Text(x, y0 + h - 2, Cells.Fit(hint, fw).TrimEnd(), t.Muted, Bg);

        if (preview)
        {
            int px = x0 + lw + 2, pw = w - lw - 4;
            string text = PreviewText(m, e);
            int py = y0 + 1;
            foreach (string line in text.Split('\n').Take(h - 3))
            {
                p.Text(px, py++, Cells.Fit(line.TrimEnd('\r').Replace("\t", "  "), pw).TrimEnd(), t.Text, Bg);
            }
            string cli = $"hoard {m.RootPath} --export {e.Format.ToString().ToLowerInvariant()}" + (e.Depth > 0 ? $" --depth {e.Depth}" : "") + " -o …";
            p.Text(px, y0 + h - 2, Cells.Fit("CLI: " + cli, pw).TrimEnd(), t.Faint, Bg);
        }
    }

    /// <summary>The first lines of what would be written (depth 1, so it's quick); cached until the options change.</summary>
    private static string PreviewText(BrowserModel m, ExportDialog e)
    {
        if (e.Format == ExportFormat.PrettyHtml)
        {
            return "A single HTML file that looks like Hoard:\n\n  · Overview, Browse, Map, Types, Age, Junk,\n    Dupes and Big & old views\n  · themes, keys and mouse like here\n  · no internet needed, nothing to install\n\nOpen it in any browser. Read-only: it\ncan't delete anything.";
        }
        if (e.Format == ExportFormat.Ncdu)
        {
            return "ncdu's JSON format (version 1.2):\nthe whole tree, files included.\n\nOpen it with:\n  ncdu -f file.json\n\nDepth, minimum size and \"folders only\"\ndon't apply: ncdu needs everything.";
        }
        var key = (e.Format, e.Scope, e.IncludeFiles, e.MinSize, m.Apparent, m.Current, m.Marks.Count);
        if (!Equals(e.PreviewKey, key))
        {
            try
            {
                ExportData data = m.ExportData(e.Scope);
                string all = Exporter.WriteToString(data, e.Options(m.Apparent) with { Depth = 1 });
                e.Preview = string.Join('\n', all.Split('\n').Take(40));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                e.Preview = "(no preview: " + ex.Message + ")";
            }
            e.PreviewKey = key;
        }
        return e.Preview ?? "";
    }
}
