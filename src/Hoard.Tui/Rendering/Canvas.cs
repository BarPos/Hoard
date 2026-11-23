using System.Text;

namespace Hoard.Tui.Rendering;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>The xterm 256-colour palette, so themes can reuse the indices from examples/lib.sh.</summary>
    public static Rgb Xterm(int i)
    {
        if (i < 16)
        {
            uint[] basic = [0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xc0c0c0,
                            0x808080, 0xff0000, 0x00ff00, 0xffff00, 0x0000ff, 0xff00ff, 0x00ffff, 0xffffff];
            return Hex(basic[i]);
        }
        if (i < 232)
        {
            int c = i - 16;
            static byte L(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return new Rgb(L(c / 36), L(c / 6 % 6), L(c % 6));
        }
        byte g = (byte)(8 + (i - 232) * 10);
        return new Rgb(g, g, g);
    }

    public Rgb Mix(Rgb other, double t) => new(
        (byte)(R + (other.R - R) * t), (byte)(G + (other.G - G) * t), (byte)(B + (other.B - B) * t));
}

[Flags]
public enum TextAttr : byte { None = 0, Bold = 1, Underline = 2, Italic = 4, Faint = 8 }

public readonly record struct Style(Rgb Fg, Rgb Bg, TextAttr Attr = TextAttr.None)
{
    public Style With(Rgb? fg = null, Rgb? bg = null, TextAttr? attr = null) =>
        new(fg ?? Fg, bg ?? Bg, attr ?? Attr);
    public Style Bold => this with { Attr = Attr | TextAttr.Bold };
}

/// <summary>Something the screen can be drawn on: the real terminal or an in-memory grid for tests.</summary>
public interface ICanvas
{
    int Width { get; }
    int Height { get; }
    /// <summary>Writes text at (x, y). Callers fit text to width first; the canvas clips at its edges.</summary>
    void Text(int x, int y, string text, Style style);
    void Fill(int x, int y, int width, int height, Style style, char ch = ' ');
}

/// <summary>Display-width helpers (wide CJK and emoji take two cells).</summary>
public static class Cells
{
    public static int Width(Rune r)
    {
        int w = Wcwidth.UnicodeCalculator.GetWidth(r.Value);
        return w < 0 ? 0 : w;
    }

    public static int Width(string s)
    {
        int w = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            w += Width(r);
        }
        return w;
    }

    /// <summary>Truncates to <paramref name="width"/> cells (with an ellipsis) and pads with spaces.</summary>
    public static string Fit(string s, int width, bool padLeft = false)
    {
        if (width <= 0)
        {
            return "";
        }
        int w = Width(s);
        if (w <= width)
        {
            return padLeft ? new string(' ', width - w) + s : s + new string(' ', width - w);
        }
        var sb = new StringBuilder();
        int used = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            int rw = Width(r);
            if (used + rw > width - 1)
            {
                break;
            }
            sb.Append(r.ToString());
            used += rw;
        }
        sb.Append('…');
        used++;
        return sb.Append(' ', Math.Max(0, width - used)).ToString();
    }
}

/// <summary>An in-memory canvas for snapshot tests and headless rendering.</summary>
public sealed class MemoryCanvas(int width, int height) : ICanvas
{
    private readonly string[,] _glyph = Init(width, height);
    private readonly Style[,] _style = new Style[width, height];

    private static string[,] Init(int w, int h)
    {
        var g = new string[w, h];
        for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) g[x, y] = " ";
        return g;
    }

    public int Width { get; } = width;
    public int Height { get; } = height;

    public void Text(int x, int y, string text, Style style)
    {
        if (y < 0 || y >= Height)
        {
            return;
        }
        foreach (Rune r in text.EnumerateRunes())
        {
            int w = Cells.Width(r);
            if (x >= Width)
            {
                break;
            }
            if (x >= 0)
            {
                _glyph[x, y] = r.ToString();
                _style[x, y] = style;
                if (w == 2 && x + 1 < Width)
                {
                    _glyph[x + 1, y] = "";
                    _style[x + 1, y] = style;
                }
            }
            x += Math.Max(1, w);
        }
    }

    public void Fill(int x, int y, int width, int height, Style style, char ch = ' ')
    {
        for (int yy = Math.Max(0, y); yy < Math.Min(Height, y + height); yy++)
        {
            for (int xx = Math.Max(0, x); xx < Math.Min(Width, x + width); xx++)
            {
                _glyph[xx, yy] = ch.ToString();
                _style[xx, yy] = style;
            }
        }
    }

    public Style StyleAt(int x, int y) => _style[x, y];

    public string Row(int y)
    {
        var sb = new StringBuilder();
        for (int x = 0; x < Width; x++) sb.Append(_glyph[x, y]);
        return sb.ToString();
    }

    public override string ToString() =>
        string.Join('\n', Enumerable.Range(0, Height).Select(y => Row(y).TrimEnd()));
}
