using System.Diagnostics;
using System.Text.Json;
using Hoard.Core.Analysis;
using Hoard.Core.Export;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public sealed class ExporterTests : IDisposable
{
    private readonly TempTree _t = new();
    private readonly ScanResult _r;

    public ExporterTests()
    {
        _t.File("web/package.json", 100);
        _t.File("web/node_modules/lib/index.js", 200_000);
        _t.File("docs/report, \"final\".pdf", 50_000); // a comma and quotes, for CSV
        _t.File("docs/<script>alert(1)</script>.txt", 1_000); // must stay inert in HTML
        _t.File("big.iso", 300_000);
        _r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(_t.Root);
    }

    public void Dispose() => _t.Dispose();

    private ExportData Data() => new()
    {
        Store = _r.Store,
        RootPath = _r.RootPath,
        Roots = [NodeStore.Root],
        Types = FileTypes.Analyze(_r.Store),
        Junk = JunkFinder.Find(_r.Store, _r.RootPath, home: "/nonexistent"),
        BigOld = BigOld.Find(_r.Store, new BigOldOptions(10_000, 0, AgeAxis.Modified), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400),
        Empty = EmptyFinder.Find(_r.Store),
        Age = AgeBuckets.Analyze(_r.Store, NodeStore.Root, AgeAxis.Modified, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 8),
    };

    private string Out(ExportFormat f, int depth = 0) => Exporter.WriteToString(Data(), new ExportOptions(f, depth));

    [Fact]
    public void Json_is_the_whole_tree_with_metadata()
    {
        using JsonDocument doc = JsonDocument.Parse(Out(ExportFormat.Json));
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        JsonElement top = root.GetProperty("items")[0];
        Assert.Equal(_r.Store.Size(NodeStore.Root), top.GetProperty("size").GetInt64());
        Assert.Equal("big.iso", top.GetProperty("children")[0].GetProperty("name").GetString());
        Assert.Contains(top.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "docs" && c.TryGetProperty("children", out _));
    }

    [Fact]
    public void Depth_limits_and_folders_only_work()
    {
        using JsonDocument doc = JsonDocument.Parse(Exporter.WriteToString(Data(), new ExportOptions(ExportFormat.Json, Depth: 1, IncludeFiles: false)));
        JsonElement top = doc.RootElement.GetProperty("items")[0];
        Assert.All(top.GetProperty("children").EnumerateArray(), c =>
        {
            Assert.Equal("dir", c.GetProperty("kind").GetString());
            Assert.False(c.TryGetProperty("children", out _));
        });
        Assert.Equal(1, top.GetProperty("otherCount").GetInt32()); // big.iso, left out as a file
    }

    [Fact]
    public void Csv_quotes_awkward_names()
    {
        string[] lines = Out(ExportFormat.Csv).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("path,kind,size,apparent,files,dirs,modified,depth", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("\"" + _r.RootPath + "/docs/report, \"\"final\"\".pdf\",file,", StringComparison.Ordinal));
    }

    [Fact]
    public void Plain_html_has_no_styles_or_scripts_and_escapes_names()
    {
        string html = Out(ExportFormat.Html);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("<script", html); // the file name is escaped, not markup
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<details open>", html);
        Assert.Contains("<meter", html);
        Assert.Contains("id=\"junk\"", html);
    }

    [Fact]
    public void Markdown_has_tables()
    {
        string md = Out(ExportFormat.Markdown);
        Assert.StartsWith("# Hoard report: ", md);
        Assert.Contains("| big.iso |", md);
        Assert.Contains("## Known junk", md);
        Assert.Contains("## By type", md);
    }

    [Fact]
    public void Ncdu_follows_format_1_2()
    {
        using JsonDocument doc = JsonDocument.Parse(Out(ExportFormat.Ncdu));
        JsonElement a = doc.RootElement;
        Assert.Equal(1, a[0].GetInt32());
        Assert.Equal(2, a[1].GetInt32());
        Assert.Equal("hoard", a[2].GetProperty("progname").GetString());
        JsonElement rootDir = a[3];
        Assert.Equal(JsonValueKind.Array, rootDir.ValueKind);
        Assert.Equal(_r.RootPath, rootDir[0].GetProperty("name").GetString());
        Assert.Contains(rootDir.EnumerateArray().Skip(1), e => e.ValueKind == JsonValueKind.Object && e.GetProperty("name").GetString() == "big.iso" && e.GetProperty("dsize").GetInt64() > 0);
    }

    [Fact]
    public void Pretty_html_with_depth_everything_goes_all_the_way_down()
    {
        using var t = new TempTree();
        t.File("l1/l2/l3/l4/l5/l6/l7/l8/deepest.bin", 10_000);
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        var data = new ExportData { Store = r.Store, RootPath = r.RootPath, Roots = [NodeStore.Root] };
        Assert.Contains("\"deepest.bin\"", Exporter.WriteToString(data, new ExportOptions(ExportFormat.PrettyHtml, 0)));
        Assert.DoesNotContain("\"deepest.bin\"", Exporter.WriteToString(data, new ExportOptions(ExportFormat.PrettyHtml, 3)));
    }

    [Fact]
    public void Pretty_html_is_filled_in_and_its_script_parses()
    {
        string html = Out(ExportFormat.PrettyHtml);
        Assert.DoesNotContain("@@", html);
        Assert.Contains("const META = {", html);
        Assert.Contains("\"big.iso\"", html);
        Assert.DoesNotContain("<script>alert", html); // names are JSON strings with < escaped
        Assert.DoesNotContain("412.6", html);
        int start = html.IndexOf("<script>", StringComparison.Ordinal) + 8, end = html.LastIndexOf("</script>", StringComparison.Ordinal);
        string script = html[start..end];
        string node = new[] { "/opt/homebrew/bin/node", "/usr/local/bin/node", "/usr/bin/node" }.FirstOrDefault(File.Exists) ?? "";
        if (node.Length == 0)
        {
            return; // no Node.js here: the checks above still ran
        }
        string file = _t.P("report-script.js");
        File.WriteAllText(file, script);
        var psi = new ProcessStartInfo(node) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("--check");
        psi.ArgumentList.Add(file);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "the report's script doesn't parse: " + err);
    }

    [Theory]
    [InlineData("1500", 1500)]
    [InlineData("1.5GB", 1_500_000_000)]
    [InlineData("200 MB", 200_000_000)]
    [InlineData("10k", 10_000)]
    [InlineData("2GiB", 2L * 1024 * 1024 * 1024)]
    public void Sizes_parse(string text, long bytes)
    {
        Assert.True(Units.TryParseSize(text, out long got));
        Assert.Equal(bytes, got);
    }

    [Theory]
    [InlineData("")]
    [InlineData("big")]
    [InlineData("10 parsecs")]
    public void Bad_sizes_dont_parse(string text) => Assert.False(Units.TryParseSize(text, out _));
}
