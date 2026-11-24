using Hoard.Core.Config;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class ThemeTests(Fixture f) : IClassFixture<Fixture>
{
    public static TheoryData<string> Names() => [.. Theme.All.Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_theme_is_readable(string name)
    {
        Theme t = Theme.Named(name);
        var problems = new List<string>();
        void Need(string what, Rgb fg, Rgb bg, double min)
        {
            double c = Theme.Contrast(fg, bg);
            if (c < min)
            {
                problems.Add($"{what}: {c:0.00} < {min}");
            }
        }
        foreach (var (slotName, bg) in new[] { ("background", t.Bg), ("panel", t.Panel) })
        {
            Need("text on " + slotName, t.Text, bg, 4.5);
            Need("muted on " + slotName, t.Muted, bg, 3);
            Need("accent on " + slotName, t.Accent, bg, 3);
            Need("accent 2 on " + slotName, t.Accent2, bg, 3);
            Need("ok on " + slotName, t.Ok, bg, 3);
            Need("warn on " + slotName, t.Warn, bg, 3);
            Need("bad on " + slotName, t.Bad, bg, 3);
            Need("info on " + slotName, t.Info, bg, 3);
        }
        Need("text on the selection", t.Text, t.Selection, 4.5);
        Need("text on the header bar", t.Muted, t.Bar, 3);
        // Badges, tabs and buttons put black or white on these.
        foreach (var (slotName, bg) in new[] { ("accent", t.Accent), ("accent 2", t.Accent2), ("ok", t.Ok), ("warn", t.Warn), ("bad", t.Bad) })
        {
            Need("label on " + slotName, Theme.On(bg), bg, 4.5);
        }
        Assert.True(problems.Count == 0, $"{name}: " + string.Join("; ", problems));
        Assert.Equal(8, t.Gradient.Length);
        Assert.False(string.IsNullOrWhiteSpace(t.Description));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_view_draws_in_every_theme(string name)
    {
        var m = new BrowserModel(f.Session, Theme.Named(name), () => Fixture.Now, _ => null);
        m.WaitForTypes();
        m.WaitForJunk();
        foreach (ViewKind v in Enum.GetValues<ViewKind>())
        {
            m.View = v;
            var c = new MemoryCanvas(110, 30);
            Screens.Browser(c, m);
            Assert.Contains("q quit", c.Row(29));
        }
        m.SettingsOpen = true;
        var s = new MemoryCanvas(120, 30);
        Screens.Browser(s, m);
        Assert.Contains(name, s.ToString()); // the theme in use is always in view in the picker
    }

    [Fact]
    public void Names_are_unique_and_round_trip_through_settings()
    {
        Assert.Equal(Theme.All.Length, Theme.All.Select(t => t.Name).Distinct().Count());
        Assert.True(Theme.All.Length >= 12);
        foreach (Theme t in Theme.All)
        {
            Assert.Same(t, Theme.Named(t.Name.ToUpperInvariant()));
            Assert.Contains(t.Name, Theme.NameList);
        }
        Assert.Contains(Theme.All, t => t.IsLight);
        Assert.Same(Theme.Hoard, Theme.Named("no-such-theme"));
    }

    [Fact]
    public void Text_on_colour_picks_black_or_white()
    {
        Assert.Equal(Rgb.Hex(0x000000), Theme.On(Rgb.Hex(0xffd166)));
        Assert.Equal(Rgb.Hex(0xffffff), Theme.On(Rgb.Hex(0x1a6fc4)));
        Assert.Equal(21, Theme.Contrast(Rgb.Hex(0x000000), Rgb.Hex(0xffffff)), 1);
    }
}
