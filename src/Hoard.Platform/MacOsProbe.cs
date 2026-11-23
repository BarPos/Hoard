using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hoard.Core.Scanning;

namespace Hoard.Platform;

/// <summary>
/// macOS probe using <c>lstat(2)</c>: real allocated size (<c>st_blocks × 512</c>), inode,
/// device and link count. The struct layout below is the 64-bit-inode <c>struct stat</c> from
/// &lt;sys/stat.h&gt;, which is what <c>lstat</c> uses on arm64 and on x86_64 since 10.6
/// (verified on arm64 only).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacOsProbe : IFileProbe
{
    public string Name => "macOS lstat";
    public bool IsExact => true;

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Stat
    {
        [FieldOffset(0)] public int Dev;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort NLink;
        [FieldOffset(8)] public ulong Ino;
        [FieldOffset(32)] public long ATimeSec;
        [FieldOffset(48)] public long MTimeSec;
        [FieldOffset(96)] public long Size;
        [FieldOffset(104)] public long Blocks;
    }

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LStat(string path, out Stat buf);

    private const int S_IFMT = 0xF000;
    private const int S_IFDIR = 0x4000;
    private const int S_IFREG = 0x8000;
    private const int S_IFLNK = 0xA000;

    public bool TryGetInfo(string path, out EntryInfo info, out int errno)
    {
        if (LStat(path, out Stat st) != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            info = default;
            return false;
        }
        errno = 0;
        EntryType type = (st.Mode & S_IFMT) switch
        {
            S_IFDIR => EntryType.Directory,
            S_IFREG => EntryType.File,
            S_IFLNK => EntryType.Symlink,
            _ => EntryType.Other,
        };
        info = new EntryInfo(type, st.Blocks * 512, st.Size, unchecked((uint)st.Dev), st.Ino, st.NLink, st.MTimeSec, st.ATimeSec);
        return true;
    }
}
