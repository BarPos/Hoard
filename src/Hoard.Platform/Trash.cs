using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hoard.Core.Actions;

namespace Hoard.Platform;

public static class Trashes
{
    public static ITrash ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new MacTrash()
        : OperatingSystem.IsLinux() ? new XdgTrash()
        : new NoTrash();
}

/// <summary>
/// macOS Trash through Foundation's <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>,
/// called via the Objective-C runtime so it works under Native AOT. This is what Finder uses,
/// so "Put Back" works and items land in the right Trash for their volume.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacTrash : ITrash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public string Name => "Trash";
    public bool IsAvailable => Foundation.Value != IntPtr.Zero;

    private static readonly Lazy<IntPtr> Foundation = new(() =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out IntPtr h) ? h : IntPtr.Zero);

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Sel(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static unsafe partial byte SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr* resulting, IntPtr* error);

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static partial IntPtr PoolPush();

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static partial void PoolPop(IntPtr pool);

    private static unsafe IntPtr NSString(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = utf8)
        {
            return SendPtr(GetClass("NSString"), Sel("stringWithUTF8String:"), (IntPtr)p);
        }
    }

    private static string? ToManaged(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    public unsafe string? MoveToTrash(string path)
    {
        if (!IsAvailable)
        {
            throw new IOException("Foundation isn't available");
        }
        IntPtr pool = PoolPush();
        try
        {
            IntPtr url = SendPtr(GetClass("NSURL"), Sel("fileURLWithPath:"), NSString(path));
            IntPtr fm = Send(GetClass("NSFileManager"), Sel("defaultManager"));
            IntPtr resulting = IntPtr.Zero, error = IntPtr.Zero;
            byte ok = SendTrash(fm, Sel("trashItemAtURL:resultingItemURL:error:"), url, &resulting, &error);
            if (ok == 0)
            {
                string message = ToManaged(Send(error, Sel("localizedDescription"))) ?? "the item couldn't be moved to the Trash";
                throw new IOException(message);
            }
            return resulting == IntPtr.Zero ? null : ToManaged(Send(resulting, Sel("path")));
        }
        finally
        {
            PoolPop(pool);
        }
    }
}

/// <summary>
/// The freedesktop.org Trash spec for the home trash (<c>$XDG_DATA_HOME/Trash</c>). Items on
/// other file systems are refused rather than copied (moving across devices could be slow and partial).
/// </summary>
public sealed class XdgTrash : ITrash
{
    public string Name => "Trash";
    public bool IsAvailable => true;

    private static string TrashDir()
    {
        string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data))
        {
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(data, "Trash");
    }

    public string? MoveToTrash(string path)
    {
        string trash = TrashDir();
        string files = Path.Combine(trash, "files"), info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        string name = Path.GetFileName(path.TrimEnd('/'));
        string target = Path.Combine(files, name);
        for (int i = 2; Path.Exists(target) || File.Exists(Path.Combine(info, Path.GetFileName(target) + ".trashinfo")); i++)
        {
            target = Path.Combine(files, $"{name}.{i}");
        }
        string infoFile = Path.Combine(info, Path.GetFileName(target) + ".trashinfo");
        File.WriteAllText(infoFile, $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }
        }
        catch
        {
            File.Delete(infoFile);
            throw;
        }
        return target;
    }
}

/// <summary>Used where no trash is implemented yet (Windows for now): only permanent delete is offered.</summary>
public sealed class NoTrash : ITrash
{
    public string Name => "Recycle Bin";
    public bool IsAvailable => false;
    public string? MoveToTrash(string path) => throw new IOException("moving to the Recycle Bin isn't supported yet");
}
