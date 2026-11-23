namespace Hoard.Core.Scanning;

public enum EntryType : byte { File, Directory, Symlink, Other }

/// <summary>What the scanner needs to know about one path, read without following symlinks.</summary>
public readonly record struct EntryInfo(
    EntryType Type,
    long Allocated,
    long Apparent,
    ulong Device,
    ulong Inode,
    uint LinkCount,
    long MTime,
    long ATime);

/// <summary>Per-OS access to file metadata (plan.md §4: Core ← Platform).</summary>
public interface IFileProbe
{
    /// <summary>Human-readable name, e.g. "macOS lstat".</summary>
    string Name { get; }

    /// <summary>True when sizes are real allocated sizes and inode / device numbers are available.</summary>
    bool IsExact { get; }

    /// <summary>Reads metadata for <paramref name="path"/> without following symlinks.</summary>
    bool TryGetInfo(string path, out EntryInfo info, out int errno);
}
