using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hoard.Core.Analysis;
using Hoard.Core.Config;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Core.Snapshots;
using Hoard.Platform;

namespace Hoard.Cli;

/// <summary>
/// The reports without the TUI (plan.md §10): <c>hoard types|old|empty|dupes|snapshot|diff|watch|check [path]</c>.
/// Each one scans, honouring the saved settings (exclude patterns and so on), and prints plain text.
/// </summary>
internal static class ReportCommands
{
    private const int ExitThreshold = 3, ExitInterrupted = 130;

    /// <summary>Ctrl-C, for the whole command: the scan and everything after it.</summary>
    private static readonly Lazy<CancellationTokenSource> Interrupt = new(() =>
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return cts;
    });

    public static IEnumerable<Command> Create()
    {
        yield return Types();
        yield return Old();
        yield return Empty();
        yield return Dupes();
        yield return SnapshotCmd();
        yield return Diff();
        yield return Watch();
        yield return Check();
    }

    private static Argument<string> PathArg() => new("path") { Description = "Folder to scan", DefaultValueFactory = _ => "." };
    private static Option<bool> NoColor() => new("--no-color") { Description = "Disable colour" };

    /// <summary>Scans with the saved settings. Returns null (and the exit code) when it couldn't.</summary>
    private static (ScanResult? Result, int Code, Settings Settings) Scan(string path, Tty term)
    {
        var (settings, _) = SettingsFile.Load(SettingsFile.DefaultPath());
        CancellationToken cancel = Interrupt.Value.Token;
        string full = Path.GetFullPath(path);
        var scanner = new Scanner(FileProbes.ForCurrentPlatform(), new ScanOptions
        {
            Threads = settings.Threads,
            Excludes = settings.Exclude,
            CrossFileSystems = settings.CrossFileSystems,
            AliasPaths = MacFirmlinks.AliasesFor(full),
        });
        try
        {
            ScanResult r;
            using (Spinner.Start(scanner.Progress, term, false))
            {
                r = scanner.Scan(path, cancel);
            }
            if (r.Cancelled)
            {
                Console.Error.WriteLine("hoard: scan interrupted");
                return (null, ExitInterrupted, settings);
            }
            return (r, 0, settings);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine("hoard: " + ex.Message);
            return (null, 1, settings);
        }
    }

    private static string FullPath(ScanResult r, int id)
    {
        string rel = r.Store.RelativePath(id);
        return rel.Length == 0 ? r.RootPath : r.RootPath == "/" ? "/" + rel : r.RootPath + "/" + rel;
    }

    private static string Display(string full)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && (full == home || full.StartsWith(home + "/", StringComparison.Ordinal)) ? "~" + full[home.Length..] : full;
    }

    private static void Row(Tty term, string size, string text, string? note = null) =>
        Console.WriteLine($"  {term.Bold(size.PadLeft(10))}  {text}{(note is null ? "" : "  " + term.Fg(Tty.Muted, note))}");

    private static long ParseSizeOr(string? text, long fallback, string option)
    {
        if (text is null)
        {
            return fallback;
        }
        if (Units.TryParseSize(text, out long v))
        {
            return v;
        }
        throw new ArgumentException($"{option}: '{text}' isn't a size (try 100MB)");
    }

    // ── types ───────────────────────────────────────────────────────────────

    private static Command Types()
    {
        var path = PathArg();
        var noColor = NoColor();
        var cmd = new Command("types", "What the space is made of: video, images, apps, code… and the biggest extensions") { path, noColor };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            var (r, code, _) = Scan(parse.GetValue(path)!, term);
            if (r is null)
            {
                return code;
            }
            TypeReport t = FileTypes.Analyze(r.Store);
            Console.WriteLine(term.Bold($"By type · {Display(r.RootPath)} · {Units.Size(t.TotalBytes)} in files"));
            foreach (TypeTotal c in t.Categories)
            {
                double f = t.TotalBytes > 0 ? (double)c.Bytes / t.TotalBytes : 0;
                Console.WriteLine($"  {FileTypes.Label(c.Category),-16} {Units.Size(c.Bytes),10}  {term.Bar(f, 20)} {Units.Percent(f),6}  {term.Fg(Tty.Muted, Units.Count(c.Files) + " files")}");
            }
            Console.WriteLine();
            Console.WriteLine(term.Bold("Biggest extensions"));
            foreach (ExtensionTotal e in t.Extensions.Take(12))
            {
                Row(term, Units.Size(e.Bytes), e.Extension == FileTypes.NoExtension ? e.Extension : "." + e.Extension, $"{Units.Count(e.Files)} files · {FileTypes.Label(e.Category)}");
            }
            return 0;
        });
        return cmd;
    }

    // ── old ─────────────────────────────────────────────────────────────────

    private static Command Old()
    {
        var path = PathArg();
        var noColor = NoColor();
        var minSize = new Option<string>("--min-size") { Description = "Only files at least this big (default: from settings, 100MB)" };
        var days = new Option<int>("--days") { Description = "Untouched for at least this many days (default: from settings, 365)" };
        var accessed = new Option<bool>("--accessed") { Description = "Count from the last time a file was opened instead of changed" };
        var limit = new Option<int>("--limit", "-n") { Description = "How many to list", DefaultValueFactory = _ => 30 };
        var cmd = new Command("old", "Big files nobody has touched for a long time") { path, noColor, minSize, days, accessed, limit };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            var (r, code, settings) = Scan(parse.GetValue(path)!, term);
            if (r is null)
            {
                return code;
            }
            BigOldOptions o = settings.BigOld;
            try
            {
                o = o with { MinSize = ParseSizeOr(parse.GetValue(minSize), o.MinSize, "--min-size") };
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("hoard: " + e.Message);
                return 2;
            }
            if (parse.GetResult(days) is not null)
            {
                o = o with { MinAgeDays = Math.Max(0, parse.GetValue(days)) };
            }
            if (parse.GetValue(accessed))
            {
                o = o with { Axis = AgeAxis.Accessed };
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BigOldReport report = BigOld.Find(r.Store, o, now);
            Console.WriteLine(term.Bold($"Big & old · ≥ {Units.Size(o.MinSize)}, not {(o.Axis == AgeAxis.Accessed ? "opened" : "changed")} for {o.MinAgeDays} days · {report.Items.Count} files · {Units.Size(report.TotalBytes)}"));
            foreach (int id in report.Items.Take(Math.Max(0, parse.GetValue(limit))))
            {
                long touched = BigOld.Touched(r.Store, id, o.Axis);
                Row(term, Units.Size(r.Store.Size(id)), Display(FullPath(r, id)), DateTimeOffset.FromUnixTimeSeconds(touched).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            if (report.AccessTimesLookUnused && o.Axis == AgeAxis.Accessed)
            {
                Console.WriteLine(term.Fg(Tty.Warn, "  note: access times look unused on this disk; “opened” falls back to changed."));
            }
            return 0;
        });
        return cmd;
    }

    // ── empty ───────────────────────────────────────────────────────────────

    private static Command Empty()
    {
        var path = PathArg();
        var noColor = NoColor();
        var limit = new Option<int>("--limit", "-n") { Description = "How many folders to list", DefaultValueFactory = _ => 30 };
        var keep = new Option<bool>("--keep-files-count") { Description = "Folders with only .gitkeep / .keep count as not empty" };
        var cmd = new Command("empty", "Empty folders and zero-byte files (installed software and app bundles are left out)") { path, noColor, limit, keep };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            var (r, code, _) = Scan(parse.GetValue(path)!, term);
            if (r is null)
            {
                return code;
            }
            EmptyReport e = EmptyFinder.Find(r.Store, !parse.GetValue(keep));
            Console.WriteLine(term.Bold($"Empty folders · {Units.Count(e.TotalFolders)} (outermost {e.Folders.Count})"));
            foreach (int id in e.Folders.Take(Math.Max(0, parse.GetValue(limit))))
            {
                int inside = r.Store.Dirs(id);
                Console.WriteLine($"  {Display(FullPath(r, id))}/" + (inside > 0 ? term.Fg(Tty.Muted, $"  + {inside} inside") : ""));
            }
            Console.WriteLine();
            Console.WriteLine(term.Bold($"Zero-byte files · {Units.Count(e.ZeroFileCount)}"));
            foreach (ZeroGroup g in e.Zero.Take(15))
            {
                string badge = g.Safety switch { ZeroSafety.Safe => term.Fg(Tty.Ok, "SAFE"), ZeroSafety.Keep => term.Fg(Tty.Faint, "KEEP"), _ => term.Fg(Tty.Warn, "CAREFUL") };
                Console.WriteLine($"  {g.Pattern,-22} {Units.Count(g.Files.Count),8}  {badge}  {term.Fg(Tty.Muted, g.Where)}");
            }
            return 0;
        });
        return cmd;
    }

    // ── dupes ───────────────────────────────────────────────────────────────

    private static Command Dupes()
    {
        var path = PathArg();
        var noColor = NoColor();
        var minSize = new Option<string>("--min-size") { Description = "Ignore files smaller than this (default 1MB)" };
        var limit = new Option<int>("--limit", "-n") { Description = "How many groups to list", DefaultValueFactory = _ => 20 };
        var cmd = new Command("dupes", "Files with identical contents (reads the files; APFS clones are recognised)") { path, noColor, minSize, limit };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            long min;
            try
            {
                min = ParseSizeOr(parse.GetValue(minSize), DuplicateFinder.DefaultMinSize, "--min-size");
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("hoard: " + e.Message);
                return 2;
            }
            var (r, code, _) = Scan(parse.GetValue(path)!, term);
            if (r is null)
            {
                return code;
            }
            DupReport d;
            try
            {
                d = DuplicateFinder.Find(r.Store, id => FullPath(r, id), min, cache: new HashCache(HashCache.DefaultFile()), cancel: Interrupt.Value.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("hoard: interrupted");
                return ExitInterrupted;
            }
            ISharedBlocks blocks = SharedBlocks.ForCurrentPlatform();
            long Frees(int id) => Math.Min(blocks.PrivateBytes(FullPath(r, id)) ?? r.Store.Size(id), r.Store.Size(id));
            var groups = d.Groups.Select(g => (g, frees: g.Files.Skip(1).Sum(Frees))).OrderByDescending(x => x.frees).ToList();
            Console.WriteLine(term.Bold($"Duplicates · {groups.Count} group{(groups.Count == 1 ? "" : "s")} · {Units.Size(groups.Sum(x => x.frees))} could be freed (keeping one copy each)"));
            foreach (var (g, frees) in groups.Take(Math.Max(0, parse.GetValue(limit))))
            {
                Console.WriteLine();
                Console.WriteLine($"  {term.Bold(r.Store.Name(g.Files[0]))}  {term.Fg(Tty.Muted, $"{Units.Size(g.Size)} × {g.Files.Count} · frees {Units.Size(frees)}")}");
                foreach (int id in g.Files)
                {
                    Console.WriteLine("    " + Display(FullPath(r, id)));
                }
            }
            return 0;
        });
        return cmd;
    }

    // ── snapshot & diff ─────────────────────────────────────────────────────

    private static Command SnapshotCmd()
    {
        var path = PathArg();
        var noColor = NoColor();
        var cmd = new Command("snapshot", "Scan and save a snapshot (folder totals) for later diffs") { path, noColor };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            var (r, code, settings) = Scan(parse.GetValue(path)!, term);
            if (r is null)
            {
                return code;
            }
            var store = new SnapshotStore(SnapshotStore.DefaultDirectory(), settings.KeepSnapshots);
            string file = store.Save(Snapshot.FromStore(r.Store, r.RootPath, r.ScannedAt));
            Console.WriteLine($"hoard: saved {Display(file)} · {Units.Size(r.Store.Size(NodeStore.Root))} · {store.List(r.RootPath).Count} snapshots of {Display(r.RootPath)}");
            return 0;
        });
        return cmd;
    }

    private static Command Diff()
    {
        var @base = new Argument<string>("base") { Description = "Snapshot to compare from: last, a date (2026-11-16), or a number (1 = newest)", DefaultValueFactory = _ => "last" };
        var compare = new Argument<string>("compare") { Description = "Snapshot to compare to, or now (scan again)", DefaultValueFactory = _ => "now" };
        var path = new Option<string>("--path") { Description = "Folder the snapshots are of", DefaultValueFactory = _ => "." };
        var limit = new Option<int>("--limit", "-n") { Description = "How many changes to list", DefaultValueFactory = _ => 15 };
        var noColor = NoColor();
        var cmd = new Command("diff", "What grew and shrank between two snapshots (or since one)") { @base, compare, path, limit, noColor };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            string root = Path.GetFullPath(parse.GetValue(path)!).TrimEnd('/');
            if (root.Length == 0)
            {
                root = "/";
            }
            var (settings, _) = SettingsFile.Load(SettingsFile.DefaultPath());
            var store = new SnapshotStore(SnapshotStore.DefaultDirectory(), settings.KeepSnapshots);
            List<Snapshot> saved = store.List(root);
            Snapshot? Pick(string spec)
            {
                if (spec is "last" or "latest")
                {
                    return saved.FirstOrDefault();
                }
                if (int.TryParse(spec, out int n) && n >= 1 && n <= saved.Count)
                {
                    return saved[n - 1];
                }
                return saved.FirstOrDefault(s => s.TakenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture).StartsWith(spec, StringComparison.Ordinal));
            }
            if (saved.Count == 0)
            {
                Console.Error.WriteLine($"hoard: no snapshots of {Display(root)} yet (hoard snapshot {Display(root)} saves one)");
                return 1;
            }
            Snapshot? b = Pick(parse.GetValue(@base)!);
            if (b is null)
            {
                Console.Error.WriteLine($"hoard: no snapshot matches '{parse.GetValue(@base)}' ({saved.Count} saved; try last, 1, 2 or a date)");
                return 2;
            }
            Snapshot c;
            if (parse.GetValue(compare) is "now")
            {
                var (r, code, _) = Scan(root, term);
                if (r is null)
                {
                    return code;
                }
                c = Snapshot.FromStore(r.Store, r.RootPath, DateTimeOffset.Now);
            }
            else if (Pick(parse.GetValue(compare)!) is { } other)
            {
                c = Snapshot.Load(other.File!);
            }
            else
            {
                Console.Error.WriteLine($"hoard: no snapshot matches '{parse.GetValue(compare)}'");
                return 2;
            }
            Snapshot full = Snapshot.Load(b.File!);
            long net = c.Root.Size - full.Root.Size;
            string when(Snapshot s) => s.File is null ? "now" : s.TakenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Console.WriteLine(term.Bold($"{Display(root)} · {when(full)} → {when(c)} · net {(net >= 0 ? "+" : "−")}{Units.Size(Math.Abs(net))}"));
            // The biggest changes two levels deep, so it says where, not just "Library".
            var changes = SnapshotDiff.Children(full, c, "")
                .SelectMany(ch => ch.Path.Length > 0 && !ch.Added && !ch.Removed
                    ? SnapshotDiff.Children(full, c, ch.Path).DefaultIfEmpty(ch).Select(x => x!)
                    : [ch])
                .OrderByDescending(ch => Math.Abs(ch.Delta)).Take(Math.Max(0, parse.GetValue(limit)));
            if (!changes.Any())
            {
                Console.WriteLine("  nothing changed");
            }
            foreach (FolderChange ch in changes)
            {
                string delta = (ch.Delta >= 0 ? "+" : "−") + Units.Size(Math.Abs(ch.Delta));
                string where = ch.Path.Length == 0 ? ch.Name : Display(root == "/" ? "/" + ch.Path : root + "/" + ch.Path);
                Console.WriteLine($"  {term.Fg(ch.Delta > 0 ? Tty.Bad : Tty.Ok, delta.PadLeft(10))}  {where}{(ch.Added ? term.Fg(Tty.Warn, "  new") : ch.Removed ? term.Fg(Tty.Info, "  gone") : "")}");
            }
            return 0;
        });
        return cmd;
    }

    // ── watch ───────────────────────────────────────────────────────────────

    private static Command Watch()
    {
        var path = PathArg();
        var log = new Option<bool>("--log") { Description = "Print one JSON object per change (for other tools)" };
        var cmd = new Command("watch", "Print changes as they happen, until Ctrl-C") { path, log };
        cmd.SetAction(parse =>
        {
            string root = Path.GetFullPath(parse.GetValue(path)!);
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine("hoard: Not a directory: " + root);
                return 1;
            }
            bool json = parse.GetValue(log);
            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            using var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
            var gate = new object();
            void Emit(string kind, string full)
            {
                long size = -1;
                try
                {
                    if (File.Exists(full))
                    {
                        size = new FileInfo(full).Length;
                    }
                }
                catch (IOException)
                {
                }
                lock (gate)
                {
                    if (json)
                    {
                        using var stream = Console.OpenStandardOutput();
                        using (var jw = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                        {
                            jw.WriteStartObject();
                            jw.WriteString("time", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                            jw.WriteString("event", kind);
                            jw.WriteString("path", full);
                            if (size >= 0)
                            {
                                jw.WriteNumber("size", size);
                            }
                            jw.WriteEndObject();
                        }
                        stream.Write("\n"u8);
                    }
                    else
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {kind,-8} {(size >= 0 ? Units.Size(size) : ""),10}  {Display(full)}");
                    }
                }
            }
            w.Created += (_, e) => Emit("created", e.FullPath);
            w.Changed += (_, e) => Emit("changed", e.FullPath);
            w.Deleted += (_, e) => Emit("deleted", e.FullPath);
            w.Renamed += (_, e) => Emit("renamed", e.FullPath);
            w.Error += (_, e) => Console.Error.WriteLine("hoard: watcher: " + e.GetException().Message);
            w.EnableRaisingEvents = true;
            if (!json)
            {
                Console.Error.WriteLine($"hoard: watching {Display(root)} · Ctrl-C stops");
            }
            done.Wait();
            return 0;
        });
        return cmd;
    }

    // ── check ───────────────────────────────────────────────────────────────

    private static Command Check()
    {
        var path = PathArg();
        var above = new Option<string>("--fail-above") { Description = "Exit 3 when over this: a disk percentage (90%) or a size for the folder (50GB)", Required = true };
        var cmd = new Command("check", "For scripts and cron: exit 3 when a disk is too full or a folder too big") { path, above };
        cmd.SetAction(parse =>
        {
            string limit = parse.GetValue(above)!.Trim();
            string target = parse.GetValue(path)!;
            if (limit.EndsWith('%'))
            {
                if (!double.TryParse(limit[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) || pct is < 0 or > 100)
                {
                    Console.Error.WriteLine($"hoard: --fail-above: '{limit}' isn't a percentage");
                    return 2;
                }
                if (DiskSpace.For(Path.GetFullPath(target)) is not { } disk)
                {
                    Console.Error.WriteLine("hoard: couldn't read the disk's size");
                    return 1;
                }
                double used = 100.0 * disk.Used / Math.Max(1, disk.Total);
                bool over = used > pct;
                Console.WriteLine($"{(over ? "OVER" : "ok")}: {disk.Name} is {used.ToString("0.0", CultureInfo.InvariantCulture)}% full ({Units.Size(disk.Used)} of {Units.Size(disk.Total)}), limit {limit}");
                return over ? ExitThreshold : 0;
            }
            if (!Units.TryParseSize(limit, out long bytes))
            {
                Console.Error.WriteLine($"hoard: --fail-above: '{limit}' isn't a size or a percentage (try 50GB or 90%)");
                return 2;
            }
            var (r, code, _) = Scan(target, new Tty(true));
            if (r is null)
            {
                return code;
            }
            long size = r.Store.Size(NodeStore.Root);
            bool tooBig = size > bytes;
            Console.WriteLine($"{(tooBig ? "OVER" : "ok")}: {Display(r.RootPath)} is {Units.Size(size)}, limit {Units.Size(bytes)}");
            return tooBig ? ExitThreshold : 0;
        });
        return cmd;
    }
}
