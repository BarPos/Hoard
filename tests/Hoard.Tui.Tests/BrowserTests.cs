using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

internal sealed class FakeShell : IShell
{
    public List<string> Revealed { get; } = [];
    public string Reveal(string path) { Revealed.Add(path); return "revealed"; }
    public string Copy(string text) => "copied " + text;
}

/// <summary>A small fixed tree: big/ (3 files), mid/, small.txt, an empty folder, a deep chain.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hoard-tui-" + Guid.NewGuid().ToString("N")[..8]);
    public static readonly long Now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    public Fixture()
    {
        Write("big/one.bin", 400_000, daysOld: 2);
        Write("big/two.bin", 300_000, daysOld: 200);
        Write("big/three.bin", 100_000, daysOld: 800);
        Write("mid/data.bin", 250_000, daysOld: 10);
        Write("small.txt", 5_000, daysOld: 1);
        Write("a/b/c/deep.txt", 1_000, daysOld: 1000);
        Write("名前のファイル_とても長い名前_テスト.txt", 2_000, daysOld: 3);
        Directory.CreateDirectory(Path.Combine(Root, "empty"));
        Directory.SetLastWriteTimeUtc(Path.Combine(Root, "empty"), DateTimeOffset.FromUnixTimeSeconds(Now - 400 * 86400L).UtcDateTime);
        for (int i = 0; i < 30; i++)
        {
            Write($"many/f{i:00}.bin", 1_000 + i * 100, daysOld: i);
        }
        Session = new Scanner(FileProbes.ForCurrentPlatform()).Start(Root);
        Result = Session.Completion.GetAwaiter().GetResult();
    }

    public ScanSession Session { get; }
    public ScanResult Result { get; }

    public BrowserModel Model(Theme? theme = null) => new(Session, theme, () => Now);

    private void Write(string rel, int bytes, int daysOld)
    {
        string p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes].Select((_, i) => (byte)(i * 31 + bytes)).ToArray());
        File.SetLastWriteTimeUtc(p, DateTimeOffset.FromUnixTimeSeconds(Now - daysOld * 86400L).UtcDateTime);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class BrowserTests(Fixture f) : IClassFixture<Fixture>
{
    private static MemoryCanvas Render(BrowserModel m, int w = 100, int h = 26)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    private static string RepoRoot()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "VERSION"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("VERSION file not found above the test folder");
    }

    private static int RowOf(MemoryCanvas c, string text) =>
        Enumerable.Range(0, c.Height).First(y => c.Row(y).Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Wide_layout_has_header_crumbs_list_details_and_footer()
    {
        MemoryCanvas c = Render(f.Model());
        string screen = c.ToString();
        Assert.Contains("◆ HOARD", c.Row(0));
        Assert.Contains("Browse", c.Row(0));
        Assert.Contains("⌂", c.Row(1));
        Assert.Contains("sort size ▼", c.Row(1));
        Assert.Contains("Details", c.Row(2));
        Assert.Contains("NAME", c.Row(3));
        Assert.Contains("q quit", c.Row(c.Height - 1));
        Assert.Contains("Size on disk", screen);
        // Biggest first, selection marker on the first row.
        Assert.True(RowOf(c, "big") < RowOf(c, "mid"));
        Assert.True(RowOf(c, "mid") < RowOf(c, "small.txt"));
        Assert.StartsWith("│ ►", c.Row(4));
    }

    [Fact]
    public void Every_row_is_exactly_the_screen_width()
    {
        foreach (var (w, h) in new[] { (100, 26), (140, 40), (80, 24), (60, 16) })
        {
            MemoryCanvas c = Render(f.Model(), w, h);
            for (int y = 0; y < h; y++)
            {
                Assert.Equal(w, Cells.Width(c.Row(y)));
            }
            Assert.Equal('╭', c.Row(2)[0]);
            Assert.Equal('╰', c.Row(h - 2)[0]);
        }
    }

    [Fact]
    public void Narrow_layout_drops_the_details_pane()
    {
        MemoryCanvas c = Render(f.Model(), 80, 24);
        Assert.DoesNotContain("Details", c.ToString());
        Assert.Contains("NAME", c.Row(3));
    }

    [Fact]
    public void Tiny_window_asks_for_more_room()
    {
        MemoryCanvas c = Render(f.Model(), 50, 12);
        Assert.Contains("make the window a bit bigger", c.ToString());
        Assert.Contains("50×12", c.ToString());
    }

    [Fact]
    public void Open_and_up_navigate_and_keep_the_selection()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        Assert.Equal("big", m.Store.Name(m.SelectedNode!.Value));
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal("big", m.Store.Name(m.Current));
        Assert.Equal(["one.bin", "two.bin", "three.bin"], m.Rows.Select(m.Store.Name));
        Assert.Contains("big", Render(m).Row(1));
        ctl.Apply(Cmd.Back, 10);
        Assert.Equal(NodeStore.Root, m.Current);
        Assert.Equal("big", m.Store.Name(m.SelectedNode!.Value));
        ctl.Apply(Cmd.Back, 10);
        Assert.Equal("already at the top of the scan", m.Toast);
        ctl.Apply(Cmd.Escape, 10);
        Assert.Equal("press q to quit", m.Toast);
        Assert.False(ctl.QuitRequested);
        ctl.Apply(Cmd.Quit, 10);
        Assert.False(ctl.QuitRequested);              // asks first
        Assert.True(m.QuitConfirmOpen);
        Assert.Contains("Quit Hoard?", Render(m).ToString());
        ctl.Apply(Cmd.Escape, 10);                    // esc stays
        Assert.False(m.QuitConfirmOpen);
        Assert.False(ctl.QuitRequested);
        ctl.Apply(Cmd.Quit, 10);
        Assert.True(ctl.AnswerQuit('n'));             // n stays
        Assert.False(ctl.QuitRequested);
        ctl.Apply(Cmd.Quit, 10);
        ctl.Apply(Cmd.Quit, 10);                      // q q quits
        Assert.True(ctl.QuitRequested);
    }

    [Fact]
    public void Opening_a_file_or_empty_folder_explains_why_not()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        m.Select(m.Rows.ToList().FindIndex(id => m.Store.Name(id) == "small.txt"));
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal("a file, not a folder", m.Toast);
        Assert.Contains("a file, not a folder", Render(m).ToString());
        m.Select(m.Rows.ToList().FindIndex(id => m.Store.Name(id) == "empty"));
        ctl.Apply(Cmd.Open, 10);
        Assert.Equal("this folder is empty", m.Toast);
    }

    [Fact]
    public void Sorting_cycles_and_reverses()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        ctl.Apply(Cmd.SortNext, 10);
        Assert.Equal(SortField.Name, m.Sort);
        var names = m.Rows.Select(m.Store.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
        Assert.Contains("sort name ▲", Render(m).Row(1));
        ctl.Apply(Cmd.SortReverse, 10);
        Assert.Equal(names.AsEnumerable().Reverse(), m.Rows.Select(m.Store.Name));
        ctl.Apply(Cmd.SortNext, 10);
        ctl.Apply(Cmd.SortNext, 10);
        Assert.Equal(SortField.Modified, m.Sort);
        Assert.Equal("many", m.Store.Name(m.Rows[0]));       // contains a file modified "now" (0 days)
        Assert.Equal("small.txt", m.Store.Name(m.Rows[1])); // 1 day old
    }

    [Fact]
    public void Size_mode_toggle_shows_in_crumbs()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        ctl.Apply(Cmd.ToggleSize, 10);
        Assert.True(m.Apparent);
        Assert.Contains("apparent", Render(m).Row(1));
    }

    [Fact]
    public void Mouse_click_selects_double_click_opens_and_crumbs_go_back()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        var layout = new Layout(100, 26);
        HitMap hits = Screens.Browser(new MemoryCanvas(100, 26), m);
        Assert.True(ctl.Click(10, layout.FirstRow + 1, false, layout, hits));
        Assert.Equal(1, m.Selected);
        ctl.Click(10, layout.FirstRow, true, layout, hits);
        Assert.Equal("big", m.Store.Name(m.Current));
        hits = Screens.Browser(new MemoryCanvas(100, 26), m);
        var rootCrumb = hits.Crumbs.First();
        Assert.Equal(NodeStore.Root, rootCrumb.Node);
        ctl.Click(rootCrumb.X0, 1, false, layout, hits);
        Assert.Equal(NodeStore.Root, m.Current);
        Assert.True(ctl.Wheel(3));
        Assert.Equal(3, m.Selected);
    }

    [Fact]
    public void Help_overlay_opens_and_any_key_closes_it()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        ctl.Apply(Cmd.Help, 10);
        Assert.Contains("any key closes", Render(m).ToString());
        Assert.Contains($"Hoard {Hoard.Core.AppInfo.Version} · keys", Render(m).ToString());
        Assert.Matches(@"^\d+\.\d+\.\d+", Hoard.Core.AppInfo.Version);
        Assert.Equal(File.ReadAllText(Path.Combine(RepoRoot(), "VERSION")).Trim(), Hoard.Core.AppInfo.Version);
        ctl.Apply(Cmd.Down, 10);
        Assert.False(m.HelpOpen);
        Assert.Equal(0, m.Selected); // the key only closed help
    }

    [Fact]
    public void Reveal_uses_the_full_path_and_theme_cycles()
    {
        BrowserModel m = f.Model();
        var shell = new FakeShell();
        var ctl = new BrowserController(m, shell);
        ctl.Apply(Cmd.Reveal, 10);
        Assert.Equal(Path.Combine(f.Result.RootPath, "big"), shell.Revealed.Single());
        ctl.Apply(Cmd.NextTheme, 10);
        Assert.Equal("neon", m.Theme.Name);
        Assert.Equal(Theme.Neon.Bar, Render(m).StyleAt(0, 0).Bg);
    }

    [Fact]
    public void Long_and_wide_names_are_truncated_to_fit()
    {
        MemoryCanvas c = Render(f.Model(), 80, 24);
        string row = c.Row(RowOf(c, "名前"));
        Assert.Contains("…", row);
        Assert.Equal(80, Cells.Width(row));
    }

    [Fact]
    public void Scroll_keeps_the_selection_visible()
    {
        BrowserModel m = f.Model();
        var ctl = new BrowserController(m, new FakeShell());
        m.Select(m.Rows.ToList().FindIndex(id => m.Store.Name(id) == "many"));
        ctl.Apply(Cmd.Open, 10);
        ctl.Apply(Cmd.Bottom, 10);
        MemoryCanvas c = Render(m, 70, 16); // 10 list rows for 30 entries
        Assert.Contains("►", string.Join("\n", Enumerable.Range(4, 10).Select(c.Row)));
        Assert.True(m.Scroll > 0);
    }

    [Fact]
    public void Finished_scan_shows_totals_not_a_spinner()
    {
        MemoryCanvas c = Render(f.Model());
        Assert.DoesNotContain("scanning", c.Row(0));
        Assert.Contains("files ·", c.Row(0));
    }

}

