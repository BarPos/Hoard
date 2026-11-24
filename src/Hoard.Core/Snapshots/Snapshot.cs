using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Hoard.Core.Model;

namespace Hoard.Core.Snapshots;

/// <summary>One folder's totals in a snapshot.</summary>
public readonly record struct FolderTotals(long Size, long Apparent, long Files, long Dirs, uint MTime);

/// <summary>
/// The folder-level totals of one scan (plan.md §9.1): no individual files, so a million-file
/// scan stays small. Paths are relative to the scanned folder ("" is the folder itself).
/// </summary>
public sealed class Snapshot(string rootPath, DateTimeOffset takenAt, Dictionary<string, FolderTotals> folders)
{
    public string RootPath { get; } = rootPath;
    public DateTimeOffset TakenAt { get; } = takenAt;
    public Dictionary<string, FolderTotals> Folders { get; } = folders;
    /// <summary>The file it was loaded from or saved to; null for the live scan.</summary>
    public string? File { get; init; }

    public FolderTotals Root => Folders.TryGetValue("", out var r) ? r : default;

    /// <summary>Totals of every folder in a (finished or running) scan.</summary>
    public static Snapshot FromStore(NodeStore s, string rootPath, DateTimeOffset at, CancellationToken cancel = default)
    {
        var folders = new Dictionary<string, FolderTotals>(StringComparer.Ordinal);
        var stack = new Stack<(int Id, string Path)>();
        stack.Push((NodeStore.Root, ""));
        int n = 0;
        while (stack.Count > 0)
        {
            if ((++n & 4095) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }
            var (id, path) = stack.Pop();
            folders[path] = new FolderTotals(s.Size(id), s.Apparent(id), s.Files(id), s.Dirs(id), s.MTime(id));
            foreach (int child in s.Children(id))
            {
                if (s.Kind(child) == NodeKind.Directory)
                {
                    stack.Push((child, path.Length == 0 ? s.Name(child) : path + "/" + s.Name(child)));
                }
            }
        }
        return new Snapshot(rootPath, at, folders);
    }

    private static readonly byte[] Magic = "HOARDSN1"u8.ToArray();

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            fs.Write(Magic);
            using var z = new BrotliStream(fs, CompressionLevel.Optimal);
            using var w = new BinaryWriter(z, Encoding.UTF8);
            w.Write(RootPath);
            w.Write(TakenAt.ToUnixTimeSeconds());
            w.Write(Folders.Count);
            // Sorted so each path can share its start with the one before (prefix compression).
            string prev = "";
            foreach (var (path, t) in Folders.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                int common = 0;
                int max = Math.Min(prev.Length, path.Length);
                while (common < max && prev[common] == path[common])
                {
                    common++;
                }
                w.Write7BitEncodedInt(common);
                w.Write(path[common..]);
                w.Write7BitEncodedInt64(t.Size);
                w.Write7BitEncodedInt64(t.Apparent);
                w.Write7BitEncodedInt64(t.Files);
                w.Write7BitEncodedInt64(t.Dirs);
                w.Write(t.MTime);
                prev = path;
            }
        }
        System.IO.File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Reads only the header (root, time, totals) without the folders: for listing.</summary>
    public static Snapshot Load(string file, bool headerOnly = false)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        Span<byte> magic = stackalloc byte[8];
        fs.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a Hoard snapshot");
        }
        using var z = new BrotliStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(z, Encoding.UTF8);
        string root = r.ReadString();
        var at = DateTimeOffset.FromUnixTimeSeconds(r.ReadInt64());
        int count = r.ReadInt32();
        var folders = new Dictionary<string, FolderTotals>(headerOnly ? 1 : count, StringComparer.Ordinal);
        string prev = "";
        for (int i = 0; i < count; i++)
        {
            int common = r.Read7BitEncodedInt();
            string path = prev[..common] + r.ReadString();
            var t = new FolderTotals(r.Read7BitEncodedInt64(), r.Read7BitEncodedInt64(), r.Read7BitEncodedInt64(), r.Read7BitEncodedInt64(), r.ReadUInt32());
            if (!headerOnly || path.Length == 0)
            {
                folders[path] = t;
            }
            if (headerOnly && i == 0)
            {
                break; // "" sorts first: the root's totals are all a listing needs
            }
            prev = path;
        }
        return new Snapshot(root, at, folders) { File = file };
    }
}

