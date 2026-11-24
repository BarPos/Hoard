using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class EmptyFinderTests
{
    private static (ScanResult R, TempTree T) Tree()
    {
        var t = new TempTree();
        t.Dir("proj/tmp");                       // empty
        t.Dir("proj/old/a/b");                   // nested: old is empty as a whole
        t.File("proj/old/.DS_Store", 6_000);     // Finder's file doesn't count
        t.File("proj/keep/.gitkeep", 0);         // empty unless .gitkeep counts
        t.File("proj/src/main.py", 100);         // not empty
        t.File("proj/src/__init__.py", 0);
        t.File("logs/app.log", 0);
        t.File("logs/web.log", 0);
        t.File("logs/full.log", 500);
        t.Dir("repo/.git/refs/tags");            // git's own empty folders: left alone
        t.File("repo/.git/HEAD", 0);
        t.Dir("Tool.app/Contents/Resources");    // bundles too
        t.Dir("brew/Cellar/pkg/1.0/var");        // and installed software
        t.File("web/node_modules/x/index.d.ts", 0);
        return (new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root), t);
    }

    [Fact]
    public void Finds_outermost_empty_folders_and_counts_nested_ones()
    {
        var (r, t) = Tree();
        using (t)
        {
            var s = r.Store;
            EmptyReport e = EmptyFinder.Find(s);
            Assert.Equal(["proj/keep", "proj/old", "proj/tmp"], e.Folders.Select(s.RelativePath));
            Assert.Equal(5, e.TotalFolders); // keep, old, old/a, old/a/b, tmp

            EmptyReport strict = EmptyFinder.Find(s, ignoreKeepFiles: false);
            Assert.Equal(["proj/old", "proj/tmp"], strict.Folders.Select(s.RelativePath));
        }
    }

    [Fact]
    public void Groups_zero_byte_files_by_pattern_with_a_safety_hint()
    {
        var (r, t) = Tree();
        using (t)
        {
            var s = r.Store;
            EmptyReport e = EmptyFinder.Find(s);
            ZeroGroup logs = e.Zero.Single(g => g.Pattern == "*.log");
            Assert.Equal(ZeroSafety.Safe, logs.Safety);
            Assert.Equal(["logs/app.log", "logs/web.log"], logs.Files.Select(s.RelativePath));
            Assert.Equal("logs", logs.Where);
            Assert.Equal(ZeroSafety.Careful, e.Zero.Single(g => g.Pattern == "__init__.py").Safety);
            Assert.DoesNotContain(e.Zero, g => g.Files.Any(id => s.RelativePath(id).Contains(".git/") || s.RelativePath(id).Contains("node_modules")));
            Assert.Equal(logs, e.Zero[0]); // the biggest group first
        }
    }

    [Theory]
    [InlineData("server.log", "*.log")]
    [InlineData("libfoo.so.1", "*.so")]
    [InlineData("README", "no extension")]
    [InlineData("Icon\r", "Icon (folder icon)")]
    public void Patterns(string name, string pattern) => Assert.Equal(pattern, EmptyFinder.PatternOf(name));
}
