using Hoard.Core.Config;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class SettingsViewTests(Fixture f) : IClassFixture<Fixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hoard-settings-").FullName;
    private string ConfigPath => Path.Combine(_dir, "hoard", "config.toml");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Overview };
        m.UseSettings(new Settings(), ConfigPath, apparent: false);
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void Comma_opens_settings_with_every_theme_previewed()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar(','), 10);
        Assert.True(m.SettingsOpen);
        MemoryCanvas c = Render(m);
        string s = c.ToString();
        Assert.Contains("Settings", c.Row(2));
        Assert.Contains("Themes", c.Row(2));
        Assert.Contains("saved to", c.Row(1));
        Assert.Contains("hoard", s);   // the theme in use…
        Assert.Contains("neon", s);    // …and its neighbours
        Assert.Contains("↓", s);       // more below: the list scrolls
        m.Theme = Theme.All[^1];
        string last = Render(m).ToString();
        Assert.Contains(Theme.All[^1].Name, last); // scrolled to keep the one in use in view
        Assert.Contains("in use", s);
        Assert.Contains("◂ hoard ▸", s);
        Assert.Contains("Appearance", s);
        Assert.Contains("Big & old", s);
        ctl.Apply(Cmd.Escape, 10);
        Assert.False(m.SettingsOpen);
    }

    [Fact]
    public void Changing_the_theme_applies_at_once_and_is_saved()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Settings, 10);
        ctl.Apply(Cmd.Right, 10);
        Assert.Equal("neon", m.Theme.Name);
        Assert.Contains("saved to", m.Toast);
        Assert.Equal("neon", SettingsFile.Load(ConfigPath).Settings.Theme);
        ctl.Apply(Cmd.Left, 10);
        ctl.Apply(Cmd.Left, 10); // wraps round to the last theme
        Assert.Equal(Theme.All[^1].Name, SettingsFile.Load(ConfigPath).Settings.Theme);
    }

    [Fact]
    public void T_anywhere_saves_the_theme_too()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.NextTheme, 10);
        Assert.Equal("neon", SettingsFile.Load(ConfigPath).Settings.Theme);
    }

    [Fact]
    public void Other_settings_save_and_take_effect()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Settings, 10);
        int Row(string label) => Array.FindIndex(SettingsItems.All, i => i.Label == label);
        m.SettingsRow = Row("Mouse");
        ctl.Apply(Cmd.Mark, 10);
        Assert.False(m.Settings.Mouse);
        m.SettingsRow = Row("Size shown");
        ctl.Apply(Cmd.Right, 10);
        Assert.True(m.Apparent);
        m.SettingsRow = SettingsItems.All.Length - 1; // Big & old: based on
        ctl.Apply(Cmd.Right, 10);
        Settings saved = SettingsFile.Load(ConfigPath).Settings;
        Assert.False(saved.Mouse);
        Assert.True(saved.ApparentSizes);
        Assert.Equal(Hoard.Core.Analysis.AgeAxis.Accessed, saved.BigOld.Axis);
        Assert.Equal(saved.BigOld, m.BigOldOptions); // the Big & old tab follows
    }

    [Fact]
    public void Without_a_settings_file_nothing_is_written()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null);
        new BrowserController(m, new FakeShell()).Apply(Cmd.NextTheme, 10);
        Assert.False(File.Exists(ConfigPath));
        Assert.Contains("not saved", Render(new BrowserModel(f.Session) { SettingsOpen = true }).Row(1));
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        var (m, _) = Make();
        m.SettingsOpen = true;
        foreach (int w in new[] { 60, 89, 90, 120, 160 })
        {
            foreach (int h in new[] { 16, 20, 30 })
            {
                MemoryCanvas c = Render(m, w, h);
                Assert.Contains("q quit", c.Row(h - 1));
                for (int y = 3; y < h - 2; y++)
                {
                    string row = c.Row(y).TrimEnd();
                    Assert.True(row.EndsWith('│') || row.EndsWith('┤'), $"{w}×{h} row {y} spills: {row}");
                }
            }
        }
    }

    [Fact]
    public void Big_and_old_filters_are_remembered()
    {
        var (m, ctl) = Make();
        m.View = ViewKind.BigOld;
        ctl.Apply(Cmd.Filters, 10);
        ctl.Apply(Cmd.Left, 10); // 100 MB → 50 MB
        Assert.Equal(50_000_000, SettingsFile.Load(ConfigPath).Settings.BigOld.MinSize);
    }
}
