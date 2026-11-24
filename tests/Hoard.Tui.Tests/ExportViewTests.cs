using Hoard.Core.Export;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public sealed class ExportViewTests(Fixture f) : IClassFixture<Fixture>, IDisposable
{
    private readonly string _out = Directory.CreateTempSubdirectory("hoard-export-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_out, true); } catch { }
    }

    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Browse };
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    [Fact]
    public void X_opens_the_dialog_with_a_preview()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar('x'), 10);
        Assert.NotNull(m.Export);
        Assert.EndsWith(".html", m.Export!.Path);
        string s = Render(m).ToString();
        Assert.Contains("Export", s);
        Assert.Contains("Pretty HTML", s);
        Assert.Contains("Preview", s);
        ctl.Apply(Cmd.Left, 10); // format: back to Pretty's neighbour (HTML)
        Assert.Equal(ExportFormat.Html, m.Export.Format);
        ctl.Apply(Cmd.Left, 10);
        ctl.Apply(Cmd.Left, 10); // Json
        Assert.Equal(ExportFormat.Json, m.Export.Format);
        Assert.EndsWith(".json", m.Export.Path);
        Assert.Contains("\"schema\": 1", Render(m).ToString()); // the live preview
        ctl.Apply(Cmd.Escape, 10);
        Assert.Null(m.Export);
    }

    [Fact]
    public void Enter_writes_the_file_and_the_toast_says_where()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Export, 10);
        m.Export!.Format = ExportFormat.Csv;
        m.Export.Path = Path.Combine(_out, "out.csv");
        ctl.Apply(Cmd.Open, 10);
        ctl.WaitForExport();
        Assert.Null(m.Export);
        Assert.Contains("exported CSV", m.Toast);
        Assert.StartsWith("path,kind,size", File.ReadAllText(Path.Combine(_out, "out.csv")));
    }

    [Fact]
    public void The_path_can_be_typed()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Export, 10);
        for (int i = 0; i < 5; i++)
        {
            ctl.Apply(Cmd.Down, 10);
        }
        ctl.Apply(Cmd.Open, 10);
        Assert.True(ctl.WantsText);
        m.Export!.Path = "";
        foreach (char ch in Path.Combine(_out, "typed.md"))
        {
            ctl.TypeChar(ch);
        }
        ctl.Backspace();
        ctl.TypeChar('d');
        ctl.Apply(Cmd.Open, 10); // done editing
        Assert.False(ctl.WantsText);
        Assert.True(m.Export.CustomPath);
        m.Export.Format = ExportFormat.Markdown;
        ctl.Apply(Cmd.Down, 10); // to the Export button
        ctl.Apply(Cmd.Open, 10);
        ctl.WaitForExport();
        Assert.StartsWith("# Hoard report", File.ReadAllText(Path.Combine(_out, "typed.md")));
    }

    [Fact]
    public void Draws_at_every_size()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Export, 10);
        foreach (int w in new[] { 60, 89, 90, 140 })
        {
            foreach (int h in new[] { 16, 24, 40 })
            {
                MemoryCanvas c = Render(m, w, h);
                Assert.Contains("Export", c.ToString());
            }
        }
    }
}
