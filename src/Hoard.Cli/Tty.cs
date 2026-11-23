namespace Hoard.Cli;

/// <summary>Colour and glyph decisions for non-interactive output (plan.md §7.6, §10).</summary>
internal sealed class Tty
{
    public bool Color { get; }
    public bool Interactive { get; }

    public Tty(bool noColorFlag)
    {
        string? term = Environment.GetEnvironmentVariable("TERM");
        Interactive = !Console.IsOutputRedirected;
        Color = Interactive && !noColorFlag
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
            && term != "dumb";
    }

    // "hoard" theme, 256-colour palette (examples/lib.sh).
    public static readonly int[] Gradient = [226, 220, 214, 208, 202, 196, 160, 124];
    public const int Accent = 214, Muted = 244, Faint = 239, Text = 252, Ok = 114, Warn = 221, Bad = 203, Info = 81, Track = 236;

    public string Fg(int c, string s) => Color ? $"\e[38;5;{c}m{s}\e[0m" : s;
    public string Bold(string s) => Color ? $"\e[1m{s}\e[0m" : s;

    private static readonly string[] Eighths = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];

    /// <summary>A gradient bar with eighth-block precision, like examples/lib.sh.</summary>
    public string Bar(double fraction, int width)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        int eighths = (int)Math.Round(fraction * width * 8);
        int full = eighths / 8, rem = eighths % 8;
        if (!Color)
        {
            return new string('#', full) + (rem >= 4 ? "+" : "") + new string('-', width - full - (rem >= 4 ? 1 : 0));
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < full; i++)
        {
            sb.Append($"\e[38;5;{Gradient[i * Gradient.Length / width]}m█");
        }
        int used = full;
        if (rem > 0 && used < width)
        {
            sb.Append($"\e[48;5;{Track}m\e[38;5;{Gradient[Math.Min(full, width - 1) * Gradient.Length / width]}m{Eighths[rem]}");
            used++;
        }
        sb.Append($"\e[48;5;{Track}m").Append(' ', width - used).Append("\e[0m");
        return sb.ToString();
    }
}
