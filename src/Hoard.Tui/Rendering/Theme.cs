namespace Hoard.Tui.Rendering;

/// <summary>Semantic colour slots (plan.md §7.6). Values mirror examples/lib.sh.</summary>
public sealed record Theme(
    string Name,
    Rgb Bg, Rgb Panel, Rgb Accent, Rgb Accent2, Rgb Text, Rgb Muted, Rgb Faint, Rgb Border,
    Rgb Bar, Rgb Selection, Rgb Track, Rgb Ok, Rgb Warn, Rgb Bad, Rgb Info, Rgb[] Gradient)
{
    private static Rgb X(int i) => Rgb.Xterm(i);
    private static Rgb[] G(params int[] idx) => [.. idx.Select(X)];

    public static readonly Theme Hoard = new("hoard", Rgb.Hex(0x121212), Rgb.Hex(0x161412),
        X(214), X(208), X(252), X(244), X(239), X(136), X(52), X(94), X(236), X(114), X(221), X(203), X(81),
        G(226, 220, 214, 208, 202, 196, 160, 124));

    public static readonly Theme Neon = new("neon", Rgb.Hex(0x0d0b14), Rgb.Hex(0x120f1c),
        X(201), X(51), X(255), X(103), X(60), X(93), X(17), X(54), X(235), X(48), X(227), X(197), X(87),
        G(51, 45, 81, 117, 141, 177, 213, 201));

    public static readonly Theme Frost = new("frost", Rgb.Hex(0x0e1316), Rgb.Hex(0x11181c),
        X(117), X(159), X(254), X(109), X(66), X(67), X(23), X(24), X(236), X(115), X(229), X(210), X(153),
        G(195, 159, 123, 117, 81, 75, 33, 27));

    public static readonly Theme Mono = new("mono", Rgb.Hex(0x111111), Rgb.Hex(0x151515),
        X(255), X(250), X(252), X(244), X(240), X(242), X(236), X(239), X(235), X(255), X(250), X(255), X(250),
        G(255, 253, 251, 249, 247, 245, 243, 241));

    public static readonly Theme[] All = [Hoard, Neon, Frost, Mono];

    public static Theme Named(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Hoard;

    public Rgb GradientAt(double t) => Gradient[Math.Clamp((int)(t * Gradient.Length), 0, Gradient.Length - 1)];

    public Style S(Rgb fg) => new(fg, Bg);
}
