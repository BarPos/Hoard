using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class GraftTests
{
    [Fact]
    public void A_rescanned_folder_replaces_the_old_one_and_totals_follow()
    {
        using var t = new TempTree();
        t.File("keep/a.bin", 10_000);
        t.File("work/old.bin", 50_000);
        t.File("work/sub/x.bin", 5_000);
        var scanner = new Scanner(FileProbes.ForCurrentPlatform());
        ScanResult r = scanner.Scan(t.Root);
        NodeStore s = r.Store;
        int work = s.Children(NodeStore.Root).First(id => s.Name(id) == "work");
        int oldFile = s.Children(work).First(id => s.Name(id) == "old.bin");

        File.Delete(t.P("work/old.bin"));
        t.File("work/new.bin", 200_000);
        t.File("work/sub/deeper/y.bin", 7_000);
        ScanResult fresh = scanner.Scan(t.P("work"));
        s.Graft(work, fresh.Store);

        Assert.Equal(fresh.Store.Size(NodeStore.Root), s.Size(work));
        Assert.Equal(scanner.Scan(t.Root).Store.Size(NodeStore.Root), s.Size(NodeStore.Root));
        Assert.Equal(["new.bin", "sub"], s.Children(work).Select(s.Name).Order());
        Assert.True(s.IsDeleted(oldFile));
        int sub = s.Children(work).First(id => s.Name(id) == "sub");
        Assert.Equal(["deeper", "x.bin"], s.Children(sub).Select(s.Name).Order());
        Assert.Equal("work/sub/deeper", s.RelativePath(s.Children(sub).First(id => s.Name(id) == "deeper")));
        Assert.Equal(3, s.Files(work));
    }
}