/// <summary>Holds every lookup inside one folder until released, so a scan stays "in progress".</summary>
internal sealed class GatedProbe(IFileProbe inner, string gatedDir) : IFileProbe
{
    public ManualResetEventSlim Gate { get; } = new(false);
    public string Name => inner.Name;
    public bool IsExact => inner.IsExact;

    public bool TryGetInfo(string path, out EntryInfo info, out int errno)
    {
        if (path.StartsWith(gatedDir + "/", StringComparison.Ordinal))
        {
            Gate.Wait(TimeSpan.FromSeconds(10));
        }
        return inner.TryGetInfo(path, out info, out errno);
    }
}

public class LiveBrowsingTests
{
    [Fact]
    public async Task Browser_works_while_a_folder_is_still_being_scanned()
    {
        string root = Path.Combine(Path.GetTempPath(), "hoard-live-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "slow"));
            Directory.CreateDirectory(Path.Combine(root, "quick"));
            File.WriteAllBytes(Path.Combine(root, "slow", "big.bin"), new byte[300_000]);
            File.WriteAllBytes(Path.Combine(root, "quick", "a.bin"), new byte[50_000]);
            var probe = new GatedProbe(FileProbes.ForCurrentPlatform(), Path.Combine(root, "slow"));
            ScanSession session = new Scanner(probe).Start(root);
            var m = new BrowserModel(session, now: () => Fixture.Now);
            var ctl = new BrowserController(m, new FakeShell());

            // Wait until the root and "quick" are read; "slow" stays pending behind the gate.
            SpinWait.SpinUntil(() =>
            {
                m.RefreshLive();
                int q = m.Rows.FirstOrDefault(id => m.Store.Name(id) == "quick", -1);
                return q >= 0 && !m.IsPending(q);
            }, TimeSpan.FromSeconds(10));

            Assert.True(m.Scanning);
            int slow = m.Rows.Single(id => m.Store.Name(id) == "slow");
            Assert.True(m.IsPending(slow));
            Assert.True(m.IsGrowing(NodeStore.Root));
            Assert.Equal(0, m.Selected);      // nobody moved: stays on the top row
            Assert.False(m.UserMoved);
            long before = m.Store.Size(NodeStore.Root);

            var c = new MemoryCanvas(100, 24);
            Screens.Browser(c, m);
            Assert.Contains("scanning", c.Row(0));
            Assert.Contains("q stops", c.Row(23));

            // Entering a pending folder is allowed and explains itself.
            m.Select(m.Rows.ToList().IndexOf(slow));
            ctl.Apply(Cmd.Open, 10);
            Assert.Equal(slow, m.Current);
            c = new MemoryCanvas(100, 24);
            Screens.Browser(c, m);
            Assert.Contains("Still scanning this folder", c.ToString());

            probe.Gate.Set();
            ScanResult r = await session.Completion;
            m.RefreshLive();
            Assert.False(m.Scanning);
            Assert.False(m.IsPending(slow));
            Assert.Equal("big.bin", m.Store.Name(m.Rows.Single()));
            Assert.True(r.Store.Size(NodeStore.Root) > before);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
