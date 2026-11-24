using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Hoard.Core.Filters;
using Hoard.Core.Model;

namespace Hoard.Core.Scanning;

public sealed record ScanOptions
{
    /// <summary>Worker threads. 0 = automatic (cores, capped at 12; see plan.md §12 M0 findings).</summary>
    public int Threads { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = [];
    /// <summary>Descend into directories on other file systems (default: stop at mount points).</summary>
    public bool CrossFileSystems { get; init; }
    /// <summary>
    /// Absolute paths known to be aliases of other paths inside the scan (e.g. macOS firmlink
    /// targets under /System/Volumes/Data when scanning /). They are recorded as
    /// <see cref="NodeKind.Alias"/> so the same data is not counted twice.
    /// </summary>
    public IReadOnlyCollection<string> AliasPaths { get; init; } = [];
}

public sealed record ScanError(string Path, string Message);

/// <summary>Live counters, safe to read from any thread while a scan runs.</summary>
public sealed class ScanProgress
{
    internal long FilesSeen;
    internal long DirsSeen;
    internal long BytesSeen;
    internal long ErrorsSeen;
    internal readonly Stopwatch Clock = new();

    public long Files => Interlocked.Read(ref FilesSeen);
    public long Directories => Interlocked.Read(ref DirsSeen);
    public long Bytes => Interlocked.Read(ref BytesSeen);
    public long Errors => Interlocked.Read(ref ErrorsSeen);
    public TimeSpan Elapsed => Clock.Elapsed;
}

public sealed class ScanResult
{
    public required NodeStore Store { get; init; }
    public required string RootPath { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required string ProbeName { get; init; }
    public required bool ExactSizes { get; init; }
    public required IReadOnlyList<ScanError> Errors { get; init; }
    public required long HardLinkDuplicates { get; init; }
    public required long HardLinkBytesSaved { get; init; }
    public required bool Cancelled { get; init; }
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// A scan in progress (plan.md §5.1: results are browsable while scanning). The root node exists
/// as soon as the session is created; folder totals in <see cref="Store"/> grow as folders are read,
/// and a folder's entries become visible all at once, after they are filled in. Folders queued but
/// not yet read carry <see cref="NodeFlags.Pending"/>.
/// </summary>
public sealed class ScanSession
{
    internal ScanSession(NodeStore store, string root, IFileProbe probe, ScanProgress progress)
    {
        Store = store;
        RootPath = root;
        ExactSizes = probe.IsExact;
        ProbeName = probe.Name;
        Progress = progress;
    }

    public NodeStore Store { get; }
    public string RootPath { get; }
    public bool ExactSizes { get; }
    public string ProbeName { get; }
    public ScanProgress Progress { get; }
    public Task<ScanResult> Completion { get; internal set; } = null!;
    public bool IsComplete => Completion.IsCompleted;
    /// <summary>The final result, once the scan has finished successfully.</summary>
    public ScanResult? Result => Completion.IsCompletedSuccessfully ? Completion.Result : null;
}

/// <summary>
/// Parallel directory scanner (plan.md §5.1). Workers share a LIFO stack of directories
/// (depth-first, which keeps the frontier small) and stop when no work is pending.
/// Symlinks are never followed, hard links are charged once, and scanning stops at
/// file-system boundaries unless <see cref="ScanOptions.CrossFileSystems"/> is set.
/// </summary>
public sealed class Scanner
{
    private readonly IFileProbe _probe;
    private readonly ScanOptions _options;
    private readonly GlobMatcher _excludes;

    public Scanner(IFileProbe probe, ScanOptions? options = null)
    {
        _probe = probe;
        _options = options ?? new ScanOptions();
        _excludes = new GlobMatcher(_options.Excludes);
    }

    public ScanProgress Progress { get; } = new();

    /// <summary>A folder to read, with the <c>.hoardignore</c> rules from the folders above it.</summary>
    private readonly record struct Work(int Node, string FullPath, string RelPath, LocalIgnore? Ignore = null);

    /// <summary>
    /// Patterns from a <c>.hoardignore</c> file (plan.md §5.4), relative to the folder it's in,
    /// chained to the ones further up.
    /// </summary>
    private sealed record LocalIgnore(string BaseRel, GlobMatcher Matcher, LocalIgnore? Outer)
    {
        public bool IsExcluded(string rel, string full, bool isDir)
        {
            for (LocalIgnore? l = this; l is not null; l = l.Outer)
            {
                string local = l.BaseRel.Length == 0 ? rel : rel.Length > l.BaseRel.Length ? rel[(l.BaseRel.Length + 1)..] : "";
                if (local.Length > 0 && l.Matcher.IsExcluded(local, full, isDir))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>The name of the per-folder ignore file.</summary>
    public const string IgnoreFile = ".hoardignore";

    private static LocalIgnore? ReadIgnore(string folder, string rel, LocalIgnore? outer)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(folder, IgnoreFile))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            return lines.Count == 0 ? outer : new LocalIgnore(rel, new GlobMatcher(lines), outer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return outer;
        }
    }

    private sealed class Run
    {
        public required NodeStore Store;
        public required ulong RootDevice;
        public readonly ConcurrentStack<Work> Stack = new();
        public readonly SemaphoreSlim Signal = new(0);
        /// <summary>Every node that is a path to a multiply-linked file, grouped by inode.</summary>
        public readonly ConcurrentDictionary<(ulong Dev, ulong Ino), List<int>> LinkGroups = new();
        public readonly ConcurrentDictionary<(ulong Dev, ulong Ino), byte> SeenDirs = new();
        public required HashSet<string> AliasPaths;
        public readonly ConcurrentQueue<ScanError> Errors = new();
        public long Pending;
        public long HardLinkDups;
        public long HardLinkSaved;
        public int Workers;
    }

    /// <summary>Scans and waits for the result.</summary>
    /// <exception cref="DirectoryNotFoundException">The root is missing or not a directory.</exception>
    public ScanResult Scan(string rootPath, CancellationToken cancel = default) =>
        Start(rootPath, cancel).Completion.GetAwaiter().GetResult();

    /// <summary>
    /// Validates the root, creates the root node, and starts scanning in the background.
    /// Returns immediately; watch <see cref="ScanSession.Store"/> and <see cref="Progress"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The root is missing or not a directory.</exception>
    public ScanSession Start(string rootPath, CancellationToken cancel = default)
    {
        string root = Path.GetFullPath(rootPath);
        if (root.Length > 1)
        {
            root = root.TrimEnd(Path.DirectorySeparatorChar);
        }
        // The folder the user names may itself be a symlink (e.g. /tmp on macOS): resolve it.
        // Symlinks found *during* the walk are never followed.
        if (_probe.TryGetInfo(root, out EntryInfo linkInfo, out _) && linkInfo.Type == EntryType.Symlink
            && new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            root = target.FullName.Length > 1 ? target.FullName.TrimEnd(Path.DirectorySeparatorChar) : target.FullName;
        }
        if (!_probe.TryGetInfo(root, out EntryInfo rootInfo, out _) || rootInfo.Type != EntryType.Directory)
        {
            throw new DirectoryNotFoundException($"Not a directory: {rootPath}");
        }

        var store = new NodeStore();
        int rootId = store.Reserve(1);
        store.Parent(rootId) = NodeStore.None;
        store.FirstChild(rootId) = NodeStore.None;
        store.SetName(rootId, store.Names.CreateWriter(), root);
        store.Kind(rootId) = NodeKind.Directory;
        store.Flags(rootId) = NodeFlags.Pending;
        store.Incomplete(rootId) = 1;
        store.Size(rootId) = rootInfo.Allocated;
        store.Apparent(rootId) = rootInfo.Apparent;
        store.MTime(rootId) = NodeStore.ToUnixTime(rootInfo.MTime);
        store.ATime(rootId) = NodeStore.ToUnixTime(rootInfo.ATime);

        var run = new Run { Store = store, RootDevice = rootInfo.Device, AliasPaths = new HashSet<string>(_options.AliasPaths, StringComparer.Ordinal) };
        if (_probe.IsExact)
        {
            run.SeenDirs.TryAdd((rootInfo.Device, rootInfo.Inode), 0);
        }
        int threads = _options.Threads > 0 ? _options.Threads : Math.Clamp(Environment.ProcessorCount, 1, 12);
        run.Workers = threads;
        run.Pending = 1;
        run.Stack.Push(new Work(rootId, root, ""));
        run.Signal.Release();

        var session = new ScanSession(store, root, _probe, Progress);
        Progress.Clock.Restart();
        var done = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Completion = done.Task;

        var coordinator = new Thread(() =>
        {
            try
            {
                var workers = new Thread[threads];
                for (int i = 0; i < threads; i++)
                {
                    workers[i] = new Thread(() => Worker(run, cancel)) { IsBackground = true, Name = $"hoard-scan-{i}" };
                    workers[i].Start();
                }
                foreach (Thread t in workers)
                {
                    t.Join();
                }
                Progress.Clock.Stop();

                AssignHardLinks(run);
                store.FinishFolderTimes();
                done.SetResult(new ScanResult
                {
                    Store = store,
                    RootPath = root,
                    Elapsed = Progress.Elapsed,
                    ProbeName = _probe.Name,
                    ExactSizes = _probe.IsExact,
                    Errors = [.. run.Errors],
                    HardLinkDuplicates = run.HardLinkDups,
                    HardLinkBytesSaved = run.HardLinkSaved,
                    Cancelled = cancel.IsCancellationRequested,
                });
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "hoard-scan" };
        coordinator.Start();
        return session;
    }

    private void Worker(Run run, CancellationToken cancel)
    {
        NamePool.Writer names = run.Store.Names.CreateWriter();
        while (true)
        {
            try
            {
                run.Signal.Wait(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!run.Stack.TryPop(out Work w))
            {
                // Woken with nothing to do: the scan is finished (or cancelled).
                return;
            }
            if (!cancel.IsCancellationRequested)
            {
                ScanDirectory(run, w, names, cancel);
            }
            if (Interlocked.Decrement(ref run.Pending) == 0)
            {
                run.Signal.Release(run.Workers);
                return;
            }
        }
    }

    /// <summary>
    /// Charges each multiply-linked file to exactly one of its paths in the scan: the one that
    /// sorts first (ordinal, by path relative to the root). During the scan the first path *seen*
    /// was charged so live totals are right; if that isn't the final choice, the charge moves.
    /// </summary>
    private static void AssignHardLinks(Run run)
    {
        NodeStore store = run.Store;
        foreach (List<int> group in run.LinkGroups.Values)
        {
            int charged = group[0];
            int primary = charged;
            if (group.Count > 1)
            {
                string best = store.RelativePath(primary);
                for (int i = 1; i < group.Count; i++)
                {
                    string p = store.RelativePath(group[i]);
                    if (string.CompareOrdinal(p, best) < 0)
                    {
                        best = p;
                        primary = group[i];
                    }
                }
            }
            long size = store.Size(charged);
            long apparent = store.Apparent(charged);
            if (primary != charged)
            {
                store.Size(charged) = 0;
                store.Apparent(charged) = 0;
                store.AddToAncestors(store.Parent(charged), -size, -apparent, 0, 0);
                store.Size(primary) = size;
                store.Apparent(primary) = apparent;
                store.AddToAncestors(store.Parent(primary), size, apparent, 0, 0);
            }
            foreach (int id in group)
            {
                if (id == primary)
                {
                    store.Flags(id) |= NodeFlags.HardLinkPrimary;
                    continue;
                }
                store.Flags(id) |= NodeFlags.HardLinkDuplicate;
                run.HardLinkDups++;
                run.HardLinkSaved += size;
            }
        }
    }

    private static readonly EnumerationOptions EnumOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        // Default skips Hidden | System; we want every entry, dotfiles included.
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private void ScanDirectory(Run run, Work w, NamePool.Writer nameWriter, CancellationToken cancel)
    {
        NodeStore store = run.Store;
        List<string> names;
        try
        {
            names = [.. new FileSystemEnumerable<string>(w.FullPath,
                (ref FileSystemEntry e) => e.FileName.ToString(), EnumOptions)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            store.Flags(w.Node) = (store.Flags(w.Node) & ~NodeFlags.Pending) | NodeFlags.Error;
            store.AddToAncestors(w.Node, 0, 0, 0, 0, incomplete: -1);
            run.Errors.Enqueue(new ScanError(w.FullPath, ex.Message));
            Interlocked.Increment(ref Progress.ErrorsSeen);
            return;
        }

        LocalIgnore? ignore = names.Contains(IgnoreFile) ? ReadIgnore(w.FullPath, w.RelPath, w.Ignore) : w.Ignore;
        int n = names.Count;
        if (n == 0)
        {
            store.Flags(w.Node) &= ~NodeFlags.Pending;
            store.AddToAncestors(w.Node, 0, 0, 0, 0, incomplete: -1);
            return;
        }
        int first = store.Reserve(n);

        long bytes = 0, apparentBytes = 0;
        int files = 0, dirs = 0;
        var queue = new List<Work>();
        for (int i = 0; i < n; i++)
        {
            int id = first + i;
            string name = names[i];
            string full = w.FullPath.Length == 1 ? "/" + name : w.FullPath + Path.DirectorySeparatorChar + name;
            string rel = w.RelPath.Length == 0 ? name : w.RelPath + "/" + name;
            store.Parent(id) = w.Node;
            store.SetName(id, nameWriter, name);
            store.FirstChild(id) = NodeStore.None;
            if (name.StartsWith('.'))
            {
                store.Flags(id) |= NodeFlags.Hidden;
            }

            if (!_probe.TryGetInfo(full, out EntryInfo info, out int errno))
            {
                store.Kind(id) = NodeKind.Other;
                store.Flags(id) |= NodeFlags.Error;
                run.Errors.Enqueue(new ScanError(full, $"stat failed (errno {errno})"));
                Interlocked.Increment(ref Progress.ErrorsSeen);
                files++;
                continue;
            }

            store.MTime(id) = NodeStore.ToUnixTime(info.MTime);
            store.ATime(id) = NodeStore.ToUnixTime(info.ATime);
            bool isDir = info.Type == EntryType.Directory;

            if ((!_excludes.IsEmpty && _excludes.IsExcluded(rel, full, isDir)) || (ignore?.IsExcluded(rel, full, isDir) ?? false))
            {
                store.Kind(id) = NodeKind.Excluded;
                continue;
            }

            store.Kind(id) = info.Type switch
            {
                EntryType.Directory => NodeKind.Directory,
                EntryType.File => NodeKind.File,
                EntryType.Symlink => NodeKind.Symlink,
                _ => NodeKind.Other,
            };

            long allocated = info.Allocated;
            long apparent = info.Apparent;
            if (info.Type == EntryType.File && info.LinkCount > 1 && _probe.IsExact)
            {
                // The first path seen is charged for now; AssignHardLinks makes the final,
                // order-independent choice when the scan ends.
                List<int> group = run.LinkGroups.GetOrAdd((info.Device, info.Inode), _ => []);
                bool firstLink;
                lock (group)
                {
                    firstLink = group.Count == 0;
                    group.Add(id);
                }
                if (!firstLink)
                {
                    allocated = 0;
                    apparent = 0;
                }
            }

            if (isDir)
            {
                dirs++;
                bool otherDisk = !_options.CrossFileSystems && _probe.IsExact && info.Device != run.RootDevice;
                if (otherDisk)
                {
                    store.Kind(id) = NodeKind.MountPoint;
                }
                // Known aliases first (deterministic), then any directory already seen by (dev, ino).
                else if ((run.AliasPaths.Count > 0 && run.AliasPaths.Contains(full))
                    || (_probe.IsExact && !run.SeenDirs.TryAdd((info.Device, info.Inode), 0)))
                {
                    store.Kind(id) = NodeKind.Alias;
                    allocated = 0;
                    apparent = 0;
                }
                else if (!cancel.IsCancellationRequested)
                {
                    store.Flags(id) |= NodeFlags.Pending;
                    store.Incomplete(id) = 1;
                    queue.Add(new Work(id, full, rel, ignore));
                }
            }
            else
            {
                files++;
            }
            store.Size(id) = allocated;
            store.Apparent(id) = apparent;
            bytes += allocated;
            apparentBytes += apparent;
        }

        // Publish: the entries are complete, so readers may now see them.
        store.FirstChild(w.Node) = first;
        Volatile.Write(ref store.ChildCount(w.Node), n);
        // This folder is done (-1); its queued subfolders are now pending below it (+queue.Count).
        store.AddToAncestors(w.Node, bytes, apparentBytes, files, dirs, incomplete: queue.Count - 1);
        store.Flags(w.Node) &= ~NodeFlags.Pending;

        foreach (Work child in queue)
        {
            Interlocked.Increment(ref run.Pending);
            run.Stack.Push(child);
            run.Signal.Release();
        }

        Interlocked.Add(ref Progress.FilesSeen, files);
        Interlocked.Add(ref Progress.DirsSeen, dirs);
        Interlocked.Add(ref Progress.BytesSeen, bytes);
    }
}
