using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class ExcludeTests
{
    [Fact]
    public void Hoardignore_files_apply_to_their_folder_and_below()
    {
        using var t = new TempTree();
        t.File("proj/build/out.bin", 50_000);
        t.File("proj/src/a.cs", 100);
        t.File("proj/src/gen/b.tmp", 100);
        t.File("other/build/keep.bin", 30_000); // outside the ignore file's folder
        File.WriteAllText(t.P("proj/.hoardignore"), "# comment\nbuild/\n*.tmp\n");
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        NodeStore s = r.Store;
        int Find(string rel) => Enumerable.Range(0, s.Count).First(id => s.RelativePath(id) == rel);

        Assert.Equal(NodeKind.Excluded, s.Kind(Find("proj/build")));
        Assert.Equal(NodeKind.Excluded, s.Kind(Find("proj/src/gen/b.tmp")));
        Assert.Equal(NodeKind.File, s.Kind(Find("proj/src/a.cs")));
        Assert.Equal(NodeKind.Directory, s.Kind(Find("other/build")));
        Assert.True(s.Size(Find("proj")) < 50_000);
    }

    [Fact]
    public void Excluding_an_entry_takes_its_size_off_every_parent()
    {
        using var t = new TempTree();
        t.File("a/big/x.bin", 100_000);
        t.File("a/small.bin", 1_000);
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        NodeStore s = r.Store;
        int a = s.Children(NodeStore.Root).First();
        int big = s.Children(a).First(id => s.Name(id) == "big");
        int x = s.Children(big).First();
        long rootBefore = s.Size(NodeStore.Root), bigSize = s.Size(big);
        int filesBefore = s.Files(NodeStore.Root);

        s.Exclude(big);

        Assert.Equal(NodeKind.Excluded, s.Kind(big));
        Assert.Equal(rootBefore - bigSize, s.Size(NodeStore.Root));
        Assert.Equal(filesBefore - 1, s.Files(NodeStore.Root));
        Assert.Empty(s.Children(big));
        Assert.True(s.IsDeleted(x));
    }
}
