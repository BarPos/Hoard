using Hoard.Core.Config;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class ExcludeViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-exclude-").FullName;
    private readonly string _cfg = Directory.CreateTempSubdirectory("hoard-exclude-cfg-").FullName;
    private string ConfigPath => Path.Combine(_cfg, "config.toml");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_cfg, true); } catch { }
    }

    private async Task<(BrowserModel, BrowserController)> Make()
    {
        foreach (var (rel, n) in new[] { ("cache/x.bin", 90_000), ("docs/a.txt", 1_000) })
        {
            string p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[n]);
        }
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        await session.Completion;
        var m = new BrowserModel(session, null, null, _ => null) { View = ViewKind.Browse };
        m.UseSettings(new Settings(), ConfigPath, apparent: false);
        return (m, new BrowserController(m, new FakeShell()));
    }

    [Fact]
    public async Task X_excludes_the_highlighted_folder_and_remembers_it()
    {
        var (m, ctl) = await Make();
        int cache = m.Store.Children(NodeStore.Root).First(id => m.Store.Name(id) == "cache");
        m.SelectNode(cache);
        ctl.Apply(BrowserController.FromChar('X'), 10);
        Assert.Contains("excluded", m.Toast);
        Assert.Equal(NodeKind.Excluded, m.Store.Kind(cache));
        Assert.True(m.Store.Size(NodeStore.Root) < 90_000);
        string pattern = Assert.Single(SettingsFile.Load(ConfigPath).Settings.Exclude);
        Assert.EndsWith("/cache/", pattern);
    }

    [Fact]
    public async Task Patterns_can_be_added_and_removed_in_settings()
    {
        var (m, ctl) = await Make();
        ctl.Apply(Cmd.Settings, 10);
        ctl.Apply(Cmd.Bottom, 10); // "+ add pattern"
        ctl.Apply(Cmd.Open, 10);
        Assert.True(ctl.WantsText);
        foreach (char ch in "*.tmp")
        {
            ctl.TypeChar(ch);
        }
        var c = new MemoryCanvas(120, 30);
        Screens.Browser(c, m);
        Assert.Contains("+ *.tmp▏", c.ToString());
        ctl.Apply(Cmd.Open, 10);
        Assert.False(ctl.WantsText);
        Assert.Equal(["*.tmp"], SettingsFile.Load(ConfigPath).Settings.Exclude);
        c = new MemoryCanvas(120, 30);
        Screens.Browser(c, m);
        Assert.Contains("⊘ *.tmp", c.ToString());
        Assert.Contains("Exclude patterns", c.ToString());
        ctl.Apply(Cmd.Up, 10); // onto the pattern
        ctl.Apply(Cmd.Forget, 10);
        Assert.Empty(SettingsFile.Load(ConfigPath).Settings.Exclude);
    }
}
