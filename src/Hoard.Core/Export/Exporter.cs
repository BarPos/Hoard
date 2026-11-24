using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;

namespace Hoard.Core.Export;

public enum ExportFormat { Json, Csv, Html, PrettyHtml, Markdown, Ncdu }

/// <summary>What to write (plan.md §9.2). Depth 0 means the whole tree.</summary>
public sealed record ExportOptions(ExportFormat Format, int Depth = 0, long MinSize = 0, bool IncludeFiles = true, bool Apparent = false);

/// <summary>Everything an export can draw on: the tree, the reports that are ready, and the disk.</summary>
public sealed class ExportData
{
    public required NodeStore Store { get; init; }
    public required string RootPath { get; init; }
    /// <summary>What to export: one folder (usually), or several marked items.</summary>
    public required IReadOnlyList<int> Roots { get; init; }
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    public bool ExactSizes { get; init; } = true;
    public long HardLinks { get; init; }
    public long HardLinkBytes { get; init; }
    public DiskSpace? Disk { get; init; }
    public TypeReport? Types { get; init; }
    public JunkReport? Junk { get; init; }
    public BigOldReport? BigOld { get; init; }
    public EmptyReport? Empty { get; init; }
    public DupReport? Dupes { get; init; }
    /// <summary>Bytes freed by deleting a duplicate copy (its private part for APFS clones).</summary>
    public Func<int, long>? DupFrees { get; init; }
    public AgeReport? Age { get; init; }
    public long Now { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public string FullPath(int id)
    {
        string rel = Store.RelativePath(id);
        return rel.Length == 0 ? RootPath : RootPath == "/" ? "/" + rel : RootPath + "/" + rel;
    }
}

/// <summary>Writes a scan as JSON, CSV, plain HTML, pretty HTML, Markdown or ncdu JSON.</summary>
public static class Exporter
{
    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Html or ExportFormat.PrettyHtml => "html",
        ExportFormat.Markdown => "md",
        _ => "json",
    };

    public static string Describe(ExportFormat f) => f switch
    {
        ExportFormat.Json => "full tree, for tools",
        ExportFormat.Csv => "one row per entry, for spreadsheets",
        ExportFormat.Html => "plain HTML, no CSS or JavaScript",
        ExportFormat.PrettyHtml => "looks like Hoard, in a browser",
        ExportFormat.Markdown => "a summary for tickets and PRs",
        _ => "for ncdu and tools that read its format",
    };

    public static string Name(ExportFormat f) => f switch
    {
        ExportFormat.PrettyHtml => "Pretty HTML",
        ExportFormat.Html => "HTML",
        ExportFormat.Ncdu => "ncdu JSON",
        ExportFormat.Csv => "CSV",
        ExportFormat.Json => "JSON",
        _ => "Markdown",
    };

    public static void Write(ExportData d, ExportOptions o, Stream output)
    {
        switch (o.Format)
        {
            case ExportFormat.Json: WriteJson(d, o, output); break;
            case ExportFormat.Ncdu: WriteNcdu(d, o, output); break;
            default:
            {
                using var w = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                switch (o.Format)
                {
                    case ExportFormat.Csv: WriteCsv(d, o, w); break;
                    case ExportFormat.Markdown: WriteMarkdown(d, o, w); break;
                    case ExportFormat.Html: WriteHtml(d, o, w); break;
                    default: WritePretty(d, o, w); break;
                }
                break;
            }
        }
    }

