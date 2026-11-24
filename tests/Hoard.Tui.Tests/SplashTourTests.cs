using Hoard.Core.Config;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class SplashTourTests(Fixture f) : IClassFixture<Fixture>, IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("hoard-tour-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_cfg, true); } catch { }
    }

    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Overview };
        m.UseSettings(new Settings(), Path.Combine(_cfg, "config.toml"), apparent: false);
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 110, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void The_splash_shows_the_logo_and_any_key_skips_it()
    {
        var (m, ctl) = Make();
        m.SplashTicks = 19;
        m.Tick = 18; // the logo has finished wiping in
        string s = Render(m).ToString();
        Assert.Contains("██████", s);
        Assert.Contains("what's hoarding your disk?", s);
        Assert.Contains("tip:", s);
        Assert.Contains("any key to skip", s);
        ctl.Apply(Cmd.Down, 10);
        Assert.False(m.SplashOpen);
        Assert.Contains("Reclaimable", Render(m).Row(2));
    }

    [Fact]
    public void The_splash_ends_by_itself()
    {
        var (m, _) = Make();
        m.SplashTicks = 19;
        m.Tick = 19;
        Assert.False(m.SplashOpen);
    }

    [Fact]
    public void The_tour_walks_three_steps_and_is_remembered()
    {
        var (m, ctl) = Make();
        m.TourStep = 0;
        m.View = ViewKind.Browse;
        Assert.Contains("1 of 3", Render(m).ToString());
        ctl.Apply(Cmd.Right, 10);
        Assert.Equal(ViewKind.Junk, m.View);
        Assert.Contains("2 of 3", Render(m).ToString());
        ctl.Apply(Cmd.Left, 10);
        Assert.Equal(0, m.TourStep);
        ctl.Apply(Cmd.Right, 10);
        ctl.Apply(Cmd.Right, 10);
        Assert.Contains("3 of 3", Render(m).ToString());
        ctl.Apply(Cmd.Right, 10);
        Assert.Null(m.TourStep);
        Assert.True(SettingsFile.Load(Path.Combine(_cfg, "config.toml")).Settings.TourDone);
    }

    [Fact]
    public void Esc_skips_the_tour()
    {
        var (m, ctl) = Make();
        m.TourStep = 0;
        ctl.Apply(Cmd.Escape, 10);
        Assert.Null(m.TourStep);
        Assert.Equal(ViewKind.Overview, m.View);
        Assert.True(m.Settings.TourDone);
    }

    [Fact]
    public void Hover_highlights_the_row_under_the_mouse()
    {
        var (m, _) = Make();
        m.View = ViewKind.Browse;
        var plain = Render(m);
        m.HoverY = 6;
        var hovered = Render(m);
        Assert.NotEqual(plain.StyleAt(10, 6).Bg, hovered.StyleAt(10, 6).Bg);
    }

    [Fact]
    public void Tips_can_be_switched_off()
    {
        var (m, _) = Make();
        Assert.NotNull(m.Tip);
        m.Settings.Tips = false;
        Assert.Null(m.Tip);
    }
}
