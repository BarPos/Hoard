using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Tests;

public class SearchViewTests(Fixture f) : IClassFixture<Fixture>
{
    private (BrowserModel M, BrowserController C) Make()
    {
        var m = new BrowserModel(f.Session, null, () => Fixture.Now, _ => null) { View = ViewKind.Overview };
        return (m, new BrowserController(m, new FakeShell()));
    }

    private static MemoryCanvas Render(BrowserModel m, int w = 120, int h = 30)
    {
        var c = new MemoryCanvas(w, h);
        Screens.Browser(c, m);
        return c;
    }

    private static void Type(BrowserController ctl, string text)
    {
        foreach (char ch in text)
        {
            ctl.TypeChar(ch);
        }
    }

    [Fact]
    public void Slash_finds_anywhere_and_enter_jumps_there()
    {
        var (m, ctl) = Make();
        ctl.Apply(BrowserController.FromChar('/'), 10);
        Assert.True(ctl.WantsText);
        Type(ctl, "deep");
        m.WaitForSearch();
        string s = Render(m).ToString();
        Assert.Contains("Find in", s);
        Assert.Contains("a/b/c/deep.txt", s);
        Assert.Contains("1 match", s);
        ctl.Apply(Cmd.Open, 10);
        Assert.Null(m.Search);
        Assert.Equal(ViewKind.Browse, m.View);
        Assert.Equal("c", m.Store.Name(m.Current));
        Assert.Equal("deep.txt", m.Store.Name(m.SelectedNode!.Value));
    }

    [Fact]
    public void Filters_marks_and_backspace()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Find, 10);
        Type(ctl, "bin");
        m.WaitForSearch();
        int total = m.SearchResults!.Total;
        ctl.Apply(Cmd.NextView, 10); // tab: folders only
        m.WaitForSearch();
        Assert.True(m.SearchResults!.Total < total);
        ctl.Apply(Cmd.NextView, 10); // files only
        ctl.Apply(Cmd.SortNext, 10); // ^s: ≥ 1 MB
        m.WaitForSearch();
        Assert.Equal(0, m.SearchResults!.Total);
        ctl.Apply(Cmd.SortNext, 10);
        ctl.Apply(Cmd.SortNext, 10);
        ctl.Apply(Cmd.SortNext, 10); // back to any size
        m.WaitForSearch();
        ctl.Apply(Cmd.MarkAll, 10);  // ^a
        Assert.Equal(m.SearchResults!.Hits.Count, m.Marks.Count);
        ctl.Backspace();
        Assert.Equal("bi", m.Search!.Query);
        ctl.Apply(Cmd.Escape, 10);
        Assert.Null(m.Search);
    }

    [Fact]
    public void Ctrl_f_filters_the_open_list_and_esc_clears_it()
    {
        var (m, ctl) = Make();
        m.View = ViewKind.Browse;
        m.Enter(m.Store.Children(Hoard.Core.Model.NodeStore.Root).First(id => m.Store.Name(id) == "many"));
        ctl.Apply(Cmd.Find, 10);
        Type(ctl, "f1");
        ctl.Apply(Cmd.Filters, 10);
        Assert.Equal("f1", m.ListFilter);
        Assert.All(m.Rows, id => Assert.Contains("1", m.Store.Name(id)));
        Assert.Contains("matching “f1”", Render(m).Row(2));
        ctl.Apply(Cmd.Escape, 10);
        Assert.Null(m.ListFilter);
        Assert.Equal(30, m.Rows.Count);
    }

    [Fact]
    public void Draws_at_every_size()
    {
        var (m, ctl) = Make();
        ctl.Apply(Cmd.Find, 10);
        Type(ctl, "b");
        m.WaitForSearch();
        foreach (int w in new[] { 60, 90, 140 })
        {
            foreach (int h in new[] { 16, 24, 40 })
            {
                Assert.Contains("Find in", Render(m, w, h).ToString());
            }
        }
    }
}
