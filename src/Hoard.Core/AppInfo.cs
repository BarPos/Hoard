using System.Reflection;

namespace Hoard.Core;

/// <summary>Version and name shown by the app, the CLI and the installer (from the VERSION file at build time).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
