using System.Globalization;
using System.Text;
using Hoard.Core.Analysis;

namespace Hoard.Core.Config;

/// <summary>
/// The user's saved choices (plan.md §7.6, mock 16-settings.sh), kept in
/// <c>~/.config/hoard/config.toml</c>. Command-line flags override them for one run.
/// </summary>
public sealed class Settings
{
    public string Theme { get; set; } = "hoard";
    public bool Mouse { get; set; } = true;
    public bool ApparentSizes { get; set; }
    public bool CrossFileSystems { get; set; }
    /// <summary>Scanner threads; 0 = automatic.</summary>
    public int Threads { get; set; }
    public List<string> Exclude { get; set; } = [];
    public BigOldOptions BigOld { get; set; } = BigOldOptions.Default;
    /// <summary>Save a snapshot after every finished scan (plan.md §9.1).</summary>
    public bool AutoSnapshot { get; set; } = true;
    /// <summary>Snapshots kept per scanned folder; older ones are deleted.</summary>
    public int KeepSnapshots { get; set; } = 30;
    /// <summary>Powers of 1024 (GiB) instead of 1000 (GB).</summary>
    public bool Iec { get; set; }
    /// <summary>Plain ASCII instead of box-drawing and block characters, for limited terminals and fonts.</summary>
    public bool AsciiGlyphs { get; set; }
    /// <summary>The splash and other short animations (off: straight to the final frame).</summary>
    public bool Animations { get; set; } = true;
    /// <summary>One tip per day on the splash and scanning screens.</summary>
    public bool Tips { get; set; } = true;
    /// <summary>The first-run tour has been shown (or skipped).</summary>
    public bool TourDone { get; set; }
}

