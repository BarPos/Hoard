using Hoard.Core.Scanning;

namespace Hoard.Platform;

/// <summary>
/// Fallback for platforms without a native probe yet (Linux, Windows): apparent sizes only,
/// no inode / device numbers, so hard links are counted per path and mount points are not
/// detected. <see cref="IsExact"/> is false and the UI says so.
/// </summary>
public sealed class PortableProbe : IFileProbe
{
    public string Name => "portable (apparent sizes)";
    public bool IsExact => false;

    public bool TryGetInfo(string path, out EntryInfo info, out int errno)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists && !Directory.Exists(path))
            {
                errno = 2;
                info = default;
                return false;
            }
            FileAttributes a = fi.Attributes;
            EntryType type = fi.LinkTarget is not null ? EntryType.Symlink
                : (a & FileAttributes.Directory) != 0 ? EntryType.Directory
                : EntryType.File;
            long len = type == EntryType.File ? fi.Length : 0;
            info = new EntryInfo(type, len, len, 0, 0, 1,
                new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                new DateTimeOffset(fi.LastAccessTimeUtc).ToUnixTimeSeconds());
            errno = 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errno = 13;
            info = default;
            return false;
        }
    }
}
