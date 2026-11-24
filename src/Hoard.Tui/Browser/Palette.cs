using Hoard.Core.Analysis;

namespace Hoard.Tui.Browser;

/// <summary>One thing the palette (<c>:</c>) can do: what it's called, its key, and the command it runs.</summary>
public sealed record PaletteEntry(string Name, string Key, Cmd Cmd);

/// <summary>The command palette: every action by name, so nothing has to be memorised (plan.md §7.11).</summary>
public sealed class PaletteBox
{
    public static readonly PaletteEntry[] All =
    [
        new("Overview", "1", Cmd.ViewOverview),
        new("Browse the folder list", "2", Cmd.ViewBrowse),
        new("Treemap", "3", Cmd.ViewMap),
        new("File types", "4", Cmd.ViewTypes),
        new("Age heatmap", "5", Cmd.ViewAge),
        new("Duplicates", "6 D", Cmd.ViewDupes),
        new("Known junk", "7 J", Cmd.ViewJunk),
        new("Big & old files", "8 O", Cmd.ViewBigOld),
        new("Empty folders & zero-byte files", "9 E", Cmd.ViewEmpty),
        new("Snapshots & changes", "0", Cmd.ViewSnaps),
        new("Find anywhere", "/", Cmd.Find),
        new("Export a report", "x", Cmd.Export),
        new("Save a snapshot now", "P", Cmd.SnapshotNow),
        new("Rescan this folder", "r", Cmd.Rescan),
        new("Rescan everything", "R", Cmd.RescanAll),
        new("Exclude the highlighted entry", "X", Cmd.ExcludeIt),
        new("Delete marked (or highlighted)…", "d", Cmd.Delete),
        new("Mark / unmark", "space", Cmd.Mark),
        new("Mark all", "A", Cmd.MarkAll),
        new("Clear marks or undo the last delete", "u", Cmd.ClearOrUndo),
        new("Reveal in Finder", "o", Cmd.Reveal),
        new("Copy path", "y", Cmd.CopyPath),
        new("Sort by the next field", "s", Cmd.SortNext),
        new("Reverse the sort", "S", Cmd.SortReverse),
        new("On-disk ↔ apparent sizes", "z", Cmd.ToggleSize),
        new("Next theme", "t", Cmd.NextTheme),
        new("Settings", ",", Cmd.Settings),
        new("Volumes (disks)", "v", Cmd.Volumes),
        new("Watch mode (live changes)", "w", Cmd.Watch),
        new("Keys and help", "?", Cmd.Help),
        new("Quit", "q", Cmd.Quit),
    ];

    public string Query { get; set; } = "";
    public int Selected { get; set; }

    /// <summary>Entries matching the query, best first (all of them, in order, when it's empty).</summary>
    public List<PaletteEntry> Matches()
    {
        string[] words = FuzzySearch.Words(Query);
        if (words.Length == 0)
        {
            return [.. All];
        }
        return [.. All.Select(e => (e, Score: FuzzySearch.ScoreAll(e.Name + " " + e.Key, words, 0)))
            .Where(x => x.Score >= 0).OrderByDescending(x => x.Score).Select(x => x.e)];
    }
}
