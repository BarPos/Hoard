using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Cli;

internal sealed record JunkJson(string Root, long Total, long Safe, IReadOnlyList<JunkCategoryJson> Categories);
internal sealed record JunkCategoryJson(string Id, string Name, string Safety, string Why, string? Command, int Count, long Bytes, IReadOnlyList<JunkItemJson> Items);
internal sealed record JunkItemJson(string Path, long Bytes);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JunkJson))]
internal sealed partial class JunkJsonContext : JsonSerializerContext;

/// <summary><c>hoard junk [path]</c>: the known-junk report without the TUI (plan.md §10).</summary>
internal static class JunkCommand
{
    public static Command Create()
    {
        var path = new Argument<string>("path") { Description = "Folder to scan", DefaultValueFactory = _ => "." };
        var json = new Option<bool>("--json") { Description = "Write the report as JSON" };
        var items = new Option<int>("--items") { Description = "Items listed per category (JSON: up to this many; text: 0 = none)", DefaultValueFactory = _ => 3 };
        var iec = new Option<bool>("--iec") { Description = "Use powers of 1024 (KiB, MiB, GiB)" };
        var noColor = new Option<bool>("--no-color") { Description = "Disable colour" };
        var exclude = new Option<string[]>("--exclude", "-x") { Description = "gitignore-style pattern to skip (repeatable)" };
        var cmd = new Command("junk", "List known junk: build outputs, dependency folders, caches, logs, old installers")
        {
            path, json, items, iec, noColor, exclude,
        };
        cmd.SetAction(parse =>
        {
            var term = new Tty(parse.GetValue(noColor));
            bool useIec = parse.GetValue(iec);
            int perCategory = Math.Max(0, parse.GetValue(items));
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var scanner = new Scanner(FileProbes.ForCurrentPlatform(), new ScanOptions { Excludes = parse.GetValue(exclude) ?? [] });
            ScanResult r;
            try
            {
                r = scanner.Scan(parse.GetValue(path)!, cts.Token);
            }
            catch (DirectoryNotFoundException ex)
            {
                Console.Error.WriteLine("hoard: " + ex.Message);
                return 1;
            }
            if (r.Cancelled)
            {
                Console.Error.WriteLine("hoard: scan interrupted");
                return 130;
            }
            NodeStore s = r.Store;
            JunkReport report = JunkFinder.Find(s, r.RootPath);
            string PathOf(int id) => r.RootPath == "/" ? "/" + s.RelativePath(id) : r.RootPath + "/" + s.RelativePath(id);

            if (parse.GetValue(json))
            {
                var cats = report.Categories.Select(c => new JunkCategoryJson(c.Rule.Id, c.Rule.Name, c.Rule.Safety.ToString().ToLowerInvariant(),
                    c.Rule.Why, c.Rule.Command, c.Count(s), c.Size(s),
                    [.. c.Items.Take(perCategory).Select(i => new JunkItemJson(PathOf(i), s.Size(i)))])).ToList();
                Console.Out.WriteLine(JsonSerializer.Serialize(new JunkJson(r.RootPath, report.Total(s), report.SafeTotal(s), cats), JunkJsonContext.Default.JunkJson));
                return 0;
            }

            string root = Display(r.RootPath);
            Console.WriteLine(term.Fg(Tty.Muted, $"  scanned {Units.Count(s.Files(NodeStore.Root))} files in {r.Elapsed.TotalSeconds:0.0} s · known junk in {root}"));
            if (report.Categories.Count == 0)
            {
                Console.WriteLine(term.Fg(Tty.Ok, "  ✓ no known junk found"));
                return 0;
            }
            long total = report.Total(s);
            foreach (JunkCategory c in report.Categories)
            {
                bool safe = c.Rule.Safety == Safety.Safe;
                string badge = safe ? term.Fg(Tty.Ok, "SAFE  ") : term.Fg(Tty.Warn, "REVIEW");
                string count = $"{Units.Count(c.Count(s))} item{(c.Count(s) == 1 ? "" : "s")}".PadLeft(11);
                Console.WriteLine($"  {badge}  {term.Bold(term.Fg(Tty.Text, Units.Size(c.Size(s), useIec).PadLeft(9)))}  {term.Bar((double)c.Size(s) / Math.Max(1, total), 16)}  " +
                                  $"{term.Fg(Tty.Text, c.Rule.Name.PadRight(30))}{term.Fg(Tty.Faint, count)}  {term.Fg(Tty.Muted, c.Rule.Why)}");
                foreach (int i in c.Items.Take(perCategory))
                {
                    Console.WriteLine(term.Fg(Tty.Faint, $"            {Units.Size(s.Size(i), useIec),9}  {Display(PathOf(i))}"));
                }
                if (c.Rule.Command is { } command)
                {
                    Console.WriteLine(term.Fg(Tty.Info, $"            tip: {command}"));
                }
            }
            Console.WriteLine($"  {term.Bold(term.Fg(Tty.Accent, Units.Size(total, useIec).PadLeft(15)))}  {term.Fg(Tty.Muted, $"total · {Units.Size(report.SafeTotal(s), useIec)} safe · {Units.Size(total - report.SafeTotal(s), useIec)} to review")}");
            Console.WriteLine(term.Fg(Tty.Faint, "  to clean up interactively: hoard " + root + "  then press J"));
            return 0;
        });
        return cmd;
    }

    private static string Display(string full)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && (full == home || full.StartsWith(home + "/", StringComparison.Ordinal)) ? "~" + full[home.Length..] : full;
    }
}
