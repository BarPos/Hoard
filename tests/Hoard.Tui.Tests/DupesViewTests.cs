using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class DupesViewTests : IDisposable
{
    /// <summary>Pretends the files whose path contains "clone" share all their blocks.</summary>
    private sealed class FakeClones : ISharedBlocks
    {
        public long? PrivateBytes(string path) => path.Contains("clone", StringComparison.Ordinal) ? 0 : null;
    }

    private readonly string _root = Directory.CreateTempSubdirectory("hoard-dupesview-").FullName;
    private readonly BrowserModel _m;
    private readonly BrowserController _ctl;

    public DupesViewTests()
    {
        byte[] movie = Bytes(300_000, 1), data = Bytes(200_000, 2);
        Write("a/movie.mov", movie, daysOld: 300);
        Write("b/movie.mov", movie, daysOld: 10);
        Write("c/movie-clone.mov", movie, daysOld: 5);
        Write("d/data.bin", data, daysOld: 50);
        Write("very/deep/path/data.bin", data, daysOld: 40);
        Write("unique.bin", Bytes(300_000, 3), daysOld: 1);
        ScanSession session = new Scanner(FileProbes.ForCurrentPlatform()).Start(_root);
        session.Completion.GetAwaiter().GetResult();
        _m = new BrowserModel(session, null, null, _ => null) { View = ViewKind.Dupes, DupMinSize = 100_000, SharedBlocks = new FakeClones() };
        _m.WaitForDupes();
        _ctl = new BrowserController(_m, new FakeShell());
    }

    private static byte[] Bytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private void Write(string rel, byte[] bytes, int daysOld)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-daysOld));
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

    private int Row(string fileRel) => _m.DupRows().FindIndex(r => r.File >= 0 && _m.Store.RelativePath(r.File) == fileRel);

    [Fact]
    public void Shows_groups_keepers_and_what_can_be_freed()
    {
        MemoryCanvas c = Render();
        string s = c.ToString();
        Assert.Contains("6 Dupes", c.Row(0));
        Assert.Contains("Duplicate files", c.Row(2));
        Assert.Contains("2 groups", c.Row(2));
        Assert.Contains("movie.mov", s);
        Assert.Contains("× 3 copies", s);
        Assert.Contains("★ keep", s);
        Assert.Contains("⧉ APFS clones", s);   // the clone frees nothing
        Assert.Contains("shares blocks", s);
        Assert.Contains("[oldest]", s);
        // movie: keep a/ (oldest); b/ frees 300 KB, the clone frees 0. data: 200 KB.
        Assert.InRange(_m.DupReclaimable(), 500_000, 530_000); // on-disk sizes round up to blocks
    }

    [Fact]
    public void Auto_select_ticks_all_but_the_kept_copy_and_the_rule_changes_it()
    {
        _ctl.Apply(BrowserController.FromChar('a'), 10);
        var marked = _m.Marks.Select(_m.Store.RelativePath).Order().ToList();
        Assert.Equal(["b/movie.mov", "c/movie-clone.mov", "very/deep/path/data.bin"], marked);
        _ctl.Apply(BrowserController.FromChar('K'), 10); // newest
        Assert.Equal(DupKeep.Newest, _m.DupKeep);
        _ctl.Apply(Cmd.MarkAll, 10);
        marked = [.. _m.Marks.Select(_m.Store.RelativePath).Order()];
        Assert.Equal(["a/movie.mov", "b/movie.mov", "d/data.bin"], marked);
        _ctl.Apply(Cmd.KeepFiles, 10); // shortest path
        Assert.Equal("d/data.bin", _m.Store.RelativePath(_m.DupKeeper(_m.Dupes!.Report.Groups[1])));
    }

    [Fact]
    public void The_last_copy_can_never_be_ticked()
    {
        _m.DupSelected = Row("d/data.bin");
        _ctl.Apply(Cmd.Mark, 10);
        _m.DupSelected = Row("very/deep/path/data.bin");
        _ctl.Apply(Cmd.Mark, 10);
        Assert.Single(_m.Marks);
        Assert.Contains("one copy always stays", _m.Toast);
    }

    [Fact]
    public void Delete_refuses_when_every_copy_is_marked_elsewhere()
    {
        foreach (int id in _m.Dupes!.Report.Groups[1].Files)
        {
            _m.ToggleMark(id); // e.g. marked in the browser
        }
        _ctl.Apply(Cmd.Delete, 10);
        Assert.Null(_m.Dialog);
        Assert.Contains("every copy of", _m.Toast);
    }

    [Fact]
    public void Groups_collapse_and_copies_open_in_the_browser()
    {
        int group = _m.DupRows().IndexOf((0, -1));
        _m.DupSelected = group;
        _ctl.Apply(Cmd.Open, 10); // toggles: collapse
        Assert.DoesNotContain(_m.DupRows(), r => r.Group == 0 && r.File >= 0);
        _ctl.Apply(Cmd.Right, 10);
        _m.DupSelected = Row("b/movie.mov");
        _ctl.Apply(Cmd.Open, 10);
        Assert.Equal(ViewKind.Browse, _m.View);
        Assert.Equal("movie.mov", _m.Store.Name(_m.SelectedNode!.Value));
    }

    [Fact]
    public void Draws_at_every_size_without_spilling_out()
    {
        foreach (int w in new[] { 60, 80, 120, 160 })
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
    public void Delete_compares_the_bytes_first()
    {
        _ctl.Apply(BrowserController.FromChar('a'), 10);
        _ctl.Apply(Cmd.Delete, 10);
        Assert.Contains("byte for byte", _m.Toast);
        Assert.True(_m.DupVerifying || _m.WaitForDupVerify() is not null);
    }

    [Fact]
    public void A_copy_that_changed_since_hashing_is_not_deleted()
    {
        _m.DupSelected = Row("b/movie.mov");
        _ctl.Apply(Cmd.Mark, 10);
        File.WriteAllBytes(Path.Combine(_root, "b/movie.mov"), Bytes(300_000, 99)); // changed after the search
        _ctl.Apply(Cmd.Delete, 10);
        var r = _m.WaitForDupVerify();
        Assert.NotNull(r);
        Assert.Single(r.Value.Different);
        Assert.Empty(r.Value.Ok);
    }

    [Fact]
    public void F_keeps_the_copies_in_a_folder()
    {
        _m.DupSelected = Row("c/movie-clone.mov");
        _ctl.Apply(Cmd.Filters, 10);
        Assert.Equal(DupKeep.InsideFolder, _m.DupKeep);
        Assert.Equal("c/movie-clone.mov", _m.Store.RelativePath(_m.DupKeeper(_m.Dupes!.Report.Groups[0])));
        Assert.Contains("[in c]", Render().ToString());
    }
}
