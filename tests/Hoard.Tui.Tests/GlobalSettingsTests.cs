using Hoard.Core.Config;
using Hoard.Core.Formatting;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

[CollectionDefinition("process-wide settings", DisableParallelization = true)]
public sealed class ProcessWideSettings;

/// <summary>Units and glyphs are process-wide switches, so these run on their own.</summary>
[Collection("process-wide settings")]
public sealed class GlobalSettingsTests(Fixture f) : IClassFixture<Fixture>, IDisposable
{
    public void Dispose()
    {
        Units.DefaultIec = false;
        Glyphs.Ascii = false;
    }

    private BrowserModel Make(Settings s)
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Browse };
        m.UseSettings(s, null, apparent: false);
        return m;
    }

    private static string Render(BrowserModel m)
    {
        var c = new MemoryCanvas(110, 26);
        Screens.Browser(c, m);
        return c.ToString();
    }

    [Fact]
    public void Units_can_be_powers_of_1024()
    {
        Assert.Equal("1.0 KiB", Units.Size(1024, iec: true));
        BrowserModel m = Make(new Settings { Iec = true });
        Assert.Contains("KiB", Render(m));
        Assert.DoesNotContain(" KB ", Render(m));
    }

    [Fact]
    public void Ascii_glyphs_replace_boxes_and_bars()
    {
        BrowserModel m = Make(new Settings { AsciiGlyphs = true });
        string s = Render(m);
        Assert.DoesNotContain("╭", s);
        Assert.DoesNotContain("█", s);
        Assert.Contains("+-", s);
        Assert.Contains("#", s);
        Assert.All(s, ch => Assert.True(ch < 128 || char.IsLetterOrDigit(ch) || ch == '名' || ch is >= '　', $"left as Unicode: {ch} (U+{(int)ch:X4})"));
    }
}
