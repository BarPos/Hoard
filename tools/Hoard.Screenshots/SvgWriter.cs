using System.Globalization;
using System.Text;
using Hoard.Tui.Rendering;

namespace Hoard.Screenshots;

/// <summary>
/// Turns a rendered screen into an SVG of a macOS terminal window: title bar with the three
/// buttons, the cells in their exact colours, and box lines and bars drawn as shapes so they
/// join up whatever font the viewer has.
/// </summary>
internal static class SvgWriter
{
    private const double CellW = 8.4, CellH = 18, Pad = 12, TitleH = 30, Radius = 10, Margin = 22;

    private static string Hex(Rgb c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // Partial blocks: fraction of the cell width (left-aligned) or height.
    private static readonly Dictionary<string, double> LeftBlocks = new()
    {
        ["█"] = 1, ["▉"] = 7 / 8.0, ["▊"] = 6 / 8.0, ["▋"] = 5 / 8.0, ["▌"] = 4 / 8.0, ["▍"] = 3 / 8.0, ["▎"] = 2 / 8.0, ["▏"] = 1 / 8.0,
    };
    private static readonly Dictionary<string, double> BottomBlocks = new()
    {
        ["▁"] = 1 / 8.0, ["▂"] = 2 / 8.0, ["▃"] = 3 / 8.0, ["▄"] = 4 / 8.0, ["▅"] = 5 / 8.0, ["▆"] = 6 / 8.0, ["▇"] = 7 / 8.0,
    };

    /// <summary>Which sides of the cell a box-drawing character connects: (up, down, left, right).</summary>
    private static readonly Dictionary<string, (bool U, bool D, bool L, bool R)> Box = new()
    {
        ["─"] = (false, false, true, true), ["│"] = (true, true, false, false),
        ["├"] = (true, true, false, true), ["┤"] = (true, true, true, false),
        ["┬"] = (false, true, true, true), ["┴"] = (true, false, true, true), ["┼"] = (true, true, true, true),
        ["┌"] = (false, true, false, true), ["┐"] = (false, true, true, false), ["└"] = (true, false, false, true), ["┘"] = (true, false, true, false),
    };

    public static string Render(MemoryCanvas c, Theme theme, string title)
    {
        double bodyW = c.Width * CellW + Pad * 2, bodyH = c.Height * CellH + Pad * 2;
        double winW = bodyW, winH = bodyH + TitleH;
        double svgW = winW + Margin * 2, svgH = winH + Margin * 2;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(svgW)}\" height=\"{N(svgH)}\" viewBox=\"0 0 {N(svgW)} {N(svgH)}\" role=\"img\" aria-label=\"{Esc(title)}\">\n");
        sb.Append("<defs><filter id=\"shadow\" x=\"-10%\" y=\"-10%\" width=\"120%\" height=\"130%\"><feDropShadow dx=\"0\" dy=\"8\" stdDeviation=\"9\" flood-color=\"#000\" flood-opacity=\"0.45\"/></filter>");
        sb.Append(CultureInfo.InvariantCulture, $"<clipPath id=\"win\"><rect x=\"{N(Margin)}\" y=\"{N(Margin)}\" width=\"{N(winW)}\" height=\"{N(winH)}\" rx=\"{N(Radius)}\"/></clipPath></defs>\n");
        sb.Append("<style>rect.b{shape-rendering:crispEdges}text{font-family:ui-monospace,'SF Mono',SFMono-Regular,Menlo,Monaco,Consolas,'DejaVu Sans Mono',monospace;font-size:14px;white-space:pre}.t{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;font-size:13px}</style>\n");

