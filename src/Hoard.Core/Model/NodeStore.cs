namespace Hoard.Core.Model;

public enum NodeKind : byte
{
    File,
    Directory,
    Symlink,
    Other,
    /// <summary>Matched an exclude pattern; not descended into, size not counted.</summary>
    Excluded,
    /// <summary>A directory on another file system; not descended into.</summary>
    MountPoint,
    /// <summary>
    /// Another path to a directory that is counted elsewhere in this scan (a macOS firmlink,
    /// bind mount or directory hard link); not descended into, size not counted.
    /// </summary>
    Alias,
}

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    /// <summary>A hard-linked file whose size is charged here (first path seen).</summary>
    HardLinkPrimary = 1,
    /// <summary>Another path to an already-counted inode; charged 0 bytes.</summary>
    HardLinkDuplicate = 2,
    /// <summary>Could not be read (permission denied, vanished, I/O error).</summary>
    Error = 4,
    Hidden = 8,
    /// <summary>A folder queued for scanning whose entries haven't been read yet.</summary>
    Pending = 16,
    /// <summary>Deleted (or moved to the Trash) by the user; hidden and no longer counted.</summary>
    Deleted = 32,
}

/// <summary>
/// Struct-of-arrays storage for scan results (plan.md §4.1). Nodes live in fixed-size chunks
/// that never move, so readers can look at existing nodes while workers append new ones.
/// The children of a directory occupy one contiguous id range, and a child's id is always
/// greater than its parent's, which makes bottom-up aggregation a single reverse pass.
/// </summary>
public sealed class NodeStore
{
    private const int ChunkBits = 14;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;

    private sealed class Chunk
    {
        public readonly int[] Parent = new int[ChunkSize];
        public readonly int[] FirstChild = new int[ChunkSize];
        public readonly int[] ChildCount = new int[ChunkSize];
        public readonly long[] Size = new long[ChunkSize];
        public readonly long[] Apparent = new long[ChunkSize];
        public readonly int[] Files = new int[ChunkSize];
        public readonly int[] Dirs = new int[ChunkSize];
        public readonly int[] Incomplete = new int[ChunkSize];
        public readonly uint[] MTime = new uint[ChunkSize];
        public readonly uint[] ATime = new uint[ChunkSize];
        public readonly int[] NameHandle = new int[ChunkSize];
        public readonly NodeKind[] Kind = new NodeKind[ChunkSize];
        public readonly NodeFlags[] Flags = new NodeFlags[ChunkSize];
    }

    private Chunk?[] _chunks = new Chunk?[64];
    private int _count;
    private readonly Lock _gate = new();

    public const int Root = 0;
    public const int None = -1;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Entry names, stored as UTF-8 (see <see cref="NamePool"/>).</summary>
    public NamePool Names { get; } = new();

    /// <summary>Bytes held by node arrays (excluding names).</summary>
    public long AllocatedNodeBytes
    {
        get
        {
            int chunks = (Count + ChunkMask) >> ChunkBits;
            // 7 × int + 2 × uint + 2 × long + 2 × byte = 54 bytes per node.
            return (long)chunks * ChunkSize * (7 * 4 + 2 * 4 + 2 * 8 + 2);
        }
    }

    /// <summary>Reserves <paramref name="n"/> consecutive node ids and returns the first.</summary>
    public int Reserve(int n)
    {
        lock (_gate)
        {
            int start = _count;
            int end = start + n;
            int lastChunk = (end - 1) >> ChunkBits;
            if (lastChunk >= _chunks.Length)
            {
                Array.Resize(ref _chunks, Math.Max(_chunks.Length * 2, lastChunk + 1));
            }
            for (int c = start >> ChunkBits; c <= lastChunk; c++)
            {
                _chunks[c] ??= new Chunk();
            }
            Volatile.Write(ref _count, end);
            return start;
        }
    }

    private Chunk C(int id) => _chunks[id >> ChunkBits]!;

    public ref int Parent(int id) => ref C(id).Parent[id & ChunkMask];
    public ref int FirstChild(int id) => ref C(id).FirstChild[id & ChunkMask];
    public ref int ChildCount(int id) => ref C(id).ChildCount[id & ChunkMask];
    /// <summary>On-disk (allocated) bytes; for directories, the whole subtree after <see cref="Aggregate"/>.</summary>
    public ref long Size(int id) => ref C(id).Size[id & ChunkMask];
    /// <summary>Apparent (logical) bytes; for directories, the whole subtree after <see cref="Aggregate"/>.</summary>
    public ref long Apparent(int id) => ref C(id).Apparent[id & ChunkMask];
    /// <summary>Files below this node (recursive, after <see cref="Aggregate"/>).</summary>
    public ref int Files(int id) => ref C(id).Files[id & ChunkMask];
    /// <summary>Directories below this node (recursive, after <see cref="Aggregate"/>).</summary>
    public ref int Dirs(int id) => ref C(id).Dirs[id & ChunkMask];
    /// <summary>
    /// Folders in this subtree (itself included) whose entries haven't been read yet.
    /// Greater than 0 means the totals shown for this folder are still growing.
    /// </summary>
    public ref int Incomplete(int id) => ref C(id).Incomplete[id & ChunkMask];

