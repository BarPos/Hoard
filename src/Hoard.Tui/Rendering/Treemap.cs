namespace Hoard.Tui.Rendering;

/// <summary>A rectangle in terminal cells: [X, X+W) × [Y, Y+H).</summary>
public readonly record struct Cell(int X, int Y, int W, int H)
{
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
    public double CenterX => X + W / 2.0;
    public double CenterY => Y + H / 2.0;
}

/// <summary>One block of the treemap. <see cref="Node"/> is -1 for a "+N smaller" group.</summary>
public sealed record MapBlock(int Node, long Size, Cell Rect, int Depth, int GroupCount, IReadOnlyList<MapBlock> Children)
{
    public bool IsGroup => Node < 0;
}

/// <summary>
/// Squarified treemap (Bruls, Huizing &amp; van Wijk, 2000) computed in floating point and snapped
/// to terminal cells so neighbouring blocks share edges exactly (plan.md §7.3).
/// </summary>
public static class Treemap
{
    /// <summary>Smallest block worth drawing, in cells; smaller items are merged into one "+N" block.</summary>
    public const double MinCells = 6;
    /// <summary>At most this many blocks per level, so huge folders stay readable and fast.</summary>
    public const int MaxBlocks = 60;

    /// <param name="children">Children of the folder being laid out.</param>
    /// <param name="sizeOf">Size used for areas.</param>
    /// <param name="childrenOf">Children of a child (for deeper levels); empty for files.</param>
    /// <param name="depth">Levels to lay out: 1 = just the children, 2 = also their children, …</param>
    public static List<MapBlock> Layout(IEnumerable<int> children, Func<int, long> sizeOf, Func<int, IEnumerable<int>> childrenOf, Cell area, int depth)
    {
        var items = children.Select(id => (Id: id, Size: sizeOf(id))).Where(i => i.Size > 0)
            .OrderByDescending(i => i.Size).ThenBy(i => i.Id).ToList();
        var blocks = new List<MapBlock>();
        if (items.Count == 0 || area.W <= 0 || area.H <= 0)
        {
            return blocks;
        }

        // Merge the tail into one group block when items would be too small to see.
        double total = items.Sum(i => (double)i.Size);
        double cellsPerByte = area.W * (double)area.H / total;
        int keep = 0;
        while (keep < items.Count && keep < MaxBlocks && items[keep].Size * cellsPerByte >= MinCells)
        {
            keep++;
        }
        var entries = items.Take(keep).Select(i => (i.Id, i.Size, Count: 1)).ToList();
        if (keep < items.Count)
        {
            long rest = items.Skip(keep).Sum(i => i.Size);
            entries.Add((-1, rest, items.Count - keep));
        }

        foreach (var (rect, entry) in Squarify(entries.Select(e => (double)e.Size).ToList(), area).Zip(entries))
        {
            IReadOnlyList<MapBlock> inner = [];
            if (depth > 1 && entry.Id >= 0 && rect.W >= 8 && rect.H >= 4)
            {
                // Leave the top row for a header and one cell of gap at the right and bottom.
                var innerArea = new Cell(rect.X, rect.Y + 1, rect.W - 1, rect.H - 2);
                inner = Layout(childrenOf(entry.Id), sizeOf, childrenOf, innerArea, depth - 1);
            }
            blocks.Add(new MapBlock(entry.Id, entry.Size, rect, 0, entry.Count, inner));
        }
        return Deepen(blocks, 0);
    }

    private static List<MapBlock> Deepen(List<MapBlock> blocks, int depth) =>
        [.. blocks.Select(b => b with { Depth = depth, Children = Deepen([.. b.Children], depth + 1) })];

    /// <summary>Squarified layout of <paramref name="sizes"/> (descending) into integer cells.</summary>
    public static List<Cell> Squarify(IReadOnlyList<double> sizes, Cell area)
    {
        var result = new List<Cell>(sizes.Count);
        double total = sizes.Sum();
        if (total <= 0)
        {
            return [.. sizes.Select(_ => new Cell(area.X, area.Y, 0, 0))];
        }
        double scale = area.W * (double)area.H / total;
        double x = area.X, y = area.Y, w = area.W, h = area.H;
        int i = 0;
        var rects = new List<(double X, double Y, double W, double H)>(sizes.Count);
        while (i < sizes.Count)
        {
            double side = Math.Min(w, h);
            int end = i;
            double best = double.MaxValue, rowSum = 0;
            while (end < sizes.Count)
            {
                double s = rowSum + sizes[end] * scale;
                double worst = Worst(sizes, i, end, s, side, scale);
                if (worst > best)
                {
                    break;
                }
                best = worst;
                rowSum = s;
                end++;
            }
            double thick = side > 0 ? rowSum / side : 0;
            double offset = 0;
            for (int k = i; k < end; k++)
            {
                double len = thick > 0 ? sizes[k] * scale / thick : 0;
                rects.Add(w >= h ? (x, y + offset, thick, len) : (x + offset, y, len, thick));
                offset += len;
            }
            if (w >= h)
            {
                x += thick;
                w -= thick;
            }
            else
            {
                y += thick;
                h -= thick;
            }
            i = end;
        }
        // Snap edges (not sizes) to cells so adjacent blocks never overlap or leave gaps.
        foreach (var r in rects)
        {
            int x0 = (int)Math.Round(r.X), y0 = (int)Math.Round(r.Y);
            int x1 = (int)Math.Round(r.X + r.W), y1 = (int)Math.Round(r.Y + r.H);
            result.Add(new Cell(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)));
        }
        return result;
    }

    private static double Worst(IReadOnlyList<double> sizes, int from, int to, double sum, double side, double scale)
    {
        double max = 0, min = double.MaxValue;
        for (int k = from; k <= to; k++)
        {
            double a = sizes[k] * scale;
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }
        double s2 = sum * sum, side2 = side * side;
        return Math.Max(side2 * max / s2, s2 / (side2 * min));
    }

    /// <summary>
    /// Picks the block to move to from <paramref name="from"/> in direction (dx, dy): the nearest
    /// block whose centre lies that way, preferring ones that overlap on the other axis.
    /// </summary>
    public static int? Neighbour(IReadOnlyList<MapBlock> blocks, int from, int dx, int dy)
    {
        if (from < 0 || from >= blocks.Count)
        {
            return blocks.Count > 0 ? 0 : null;
        }
        Cell a = blocks[from].Rect;
        int? best = null;
        double bestScore = double.MaxValue;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i == from)
            {
                continue;
            }
            Cell b = blocks[i].Rect;
            double along = dx != 0 ? (b.CenterX - a.CenterX) * dx : (b.CenterY - a.CenterY) * dy;
            if (along <= 0)
            {
                continue;
            }
            bool overlaps = dx != 0
                ? b.Y < a.Y + a.H && a.Y < b.Y + b.H
                : b.X < a.X + a.W && a.X < b.X + b.W;
            double across = dx != 0 ? Math.Abs(b.CenterY - a.CenterY) : Math.Abs(b.CenterX - a.CenterX);
            double score = along + across * 2 + (overlaps ? 0 : 1000);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }
}
