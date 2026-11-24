using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hoard.Core.Analysis;

namespace Hoard.Platform;

/// <summary>
/// Reads <c>ATTR_CMNEXT_PRIVATESIZE</c> with <c>getattrlist(2)</c>: the bytes of a file not shared
/// with any clone. Layouts from &lt;sys/attr.h&gt; (macOS 10.13+, APFS).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacSharedBlocks : ISharedBlocks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AttrList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttr;
        public uint VolAttr;
        public uint DirAttr;
        public uint FileAttr;
        public uint ForkAttr; // with FSOPT_ATTR_CMN_EXTENDED: ATTR_CMNEXT_*
    }

    private const ushort ATTR_BIT_MAP_COUNT = 5;
    private const uint ATTR_CMN_RETURNED_ATTRS = 0x80000000;
    private const uint ATTR_CMNEXT_PRIVATESIZE = 0x00000008;
    private const uint FSOPT_NOFOLLOW = 0x00000001;
    private const uint FSOPT_ATTR_CMN_EXTENDED = 0x00000020;

    [LibraryImport("libc", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int GetAttrList(string path, ref AttrList list, byte* buffer, nuint size, uint options);

    public unsafe long? PrivateBytes(string path)
    {
        var list = new AttrList
        {
            BitmapCount = ATTR_BIT_MAP_COUNT,
            CommonAttr = ATTR_CMN_RETURNED_ATTRS,
            ForkAttr = ATTR_CMNEXT_PRIVATESIZE,
        };
        // u_int32 length · attribute_set_t (5 × u_int32) · off_t private size
        byte* buf = stackalloc byte[64];
        if (GetAttrList(path, ref list, buf, 64, FSOPT_NOFOLLOW | FSOPT_ATTR_CMN_EXTENDED) != 0)
        {
            return null;
        }
        uint length = *(uint*)buf;
        uint returnedFork = *(uint*)(buf + 4 + 16);
        if ((returnedFork & ATTR_CMNEXT_PRIVATESIZE) == 0 || length < 32)
        {
            return null; // not APFS, or not supported here
        }
        return *(long*)(buf + 24);
    }
}

public static class SharedBlocks
{
    public static ISharedBlocks ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new MacSharedBlocks() : new NoSharedBlocks();
}