    public bool IsGrowing(int id) => Volatile.Read(ref Incomplete(id)) > 0;
    /// <summary>Last modification time, Unix seconds (unsigned: 1970–2106).</summary>
    public ref uint MTime(int id) => ref C(id).MTime[id & ChunkMask];
    /// <summary>Last access time, Unix seconds (unsigned: 1970–2106).</summary>
    public ref uint ATime(int id) => ref C(id).ATime[id & ChunkMask];

    public string Name(int id) => Names.Get(C(id).NameHandle[id & ChunkMask]);

    public void SetName(int id, NamePool.Writer writer, ReadOnlySpan<char> name) =>
        C(id).NameHandle[id & ChunkMask] = Names.Add(writer, name);

    public static uint ToUnixTime(long seconds) => (uint)Math.Clamp(seconds, 0, uint.MaxValue);
    public ref NodeKind Kind(int id) => ref C(id).Kind[id & ChunkMask];
    public ref NodeFlags Flags(int id) => ref C(id).Flags[id & ChunkMask];

    public bool IsContainer(int id) => Kind(id) == NodeKind.Directory;

    /// <summary>
    /// The children of a node. While a scan runs this is either empty or complete: the scanner
    /// fills a folder's entries first and publishes the count last.
    /// </summary>
    public IEnumerable<int> Children(int id)
    {
        int n = Volatile.Read(ref ChildCount(id));
        int first = FirstChild(id);
        for (int i = 0; i < n; i++)
        {
            if ((Flags(first + i) & NodeFlags.Deleted) == 0)
            {
                yield return first + i;
            }
        }
    }

    public bool IsDeleted(int id) => (Flags(id) & NodeFlags.Deleted) != 0;

