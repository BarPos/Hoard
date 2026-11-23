using Hoard.Core.Model;
using Hoard.Core.Scanning;

namespace Hoard.Core.Actions;

/// <summary>Moves items to the OS trash / recycle bin (plan.md §8.2).</summary>
public interface ITrash
{
    /// <summary>Human-readable name, e.g. "Trash" or "Recycle Bin".</summary>
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>Moves <paramref name="path"/> to the trash.</summary>
    /// <returns>Where it ended up (used for undo), or null if that isn't known.</returns>
    /// <exception cref="IOException">The item couldn't be moved.</exception>
    string? MoveToTrash(string path);
}

/// <summary>One item the user asked to delete, after planning.</summary>
public sealed record DeleteItem(int Node, string Path, long Size, int Files, NodeKind Kind);

/// <summary>A checked list of things to delete (plan.md §8.1, §8.3).</summary>
public sealed record DeletePlan(IReadOnlyList<DeleteItem> Items, IReadOnlyList<(string Path, string Reason)> Refused)
{
    public long TotalSize => Items.Sum(i => i.Size);
    public long TotalFiles => Items.Sum(i => (long)i.Files);
    public bool IsEmpty => Items.Count == 0;
}

public sealed record DeleteOutcome(int Node, string Path, bool Ok, string? Error, string? TrashedTo);

public sealed record DeleteReport(IReadOnlyList<DeleteOutcome> Outcomes, bool Permanent)
{
    public IEnumerable<DeleteOutcome> Succeeded => Outcomes.Where(o => o.Ok);
    public IEnumerable<DeleteOutcome> Failed => Outcomes.Where(o => !o.Ok);
}

/// <summary>
/// Plans and performs deletes with the safety rails from plan.md §8.3:
/// <list type="bullet">
/// <item>only items inside the scan root, never the root itself;</item>
/// <item>never system or top-level locations (/, /System, /usr, the home folder, …);</item>
/// <item>nested marks collapse to the outermost item, so nothing is counted twice;</item>
/// <item>right before acting, each item must still look like what was scanned
///   (same type; for files also the same size and modification time) — otherwise it is skipped.</item>
/// </list>
/// </summary>
public sealed class Deleter(NodeStore store, string rootPath, IFileProbe probe, ITrash trash)
{
    private static readonly string[] ProtectedExact =
    [
        "/", "/System", "/Library", "/Applications", "/Users", "/usr", "/bin", "/sbin", "/etc", "/var",
        "/private", "/private/etc", "/private/var", "/opt", "/Volumes", "/home", "/root", "/boot", "/dev", "/proc",
        "/sys", "/lib", "/lib64",
    ];
    private static readonly string[] ProtectedTrees = ["/System", "/usr", "/bin", "/sbin", "/dev", "/proc", "/sys", "/boot"];

    public string RootPath { get; } = rootPath;
    public ITrash Trash { get; } = trash;

    public string PathOf(int id)
    {
        string rel = store.RelativePath(id);
        return rel.Length == 0 ? RootPath : RootPath == "/" ? "/" + rel : RootPath + "/" + rel;
    }

    /// <summary>Why a path must never be deleted, or null if it may be.</summary>
    public static string? ProtectedReason(string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string p = path.Length > 1 ? path.TrimEnd('/') : path;
        if (ProtectedExact.Contains(p, StringComparer.Ordinal) || p == home.TrimEnd('/'))
        {
            return "a system or top-level folder";
        }
        if (ProtectedTrees.Any(t => p.StartsWith(t + "/", StringComparison.Ordinal)))
        {
            return "inside a protected system folder";
        }
        if (home.Length > 1 && (p == home + "/Library" || p == home + "/Documents" || p == home + "/Desktop"))
        {
            return "a top-level folder of your home";
        }
        return null;
    }

