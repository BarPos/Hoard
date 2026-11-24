using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class BigOldTests
{
    private const long Day = 86400;

    [Fact]
    public void Finds_big_untouched_files_biggest_times_oldest_first()
    {
        using var t = new TempTree();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Aged(string rel, int bytes, int days) =>
            File.SetLastWriteTimeUtc(t.File(rel, bytes), DateTimeOffset.FromUnixTimeSeconds(now - days * Day).UtcDateTime);
        Aged("a/huge-recent.iso", 900_000, 10);     // too new
        Aged("b/big-old.zip", 300_000, 400);         // 300k × 400
        Aged("c/bigger-older.dmg", 500_000, 900);    // 500k × 900: first
        Aged("d/small-ancient.txt", 2_000, 3000);    // too small
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);

        BigOldReport report = BigOld.Find(r.Store, new BigOldOptions(100_000, 365, AgeAxis.Modified), now);

        Assert.Equal(["c/bigger-older.dmg", "b/big-old.zip"], report.Items.Select(r.Store.RelativePath));
        Assert.Equal(report.Items.Sum(id => r.Store.Size(id)), report.TotalBytes);
        Assert.Single(BigOld.Find(r.Store, new BigOldOptions(100_000, 730, AgeAxis.Modified), now).Items);
    }

    [Fact]
    public void Access_time_never_counts_as_older_than_the_last_change()
    {
        using var t = new TempTree();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string f = t.File("x.bin", 200_000);
        File.SetLastWriteTimeUtc(f, DateTimeOffset.FromUnixTimeSeconds(now - 10 * Day).UtcDateTime);
        File.SetLastAccessTimeUtc(f, DateTimeOffset.FromUnixTimeSeconds(now - 900 * Day).UtcDateTime);
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        // Changed 10 days ago, so not "unopened for a year" whatever the access time says.
        Assert.Empty(BigOld.Find(r.Store, new BigOldOptions(100_000, 365, AgeAxis.Accessed), now).Items);
    }
}
