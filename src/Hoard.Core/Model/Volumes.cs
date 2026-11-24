namespace Hoard.Core.Model;

public enum VolumeKind { Internal, External, Network, Removable, Other }

/// <summary>A mounted volume (plan.md §7.2 volumes, mock 01-volumes.sh).</summary>
public sealed record Volume(string Name, string Mount, string Format, VolumeKind Kind, long Total, long Free, bool System)
{
    public long Used => Math.Max(0, Total - Free);
    public double UsedFraction => Total > 0 ? (double)Used / Total : 0;
}

public static class Volumes
{
    private static readonly HashSet<string> Pseudo = new(StringComparer.OrdinalIgnoreCase) { "devfs", "autofs", "nullfs", "fdesc", "tmpfs", "proc", "sysfs", "overlay" };

    /// <summary>
    /// Mounted volumes worth scanning, fullest first. System volumes (macOS's Preboot, VM, the Data
    /// half of the startup disk…) share the startup disk's space; they're listed with
    /// <see cref="Volume.System"/> set, and hidden unless asked for.
    /// </summary>
    public static List<Volume> List()
    {
        var list = new List<Volume>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.TotalSize <= 0 || Pseudo.Contains(d.DriveFormat))
                {
                    continue;
                }
                string mount = d.RootDirectory.FullName;
                bool system = mount.StartsWith("/System/Volumes/", StringComparison.Ordinal) || mount.StartsWith("/private/", StringComparison.Ordinal)
                              || mount.StartsWith("/Library/Developer/CoreSimulator", StringComparison.Ordinal);
                VolumeKind kind = d.DriveType switch
                {
                    DriveType.Network => VolumeKind.Network,
                    DriveType.Removable => VolumeKind.Removable,
                    DriveType.Fixed when mount == "/" || system => VolumeKind.Internal,
                    DriveType.Fixed => VolumeKind.External,
                    _ => VolumeKind.Other,
                };
                string name = mount == "/" ? "Startup disk"
                    : !string.IsNullOrWhiteSpace(d.VolumeLabel) && d.VolumeLabel != mount ? d.VolumeLabel
                    : Path.GetFileName(mount.TrimEnd('/'));
                list.Add(new Volume(name, mount, d.DriveFormat, kind, d.TotalSize, d.AvailableFreeSpace, system));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // an unplugged or unreadable disk: skip it
            }
        }
        return [.. list.OrderBy(v => v.System).ThenByDescending(v => v.UsedFraction).ThenBy(v => v.Mount, StringComparer.Ordinal)];
    }
}
