using Hoard.Core.Analysis;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class JunkViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-junkview-").FullName;
    private readonly FolderTrash _trash = new();
    private readonly BrowserModel _m;
    private readonly BrowserController _ctl;

    public JunkViewTests()
    {
        Write("web/package.json", 100);
        Write("web/node_modules/lib/index.js", 200_000);
        Write("web/src/app.js", 3_000);
        Write("api/package.json", 100);
        Write("api/node_modules/x.js", 100_000);
        Write("svc/__pycache__/m.pyc", 20_000);
        Write("svc/.venv/pyvenv.cfg", 50);
        Write("svc/.venv/lib/x.py", 60_000);
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        session.Completion.Wait();
        _m = new BrowserModel(session) { View = ViewKind.Junk };
        _ctl = new BrowserController(_m, new FakeShell(), new Hoard.Core.Actions.Deleter(session.Store, session.RootPath, FileProbes.ForCurrentPlatform(), _trash));
    }

    private void Write(string rel, int bytes)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes].Select((_, i) => (byte)i).ToArray());
    }

    private MemoryCanvas Render(int w = 110, int h = 26)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, _m);
        return c;
    }

    public void Dispose()
    {
        _trash.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Shows_categories_with_safe_ones_ticked()
    {
        MemoryCanvas c = Render();
        string s = c.ToString();
        Assert.Contains("Known junk", c.Row(2));
        Assert.Contains("[✓] ▸ node_modules", s);
        Assert.Contains("[ ] ▸ Python virtual environments", s);
        Assert.Contains(" SAFE ", s);
        Assert.Contains(" REVIEW ", s);
        Assert.Contains("selected", c.Row(26 - 4));
        Assert.Contains(" 7 Junk ", c.Row(0));
        for (int y = 0; y < 26; y++) Assert.Equal(110, Cells.Width(c.Row(y)));
    }

    [Fact]
    public void Expand_tick_and_clean_open_the_delete_dialog_with_the_ticked_items()
    {
        Assert.Equal("node_modules", _m.Junk!.Categories[0].Rule.Name);
        _ctl.Apply(Cmd.Right, 10);                                // expand node_modules
        Assert.Contains("web/node_modules", Render().ToString());
        _ctl.Apply(Cmd.Down, 10);                                 // first item (web, the bigger one)
        _ctl.Apply(Cmd.Mark, 10);                                 // leave it out
        var (items, _) = _m.JunkSelection();
        Assert.Contains(items, i => _m.Store.RelativePath(i) == "api/node_modules");
        Assert.DoesNotContain(items, i => _m.Store.RelativePath(i) == "web/node_modules");
        _ctl.Apply(Cmd.Clean, 10);
        DeleteDialog d = Assert.IsType<DeleteDialog>(_m.Dialog);
        Assert.Equal(["api/node_modules", "svc/__pycache__"], d.Plan.Items.Select(i => Path.GetRelativePath(_root, i.Path)).Order());
        _ctl.Apply(Cmd.Open, 10);                                 // move to (fake) Trash
        _ctl.WaitForDelete();
        Assert.False(Directory.Exists(Path.Combine(_root, "api", "node_modules")));
        Assert.True(Directory.Exists(Path.Combine(_root, "web", "node_modules")));
        Assert.DoesNotContain("api/node_modules", Render().ToString());
    }

    [Fact]
    public void All_safe_why_and_jump_to_item()
    {
        _m.JunkSelected = _m.JunkRows().FindIndex(r => _m.Junk!.Categories[r.Cat].Rule.Id == "python-venv");
        _ctl.Apply(Cmd.Why, 10);
        Assert.StartsWith("Python virtual environments: recreate", _m.Toast);
        _m.JunkChecked.Clear();
        _ctl.Apply(Cmd.MarkAll, 10);
        Assert.Contains("node-modules", _m.JunkChecked);
        Assert.DoesNotContain("python-venv", _m.JunkChecked);

        _ctl.Apply(Cmd.Top, 10);
        _ctl.Apply(Cmd.Right, 10);
        _ctl.Apply(Cmd.Down, 10);
        _ctl.Apply(Cmd.Open, 10);                                 // ↵ on an item jumps to it
        Assert.Equal(ViewKind.Browse, _m.View);
        Assert.Equal("node_modules", _m.Store.Name(_m.SelectedNode!.Value));
        Assert.Equal("web", _m.Store.Name(_m.Current));
    }
}