    /// <summary>True if <paramref name="ancestor"/> is <paramref name="id"/> or one of its parents.</summary>
    public bool IsWithin(int id, int ancestor)
    {
        for (int n = id; n != None; n = Parent(n))
        {
            if (n == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Files a node accounts for: 1 for a file-like entry, the recursive count for a folder.</summary>
    public int FileCount(int id) => Kind(id) is NodeKind.File or NodeKind.Symlink or NodeKind.Other ? 1 : Files(id);

    /// <summary>Folders a node accounts for: itself plus everything below, for a folder.</summary>
    public int DirCount(int id) => Kind(id) is NodeKind.Directory or NodeKind.MountPoint ? Dirs(id) + 1 : 0;

    /// <summary>Removes a node from the totals of every folder above it and hides it.</summary>
    public void MarkDeleted(int id)
    {
        if (IsDeleted(id))
        {
            return;
        }
        Flags(id) |= NodeFlags.Deleted;
        AddToAncestors(Parent(id), -Size(id), -Apparent(id), -FileCount(id), -DirCount(id));
    }

    /// <summary>Reverses <see cref="MarkDeleted"/> (used by undo).</summary>
    public void Restore(int id)
    {
        if (!IsDeleted(id))
        {
            return;
        }
        Flags(id) &= ~NodeFlags.Deleted;
        AddToAncestors(Parent(id), Size(id), Apparent(id), FileCount(id), DirCount(id));
    }

    /// <summary>Path of a node relative to the root, using '/' separators ("" for the root).</summary>
    public string RelativePath(int id)
    {
        var parts = new Stack<string>();
        for (int n = id; n != Root && n != None; n = Parent(n))
        {
            parts.Push(Name(n));
        }
        return string.Join('/', parts);
    }

    /// <summary>
    /// Adds a batch of sizes and counts to <paramref name="id"/> and every folder above it.
    /// Safe to call from several scanner threads at once.
    /// </summary>
    public void AddToAncestors(int id, long size, long apparent, int files, int dirs, int incomplete = 0)
    {
        for (int n = id; n != None; n = Parent(n))
        {
            if (incomplete != 0)
            {
                Interlocked.Add(ref Incomplete(n), incomplete);
            }
            Interlocked.Add(ref Size(n), size);
            Interlocked.Add(ref Apparent(n), apparent);
            if (files != 0)
            {
                Interlocked.Add(ref Files(n), files);
            }
            if (dirs != 0)
            {
                Interlocked.Add(ref Dirs(n), dirs);
            }
        }
    }

    /// <summary>
    /// Turns an entry into an excluded one, as if a pattern had matched it during the scan: it no
    /// longer counts, and what was inside goes away (plan.md §5.4, <c>X</c>).
    /// </summary>
    public void Exclude(int id)
    {
        if (id == Root || Kind(id) == NodeKind.Excluded)
        {
            return;
        }
        var inside = new Stack<int>(Children(id));
        while (inside.Count > 0)
        {
            int n = inside.Pop();
            foreach (int c in Children(n))
            {
                inside.Push(c);
            }
            Flags(n) |= NodeFlags.Deleted;
        }
        int files = FileCount(id), dirs = DirCount(id);
        AddToAncestors(Parent(id), -Size(id), -Apparent(id), -files, -dirs);
        Volatile.Write(ref ChildCount(id), 0);
        Size(id) = 0;
        Apparent(id) = 0;
        Files(id) = 0;
        Dirs(id) = 0;
        Kind(id) = NodeKind.Excluded;
    }

    /// <summary>
    /// Replaces what's inside folder <paramref name="dir"/> with what a fresh scan of that folder
    /// found (<paramref name="fresh"/>, rooted at its <see cref="Root"/>), and moves the totals of
    /// every parent by the difference (plan.md §5: rescan the selected subtree). The old entries
    /// stay in memory but are no longer reachable. Call from one thread at a time.
    /// </summary>
    public void Graft(int dir, NodeStore fresh)
    {
        long dSize = fresh.Size(Root) - Size(dir), dApparent = fresh.Apparent(Root) - Apparent(dir);
        int dFiles = fresh.Files(Root) - Files(dir), dDirs = fresh.Dirs(Root) - Dirs(dir);
        // The old entries go away like deleted ones, so lists that remember them (reports, marks) drop them.
        var old = new Stack<int>(Children(dir));
        while (old.Count > 0)
        {
            int id = old.Pop();
            foreach (int c in Children(id))
            {
                old.Push(c);
            }
            Flags(id) |= NodeFlags.Deleted;
        }
        NamePool.Writer writer = Names.CreateWriter();
        var queue = new Queue<(int From, int To)>();
        queue.Enqueue((Root, dir));
        while (queue.Count > 0)
        {
            var (from, to) = queue.Dequeue();
            var kids = fresh.Children(from).ToList();
            int first = kids.Count > 0 ? Reserve(kids.Count) : None;
            for (int i = 0; i < kids.Count; i++)
            {
                int src = kids[i], dst = first + i;
                Parent(dst) = to;
                FirstChild(dst) = None;
                ChildCount(dst) = 0;
                Size(dst) = fresh.Size(src);
                Apparent(dst) = fresh.Apparent(src);
                Files(dst) = fresh.Files(src);
                Dirs(dst) = fresh.Dirs(src);
                MTime(dst) = fresh.MTime(src);
                ATime(dst) = fresh.ATime(src);
                Kind(dst) = fresh.Kind(src);
                Flags(dst) = fresh.Flags(src) & ~NodeFlags.Pending;
                SetName(dst, writer, fresh.Name(src));
                if (fresh.Kind(src) == NodeKind.Directory)
                {
                    queue.Enqueue((src, dst));
                }
            }
            // Count to zero, then the new range, then the new count: a reader on another thread
            // never pairs the old count with the new range.
            Volatile.Write(ref ChildCount(to), 0);
            Volatile.Write(ref FirstChild(to), first);
            Volatile.Write(ref ChildCount(to), kids.Count);
        }
        Flags(dir) = (Flags(dir) & ~(NodeFlags.Error | NodeFlags.Pending)) | (fresh.Flags(Root) & NodeFlags.Error);
        MTime(dir) = fresh.MTime(Root);
        AddToAncestors(dir, dSize, dApparent, dFiles, dDirs);
    }

    /// <summary>
    /// Call once when a scan ends. A folder's <see cref="MTime"/> becomes the newest modification
    /// time of anything inside it (a folder's own timestamp only changes when entries are added or
    /// removed, which says little about how stale its contents are). Empty folders keep their own time.
    /// Sizes and counts don't need this: the scanner keeps them up to date as it goes.
    /// </summary>
    public void FinishFolderTimes()
    {
        int n = Count;
        var newest = new uint[n];
        for (int id = n - 1; id > Root; id--)
        {
            int p = Parent(id);
            uint own = Kind(id) == NodeKind.Directory ? newest[id] : MTime(id);
            if (Kind(id) == NodeKind.Directory && newest[id] > 0)
            {
                MTime(id) = newest[id];
            }
            if (Kind(id) is NodeKind.File or NodeKind.Directory && own > newest[p])
            {
                newest[p] = own;
            }
        }
        if (n > 0 && newest[Root] > 0)
        {
            MTime(Root) = newest[Root];
        }
    }
}
