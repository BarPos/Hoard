using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

/// <summary>The full-screen scan progress, shown while a scan is held open by a gated probe.</summary>
public sealed class ScanScreenTests : IDisposable
{
    private sealed class GatedProbe(IFileProbe inner, string gated) : IFileProbe
    {
        public ManualResetEventSlim Gate { get; } = new(false);
        public string Name => inner.Name;
        public bool IsExact => inner.IsExact;
        public bool TryGetInfo(string path, out EntryInfo info, out int errno)
        {
            if (path.StartsWith(gated + "/", StringComparison.Ordinal)) Gate.Wait(TimeSpan.FromSeconds(10));
            return inner.TryGetInfo(path, out info, out errno);
        }
    }

    private readonly string _root = Directory.CreateTempSubdirectory("hoard-scanscreen-").FullName;
    private readonly GatedProbe _probe;
    private readonly ScanSession _session;
    private readonly BrowserModel _m;
    private readonly BrowserController _ctl;

    public ScanScreenTests()
    {
        Write("done/a.bin", 400_000);
        Write("held/b.bin", 90_000);
        _probe = new GatedProbe(FileProbes.ForCurrentPlatform(), Path.Combine(_root, "held"));
        _session = new Scanner(_probe).Start(_root);
        Assert.True(SpinWait.SpinUntil(() => _session.Store.Children(NodeStore.Root).Any(id => _session.Store.Name(id) == "done"), 10_000));
        _m = new BrowserModel(_session, null, null, _ => null) { View = ViewKind.Overview, ScanScreen = true };
        _ctl = new BrowserController(_m, new FakeShell());
    }

    private void Write(string rel, int bytes)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
    }

    public void Dispose()
    {
        _probe.Gate.Set();
        _session.Completion.Wait(10_000);
        try { Directory.Delete(_root, true); } catch { }
    }

    private MemoryCanvas Render(int w = 110, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, _m);
        return c;
    }

    [Fact]
    public void Shows_progress_and_the_biggest_folders_so_far()
    {
        Assert.True(_m.Scanning);
        _m.SampleScan(0);
        _m.SampleScan(500);
        MemoryCanvas c = Render();
        string s = c.ToString();
        Assert.Contains("Scanning", c.Row(2));
        Assert.Contains("found so far", s);
        Assert.Contains("files/s", s);
        Assert.Contains("Biggest so far", s);
        Assert.Contains("done/", s);
        Assert.Contains("held/", s);
        Assert.Contains("results now", c.Row(29));
    }

    [Fact]
    public void B_skips_to_the_results_which_keep_filling_in()
    {
        _ctl.Apply(BrowserController.FromChar('b'), 10);
        Assert.False(_m.ScanScreen);
        Assert.Equal(ViewKind.Overview, _m.View);
        Assert.Contains("Reclaimable", Render().Row(2));
        Assert.True(_m.Scanning);
    }

    [Fact]
    public void A_tab_key_skips_straight_to_that_tab()
    {
        _ctl.Apply(BrowserController.FromChar('2'), 10);
        Assert.False(_m.ScanScreen);
        Assert.Equal(ViewKind.Browse, _m.View);
    }

    [Fact]
    public void Other_keys_explain_how_to_leave_and_q_still_asks_to_quit()
    {
        _ctl.Apply(Cmd.Down, 10);
        Assert.True(_m.ScanScreen);
        Assert.Contains("b or ↵ shows the results", _m.Toast);
        _ctl.Apply(Cmd.Quit, 10);
        Assert.True(_m.QuitConfirmOpen);
        Assert.Contains("Quit Hoard?", Render().ToString());
        _ctl.Apply(Cmd.Escape, 10);
        Assert.False(_m.QuitConfirmOpen);
        Assert.True(_m.ScanScreen);
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        foreach (int w in new[] { 60, 80, 120 })
        {
            foreach (int h in new[] { 16, 20, 24, 40 })
            {
                MemoryCanvas c = Render(w, h);
                Assert.Contains("q quit", c.Row(h - 1));
                for (int y = 3; y < h - 2; y++)
                {
                    string row = c.Row(y).TrimEnd();
                    Assert.True(row.EndsWith('│') || row.EndsWith('┤'), $"{w}×{h} row {y} spills: {row}");
                }
            }
        }
    }
}
