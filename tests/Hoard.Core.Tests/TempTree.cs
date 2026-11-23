using System.Diagnostics;

namespace Hoard.Core.Tests;

/// <summary>A throwaway directory tree under the system temp folder, removed on dispose.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hoard-test-" + Guid.NewGuid().ToString("N")[..10]);

    public TempTree() => Directory.CreateDirectory(Root);

    public string P(string rel) => Path.Combine(Root, rel);

    public string Dir(string rel) => Directory.CreateDirectory(P(rel)).FullName;

    public string File(string rel, int bytes)
    {
        string path = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    public static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} failed: {p.StandardError.ReadToEnd()}");
        }
    }

    public static string Output(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = Process.Start(psi)!;
        string s = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return s;
    }

    public void Dispose()
    {
        // Restore permissions changed by tests so cleanup can't fail.
        foreach (string d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Prepend(Root))
        {
            try { System.IO.File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        }
        foreach (string d in Directory.GetDirectories(Root, "*", SearchOption.TopDirectoryOnly))
        {
            try { System.IO.File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        }
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
