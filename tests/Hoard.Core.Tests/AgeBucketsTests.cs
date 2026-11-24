using Hoard.Core.Analysis;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class AgeBucketsTests
{
    private const long Day = 86400;

    [Theory]
    [InlineData(-5, 0)]      // clock skew: from the future counts as fresh
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 1)]
    [InlineData(29, 1)]
    [InlineData(30, 2)]
    [InlineData(200, 4)]
    [InlineData(364, 4)]
    [InlineData(365, 5)]
    [InlineData(800, 6)]
    [InlineData(1095, 7)]
    [InlineData(20000, 7)]
    public void Buckets_follow_the_plan(long days, int bucket) =>
        Assert.Equal(bucket, AgeBuckets.BucketOf(days * Day));

    [Fact]
    public void Rows_are_the_biggest_entries_with_bytes_per_bucket()
    {
        using var t = new TempTree();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Aged(string rel, int bytes, int days) =>
            File.SetLastWriteTimeUtc(t.File(rel, bytes), DateTimeOffset.FromUnixTimeSeconds(now - days * Day).UtcDateTime);
        Aged("photos/new.jpg", 50_000, 2);
        Aged("photos/old.jpg", 80_000, 1500);
        Aged("work/a.txt", 20_000, 40);
        Aged("tiny.txt", 100, 400);
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        var s = r.Store;

        AgeReport report = AgeBuckets.Analyze(s, Hoard.Core.Model.NodeStore.Root, AgeAxis.Modified, now, rows: 2);

        Assert.Equal(["photos", "work"], report.Rows.Select(row => s.Name(row.Node)));
        AgeRow photos = report.Rows[0];
        Assert.True(photos.Bytes[0] > 0 && photos.Bytes[7] > 0);
        Assert.Equal(photos.Bytes[7], photos.Stale);
        Assert.Equal(1, report.MoreEntries); // tiny.txt didn't get a row …
        Assert.True(report.Total.Bytes[5] > 0); // … but counts in the totals
        Assert.Equal(report.Rows.Sum(row => row.Total) + report.MoreBytes, report.Total.Total);

        List<int> old = AgeBuckets.LargestFiles(s, photos.Node, AgeAxis.Modified, now, 7);
        Assert.Equal(["photos/old.jpg"], old.Select(s.RelativePath));
    }
}