    public DeletePlan Plan(IEnumerable<int> marked)
    {
        var refused = new List<(string, string)>();
        var allowed = new HashSet<int>();
        // First decide what may be deleted at all; refused items must not "cover" anything.
        foreach (int id in marked.Where(id => !store.IsDeleted(id)).Distinct())
        {
            string path = PathOf(id);
            string? why = id == NodeStore.Root ? "the scanned folder itself"
                : ProtectedReason(path) ?? store.Kind(id) switch
                {
                    NodeKind.Alias => "another path to a folder counted elsewhere",
                    NodeKind.MountPoint => "on another disk",
                    NodeKind.Excluded => "excluded from the scan",
                    _ => null,
                };
            if (why is null)
            {
                allowed.Add(id);
            }
            else
            {
                refused.Add((path, why));
            }
        }
        // Then drop anything inside another allowed item: deleting the outer one covers it.
        var items = new List<DeleteItem>();
        foreach (int id in allowed.OrderBy(PathOf, StringComparer.Ordinal))
        {
            bool covered = false;
            for (int n = store.Parent(id); n != NodeStore.None; n = store.Parent(n))
            {
                if (allowed.Contains(n))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
            {
                items.Add(new DeleteItem(id, PathOf(id), store.Size(id), store.FileCount(id), store.Kind(id)));
            }
        }
        return new DeletePlan(items, [.. refused.OrderBy(r => r.Item1, StringComparer.Ordinal)]);
    }

    /// <summary>Does the thing on disk still match what was scanned?</summary>
    public string? Revalidate(DeleteItem item)
    {
        if (!probe.TryGetInfo(item.Path, out EntryInfo info, out _))
        {
            return "it no longer exists";
        }
        NodeKind expected = item.Kind;
        bool sameType = expected switch
        {
            NodeKind.Directory => info.Type == EntryType.Directory,
            NodeKind.File => info.Type == EntryType.File,
            NodeKind.Symlink => info.Type == EntryType.Symlink,
            _ => info.Type == EntryType.Other,
        };
        if (!sameType)
        {
            return "it changed type since the scan";
        }
        if (expected == NodeKind.File && (info.Apparent != store.Apparent(item.Node) && !store.Flags(item.Node).HasFlag(NodeFlags.HardLinkDuplicate)
                                          || NodeStore.ToUnixTime(info.MTime) != store.MTime(item.Node)))
        {
            return "it was modified since the scan";
        }
        return null;
    }

    /// <summary>Deletes every item of the plan; updates the tree for the ones that succeed.</summary>
    public DeleteReport Execute(DeletePlan plan, bool permanent, IProgress<int>? progress = null)
    {
        var outcomes = new List<DeleteOutcome>();
        int done = 0;
        foreach (DeleteItem item in plan.Items)
        {
            string? problem = Revalidate(item);
            string? trashedTo = null;
            if (problem is null)
            {
                try
                {
                    if (permanent)
                    {
                        if (item.Kind == NodeKind.Directory)
                        {
                            Directory.Delete(item.Path, recursive: true);
                        }
                        else
                        {
                            File.Delete(item.Path);
                        }
                    }
                    else
                    {
                        if (!Trash.IsAvailable)
                        {
                            throw new IOException($"the {Trash.Name} isn't available on this system");
                        }
                        trashedTo = Trash.MoveToTrash(item.Path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problem = ex.Message;
                }
            }
            if (problem is null)
            {
                store.MarkDeleted(item.Node);
            }
            outcomes.Add(new DeleteOutcome(item.Node, item.Path, problem is null, problem, trashedTo));
            progress?.Report(++done);
        }
        return new DeleteReport(outcomes, permanent);
    }

    /// <summary>
    /// Puts trashed items back where they were (only for a non-permanent delete, and only if
    /// nothing has taken their place). Returns how many were restored.
    /// </summary>
    public (int Restored, List<string> Problems) Undo(DeleteReport report)
    {
        var problems = new List<string>();
        int restored = 0;
        if (report.Permanent)
        {
            problems.Add("a permanent delete can't be undone");
            return (0, problems);
        }
        foreach (DeleteOutcome o in report.Succeeded)
        {
            if (o.TrashedTo is null || !Path.Exists(o.TrashedTo))
            {
                problems.Add($"{o.Path}: not found in the {Trash.Name} any more");
                continue;
            }
            if (Path.Exists(o.Path))
            {
                problems.Add($"{o.Path}: something else is there now");
                continue;
            }
            try
            {
                if (Directory.Exists(o.TrashedTo))
                {
                    Directory.Move(o.TrashedTo, o.Path);
                }
                else
                {
                    File.Move(o.TrashedTo, o.Path);
                }
                store.Restore(o.Node);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{o.Path}: {ex.Message}");
            }
        }
        return (restored, problems);
    }
}
