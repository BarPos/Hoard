using Hoard.Core.Analysis;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Browser;

/// <summary>One line of the settings screen: what it shows, and how ←→ change it.</summary>
public sealed record SettingsItem(string Section, string Label, Func<BrowserModel, string> Value, Action<BrowserModel, int> Change, string Note);

/// <summary>Everything the settings screen offers (plan.md §7.6; mock 16-settings.sh). Each change is saved at once.</summary>
public static class SettingsItems
{
    private static readonly int[] ThreadSteps = [0, 1, 2, 4, 8, 12, 16];

    private static int StepIn(int[] steps, int current, int delta)
    {
        int i = Array.IndexOf(steps, current);
        return steps[Math.Clamp((i < 0 ? 0 : i) + delta, 0, steps.Length - 1)];
    }

    public static readonly SettingsItem[] All =
    [
        new("Appearance", "Theme", m => m.Theme.Name, (m, d) =>
        {
            int i = Array.IndexOf(Theme.All, m.Theme);
            m.Theme = Theme.All[(i + d + Theme.All.Length) % Theme.All.Length];
            m.Settings.Theme = m.Theme.Name;
        }, "also t anywhere · the colours change at once"),
        new("Appearance", "Units", m => m.Settings.Iec ? "IEC · GiB (1024)" : "SI · GB (1000)", (m, _) =>
        {
            m.Settings.Iec = !m.Settings.Iec;
            Hoard.Core.Formatting.Units.DefaultIec = m.Settings.Iec;
        }, "SI matches Finder and disk labels · IEC is what some tools (du -h) use"),
        new("Appearance", "Glyphs", m => m.Settings.AsciiGlyphs ? "plain ASCII" : "Unicode", (m, _) =>
        {
            m.Settings.AsciiGlyphs = !m.Settings.AsciiGlyphs;
            Glyphs.Ascii = m.Settings.AsciiGlyphs;
        }, "plain ASCII if your terminal or font shows boxes instead of lines and bars"),
        new("Appearance", "Animations", m => m.Settings.Animations ? "on" : "off", (m, _) => m.Settings.Animations = !m.Settings.Animations,
            "the splash at start · HOARD_ANIMATE=0 turns it off too"),
        new("Appearance", "Tips", m => m.Settings.Tips ? "one a day" : "off", (m, _) => m.Settings.Tips = !m.Settings.Tips,
            "a tip on the splash and scanning screens"),
        new("Appearance", "Tour", m => m.Settings.TourDone ? "seen" : "next start", (m, _) => m.Settings.TourDone = !m.Settings.TourDone,
            "the 3-step introduction; set to “next start” to see it again"),
        new("Appearance", "Mouse", m => m.Settings.Mouse ? "on" : "off", (m, _) => m.Settings.Mouse = !m.Settings.Mouse,
            "click, double-click and wheel · turn off if your terminal misbehaves"),

        new("Scanning", "Size shown", m => m.Apparent ? "apparent" : "on-disk", (m, _) =>
        {
            m.ToggleSizeMode();
            m.Settings.ApparentSizes = m.Apparent;
        }, "on-disk: space actually used · apparent: file lengths · z toggles for now"),
        new("Scanning", "Other disks", m => m.Settings.CrossFileSystems ? "scan into them" : "stop at them", (m, _) =>
            m.Settings.CrossFileSystems = !m.Settings.CrossFileSystems, "folders on other volumes · applies from the next scan"),
        new("Scanning", "Worker threads", m => m.Settings.Threads == 0 ? "automatic" : m.Settings.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (m, d) => m.Settings.Threads = StepIn(ThreadSteps, m.Settings.Threads, d), "automatic uses your cores · applies from the next scan"),

        new("Snapshots", "After each scan", m => m.Settings.AutoSnapshot ? "save one" : "don't save", (m, _) =>
            m.Settings.AutoSnapshot = !m.Settings.AutoSnapshot, "folder totals only, for the Snaps tab (0); P saves one any time"),
        new("Snapshots", "Keep", m => m.Settings.KeepSnapshots.ToString(System.Globalization.CultureInfo.InvariantCulture) + " per folder",
            (m, d) =>
            {
                m.Settings.KeepSnapshots = StepIn([5, 10, 20, 30, 50, 100], m.Settings.KeepSnapshots, d);
                if (m.Snapshots is { } store)
                {
                    store.Keep = m.Settings.KeepSnapshots;
                }
            }, "older ones are deleted when a new one is saved"),

        new("Big & old", "Size at least", m => Hoard.Core.Formatting.Units.Size(m.BigOldOptions.MinSize), (m, d) => StepBigOld(m, 0, d),
            "the default filter for the Big & old tab"),
        new("Big & old", "Untouched for", m => BrowserModel.AgeWords(m.BigOldOptions.MinAgeDays) + " or more", (m, d) => StepBigOld(m, 1, d),
            "the default filter for the Big & old tab"),
        new("Big & old", "Based on", m => m.BigOldOptions.Axis == AgeAxis.Accessed ? "last opened" : "last modified", (m, d) => StepBigOld(m, 2, d),
            "macOS updates “last opened” lazily, so modified is the safer default"),
    ];

    private static void StepBigOld(BrowserModel m, int row, int delta)
    {
        int keep = m.FilterRow;
        m.FilterRow = row;
        m.StepFilter(delta);
        m.FilterRow = keep;
        m.Settings.BigOld = m.BigOldOptions;
    }
}
