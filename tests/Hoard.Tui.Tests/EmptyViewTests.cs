using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class EmptyViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-emptyview-").FullName;
    private readonly BrowserModel _m;
    private readonly BrowserController _ctl;

    public EmptyViewTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "proj/tmp"));
        Directory.CreateDirectory(Path.Combine(_root, "proj/old/a/b"));
        Directory.CreateDirectory(Path.Combine(_root, "loose"));
        Write("proj/src/main.py", 100);
        Write("proj/src/__init__.py", 0);
        Write("logs/app.log", 0);
        Write("logs/web.log", 0);
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        session.Completion.GetAwaiter().GetResult();
        _m = new BrowserModel(session, null, null, _ => null) { View = ViewKind.Empty };
        _m.WaitForEmpty();
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
        try { Directory.Delete(_root, true); } catch { }
    }

    private MemoryCanvas Render(int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, _m);
        return c;
    }

    [Fact]
    public void Shows_folder_groups_and_zero_byte_groups()
    {
        MemoryCanvas c = Render();
        string s = c.ToString();
        Assert.Contains("9 Empty", c.Row(0));
        Assert.Contains("5 empty folders · 3 zero-byte files", c.Row(2));
        Assert.Contains("Empty folders · 5", s);
        Assert.Contains("proj/", s);
        Assert.Contains("at the top of", s); // loose/ sits directly in the scanned folder
        Assert.Contains("Zero-byte files · 3", s);
        Assert.Contains("*.log", s);
        Assert.Contains("SAFE", s);
        Assert.Contains("CAREFUL", s);
        Assert.Contains("Selected", s);
    }

    [Fact]
    public void Groups_expand_and_marks_cover_the_whole_group()
    {
        List<EmptyRow> rows = _m.EmptyRows();
        int proj = rows.FindIndex(r => r.Kind == EmptyRowKind.FolderGroup && _m.Store.Name(r.Group) == "proj");
        _m.EmptySelected = proj;
        _ctl.Apply(Cmd.Right, 10);
        Assert.Contains("old/", Render().ToString());
        Assert.Contains("+ 2 inside", Render().ToString());
        _m.EmptySelected = proj;
        _ctl.Apply(Cmd.Mark, 10);
        Assert.Equal(["proj/old", "proj/tmp"], _m.Marks.Select(_m.Store.RelativePath).Order());
        _m.EmptySelected = proj;
        _ctl.Apply(Cmd.Mark, 10); // again: unmarks
        Assert.Empty(_m.Marks);
        _m.EmptySelected = proj + 1;
        _ctl.Apply(Cmd.Left, 10);  // collapses and goes back to the group
        Assert.Equal(proj, _m.EmptySelected);
        Assert.DoesNotContain("+ 2 inside", Render().ToString());
    }

    [Fact]
    public void A_marks_every_empty_folder_and_only_safe_files()
    {
        _ctl.Apply(Cmd.MarkAll, 10);
        var marked = _m.Marks.Select(_m.Store.RelativePath).Order().ToList();
        Assert.Equal(["logs/app.log", "logs/web.log", "loose", "proj/old", "proj/tmp"], marked);
    }

    [Fact]
    public void K_switches_whether_keep_files_count()
    {
        Assert.True(_m.EmptyIgnoreKeep);
        _ctl.Apply(BrowserController.FromChar('K'), 10);
        Assert.False(_m.EmptyIgnoreKeep);
        _m.WaitForEmpty();
        Assert.False(_m.Empty!.IgnoredKeepFiles);
        Assert.Contains("count as content", Render().Row(3));
    }

    [Fact]
    public void Enter_on_a_folder_shows_it_in_the_browser()
    {
        _ctl.Apply(Cmd.Open, 10); // expand the first group
        _ctl.Apply(Cmd.Down, 10);
        EmptyRow row = _m.EmptyRows()[_m.EmptySelected];
        Assert.Equal(EmptyRowKind.Folder, row.Kind);
        _ctl.Apply(Cmd.Open, 10);
        Assert.Equal(ViewKind.Browse, _m.View);
        Assert.Equal(row.Item, _m.SelectedNode);
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        _m.EmptyExpanded.Add("z:*.log");
        foreach (int w in new[] { 60, 80, 109, 110, 150 })
        {
            foreach (int h in new[] { 16, 20, 30 })
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

    [Fact]
    public void Help_fits_any_window()
    {
        _m.HelpOpen = true;
        foreach (var (w, h) in new[] { (60, 16), (80, 24), (120, 30), (160, 50) })
        {
            MemoryCanvas c = Render(w, h);
            string s = c.ToString();
            Assert.Contains("any key closes", s);
            Assert.Contains("q quit", c.Row(h - 1)); // the footer is still there underneath
            if (h >= 30)
            {
                Assert.Contains("mouse", s); // everything fits
            }
        }
    }
}
