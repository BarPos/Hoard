using Hoard.Core.Analysis;
using Hoard.Core.Config;

namespace Hoard.Core.Tests;

public class SettingsFileTests
{
    [Fact]
    public void Saves_and_loads_every_setting()
    {
        using var t = new TempTree();
        string path = t.P("cfg/hoard/config.toml"); // folders are created on save
        var s = new Settings
        {
            Theme = "neon",
            Mouse = false,
            ApparentSizes = true,
            CrossFileSystems = true,
            Threads = 4,
            Exclude = ["**/.git/objects", "~/Library/Mobile Documents", "has \"quotes\" # and a hash"],
            BigOld = new BigOldOptions(250_000_000, 730, AgeAxis.Accessed),
        };
        SettingsFile.Save(s, path);
        var (back, problems) = SettingsFile.Load(path);
        Assert.Empty(problems);
        Assert.Equal("neon", back.Theme);
        Assert.False(back.Mouse);
        Assert.True(back.ApparentSizes);
        Assert.True(back.CrossFileSystems);
        Assert.Equal(4, back.Threads);
        Assert.Equal(s.Exclude, back.Exclude);
        Assert.Equal(s.BigOld, back.BigOld);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_missing_file_means_defaults()
    {
        var (s, problems) = SettingsFile.Load("/nonexistent/hoard/config.toml");
        Assert.Empty(problems);
        Assert.Equal("hoard", s.Theme);
        Assert.True(s.Mouse);
        Assert.Equal(BigOldOptions.Default, s.BigOld);
    }

    [Fact]
    public void Bad_lines_are_reported_and_the_rest_still_loads()
    {
        using var t = new TempTree();
        string path = t.P("config.toml");
        File.WriteAllText(path, """
            # hand-edited
            theme = "frost"   # nice
            mouse = maybe
            colour = "red"
            this line is wrong
            threads = 1_000

            [big_old]
            min_age_days = 90
            based_on = "created"
            """);
        var (s, problems) = SettingsFile.Load(path);
        Assert.Equal("frost", s.Theme);
        Assert.True(s.Mouse); // bad value: default kept
        Assert.Equal(256, s.Threads); // clamped
        Assert.Equal(90, s.BigOld.MinAgeDays);
        Assert.Equal(AgeAxis.Modified, s.BigOld.Axis);
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("line 3") && p.Contains("mouse"));
        Assert.Contains(problems, p => p.Contains("colour") && p.Contains("unknown"));
    }

    [Fact]
    public void Lives_in_the_xdg_config_folder()
    {
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/xdg-test");
            Assert.Equal("/tmp/xdg-test/hoard/config.toml", SettingsFile.DefaultPath());
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(Path.Combine(home, ".config", "hoard", "config.toml"), SettingsFile.DefaultPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old);
        }
    }
}
