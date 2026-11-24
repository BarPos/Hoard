using Hoard.Core.Actions;
using Hoard.Core.Analysis;
using Hoard.Core.Export;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Core.Snapshots;
using Hoard.Platform;
using Hoard.Screenshots;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

// Renders the screenshots for the README and docs (docs/images/*.svg) from real Hoard screens.
// The data is a made-up home folder: real (tiny) files and folders, with sizes and ages supplied
// by a fake probe, so nobody's actual disk ends up in the pictures.
//   dotnet run --project tools/Hoard.Screenshots [-- output-folder]

string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(FindRepo(), "docs", "images"));
Directory.CreateDirectory(outDir);
// /private/tmp has no symlink in its path, so the scanned root and HOME match exactly and show as "~".
string work = Path.Combine("/private/tmp", "hoard-screenshots-" + Guid.NewGuid().ToString("N")[..6]);
string home = Path.Combine(work, "Users", "alex");
Directory.CreateDirectory(home);
// The demo folder plays the user's home, so paths show as "~/…" and ~/Library rules apply.
Environment.SetEnvironmentVariable("HOME", home);
const int W = 118, H = 34;
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var disk = new DiskSpace("Macintosh HD", "/", 994_700_000_000, 182_400_000_000);

try
{
    var probe = new FakeProbe(FileProbes.ForCurrentPlatform());
    // Two earlier states of the folder for the Snapshots trend, then today's.
    // Snapshots where Hoard really keeps them; the demo scans leave that folder out.
    var store = new SnapshotStore(Path.Combine(home, ".local", "share", "hoard", "snapshots"));
    var options = new ScanOptions { Excludes = ["/.local/", "/.config/"] };
    foreach (var (daysAgo, scale) in new[] { (21, 0.93), (14, 0.96), (7, 0.985) })
    {
        Demo.Write(home, probe, now, Demo.Phase.Earlier, scale);
        ScanResult earlier = new Scanner(probe, options).Scan(home);
        store.Save(Snapshot.FromStore(earlier.Store, earlier.RootPath, DateTimeOffset.FromUnixTimeSeconds(now - daysAgo * 86400L)));
    }
    Demo.Write(home, probe, now, Demo.Phase.Now, 1);

    probe.Delay = TimeSpan.FromMilliseconds(0.45); // a real disk takes a moment; "0.0 s" would look fake
    ScanSession session = new Scanner(probe, options).Start(home);
    session.Completion.GetAwaiter().GetResult();
    // Only ever opened for the picture, never confirmed: nothing is moved or deleted.
    var deleter = new Deleter(session.Store, session.RootPath, probe, Trashes.ForCurrentPlatform());
    var m = new BrowserModel(session, Theme.Hoard, () => now, _ => disk)
    {
        View = ViewKind.Overview,
        Snapshots = store,
        Rescanner = (p, c) => new Scanner(probe, options).Scan(p, c),
    };
    m.UseSettings(new Hoard.Core.Config.Settings(), Path.Combine(home, ".config", "hoard", "config.toml"), apparent: false);
    var ctl = new BrowserController(m, new NoShell(), deleter);
    m.WaitForJunk();
    m.WaitForTypes();
    m.WaitForBigOld();
    m.WaitForEmpty();
    m.WaitForDupes();
    m.WaitForAge();
    _ = m.Clones;

    NodeStore s = m.Store;
    int Node(string rel) => Enumerable.Range(0, s.Count).First(id => !s.IsDeleted(id) && s.RelativePath(id) == rel);
    void Shot(string name, string? caption = null, Theme? theme = null)
    {
        Theme t = theme ?? Theme.Hoard;
        Theme before = m.Theme;
        m.Theme = t;
        var c = new MemoryCanvas(W, H);
        Screens.Browser(c, m);
        File.WriteAllText(Path.Combine(outDir, name + ".svg"), SvgWriter.Render(c, t, caption ?? $"alex — hoard — {W}×{H}"));
        m.Theme = before;
        m.ClearToast();
        Console.WriteLine("  " + name + ".svg");
    }
    void Reset()
    {
        m.Search = null;
        m.Palette = null;
        m.Export = null;
        m.SettingsOpen = false;
        m.HelpOpen = false;
        m.Dialog = null;
        m.ClearMarks();
        m.ClearToast();
    }

    Console.WriteLine("Rendering to " + outDir);
    m.View = ViewKind.Overview;
    Shot("overview");
    Shot("theme-neon", theme: Theme.Neon);
    Shot("theme-frost", theme: Theme.Frost);
    Shot("theme-mono", theme: Theme.Mono);

    m.View = ViewKind.Browse;
    m.Enter(NodeStore.Root);
    m.SelectNode(Node("Library"));
    m.ToggleMark(Node(".npm"));
    m.ToggleMark(Node("Downloads"));
    Shot("browse");
    Reset();

    m.Enter(Node("dev"));
    m.SelectNode(Node("dev/monolith"));
    m.View = ViewKind.Map;
    Shot("map");
    m.Enter(NodeStore.Root);

    m.View = ViewKind.Types;
    Shot("types");
    m.View = ViewKind.Age;
    m.AgeSelected = 2;
    m.AgeColumn = 7;
    Shot("age");

    m.View = ViewKind.Dupes;
    ctl.Apply(Cmd.AxisAccessed, H); // a: tick every copy but the kept one
    m.ClearToast();
    Shot("dupes");
    Reset();

    m.View = ViewKind.Junk;
    m.JunkExpanded.Add(m.Junk!.Categories[0].Rule.Id);
    Shot("junk");

    m.View = ViewKind.BigOld;
    m.BigOldSelected = 1;
    Shot("big-old");
    m.ToggleMark(m.BigOldItems()[2]);
    m.ToggleMark(m.BigOldItems()[4]);
    ctl.Apply(Cmd.Delete, H);
    Shot("delete");
    Reset();

    m.View = ViewKind.Empty;
    m.EmptyExpanded.Add("f:" + m.EmptyGroups()[0].Top);
    m.EmptySelected = 1;
    Shot("empty");

    m.View = ViewKind.Snaps;
    m.SnapCompare = 0;
    m.SnapBase = 1;
    m.WaitForSnapPair();
    m.SnapDiffFocus = true;
    Shot("snapshots");
    m.SnapDiffFocus = false;

    m.View = ViewKind.Overview;
    ctl.Apply(Cmd.Find, H);
    foreach (char ch in "nmod")
    {
        ctl.TypeChar(ch);
    }
    m.WaitForSearch();
    Shot("find");
    Reset();

    ctl.Apply(Cmd.Palette, H);
    foreach (char ch in "snap")
    {
        ctl.TypeChar(ch);
    }
    Shot("palette");
    Reset();

    m.View = ViewKind.Browse;
    m.Enter(Node("dev"));
    ctl.Apply(Cmd.Export, H);
    m.Export!.Path = Path.Combine(home, "Desktop", $"hoard-dev-{DateTime.Now:yyyy-MM-dd}.html");
    Shot("export");
    Reset();
    m.Enter(NodeStore.Root);

    m.View = ViewKind.Overview;
    m.SettingsOpen = true;
    Shot("settings");
    Reset();
    m.HelpOpen = true;
    Shot("help");
    Reset();

    // Watch: a few changes "since watching started".
    m.StartWatch();
    WatchSession w = m.Watching!;
    var feed = new (int MinutesAgo, long Delta, string Rel)[]
    {
        (0, 412_000_000, "dev/ml-sandbox/checkpoints/run-043"),
        (1, 38_000_000, "Library/Caches/com.spotify.client/Data"),
        (1, -1_200_000_000, "Downloads"),
        (2, 6_000_000, "dev/monolith/.git/objects/pack"),
        (3, 212_000_000, "Movies/screen-recordings"),
        (4, 3_000_000, ".npm/_cacache/content-v2"),
    };
    foreach (var (ago, delta, rel) in feed)
    {
        int folder = Node(rel);
        w.Feed.Add(new WatchChange(DateTime.Now.AddMinutes(-ago).AddSeconds(-7 * ago), delta, m.FullPath(folder), folder));
        int top = folder;
        while (s.Parent(top) != NodeStore.Root)
        {
            top = s.Parent(top);
        }
        w.Baseline[top] = w.Baseline.GetValueOrDefault(top, s.Size(top)) - delta;
        if (!w.Activity.TryGetValue(top, out var times))
        {
            w.Activity[top] = times = [];
        }
        for (int i = 0; i < 3 + ago; i++)
        {
            times.Add(DateTime.Now.AddSeconds(-i * 13 - ago * 9));
        }
    }
    Shot("watch");
    m.StopWatch();
    m.ClearToast();

    // The splash and the scanning screen need a scan that's still running: hold one folder back.
    probe.Delay = TimeSpan.FromMilliseconds(3); // slow enough to sample the throughput a few times
    var held = new HeldProbe(probe, Path.Combine(home, "Pictures"));
    ScanSession running = new Scanner(held, options).Start(home);
    var live = new BrowserModel(running, Theme.Hoard, () => now, _ => disk) { ScanScreen = true, View = ViewKind.Overview };
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (running.Progress.Files < 4_400 && clock.Elapsed < TimeSpan.FromSeconds(30))
    {
        live.SampleScan(clock.ElapsedMilliseconds);
        Thread.Sleep(50);
    }
    live.Tick = 7;
    var lc = new MemoryCanvas(W, H);
    Screens.Browser(lc, live);
    File.WriteAllText(Path.Combine(outDir, "scanning.svg"), SvgWriter.Render(lc, Theme.Hoard, $"alex — hoard — {W}×{H}"));
    Console.WriteLine("  scanning.svg");
    live.SplashTicks = 19;
    live.Tick = 18;
    lc = new MemoryCanvas(W, H);
    Screens.Browser(lc, live);
    File.WriteAllText(Path.Combine(outDir, "splash.svg"), SvgWriter.Render(lc, Theme.Hoard, $"alex — hoard — {W}×{H}"));
    Console.WriteLine("  splash.svg");
    held.Release();
    running.Completion.GetAwaiter().GetResult();
}
finally
{
    try
    {
        Directory.Delete(work, true);
    }
    catch (IOException)
    {
    }
}
return 0;