/// <summary>Where snapshots live and how many are kept (plan.md §9.1).</summary>
public sealed class SnapshotStore(string directory, int keep = 30)
{
    public string Directory { get; } = directory;
    public int Keep { get; set; } = keep;

    /// <summary><c>$XDG_DATA_HOME/hoard/snapshots</c>, else <c>~/.local/share/hoard/snapshots</c>.</summary>
    public static string DefaultDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string baseDir = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "hoard", "snapshots");
    }

    /// <summary>A short stable tag for a scanned folder, so snapshots of different folders don't mix.</summary>
    public static string RootTag(string rootPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.TrimEnd('/') is { Length: > 0 } p ? p : "/")))[..10];

    /// <summary>Snapshots of this folder, newest first (headers only).</summary>
    public List<Snapshot> List(string rootPath)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }
        var list = new List<Snapshot>();
        foreach (string f in System.IO.Directory.EnumerateFiles(Directory, RootTag(rootPath) + "-*.hsnap"))
        {
            try
            {
                list.Add(Snapshot.Load(f, headerOnly: true));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
            {
                // a damaged or half-written file: skip it
            }
        }
        return [.. list.OrderByDescending(s => s.TakenAt)];
    }

    /// <summary>Saves and prunes the oldest beyond <see cref="Keep"/>. Returns the file.</summary>
    public string Save(Snapshot s)
    {
        string name = $"{RootTag(s.RootPath)}-{s.TakenAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.hsnap";
        string file = Path.Combine(Directory, name);
        s.Save(file);
        foreach (Snapshot old in List(s.RootPath).Skip(Math.Max(1, Keep)))
        {
            Delete(old);
        }
        return file;
    }

    public void Delete(Snapshot s)
    {
        if (s.File is not null)
        {
            System.IO.File.Delete(s.File);
        }
    }
}

/// <summary>A change in one folder between two snapshots.</summary>
public sealed record FolderChange(string Path, string Name, long Before, long After, long FilesBefore, long FilesAfter, bool Added, bool Removed)
{
    public long Delta => After - Before;
}

public static class SnapshotDiff
{
    /// <summary>
    /// How each folder directly inside <paramref name="folder"/> changed from <paramref name="base"/>
    /// to <paramref name="compare"/>, biggest change first. Files directly in the folder show as one row.
    /// </summary>
    public static List<FolderChange> Children(Snapshot @base, Snapshot compare, string folder)
    {
        string prefix = folder.Length == 0 ? "" : folder + "/";
        static IEnumerable<string> Kids(Snapshot s, string prefix) => s.Folders.Keys.Where(k =>
            k.Length > prefix.Length && k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) < 0);
        var names = new HashSet<string>(Kids(@base, prefix), StringComparer.Ordinal);
        names.UnionWith(Kids(compare, prefix));
        var changes = new List<FolderChange>();
        foreach (string path in names)
        {
            bool inBase = @base.Folders.TryGetValue(path, out var b), inNow = compare.Folders.TryGetValue(path, out var a);
            changes.Add(new FolderChange(path, path[prefix.Length..], b.Size, a.Size, b.Files, a.Files, !inBase, !inNow));
        }
        // Whatever isn't in a subfolder: files sitting directly in this folder.
        @base.Folders.TryGetValue(folder, out var fb);
        compare.Folders.TryGetValue(folder, out var fa);
        long kidsBefore = changes.Sum(c => c.Before), kidsAfter = changes.Sum(c => c.After);
        long looseBefore = fb.Size - kidsBefore, looseAfter = fa.Size - kidsAfter;
        if (looseBefore != looseAfter)
        {
            changes.Add(new FolderChange("", "(files directly here)", looseBefore, looseAfter, 0, 0, false, false));
        }
        return [.. changes.Where(c => c.Delta != 0).OrderByDescending(c => Math.Abs(c.Delta)).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }
}
