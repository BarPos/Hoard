namespace Hoard.Platform;

/// <summary>
/// macOS firmlinks: since Catalina, folders such as /Users and /Applications on the read-only
/// system volume are firmlinked to /System/Volumes/Data/Users etc. Both paths report the same
/// device and inode, so scanning / would otherwise count that data twice. The list lives in
/// /usr/share/firmlinks as "&lt;path at /&gt;\t&lt;path relative to the Data volume&gt;".
/// </summary>
public static class MacFirmlinks
{
    public const string DataVolume = "/System/Volumes/Data";
    private const string ListFile = "/usr/share/firmlinks";

    /// <summary>
    /// The Data-volume paths to treat as aliases when scanning <paramref name="root"/>: only those
    /// whose /-side twin is also inside the scan, so skipping them never hides data.
    /// </summary>
    public static IReadOnlyCollection<string> AliasesFor(string root, string? listFile = null)
    {
        listFile ??= ListFile;
        if (!OperatingSystem.IsMacOS() && listFile == ListFile)
        {
            return [];
        }
        string[] lines;
        try
        {
            lines = File.ReadAllLines(listFile);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        string r = root.Length > 1 ? root.TrimEnd('/') : root;
        var aliases = new List<string>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 2 || !parts[0].StartsWith('/'))
            {
                continue;
            }
            string visible = parts[0];
            string alias = DataVolume + "/" + parts[1].TrimStart('/');
            if (IsWithin(visible, r) && IsWithin(alias, r))
            {
                aliases.Add(alias);
            }
        }
        return aliases;
    }

    private static bool IsWithin(string path, string root) =>
        root == "/" || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
