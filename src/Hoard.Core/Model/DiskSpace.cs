namespace Hoard.Core.Model;

/// <summary>Size and free space of the volume a folder is on (plan.md §7.2 Overview).</summary>
public sealed record DiskSpace(string Name, string Mount, long Total, long Free)
{
    /// <summary>Space in use on the whole volume (on APFS: the whole container).</summary>
    public long Used => Math.Max(0, Total - Free);

    /// <summary>The volume holding <paramref name="path"/>: the mount point that is its longest prefix.</summary>
    public static DiskSpace? For(string path)
    {
        try
        {
            DriveInfo? best = null;
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                string mount = d.RootDirectory.FullName;
                bool contains = mount == "/" || path == mount.TrimEnd('/')
                    || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);
                if (contains && d.IsReady && d.TotalSize > 0
                    && (best is null || mount.Length > best.RootDirectory.FullName.Length))
                {
                    best = d;
                }
            }
            if (best is null)
            {
                return null;
            }
            string m = best.RootDirectory.FullName;
            string name = m == "/" ? "startup disk" : Path.GetFileName(m.TrimEnd('/'));
            return new DiskSpace(name, m, best.TotalSize, best.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
