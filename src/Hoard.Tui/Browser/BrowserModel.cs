using Hoard.Core.Analysis;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Browser;

public enum SortField { Size, Name, Files, Modified }

/// <summary>The views that exist so far, with their tab keys from plan.md §7.1a.</summary>
public enum ViewKind { Overview, Browse, Map, Types, Age, Dupes, Junk, BigOld, Empty, Snaps, Watch }

public enum DupKeep { Oldest, Newest, ShortestPath, InsideFolder }

public enum BigOldSort { Score, Size, Age, Path }

/// <summary>Everything the browser shows, independent of any terminal library.</summary>
public sealed class BrowserModel
{
    public BrowserModel(ScanSession session, Theme? theme = null, Func<long>? now = null, Func<string, DiskSpace?>? disk = null)
    {
        Session = session;
        Theme = theme ?? Theme.Hoard;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _disk = disk ?? DiskSpace.For;
        _types = new BackgroundJob<TypeReport>(cancel => FileTypes.Analyze(Store, cancel));
        Refresh();
    }

    // ── settings (plan.md §7.6, mock 16-settings.sh) ──────────────────────
    /// <summary>The saved choices; changing them through the settings screen saves the file.</summary>
    public Hoard.Core.Config.Settings Settings { get; private set; } = new();
    /// <summary>Where settings are saved; null means "don't save" (tests, read-only setups).</summary>
    public string? SettingsPath { get; private set; }
    public bool SettingsOpen { get; set; }
    public int SettingsRow { get; set; }

    /// <summary>Adopts saved settings at start-up (the theme is decided by the caller, as flags can override it).</summary>
    public void UseSettings(Hoard.Core.Config.Settings settings, string? path, bool apparent)
    {
        Settings = settings;
        SettingsPath = path;
        Hoard.Core.Formatting.Units.DefaultIec = settings.Iec;
        Glyphs.Ascii = settings.AsciiGlyphs;
        BigOldOptions = settings.BigOld;
        if (apparent != Apparent)
        {
            Apparent = apparent;
            ResortKeepingSelection();
        }
    }

