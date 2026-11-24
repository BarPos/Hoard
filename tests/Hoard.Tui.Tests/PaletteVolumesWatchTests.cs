using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class PaletteVolumesWatchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-pvw-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<(BrowserModel M, BrowserController C)> Make()
    {
        Directory.CreateDirectory(Path.Combine(_root, "inbox"));
        File.WriteAllBytes(Path.Combine(_root, "inbox", "a.bin"), new byte[1_000]);
        var scanner = new Scanner(FileProbes.ForCurrentPlatform());
        ScanSession session = scanner.Start(_root);
        await session.Completion;
        var m = new BrowserModel(session, null, null, _ => null) { View = ViewKind.Overview, Rescanner = (p, c) => scanner.Scan(p, c) };
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public async Task The_palette_finds_actions_by_name_and_runs_them()
    {
        var (m, ctl) = await Make();
        ctl.Apply(BrowserController.FromChar(':'), 10);
        Assert.True(ctl.WantsText);
        Assert.Contains("Do something", Render(m).ToString());
        foreach (char ch in "big old")
        {
            ctl.TypeChar(ch);
        }
        Assert.Equal(Cmd.ViewBigOld, m.Palette!.Matches()[0].Cmd);
        ctl.Apply(Cmd.Open, 10);
        Assert.Null(m.Palette);
        Assert.Equal(ViewKind.BigOld, m.View);
    }

    [Fact]
    public async Task Volumes_list_the_disks_and_pick_one_to_scan()
    {
        var (m, ctl) = await Make();
        ctl.Apply(BrowserController.FromChar('v'), 10);
        Assert.True(m.VolumesOpen);
        Assert.Contains(m.VolumeList, v => v.Mount == "/");
        string s = Render(m).ToString();
        Assert.Contains("Mounted volumes", s);
        Assert.Contains("Startup disk", s);
        ctl.Apply(Cmd.PathEntry, 10); // p: type a path
        foreach (char ch in _root)
        {
            ctl.TypeChar(ch);
        }
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal(_root, ctl.ScanPathRequested);
        Assert.False(m.VolumesOpen);
    }

    [Fact]
    public async Task Watching_shows_new_files_in_the_feed()
    {
        var (m, ctl) = await Make();
        ctl.Apply(BrowserController.FromChar('w'), 10);
        Assert.Equal(ViewKind.Watch, m.View);
        Assert.NotNull(m.Watching);
        Assert.Contains("Watch", Render(m).Row(2));
        await Task.Delay(300); // let FSEvents start
        File.WriteAllBytes(Path.Combine(_root, "inbox", "new.bin"), new byte[500_000]);
        bool seen = SpinWait.SpinUntil(() =>
        {
            m.PollWatch();
            m.WaitForRescan();
            return m.Watching!.Feed.Count > 0;
        }, 10_000);
        Assert.True(seen, "the new file never showed up");
        WatchChange change = m.Watching!.Feed[0];
        Assert.True(change.Delta >= 500_000);
        Assert.EndsWith("inbox", change.Path);
        Assert.Contains("inbox", Render(m).ToString());
        ctl.Apply(Cmd.Escape, 10);
        Assert.Null(m.Watching);
        Assert.Equal(ViewKind.Overview, m.View);
    }

    [Fact]
    public async Task Nearest_folder_follows_the_path_as_far_as_the_scan_goes()
    {
        var (m, _) = await Make();
        int inbox = m.Store.Children(NodeStore.Root).First();
        Assert.Equal(inbox, m.NearestFolder(Path.Combine(_root, "inbox", "not", "scanned", "yet")));
        Assert.Equal(NodeStore.Root, m.NearestFolder(_root));
        Assert.Equal(NodeStore.None, m.NearestFolder("/somewhere/else"));
    }
}
