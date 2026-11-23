using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Hoard.Cli;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

// Exit codes (plan.md §10): 0 ok · 1 error · 2 usage · 3 threshold exceeded · 130 interrupted.
const int ExitOk = 0, ExitError = 1, ExitUsage = 2, ExitInterrupted = 130;

var pathArg = new Argument<string>("path") { Description = "Folder to scan", DefaultValueFactory = _ => "." };
var noTui = new Option<bool>("--no-tui") { Description = "Print a summary instead of opening the interactive view" };
var top = new Option<int>("--top", "-n") { Description = "How many of the largest entries to list", DefaultValueFactory = _ => 10 };
var json = new Option<bool>("--json") { Description = "Write the summary as JSON (never coloured)" };
var apparent = new Option<bool>("--apparent") { Description = "Use apparent (logical) sizes instead of on-disk sizes" };
var iec = new Option<bool>("--iec") { Description = "Use powers of 1024 (KiB, MiB, GiB) instead of 1000" };
var exclude = new Option<string[]>("--exclude", "-x") { Description = "gitignore-style pattern to skip (repeatable)", AllowMultipleArgumentsPerToken = false };
var threads = new Option<int>("--threads") { Description = "Scanner threads (0 = automatic)" };
var crossFs = new Option<bool>("--cross-fs") { Description = "Descend into other file systems (default: stay on one)" };
var noColor = new Option<bool>("--no-color") { Description = "Disable colour (also honoured: NO_COLOR)" };
var theme = new Option<string>("--theme") { Description = "Colour theme: hoard, neon, frost or mono", DefaultValueFactory = _ => "hoard" };
var stats = new Option<bool>("--stats") { Description = "Print scanner statistics (nodes, memory, throughput) to stderr" };

var root = new RootCommand("hoard — what's hoarding my disk?")
{
    pathArg, noTui, top, json, apparent, iec, exclude, threads, crossFs, noColor, theme, stats,
};
root.Subcommands.Add(JunkCommand.Create());

root.SetAction(parse =>
{
    var term = new Tty(parse.GetValue(noColor));
    bool asJson = parse.GetValue(json);
    bool useApparent = parse.GetValue(apparent);
    bool useIec = parse.GetValue(iec);
    int topN = Math.Max(0, parse.GetValue(top));

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var scanner = new Scanner(FileProbes.ForCurrentPlatform(), new ScanOptions
    {
        Threads = parse.GetValue(threads),
        Excludes = parse.GetValue(exclude) ?? [],
        CrossFileSystems = parse.GetValue(crossFs),
        AliasPaths = MacFirmlinks.AliasesFor(ResolveRoot(parse.GetValue(pathArg)!)),
    });

    // Interactive terminal → the TUI; --no-tui, --json or redirected input/output → the summary.
    bool tui = !parse.GetValue(noTui) && !asJson && term.Interactive && !Console.IsInputRedirected;
    if (tui)
    {
        string themeName = parse.GetValue(theme) ?? "hoard";
        if (!Hoard.Tui.Rendering.Theme.All.Any(t => t.Name == themeName))
        {
            Console.Error.WriteLine($"hoard: unknown theme '{themeName}' (try hoard, neon, frost or mono)");
            return ExitUsage;
        }
        string target = parse.GetValue(pathArg)!;
        if (!Directory.Exists(target))
        {
            Console.Error.WriteLine(term.Fg(Tty.Bad, "hoard: ") + "Not a directory: " + target);
            return ExitError;
        }
        return Hoard.Tui.TuiApp.Run(target, scanner, Hoard.Tui.Rendering.Theme.Named(themeName));
    }

    long memBefore = parse.GetValue(stats) ? HeapAfterFullGc() : 0;
    ScanResult result;
    using (var spinner = Spinner.Start(scanner.Progress, term, useIec))
    {
        try
        {
            result = scanner.Scan(parse.GetValue(pathArg)!, cts.Token);
        }
        catch (DirectoryNotFoundException ex)
        {
            spinner.Dispose();
            Console.Error.WriteLine(term.Fg(Tty.Bad, "hoard: ") + ex.Message);
            return ExitError;
        }
    }

    if (parse.GetValue(stats))
    {
        long memAfter = HeapAfterFullGc();
        int nodes = result.Store.Count;
        int per = Math.Max(1, nodes);
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"stats: {nodes} nodes · {(memAfter - memBefore) / per} B/node managed heap (arrays {result.Store.AllocatedNodeBytes / per} + names {result.Store.Names.AllocatedBytes / per}) · {result.Elapsed.TotalSeconds:0.000} s · {nodes / Math.Max(0.001, result.Elapsed.TotalSeconds):0} nodes/s · probe {result.ProbeName}"));
    }

    if (asJson)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(BuildJson(result, topN, useApparent), HoardJson.Default.JsonReport));
    }
    else
    {
        PrintSummary(result, topN, useApparent, useIec, term);
    }
    return result.Cancelled ? ExitInterrupted : ExitOk;
});

