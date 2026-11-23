using Hoard.Core.Analysis;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Core.Tests;

public class JunkFinderTests
{
    private static (ScanResult R, JunkReport J) Run(TempTree t, string? home = null)
    {
        ScanResult r = new Scanner(FileProbes.ForCurrentPlatform()).Scan(t.Root);
        return (r, JunkFinder.Find(r.Store, r.RootPath, home: home ?? "/nonexistent-home"));
    }

    private static List<string> Found(ScanResult r, JunkReport j, string ruleId) =>
        j.Categories.FirstOrDefault(c => c.Rule.Id == ruleId)?.Items.Select(r.Store.RelativePath).Order().ToList() ?? [];

    [Fact]
    public void Project_folders_need_their_project_file()
    {
        using var t = new TempTree();
        t.File("web/package.json", 100);
        t.File("web/node_modules/react/index.js", 5000);
        t.File("notes/node_modules/x.txt", 5000);           // no package.json: leave alone
        t.File("app/App.csproj", 100);
        t.File("app/bin/Debug/app.dll", 8000);
        t.File("app/obj/project.assets.json", 3000);
        t.File("plain/bin/tool", 4000);                    // no project file: leave alone
        t.File("crate/Cargo.toml", 100);
        t.File("crate/target/debug/crate", 9000);
        t.File("other/target/x", 9000);                    // not a Rust/Maven project
        var (r, j) = Run(t);
        Assert.Equal(["web/node_modules"], Found(r, j, "node-modules"));
        Assert.Equal(["app/bin", "app/obj"], Found(r, j, "dotnet-build"));
        Assert.Equal(["crate/target"], Found(r, j, "rust-target"));
    }

    [Fact]
    public void Nested_and_installed_trees_are_not_double_counted_or_touched()
    {
        using var t = new TempTree();
        t.File("proj/package.json", 100);
        t.File("proj/node_modules/a/package.json", 100);
        t.File("proj/node_modules/a/node_modules/b/index.js", 5000);  // inside the outer match
        t.File("lib/node_modules/npm/package.json", 100);
        t.File("lib/node_modules/npm/node_modules/dep/index.js", 5000); // npm's own deps
        t.File("Cellar/tool/1.0/libexec/package.json", 100);
        t.File("Cellar/tool/1.0/libexec/node_modules/x/i.js", 5000);    // Homebrew-installed
        var (r, j) = Run(t);
        Assert.Equal(["proj/node_modules"], Found(r, j, "node-modules"));
    }

    [Fact]
    public void Python_caches_and_virtual_environments()
    {
        using var t = new TempTree();
        t.File("svc/__pycache__/m.cpython-312.pyc", 2000);
        t.File("svc/.venv/pyvenv.cfg", 50);
        t.File("svc/.venv/lib/python3.12/site-packages/x.py", 9000);
        t.File("svc/env/settings.yaml", 300);              // a folder named env that isn't a venv
        var (r, j) = Run(t);
        Assert.Equal(["svc/__pycache__"], Found(r, j, "python-caches"));
        Assert.Equal(["svc/.venv"], Found(r, j, "python-venv"));
        Assert.Equal(Safety.Review, j.Categories.Single(c => c.Rule.Id == "python-venv").Rule.Safety);
    }

    [Fact]
    public void Home_path_rules_use_the_home_folder()
    {
        using var t = new TempTree();
        t.File("Library/Caches/com.example.app/cache.db", 7000);
        t.File("Library/Caches/com.other/blob", 3000);
        t.File("Library/Developer/Xcode/DerivedData/Proj-abc/Build/x.o", 9000);
        t.File("Library/Logs/app.log", 1000);
        t.File("Downloads/Installer.dmg", 6000);
        t.File("Downloads/report.pdf", 6000);
        var (r, j) = Run(t, home: t.Root);
        Assert.Equal(["Library/Caches/com.example.app", "Library/Caches/com.other"], Found(r, j, "user-caches"));
        Assert.Equal(["Library/Developer/Xcode/DerivedData/Proj-abc"], Found(r, j, "xcode-derived-data"));
        Assert.Equal(["Library/Logs/app.log"], Found(r, j, "logs"));
        Assert.Equal(["Downloads/Installer.dmg"], Found(r, j, "installers"));
        // Without that home, none of the home-only rules fire.
        var (_, j2) = Run(t);
        Assert.DoesNotContain(j2.Categories, c => c.Rule.Id is "user-caches" or "installers");
    }

    [Fact]
    public void Totals_skip_deleted_items_and_categories_are_biggest_first()
    {
        using var t = new TempTree();
        t.File("a/package.json", 10);
        t.File("a/node_modules/big.js", 90_000);
        t.File("b/x/__pycache__/m.pyc", 5_000);
        var (r, j) = Run(t);
        Assert.Equal("node-modules", j.Categories[0].Rule.Id);
        long before = j.Total(r.Store);
        int nm = j.Categories[0].Items.Single();
        r.Store.MarkDeleted(nm);
        Assert.Equal(before - r.Store.Size(nm), j.Total(r.Store));
        Assert.Equal(0, j.Categories[0].Count(r.Store));
    }

    [Fact]
    public void Every_built_in_rule_is_well_formed()
    {
        Assert.Equal(JunkRules.BuiltIn.Count, JunkRules.BuiltIn.Select(r => r.Id).Distinct().Count());
        Assert.All(JunkRules.BuiltIn, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Why));
            Assert.True(r.Names is { Length: > 0 } || r.Paths is { Length: > 0 }, r.Id);
        });
    }
}
