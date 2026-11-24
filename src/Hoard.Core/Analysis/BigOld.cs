using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

/// <summary>What counts as big and old (plan.md §6.3).</summary>
public sealed record BigOldOptions(long MinSize, int MinAgeDays, AgeAxis Axis)
{
    public static readonly BigOldOptions Default = new(100_000_000, 365, AgeAxis.Modified);
}

public sealed class BigOldReport(BigOldOptions options, long now, List<int> items, long totalBytes, bool accessTimesLookUnused)
{
    public BigOldOptions Options { get; } = options;
    public long Now { get; } = now;
    /// <summary>Matching files, biggest × oldest first.</summary>
    public List<int> Items { get; } = items;
    public long TotalBytes { get; } = totalBytes;
    /// <summary>
    /// No file was opened after it was last modified, which usually means access times aren't
    /// being kept up to date (noatime-style mounts); "last opened" then means "last modified".
    /// </summary>
    public bool AccessTimesLookUnused { get; } = accessTimesLookUnused;
}

/// <summary>
/// Big files nobody has touched for a long time (plan.md §6.3). Installed software, app bundles and
/// <c>.git</c> are left out (see <see cref="EmptyFinder.IsSealed"/>): their files are old on purpose.
/// </summary>
public static class BigOld
{
    /// <summary>
    /// The time a file was last touched on the given axis. An access time earlier than the last
    /// modification means nothing (writing counts as touching), so "accessed" is the later of the two.
    /// </summary>
    public static long Touched(NodeStore s, int id, AgeAxis axis) =>
        axis == AgeAxis.Accessed ? Math.Max(s.ATime(id), s.MTime(id)) : s.MTime(id);

    /// <summary>Size × age: the bigger and the longer untouched, the higher.</summary>
    public static double Score(NodeStore s, int id, AgeAxis axis, long now) =>
        (double)s.Size(id) * Math.Max(0, now - Touched(s, id, axis));

    public static BigOldReport Find(NodeStore s, BigOldOptions o, long now, CancellationToken cancel = default)
    {
        var items = new List<int>();
        long total = 0, files = 0, openedLater = 0;
        long cutoff = now - o.MinAgeDays * 86400L;
        var stack = new Stack<int>();
        stack.Push(NodeStore.Root);
        int n = 0;
        while (stack.Count > 0)
        {
            if ((++n & 4095) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }
            foreach (int id in s.Children(stack.Pop()))
            {
                NodeKind kind = s.Kind(id);
                if (kind == NodeKind.Directory)
                {
                    // Installed software, app bundles and git's own files aren't yours to clear out.
                    if (!EmptyFinder.IsSealed(s.Name(id)))
                    {
                        stack.Push(id);
                    }
                    continue;
                }
                if (kind != NodeKind.File)
                {
                    continue;
                }
                files++;
                if (s.ATime(id) > s.MTime(id))
                {
                    openedLater++;
                }
                if (s.Size(id) >= o.MinSize && Touched(s, id, o.Axis) <= cutoff)
                {
                    items.Add(id);
                    total += s.Size(id);
                }
            }
        }
        items.Sort((a, b) =>
        {
            int c = Score(s, b, o.Axis, now).CompareTo(Score(s, a, o.Axis, now));
            return c != 0 ? c : a.CompareTo(b);
        });
        return new BigOldReport(o, now, items, total, files > 100 && openedLater == 0);
    }
}
