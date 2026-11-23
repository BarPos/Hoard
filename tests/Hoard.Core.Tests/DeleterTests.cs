using Hoard.Core.Actions;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

/// <summary>A "trash" that is just a temp folder, so tests never touch the real Trash.</summary>
internal sealed class FolderTrash : ITrash, IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("hoard-fake-trash-").FullName;
    public bool Available { get; set; } = true;
    public string Name => "Trash";
    public bool IsAvailable => Available;

    public string? MoveToTrash(string path)
    {
        string target = Path.Combine(Dir, Guid.NewGuid().ToString("N")[..6] + "-" + Path.GetFileName(path));
        if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
        return target;
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class DeleterTests
{
    private static readonly IFileProbe Probe = FileProbes.ForCurrentPlatform();

    private static (ScanResult R, Deleter D, FolderTrash T) Setup(TempTree t)
    {
        ScanResult r = new Scanner(Probe).Scan(t.Root);
        var trash = new FolderTrash();
        return (r, new Deleter(r.Store, r.RootPath, Probe, trash), trash);
    }

    private static int Find(NodeStore s, string rel)
    {
        int id = NodeStore.Root;
        foreach (string part in rel.Split('/'))
        {
            id = s.Children(id).Single(c => s.Name(c) == part);
        }
        return id;
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/System", true)]
    [InlineData("/usr/local", true)]
    [InlineData("/Users", true)]
    [InlineData("/Users/you", true)]
    [InlineData("/Users/you/Library", true)]
    [InlineData("/Users/you/Downloads/old.iso", false)]
    [InlineData("/Applications/Some.app", false)]
    [InlineData("/opt/homebrew/Cellar/x", false)]
    public void Protected_paths(string path, bool isProtected) =>
        Assert.Equal(isProtected, Deleter.ProtectedReason(path, "/Users/you") is not null);

    [Fact]
    public void Plan_collapses_nested_marks_and_refuses_the_root()
    {
        using var t = new TempTree();
        t.File("a/inner/x.bin", 1000);
        t.File("b.bin", 2000);
        var (r, d, trash) = Setup(t);
        using var _ = trash;
        NodeStore s = r.Store;
        DeletePlan plan = d.Plan([Find(s, "a"), Find(s, "a/inner"), Find(s, "a/inner/x.bin"), Find(s, "b.bin"), NodeStore.Root]);
        Assert.Equal(["a", "b.bin"], plan.Items.Select(i => Path.GetFileName(i.Path)));
        Assert.Single(plan.Refused);
        Assert.Equal(2, plan.TotalFiles);
    }

    [Fact]
    public void Permanent_delete_removes_files_and_updates_totals()
    {
        using var t = new TempTree();
        t.File("keep/k.bin", 30_000);
        t.File("junk/j1.bin", 50_000);
        t.File("junk/sub/j2.bin", 20_000);
        var (r, d, trash) = Setup(t);
        using var _ = trash;
        NodeStore s = r.Store;
        long before = s.Size(NodeStore.Root);
        int junk = Find(s, "junk");
        long junkSize = s.Size(junk);

        DeleteReport rep = d.Execute(d.Plan([junk]), permanent: true);
        Assert.True(rep.Outcomes.Single().Ok);
        Assert.False(Directory.Exists(t.P("junk")));
        Assert.Equal(before - junkSize, s.Size(NodeStore.Root));
        Assert.Equal(1, s.Files(NodeStore.Root));
        Assert.Equal(["keep"], s.Children(NodeStore.Root).Select(s.Name));
        Assert.Contains("can't be undone", d.Undo(rep).Problems.Single());
    }

    [Fact]
    public void Trash_then_undo_puts_everything_back()
    {
        using var t = new TempTree();
        t.File("old/a.bin", 40_000);
        t.File("tmp.log", 3_000);
        var (r, d, trash) = Setup(t);
        using var _ = trash;
        NodeStore s = r.Store;
        long before = s.Size(NodeStore.Root);
        int files = s.Files(NodeStore.Root);

        DeleteReport rep = d.Execute(d.Plan([Find(s, "old"), Find(s, "tmp.log")]), permanent: false);
        Assert.All(rep.Outcomes, o => Assert.True(o.Ok));
        Assert.False(Path.Exists(t.P("old")));
        Assert.True(Directory.EnumerateFileSystemEntries(trash.Dir).Count() == 2);
        Assert.Empty(s.Children(NodeStore.Root));

        var (restored, problems) = d.Undo(rep);
        Assert.Equal(2, restored);
        Assert.Empty(problems);
        Assert.True(File.Exists(t.P("old/a.bin")));
        Assert.Equal(before, s.Size(NodeStore.Root));
        Assert.Equal(files, s.Files(NodeStore.Root));
    }

    [Fact]
    public void Items_that_changed_since_the_scan_are_skipped()
    {
        using var t = new TempTree();
        string changed = t.File("changed.bin", 1000);
        string gone = t.File("gone.bin", 1000);
        t.File("fine.bin", 1000);
        var (r, d, trash) = Setup(t);
        using var _ = trash;
        NodeStore s = r.Store;
        File.AppendAllText(changed, "more data");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(5));
        File.Delete(gone);

        DeleteReport rep = d.Execute(d.Plan([Find(s, "changed.bin"), Find(s, "gone.bin"), Find(s, "fine.bin")]), permanent: true);
        var byName = rep.Outcomes.ToDictionary(o => Path.GetFileName(o.Path));
        Assert.Equal("it was modified since the scan", byName["changed.bin"].Error);
        Assert.Equal("it no longer exists", byName["gone.bin"].Error);
        Assert.True(byName["fine.bin"].Ok);
        Assert.True(File.Exists(changed));
    }

    [Fact]
    public void Missing_trash_fails_safely()
    {
        using var t = new TempTree();
        t.File("a.bin", 1000);
        var (r, d, trash) = Setup(t);
        using var _ = trash;
        trash.Available = false;
        DeleteReport rep = d.Execute(d.Plan([Find(r.Store, "a.bin")]), permanent: false);
        Assert.False(rep.Outcomes.Single().Ok);
        Assert.True(File.Exists(t.P("a.bin")));
        Assert.Contains("isn't available", rep.Outcomes.Single().Error);
    }
}
