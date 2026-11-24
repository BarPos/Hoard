using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class FileTypesTests
{
    [Theory]
    [InlineData("clip.MOV", TypeCategory.Video)]
    [InlineData("photo.heic", TypeCategory.Images)]
    [InlineData("backup.tar.gz", TypeCategory.Archives)]
    [InlineData("Xcode.xip", TypeCategory.DiskImages)]
    [InlineData("main.cs", TypeCategory.Code)]
    [InlineData("libfoo.dylib", TypeCategory.Apps)]
    [InlineData(".bashrc", TypeCategory.Other)]
    [InlineData("README", TypeCategory.Other)]
    [InlineData("trailing.", TypeCategory.Other)]
    [InlineData("libssl.so.3", TypeCategory.Apps)]
    [InlineData("libz.1.3.1.dylib", TypeCategory.Apps)]
    [InlineData("ls.1", TypeCategory.Other)]
    [InlineData("Inter.woff2", TypeCategory.Fonts)]
    [InlineData("2024.12", TypeCategory.Other)]
    public void Categories_come_from_the_extension(string name, TypeCategory expected) =>
        Assert.Equal(expected, FileTypes.CategoryOf(name));

    [Fact]
    public void Totals_match_the_scan_and_app_bundles_count_as_apps()
    {
        using var t = new TempTree();
        t.File("movies/a.mp4", 50_000);
        t.File("movies/b.mkv", 30_000);
        t.File("pics/x.png", 8_000);
        t.File("Tools.app/Contents/Resources/icon.png", 4_000); // inside a bundle: Apps, not Images
        t.File("Tools.app/Contents/MacOS/Tools", 6_000);
        t.File("notes.txt", 100);
        t.File("tool/bin/run", 2_000); // extensionless file in bin/: a program
        t.File("empty.log", 0);
        t.Dir("nothing-here");
        t.Dir("pics/also-empty");
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);

        TypeReport report = FileTypes.Analyze(r.Store);

        Assert.Equal(FileBytes(r), report.TotalBytes);
        Assert.Equal(TypeCategory.Video, report.Categories[0].Category);
        Assert.Equal(2, report.Categories[0].Files);
        TypeTotal apps = report.Categories.Single(c => c.Category == TypeCategory.Apps);
        Assert.Equal(3, apps.Files);
        Assert.Equal(1, report.Categories.Single(c => c.Category == TypeCategory.Images).Files);
        // The extension list still says what the files are, wherever they live.
        Assert.Equal(TypeCategory.Images, report.Extensions.Single(e => e.Extension == "png").Category);
        Assert.Equal(2, report.Extensions.Single(e => e.Extension == "png").Files);
        Assert.Equal(2, report.EmptyFolders);
        Assert.Equal(1, report.ZeroByteFiles);
        Assert.True(report.Categories.Zip(report.Categories.Skip(1)).All(p => p.First.Bytes >= p.Second.Bytes));
    }

    [Fact]
    public void Can_be_cancelled()
    {
        using var t = new TempTree();
        for (int i = 0; i < 50; i++)
        {
            t.File($"d{i}/f.txt", 10);
        }
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        // Small trees finish before the first check; the call must not throw anything else.
        try
        {
            FileTypes.Analyze(r.Store, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void Largest_files_of_a_category_or_extension_come_biggest_first()
    {
        using var t = new TempTree();
        t.File("a/one.mp4", 40_000);
        t.File("b/two.mov", 90_000);
        t.File("c/three.mp4", 60_000);
        t.File("notes.txt", 70_000);
        t.File("Player.app/Contents/intro.mp4", 80_000); // counts as Apps, but is still an .mp4
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        var s = r.Store;

        List<string> video = [.. FileTypes.LargestFiles(s, TypeCategory.Video, null).Select(s.RelativePath)];
        Assert.Equal(["b/two.mov", "c/three.mp4", "a/one.mp4"], video);

        List<string> mp4 = [.. FileTypes.LargestFiles(s, null, "mp4").Select(s.RelativePath)];
        Assert.Equal(["Player.app/Contents/intro.mp4", "c/three.mp4", "a/one.mp4"], mp4);

        Assert.Equal(["b/two.mov"], FileTypes.LargestFiles(s, TypeCategory.Video, null, limit: 1).Select(s.RelativePath));
        Assert.Empty(FileTypes.LargestFiles(s, TypeCategory.Fonts, null));
    }

    /// <summary>Sum of on-disk file sizes (folders' own entries aren't files of any type).</summary>
    private static long FileBytes(ScanResult r)
    {
        var s = r.Store;
        long files = 0;
        var stack = new Stack<int>([Hoard.Core.Model.NodeStore.Root]);
        while (stack.Count > 0)
        {
            foreach (int id in s.Children(stack.Pop()))
            {
                if (s.Kind(id) == Hoard.Core.Model.NodeKind.File)
                {
                    files += s.Size(id);
                }
                else
                {
                    stack.Push(id);
                }
            }
        }
        return files;
    }
}
