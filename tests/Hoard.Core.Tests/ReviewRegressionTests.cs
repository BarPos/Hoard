using Hoard.Core.Actions;
using Hoard.Core.Analysis;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

/// <summary>Regressions found in the 1.0.1 code review.</summary>
public class ReviewRegressionTests
{
    private static readonly IFileProbe Probe = FileProbes.ForCurrentPlatform();

    private static int Find(NodeStore s, string rel) => Enumerable.Range(0, s.Count).First(id => !s.IsDeleted(id) && s.RelativePath(id) == rel);

    [Fact]
    public void Deleting_a_folder_that_holds_an_excluded_entry_is_refused()
    {
        using var t = new TempTree();
        t.File("proj/src/a.cs", 100);
        t.File("proj/build/out.bin", 50_000);
        ScanResult r = new Scanner(Probe, new ScanOptions { Excludes = ["build/"] }).Scan(t.Root);
        NodeStore s = r.Store;
        Assert.Equal(NodeKind.Excluded, s.Kind(Find(s, "proj/build")));
        using var trash = new FolderTrash();
        var d = new Deleter(s, r.RootPath, Probe, trash);

        DeletePlan plan = d.Plan([Find(s, "proj")]);

        Assert.Empty(plan.Items);
        Assert.Contains("excluded", plan.Refused.Single().Reason);
        Assert.Contains("proj/build", plan.Refused.Single().Reason);
        // what's around it can still go
        Assert.Single(d.Plan([Find(s, "proj/src")]).Items);
    }

    [Fact]
    public void Extra_names_of_a_hard_linked_file_are_not_zero_byte_files()
    {
        using var t = new TempTree();
        string original = t.File("a/data.bin", 5_000);
        t.Dir("b");
        TempTree.Run("ln", original, t.P("b/link.bin"));
        ScanResult r = new Scanner(Probe).Scan(t.Root);
        Assert.Equal(1, r.HardLinkDuplicates);

        Assert.Equal(0, EmptyFinder.Find(r.Store).ZeroFileCount);
        Assert.Equal(0, FileTypes.Analyze(r.Store).ZeroByteFiles);
    }

    [Fact]
    public void Rescanning_a_subfolder_keeps_root_anchored_patterns_and_parent_ignore_files()
    {
        using var t = new TempTree();
        t.File("proj/vendor/lib.bin", 40_000);  // "/proj/vendor": anchored to the scan root
        t.File("proj/src/x.tmp", 10_000);       // "*.tmp" from the root's .hoardignore
        t.File("proj/src/main.cs", 100);
        File.WriteAllText(t.P(".hoardignore"), "*.tmp\n");
        var scanner = new Scanner(Probe, new ScanOptions { Excludes = ["/proj/vendor"] });
        ScanResult full = scanner.Scan(t.Root);
        NodeStore fs = full.Store;
        Assert.Equal(NodeKind.Excluded, fs.Kind(Find(fs, "proj/vendor")));
        Assert.Equal(NodeKind.Excluded, fs.Kind(Find(fs, "proj/src/x.tmp")));

        ScanResult sub = scanner.ScanSubtree(full.RootPath, Path.Combine(full.RootPath, "proj"));
        NodeStore ss = sub.Store;
        int Child(string rel)
        {
            int id = NodeStore.Root;
            foreach (string part in rel.Split('/'))
            {
                id = ss.Children(id).Single(c => ss.Name(c) == part);
            }
            return id;
        }

        Assert.Equal(NodeKind.Excluded, ss.Kind(Child("vendor")));
        Assert.Equal(NodeKind.Excluded, ss.Kind(Child("src/x.tmp")));
        Assert.Equal(NodeKind.File, ss.Kind(Child("src/main.cs")));
    }
}
