using System.IO.Hashing;
using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

/// <summary>Files with identical contents. <see cref="Files"/> are sorted by path.</summary>
public sealed class DupGroup(long size, string hash, List<int> files)
{
    /// <summary>Length of each copy in bytes.</summary>
    public long Size { get; } = size;
    /// <summary>Full-content XxHash128, as hex.</summary>
    public string Hash { get; } = hash;
    public List<int> Files { get; } = files;
}

public sealed class DupReport(List<DupGroup> groups, long candidates, long minSize)
{
    /// <summary>Groups, most wasted space first.</summary>
    public List<DupGroup> Groups { get; } = groups;
    /// <summary>Files that had a same-size partner and were looked at.</summary>
    public long Candidates { get; } = candidates;
    public long MinSize { get; } = minSize;
}

/// <summary>Live counters for a running duplicate search, safe to read from another thread.</summary>
public sealed class DupProgress
{
    private long _files, _bytes, _filesTotal, _bytesTotal;
    private int _stage;
    /// <summary>0 grouping by size, 1 reading the start of each file, 2 reading whole files.</summary>
    public int Stage => Volatile.Read(ref _stage);
    public long FilesDone => Interlocked.Read(ref _files);
    public long FilesTotal => Interlocked.Read(ref _filesTotal);
    public long BytesDone => Interlocked.Read(ref _bytes);
    public long BytesTotal => Interlocked.Read(ref _bytesTotal);

    internal void Begin(int stage, long files, long bytes)
    {
        Interlocked.Exchange(ref _files, 0);
        Interlocked.Exchange(ref _bytes, 0);
        Interlocked.Exchange(ref _filesTotal, files);
        Interlocked.Exchange(ref _bytesTotal, bytes);
        Volatile.Write(ref _stage, stage);
    }

    internal void Done(long bytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
    }
}

/// <summary>
/// Finds files with the same contents (plan.md §6.2): group by size, then by a hash of the first
/// 64 KB, then by a hash of the whole file, so most files are never read in full. Hard-link
/// extras (the same file under another name) are skipped: they take no extra space. So are the
/// insides of installed software, app bundles and <c>.git</c> (see <see cref="EmptyFinder.IsSealed"/>).
/// </summary>
public static class DuplicateFinder
{
    public const long DefaultMinSize = 1_000_000;
    private const int HeadBytes = 64 * 1024;

    /// <param name="pathOf">Full path of a node (the store only knows paths relative to the scan root).</param>
    /// <param name="parallel">Files read at once; about two per disk keeps a disk busy without thrashing it.</param>
    public static DupReport Find(NodeStore s, Func<int, string> pathOf, long minSize = DefaultMinSize, DupProgress? progress = null,
        int parallel = 2, CancellationToken cancel = default, HashCache? cache = null)
    {
        progress ??= new DupProgress();
        progress.Begin(0, 0, 0);

        // 1. Same length (apparent size: the contents, whatever the blocks).
        var bySize = new Dictionary<long, List<int>>();
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
                    // Copies inside installed software, app bundles and git's own files belong
                    // there: removing one breaks what they're part of.
                    if (!EmptyFinder.IsSealed(s.Name(id)))
                    {
                        stack.Push(id);
                    }
                }
                else if (kind == NodeKind.File && s.Apparent(id) >= Math.Max(1, minSize)
                         && (s.Flags(id) & (NodeFlags.HardLinkDuplicate | NodeFlags.Error)) == 0)
                {
                    long len = s.Apparent(id);
                    if (!bySize.TryGetValue(len, out var list))
                    {
                        bySize[len] = list = [];
                    }
                    list.Add(id);
                }
            }
        }
        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        long candidateCount = candidates.Sum(l => l.Count);

        // 2. Same start.
        var heads = Regroup(candidates, s, pathOf, full: false, progress, stage: 1, parallel, cancel, cache);
        // 3. Same contents.
        var full = Regroup(heads.Select(h => (IReadOnlyList<int>)h.Files), s, pathOf, full: true, progress, stage: 2, parallel, cancel, cache);
        cache?.Save();

        var groups = full.Select(g => new DupGroup(s.Apparent(g.Files[0]), g.Hash,
                [.. g.Files.OrderBy(s.RelativePath, StringComparer.Ordinal)]))
            .OrderByDescending(g => g.Size * (g.Files.Count - 1))
            .ThenBy(g => s.RelativePath(g.Files[0]), StringComparer.Ordinal)
            .ToList();
        return new DupReport(groups, candidateCount, minSize);
    }

    private sealed record Hashed(string Hash, List<int> Files);

    /// <summary>Splits each group by a hash of the start (or all) of each file; keeps the parts with 2+ files.</summary>
    private static List<Hashed> Regroup(IEnumerable<IReadOnlyList<int>> groups, NodeStore s, Func<int, string> pathOf, bool full,
        DupProgress progress, int stage, int parallel, CancellationToken cancel, HashCache? cache)
    {
        var input = groups.Where(g => g.Count > 1).ToList();
        long Bytes(int id) => full ? s.Apparent(id) : Math.Min(HeadBytes, s.Apparent(id));
        var all = input.SelectMany(g => g).ToList();
        progress.Begin(stage, all.Count, all.Sum(Bytes));
        var hashes = new string?[all.Count];
        var index = new Dictionary<int, int>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            index[all[i]] = i;
        }
        Parallel.For(0, all.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = cancel }, i =>
        {
            int id = all[i];
            string path = pathOf(id);
            if (cache?.Get(path, s.Apparent(id), s.MTime(id), full) is { } known)
            {
                hashes[i] = known; // unchanged since it was last read: no need to read it again
            }
            else
            {
                hashes[i] = HashFile(path, full ? long.MaxValue : HeadBytes, cancel);
                if (hashes[i] is { } h)
                {
                    cache?.Set(path, s.Apparent(id), s.MTime(id), full, h);
                }
            }
            progress.Done(Bytes(id));
        });
        var result = new List<Hashed>();
        foreach (IReadOnlyList<int> g in input)
        {
            foreach (var part in g.Where(id => hashes[index[id]] is not null).GroupBy(id => hashes[index[id]]!))
            {
                var files = part.ToList();
                if (files.Count > 1)
                {
                    result.Add(new Hashed(part.Key, files));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// True when two files have exactly the same bytes: the last check before deleting a copy
    /// (plan.md §6.2), so a hash collision can never cost a file.
    /// </summary>
    public static bool SameBytes(string a, string b, CancellationToken cancel = default)
    {
        try
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            if (fa.Length != fb.Length)
            {
                return false;
            }
            byte[] ba = new byte[1 << 20], bb = new byte[1 << 20];
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                int na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                int nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb)))
                {
                    return false;
                }
                if (na == 0)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>XxHash3 of the first <paramref name="limit"/> bytes, or XxHash128 of the whole file. Null if unreadable.</summary>
    private static string? HashFile(string path, long limit, CancellationToken cancel)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            byte[] buffer = new byte[Math.Min(limit, 1 << 20)];
            if (limit != long.MaxValue)
            {
                int read = 0, got;
                while (read < buffer.Length && (got = stream.Read(buffer, read, buffer.Length - read)) > 0)
                {
                    read += got;
                }
                return Convert.ToHexStringLower(BitConverter.GetBytes(XxHash3.HashToUInt64(buffer.AsSpan(0, read))));
            }
            var hash = new XxHash128();
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                hash.Append(buffer.AsSpan(0, count));
            }
            return Convert.ToHexStringLower(hash.GetCurrentHash());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // vanished or unreadable: can't be called a duplicate
        }
    }
}

