// M0 spike: a gradient size-bar list + animated progress drawn by a custom View, built with Native AOT.
// HOARD_SPIKE_QUIT_MS=<n> quits automatically after n ms (for unattended runs under a pty).
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

using IApplication app = Application.Create();
app.Init();
var win = new SpikeWindow();
if (int.TryParse(Environment.GetEnvironmentVariable("HOARD_SPIKE_QUIT_MS"), out int quitMs))
{
    app.AddTimeout(TimeSpan.FromMilliseconds(quitMs), () => { app.RequestStop(); return false; });
}
app.AddTimeout(TimeSpan.FromMilliseconds(33), () => { win.Bars.Tick++; win.Bars.SetNeedsDraw(); return true; });
app.Run(win);
Console.WriteLine($"spike ok: {win.Bars.Tick} frames, last draw {win.Bars.LastDrawMicros} µs");

internal sealed class SpikeWindow : Window
{
    public BarsView Bars { get; } = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    public SpikeWindow()
    {
        Title = "HOARD spike (Esc quits)";
        Add(Bars);
    }
}

internal sealed class BarsView : View
{
    public int Tick;
    public long LastDrawMicros;
    private static readonly (string Name, double Gb)[] Rows =
    [
        ("monolith", 31.2), ("game-engine", 19.6), ("ml-sandbox", 14.1), ("website", 8.3),
        ("android-app", 7.9), ("scratch", 4.8), ("rust-cli", 2.2), ("dotfiles", 0.212),
    ];
    private static readonly string Eighths = " ▏▎▍▌▋▊▉";
    private static readonly (int R, int G, int B)[] Grad =
        [(255, 255, 0), (255, 215, 0), (255, 175, 0), (255, 135, 0), (255, 95, 0), (255, 0, 0)];

    protected override bool OnDrawingContent(DrawContext? context)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var bg = new TgColor(18, 18, 18);
        var track = new TgColor(48, 48, 48);
        int barW = Math.Max(10, Viewport.Width - 34);
        double total = Rows.Sum(r => r.Gb);
        for (int i = 0; i < Rows.Length; i++)
        {
            var (name, gb) = Rows[i];
            SetAttribute(new TgAttribute(new TgColor(208, 208, 208), bg));
            Move(1, i);
            AddStr($"{name,-14}{gb,6:0.0} GB ");
            double frac = Math.Min(1, gb / total * Math.Min(1, Tick / 30.0));
            int eighths = (int)(frac * barW * 8);
            for (int x = 0; x < barW; x++)
            {
                var g = Grad[x * (Grad.Length - 1) / Math.Max(1, barW - 1)];
                var fg = new TgColor(g.R, g.G, g.B);
                int cell = Math.Clamp(eighths - x * 8, 0, 8);
                SetAttribute(new TgAttribute(fg, track));
                AddRune(cell == 8 ? new Rune('█') : new Rune(Eighths[cell]));
            }
        }
        SetAttribute(new TgAttribute(new TgColor(255, 175, 0), bg));
        Move(1, Rows.Length + 1);
        AddStr($"{"⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[Tick % 10]} frame {Tick}  ·  {Viewport.Width}×{Viewport.Height}");
        LastDrawMicros = (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return true;
    }
}