/// <summary>
/// Reads and writes <see cref="Settings"/> as a small TOML file: <c>key = value</c> lines, one
/// <c>[big_old]</c> table, strings, integers, booleans and string arrays. Unknown keys are
/// reported and kept out of the way, never fatal; a broken file falls back to defaults.
/// </summary>
public static class SettingsFile
{
    /// <summary><c>$XDG_CONFIG_HOME/hoard/config.toml</c>, else <c>~/.config/hoard/config.toml</c>.</summary>
    public static string DefaultPath()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string baseDir = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "hoard", "config.toml");
    }

    /// <summary>Loads the file if it exists. Problems (bad lines, unknown keys) come back as messages.</summary>
    public static (Settings Settings, List<string> Problems) Load(string path)
    {
        var s = new Settings();
        var problems = new List<string>();
        if (!File.Exists(path))
        {
            return (s, problems);
        }
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"couldn't read {path}: {e.Message}");
            return (s, problems);
        }
        string table = "";
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                table = line[1..^1].Trim();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                problems.Add($"line {i + 1}: expected key = value");
                continue;
            }
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            string full = table.Length > 0 ? table + "." + key : key;
            try
            {
                Apply(s, full, value);
            }
            catch (FormatException e)
            {
                problems.Add($"line {i + 1}: {full}: {e.Message}");
            }
        }
        return (s, problems);
    }

    private static void Apply(Settings s, string key, string value)
    {
        switch (key)
        {
            case "theme": s.Theme = Str(value); break;
            case "mouse": s.Mouse = Bool(value); break;
            case "apparent_sizes": s.ApparentSizes = Bool(value); break;
            case "cross_filesystems": s.CrossFileSystems = Bool(value); break;
            case "threads": s.Threads = Math.Clamp((int)Int(value), 0, 256); break;
            case "exclude": s.Exclude = StrArray(value); break;
            case "auto_snapshot": s.AutoSnapshot = Bool(value); break;
            case "animations": s.Animations = Bool(value); break;
            case "tips": s.Tips = Bool(value); break;
            case "tour_done": s.TourDone = Bool(value); break;
            case "units": s.Iec = Str(value) switch { "iec" => true, "si" => false, var u => throw new FormatException($"'{u}' isn't si or iec") }; break;
            case "glyphs": s.AsciiGlyphs = Str(value) switch { "ascii" => true, "unicode" => false, var g => throw new FormatException($"'{g}' isn't unicode or ascii") }; break;
            case "keep_snapshots": s.KeepSnapshots = (int)Math.Clamp(Int(value), 1, 1000); break;
            case "big_old.min_size": s.BigOld = s.BigOld with { MinSize = Math.Max(0, Int(value)) }; break;
            case "big_old.min_age_days": s.BigOld = s.BigOld with { MinAgeDays = (int)Math.Clamp(Int(value), 0, 36500) }; break;
            case "big_old.based_on":
                s.BigOld = s.BigOld with
                {
                    Axis = Str(value) switch
                    {
                        "accessed" => AgeAxis.Accessed,
                        "modified" => AgeAxis.Modified,
                        var other => throw new FormatException($"'{other}' isn't modified or accessed"),
                    },
                };
                break;
            default: throw new FormatException("unknown setting (ignored)");
        }
    }

    public static void Save(Settings s, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Hoard settings. Change them in Hoard (press ,) or edit this file.");
        sb.AppendLine("# Command-line flags (for example --theme) override these for one run.");
        sb.AppendLine();
        sb.AppendLine($"theme = {Quote(s.Theme)}            # hoard, neon, frost or mono");
        sb.AppendLine($"mouse = {B(s.Mouse)}");
        sb.AppendLine($"units = {Quote(s.Iec ? "iec" : "si")}            # si: GB (1000) · iec: GiB (1024)");
        sb.AppendLine($"animations = {B(s.Animations)}       # the splash; HOARD_ANIMATE=0 also turns it off");
        sb.AppendLine($"tips = {B(s.Tips)}");
        sb.AppendLine($"tour_done = {B(s.TourDone)}        # false shows the 3-step tour again");
        sb.AppendLine($"glyphs = {Quote(s.AsciiGlyphs ? "ascii" : "unicode")}      # ascii for terminals or fonts without box drawing");
        sb.AppendLine($"apparent_sizes = {B(s.ApparentSizes)}  # false: space used on disk · true: file lengths");
        sb.AppendLine($"cross_filesystems = {B(s.CrossFileSystems)}");
        sb.AppendLine($"threads = {s.Threads.ToString(CultureInfo.InvariantCulture)}                 # 0 = automatic");
        sb.AppendLine("# gitignore-style patterns to skip, e.g. [\"**/.git/objects\", \"~/Library/Mobile Documents\"]");
        sb.AppendLine($"exclude = [{string.Join(", ", s.Exclude.Select(Quote))}]");
        sb.AppendLine($"auto_snapshot = {B(s.AutoSnapshot)}       # save folder totals after each scan, for diffs");
        sb.AppendLine($"keep_snapshots = {s.KeepSnapshots.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("[big_old]");
        sb.AppendLine($"min_size = {s.BigOld.MinSize.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"min_age_days = {s.BigOld.MinAgeDays.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"based_on = {Quote(s.BigOld.Axis == AgeAxis.Accessed ? "accessed" : "modified")}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write next to it, then swap: a crash mid-write never leaves a half file.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, overwrite: true);
    }

    private static string B(bool b) => b ? "true" : "false";

    private static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Removes a # comment that isn't inside a string.</summary>
    private static string StripComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '\\' && inString)
            {
                i++;
            }
            else if (ch == '"')
            {
                inString = !inString;
            }
            else if (ch == '#' && !inString)
            {
                return line[..i];
            }
        }
        return line;
    }

    private static bool Bool(string v) => v switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException($"'{v}' isn't true or false"),
    };

    private static long Int(string v) =>
        long.TryParse(v.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            ? n : throw new FormatException($"'{v}' isn't a whole number");

    private static string Str(string v)
    {
        if (v.Length < 2 || v[0] != '"' || v[^1] != '"')
        {
            throw new FormatException($"{v} should be in \"quotes\"");
        }
        var sb = new StringBuilder();
        for (int i = 1; i < v.Length - 1; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length - 1)
            {
                i++;
            }
            sb.Append(v[i]);
        }
        return sb.ToString();
    }

    private static List<string> StrArray(string v)
    {
        if (!v.StartsWith('[') || !v.EndsWith(']'))
        {
            throw new FormatException("should be a list like [\"a\", \"b\"]");
        }
        var items = new List<string>();
        string body = v[1..^1];
        int i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ','))
            {
                i++;
            }
            if (i >= body.Length)
            {
                break;
            }
            if (body[i] != '"')
            {
                throw new FormatException("list items should be in \"quotes\"");
            }
            int start = i++;
            while (i < body.Length && body[i] != '"')
            {
                i += body[i] == '\\' ? 2 : 1;
            }
            if (i >= body.Length)
            {
                throw new FormatException("a string in the list isn't closed");
            }
            items.Add(Str(body[start..(i + 1)]));
            i++;
        }
        return items;
    }
}
