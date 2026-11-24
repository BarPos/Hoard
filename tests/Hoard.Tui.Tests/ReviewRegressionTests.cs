using Hoard.Core.Actions;
using Hoard.Core.Config;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;

namespace Hoard.Tui.Tests;

/// <summary>Regressions found in the 1.0.1 code review.</summary>
public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-review-").FullName;
    private readonly string _config = Path.Combine(Directory.CreateTempSubdirectory("hoard-review-cfg-").FullName, "config.toml");
    private readonly FolderTrash _trash = new();

    public void Dispose()
    {
        _trash.Dispose();
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(_config)!, true); } catch { }
    }

    private void Write(string rel, int bytes)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes].Select((_, i) => (byte)i).ToArray());
    }

    private (BrowserModel M, BrowserController C, Scanner S) Make(ViewKind view)
    {
        var scanner = new Scanner(FileProbes.ForCurrentPlatform());
        ScanSession session = scanner.Start(_root);
        session.Completion.Wait();
        var m = new BrowserModel(session, null, null, _ => null) { View = view, Rescanner = (p, c) => scanner.ScanSubtree(session.RootPath, p, c) };
        m.UseSettings(new Settings(), _config, apparent: false);
        var ctl = new BrowserController(m, new FakeShell(), new Deleter(session.Store, session.RootPath, FileProbes.ForCurrentPlatform(), _trash));
        return (m, ctl, scanner);
    }

    [Fact]
    public void Delete_on_the_overview_targets_the_highlighted_entry_not_the_hidden_list_selection()
    {
        Write("big/one.bin", 300_000);
        Write("small/two.bin", 5_000);
        var (m, ctl, _) = Make(ViewKind.Browse);
        int small = m.Store.Children(NodeStore.Root).First(id => m.Store.Name(id) == "small");
        m.SelectNode(small);
        m.View = ViewKind.Overview;
        m.OverviewSelected = m.TopEntries().FindIndex(id => m.Store.Name(id) == "big");

        ctl.Apply(Cmd.Delete, 10);

        DeleteDialog d = Assert.IsType<DeleteDialog>(m.Dialog);
        Assert.Equal("big", Path.GetFileName(d.Plan.Items.Single().Path));
    }

    [Fact]
    public void Delete_in_a_view_without_a_list_or_marks_does_not_pick_the_hidden_selection()
    {
        Write("a/x.bin", 1_000);
        var (m, ctl, _) = Make(ViewKind.Browse);
        m.Select(0);
        m.View = ViewKind.Dupes;
        ctl.Apply(Cmd.Delete, 10);
        Assert.Null(m.Dialog);
    }

    [Fact]
    public void Excluding_a_marked_entry_unmarks_it()
    {
        Write("cache/x.bin", 50_000);
        Write("keep/y.bin", 1_000);
        var (m, ctl, _) = Make(ViewKind.Browse);
        int cache = m.Store.Children(NodeStore.Root).First(id => m.Store.Name(id) == "cache");
        m.ToggleMark(cache);
        m.SelectNode(cache);
        ctl.Apply(BrowserController.FromChar('X'), 10);
        Assert.Equal(NodeKind.Excluded, m.Store.Kind(cache));
        Assert.DoesNotContain(cache, m.Marks);
    }

    [Fact]
    public void A_rescan_does_not_tick_a_safe_junk_category_again()
    {
        Write("web/package.json", 100);
        Write("web/node_modules/lib/index.js", 200_000);
        var (m, ctl, _) = Make(ViewKind.Junk);
        m.WaitForJunk();
        string rule = Assert.Single(m.JunkChecked);
        m.JunkChecked.Remove(rule); // the user unticks it

        int web = m.Store.Children(NodeStore.Root).First(id => m.Store.Name(id) == "web");
        m.Rescan(web);
        m.WaitForRescan();
        m.WaitForJunk();
        Assert.DoesNotContain(rule, m.JunkChecked);
    }
}
