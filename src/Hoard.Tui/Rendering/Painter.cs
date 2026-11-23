namespace Hoard.Tui.Rendering;

/// <summary>Drawing primitives in the style of the mock screens: rounded boxes, gradient bars.</summary>
internal sealed class Painter(ICanvas c, Theme t)
{
    public ICanvas Canvas => c;
    public Theme Theme => t;

    public void Clear() => c.Fill(0, 0, c.Width, c.Height, new Style(t.Text, t.Bg));

    public int Text(int x, int y, string s, Rgb fg, Rgb? bg = null, TextAttr attr = TextAttr.None)
    {
        c.Text(x, y, s, new Style(fg, bg ?? t.Bg, attr));
        return x + Cells.Width(s);
    }

    /// <summary>A horizontal border segment with an optional title on the left and note on the right.</summary>
    public void HLine(int x, int y, int width, char left, char right, string? title = null, string? note = null, Rgb? noteColor = null)
    {
        if (width < 2)
        {
            return;
        }
        var border = new Style(t.Border, t.Bg);
        c.Fill(x, y, width, 1, border, '─');
        c.Text(x, y, left.ToString(), border);
        c.Text(x + width - 1, y, right.ToString(), border);
        int room = width - 4;
        if (!string.IsNullOrEmpty(title) && room > 4)
        {
            string tt = " " + Cells.Fit(title, Math.Min(room - 2, Cells.Width(title))).TrimEnd() + " ";
            c.Text(x + 2, y, tt, new Style(t.Accent, t.Bg, TextAttr.Bold));
            room -= Cells.Width(tt) + 1;
        }
        if (!string.IsNullOrEmpty(note) && room > 6)
        {
            string nn = " " + Cells.Fit(note, Math.Min(room - 2, Cells.Width(note))).TrimEnd() + " ";
            c.Text(x + width - 2 - Cells.Width(nn), y, nn, new Style(noteColor ?? t.Muted, t.Bg));
        }
    }

    public void VLine(int x, int y, int height)
    {
        var border = new Style(t.Border, t.Bg);
        for (int i = 0; i < height; i++)
        {
            c.Text(x, y + i, "│", border);
        }
    }

    private static readonly string[] Eighths = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];

    /// <summary>A bar with eighth-block precision; gradient by position unless a colour is given.</summary>
    public void Bar(int x, int y, int width, double fraction, Rgb? solid = null)
    {
        if (width <= 0)
        {
            return;
        }
        fraction = Math.Clamp(fraction, 0, 1);
        int eighths = (int)Math.Round(fraction * width * 8);
        int full = eighths / 8, rem = eighths % 8;
        for (int i = 0; i < width; i++)
        {
            Rgb fg = solid ?? t.GradientAt((double)i / width);
            string glyph = i < full ? "█" : i == full && rem > 0 ? Eighths[rem] : " ";
            c.Text(x + i, y, glyph, new Style(fg, t.Track));
        }
    }
}
