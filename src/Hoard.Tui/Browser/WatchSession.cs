using System.Collections.Concurrent;
using Hoard.Core.Model;

namespace Hoard.Tui.Browser;

/// <summary>One line of the change feed.</summary>
public sealed record WatchChange(DateTime At, long Delta, string Path, int Folder);

/// <summary>
/// Watch mode (plan.md §9.3): file-system events collect the folders that changed; after a short
/// quiet spell each one is rescanned (never the whole scan) and the difference goes to the feed.
/// The watcher thread only queues paths; everything else happens on the UI thread.
/// </summary>
public sealed class WatchSession : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<string> _events = new();
    private long _lastEventTicks;

    public DateTime Started { get; } = DateTime.Now;
    public string? Error { get; private set; }
    public bool Paused { get; set; }
    public List<WatchChange> Feed { get; } = [];
    /// <summary>Sizes of the scanned folder's entries when watching started (for "Δ since start").</summary>
    public Dictionary<int, long> Baseline { get; } = [];
    /// <summary>Changes per top-level entry: when they happened (for the activity sparkline).</summary>
    public Dictionary<int, List<DateTime>> Activity { get; } = [];
    /// <summary>Folders waiting to be rescanned, in order.</summary>
    public Queue<int> Pending { get; } = new();
    public long EventCount;

    public WatchSession(string root, NodeStore store)
    {
        foreach (int id in store.Children(NodeStore.Root))
        {
            Baseline[id] = store.Size(id);
        }
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Queue(e.FullPath);
            _watcher.Changed += (_, e) => Queue(e.FullPath);
            _watcher.Deleted += (_, e) => Queue(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Queue(e.OldFullPath);
                Queue(e.FullPath);
            };
            _watcher.Error += (_, e) => Error = e.GetException().Message;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
    }

    private void Queue(string path)
    {
        _events.Enqueue(path);
        Interlocked.Increment(ref EventCount);
        Interlocked.Exchange(ref _lastEventTicks, Environment.TickCount64);
    }

    /// <summary>
    /// After 250 ms without new events, turns the queued paths into the folders to rescan: the
    /// nearest folder in the scan above each changed path. Returns true if any were added.
    /// </summary>
    public bool Collect(Func<string, int> nearestFolder)
    {
        if (_events.IsEmpty || Environment.TickCount64 - Interlocked.Read(ref _lastEventTicks) < 250)
        {
            return false;
        }
        var folders = new HashSet<int>();
        while (_events.TryDequeue(out string? path))
        {
            int folder = nearestFolder(System.IO.Path.GetDirectoryName(path) ?? path);
            if (folder != NodeStore.None)
            {
                folders.Add(folder);
            }
        }
        // A folder inside another that's also queued is covered by the outer one.
        var queued = new HashSet<int>(Pending);
        bool added = false;
        foreach (int f in folders)
        {
            if (!queued.Contains(f))
            {
                Pending.Enqueue(f);
                queued.Add(f);
                added = true;
            }
        }
        return added;
    }

    public void Dispose() => _watcher?.Dispose();
}
