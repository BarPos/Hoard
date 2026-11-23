using System.Globalization;

namespace Hoard.Core.Formatting;

/// <summary>Human-readable sizes and counts (plan.md §7.4).</summary>
public static class Units
{
    private static readonly string[] Si = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] Iec = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// Formats like the mock screens: <c>512 B</c>, <c>4.0 KB</c>, <c>212 MB</c>, <c>31.2 GB</c>, <c>412.6 GB</c>.
    /// Below GB, values of 100 or more drop the decimal; GB and above always keep one.
    /// SI (powers of 1000) by default, IEC (powers of 1024) when <paramref name="iec"/> is true.
    /// </summary>
    public static string Size(long bytes, bool iec = false)
    {
        if (bytes < 0)
        {
            return "-" + Size(-bytes, iec);
        }
        double step = iec ? 1024 : 1000;
        string[] units = iec ? Iec : Si;
        if (bytes < step)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
        double v = bytes;
        int u = 0;
        while (v >= step && u < units.Length - 1)
        {
            v /= step;
            u++;
        }
        // Round first, so 999.95 KB becomes "1.0 MB" rather than "1000 KB".
        bool whole = v >= 100 && u < 3;
        if (Math.Round(v, whole ? 0 : 1) >= step && u < units.Length - 1)
        {
            v /= step;
            u++;
        }
        whole = v >= 100 && u < 3;
        string num = whole
            ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.0", CultureInfo.InvariantCulture);
        return num + " " + units[u];
    }

    /// <summary>Groups digits with spaces: <c>1 284 312</c>.</summary>
    public static string Count(long n) =>
        n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

    public static string Percent(double fraction) =>
        (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