ParseResult parsed = root.Parse(args);
string? pathValue = parsed.CommandResult.Command == root ? parsed.GetValue(pathArg) : null;
bool unknownOption = pathValue is not null && pathValue.StartsWith('-') && pathValue != "-" && !Directory.Exists(pathValue);
if (parsed.Errors.Count > 0 || unknownOption)
{
    foreach (var error in parsed.Errors)
    {
        Console.Error.WriteLine("hoard: " + error.Message);
    }
    if (unknownOption)
    {
        Console.Error.WriteLine($"hoard: unknown option '{pathValue}'");
    }
    Console.Error.WriteLine("Try 'hoard --help'.");
    return ExitUsage;
}
return parsed.Invoke();

// GC.GetTotalMemory over-reports here; the heap size after a full compacting GC is what a scan really retains.
static long HeapAfterFullGc()
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    return GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes;
}

// The scanner resolves a symlinked root itself; firmlink lookup needs the same resolved path.
static string ResolveRoot(string path)
{
    string full = Path.GetFullPath(path);
    try
    {
        return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
    }
    catch (IOException)
    {
        return full;
    }
}

static long SizeOf(NodeStore s, int id, bool apparent) => apparent ? s.Apparent(id) : s.Size(id);

static string DisplayPath(string full)
{
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (home.Length > 1 && (full == home || full.StartsWith(home + "/", StringComparison.Ordinal)))
    {
        return "~" + full[home.Length..];
    }
    return full;
}

static string Kind(NodeKind k) => k switch
{
    NodeKind.Directory => "directory",
    NodeKind.File => "file",
    NodeKind.Symlink => "symlink",
    NodeKind.Excluded => "excluded",
    NodeKind.MountPoint => "mount-point",
    NodeKind.Alias => "alias",
    _ => "other",
};

static List<int> Largest(NodeStore s, bool apparent) =>
    [.. s.Children(NodeStore.Root).OrderByDescending(id => SizeOf(s, id, apparent)).ThenBy(id => s.Name(id), StringComparer.Ordinal)];

static JsonReport BuildJson(ScanResult r, int topN, bool apparent)
{
    NodeStore s = r.Store;
    List<int> kids = Largest(s, apparent);
    var shown = kids.Take(topN).Select(id => new JsonEntry(
        s.Name(id), r.RootPath.TrimEnd('/') + "/" + s.Name(id), Kind(s.Kind(id)),
        s.Size(id), s.Apparent(id), s.Kind(id) == NodeKind.Directory ? s.Files(id) : 1)).ToList();
    var rest = kids.Skip(topN).ToList();
    return new JsonReport(Hoard.Core.AppInfo.Version, r.RootPath, r.ScannedAt, apparent ? "apparent" : "onDisk", r.ExactSizes, r.ProbeName,
        Math.Round(r.Elapsed.TotalSeconds, 3), s.Size(NodeStore.Root), s.Apparent(NodeStore.Root),
        s.Files(NodeStore.Root), s.Dirs(NodeStore.Root), r.HardLinkDuplicates, r.HardLinkBytesSaved, r.Cancelled,
        shown, rest.Count, rest.Sum(id => SizeOf(s, id, apparent)),
        [.. r.Errors.Take(100).Select(e => new JsonError(e.Path, e.Message))]);
}

