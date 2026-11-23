using System.Runtime.InteropServices;
using Hoard.Core.Scanning;

namespace Hoard.Platform;

public static class FileProbes
{
    /// <summary>The best probe for the current OS and CPU.</summary>
    public static IFileProbe ForCurrentPlatform()
    {
        // The lstat struct layout has only been verified on macOS arm64 (plan.md §12, M0).
        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            return new MacOsProbe();
        }
        return new PortableProbe();
    }
}
