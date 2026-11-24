using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Rendering;

public static partial class Screens
{
    private static readonly string[] Logo =
    [
        "██   ██  ██████   █████  ██████  ██████ ",
        "██░  ██░██░░░░██ ██░░░██ ██░░░██ ██░░░██",
        "███████░██░   ██░███████░██████░░██░  ██░",
        "██░░░██░██░   ██░██░░░██░██░░░██ ██░  ██░",
        "██░  ██░ ██████░░██░  ██░██░  ██░██████░░",
        " ░░   ░░  ░░░░░░  ░░   ░░ ░░   ░░ ░░░░░░ ",
    ];

    private static readonly string[] Chest =
    [
        "  ┌─────────────────┐  ",
        " ╱ ◦ ◆ ◦ ◆ ◦ ◆ ◦ ◆ ◦ ╲ ",
        "╱─────────────────────╲",
        "│▒▒▒▒▒▒▒▒▒╔═╗▒▒▒▒▒▒▒▒▒│",
        "│░░░░░░░░░╚═╝░░░░░░░░░│",
        "│░░░░░░░░░░░░░░░░░░░░░│",
        "└─────────────────────┘",
    ];

    /// <summary>The splash (plan.md §7.8; mock: examples/00-splash.sh): ≤ 1.5 s, any key skips.</summary>
    public static HitMap SplashView(ICanvas c, BrowserModel m)
    {
        var hits = new HitMap();
        Theme t = m.Theme;
        var p = new Painter(c, t);
        p.Clear();
        int w = c.Width, h = c.Height;
        p.HLine(0, 0, w, '╭', '╮', null, "v" + Hoard.Core.AppInfo.Version);
        p.VLine(0, 1, h - 2);
        p.VLine(w - 1, 1, h - 2);
        p.HLine(0, h - 1, w, '╰', '╯', null, "any key to skip");
        double progress = Math.Clamp((double)m.Tick / Math.Max(1, m.SplashTicks), 0, 1);

        // The logo wipes in from the left in the first third, with a shimmer sweeping after it.
        bool chest = w >= 76;
        int logoW = Logo[0].Length, chestW = chest ? Chest[0].Length + 4 : 0;
        int x0 = Math.Max(2, (w - logoW - chestW) / 2), y0 = Math.Max(1, h / 2 - 7);
        int reveal = (int)(Math.Min(1, progress * 3) * logoW);
        int shimmer = (int)(progress * (logoW + 20)) - 10;
        for (int row = 0; row < Logo.Length; row++)
        {
            string line = Logo[row];
            for (int col = 0; col < Math.Min(reveal, line.Length); col++)
            {
                char ch = line[col];
                if (ch == ' ')
                {
                    continue;
                }
                Rgb fg = ch == '░' ? t.Faint : Math.Abs(col - shimmer) < 2 ? t.Text : t.GradientAt((double)col / logoW);
                p.Text(x0 + col, y0 + 1 + row, ch.ToString(), fg, attr: ch == '░' ? TextAttr.None : TextAttr.Bold);
            }
        }
        if (chest)
        {
            int cx = x0 + logoW + 4, cy = y0;
            for (int row = 0; row < Chest.Length; row++)
            {
                p.Text(cx, cy + row, Chest[row], row == 1 ? t.Accent : row is 3 or 4 ? t.Accent2 : t.Border);
            }
            // Sparkles twinkle around the chest.
            (int dx, int dy)[] spots = [(-2, -1), (5, -2), (12, -1), (20, -2), (25, 0), (-3, 2), (26, 3)];
            for (int i = 0; i < spots.Length; i++)
            {
                int phase = (m.Tick + i * 3) % 8;
                string star = phase < 2 ? "*" : phase < 4 ? "·" : phase < 5 ? "✦" : " ";
                if (cy + spots[i].dy >= 1)
                {
                    p.Text(cx + spots[i].dx, cy + spots[i].dy, star, t.GradientAt(i / 8.0));
                }
            }
        }

        int ty = y0 + 9;
        void Centre(int y, string text, Rgb fg, TextAttr attr = TextAttr.None)
        {
            string fit = Cells.Fit(text, w - 4).TrimEnd();
            p.Text(Math.Max(2, (w - Cells.Width(fit)) / 2), y, fit, fg, attr: attr);
        }
        Centre(ty, "what's hoarding your disk?", t.Text, TextAttr.Bold);
        Centre(ty + 1, "a colourful, very fast disk-space explorer for the terminal", t.Muted);

        // The scan is already running underneath: show how far it is.
        long files = m.Session.Progress.Files;
        string counts = $"{Units.Count(files)} files · {m.Elapsed.TotalSeconds:0.0} s";
        int barW = Math.Max(10, Math.Min(40, w - 40));
        string label = $"Scanning {CrumbName(m, NodeStore.Root)}  ";
        int bx = Math.Max(2, (w - Cells.Width(label) - barW - Cells.Width(counts) - 4) / 2);
        int lx = p.Text(bx, ty + 3, label, t.Muted);
        if (m.Scanning)
        {
            int seg = Math.Max(3, barW / 5), pos = m.Tick % (barW - seg + 1);
            for (int i = 0; i < barW; i++)
            {
                p.Text(lx + i, ty + 3, i >= pos && i < pos + seg ? "█" : " ", t.GradientAt((double)i / barW), t.Track);
            }
        }
        else
        {
            p.Bar(lx, ty + 3, barW, 1);
        }
        p.Text(lx + barW + 2, ty + 3, counts, t.Text);
        if (m.Tip is { } tip)
        {
            Centre(ty + 5, "tip: " + tip + " · ? for every key", t.Faint);
        }
        return hits;
    }

    private static readonly (string Title, string Text)[] TourSteps =
    [
        ("1 of 3 · size bars", "Each bar is the item's share of the folder you're in, biggest first. → opens a folder, ← goes back up."),
        ("2 of 3 · known junk", "Build outputs, caches and old installers that are safe to remove. SAFE ones come back by themselves; c cleans what's ticked."),
        ("3 of 3 · marking and deleting", "␣ marks anything, in any tab; d moves everything marked to the Trash (it asks first), and u undoes."),
    ];

    private static void TourBox(Painter p, int step)
    {
        Theme t = p.Theme;
        ICanvas c = p.Canvas;
        var (title, text) = TourSteps[Math.Clamp(step, 0, TourSteps.Length - 1)];
        int w = Math.Min(c.Width - 4, 64);
        var lines = Wrap(text, w - 6).ToList();
        int h = lines.Count + 5;
        int x0 = (c.Width - w) / 2, y0 = Math.Max(1, c.Height - h - 2);
        c.Fill(x0, y0, w, h, new Style(t.Text, t.Panel));
        p.HLine(x0, y0, w, '╭', '╮', "Welcome to Hoard · " + title);
        for (int i = 1; i < h - 1; i++)
        {
            p.Text(x0, y0 + i, "│", t.Accent, t.Panel);
            p.Text(x0 + w - 1, y0 + i, "│", t.Accent, t.Panel);
        }
        p.HLine(x0, y0 + h - 1, w, '╰', '╯');
        for (int i = 0; i < lines.Count; i++)
        {
            p.Text(x0 + 3, y0 + 2 + i, lines[i], t.Text, t.Panel);
        }
        string keys = step < TourSteps.Length - 1 ? "→ next · ← back · esc skip the tour" : "→ done · ← back · esc close";
        p.Text(x0 + 3, y0 + h - 2, Cells.Fit(keys, w - 6).TrimEnd(), t.Muted, t.Panel);
    }
}