static void PrintSummary(ScanResult r, int topN, bool apparent, bool iec, Tty t)
{
    NodeStore s = r.Store;
    long total = SizeOf(s, NodeStore.Root, apparent);
    string mode = apparent ? "apparent size" : r.ExactSizes ? "on-disk size" : "apparent size (exact sizes not supported on this OS yet)";
    string links = r.ExactSizes ? " · hard links once" : "";
    Console.WriteLine(t.Fg(Tty.Muted,
        $"  scanned {Units.Count(s.Files(NodeStore.Root))} files in {r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s · {mode}{links}"));

    List<int> kids = Largest(s, apparent);
    string rootShown = DisplayPath(r.RootPath).TrimEnd('/');
    foreach (int id in kids.Take(topN))
    {
        long size = SizeOf(s, id, apparent);
        double frac = total > 0 ? (double)size / total : 0;
        string name = s.Name(id) + (s.Kind(id) is NodeKind.Directory or NodeKind.MountPoint ? "/" : "");
        string note = s.Kind(id) switch
        {
            NodeKind.Excluded => t.Fg(Tty.Faint, "  ⊘ excluded"),
            NodeKind.MountPoint => t.Fg(Tty.Faint, "  ↧ other file system, not scanned"),
            NodeKind.Alias => t.Fg(Tty.Faint, "  ↺ same folder as another path here, counted once"),
            NodeKind.Symlink => t.Fg(Tty.Faint, "  symlink, not followed"),
            _ => (s.Flags(id) & NodeFlags.Error) != 0 ? t.Fg(Tty.Warn, "  ! partly unreadable") : "",
        };
        string sizeCol = Units.Size(size, iec).PadLeft(9);
        string pct = (frac * 100).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5) + "%";
        Console.WriteLine($"  {t.Bold(t.Fg(Tty.Text, sizeCol))}  {t.Bar(frac, 30)} {t.Fg(Tty.Muted, pct)}  {t.Fg(Tty.Text, rootShown + "/" + name)}{note}");
    }
    List<int> rest = [.. kids.Skip(topN)];
    if (rest.Count > 0)
    {
        long restSize = rest.Sum(id => SizeOf(s, id, apparent));
        Console.WriteLine(t.Fg(Tty.Faint, $"  {"…",9}  {Units.Count(rest.Count)} more, {Units.Size(restSize, iec)}"));
    }
    Console.WriteLine($"  {t.Bold(t.Fg(Tty.Accent, Units.Size(total, iec).PadLeft(9)))}  {t.Fg(Tty.Muted, "total")}  {t.Fg(Tty.Faint, rootShown)}");

    if (r.HardLinkDuplicates > 0)
    {
        Console.WriteLine(t.Fg(Tty.Info, "  ≡ ") + t.Fg(Tty.Muted,
            $"{Units.Count(r.HardLinkDuplicates)} extra hard links counted once ({Units.Size(r.HardLinkBytesSaved, iec)} not double-counted)"));
    }
    if (r.Errors.Count > 0)
    {
        string sample = string.Join(", ", r.Errors.Take(2).Select(e => DisplayPath(e.Path)));
        Console.WriteLine(t.Fg(Tty.Warn, "  ! ") + t.Fg(Tty.Muted,
            $"{Units.Count(r.Errors.Count)} unreadable {(r.Errors.Count == 1 ? "entry" : "entries")} skipped: {sample}{(r.Errors.Count > 2 ? ", …" : "")}"));
    }
    if (r.Cancelled)
    {
        Console.WriteLine(t.Fg(Tty.Warn, "  ! scan interrupted, so these numbers are partial"));
    }
}
