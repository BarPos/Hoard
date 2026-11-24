using Hoard.Core.Actions;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

internal sealed class FolderTrash : ITrash, IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("hoard-fake-trash-").FullName;
    public string Name => "Trash";
    public bool IsAvailable => true;
    public string? MoveToTrash(string path)
    {
        string target = Path.Combine(Dir, Guid.NewGuid().ToString("N")[..6] + "-" + Path.GetFileName(path));
        if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
        return target;
    }
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

/// <summary>Each test gets its own small tree, since these tests really delete files.</summary>
public sealed class DeleteFlowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hoard-del-").FullName;
    private readonly FolderTrash _trash = new();
    private readonly BrowserModel _m;
    private readonly BrowserController _ctl;

    public DeleteFlowTests()
    {
        Write("big/one.bin", 300_000);
        Write("big/two.bin", 100_000);
        Write("mid/data.bin", 150_000);
        Write("small.txt", 5_000);
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        session.Completion.Wait();
        _m = new BrowserModel(session);
        _ctl = new BrowserController(_m, new FakeShell(), new Deleter(session.Store, session.RootPath, FileProbes.ForCurrentPlatform(), _trash));
    }

    private void Write(string rel, int bytes)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes].Select((_, i) => (byte)i).ToArray());
    }

    private int Row(string name) => _m.Rows.ToList().FindIndex(id => _m.Store.Name(id) == name);

    private MemoryCanvas Render(int w = 100, int h = 26)
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
    public void Space_marks_and_the_footer_shows_the_total()
    {
        _ctl.Apply(Cmd.Mark, 10);          // marks "big", moves to the next row
        Assert.Equal(1, _m.Selected);
        _ctl.Apply(Cmd.Mark, 10);          // marks "mid"
        Assert.Equal(2, _m.Marks.Count);
        MemoryCanvas c = Render();
        Assert.Contains("● 2 marked", c.Row(25));
        Assert.Contains("│  ●▸ big", c.ToString());        // cursor column, then the mark
        _ctl.Apply(Cmd.ClearOrUndo, 10);
        Assert.Empty(_m.Marks);
        Assert.Equal("marks cleared", _m.Toast);
    }

    [Fact]
    public void Nested_marks_count_once()
    {
        int big = _m.Rows[Row("big")];
        _m.ToggleMark(big);
        _m.ToggleMark(_m.Store.Children(big).First());
        var (count, size) = _m.MarkedSummary();
        Assert.Equal(1, count);
        Assert.Equal(_m.SizeOf(big), size);
    }

    [Fact]
    public void Delete_without_marks_uses_the_selection_and_esc_cancels()
    {
        _m.Select(Row("small.txt"));
        _ctl.Apply(Cmd.Delete, 10);
        DeleteDialog d = Assert.IsType<DeleteDialog>(_m.Dialog);
        Assert.Equal("small.txt", Path.GetFileName(d.Plan.Items.Single().Path));
        Assert.Equal(DeleteDialog.TrashChoice, d.Choice);      // the safe default
        MemoryCanvas c = Render();
        Assert.Contains("Move 1 item to the Trash?", c.ToString());
        Assert.Contains("► Move to Trash", c.ToString());
        _ctl.Apply(Cmd.Escape, 10);
        Assert.Null(_m.Dialog);
        Assert.Equal("nothing was deleted", _m.Toast);
        Assert.True(File.Exists(Path.Combine(_root, "small.txt")));
    }

    [Fact]
    public void Move_to_trash_then_undo()
    {
        long before = _m.Store.Size(NodeStore.Root);
        _m.Select(Row("big"));
        _ctl.Apply(Cmd.Mark, 10);
        _m.Select(Row("mid"));
        _ctl.Apply(Cmd.Mark, 10);
        _ctl.Apply(Cmd.Delete, 10);
        _ctl.Apply(Cmd.Open, 10);                              // ↵ on "Move to Trash"
        _ctl.WaitForDelete();
        Assert.Null(_m.Dialog);
        Assert.StartsWith("moved 2 items to the Trash", _m.Toast);
        Assert.Contains("u undoes", _m.Toast);
        Assert.Equal(["small.txt"], _m.Rows.Select(_m.Store.Name));
        Assert.Empty(_m.Marks);
        Assert.False(Directory.Exists(Path.Combine(_root, "big")));

        _ctl.Apply(Cmd.ClearOrUndo, 10);                       // no marks → undo
        Assert.Equal("restored 2 from the Trash", _m.Toast);
        Assert.True(File.Exists(Path.Combine(_root, "big", "one.bin")));
        Assert.Equal(before, _m.Store.Size(NodeStore.Root));
        Assert.Equal(3, _m.Rows.Count);
    }

    [Fact]
    public void Permanent_delete_needs_the_name_typed()
    {
        _m.Select(Row("mid"));
        _ctl.Apply(Cmd.Delete, 10);
        _ctl.Apply(Cmd.Right, 10);                             // focus "Delete permanently"
        Assert.Equal(DeleteDialog.PermanentChoice, _m.Dialog!.Choice);
        _ctl.Apply(Cmd.Open, 10);
        Assert.True(_ctl.WantsText);
        Assert.Contains("Type mid to confirm", Render().ToString());
        foreach (char ch in "mud") _ctl.TypeChar(ch);
        _ctl.Apply(Cmd.Open, 10);
        Assert.Equal("type mid exactly, or esc to go back", _m.Dialog!.Hint);
        Assert.True(Directory.Exists(Path.Combine(_root, "mid")));
        _ctl.Backspace(); _ctl.Backspace();
        foreach (char ch in "id") _ctl.TypeChar(ch);
        _ctl.Apply(Cmd.Open, 10);
        _ctl.WaitForDelete();
        Assert.StartsWith("deleted mid · freed", _m.Toast);
        Assert.False(Directory.Exists(Path.Combine(_root, "mid")));
        _ctl.Apply(Cmd.ClearOrUndo, 10);
        Assert.Equal("nothing to undo", _m.Toast);
    }

    [Fact]
    public void Deleting_the_open_folder_moves_back_up()
    {
        int big = _m.Rows[Row("big")];
        _m.ToggleMark(big);
        _m.Enter(big);
        _ctl.Apply(Cmd.Delete, 10);
        _ctl.Apply(Cmd.Open, 10);
        _ctl.WaitForDelete();
        Assert.Equal(NodeStore.Root, _m.Current);
    }

    [Fact]
    public void Dialog_fits_every_size()
    {
        foreach (string n in new[] { "big", "mid", "small.txt" }) _m.ToggleMark(_m.Rows[Row(n)]);
        _ctl.Apply(Cmd.Delete, 10);
        foreach (var (w, h) in new[] { (60, 16), (80, 24), (120, 40) })
        {
            MemoryCanvas c = Render(w, h);
            for (int y = 0; y < h; y++) Assert.Equal(w, Cells.Width(c.Row(y)));
            Assert.Contains("Total", c.ToString());
        }
    }

    [Fact]
    public void Quit_question_warns_about_marks()
    {
        _ctl.Apply(Cmd.Mark, 10);
        _ctl.Apply(Cmd.Quit, 10);
        string screen = Render().ToString();
        Assert.Contains("Quit Hoard?", screen);
        Assert.Contains("Your 1 mark will be lost.", screen);
        _ctl.Apply(Cmd.Open, 10);                      // ↵ quits
        Assert.True(_ctl.QuitRequested);
    }
}