    /// <summary>Writes the settings file; says so (or why it couldn't) in the toast.</summary>
    public void SaveSettings()
    {
        if (SettingsPath is null)
        {
            return;
        }
        try
        {
            Hoard.Core.Config.SettingsFile.Save(Settings, SettingsPath);
            ShowToast("saved to " + Screens.Shorten(SettingsPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast("couldn't save settings: " + e.Message);
        }
    }

    /// <summary>Changes the highlighted setting by one step and saves.</summary>
    public void ChangeSetting(int delta)
    {
        if (SettingsRow >= SettingsItems.All.Length)
        {
            return; // pattern rows: ↵ adds, del removes
        }
        SettingsItems.All[SettingsRow].Change(this, delta);
        SaveSettings();
    }

    // ── splash, tour and tips (plan.md §7.8, §7.11) ───────────────────────
    /// <summary>Ticks (80 ms each) the splash stays up; 0 means no splash.</summary>
    public int SplashTicks { get; set; }
    public bool SplashOpen => SplashTicks > 0 && Tick < SplashTicks;
    public void SkipSplash() => SplashTicks = 0;
    /// <summary>The first-run tour's step (0–2) while it's showing.</summary>
    public int? TourStep { get; set; }
    /// <summary>The row under the mouse pointer, for a hover highlight.</summary>
    public int? HoverY { get; set; }

    public static readonly string[] AllTips =
    [
        "press J to jump straight to the junk report",
        ": opens a palette with every action, by name",
        "/ finds anything in the scan, with fuzzy matching",
        "u undoes the last move to the Trash",
        "x exports a report: HTML that looks like this, JSON, CSV…",
        "0 shows what grew since your last scan",
        "w watches the folder and updates sizes as files change",
        "t cycles the colour themes, and , picks one for good",
        "space marks things in any tab; d deletes them all at once",
        "6 finds duplicate files, and knows about APFS clones",
    ];

    /// <summary>Today's tip (the same all day), or null when tips are off.</summary>
    public string? Tip => Settings.Tips ? AllTips[DateTime.Now.DayOfYear % AllTips.Length] : null;

    public void FinishTour()
    {
        TourStep = null;
        Settings.TourDone = true;
        SaveSettings();
        ClearToast();
    }

    // ── scanning screen (plan.md §5.1, mock 02-scanning.sh) ────────────────
    /// <summary>Show the full-screen scan progress while scanning (until the user skips to the results).</summary>
    public bool ScanScreen { get; set; }

    private readonly List<double> _rates = [];
    private (long Ms, long Files)? _lastSample;

    /// <summary>Files per second, one sample every ~0.4 s, oldest first (for the sparkline).</summary>
    public IReadOnlyList<double> ScanRates => _rates;

    /// <summary>Records scan throughput; call regularly from the UI thread while scanning.</summary>
    public void SampleScan(long nowMs)
    {
        long files = Session.Progress.Files;
        if (_lastSample is not { } last)
        {
            _lastSample = (nowMs, files);
            return;
        }
        if (nowMs - last.Ms < 400)
        {
            return;
        }
        _rates.Add((files - last.Files) * 1000.0 / (nowMs - last.Ms));
        if (_rates.Count > 120)
        {
            _rates.RemoveAt(0);
        }
        _lastSample = (nowMs, files);
    }

    // ── overview (plan.md §7.2) ────────────────────────────────────────────
    private readonly Func<string, DiskSpace?> _disk;
    private DiskSpace? _diskInfo;
    private long? _diskReadAt;
    private readonly BackgroundJob<TypeReport> _types;

    /// <summary>The volume being scanned, re-read at most every few seconds (free space changes as you delete).</summary>
    public DiskSpace? Disk
    {
        get
        {
            long now = Environment.TickCount64;
            if (_diskReadAt is not long last || now - last > 3000)
            {
                _diskReadAt = now;
                _diskInfo = _disk(RootPath);
            }
            return _diskInfo;
        }
    }

    /// <summary>The by-type breakdown once the scan is done and it has been added up (never blocks).</summary>
    public TypeReport? Types
    {
        get
        {
            StartTypes();
            _types.Poll();
            return _types.Result;
        }
    }

    public bool TypesLoading => _types.Result is null && _types.Error is null;
    public string? TypesError => _types.Error;

    public void StartTypes()
    {
        if (!Scanning)
        {
            _types.Start();
        }
    }

    public bool PollTypes() => _types.Poll();
    public TypeReport? WaitForTypes() => Scanning ? null : _types.Wait();

    /// <summary>Adds the types up again, e.g. after a delete.</summary>
    public void RefreshTypes()
    {
        if (_types.Started)
        {
            _types.Restart();
        }
        _ageKey = null; // the heatmap is worked out again on the next frame
        _bigOldKey = null;
        _emptyKey = null;
        ForgetLive();
        _diskReadAt = null;
    }

    // ── types view (plan.md §6.5) ──────────────────────────────────────────
    /// <summary>Group the table by extension instead of by category (<c>e</c>).</summary>
    public bool TypesByExtension { get; set; }
    public int TypesSelected { get; set; }
    public int TypesScroll { get; set; }

    /// <summary>The files of one category or extension, biggest first, while that list is open.</summary>
    public FileList? TypeFiles { get; private set; }

    /// <summary>Opens the file list for a category or an extension; it fills in the background.</summary>
    public void OpenTypeFiles(TypeCategory? category, string? extension, string title, long count)
    {
        TypeFiles?.Job.Cancel();
        var job = new BackgroundJob<List<int>>(cancel => FileTypes.LargestFiles(Store, category, extension, FileList.Limit, cancel));
        TypeFiles = new FileList(title, count, job);
        job.Start();
    }

    public void CloseTypeFiles()
    {
        TypeFiles?.Job.Cancel();
        TypeFiles = null;
    }

    // ── age heatmap (plan.md §6.6) ─────────────────────────────────────────
    public AgeAxis AgeAxis { get; private set; } = AgeAxis.Modified;
    /// <summary>How many of the biggest entries get a row (+ and - change it).</summary>
    public int AgeRowLimit { get; private set; } = 8;
    public int AgeSelected { get; set; }
    /// <summary>Highlighted column: -1 is the name (↵ opens the folder), 0–7 the age buckets (↵ lists the files).</summary>
    public int AgeColumn { get; set; } = AgeBuckets.Count - 1;
    public FileList? AgeFiles { get; private set; }

    private BackgroundJob<AgeReport>? _age;
    private (int Folder, AgeAxis Axis, int Rows)? _ageKey;

    /// <summary>The heatmap of the open folder; null while scanning or while it's being worked out (never blocks).</summary>
    public AgeReport? Age
    {
        get
        {
            if (Scanning)
            {
                return null;
            }
            var key = (Current, AgeAxis, AgeRowLimit);
            if (_age is null || _ageKey != key)
            {
                _age?.Cancel();
                _ageKey = key;
                long now = Now;
                var (folder, axis, rows) = key;
                _age = new BackgroundJob<AgeReport>(cancel => AgeBuckets.Analyze(Store, folder, axis, now, rows, cancel));
                _age.Start();
            }
            _age.Poll();
            return _age.Result;
        }
    }

    public bool AgeLoading => !Scanning && Age is null && AgeError is null;
    public string? AgeError => _age?.Error;
    public AgeReport? WaitForAge()
    {
        _ = Age;
        return _age?.Wait();
    }

    public void SetAgeAxis(AgeAxis axis)
    {
        ShowToast(axis == AgeAxis.Accessed
            ? "by last access · macOS updates access times lazily, so treat them as a rough guide"
            : "by last modification");
        AgeAxis = axis;
        CloseAgeFiles();
    }

    public void ChangeAgeRows(int delta)
    {
        int rows = Math.Clamp(AgeRowLimit + delta, 3, 30);
        ShowToast(rows == AgeRowLimit ? $"rows stay at {rows} (3–30)" : $"showing the {rows} biggest entries");
        AgeRowLimit = rows;
    }

    public void OpenAgeFiles(int under, int bucket, long count)
    {
        AgeFiles?.Job.Cancel();
        long now = Now;
        AgeAxis axis = AgeAxis;
        string what = axis == AgeAxis.Accessed ? "opened" : "modified";
        string where = under == NodeStore.Root ? CrumbLabel : Store.Name(under);
        var job = new BackgroundJob<List<int>>(cancel => AgeBuckets.LargestFiles(Store, under, axis, now, bucket, FileList.Limit, cancel));
        AgeFiles = new FileList($"{where} · {what} {AgeBuckets.Phrases[bucket]}", count, job);
        job.Start();
    }

    public void CloseAgeFiles()
    {
        AgeFiles?.Job.Cancel();
        AgeFiles = null;
    }

    /// <summary>Short name of the scanned folder for titles.</summary>
    private string CrumbLabel => Path.GetFileName(RootPath.TrimEnd('/')) is { Length: > 0 } n ? n : RootPath;

    // ── volumes (v) ────────────────────────────────────────────────────────
    public bool VolumesOpen { get; set; }
    public bool ShowSystemVolumes { get; set; }
    public int VolumeSelected { get; set; }
    /// <summary>A path being typed on the volumes screen (<c>p</c>); null when not typing.</summary>
    public string? PathInput { get; set; }
    private List<Volume>? _volumes;
    /// <summary>Read once each time the screen opens (it's quick, but not free).</summary>
    public List<Volume> VolumeList => _volumes ??= [.. Hoard.Core.Model.Volumes.List().Where(v => ShowSystemVolumes || !v.System)];
    public void RefreshVolumes() => _volumes = null;

    // ── watch mode (w, plan.md §9.3) ───────────────────────────────────────
    public WatchSession? Watching { get; private set; }
    public int WatchSelected { get; set; }
    private ViewKind _beforeWatch;
    private readonly HashSet<int> _quietRescans = [];

    public string StartWatch()
    {
        if (Scanning)
        {
            return "watching starts when the scan finishes";
        }
        if (Rescanner is null)
        {
            return "watching isn't available here";
        }
        Watching?.Dispose();
        Watching = new WatchSession(RootPath, Store);
        _beforeWatch = View == ViewKind.Watch ? ViewKind.Browse : View;
        View = ViewKind.Watch;
        return Watching.Error is { } e ? "can't watch this folder: " + e : "watching for changes · esc stops";
    }

    public void StopWatch()
    {
        Watching?.Dispose();
        Watching = null;
        View = _beforeWatch;
        ShowToast("stopped watching");
    }

    /// <summary>The deepest folder in the scan that contains <paramref name="fullPath"/>.</summary>
    public int NearestFolder(string fullPath)
    {
        string root = RootPath.TrimEnd('/');
        if (fullPath != root && !fullPath.StartsWith(root + "/", StringComparison.Ordinal) && root.Length > 0)
        {
            return NodeStore.None;
        }
        int node = NodeStore.Root;
        string rest = fullPath.Length > root.Length ? fullPath[(root.Length + 1)..] : "";
        foreach (string part in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            int next = Store.Children(node).FirstOrDefault(id => Store.Name(id) == part && Store.Kind(id) == NodeKind.Directory, NodeStore.None);
            if (next == NodeStore.None)
            {
                break;
            }
            node = next;
        }
        return node;
    }

    /// <summary>Call every tick while watching: queues changed folders and rescans them one by one.</summary>
    public bool PollWatch()
    {
        if (Watching is not { } w)
        {
            return false;
        }
        bool changed = w.Collect(NearestFolder);
        if (!w.Paused && _rescan is null && w.Pending.Count > 0)
        {
            int folder = w.Pending.Dequeue();
            if (!Store.IsDeleted(folder))
            {
                _quietRescans.Add(folder);
                Rescan(folder);
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>Records a finished watch rescan in the feed instead of a toast.</summary>
    private bool NoteWatchChange(int folder, long delta)
    {
        if (!_quietRescans.Remove(folder) || Watching is not { } w)
        {
            return false;
        }
        if (delta != 0)
        {
            w.Feed.Insert(0, new WatchChange(DateTime.Now, delta, FullPath(folder), folder));
            if (w.Feed.Count > 500)
            {
                w.Feed.RemoveAt(w.Feed.Count - 1);
            }
            int top = folder;
            while (top != NodeStore.Root && Store.Parent(top) != NodeStore.Root)
            {
                top = Store.Parent(top);
            }
            if (!w.Activity.TryGetValue(top, out var times))
            {
                w.Activity[top] = times = [];
            }
            times.Add(DateTime.Now);
        }
        return true;
    }

    // ── rescan (r, R) ──────────────────────────────────────────────────────
    /// <summary>Scans one folder from scratch (set by the app; null where rescans aren't possible).</summary>
    public Func<string, CancellationToken, ScanResult>? Rescanner { get; set; }
    private (int Folder, Task<ScanResult> Task, CancellationTokenSource Cancel)? _rescan;

    public bool Rescanning => _rescan is not null;
    public int? RescanFolder => _rescan?.Folder;

    /// <summary>Starts a background rescan of one folder; its new totals replace the old ones when done.</summary>
    public string Rescan(int folder)
    {
        if (Rescanner is not { } scan)
        {
            return "rescanning isn't available here";
        }
        if (Scanning)
        {
            return "the scan is still running";
        }
        if (_rescan is not null)
        {
            return "already rescanning " + Store.Name(_rescan.Value.Folder);
        }
        if (Store.Kind(folder) != NodeKind.Directory)
        {
            folder = Store.Parent(folder);
        }
        string path = FullPath(folder);
        var cancel = new CancellationTokenSource();
        _rescan = (folder, Task.Run(() => scan(path, cancel.Token)), cancel);
        return "rescanning " + Rendering.Screens.Shorten(path) + "…";
    }

    /// <summary>Call regularly from the UI thread: puts a finished rescan in place. True when one just finished.</summary>
    public bool PollRescan()
    {
        if (_rescan is not { Task.IsCompleted: true } r)
        {
            return false;
        }
        _rescan = null;
        if (r.Task.IsFaulted || r.Task.Result.Cancelled)
        {
            ShowToast("rescan failed: " + (r.Task.Exception?.GetBaseException().Message ?? "cancelled"));
            return true;
        }
        long before = Store.Size(r.Folder);
        Store.Graft(r.Folder, r.Task.Result.Store);
        long delta = Store.Size(r.Folder) - before;
        bool quiet = NoteWatchChange(r.Folder, delta);
        // Everything worked out from the old totals is out of date now.
        RefreshTypes();
        // A fresh junk search on the new totals; the old one is stopped, not left running.
        _junkCancel.Cancel();
        _junkCancel = new CancellationTokenSource();
        _junk = null;
        _junkTask = null;
        StartJunk();
        Unmark([]); // marks on entries that were replaced
        LeaveDeletedFolders();
        RefreshLive();
        string name = r.Folder == NodeStore.Root ? "the scan" : Store.Name(r.Folder);
        if (quiet)
        {
            return true;
        }
        ShowToast($"rescanned {name} · {(delta == 0 ? "no change" : (delta > 0 ? "+" : "−") + Hoard.Core.Formatting.Units.Size(Math.Abs(delta)))}");
        return true;
    }

    public void WaitForRescan()
    {
        try
        {
            _rescan?.Task.Wait();
        }
        catch (AggregateException)
        {
        }
        PollRescan();
    }

    // ── exclude patterns (plan.md §5.4) ────────────────────────────────────
    /// <summary>The pattern being typed in settings ("+ add pattern"); null when not typing.</summary>
    public string? PatternInput { get; set; }

    /// <summary>A pattern that matches exactly this entry from anywhere: "~/…" under home, else "//…".</summary>
    public string PatternFor(int id)
    {
        string full = FullPath(id);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string p = home.Length > 1 && full.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + full[home.Length..] : "/" + full;
        return Store.Kind(id) == NodeKind.Directory ? p + "/" : p;
    }

    /// <summary>X: stops counting an entry now and leaves it out of future scans (saved in settings).</summary>
    public string ExcludeNode(int id)
    {
        if (id == NodeStore.Root)
        {
            return "the scanned folder itself can't be excluded";
        }
        if (Scanning)
        {
            return "wait for the scan to finish";
        }
        string pattern = PatternFor(id);
        long size = Store.Size(id);
        Store.Exclude(id);
        if (!Settings.Exclude.Contains(pattern))
        {
            Settings.Exclude.Add(pattern);
            SaveSettings();
        }
        Unmark([id]); // no longer counted, so no longer something to delete
        JunkSkipped.Add(id);
        RefreshTypes();
        RefreshLive();
        return $"excluded {pattern} · {Hoard.Core.Formatting.Units.Size(size)} no longer counted · remove it in settings (,)";
    }

    public void AddPattern(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0 || Settings.Exclude.Contains(pattern))
        {
            return;
        }
        Settings.Exclude.Add(pattern);
        SaveSettings();
        ShowToast($"added {pattern} · applies from the next scan (R rescans now)");
    }

    public void RemovePattern(int index)
    {
        if (index < 0 || index >= Settings.Exclude.Count)
        {
            return;
        }
        string p = Settings.Exclude[index];
        Settings.Exclude.RemoveAt(index);
        SaveSettings();
        ShowToast($"removed {p} · applies from the next scan (R rescans now)");
    }

    /// <summary>Settings rows: the fixed settings, then one per exclude pattern, then "+ add pattern".</summary>
    public int SettingsRowCount => SettingsItems.All.Length + Settings.Exclude.Count + 1;

    /// <summary>The command palette (<c>:</c>) while it's open.</summary>
    public PaletteBox? Palette { get; set; }

    // ── find (plan.md §7.5) ────────────────────────────────────────────────
    public SearchBox? Search { get; set; }

    /// <summary>Filters the Browse list in place (<c>^f</c> in the finder); null shows everything.</summary>
    public string? ListFilter { get; private set; }

    public void SetListFilter(string? filter)
    {
        ListFilter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        Selected = 0;
        Scroll = 0;
        UserMoved = false;
        Refresh();
    }

    /// <summary>The results for the current query (searching in the background; never blocks).</summary>
    public SearchResult? SearchResults
    {
        get
        {
            if (Search is not { } box)
            {
                return null;
            }
            string key = $"{box.Query}\u0001{box.Kind}\u0001{box.MinSize}";
            if (box.Job is null || box.JobKey != key)
            {
                box.Job?.Cancel();
                box.JobKey = key;
                string query = box.Query;
                SearchKind kind = box.Kind;
                long min = box.MinSize;
                box.Job = new BackgroundJob<SearchResult>(cancel => FuzzySearch.Search(Store, query, kind, min, 500, cancel));
                box.Job.Start();
            }
            box.Job.Poll();
            if (box.Job.Result is { } r)
            {
                box.Last = r;
            }
            return box.Last; // the previous results stay up while the next search runs
        }
    }

    public bool SearchRunning => Search?.Job is { Result: null, Error: null };

    public SearchResult? WaitForSearch()
    {
        _ = SearchResults;
        Search?.Job?.Wait();
        return SearchResults;
    }

    // ── export (plan.md §9.2) ──────────────────────────────────────────────
    public ExportDialog? Export { get; set; }

    /// <summary>Opens the export dialog with a sensible file name on the Desktop (or in the home folder).</summary>
    public void OpenExport()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string desktop = System.IO.Path.Combine(home, "Desktop");
        string dir = Directory.Exists(desktop) ? desktop : home;
        string name = Current == NodeStore.Root ? System.IO.Path.GetFileName(RootPath.TrimEnd('/')) : Store.Name(Current);
        if (string.IsNullOrEmpty(name))
        {
            name = "disk";
        }
        var dlg = new ExportDialog { Scope = Marks.Count > 0 ? ExportScope.Marked : ExportScope.Folder };
        dlg.Path = System.IO.Path.Combine(dir, $"hoard-{name}-{DateTime.Now:yyyy-MM-dd}.{Hoard.Core.Export.Exporter.Extension(dlg.Format)}");
        Export = dlg;
    }

    /// <summary>The tree and every report that's ready, for an export.</summary>
    public Hoard.Core.Export.ExportData ExportData(ExportScope scope)
    {
        IReadOnlyList<int> roots = scope switch
        {
            ExportScope.Everything => [NodeStore.Root],
            ExportScope.Marked when Marks.Count > 0 => [.. Marks.Where(id => !Store.IsDeleted(id)).OrderBy(Store.RelativePath, StringComparer.Ordinal)],
            _ => [Current],
        };
        return new Hoard.Core.Export.ExportData
        {
            Store = Store,
            RootPath = RootPath,
            Roots = roots,
            ScannedAt = Session.Result?.ScannedAt ?? DateTimeOffset.Now,
            ExactSizes = Session.ExactSizes,
            HardLinks = HardLinkDuplicates,
            HardLinkBytes = Session.Result?.HardLinkBytesSaved ?? 0,
            Disk = Disk,
            Types = Types,
            Junk = Junk,
            BigOld = BigOld,
            Empty = Empty,
            Dupes = Dupes?.Report,
            DupFrees = DupFrees,
            Age = Age,
            Now = Now,
        };
    }

    // ── snapshots (plan.md §9.1) ───────────────────────────────────────────
    /// <summary>Where snapshots are kept; null means snapshots are off (tests, or no data folder).</summary>
    public Hoard.Core.Snapshots.SnapshotStore? Snapshots { get; set; }
    private List<Hoard.Core.Snapshots.Snapshot>? _snapList;
    /// <summary>Highlighted row of the snapshot list: 0 is the current scan ("now"), then saved ones newest first.</summary>
    public int SnapSelected { get; set; }
    /// <summary>Rows (as in <see cref="SnapSelected"/>) of the two being compared.</summary>
    public int SnapCompare { get; set; }
    public int SnapBase { get; set; } = 1;
    /// <summary>The right pane (what changed) has the keys, not the list.</summary>
    public bool SnapDiffFocus { get; set; }
    public string SnapFolder { get; private set; } = "";
    public int SnapDiffSelected { get; set; }
    public int SnapDiffScroll { get; set; }
    public bool SnapOnlyGrowth { get; set; }
    /// <summary>The snapshot "del" was pressed on once (a second press deletes it).</summary>
    public string? SnapDeletePending { get; set; }
    private Task<string>? _snapSave;
    private bool _snapQuiet;
    private BackgroundJob<SnapshotPair>? _snapDiff;
    private (string?, string?)? _snapDiffKey;
    private Hoard.Core.Snapshots.Snapshot? _live;

    /// <summary>Saved snapshots of this folder, newest first (headers only; cached until something changes).</summary>
    /// <remarks>The one this run saved is "now" itself, so it's left out until something is deleted.</remarks>
    public List<Hoard.Core.Snapshots.Snapshot> SavedSnapshots => _snapList ??= [.. (Snapshots?.List(RootPath) ?? [])
        .Where(sn => sn.File is null || sn.File != _savedThisRun)];
    private string? _savedThisRun;

    /// <summary>Rows of the list: null is "now" (this scan, live).</summary>
    public List<Hoard.Core.Snapshots.Snapshot?> SnapRows => [null, .. SavedSnapshots];

    /// <summary>Saves the current (finished) scan in the background; the toast says when it's done.</summary>
    public bool SaveSnapshot(bool quiet = false)
    {
        _snapQuiet = quiet;
        if (Snapshots is not { } store || Scanning || _snapSave is { IsCompleted: false })
        {
            return false;
        }
        NodeStore s = Store;
        string root = RootPath;
        DateTimeOffset at = Session.Result?.ScannedAt ?? DateTimeOffset.Now;
        _snapSave = Task.Run(() => store.Save(Hoard.Core.Snapshots.Snapshot.FromStore(s, root, at)));
        return true;
    }

    /// <summary>Call regularly: finishes a background save. True when one just finished.</summary>
    public bool PollSnapshotSave()
    {
        if (_snapSave is not { IsCompleted: true } t)
        {
            return false;
        }
        _snapSave = null;
        _snapList = null;
        if (t.IsCompletedSuccessfully)
        {
            _savedThisRun = t.Result;
        }
        if (t.IsFaulted)
        {
            ShowToast("couldn't save the snapshot: " + t.Exception?.GetBaseException().Message);
        }
        else if (!_snapQuiet)
        {
            ShowToast("snapshot saved · 0 compares them");
        }
        // The base stays the latest saved one before "now".
        SnapBase = Math.Min(Math.Max(1, SnapBase), Math.Max(1, SavedSnapshots.Count));
        return true;
    }

    public void WaitForSnapshotSave()
    {
        try
        {
            _snapSave?.Wait();
        }
        catch (AggregateException)
        {
        }
        PollSnapshotSave();
    }

    public void ForgetSnapshot(Hoard.Core.Snapshots.Snapshot snap)
    {
        Snapshots?.Delete(snap);
        _snapList = null;
        _snapDiffKey = null;
        int rows = SnapRows.Count;
        SnapSelected = Math.Clamp(SnapSelected, 0, rows - 1);
        SnapCompare = Math.Clamp(SnapCompare, 0, rows - 1);
        SnapBase = Math.Clamp(SnapBase, 0, rows - 1);
    }

    /// <summary>The two snapshots being compared, loaded in full; null while loading or when there's nothing to compare.</summary>
    public SnapshotPair? SnapPair
    {
        get
        {
            List<Hoard.Core.Snapshots.Snapshot?> rows = SnapRows;
            if (Scanning || rows.Count < 2 || SnapBase == SnapCompare)
            {
                return null;
            }
            Hoard.Core.Snapshots.Snapshot? b = rows[Math.Clamp(SnapBase, 0, rows.Count - 1)], c = rows[Math.Clamp(SnapCompare, 0, rows.Count - 1)];
            var key = (b?.File, c?.File);
            if (_snapDiff is null || _snapDiffKey != key)
            {
                _snapDiff?.Cancel();
                _snapDiffKey = key;
                NodeStore s = Store;
                string root = RootPath;
                _snapDiff = new BackgroundJob<SnapshotPair>(cancel =>
                {
                    Hoard.Core.Snapshots.Snapshot Full(Hoard.Core.Snapshots.Snapshot? h) => h?.File is { } f
                        ? Hoard.Core.Snapshots.Snapshot.Load(f)
                        : _live ??= Hoard.Core.Snapshots.Snapshot.FromStore(s, root, DateTimeOffset.Now, cancel);
                    return new SnapshotPair(Full(b), Full(c));
                });
                _snapDiff.Start();
            }
            _snapDiff.Poll();
            return _snapDiff.Result;
        }
    }

    public bool SnapLoading => _snapDiff is { Result: null, Error: null } && !Scanning;
    public string? SnapError => _snapDiff?.Error;
    public void WaitForSnapPair()
    {
        _ = SnapPair;
        _snapDiff?.Wait();
    }

    public List<Hoard.Core.Snapshots.FolderChange> SnapChanges() =>
        SnapPair is { } pair
            ? [.. Hoard.Core.Snapshots.SnapshotDiff.Children(pair.Base, pair.Compare, SnapFolder).Where(c => !SnapOnlyGrowth || c.Delta > 0)]
            : [];

    public void SnapEnter(string folder)
    {
        SnapFolder = folder;
        SnapDiffSelected = 0;
        SnapDiffScroll = 0;
    }

    /// <summary>Goes up one folder in the diff; false at the top.</summary>
    public bool SnapUp()
    {
        if (SnapFolder.Length == 0)
        {
            return false;
        }
        int cut = SnapFolder.LastIndexOf('/');
        string from = SnapFolder;
        SnapEnter(cut < 0 ? "" : SnapFolder[..cut]);
        SnapDiffSelected = Math.Max(0, SnapChanges().FindIndex(ch => ch.Path == from));
        return true;
    }

    /// <summary>After a delete the live totals changed: the "now" snapshot is built again.</summary>
    private void ForgetLive()
    {
        _live = null;
        _savedThisRun = null; // the scan changed: what was saved is now an earlier state worth comparing with
        _snapList = null;
        if (_snapDiffKey is { } k && (k.Item1 is null || k.Item2 is null))
        {
            _snapDiffKey = null;
        }
    }

    // ── APFS clones in the totals (plan.md §5.3) ──────────────────────────
    /// <summary>Files sharing blocks with an APFS clone, and the bytes they share.</summary>
    public sealed record CloneInfo(long Files, long SharedBytes);
    private BackgroundJob<CloneInfo>? _clones;

    /// <summary>
    /// Shared blocks are counted once per copy in the totals (the scanner sees each file's full
    /// size), so this measures how much that is, for files of 1 MB or more. Starts after the scan.
    /// </summary>
    public CloneInfo? Clones
    {
        get
        {
            if (Scanning || SharedBlocks is NoSharedBlocks)
            {
                return null;
            }
            if (_clones is null)
            {
                ISharedBlocks blocks = SharedBlocks;
                _clones = new BackgroundJob<CloneInfo>(cancel =>
                {
                    long files = 0, shared = 0;
                    var stack = new Stack<int>([NodeStore.Root]);
                    int n = 0;
                    while (stack.Count > 0)
                    {
                        foreach (int id in Store.Children(stack.Pop()))
                        {
                            if ((++n & 1023) == 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                            }
                            NodeKind k = Store.Kind(id);
                            if (k == NodeKind.Directory)
                            {
                                stack.Push(id);
                            }
                            else if (k == NodeKind.File && Store.Size(id) >= 1_000_000 && blocks.PrivateBytes(FullPath(id)) is long own && own < Store.Size(id))
                            {
                                files++;
                                shared += Store.Size(id) - own;
                            }
                        }
                    }
                    return new CloneInfo(files, shared);
                });
                _clones.Start();
            }
            _clones.Poll();
            return _clones.Result;
        }
    }

    // ── duplicates (plan.md §6.2) ──────────────────────────────────────────
    /// <summary>Duplicates plus how much of each copy is its own (APFS clones share blocks).</summary>
    public sealed record DupResult(DupReport Report, Dictionary<int, long> Private);

    public DupKeep DupKeep { get; private set; } = DupKeep.Oldest;
    public HashSet<int> DupExpanded { get; } = [];
    public int DupSelected { get; set; }
    public int DupScroll { get; set; }
    public DupProgress DupWork { get; private set; } = new();
    /// <summary>How the OS reports shared blocks; replaceable in tests.</summary>
    public ISharedBlocks SharedBlocks { get; set; } = new NoSharedBlocks();
    public long DupMinSize { get; set; } = DuplicateFinder.DefaultMinSize;
    /// <summary>Remembers hashes between runs; null means read everything every time.</summary>
    public HashCache? DupCache { get; set; }
    /// <summary>For the "inside folder" keep rule: copies under this path are the ones kept.</summary>
    public string? DupKeepFolder { get; private set; }
    private BackgroundJob<DupResult>? _dupes;
    private bool _dupesSeen;

    public bool DupesStarted => _dupes is not null;
    public bool DupesRunning => _dupes is { Result: null, Error: null };
    public string? DupesError => _dupes?.Error;

    /// <summary>
    /// Starts looking for duplicates (only when asked: it reads file contents, which takes a while
    /// on a big disk). Returns false while scanning.
    /// </summary>
    public bool StartDupes()
    {
        if (Scanning)
        {
            return false;
        }
        if (_dupes is null)
        {
            var work = DupWork = new DupProgress();
            long min = DupMinSize;
            ISharedBlocks blocks = SharedBlocks;
            _dupes = new BackgroundJob<DupResult>(cancel =>
            {
                DupReport r = DuplicateFinder.Find(Store, FullPath, min, work, cancel: cancel, cache: DupCache);
                var own = new Dictionary<int, long>();
                foreach (int id in r.Groups.SelectMany(g => g.Files))
                {
                    if (blocks.PrivateBytes(FullPath(id)) is long p)
                    {
                        own[id] = p;
                    }
                }
                return new DupResult(r, own);
            });
            _dupes.Start();
        }
        return true;
    }

    public DupResult? Dupes
    {
        get
        {
            if (_dupes is null)
            {
                return null;
            }
            _dupes.Poll();
            if (_dupes.Result is not null && !_dupesSeen)
            {
                _dupesSeen = true;
                // Open the groups that free the most, so the screen shows what's going on at once.
                foreach (int i in DupOrder(_dupes.Result!).Take(2))
                {
                    DupExpanded.Add(i);
                }
            }
            return _dupes.Result;
        }
    }

    public DupResult? WaitForDupes()
    {
        StartDupes();
        _dupes?.Wait();
        return Dupes;
    }

    /// <summary>Copies of a group that still exist.</summary>
    public List<int> DupFiles(DupGroup g) => [.. g.Files.Where(id => !Store.IsDeleted(id) && Store.Kind(id) != NodeKind.Excluded)];

    /// <summary>The copy the keep rule would keep.</summary>
    public int DupKeeper(DupGroup g)
    {
        List<int> files = DupFiles(g);
        if (files.Count == 0)
        {
            return NodeStore.None;
        }
        return DupKeep switch
        {
            DupKeep.Newest => files.OrderByDescending(id => Store.MTime(id)).ThenBy(id => id).First(),
            DupKeep.ShortestPath => files.OrderBy(id => FullPath(id).Length).ThenBy(id => FullPath(id), StringComparer.Ordinal).First(),
            DupKeep.InsideFolder when DupKeepFolder is { } folder =>
                files.Where(id => FullPath(id).StartsWith(folder + "/", StringComparison.Ordinal)).OrderBy(id => Store.MTime(id)).ThenBy(id => id)
                    .DefaultIfEmpty(files.OrderBy(id => Store.MTime(id)).ThenBy(id => id).First()).First(),
            _ => files.OrderBy(id => Store.MTime(id)).ThenBy(id => id).First(),
        };
    }

    public void CycleDupKeep()
    {
        DupKeep = DupKeep switch
        {
            DupKeep.Oldest => DupKeep.Newest,
            DupKeep.Newest => DupKeep.ShortestPath,
            DupKeep.ShortestPath when DupKeepFolder is not null => DupKeep.InsideFolder,
            _ => DupKeep.Oldest,
        };
        ShowToast("keep the " + DupKeepName + " copy · a selects the others in every group");
    }

    public string DupKeepName => DupKeep switch
    {
        DupKeep.Newest => "newest",
        DupKeep.ShortestPath => "shortest-path",
        DupKeep.InsideFolder => "inside " + Rendering.Screens.Shorten(DupKeepFolder ?? "?"),
        _ => "oldest",
    };

    /// <summary>f on a copy: keep the copies in its folder (the rule then applies to every group).</summary>
    public void KeepInside(string folder)
    {
        DupKeepFolder = folder;
        DupKeep = DupKeep.InsideFolder;
        ShowToast("keep the copy inside " + Rendering.Screens.Shorten(folder) + " (where there is one) · a selects the others");
    }

    // Deleting duplicates: every ticked copy is compared with the kept one byte for byte first.
    private Task<(List<int> Ok, List<string> Different)>? _dupVerify;
    public bool DupVerifying => _dupVerify is { IsCompleted: false };

    /// <summary>Starts comparing each ticked copy with the copy its group keeps.</summary>
    public void VerifyDupes(List<int> targets)
    {
        if (Dupes is not { } d)
        {
            return;
        }
        var pairs = new List<(int Copy, string CopyPath, string KeepPath)>();
        foreach (DupGroup g in d.Report.Groups)
        {
            // Compare with a copy that stays: the kept one if it isn't going, else any copy that isn't.
            List<int> files = DupFiles(g);
            int keep = DupKeeper(g);
            int? survivor = !targets.Contains(keep) ? keep : files.Where(f => !targets.Contains(f)).Select(f => (int?)f).FirstOrDefault();
            foreach (int id in files.Where(targets.Contains))
            {
                if (survivor is int other)
                {
                    pairs.Add((id, FullPath(id), FullPath(other)));
                }
            }
        }
        // Marked things that aren't copies in any group go through as they are; a copy with no
        // surviving partner to compare against never does.
        var inGroups = new HashSet<int>(d.Report.Groups.SelectMany(DupFiles));
        var others = targets.Where(id => !inGroups.Contains(id)).ToList();
        _dupVerify = Task.Run(() =>
        {
            var ok = new List<int>(others);
            var different = new List<string>();
            foreach (var (copy, copyPath, keepPath) in pairs)
            {
                if (DuplicateFinder.SameBytes(copyPath, keepPath))
                {
                    ok.Add(copy);
                }
                else
                {
                    different.Add(copyPath);
                }
            }
            return (ok, different);
        });
    }

    /// <summary>The finished comparison, once: what's safe to delete and what turned out different.</summary>
    public (List<int> Ok, List<string> Different)? PollDupVerify()
    {
        if (_dupVerify is not { IsCompleted: true } t)
        {
            return null;
        }
        _dupVerify = null;
        return t.IsCompletedSuccessfully ? t.Result : ([], ["the comparison failed: " + t.Exception?.GetBaseException().Message]);
    }

    public (List<int> Ok, List<string> Different)? WaitForDupVerify()
    {
        try
        {
            _dupVerify?.Wait();
        }
        catch (AggregateException)
        {
        }
        return PollDupVerify();
    }

    /// <summary>Bytes freed by deleting this copy: its private part when it shares blocks with a clone.</summary>
    public long DupFrees(int id) => Dupes?.Private.TryGetValue(id, out long own) == true ? Math.Min(own, Store.Size(id)) : Store.Size(id);

    /// <summary>What deleting every copy but the kept one would free, over all groups.</summary>
    public long DupReclaimable(Func<int, bool>? countIt = null)
    {
        if (Dupes is not { } d)
        {
            return 0;
        }
        long total = 0;
        foreach (DupGroup g in d.Report.Groups)
        {
            int keep = DupKeeper(g);
            total += DupFiles(g).Where(id => id != keep && (countIt?.Invoke(id) ?? true)).Sum(DupFrees);
        }
        return total;
    }

    /// <summary>Marks every copy except the kept one (and unmarks the kept one). Returns how many are marked.</summary>
    public int DupAutoSelect()
    {
        int n = 0;
        if (Dupes is not { } d)
        {
            return 0;
        }
        foreach (DupGroup g in d.Report.Groups)
        {
            int keep = DupKeeper(g);
            foreach (int id in DupFiles(g))
            {
                bool want = id != keep;
                if (IsMarked(id) != want)
                {
                    ToggleMark(id);
                }
                n += want ? 1 : 0;
            }
        }
        return n;
    }

    /// <summary>Groups whose every remaining copy is marked: deleting would lose the file entirely.</summary>
    public List<DupGroup> DupGroupsFullyMarked() =>
        Dupes is { } d ? [.. d.Report.Groups.Where(g => DupFiles(g) is { Count: > 0 } f && f.All(IsMarked))] : [];

    private (DupResult, int[])? _dupOrder;

    /// <summary>
    /// Group indices by what deleting the extra copies would really free (clones free little),
    /// worked out once per result so rows don't jump around while you tick.
    /// </summary>
    private int[] DupOrder(DupResult d)
    {
        if (_dupOrder is { } o && o.Item1 == d)
        {
            return o.Item2;
        }
        int[] order = [.. Enumerable.Range(0, d.Report.Groups.Count)
            .OrderByDescending(i =>
            {
                DupGroup g = d.Report.Groups[i];
                int keep = DupKeeper(g);
                return DupFiles(g).Where(id => id != keep).Sum(DupFrees);
            })
            .ThenBy(i => i)];
        _dupOrder = (d, order);
        return order;
    }

    /// <summary>Visible rows: (group index, file id or -1 for the group line).</summary>
    public List<(int Group, int File)> DupRows()
    {
        var rows = new List<(int, int)>();
        if (Dupes is not { } d)
        {
            return rows;
        }
        foreach (int i in DupOrder(d))
        {
            List<int> files = DupFiles(d.Report.Groups[i]);
            if (files.Count < 2)
            {
                continue; // down to one copy: not a duplicate any more
            }
            rows.Add((i, -1));
            if (DupExpanded.Contains(i))
            {
                rows.AddRange(files.Select(id => (i, id)));
            }
        }
        return rows;
    }

    // ── empty folders and zero-byte files (plan.md §6.4) ───────────────────
    /// <summary><c>.gitkeep</c> / <c>.keep</c> don't stop a folder counting as empty (<c>K</c> toggles).</summary>
    public bool EmptyIgnoreKeep { get; private set; } = true;
    public HashSet<string> EmptyExpanded { get; } = [];
    public int EmptySelected { get; set; }
    public int EmptyScroll { get; set; }

    private BackgroundJob<EmptyReport>? _empty;
    private bool? _emptyKey;
    private (EmptyReport Report, List<(int Top, List<int> Folders)> Groups)? _emptyGroups;

    public EmptyReport? Empty
    {
        get
        {
            if (Scanning)
            {
                return null;
            }
            if (_empty is null || _emptyKey != EmptyIgnoreKeep)
            {
                _empty?.Cancel();
                _emptyKey = EmptyIgnoreKeep;
                bool ignore = EmptyIgnoreKeep;
                _empty = new BackgroundJob<EmptyReport>(cancel => EmptyFinder.Find(Store, ignore, cancel));
                _empty.Start();
            }
            _empty.Poll();
            return _empty.Result;
        }
    }

    public bool EmptyLoading => !Scanning && Empty is null && EmptyError is null;
    public string? EmptyError => _empty?.Error;
    public EmptyReport? WaitForEmpty()
    {
        _ = Empty;
        return _empty?.Wait();
    }

    public void ToggleEmptyKeep()
    {
        EmptyIgnoreKeep = !EmptyIgnoreKeep;
        EmptySelected = 0;
        EmptyScroll = 0;
        ShowToast(EmptyIgnoreKeep ? "folders with only .gitkeep / .keep count as empty" : ".gitkeep / .keep now count as content");
    }

    /// <summary>Empty folders grouped by the top-level entry they're in (the folder itself when it's top-level, under the root).</summary>
    public List<(int Top, List<int> Folders)> EmptyGroups()
    {
        if (Empty is not { } r)
        {
            return [];
        }
        if (_emptyGroups is { } g && g.Report == r)
        {
            return g.Groups;
        }
        var groups = r.Folders
            .GroupBy(id =>
            {
                int top = id;
                while (Store.Parent(top) != NodeStore.Root && Store.Parent(top) != NodeStore.None)
                {
                    top = Store.Parent(top);
                }
                return top == id ? NodeStore.Root : top;
            })
            .Select(grp => (grp.Key, grp.ToList()))
            .OrderByDescending(grp => grp.Item2.Sum(EmptyCount)).ThenBy(grp => Store.Name(grp.Key), StringComparer.OrdinalIgnoreCase)
            .ToList();
        _emptyGroups = (r, groups);
        return groups;
    }

    /// <summary>Empty folders in and including this outermost empty folder.</summary>
    public int EmptyCount(int folder) => Store.Dirs(folder) + 1;

    /// <summary>The visible rows of the Empty view, in order.</summary>
    public List<EmptyRow> EmptyRows()
    {
        var rows = new List<EmptyRow>();
        if (Empty is not { } r)
        {
            return rows;
        }
        foreach (var (top, folders) in EmptyGroups())
        {
            var live = folders.Where(id => !Store.IsDeleted(id)).ToList();
            if (live.Count == 0)
            {
                continue;
            }
            rows.Add(new EmptyRow(EmptyRowKind.FolderGroup, top, -1));
            if (EmptyExpanded.Contains("f:" + top))
            {
                rows.AddRange(live.Select(id => new EmptyRow(EmptyRowKind.Folder, top, id)));
            }
        }
        for (int i = 0; i < r.Zero.Count; i++)
        {
            ZeroGroup grp = r.Zero[i];
            if (grp.Files.All(Store.IsDeleted))
            {
                continue;
            }
            rows.Add(new EmptyRow(EmptyRowKind.ZeroGroup, i, -1));
            if (EmptyExpanded.Contains("z:" + grp.Pattern))
            {
                rows.AddRange(grp.Files.Where(id => !Store.IsDeleted(id)).Select(id => new EmptyRow(EmptyRowKind.ZeroFile, i, id)));
            }
        }
        return rows;
    }

    /// <summary>The nodes a row stands for (a group: all its items that are still there).</summary>
    public List<int> EmptyRowNodes(EmptyRow row)
    {
        if (Empty is not { } r)
        {
            return [];
        }
        return row.Kind switch
        {
            EmptyRowKind.FolderGroup => [.. EmptyGroups().First(g => g.Top == row.Group).Folders.Where(id => !Store.IsDeleted(id))],
            EmptyRowKind.ZeroGroup => [.. r.Zero[row.Group].Files.Where(id => !Store.IsDeleted(id))],
            _ => [row.Item],
        };
    }

    // ── big & old (plan.md §6.3) ───────────────────────────────────────────
    public BigOldOptions BigOldOptions { get; set; } = BigOldOptions.Default;
    public BigOldSort BigOldSort { get; private set; } = BigOldSort.Score;
    public int BigOldSelected { get; set; }
    public int BigOldScroll { get; set; }
    /// <summary>The filter panel (<c>f</c>) is open; <see cref="FilterRow"/> is the highlighted setting.</summary>
    public bool FilterOpen { get; set; }
    public int FilterRow { get; set; }

    public static readonly long[] SizeSteps = [10_000_000, 50_000_000, 100_000_000, 250_000_000, 500_000_000, 1_000_000_000, 5_000_000_000];
    public static readonly int[] AgeSteps = [30, 91, 182, 365, 730, 1095];

    private BackgroundJob<BigOldReport>? _bigOld;
    private BigOldOptions? _bigOldKey;
    private List<int>? _bigOldSorted;
    private (BigOldReport, BigOldSort)? _sortedFor;

    /// <summary>The big & old files for the current filters; null while scanning or searching (never blocks).</summary>
    public BigOldReport? BigOld
    {
        get
        {
            if (Scanning)
            {
                return null;
            }
            if (_bigOld is null || _bigOldKey != BigOldOptions)
            {
                _bigOld?.Cancel();
                _bigOldKey = BigOldOptions;
                BigOldOptions o = BigOldOptions;
                long now = Now;
                _bigOld = new BackgroundJob<BigOldReport>(cancel => Hoard.Core.Analysis.BigOld.Find(Store, o, now, cancel));
                _bigOld.Start();
            }
            _bigOld.Poll();
            return _bigOld.Result;
        }
    }

    public bool BigOldLoading => !Scanning && BigOld is null && BigOldError is null;
    public string? BigOldError => _bigOld?.Error;
    public BigOldReport? WaitForBigOld()
    {
        _ = BigOld;
        return _bigOld?.Wait();
    }

    /// <summary>The matching files that still exist, in the chosen order.</summary>
    public List<int> BigOldItems()
    {
        if (BigOld is not { } r)
        {
            return [];
        }
        if (_bigOldSorted is null || _sortedFor is not { } k || k.Item1 != r || k.Item2 != BigOldSort)
        {
            NodeStore s = Store;
            IEnumerable<int> items = r.Items;
            _bigOldSorted = BigOldSort switch
            {
                BigOldSort.Size => [.. items.OrderByDescending(id => s.Size(id)).ThenBy(id => id)],
                BigOldSort.Age => [.. items.OrderBy(id => Hoard.Core.Analysis.BigOld.Touched(s, id, r.Options.Axis)).ThenBy(id => id)],
                BigOldSort.Path => [.. items.OrderBy(s.RelativePath, StringComparer.OrdinalIgnoreCase)],
                _ => [.. items],
            };
            _sortedFor = (r, BigOldSort);
        }
        return [.. _bigOldSorted.Where(id => !Store.IsDeleted(id))];
    }

    public void CycleBigOldSort()
    {
        BigOldSort = BigOldSort switch
        {
            BigOldSort.Score => BigOldSort.Size,
            BigOldSort.Size => BigOldSort.Age,
            BigOldSort.Age => BigOldSort.Path,
            _ => BigOldSort.Score,
        };
        BigOldSelected = 0;
        BigOldScroll = 0;
    }

    /// <summary>Changes the highlighted filter one step (←→ in the filter panel).</summary>
    public void StepFilter(int delta)
    {
        BigOldOptions o = BigOldOptions;
        BigOldOptions = FilterRow switch
        {
            0 => o with { MinSize = Step(SizeSteps, o.MinSize, delta) },
            1 => o with { MinAgeDays = (int)Step([.. AgeSteps.Select(a => (long)a)], o.MinAgeDays, delta) },
            _ => o with { Axis = o.Axis == AgeAxis.Modified ? AgeAxis.Accessed : AgeAxis.Modified },
        };
        BigOldSelected = 0;
        BigOldScroll = 0;
    }

    private static long Step(long[] steps, long current, int delta)
    {
        int i = Array.IndexOf(steps, current);
        if (i < 0)
        {
            i = Array.FindIndex(steps, v => v >= current);
            i = i < 0 ? steps.Length - 1 : i;
        }
        return steps[Math.Clamp(i + delta, 0, steps.Length - 1)];
    }

    /// <summary>"1 year", "3 months": an age filter in words.</summary>
    public static string AgeWords(int days) => days switch
    {
        < 60 => "1 month",
        < 365 => $"{(int)Math.Round(days / 30.4)} months",
        < 730 => "1 year",
        _ => $"{days / 365} years",
    };

    /// <summary>Big & old bytes that aren't inside junk already (so the Overview doesn't count them twice).</summary>
    public long BigOldBeyondJunk()
    {
        if (BigOld is null)
        {
            return 0;
        }
        var junk = new HashSet<int>(Junk?.Categories.SelectMany(c => c.Items) ?? []);
        long total = 0;
        foreach (int id in BigOldItems())
        {
            bool inJunk = false;
            for (int n = id; n != NodeStore.None; n = Store.Parent(n))
            {
                if (junk.Contains(n))
                {
                    inJunk = true;
                    break;
                }
            }
            if (!inJunk)
            {
                total += Store.Size(id);
            }
        }
        return total;
    }

    /// <summary>Duplicate copies' bytes not already counted as junk or big & old (for the Overview).</summary>
    public long DupesBeyondOthers()
    {
        if (Dupes is null)
        {
            return 0;
        }
        var junk = new HashSet<int>(Junk?.Categories.SelectMany(c => c.Items) ?? []);
        var old = new HashSet<int>(BigOld?.Items ?? []);
        bool Counted(int id)
        {
            if (old.Contains(id))
            {
                return true;
            }
            for (int n = id; n != NodeStore.None; n = Store.Parent(n))
            {
                if (junk.Contains(n))
                {
                    return true;
                }
            }
            return false;
        }
        return DupReclaimable(id => !Counted(id));
    }

    /// <summary>Rows of the Types table in the current grouping.</summary>
    public int TypeRowCount => Types is not { } r ? 0 : TypesByExtension ? r.Extensions.Count : r.Categories.Count;

    /// <summary>Highlighted row of the Overview's top-folders list.</summary>
    public int OverviewSelected { get; set; }
    /// <summary>How many top entries the last Overview frame had room for (kept by the renderer).</summary>
    public int OverviewShown { get; set; } = 1;

    /// <summary>The scanned folder's entries, biggest first (the Overview's "top hogs").</summary>
    public List<int> TopEntries() =>
        [.. Store.Children(NodeStore.Root).OrderByDescending(SizeOf).ThenBy(id => Store.Name(id), StringComparer.Ordinal)];

    private readonly Func<long> _now;
    private List<int> _rows = [];

    public ScanSession Session { get; }
    public NodeStore Store => Session.Store;
    public string RootPath => Session.RootPath;
    /// <summary>True until the scan finishes; sizes and lists keep growing meanwhile.</summary>
    public bool Scanning => !Session.IsComplete;
    /// <summary>Animation frame counter, advanced by the host.</summary>
    public int Tick { get; set; }
    public long ErrorCount => Session.Result?.Errors.Count ?? Session.Progress.Errors;
    public long HardLinkDuplicates => Session.Result?.HardLinkDuplicates ?? 0;
    public TimeSpan Elapsed => Session.Result?.Elapsed ?? Session.Progress.Elapsed;

    public bool IsPending(int id) => (Store.Flags(id) & NodeFlags.Pending) != 0;

    /// <summary>Anything in this folder still being scanned (its totals are still growing).</summary>
    public bool IsGrowing(int id) => Scanning && Store.IsGrowing(id);

    /// <summary>
    /// False until the user moves the selection in this folder. While false, live refreshes keep
    /// the highlight on the top row instead of following whichever item happened to be first.
    /// </summary>
    public bool UserMoved { get; private set; }
    public int Current { get; private set; } = NodeStore.Root;
    /// <summary>Index into <see cref="Rows"/> of the highlighted entry.</summary>
    public int Selected { get; private set; }
    /// <summary>First visible row index (kept by the renderer so the selection stays on screen).</summary>
    public int Scroll { get; set; }
    public SortField Sort { get; private set; } = SortField.Size;
    public bool Descending { get; private set; } = true;
    public bool Apparent { get; private set; }
    public Theme Theme { get; set; }
    public bool HelpOpen { get; set; }
    /// <summary>The "Quit Hoard?" question is showing.</summary>
    public bool QuitConfirmOpen { get; set; }

    /// <summary>The delete confirmation, while it's open.</summary>
    public DeleteDialog? Dialog { get; set; }

    // ── junk report (plan.md §6.1) ─────────────────────────────────────────
    private JunkReport? _junk;
    /// <summary>Rule ids whose items are included in "clean selected". SAFE ones start checked.</summary>
    public HashSet<string> JunkChecked { get; } = [];
    public HashSet<string> JunkExpanded { get; } = [];
    /// <summary>Individual items left out of an otherwise checked category.</summary>
    public HashSet<int> JunkSkipped { get; } = [];
    public int JunkSelected { get; set; }
    public int JunkScroll { get; set; }

    private Task<JunkReport>? _junkTask;
    private CancellationTokenSource _junkCancel = new();
    /// <summary>Rules already seen in a report: SAFE ones are ticked only the first time, so un-ticks survive rescans.</summary>
    private readonly HashSet<string> _junkRulesSeen = [];

    /// <summary>Progress of the background junk search (folders checked so far).</summary>
    public JunkProgress JunkWork { get; } = new();

    /// <summary>True until the junk report is ready (while scanning, or while searching), unless the search failed.</summary>
    public bool JunkLoading => _junk is null && JunkError is null;

    /// <summary>Why the junk search failed, if it did.</summary>
    public string? JunkError => _junkTask is { IsFaulted: true } t ? t.Exception?.GetBaseException().Message ?? "unknown error" : null;

    /// <summary>Starts the junk search in the background once the scan is done (idempotent).</summary>
    public void StartJunk()
    {
        if (_junkTask is not null || Scanning)
        {
            return;
        }
        _junkTask = Task.Run(() => JunkFinder.Find(Store, RootPath, progress: JunkWork, cancel: _junkCancel.Token));
    }

    public void CancelJunk()
    {
        _junkCancel.Cancel();
        _types.Cancel();
        TypeFiles?.Job.Cancel();
        AgeFiles?.Job.Cancel();
        _age?.Cancel();
        _bigOld?.Cancel();
        _empty?.Cancel();
        _dupes?.Cancel();
        _clones?.Cancel();
        _rescan?.Cancel.Cancel();
        Watching?.Dispose();
    }

    /// <summary>Adopts a finished background search. Returns true when the report just became available.</summary>
    public bool PollJunk()
    {
        if (_junk is not null || _junkTask is not { IsCompletedSuccessfully: true } task)
        {
            return false;
        }
        _junk = task.Result;
        foreach (JunkCategory c in _junk.Categories)
        {
            // SAFE rules start ticked the first time they show up; after that the user's choice stands.
            if (_junkRulesSeen.Add(c.Rule.Id) && c.Rule.Safety == Safety.Safe)
            {
                JunkChecked.Add(c.Rule.Id);
            }
        }
        return true;
    }

    /// <summary>Blocks until the report is ready (tests and non-interactive use).</summary>
    public JunkReport? WaitForJunk()
    {
        StartJunk();
        try
        {
            _junkTask?.Wait();
        }
        catch (AggregateException)
        {
            // reported through JunkError
        }
        PollJunk();
        return _junk;
    }

    /// <summary>The report once it's ready; null while scanning or still searching (never blocks).</summary>
    public JunkReport? Junk
    {
        get
        {
            StartJunk();
            PollJunk();
            return _junk;
        }
    }

    /// <summary>Visible junk rows: (category index, item index or -1 for the category line).</summary>
    public List<(int Cat, int Item)> JunkRows()
    {
        var rows = new List<(int, int)>();
        if (Junk is not { } report)
        {
            return rows;
        }
        for (int c = 0; c < report.Categories.Count; c++)
        {
            JunkCategory cat = report.Categories[c];
            if (cat.Count(Store) == 0)
            {
                continue;
            }
            rows.Add((c, -1));
            if (JunkExpanded.Contains(cat.Rule.Id))
            {
                for (int i = 0; i < cat.Items.Count; i++)
                {
                    if (!Store.IsDeleted(cat.Items[i]))
                    {
                        rows.Add((c, i));
                    }
                }
            }
        }
        return rows;
    }

    public bool IsJunkItemIncluded(JunkCategory cat, int node) =>
        JunkChecked.Contains(cat.Rule.Id) && !JunkSkipped.Contains(node);

    /// <summary>Items "clean selected" would delete, and their total.</summary>
    public (List<int> Items, long Size) JunkSelection()
    {
        var items = new List<int>();
        if (Junk is { } report)
        {
            foreach (JunkCategory cat in report.Categories)
            {
                items.AddRange(cat.Items.Where(i => !Store.IsDeleted(i) && IsJunkItemIncluded(cat, i)));
            }
        }
        return (items, items.Sum(i => Store.Size(i)));
    }

    // ── marks (global across folders and views, plan.md §8.1) ──────────────
    private readonly HashSet<int> _marks = [];
    public IReadOnlySet<int> Marks => _marks;
    public bool IsMarked(int id) => _marks.Contains(id);

    public void ToggleMark(int id)
    {
        if (!_marks.Remove(id))
        {
            _marks.Add(id);
        }
    }

    public int MarkAllVisible()
    {
        int added = 0;
        foreach (int id in _rows)
        {
            if (_marks.Add(id))
            {
                added++;
            }
        }
        return added;
    }

    public void ClearMarks() => _marks.Clear();

    public void Unmark(IEnumerable<int> ids)
    {
        foreach (int id in ids)
        {
            _marks.Remove(id);
        }
        _marks.RemoveWhere(Store.IsDeleted);
    }

    /// <summary>Marked items and their total size, counting nested marks once.</summary>
    public (int Count, long Size) MarkedSummary()
    {
        int count = 0;
        long size = 0;
        foreach (int id in _marks)
        {
            if (Store.IsDeleted(id))
            {
                continue;
            }
            bool covered = false;
            for (int n = Store.Parent(id); n != NodeStore.None; n = Store.Parent(n))
            {
                if (_marks.Contains(n))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
            {
                count++;
                size += SizeOf(id);
            }
        }
        return (count, size);
    }

    /// <summary>After a delete: if the open folder (or a parent) is gone, move up to what remains.</summary>
    public void LeaveDeletedFolders()
    {
        int target = Current;
        for (int n = Current; n != NodeStore.None; n = Store.Parent(n))
        {
            if (Store.IsDeleted(n))
            {
                target = Store.Parent(n);
            }
        }
        if (target != Current)
        {
            Enter(target);
        }
        else
        {
            RefreshLive();
        }
    }
    public ViewKind View { get; set; } = ViewKind.Browse;
    /// <summary>Treemap levels drawn (1–3).</summary>
    public int MapDepth { get; private set; } = 2;

    public void ChangeMapDepth(int delta)
    {
        int d = Math.Clamp(MapDepth + delta, 1, 3);
        ShowToast(d == MapDepth ? $"depth stays at {d} (1–3)" : $"treemap depth {d}");
        MapDepth = d;
    }

    /// <summary>Selects an entry of the current folder by node id (from the treemap).</summary>
    public void SelectNode(int id)
    {
        int i = _rows.IndexOf(id);
        if (i >= 0)
        {
            Select(i);
        }
    }
    public string? Toast { get; private set; }
    public long Now => _now();

    public IReadOnlyList<int> Rows => _rows;
    public int? SelectedNode => _rows.Count > 0 ? _rows[Selected] : null;

    public long SizeOf(int id) => Apparent ? Store.Apparent(id) : Store.Size(id);

    public string FullPath(int id)
    {
        string rel = Store.RelativePath(id);
        if (rel.Length == 0)
        {
            return RootPath;
        }
        return RootPath == "/" ? "/" + rel : RootPath + "/" + rel;
    }

    public void ShowToast(string message) => Toast = message;
    public void ClearToast() => Toast = null;

    public void MoveBy(int delta) => Select(Selected + delta);

    /// <summary>Selects a row on the user's behalf.</summary>
    public void Select(int index)
    {
        UserMoved = true;
        Clamp(index);
    }

    private void Clamp(int index) =>
        Selected = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1);

    /// <summary>Opens the selected directory. Returns false (with a hint) if it can't be opened.</summary>
    public bool Open()
    {
        if (SelectedNode is not int id)
        {
            return false;
        }
        switch (Store.Kind(id))
        {
            case NodeKind.Directory when Store.ChildCount(id) > 0 || IsPending(id):
                Enter(id);
                return true;
            case NodeKind.Directory:
                ShowToast((Store.Flags(id) & NodeFlags.Error) != 0 ? "can't read this folder (permission denied)" : "this folder is empty");
                return false;
            case NodeKind.MountPoint:
                ShowToast("on another disk, so not scanned (use --cross-fs)");
                return false;
            case NodeKind.Alias:
                ShowToast("same folder as another path in this scan, so it is counted there");
                return false;
            case NodeKind.Excluded:
                ShowToast("excluded by a pattern, so not scanned");
                return false;
            case NodeKind.Symlink:
                ShowToast("a symlink: hoard doesn't follow links");
                return false;
            default:
                ShowToast("a file, not a folder");
                return false;
        }
    }

    public void Enter(int dir)
    {
        if (dir != Current)
        {
            ListFilter = null;
        }
        Current = dir;
        Selected = 0;
        Scroll = 0;
        UserMoved = false;
        Refresh();
    }

    /// <summary>Goes to the parent folder, keeping the folder we came from selected.</summary>
    public bool Up()
    {
        if (Current == NodeStore.Root)
        {
            return false;
        }
        int from = Current;
        Current = Store.Parent(Current);
        Refresh();
        Select(Math.Max(0, _rows.IndexOf(from))); // keeps following the folder we came from
        Scroll = Math.Max(0, Selected - 3);
        return true;
    }

    public void CycleSort()
    {
        Sort = Sort switch
        {
            SortField.Size => SortField.Name,
            SortField.Name => SortField.Files,
            SortField.Files => SortField.Modified,
            _ => SortField.Size,
        };
        Descending = Sort != SortField.Name;
        ResortKeepingSelection();
    }

    public void ReverseSort()
    {
        Descending = !Descending;
        ResortKeepingSelection();
    }

    public void ToggleSizeMode()
    {
        Apparent = !Apparent;
        ResortKeepingSelection();
        ShowToast(Apparent ? "showing apparent sizes (logical file length)" : "showing on-disk sizes (space actually used)");
    }

    public void NextTheme()
    {
        int i = Array.IndexOf(Theme.All, Theme);
        Theme = Theme.All[(i + 1) % Theme.All.Length];
        Settings.Theme = Theme.Name;
        SaveSettings();
        ShowToast("theme: " + Theme.Name + (SettingsPath is null ? "" : " · saved"));
    }

    /// <summary>Re-reads the current folder (new entries, grown sizes) while keeping the selection.</summary>
    public void RefreshLive() => ResortKeepingSelection();

    private void ResortKeepingSelection()
    {
        int? keep = SelectedNode;
        Refresh();
        if (!UserMoved)
        {
            Clamp(0);
        }
        else if (keep is int k)
        {
            Clamp(Math.Max(0, _rows.IndexOf(k)));
        }
    }

    private void Refresh()
    {
        NodeStore s = Store;
        IEnumerable<int> kids = s.Children(Current);
        if (ListFilter is { } filter)
        {
            string[] words = FuzzySearch.Words(filter);
            kids = kids.Where(id => FuzzySearch.ScoreAll(s.Name(id), words, 0) >= 0);
        }
        IOrderedEnumerable<int> ordered = Sort switch
        {
            SortField.Name => kids.OrderBy(id => s.Name(id), StringComparer.OrdinalIgnoreCase),
            SortField.Files => kids.OrderBy(id => s.Files(id)),
            SortField.Modified => kids.OrderBy(id => s.MTime(id)),
            _ => kids.OrderBy(SizeOf),
        };
        // Ties always break by name so the order is stable.
        ordered = ordered.ThenBy(id => s.Name(id), StringComparer.Ordinal);
        _rows = [.. ordered];
        if (Descending)
        {
            _rows.Reverse();
        }
        Clamp(Selected);
    }
}

/// <summary>A list of the biggest files of something (a type, an age bucket), found in the background.</summary>
public sealed class FileList(string title, long count, BackgroundJob<List<int>> job)
{
    /// <summary>At most this many files are listed (the biggest ones).</summary>
    public const int Limit = 5000;

    public string Title { get; } = title;
    /// <summary>How many files there are in all (the list may hold fewer).</summary>
    public long Count { get; } = count;
    public BackgroundJob<List<int>> Job { get; } = job;
    public int Selected { get; set; }
    public int Scroll { get; set; }

    /// <summary>The files once found, without ones deleted since (null while searching).</summary>
    public List<int>? Items(NodeStore s)
    {
        Job.Poll();
        return Job.Result?.Where(id => !s.IsDeleted(id)).ToList();
    }

    public int? SelectedNode(NodeStore s) => Items(s) is { Count: > 0 } items ? items[Math.Clamp(Selected, 0, items.Count - 1)] : null;
}

public enum EmptyRowKind { FolderGroup, Folder, ZeroGroup, ZeroFile }

/// <summary>A row of the Empty view. Folder rows: Group = top-level entry, Item = folder. Zero rows: Group = group index, Item = file.</summary>
public readonly record struct EmptyRow(EmptyRowKind Kind, int Group, int Item);

/// <summary>Two snapshots loaded in full for a diff.</summary>
public sealed record SnapshotPair(Hoard.Core.Snapshots.Snapshot Base, Hoard.Core.Snapshots.Snapshot Compare);

/// <summary>The finder (<c>/</c>): query, filters, and where the highlight is.</summary>
public sealed class SearchBox
{
    public static readonly long[] SizeSteps = [0, 1_000_000, 100_000_000, 1_000_000_000];
    public string Query { get; set; } = "";
    public SearchKind Kind { get; set; }
    public long MinSize { get; set; }
    public int Selected { get; set; }
    public int Scroll { get; set; }
    internal BackgroundJob<SearchResult>? Job { get; set; }
    internal string? JobKey { get; set; }
    internal SearchResult? Last { get; set; }
}