        // Window: shadow, body, title bar, buttons, title.
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(Margin)}\" y=\"{N(Margin)}\" width=\"{N(winW)}\" height=\"{N(winH)}\" rx=\"{N(Radius)}\" fill=\"{Hex(theme.Bg)}\" filter=\"url(#shadow)\"/>\n");
        sb.Append("<g clip-path=\"url(#win)\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(Margin)}\" y=\"{N(Margin)}\" width=\"{N(winW)}\" height=\"{N(TitleH)}\" fill=\"#2b2b2d\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(Margin)}\" y=\"{N(Margin + TitleH - 1)}\" width=\"{N(winW)}\" height=\"1\" fill=\"#000\" opacity=\"0.6\"/>\n");
        string[] buttons = ["#ff5f57", "#febc2e", "#28c840"];
        for (int i = 0; i < 3; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{N(Margin + 20 + i * 20)}\" cy=\"{N(Margin + TitleH / 2)}\" r=\"6\" fill=\"{buttons[i]}\"/>\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"<text class=\"t\" x=\"{N(Margin + winW / 2)}\" y=\"{N(Margin + TitleH / 2 + 4.5)}\" text-anchor=\"middle\" fill=\"#a8a8aa\">{Esc(title)}</text>\n");

        double ox = Margin + Pad, oy = Margin + TitleH + Pad;
        // Backgrounds: one rect per run of equal colour in a row.
        for (int y = 0; y < c.Height; y++)
        {
            int x = 0;
            while (x < c.Width)
            {
                Rgb bg = c.StyleAt(x, y).Bg;
                int start = x;
                while (x < c.Width && c.StyleAt(x, y).Bg == bg)
                {
                    x++;
                }
                if (bg != theme.Bg)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(ox + start * CellW)}\" y=\"{N(oy + y * CellH)}\" width=\"{N((x - start) * CellW + 0.4)}\" height=\"{N(CellH + 0.4)}\" fill=\"{Hex(bg)}\" class=\"b\"/>\n");
                }
            }
        }

        // Foreground: shapes for blocks and box lines, text runs for the rest.
        for (int y = 0; y < c.Height; y++)
        {
            double top = oy + y * CellH;
            int x = 0;
            while (x < c.Width)
            {
                string g = c.Glyph(x, y);
                Style st = c.StyleAt(x, y);
                double left = ox + x * CellW;
                string fg = Hex(st.Fg);
                if (LeftBlocks.TryGetValue(g, out double fw))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(left)}\" y=\"{N(top)}\" width=\"{N(CellW * fw + (fw == 1 ? 0.4 : 0))}\" height=\"{N(CellH + 0.4)}\" fill=\"{fg}\" class=\"b\"/>\n");
                    x++;
                    continue;
                }
                if (BottomBlocks.TryGetValue(g, out double fh))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(left)}\" y=\"{N(top + CellH * (1 - fh))}\" width=\"{N(CellW + 0.4)}\" height=\"{N(CellH * fh)}\" fill=\"{fg}\" class=\"b\"/>\n");
                    x++;
                    continue;
                }
                if (g is "▀" or "■")
                {
                    bool square = g == "■";
                    double h = square ? CellW * 0.9 : CellH / 2, w = square ? CellW * 0.9 : CellW + 0.4;
                    double yy = square ? top + (CellH - h) / 2 : top;
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(left + (square ? CellW * 0.05 : 0))}\" y=\"{N(yy)}\" width=\"{N(w)}\" height=\"{N(h)}\" fill=\"{fg}\"/>\n");
                    x++;
                    continue;
                }
                if (Box.TryGetValue(g, out var sides) || g is "╭" or "╮" or "╰" or "╯")
                {
                    double cx = left + CellW / 2, cy = top + CellH / 2;
                    var path = new StringBuilder();
                    if (g is "╭" or "╮" or "╰" or "╯")
                    {
                        double r = Math.Min(CellW / 2, CellH / 2);
                        // A quarter circle from the line's start to its end.
                        path.Append(g switch
                        {
                            "╭" => $"M{N(cx)},{N(top + CellH)} V{N(cy + r)} A{N(r)},{N(r)} 0 0 1 {N(cx + r)},{N(cy)} H{N(left + CellW + 0.2)}",
                            "╮" => $"M{N(left - 0.2)},{N(cy)} H{N(cx - r)} A{N(r)},{N(r)} 0 0 1 {N(cx)},{N(cy + r)} V{N(top + CellH)}",
                            "╰" => $"M{N(cx)},{N(top)} V{N(cy - r)} A{N(r)},{N(r)} 0 0 0 {N(cx + r)},{N(cy)} H{N(left + CellW + 0.2)}",
                            _ => $"M{N(left - 0.2)},{N(cy)} H{N(cx - r)} A{N(r)},{N(r)} 0 0 0 {N(cx)},{N(cy - r)} V{N(top)}",
                        });
                    }
                    else
                    {
                        if (sides.U || sides.D)
                        {
                            path.Append(CultureInfo.InvariantCulture, $"M{N(cx)},{N(sides.U ? top - 0.2 : cy)} V{N(sides.D ? top + CellH + 0.2 : cy)} ");
                        }
                        if (sides.L || sides.R)
                        {
                            path.Append(CultureInfo.InvariantCulture, $"M{N(sides.L ? left - 0.2 : cx)},{N(cy)} H{N(sides.R ? left + CellW + 0.2 : cx)}");
                        }
                    }
                    sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{path}\" stroke=\"{fg}\" stroke-width=\"1.3\" fill=\"none\"/>\n");
                    x++;
                    continue;
                }
                if (g is " " or "")
                {
                    x++;
                    continue;
                }
                // A run of ordinary characters in one style.
                int start = x;
                var run = new StringBuilder();
                int cells = 0;
                while (x < c.Width)
                {
                    string gg = c.Glyph(x, y);
                    if (c.StyleAt(x, y) != st || LeftBlocks.ContainsKey(gg) || BottomBlocks.ContainsKey(gg) || Box.ContainsKey(gg) || gg is "▀" or "■" or "╭" or "╮" or "╰" or "╯")
                    {
                        break;
                    }
                    if (gg.Length > 0)
                    {
                        run.Append(gg);
                    }
                    cells++;
                    x++;
                }
                string text = run.ToString().TrimEnd();
                if (text.Length == 0)
                {
                    continue;
                }
                int used = cells - (run.Length - text.Length);
                string weight = (st.Attr & TextAttr.Bold) != 0 ? " font-weight=\"700\"" : "";
                string deco = (st.Attr & TextAttr.Underline) != 0 ? " text-decoration=\"underline\"" : "";
                string fade = (st.Attr & TextAttr.Faint) != 0 ? " opacity=\"0.6\"" : "";
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{N(left)}\" y=\"{N(top + CellH - 5)}\" fill=\"{fg}\" textLength=\"{N(used * CellW)}\" lengthAdjust=\"spacingAndGlyphs\"{weight}{deco}{fade}>{Esc(text)}</text>\n");
            }
        }
        sb.Append("</g>\n</svg>\n");
        return sb.ToString();
    }
}
