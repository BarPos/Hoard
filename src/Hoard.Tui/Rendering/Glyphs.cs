using System.Text;

namespace Hoard.Tui.Rendering;

/// <summary>
/// ASCII fallbacks for the box-drawing, block and symbol characters (the "Glyphs" setting), for
/// terminals and fonts that show them as boxes. Every replacement is one cell wide, so layouts hold.
/// </summary>
public static class Glyphs
{
    public static bool Ascii { get; set; }

    private static readonly Dictionary<char, char> Map = new()
    {
        ['╭'] = '+', ['╮'] = '+', ['╰'] = '+', ['╯'] = '+', ['├'] = '+', ['┤'] = '+', ['┬'] = '+', ['┴'] = '+', ['┼'] = '+',
        ['─'] = '-', ['│'] = '|', ['━'] = '=',
        ['█'] = '#', ['▉'] = '#', ['▊'] = '#', ['▋'] = '#', ['▌'] = '#', ['▍'] = '+', ['▎'] = '+', ['▏'] = '|',
        ['▀'] = '"', ['▄'] = '_', ['▁'] = '_', ['▂'] = '_', ['▃'] = '-', ['▅'] = '=', ['▆'] = '=', ['▇'] = '#',
        ['■'] = '#', ['▣'] = '#', ['▦'] = '#', ['▤'] = '#', ['◫'] = '#', ['◎'] = 'o', ['◆'] = '*', ['◐'] = 'o',
        ['►'] = '>', ['▸'] = '>', ['▾'] = 'v', ['▷'] = '>', ['◂'] = '<', ['▲'] = '^', ['▼'] = 'v',
        ['●'] = '*', ['○'] = 'o', ['□'] = 'o', ['✓'] = 'v', ['★'] = '*', ['⊘'] = 'x', ['·'] = '.', ['…'] = '~',
        ['≡'] = '=', ['⧉'] = '&', ['≋'] = '~', ['⇄'] = '=', ['♪'] = 'd', ['»'] = '>', ['ƒ'] = 'f', ['⌂'] = '~',
        ['←'] = '<', ['→'] = '>', ['↑'] = '^', ['↓'] = 'v', ['↵'] = '<', ['⌫'] = '<', ['␣'] = '_', ['−'] = '-',
        ['×'] = 'x', ['≥'] = '>', ['≈'] = '~', ['Δ'] = 'D', ['❚'] = '|',
        ['⠋'] = '|', ['⠙'] = '/', ['⠹'] = '-', ['⠸'] = '\\', ['⠼'] = '|', ['⠴'] = '/', ['⠦'] = '-', ['⠧'] = '\\', ['⠇'] = '|', ['⠏'] = '/',
    };

    /// <summary>The text as it should be drawn: unchanged, or with every mapped character replaced.</summary>
    public static string Apply(string text)
    {
        if (!Ascii)
        {
            return text;
        }
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (Map.TryGetValue(text[i], out char r))
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
                sb.Append(r);
            }
            else
            {
                sb?.Append(text[i]);
            }
        }
        return sb?.ToString() ?? text;
    }

    public static char Apply(char c) => Ascii && Map.TryGetValue(c, out char r) ? r : c;
}
