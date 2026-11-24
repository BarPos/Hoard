using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Tests;

public sealed class RescanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-rescan-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string rel, int bytes)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
    }

    [Fact]
    public async Task R_rescans_the_highlighted_folder_and_updates_everything()
    {
        Write("work/a.bin", 10_000);
        Write("other/b.bin", 5_000);
        var scanner = new Scanner(FileProbes.ForCurrentPlatform());
        ScanSession session = scanner.Start(_root);
        await session.Completion;
        var m = new BrowserModel(session) { View = ViewKind.Browse, Rescanner = (p, c) => scanner.Scan(p, c) };
        var ctl = new BrowserController(m, new FakeShell());
        int work = m.Store.Children(NodeStore.Root).First(id => m.Store.Name(id) == "work");
        m.SelectNode(work);
        m.ToggleMark(m.Store.Children(work).First());

        Write("work/big.bin", 400_000);
        ctl.Apply(BrowserController.FromChar('r'), 10);
        Assert.Contains("rescanning", m.Toast);
        Assert.True(m.Rescanning);
        m.WaitForRescan();
        Assert.False(m.Rescanning);
        Assert.Contains("rescanned work", m.Toast);
        Assert.Contains("+", m.Toast);
        Assert.Empty(m.Marks); // the marked entry was replaced
        Assert.True(m.Store.Size(NodeStore.Root) > 400_000);
        Assert.Equal("work", m.Store.Name(m.Rows[0])); // biggest first, with the new size
    }

    [Fact]
    public async Task Shift_r_asks_the_app_for_a_whole_new_scan()
    {
        Write("a.bin", 1_000);
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        await session.Completion;
        var ctl = new BrowserController(new BrowserModel(session), new FakeShell());
        ctl.Apply(BrowserController.FromChar('R'), 10);
        Assert.True(ctl.RescanAllRequested);
    }
}
