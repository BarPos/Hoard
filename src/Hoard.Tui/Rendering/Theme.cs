namespace Hoard.Tui.Rendering;

/// <summary>
/// Semantic colour slots. Every view draws with these, never with fixed colours, so a theme
/// changes everything at once. The gradient runs from small to big (bars, the logo, categories).
/// </summary>
public sealed record Theme(
    string Name,
    Rgb Bg, Rgb Panel, Rgb Accent, Rgb Accent2, Rgb Text, Rgb Muted, Rgb Faint, Rgb Border,
    Rgb Bar, Rgb Selection, Rgb Track, Rgb Ok, Rgb Warn, Rgb Bad, Rgb Info, Rgb[] Gradient)
{
    private static Rgb X(int i) => Rgb.Xterm(i);
    private static Rgb[] G(params int[] idx) => [.. idx.Select(X)];
    private static Rgb H(uint v) => Rgb.Hex(v);
    private static Rgb[] Hs(params uint[] v) => [.. v.Select(Rgb.Hex)];

    /// <summary>One line for the theme picker.</summary>
    public string Description { get; init; } = "";

    /// <summary>True for themes with a light background.</summary>
    public bool IsLight => Luminance(Bg) > 0.5;

    // ── the original four ───────────────────────────────────────────────────

    public static readonly Theme Hoard = new("hoard", Rgb.Hex(0x121212), Rgb.Hex(0x161412),
        // Selection: a deeper amber than xterm 94, so selected text stays readable (4.5:1).
        X(214), X(208), X(252), X(244), X(239), X(136), X(52), H(0x6b4800), X(236), X(114), X(221), X(203), X(81),
        G(226, 220, 214, 208, 202, 196, 160, 124)) { Description = "gold and fire on charcoal (the default)" };

    public static readonly Theme Neon = new("neon", Rgb.Hex(0x0d0b14), Rgb.Hex(0x120f1c),
        X(201), X(51), X(255), X(103), X(60), X(93), X(17), X(54), X(235), X(48), X(227), X(197), X(87),
        G(51, 45, 81, 117, 141, 177, 213, 201)) { Description = "electric cyan to magenta" };

    public static readonly Theme Frost = new("frost", Rgb.Hex(0x0e1316), Rgb.Hex(0x11181c),
        X(117), X(159), X(254), X(109), X(66), X(67), X(23), X(24), X(236), X(115), X(229), X(210), X(153),
        G(195, 159, 123, 117, 81, 75, 33, 27)) { Description = "cool ice blues" };

    public static readonly Theme Mono = new("mono", Rgb.Hex(0x111111), Rgb.Hex(0x151515),
        X(255), X(250), X(252), X(244), X(240), X(242), X(236), X(239), X(235), X(255), X(250), X(255), X(250),
        G(255, 253, 251, 249, 247, 245, 243, 241)) { Description = "greys only, for minimal or limited terminals" };

    // ── more ────────────────────────────────────────────────────────────────

    public static readonly Theme Ember = new("ember", H(0x141010), H(0x1c1514),
        H(0xff6b35), H(0xff9f1c), H(0xeadbd3), H(0xa89089), H(0x5e4c46), H(0x7a3a22),
        H(0x3a1410), H(0x4a2418), H(0x2a1e1b), H(0x9bd17a), H(0xffd166), H(0xff5a5a), H(0x7fc8f8),
        Hs(0xffe8a3, 0xffd166, 0xffb347, 0xff8c42, 0xff6b35, 0xf2542d, 0xd63c2a, 0xb02a24)) { Description = "glowing coals" };

    public static readonly Theme Forest = new("forest", H(0x0f1410), H(0x131a14),
        H(0x8fd16a), H(0xc3e88d), H(0xdae7d2), H(0x93a78e), H(0x4b5c48), H(0x3f6b3a),
        H(0x1d3320), H(0x29452c), H(0x1e2a1f), H(0x8fd16a), H(0xe6c86e), H(0xe8806b), H(0x7ec8c8),
        Hs(0xe6f5a8, 0xc3e88d, 0x9fd77a, 0x7cc46a, 0x5eab5e, 0x4a9658, 0x3b7e4e, 0x2e6644)) { Description = "moss, leaves and bark" };

    public static readonly Theme Ocean = new("ocean", H(0x0b1220), H(0x0f1828),
        H(0x4fc3f7), H(0x80deea), H(0xd8e5f1), H(0x8d9fb5), H(0x44546a), H(0x2a4a6e),
        H(0x102a48), H(0x1c3a5e), H(0x18243a), H(0x69db9e), H(0xffd479), H(0xff7a85), H(0x4fc3f7),
        Hs(0xb2f5ea, 0x81e6d9, 0x4fd1c5, 0x38b2ac, 0x3b8fd4, 0x3373c4, 0x2f5ea8, 0x2c4a8a)) { Description = "deep water, from shallows to depths" };

    public static readonly Theme Sunset = new("sunset", H(0x17111c), H(0x1d1524),
        H(0xff7eb6), H(0xffb86b), H(0xecdff2), H(0xab99b6), H(0x5a4a66), H(0x6b3a6e),
        H(0x3a1838), H(0x4a2450), H(0x261c2e), H(0x8be9a8), H(0xffd36e), H(0xff6684), H(0x8ab4ff),
        Hs(0xffe29a, 0xffc46b, 0xffa26b, 0xff7e8a, 0xf45d9c, 0xd453b4, 0xa74fc4, 0x7a4cc2)) { Description = "peach, pink and violet" };

    public static readonly Theme Lilac = new("lilac", H(0x18161f), H(0x1e1b27),
        H(0xc3a6ff), H(0xffb3d9), H(0xe9e4f5), H(0xa29abb), H(0x534c66), H(0x5c4f80),
        H(0x2b2440), H(0x3a3156), H(0x241f31), H(0xa6e3b8), H(0xffe0a3), H(0xff9eb0), H(0xa6d8ff),
        Hs(0xd8f3ff, 0xb8e0ff, 0xc3c6ff, 0xc3a6ff, 0xe0a6ff, 0xffa6e8, 0xff9ec4, 0xff8fa3)) { Description = "soft pastels on dusk" };

    public static readonly Theme Amber = new("amber", H(0x110d05), H(0x161106),
        H(0xffb000), H(0xffcc33), H(0xffd27a), H(0xc78f30), H(0x6b4c14), H(0x8a5c0a),
        H(0x2e1f04), H(0x4a3206), H(0x241a07), H(0xe6c35c), H(0xffb000), H(0xff6a00), H(0xffe0a3),
        Hs(0xfff0c2, 0xffe08a, 0xffcc4d, 0xffb000, 0xf0a000, 0xd98c00, 0xbf7600, 0xa36300)) { Description = "a vintage amber monitor" };

    public static readonly Theme Phosphor = new("phosphor", H(0x050f07), H(0x08150a),
        H(0x33ff66), H(0x99ff99), H(0xa3ffb5), H(0x56c46f), H(0x1f5a2c), H(0x1f7a36),
        H(0x0a2a12), H(0x12401e), H(0x0d1f12), H(0x66ff88), H(0xccff66), H(0xff6655), H(0x66ffcc),
        Hs(0xccffd6, 0x99ffad, 0x66ff88, 0x33ff66, 0x22e055, 0x1ac24a, 0x15a33e, 0x108533)) { Description = "green phosphor, like an old terminal" };

    public static readonly Theme Paper = new("paper", H(0xf7f4ee), H(0xefeae0),
        H(0xb5520a), H(0x9a6a00), H(0x2b2620), H(0x665d50), H(0xa89f90), H(0xc9b99a),
        H(0xe3d9c6), H(0xf1d7ae), H(0xe8e1d3), H(0x2f7d32), H(0x9a5c00), H(0xbb3326), H(0x1f6fa8),
        Hs(0xf2b632, 0xe99a2c, 0xdf7f29, 0xd16628, 0xbf4f29, 0xa53c2c, 0x88302d, 0x6b252b)) { Description = "light: ink on warm paper" };

    public static readonly Theme Daylight = new("daylight", H(0xf5f8fb), H(0xeaf0f6),
        H(0x1a6fc4), H(0x6a4fc4), H(0x1d2733), H(0x566474), H(0xa3b0bd), H(0xb7c7d8),
        H(0xdde6ef), H(0xcfe3f7), H(0xe3eaf1), H(0x1f8a4c), H(0x9a6400), H(0xc2304a), H(0x1a6fc4),
        Hs(0x9ad7f0, 0x6cc3ea, 0x45a9e0, 0x2f8fd4, 0x2a74c2, 0x3a5cb0, 0x4a479c, 0x563a86)) { Description = "light: crisp blue on white" };

    public static readonly Theme[] All = [Hoard, Neon, Frost, Mono, Ember, Forest, Ocean, Sunset, Lilac, Amber, Phosphor, Paper, Daylight];

    public static Theme Named(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Hoard;

    /// <summary>Every theme name, for help text: "hoard, neon, … or daylight".</summary>
    public static string NameList => string.Join(", ", All[..^1].Select(t => t.Name)) + " or " + All[^1].Name;

    public Rgb GradientAt(double t) => Gradient[Math.Clamp((int)(t * Gradient.Length), 0, Gradient.Length - 1)];

    public Style S(Rgb fg) => new(fg, Bg);

    /// <summary>Black or white, whichever reads better on <paramref name="bg"/> (text on badges, tabs and blocks).</summary>
    public static Rgb On(Rgb bg) => Contrast(Rgb.Hex(0x000000), bg) >= Contrast(Rgb.Hex(0xffffff), bg) ? Rgb.Hex(0x000000) : Rgb.Hex(0xffffff);

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Rgb c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG contrast ratio between two colours, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