    public static string WriteToString(ExportData d, ExportOptions o)
    {
        using var ms = new MemoryStream();
        Write(d, o, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── shared walking ──────────────────────────────────────────────────────

    private static long SizeOf(ExportData d, ExportOptions o, int id) => o.Apparent ? d.Store.Apparent(id) : d.Store.Size(id);

    /// <summary>Children to write: biggest first, files dropped if asked, small ones folded into a remainder.</summary>
    private static (List<int> Shown, int OtherCount, long OtherSize) Kids(ExportData d, ExportOptions o, int id)
    {
        var shown = new List<int>();
        int other = 0;
        long otherSize = 0;
        foreach (int c in d.Store.Children(id).OrderByDescending(c => SizeOf(d, o, c)).ThenBy(c => d.Store.Name(c), StringComparer.Ordinal))
        {
            bool isFile = d.Store.Kind(c) != NodeKind.Directory;
            if ((isFile && !o.IncludeFiles) || SizeOf(d, o, c) < o.MinSize)
            {
                other++;
                otherSize += SizeOf(d, o, c);
                continue;
            }
            shown.Add(c);
        }
        return (shown, other, otherSize);
    }

    private static string Kind(NodeKind k) => k switch
    {
        NodeKind.Directory => "dir",
        NodeKind.File => "file",
        NodeKind.Symlink => "symlink",
        NodeKind.MountPoint => "mountpoint",
        NodeKind.Alias => "alias",
        NodeKind.Excluded => "excluded",
        _ => "other",
    };

    private static string Iso(uint unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    // ── JSON ────────────────────────────────────────────────────────────────

    private static void WriteJson(ExportData d, ExportOptions o, Stream output)
    {
        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        NodeStore s = d.Store;
        w.WriteStartObject();
        w.WriteString("hoard", AppInfo.Version);
        w.WriteNumber("schema", 1);
        w.WriteString("root", d.RootPath);
        w.WriteString("scannedAt", d.ScannedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        w.WriteString("sizeMode", o.Apparent ? "apparent" : d.ExactSizes ? "onDisk" : "apparentFallback");
        w.WriteNumber("depth", o.Depth);
        w.WriteNumber("minSize", o.MinSize);
        w.WriteNumber("hardLinksCountedOnce", d.HardLinks);
        w.WritePropertyName("items");
        w.WriteStartArray();
        foreach (int root in d.Roots)
        {
            Node(root, 0);
        }
        w.WriteEndArray();
        w.WriteEndObject();

        void Node(int id, int level)
        {
            w.WriteStartObject();
            w.WriteString("name", id == NodeStore.Root ? d.RootPath : s.Name(id));
            w.WriteString("path", d.FullPath(id));
            w.WriteString("kind", Kind(s.Kind(id)));
            w.WriteNumber("size", s.Size(id));
            w.WriteNumber("apparent", s.Apparent(id));
            if (s.Kind(id) == NodeKind.Directory)
            {
                w.WriteNumber("files", s.Files(id));
                w.WriteNumber("dirs", s.Dirs(id));
            }
            w.WriteString("modified", Iso(s.MTime(id)));
            if ((s.Flags(id) & NodeFlags.HardLinkDuplicate) != 0)
            {
                w.WriteBoolean("hardLinkCountedElsewhere", true);
            }
            if (s.Kind(id) == NodeKind.Directory && (o.Depth == 0 || level < o.Depth))
            {
                var (kids, other, otherSize) = Kids(d, o, id);
                w.WritePropertyName("children");
                w.WriteStartArray();
                foreach (int c in kids)
                {
                    Node(c, level + 1);
                }
                w.WriteEndArray();
                if (other > 0)
                {
                    w.WriteNumber("otherCount", other);
                    w.WriteNumber("otherSize", otherSize);
                }
            }
            w.WriteEndObject();
        }
    }

    // ── ncdu ────────────────────────────────────────────────────────────────

    /// <summary>ncdu's export format 1.2: <c>[1, 2, {meta}, [{dir}, children…]]</c>, always the whole tree with files.</summary>
    private static void WriteNcdu(ExportData d, ExportOptions o, Stream output)
    {
        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        NodeStore s = d.Store;
        w.WriteStartArray();
        w.WriteNumberValue(1);
        w.WriteNumberValue(2);
        w.WriteStartObject();
        w.WriteString("progname", "hoard");
        w.WriteString("progver", AppInfo.Version);
        w.WriteNumber("timestamp", d.ScannedAt.ToUnixTimeSeconds());
        w.WriteEndObject();
        if (d.Roots.Count == 1)
        {
            Node(d.Roots[0], true);
        }
        else
        {
            // Several marked items: under one made-up folder, as ncdu needs a single root.
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("name", "marked items");
            w.WriteEndObject();
            foreach (int id in d.Roots)
            {
                Node(id, false);
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();

        void Info(int id, bool top)
        {
            w.WriteStartObject();
            w.WriteString("name", top ? d.FullPath(id) : s.Name(id));
            if (s.Kind(id) != NodeKind.Directory)
            {
                w.WriteNumber("asize", s.Apparent(id));
                w.WriteNumber("dsize", s.Size(id));
            }
            w.WriteNumber("mtime", s.MTime(id));
            if ((s.Flags(id) & NodeFlags.Error) != 0)
            {
                w.WriteBoolean("read_error", true);
            }
            if (s.Kind(id) is NodeKind.Excluded)
            {
                w.WriteString("excluded", "pattern");
            }
            else if (s.Kind(id) is NodeKind.MountPoint)
            {
                w.WriteString("excluded", "othfs");
            }
            if ((s.Flags(id) & (NodeFlags.HardLinkPrimary | NodeFlags.HardLinkDuplicate)) != 0)
            {
                w.WriteBoolean("hlnkc", true);
            }
            if (s.Kind(id) is NodeKind.Symlink or NodeKind.Alias)
            {
                w.WriteBoolean("notreg", true);
            }
            w.WriteEndObject();
        }

        void Node(int id, bool top)
        {
            if (s.Kind(id) == NodeKind.Directory)
            {
                w.WriteStartArray();
                Info(id, top);
                foreach (int c in s.Children(id))
                {
                    Node(c, false);
                }
                w.WriteEndArray();
            }
            else
            {
                Info(id, top);
            }
        }
    }

    // ── CSV ─────────────────────────────────────────────────────────────────

    private static string Csv(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    private static void WriteCsv(ExportData d, ExportOptions o, StreamWriter w)
    {
        NodeStore s = d.Store;
        w.WriteLine("path,kind,size,apparent,files,dirs,modified,depth");
        foreach (int root in d.Roots)
        {
            Row(root, 0);
        }

        void Row(int id, int level)
        {
            bool dir = s.Kind(id) == NodeKind.Directory;
            w.Write(Csv(d.FullPath(id)));
            w.Write(',');
            w.Write(Kind(s.Kind(id)));
            w.Write(string.Create(CultureInfo.InvariantCulture,
                $",{s.Size(id)},{s.Apparent(id)},{(dir ? s.Files(id) : 0)},{(dir ? s.Dirs(id) : 0)},{Iso(s.MTime(id))},{level}"));
            w.WriteLine();
            if (dir && (o.Depth == 0 || level < o.Depth))
            {
                foreach (int c in Kids(d, o, id).Shown)
                {
                    Row(c, level + 1);
                }
            }
        }
    }

    // ── Markdown ────────────────────────────────────────────────────────────

    private static string Md(string v) => v.Replace("|", "\\|").Replace("\n", " ");

    private static void WriteMarkdown(ExportData d, ExportOptions o, StreamWriter w)
    {
        NodeStore s = d.Store;
        long total = d.Roots.Sum(id => SizeOf(d, o, id));
        w.WriteLine($"# Hoard report: {Md(d.RootPath)}");
        w.WriteLine();
        w.WriteLine($"Scanned {d.ScannedAt.ToLocalTime():yyyy-MM-dd HH:mm} with hoard {AppInfo.Version} · **{Units.Size(total)}** "
                    + $"({(o.Apparent ? "apparent" : "on-disk")} sizes) · {Units.Count(d.Roots.Sum(id => (long)s.Files(id)))} files");
        if (d.Disk is { } disk)
        {
            w.WriteLine($"· disk {Units.Size(disk.Used)} of {Units.Size(disk.Total)} used");
        }
        w.WriteLine();
        w.WriteLine("## Biggest entries");
        w.WriteLine();
        w.WriteLine("| Entry | Size | Share | Files |");
        w.WriteLine("|---|---:|---:|---:|");
        var entries = d.Roots.Count == 1 && s.Kind(d.Roots[0]) == NodeKind.Directory
            ? Kids(d, o with { MinSize = 0 }, d.Roots[0]).Shown : [.. d.Roots];
        foreach (int id in entries.Take(15))
        {
            bool dir = s.Kind(id) == NodeKind.Directory;
            w.WriteLine($"| {Md(s.Name(id))}{(dir ? "/" : "")} | {Units.Size(SizeOf(d, o, id))} | {Pct(SizeOf(d, o, id), total)} | {(dir ? Units.Count(s.Files(id)) : "")} |");
        }
        if (entries.Count > 15)
        {
            w.WriteLine($"| … {entries.Count - 15} more | | | |");
        }
        Reports(d, w, md: true);
    }

    private static string Pct(long part, long whole) =>
        (whole > 0 ? 100.0 * part / whole : 0).ToString(part * 10 >= whole ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The report sections shared by Markdown and plain HTML (only the ones that are ready).</summary>
    private static void Reports(ExportData d, StreamWriter w, bool md)
    {
        NodeStore s = d.Store;
        void Table(string id, string title, string[] head, IEnumerable<string[]> rows, string? note = null)
        {
            var list = rows.ToList();
            if (md)
            {
                w.WriteLine();
                w.WriteLine($"## {title}");
                w.WriteLine();
                if (list.Count == 0)
                {
                    w.WriteLine("Nothing found.");
                    return;
                }
                w.WriteLine("| " + string.Join(" | ", head) + " |");
                w.WriteLine("|" + string.Concat(head.Select((_, i) => i == 0 ? "---|" : "---:|")));
                foreach (string[] r in list)
                {
                    w.WriteLine("| " + string.Join(" | ", r.Select(Md)) + " |");
                }
                if (note is not null)
                {
                    w.WriteLine();
                    w.WriteLine(note);
                }
                return;
            }
            w.WriteLine($"<h2 id=\"{id}\">{H(title)}</h2>");
            if (list.Count == 0)
            {
                w.WriteLine("<p>Nothing found.</p>");
                return;
            }
            w.WriteLine("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\">");
            w.WriteLine("<thead><tr>" + string.Concat(head.Select(h => $"<th scope=\"col\">{H(h)}</th>")) + "</tr></thead><tbody>");
            foreach (string[] r in list)
            {
                w.WriteLine("<tr>" + string.Concat(r.Select((c, i) => i == 0 ? $"<th scope=\"row\" align=\"left\">{H(c)}</th>" : $"<td align=\"right\">{H(c)}</td>")) + "</tr>");
            }
            w.WriteLine("</tbody></table>");
            if (note is not null)
            {
                w.WriteLine($"<p>{H(note)}</p>");
            }
        }

        if (d.Types is { } types)
        {
            Table("types", "By type", ["Type", "Size", "Share", "Files"],
                types.Categories.Select(c => new[] { FileTypes.Label(c.Category), Units.Size(c.Bytes), Pct(c.Bytes, types.TotalBytes), Units.Count(c.Files) }));
        }
        if (d.Junk is { } junk)
        {
            Table("junk", "Known junk", ["What", "Items", "Size", "Safety"],
                junk.Categories.Where(c => c.Count(s) > 0).Select(c => new[] { c.Rule.Name, Units.Count(c.Count(s)), Units.Size(c.Size(s)), c.Rule.Safety == Safety.Safe ? "SAFE" : "REVIEW" }),
                $"{Units.Size(junk.Total(s))} found, {Units.Size(junk.SafeTotal(s))} of it safe to remove.");
        }
        if (d.BigOld is { } old)
        {
            var o = old.Options;
            Table("old", $"Big & old (≥ {Units.Size(o.MinSize)}, not {(o.Axis == AgeAxis.Accessed ? "opened" : "modified")} for {o.MinAgeDays} days)",
                ["File", "Size", "Last touched"],
                old.Items.Take(50).Select(id => new[] { d.FullPath(id), Units.Size(s.Size(id)), DateTimeOffset.FromUnixTimeSeconds(BigOld.Touched(s, id, o.Axis)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }),
                old.Items.Count > 50 ? $"… and {old.Items.Count - 50} more." : null);
        }
        if (d.Dupes is { } dupes)
        {
            Func<int, long> frees = d.DupFrees ?? (id => s.Size(id));
            Table("dupes", "Duplicate files", ["File", "Copy size", "Copies", "Frees"],
                dupes.Groups.Take(50).Select(g => new[] { s.Name(g.Files[0]), Units.Size(g.Size), g.Files.Count.ToString(CultureInfo.InvariantCulture), Units.Size(g.Files.Skip(1).Sum(frees)) }),
                dupes.Groups.Count > 50 ? $"… and {dupes.Groups.Count - 50} more groups." : null);
        }
        if (d.Empty is { } empty)
        {
            Table("empty", "Empty folders and zero-byte files", ["What", "Count"],
                [["Empty folders (nested ones included)", Units.Count(empty.TotalFolders)], .. empty.Zero.Take(20).Select(g => new[] { "zero-byte " + g.Pattern, Units.Count(g.Files.Count) })]);
        }
    }

    // ── plain HTML ──────────────────────────────────────────────────────────

    private static string H(string v) => WebUtility.HtmlEncode(v);

    private static void WriteHtml(ExportData d, ExportOptions o, StreamWriter w)
    {
        NodeStore s = d.Store;
        long total = d.Roots.Sum(id => SizeOf(d, o, id));
        w.WriteLine("<!DOCTYPE html>");
        w.WriteLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        w.WriteLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        w.WriteLine($"<title>Hoard report: {H(d.RootPath)}</title></head><body>");
        w.WriteLine($"<h1>Hoard report: {H(d.RootPath)}</h1>");
        w.WriteLine($"<p>Scanned {H(d.ScannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} with hoard {H(AppInfo.Version)}. "
                    + $"Total <strong>{H(Units.Size(total))}</strong> ({(o.Apparent ? "apparent" : "on-disk")} sizes), {H(Units.Count(d.Roots.Sum(id => (long)s.Files(id))))} files."
                    + (d.Disk is { } disk ? $" Disk: {H(Units.Size(disk.Used))} of {H(Units.Size(disk.Total))} used." : "") + "</p>");
        w.WriteLine("<nav><ul><li><a href=\"#tree\">Folders</a></li>"
                    + (d.Types is null ? "" : "<li><a href=\"#types\">By type</a></li>")
                    + (d.Junk is null ? "" : "<li><a href=\"#junk\">Known junk</a></li>")
                    + (d.BigOld is null ? "" : "<li><a href=\"#old\">Big &amp; old</a></li>")
                    + (d.Dupes is null ? "" : "<li><a href=\"#dupes\">Duplicates</a></li>")
                    + (d.Empty is null ? "" : "<li><a href=\"#empty\">Empty</a></li>") + "</ul></nav>");
        w.WriteLine("<h2 id=\"tree\">Folders</h2>");
        w.WriteLine("<p>Biggest first. Open a folder to see what's inside.</p>");
        foreach (int root in d.Roots)
        {
            Tree(root, 0, true);
        }
        Reports(d, w, md: false);
        w.WriteLine($"<hr><p><small>Made by hoard {H(AppInfo.Version)}. Plain HTML: no styles, no scripts.</small></p></body></html>");

        void Tree(int id, int level, bool open)
        {
            bool dir = s.Kind(id) == NodeKind.Directory;
            long size = SizeOf(d, o, id);
            string label = $"{H(id == NodeStore.Root ? d.RootPath : s.Name(id))}{(dir ? "/" : "")} · {H(Units.Size(size))} "
                           + $"<meter min=\"0\" max=\"{Math.Max(1, total)}\" value=\"{size}\">{H(Pct(size, total))}</meter> {H(Pct(size, total))}";
            if (!dir || (o.Depth > 0 && level >= o.Depth))
            {
                w.WriteLine($"<div>{label}</div>");
                return;
            }
            var (kids, other, otherSize) = Kids(d, o, id);
            w.WriteLine($"<details{(open ? " open" : "")}><summary>{label}</summary><blockquote>");
            foreach (int c in kids)
            {
                Tree(c, level + 1, false);
            }
            if (other > 0)
            {
                w.WriteLine($"<div><em>{other} smaller item{(other == 1 ? "" : "s")} · {H(Units.Size(otherSize))}</em></div>");
            }
            w.WriteLine("</blockquote></details>");
        }
    }

    // ── pretty HTML ─────────────────────────────────────────────────────────

    private static readonly string[] Palette = ["#ffaf00", "#00afff", "#d75fd7", "#87d787", "#ff875f", "#5fafff", "#af87ff", "#5f87d7", "#d7af87", "#8a8a8a"];

    private static string Js(string v) => JsonSerializer.Serialize(v, PrettyJson.Default.String);
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WritePretty(ExportData d, ExportOptions o, StreamWriter w)
    {
        using Stream res = typeof(Exporter).Assembly.GetManifestResourceStream("Hoard.PrettyReport.html")
                           ?? throw new InvalidOperationException("the report template is missing from the build");
        string template = new StreamReader(res).ReadToEnd();
        NodeStore s = d.Store;
        int root = d.Roots.Count == 1 ? d.Roots[0] : NodeStore.Root;
        var junkKind = new Dictionary<int, string>();
        foreach (JunkCategory c in d.Junk?.Categories ?? [])
        {
            foreach (int id in c.Items)
            {
                junkKind.TryAdd(id, c.Rule.Safety == Safety.Safe ? "SAFE" : "REVIEW");
            }
        }
        const double G = 1e9;
        var js = new StringBuilder();
        js.AppendLine("const G = 1e9, M = 1e6;");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string label = d.Roots.Count == 1 ? (root == NodeStore.Root ? Label(d.RootPath, home) : s.Name(root)) : "marked items";

        // The tree, biggest first, to the chosen depth; small entries fold into "other (N items)".
        void Node(int id, int level, string? colour, bool top)
        {
            long size = SizeOf(d, o, id);
            long age = d.Now - s.MTime(id);
            string ageClass = age < 90 * 86400 ? "ok" : age < 365 * 86400 ? "warn" : "bad";
            js.Append("{n:").Append(Js(top ? label : s.Name(id))).Append(",s:").Append(size)
              .Append(",files:").Append(s.Files(id)).Append(",age:\"").Append(ageClass).Append('"');
            if (top)
            {
                js.Append(",path:").Append(Js(d.Roots.Count == 1 ? d.FullPath(id) : d.RootPath));
            }
            if (colour is not null)
            {
                js.Append(",col:\"").Append(colour).Append('"');
            }
            if (junkKind.TryGetValue(id, out string? jk))
            {
                js.Append(",junk:\"").Append(jk).Append('"');
            }
            if (s.Kind(id) == NodeKind.Directory && (o.Depth == 0 || level < o.Depth))
            {
                var (kids, other, otherSize) = Kids(d, o with { MinSize = Math.Max(o.MinSize, 1) }, id);
                if (kids.Count > 0 || other > 0)
                {
                    js.Append(",c:[");
                    int i = 0;
                    foreach (int c in kids.Take(60))
                    {
                        Node(c, level + 1, level == 0 ? Palette[Math.Min(i++, Palette.Length - 1)] : null, false);
                        js.Append(',');
                    }
                    int extra = other + Math.Max(0, kids.Count - 60);
                    long extraSize = otherSize + kids.Skip(60).Sum(c => SizeOf(d, o, c));
                    if (extra > 0 && extraSize > 0)
                    {
                        // Its own colour: at the top level there's no parent colour to inherit.
                        js.Append("{n:").Append(Js($"other ({extra} items)")).Append(",s:").Append(extraSize).Append(",age:\"warn\",col:\"#8a8a8a\"},");
                    }
                    js.Append(']');
                }
            }
            js.Append('}');
        }

        js.Append("const ROOT = ");
        if (d.Roots.Count == 1)
        {
            Node(root, 0, null, true);
        }
        else
        {
            js.Append("{n:\"marked items\",s:").Append(d.Roots.Sum(id => SizeOf(d, o, id))).Append(",path:").Append(Js(d.RootPath)).Append(",c:[");
            int i = 0;
            foreach (int id in d.Roots.OrderByDescending(id => SizeOf(d, o, id)))
            {
                Node(id, 1, Palette[Math.Min(i++, Palette.Length - 1)], false);
                js.Append(',');
            }
            js.Append("]}");
        }
        js.AppendLine(";");
        js.AppendLine("""
            (function finish(node, parent) {
              node.parent = parent;
              if (parent) node.path = parent.path + "/" + node.n;
              if (node.c) { for (const ch of node.c) finish(ch, node); node.c.sort((a,b) => b.s - a.s); }
              if (!node.col && parent) node.col = parent.col;
            })(ROOT, null);
            """);

        // Types, extensions, age, junk, duplicates, big & old: in GB, as the template expects.
        TypeReport? types = d.Types;
        js.Append("const TYPES = [").AppendJoin(",", (types?.Categories ?? []).Select((c, i) =>
            $"[{Js(FileTypes.Label(c.Category))},{Num(Math.Round(c.Bytes / G, 3))},\"{Palette[Math.Min(i, Palette.Length - 1)]}\"]")).AppendLine("];");
        js.Append("const EXTS = [").AppendJoin(",", (types?.Extensions ?? []).Take(12).Select(e =>
            $"[{Js(e.Extension == FileTypes.NoExtension ? e.Extension : "." + e.Extension)},{Num(Math.Round(e.Bytes / G, 3))},{e.Files}]")).AppendLine("];");
        js.AppendLine("const AGE_BUCKETS = [" + string.Join(",", AgeBuckets.Labels.Select(Js)) + "];");
        js.Append("const AGE = [").AppendJoin(",", (d.Age?.Rows ?? []).Select(r =>
            $"[{Js(s.Name(r.Node))},{Num(Math.Round(r.Total / G, 3))},[{string.Join(",", r.Bytes.Select(b => Num(Math.Round(b / G, 3))))}]]")).AppendLine("];");
        js.Append("const JUNK = [").AppendJoin(",", (d.Junk?.Categories ?? []).Where(c => c.Count(s) > 0).Select(c =>
            $"[{Js(c.Rule.Name)},{Js(Units.Count(c.Count(s)) + " items")},{Num(Math.Round(c.Size(s) / G, 3))},\"{(c.Rule.Safety == Safety.Safe ? "SAFE" : "REVIEW")}\",{Js(c.Rule.Why)}]")).AppendLine("];");
        js.Append("const DUPES = [").AppendJoin(",", (d.Dupes?.Groups ?? []).Take(40).Select(g =>
            $"[{Js(s.Name(g.Files[0]))},{Num(Math.Round(g.Size / G, 4))},[{string.Join(",", g.Files.Select(id => Js(d.FullPath(id))))}]]")).AppendLine("];");
        BigOldReport? old = d.BigOld;
        js.Append("const OLD = [").AppendJoin(",", (old?.Items ?? []).Take(40).Select(id =>
        {
            long touched = BigOld.Touched(s, id, old!.Options.Axis);
            return $"[{Num(Math.Round(s.Size(id) / G, 2))},\"{DateTimeOffset.FromUnixTimeSeconds(touched):yyyy-MM-dd}\",{Num(Math.Floor((d.Now - touched) / (365.25 * 86400) * 10) / 10)},{Js(d.FullPath(id))}]";
        })).AppendLine("];");

        long junkTotal = d.Junk?.Total(s) ?? 0, junkSafe = d.Junk?.SafeTotal(s) ?? 0;
        long oldTotal = old?.Items.Sum(id => s.Size(id)) ?? 0;
        Func<int, long> frees = d.DupFrees ?? (id => s.Size(id));
        long dupes = d.Dupes?.Groups.Sum(g => g.Files.Skip(1).Sum(frees)) ?? 0;
        string oldRule = old is null ? "Not worked out for this export."
            : $"Files of {Units.Size(old.Options.MinSize)} or more not {(old.Options.Axis == AgeAxis.Accessed ? "opened" : "modified")} for {old.Options.MinAgeDays} days or more.";
        js.Append("const META = {")
          .Append("version:").Append(Js(AppInfo.Version))
          .Append(",root:").Append(Js(d.RootPath))
          .Append(",home:").Append(Js(home))
          .Append(",rootLabel:").Append(Js(label))
          .Append(",scanned:").Append(Js(d.ScannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
          .Append(",sizeMode:").Append(Js(o.Apparent ? "apparent" : "on-disk"))
          .Append(",typesGB:").Append(Num(Math.Max(1e-9, (types?.TotalBytes ?? 0) / G)))
          .Append(",reclaim:").Append(junkTotal + oldTotal + dupes)
          .Append(",junk:").Append(junkTotal).Append(",junkSafe:").Append(junkSafe)
          .Append(",old:").Append(oldTotal).Append(",oldCount:").Append(old?.Items.Count ?? 0).Append(",oldRule:").Append(Js(oldRule))
          .Append(",dupes:").Append(dupes).Append(",dupesKnown:").Append(d.Dupes is null ? "false" : "true").Append(",dupeGroups:").Append(d.Dupes?.Groups.Count ?? 0)
          .Append(",emptyFolders:").Append(d.Empty?.TotalFolders ?? types?.EmptyFolders ?? 0)
          .Append(",zeroFiles:").Append(d.Empty?.ZeroFileCount ?? types?.ZeroByteFiles ?? 0)
          .Append(",stale:").Append(d.Age?.Total.Stale ?? 0)
          .Append(",hardLinks:").Append(d.HardLinks).Append(",hardSaved:").Append(d.HardLinkBytes)
          .Append(",disk:").Append(d.Disk is { } disk ? $"{{name:{Js(disk.Name)},total:{disk.Total},used:{disk.Used}}}" : "null")
          .AppendLine("};");

        string title = $"Hoard report · {label} · {Units.Size(SizeOf(d, o, root))}";
        w.Write(template.Replace("@@TITLE@@", H(title)).Replace("/*@@DATA@@*/", js.ToString()));
    }

    private static string Label(string path, string home) =>
        home.Length > 1 && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal)) ? "~" + path[home.Length..] : path;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class PrettyJson : System.Text.Json.Serialization.JsonSerializerContext;
