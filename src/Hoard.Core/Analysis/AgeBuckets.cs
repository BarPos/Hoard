using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

/// <summary>Which file time the age heatmap is based on (creation times aren't recorded).</summary>
public enum AgeAxis { Modified, Accessed }

/// <summary>One entry of the heatmap: bytes and files in each age bucket.</summary>
public sealed class AgeRow(int node, long[] bytes, long[] files)
{
    public int Node { get; } = node;
    public long[] Bytes { get; } = bytes;
    public long[] Files { get; } = files;
    public long Total => Bytes.Sum();
    /// <summary>Bytes not touched for over a year.</summary>
    public long Stale => Bytes[AgeBuckets.StaleFrom..].Sum();
}

/// <summary>The heatmap of one folder: its biggest entries, plus totals over everything in it.</summary>
public sealed class AgeReport(int folder, AgeAxis axis, long now, List<AgeRow> rows, AgeRow total, int moreEntries, long moreBytes)
{
    public int Folder { get; } = folder;
    public AgeAxis Axis { get; } = axis;
    /// <summary>The moment ages are measured from (Unix seconds).</summary>
    public long Now { get; } = now;
    public List<AgeRow> Rows { get; } = rows;
    /// <summary>Everything in the folder, including entries not shown as rows.</summary>
    public AgeRow Total { get; } = total;
    /// <summary>Entries left out because only the biggest are shown, and their size.</summary>
    public int MoreEntries { get; } = moreEntries;
    public long MoreBytes { get; } = moreBytes;
}

/// <summary>How old the bytes are (plan.md §6.6): eight buckets from "under a week" to "three years and more".</summary>
public static class AgeBuckets
{
    public const int Count = 8;
    /// <summary>First bucket that counts as stale (older than a year).</summary>
    public const int StaleFrom = 5;

    public static readonly string[] Labels = ["< 1w", "< 1m", "< 3m", "< 6m", "< 1y", "< 2y", "< 3y", "3y +"];

    /// <summary>Spoken form, for sentences: "modified 1–3 months ago".</summary>
    public static readonly string[] Phrases =
        ["in the last week", "1 week to 1 month ago", "1–3 months ago", "3–6 months ago", "6–12 months ago",
         "1–2 years ago", "2–3 years ago", "3 or more years ago"];

    private static readonly long[] UpperDays = [7, 30, 91, 182, 365, 730, 1095];

    public static int BucketOf(long ageSeconds)
    {
        long days = Math.Max(0, ageSeconds) / 86400;
        for (int i = 0; i < UpperDays.Length; i++)
        {
            if (days < UpperDays[i])
            {
                return i;
            }
        }
        return Count - 1;
    }

    private static long TimeOf(NodeStore s, int id, AgeAxis axis) => axis == AgeAxis.Accessed ? s.ATime(id) : s.MTime(id);

    /// <summary>Calls <paramref name="file"/> for every file at or below <paramref name="under"/>.</summary>
    private static void Files(NodeStore s, int under, CancellationToken cancel, Action<int> file)
    {
        if (s.Kind(under) == NodeKind.File)
        {
            file(under);
            return;
        }
        var stack = new Stack<int>();
        stack.Push(under);
        int n = 0;
        while (stack.Count > 0)
        {
            if ((++n & 4095) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }
            foreach (int id in s.Children(stack.Pop()))
            {
                switch (s.Kind(id))
                {
                    case NodeKind.File: file(id); break;
                    case NodeKind.Directory: stack.Push(id); break;
                }
            }
        }
    }

    /// <summary>Buckets the files under each of the <paramref name="rows"/> biggest entries of <paramref name="folder"/>.</summary>
    public static AgeReport Analyze(NodeStore s, int folder, AgeAxis axis, long now, int rows, CancellationToken cancel = default)
    {
        var entries = s.Children(folder).OrderByDescending(id => s.Size(id)).ThenBy(id => s.Name(id), StringComparer.Ordinal).ToList();
        var totalBytes = new long[Count];
        var totalFiles = new long[Count];
        var result = new List<AgeRow>();
        long moreBytes = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var bytes = new long[Count];
            var files = new long[Count];
            Files(s, entries[i], cancel, id =>
            {
                int b = BucketOf(now - TimeOf(s, id, axis));
                bytes[b] += s.Size(id);
                files[b]++;
            });
            for (int b = 0; b < Count; b++)
            {
                totalBytes[b] += bytes[b];
                totalFiles[b] += files[b];
            }
            if (i < rows)
            {
                result.Add(new AgeRow(entries[i], bytes, files));
            }
            else
            {
                moreBytes += bytes.Sum();
            }
        }
        return new AgeReport(folder, axis, now, result, new AgeRow(folder, totalBytes, totalFiles),
            Math.Max(0, entries.Count - rows), moreBytes);
    }

    /// <summary>The biggest files under <paramref name="under"/> in one age bucket, biggest first.</summary>
    public static List<int> LargestFiles(NodeStore s, int under, AgeAxis axis, long now, int bucket, int limit = 5000, CancellationToken cancel = default)
    {
        var heap = new PriorityQueue<int, long>();
        Files(s, under, cancel, id =>
        {
            if (BucketOf(now - TimeOf(s, id, axis)) != bucket)
            {
                return;
            }
            long size = s.Size(id);
            if (heap.Count < limit)
            {
                heap.Enqueue(id, size);
            }
            else if (heap.TryPeek(out _, out long smallest) && size > smallest)
            {
                heap.DequeueEnqueue(id, size);
            }
        });
        var list = new List<int>(heap.Count);
        while (heap.Count > 0)
        {
            list.Add(heap.Dequeue());
        }
        list.Sort((a, b) => s.Size(b) != s.Size(a) ? s.Size(b).CompareTo(s.Size(a)) : a.CompareTo(b));
        return list;
    }
}
