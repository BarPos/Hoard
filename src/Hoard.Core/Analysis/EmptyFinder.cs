using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

/// <summary>How safe it is to remove a group of zero-byte files.</summary>
public enum ZeroSafety { Safe, Careful, Keep }

/// <summary>Zero-byte files that share a name pattern, e.g. <c>*.log</c> or <c>__init__.py</c>.</summary>
public sealed record ZeroGroup(string Pattern, ZeroSafety Safety, string Why, List<int> Files, string Where);

public sealed class EmptyReport(List<int> folders, int totalFolders, List<ZeroGroup> zero, bool ignoredKeepFiles)
{
    /// <summary>Outermost empty folders (an empty folder inside another is part of it), by path.</summary>
    public List<int> Folders { get; } = folders;
    /// <summary>All empty folders, nested ones included.</summary>
    public int TotalFolders { get; } = totalFolders;
    /// <summary>Zero-byte file groups, biggest count first.</summary>
    public List<ZeroGroup> Zero { get; } = zero;
    public bool IgnoredKeepFiles { get; } = ignoredKeepFiles;
    public int ZeroFileCount => Zero.Sum(g => g.Files.Count);
}

/// <summary>
/// Empty folders and zero-byte files (plan.md §6.4). A folder is empty when it holds nothing, or
/// only empty folders (and, by default, <c>.gitkeep</c> / <c>.keep</c>; <c>.DS_Store</c> always
/// counts as nothing). Git repositories' insides, app bundles and installed software (Homebrew's
/// Cellar, node_modules, site-packages… see <see cref="JunkRules.InstalledTrees"/>) are left alone:
/// empty folders and files there are part of how they work.
/// </summary>
public static class EmptyFinder
{
    private static readonly HashSet<string> KeepFiles = new(StringComparer.Ordinal) { ".gitkeep", ".keep" };
    private const string FinderJunk = ".DS_Store";
    private static readonly string[] BundleSuffixes = [".app", ".framework", ".bundle", ".plugin", ".kext", ".photoslibrary", ".xcodeproj", ".xcworkspace"];

    private static readonly HashSet<string> Installed = new(JunkRules.InstalledTrees, StringComparer.Ordinal);

    /// <summary>Folders whose insides Hoard doesn't judge.</summary>
    public static bool IsSealed(string name) =>
        name == ".git" || Installed.Contains(name) || BundleSuffixes.Any(sfx => name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase));

    public static EmptyReport Find(NodeStore s, bool ignoreKeepFiles = true, CancellationToken cancel = default)
    {
        // Pre-order list of folders to judge; then decide bottom-up (children come later in the list).
        var order = new List<int>();
        var zero = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var stack = new Stack<int>();
        stack.Push(NodeStore.Root);
        int n = 0;
        while (stack.Count > 0)
        {
            if ((++n & 4095) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }
            int dir = stack.Pop();
            order.Add(dir);
            foreach (int id in s.Children(dir))
            {
                NodeKind kind = s.Kind(id);
                if (kind == NodeKind.Directory && !IsSealed(s.Name(id)))
                {
                    stack.Push(id);
                }
                else if (kind == NodeKind.File && s.Apparent(id) == 0)
                {
                    string pattern = PatternOf(s.Name(id));
                    if (!zero.TryGetValue(pattern, out var list))
                    {
                        zero[pattern] = list = [];
                    }
                    list.Add(id);
                }
            }
        }

        var empty = new HashSet<int>();
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int dir = order[i];
            if ((s.Flags(dir) & (NodeFlags.Error | NodeFlags.Pending)) != 0)
            {
                continue; // unreadable or unfinished: can't say it's empty
            }
            bool isEmpty = true;
            foreach (int id in s.Children(dir))
            {
                NodeKind kind = s.Kind(id);
                string name = s.Name(id);
                bool nothing = kind switch
                {
                    NodeKind.Directory => empty.Contains(id),
                    NodeKind.File => name == FinderJunk || (ignoreKeepFiles && KeepFiles.Contains(name)),
                    _ => false, // links, other disks, excluded folders: something is there
                };
                if (!nothing)
                {
                    isEmpty = false;
                    break;
                }
            }
            if (isEmpty)
            {
                empty.Add(dir);
            }
        }
        empty.Remove(NodeStore.Root); // the scanned folder itself is never offered
        var outermost = empty.Where(id => !empty.Contains(s.Parent(id)))
            .OrderBy(s.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        var groups = zero.Select(kv =>
        {
            var (safety, why) = Judge(kv.Key);
            kv.Value.Sort((a, b) => string.CompareOrdinal(s.RelativePath(a), s.RelativePath(b)));
            return new ZeroGroup(kv.Key, safety, why, kv.Value, CommonPlace(s, kv.Value));
        }).OrderByDescending(g => g.Files.Count).ThenBy(g => g.Pattern, StringComparer.Ordinal).ToList();

        return new EmptyReport(outermost, empty.Count, groups, ignoreKeepFiles);
    }

    /// <summary>The group a zero-byte file belongs to: a well-known name, else "*.ext", else "no extension".</summary>
    public static string PatternOf(string name) => name switch
    {
        "__init__.py" or "py.typed" or ".gitkeep" or ".keep" or ".localized" or ".nojekyll" or ".metadata_never_index" => name,
        "Icon\r" => "Icon (folder icon)",
        _ => FileTypes.ExtensionOf(name) is { } ext ? "*." + ext.ToLowerInvariant() : "no extension",
    };

    public static (ZeroSafety, string) Judge(string pattern) => pattern switch
    {
        "*.log" or "*.tmp" or "*.temp" or "*.bak" or "*.old" or "*.out" or "*.err" or "*.pid" =>
            (ZeroSafety.Safe, "empty logs and temporary files: nothing is lost"),
        "__init__.py" => (ZeroSafety.Careful, "marks a folder as a Python package; code may stop importing without it"),
        "py.typed" => (ZeroSafety.Careful, "tells Python tools a package has type hints"),
        ".gitkeep" or ".keep" => (ZeroSafety.Careful, "keeps an otherwise empty folder in git"),
        ".nojekyll" => (ZeroSafety.Careful, "tells GitHub Pages how to build a site"),
        "*.lock" => (ZeroSafety.Careful, "a lock may belong to a program that's running"),
        "Icon (folder icon)" or ".localized" or ".metadata_never_index" =>
            (ZeroSafety.Keep, "used by macOS (folder icons, names, Spotlight): kept"),
        _ => (ZeroSafety.Careful, "empty, but something may expect the file to exist"),
    };

    /// <summary>Where most of the files are: the most common first two folders of their paths.</summary>
    private static string CommonPlace(NodeStore s, List<int> files)
    {
        string? best = files.Select(id =>
            {
                string[] parts = s.RelativePath(s.Parent(id)).Split('/', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 ? "(top)" : string.Join('/', parts.Take(2));
            })
            .GroupBy(p => p).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault();
        return best ?? "";
    }
}