static string FindRepo()
{
    string? d = AppContext.BaseDirectory;
    while (d is not null && !File.Exists(Path.Combine(d, "VERSION")))
    {
        d = Path.GetDirectoryName(d);
    }
    return d ?? Directory.GetCurrentDirectory();
}

/// <summary>Shell actions do nothing here.</summary>
internal sealed class NoShell : IShell
{
    public string Reveal(string path) => "";
    public string Copy(string text) => "";
}

/// <summary>Reports sizes and ages from the demo table instead of the (tiny) real files.</summary>
internal sealed class FakeProbe(IFileProbe real) : IFileProbe
{
    public Dictionary<string, (long Size, long MTime)> Files { get; } = new(StringComparer.Ordinal);
    public TimeSpan Delay { get; set; }
    public string Name => "demo";
    public bool IsExact => real.IsExact;

    public bool TryGetInfo(string path, out EntryInfo info, out int errno)
    {
        if (Delay > TimeSpan.Zero)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < Delay)
            {
                Thread.SpinWait(20);
            }
        }
        if (!real.TryGetInfo(path, out info, out errno))
        {
            return false;
        }
        if (info.Type == EntryType.File && Files.TryGetValue(path, out var f))
        {
            long blocks = (f.Size + 4095) / 4096 * 4096;
            info = info with { Apparent = f.Size, Allocated = blocks, MTime = f.MTime, ATime = f.MTime, LinkCount = 1 };
        }
        return true;
    }
}

/// <summary>Holds back one folder so a scan stays "running" long enough to photograph.</summary>
internal sealed class HeldProbe(IFileProbe inner, string held) : IFileProbe
{
    private readonly ManualResetEventSlim _gate = new(false);
    public string Name => inner.Name;
    public bool IsExact => inner.IsExact;
    public void Release() => _gate.Set();

    public bool TryGetInfo(string path, out EntryInfo info, out int errno)
    {
        if (path.StartsWith(held + "/", StringComparison.Ordinal))
        {
            _gate.Wait(TimeSpan.FromSeconds(20));
        }
        return inner.TryGetInfo(path, out info, out errno);
    }
}