/// <summary>
/// How much of a file's space is its own. On APFS, clones (Finder's Duplicate, <c>cp -c</c>) share
/// blocks, so deleting one copy frees only its private part. Null when the OS can't tell.
/// </summary>
public interface ISharedBlocks
{
    long? PrivateBytes(string path);
}

/// <summary>For systems without block sharing (or when unknown): every byte counts.</summary>
public sealed class NoSharedBlocks : ISharedBlocks
{
    public long? PrivateBytes(string path) => null;
}

/// <summary>
/// Hashes remembered between runs (plan.md §6.2), keyed by path, length and modification time, so
/// an unchanged file is never read twice. A tab-separated text file; damaged lines are skipped.
/// </summary>
public sealed class HashCache(string file)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, uint MTime, string? Head, string? Full)> _entries = new(StringComparer.Ordinal);
    private bool _loaded, _dirty;
    public string File { get; } = file;

    /// <summary><c>$XDG_CACHE_HOME/hoard/hashes.tsv</c>, else <c>~/.cache/hoard/hashes.tsv</c>.</summary>
    public static string DefaultFile()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string baseDir = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(baseDir, "hoard", "hashes.tsv");
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            foreach (string line in System.IO.File.ReadLines(File))
            {
                string[] f = line.Split('\t');
                if (f.Length == 5 && long.TryParse(f[0], out long size) && uint.TryParse(f[1], out uint mtime))
                {
                    _entries[f[4]] = (size, mtime, f[2].Length > 0 ? f[2] : null, f[3].Length > 0 ? f[3] : null);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no cache yet (or unreadable): start empty
        }
    }

    public string? Get(string path, long size, uint mtime, bool full)
    {
        lock (_entries)
        {
            Load();
        }
        return _entries.TryGetValue(path, out var e) && e.Size == size && e.MTime == mtime ? (full ? e.Full : e.Head) : null;
    }

    public void Set(string path, long size, uint mtime, bool full, string hash)
    {
        lock (_entries)
        {
            Load();
            _entries.TryGetValue(path, out var e);
            if (e.Size != size || e.MTime != mtime)
            {
                e = (size, mtime, null, null);
            }
            _entries[path] = full ? e with { Full = hash } : e with { Head = hash };
            _dirty = true;
        }
    }

    public int Count => _entries.Count;

    public void Save()
    {
        lock (_entries)
        {
            if (!_dirty)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                string tmp = File + ".tmp";
                using (var w = new StreamWriter(tmp))
                {
                    foreach (var (path, e) in _entries)
                    {
                        if (path.Contains('\t') || path.Contains('\n'))
                        {
                            continue;
                        }
                        w.Write(e.Size);
                        w.Write('\t');
                        w.Write(e.MTime);
                        w.Write('\t');
                        w.Write(e.Head);
                        w.Write('\t');
                        w.Write(e.Full);
                        w.Write('\t');
                        w.WriteLine(path);
                    }
                }
                System.IO.File.Move(tmp, File, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a cache that can't be written just isn't kept
            }
        }
    }
}
