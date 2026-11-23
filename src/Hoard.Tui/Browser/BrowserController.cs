using System.Diagnostics;
using Hoard.Core.Actions;
using Hoard.Core.Analysis;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Browser;

public enum Cmd
{
    None, Up, Down, Left, Right, PageUp, PageDown, Top, Bottom, Open, Back,
    SortNext, SortReverse, ToggleSize, NextTheme, Help, Reveal, CopyPath, Escape, Quit,
    NextView, ViewBrowse, ViewMap, DepthUp, DepthDown,
    Mark, MarkAll, ClearOrUndo, Delete,
    ViewJunk, Clean, Why,
}

/// <summary>Operating-system actions, swappable in tests.</summary>
public interface IShell
{
    /// <summary>Shows the item in Finder / Explorer / the file manager. Returns a message for the user.</summary>
    string Reveal(string path);
    string Copy(string text);
}

public sealed class SystemShell : IShell
{
    public string Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Run("open", "-R", path);
                return "revealed in Finder";
            }
            if (OperatingSystem.IsWindows())
            {
                Run("explorer.exe", "/select," + path);
                return "revealed in Explorer";
            }
            Run("xdg-open", Path.GetDirectoryName(path) ?? path);
            return "opened the containing folder";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "couldn't open a file manager: " + ex.Message;
        }
    }

    public string Copy(string text)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return "copy isn't supported on this OS yet: " + text;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pbcopy") { RedirectStandardInput = true })!;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(2000);
            return "copied " + text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "couldn't copy: " + ex.Message;
        }
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>Turns keys and mouse events into model changes (plan.md §7.1a key table).</summary>
public sealed class BrowserController(BrowserModel model, IShell shell, Deleter? deleter = null)
{
    public BrowserModel Model { get; } = model;
    public bool QuitRequested { get; private set; }
    /// <summary>The last delete that went to the Trash, for undo (<c>u</c>).</summary>
    public DeleteReport? LastDelete { get; private set; }
    private Task<DeleteReport>? _running;
    /// <summary>What was on screen last frame; the treemap needs it to move between blocks.</summary>
    public HitMap LastHits { get; set; } = new();

    public static Cmd FromChar(char c) => c switch
    {
        'j' => Cmd.Down,
        'k' => Cmd.Up,
        'l' => Cmd.Right,
        'h' => Cmd.Left,
        '2' => Cmd.ViewBrowse,
        '3' => Cmd.ViewMap,
        '+' or '=' => Cmd.DepthUp,
        '-' or '_' => Cmd.DepthDown,
        'g' => Cmd.Top,
        'G' => Cmd.Bottom,
        's' => Cmd.SortNext,
        'S' => Cmd.SortReverse,
        'z' => Cmd.ToggleSize,
        't' => Cmd.NextTheme,
        '?' => Cmd.Help,
        'o' => Cmd.Reveal,
        'y' => Cmd.CopyPath,
        'q' => Cmd.Quit,
        ' ' => Cmd.Mark,
        'A' => Cmd.MarkAll,
        'u' => Cmd.ClearOrUndo,
        'd' => Cmd.Delete,
        '7' or 'J' => Cmd.ViewJunk,
        'c' => Cmd.Clean,
        'i' => Cmd.Why,
        _ => Cmd.None,
    };

    /// <summary>True while the dialog wants raw typing (the permanent-delete confirmation).</summary>
    public bool WantsText => Model.Dialog is { Stage: DialogStage.Confirm };

    public void TypeChar(char c)
    {
        if (Model.Dialog is { Stage: DialogStage.Confirm } d && !char.IsControl(c) && d.Typed.Length < 255)
        {
            d.Typed += c;
            d.Hint = null;
        }
    }

    public void Backspace()
    {
        if (Model.Dialog is { Stage: DialogStage.Confirm, Typed.Length: > 0 } d)
        {
            d.Typed = d.Typed[..^1];
        }
    }

    private bool DialogKey(Cmd cmd, DeleteDialog d)
    {
        switch (d.Stage)
        {
            case DialogStage.Running:
                return false;
            case DialogStage.Confirm:
                if (cmd is Cmd.Escape)
                {
                    d.Stage = DialogStage.Choose;
                    d.Typed = "";
                    d.Hint = null;
                }
                else if (cmd is Cmd.Open)
                {
                    if (d.Typed == d.ConfirmWord)
                    {
                        StartDelete(d, permanent: true);
                    }
                    else
                    {
                        d.Hint = $"type {d.ConfirmWord} exactly, or esc to go back";
                    }
                }
                else if (cmd is Cmd.Back)
                {
                    Backspace();
                }
                return true;
        }
        switch (cmd)
        {
            case Cmd.Left or Cmd.Up:
                d.Move(-1);
                break;
            case Cmd.Right or Cmd.Down or Cmd.NextView:
                d.Move(1);
                break;
            case Cmd.Escape or Cmd.Quit or Cmd.Back:
                Model.Dialog = null;
                Model.ShowToast("nothing was deleted");
                break;
            case Cmd.Open:
                switch (d.Choice)
                {
                    case DeleteDialog.TrashChoice: StartDelete(d, permanent: false); break;
                    case DeleteDialog.PermanentChoice: d.Stage = DialogStage.Confirm; break;
                    default:
                        Model.Dialog = null;
                        Model.ShowToast("nothing was deleted");
                        break;
                }
                break;
        }
        return true;
    }

    /// <summary>Keys of the Junk view. Returns false for keys it leaves to the shared handling.</summary>
    private bool JunkKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        if (m.Junk is not { } report)
        {
            if (cmd is Cmd.Clean or Cmd.Why or Cmd.Mark or Cmd.MarkAll)
            {
                m.ShowToast("the junk report is ready when the scan finishes");
                return true;
            }
            return false;
        }
        List<(int Cat, int Item)> rows = m.JunkRows();
        m.JunkSelected = Math.Clamp(m.JunkSelected, 0, Math.Max(0, rows.Count - 1));
        (int Cat, int Item)? row = rows.Count > 0 ? rows[m.JunkSelected] : null;
        JunkCategory? cat = row is { } r0 ? report.Categories[r0.Cat] : null;
        switch (cmd)
        {
            case Cmd.Up: m.JunkSelected = Math.Max(0, m.JunkSelected - 1); return true;
            case Cmd.Down: m.JunkSelected = Math.Min(rows.Count - 1, m.JunkSelected + 1); return true;
            case Cmd.PageUp: m.JunkSelected = Math.Max(0, m.JunkSelected - pageSize); return true;
            case Cmd.PageDown: m.JunkSelected = Math.Min(rows.Count - 1, m.JunkSelected + pageSize); return true;
            case Cmd.Top: m.JunkSelected = 0; return true;
            case Cmd.Bottom: m.JunkSelected = Math.Max(0, rows.Count - 1); return true;
            case Cmd.Right or Cmd.Open when cat is not null && row!.Value.Item < 0:
                if (!m.JunkExpanded.Add(cat.Rule.Id) && cmd == Cmd.Open)
                {
                    m.JunkExpanded.Remove(cat.Rule.Id);
                }
                return true;
            case Cmd.Right or Cmd.Open when cat is not null:
            {
                // Jump to the item in the list view.
                int node = cat.Items[row!.Value.Item];
                m.Enter(m.Store.Parent(node));
                m.SelectNode(node);
                m.View = ViewKind.Browse;
                return true;
            }
            case Cmd.Left or Cmd.Back or Cmd.Escape when cat is not null:
                if (m.JunkExpanded.Remove(cat.Rule.Id))
                {
                    m.JunkSelected = m.JunkRows().FindIndex(x => x.Cat == row!.Value.Cat && x.Item < 0);
                    return true;
                }
                if (cmd == Cmd.Escape)
                {
                    m.View = ViewKind.Browse;
                    return true;
                }
                return true;
            case Cmd.Mark when cat is not null:
                if (row!.Value.Item < 0)
                {
                    if (!m.JunkChecked.Add(cat.Rule.Id))
                    {
                        m.JunkChecked.Remove(cat.Rule.Id);
                    }
                }
                else
                {
                    int node = cat.Items[row.Value.Item];
                    if (!m.JunkChecked.Contains(cat.Rule.Id))
                    {
                        // Ticking one item of an unticked category ticks the category with only that item.
                        m.JunkChecked.Add(cat.Rule.Id);
                        foreach (int other in cat.Items.Where(i => i != node))
                        {
                            m.JunkSkipped.Add(other);
                        }
                        m.JunkSkipped.Remove(node);
                    }
                    else if (!m.JunkSkipped.Add(node))
                    {
                        m.JunkSkipped.Remove(node);
                    }
                }
                m.JunkSelected = Math.Min(rows.Count - 1, m.JunkSelected + 1);
                return true;
            case Cmd.MarkAll:
            {
                int n = 0;
                foreach (JunkCategory c in report.Categories.Where(c => c.Rule.Safety == Safety.Safe))
                {
                    n += m.JunkChecked.Add(c.Rule.Id) ? 1 : 0;
                    m.JunkSkipped.ExceptWith(c.Items);
                }
                m.ShowToast(n > 0 ? $"selected {n} more SAFE categories" : "all SAFE categories are selected");
                return true;
            }
            case Cmd.Why when cat is not null:
                m.ShowToast($"{cat.Rule.Name}: {cat.Rule.Why}" + (cat.Rule.Command is { } command ? $" · or run: {command}" : ""));
                return true;
            case Cmd.Clean:
            {
                var (items, _) = m.JunkSelection();
                if (items.Count == 0)
                {
                    m.ShowToast("nothing selected · ␣ ticks a category, A ticks all SAFE ones");
                    return true;
                }
                OpenDeleteDialog(items);
                return true;
            }
            case Cmd.Delete:
                m.ShowToast("c cleans the ticked junk · d deletes in the list and map");
                return true;
        }
        return false;
    }

    private void OpenDeleteDialog(List<int>? explicitTargets = null)
    {
        BrowserModel m = Model;
        if (deleter is null)
        {
            m.ShowToast("deleting isn't available here");
            return;
        }
        if (m.Scanning)
        {
            m.ShowToast("wait for the scan to finish before deleting");
            return;
        }
        List<int> targets = explicitTargets ?? (m.Marks.Count > 0 ? [.. m.Marks] : m.SelectedNode is int sel ? [sel] : []);
        if (targets.Count == 0)
        {
            m.ShowToast("nothing selected to delete");
            return;
        }
        DeletePlan plan = deleter.Plan(targets);
        m.Dialog = new DeleteDialog(plan, deleter.Trash.IsAvailable, deleter.Trash.Name);
    }

    private void StartDelete(DeleteDialog d, bool permanent)
    {
        d.Stage = DialogStage.Running;
        d.Permanent = permanent;
        Deleter del = deleter!;
        _running = Task.Run(() => del.Execute(d.Plan, permanent, new SyncProgress(d.SetDone)));
    }

    /// <summary>A progress sink that reports on the calling thread (no SynchronizationContext needed).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>Call regularly from the UI thread: finishes a delete when it completes.</summary>
    /// <returns>True if something changed.</returns>
    public bool Poll()
    {
        if (_running is not { IsCompleted: true } task || Model.Dialog is not { } d)
        {
            return false;
        }
        _running = null;
        Model.Dialog = null;
        if (task.IsFaulted)
        {
            Model.ShowToast("delete failed: " + task.Exception?.GetBaseException().Message);
            return true;
        }
        DeleteReport report = task.Result;
        var ok = report.Succeeded.ToList();
        var failed = report.Failed.ToList();
        Model.Unmark(ok.Select(o => o.Node));
        Model.LeaveDeletedFolders();
        long freed = ok.Sum(o => d.Plan.Items.First(i => i.Node == o.Node).Size);
        string what = ok.Count == 1 ? Path.GetFileName(ok[0].Path) : $"{ok.Count} items";
        string done = report.Permanent
            ? $"deleted {what} · freed {Units.Size(freed)}"
            : $"moved {what} to the {d.TrashName} · {Units.Size(freed)} · u undoes";
        if (ok.Count == 0)
        {
            done = "nothing was deleted";
        }
        if (failed.Count > 0)
        {
            done += $" · {failed.Count} skipped: {Path.GetFileName(failed[0].Path)} ({failed[0].Error})";
        }
        LastDelete = report.Permanent || ok.Count == 0 ? null : report;
        Model.ShowToast(done);
        return true;
    }

    /// <summary>Waits for a running delete (tests and shutdown).</summary>
    public void WaitForDelete()
    {
        _running?.Wait();
        Poll();
    }

    /// <returns>True if the command did something (so the screen needs a redraw).</returns>
    public bool Apply(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        if (cmd == Cmd.None)
        {
            return false;
        }
        if (m.Dialog is { } dialog)
        {
            return DialogKey(cmd, dialog);
        }
        if (m.HelpOpen)
        {
            // Any key closes help; q still quits.
            m.HelpOpen = false;
            if (cmd == Cmd.Quit)
            {
                QuitRequested = true;
            }
            return true;
        }
        m.ClearToast();
        switch (cmd)
        {
            case Cmd.NextView:
                m.View = m.View switch { ViewKind.Browse => ViewKind.Map, ViewKind.Map => ViewKind.Junk, _ => ViewKind.Browse };
                return true;
            case Cmd.ViewJunk:
                m.View = ViewKind.Junk;
                return true;
            case Cmd.ViewBrowse:
                m.View = ViewKind.Browse;
                return true;
            case Cmd.ViewMap:
                m.View = ViewKind.Map;
                return true;
            case Cmd.DepthUp or Cmd.DepthDown:
                if (m.View == ViewKind.Map)
                {
                    m.ChangeMapDepth(cmd == Cmd.DepthUp ? 1 : -1);
                }
                else
                {
                    m.ShowToast("+ and - change the treemap depth (press 3 for the map)");
                }
                return true;
        }
        if (m.View == ViewKind.Map && MapMove(cmd))
        {
            return true;
        }
        if (m.View == ViewKind.Junk && JunkKey(cmd, pageSize))
        {
            return true;
        }
        switch (cmd)
        {
            case Cmd.Left: goto case Cmd.Back;
            case Cmd.Right: m.Open(); break;
            case Cmd.Up: m.MoveBy(-1); break;
            case Cmd.Down: m.MoveBy(1); break;
            case Cmd.PageUp: m.MoveBy(-Math.Max(1, pageSize - 1)); break;
            case Cmd.PageDown: m.MoveBy(Math.Max(1, pageSize - 1)); break;
            case Cmd.Top: m.Select(0); break;
            case Cmd.Bottom: m.Select(int.MaxValue); break;
            case Cmd.Open: m.Open(); break;
            case Cmd.Back:
                if (!m.Up())
                {
                    m.ShowToast("already at the top of the scan");
                }
                break;
            case Cmd.SortNext: m.CycleSort(); break;
            case Cmd.SortReverse: m.ReverseSort(); break;
            case Cmd.ToggleSize: m.ToggleSizeMode(); break;
            case Cmd.NextTheme: m.NextTheme(); break;
            case Cmd.Help: m.HelpOpen = true; break;
            case Cmd.Reveal: m.ShowToast(shell.Reveal(m.FullPath(m.SelectedNode ?? m.Current))); break;
            case Cmd.CopyPath: m.ShowToast(shell.Copy(m.FullPath(m.SelectedNode ?? m.Current))); break;
            case Cmd.Escape:
                if (!m.Up())
                {
                    m.ShowToast("press q to quit");
                }
                break;
            case Cmd.Quit: QuitRequested = true; break;
            case Cmd.Mark:
                if (m.SelectedNode is int id)
                {
                    m.ToggleMark(id);
                    if (m.View == ViewKind.Browse)
                    {
                        m.MoveBy(1);
                    }
                }
                break;
            case Cmd.MarkAll:
                m.ShowToast($"marked {m.MarkAllVisible()} more · u clears marks");
                break;
            case Cmd.ClearOrUndo:
                if (m.Marks.Count > 0)
                {
                    m.ClearMarks();
                    m.ShowToast("marks cleared");
                }
                else if (LastDelete is { } last && deleter is not null)
                {
                    var (restored, problems) = deleter.Undo(last);
                    LastDelete = null;
                    m.LeaveDeletedFolders();
                    m.ShowToast(problems.Count == 0 ? $"restored {restored} from the {deleter.Trash.Name}"
                        : $"restored {restored}; {problems.Count} couldn't be: {problems[0]}");
                }
                else
                {
                    m.ShowToast("nothing to undo");
                }
                break;
            case Cmd.Delete:
                OpenDeleteDialog();
                break;
        }
        return true;
    }

    /// <summary>Treemap navigation: arrows move between neighbouring blocks.</summary>
    private bool MapMove(Cmd cmd)
    {
        (int dx, int dy) = cmd switch
        {
            Cmd.Left => (-1, 0),
            Cmd.Right => (1, 0),
            Cmd.Up => (0, -1),
            Cmd.Down => (0, 1),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0)
        {
            return false;
        }
        List<MapBlock> blocks = [.. LastHits.Blocks.Where(b => !b.IsGroup)];
        if (blocks.Count == 0)
        {
            return true;
        }
        int from = blocks.FindIndex(b => b.Node == Model.SelectedNode);
        int? to = from < 0 ? 0 : Treemap.Neighbour(blocks, from, dx, dy);
        if (to is int i)
        {
            Model.SelectNode(blocks[i].Node);
        }
        return true;
    }

    /// <summary>Handles a click at (x, y) using the last frame's layout and hit map.</summary>
    public bool Click(int x, int y, bool doubleClick, Layout layout, HitMap hits)
    {
        BrowserModel m = Model;
        if (m.HelpOpen)
        {
            m.HelpOpen = false;
            return true;
        }
        if (y == 0)
        {
            foreach (var (x0, x1, view) in hits.Tabs)
            {
                if (x >= x0 && x < x1)
                {
                    m.View = view;
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Map && y >= 2)
        {
            MapBlock? hit = hits.Blocks.FirstOrDefault(b => b.Rect.Contains(x, y));
            if (hit is null)
            {
                return false;
            }
            m.ClearToast();
            if (hit.IsGroup)
            {
                m.ShowToast($"{hit.GroupCount} smaller items: open the list (tab) to see them");
                return true;
            }
            m.SelectNode(hit.Node);
            if (doubleClick)
            {
                m.Open();
            }
            return true;
        }
        if (y == 1)
        {
            foreach (var (x0, x1, node) in hits.Crumbs)
            {
                if (x >= x0 && x < x1)
                {
                    m.ClearToast();
                    if (node != m.Current)
                    {
                        m.Enter(node);
                    }
                    return true;
                }
            }
            return false;
        }
        bool inList = x >= 1 && x <= layout.ListX + layout.ListWidth && y >= layout.FirstRow && y < layout.FirstRow + layout.VisibleRows;
        if (!inList)
        {
            return false;
        }
        int index = m.Scroll + (y - layout.FirstRow);
        if (index >= m.Rows.Count)
        {
            return false;
        }
        m.ClearToast();
        m.Select(index);
        if (doubleClick)
        {
            m.Open();
        }
        return true;
    }

    public bool Wheel(int delta)
    {
        if (Model.HelpOpen)
        {
            return false;
        }
        if (Model.View == ViewKind.Map)
        {
            // Wheel down zooms out; wheel up zooms into the selected block.
            if (delta > 0)
            {
                Model.Up();
            }
            else
            {
                Model.Open();
            }
            return true;
        }
        Model.MoveBy(delta);
        return true;
    }

    public static bool IsContainer(NodeStore s, int id) => s.Kind(id) == NodeKind.Directory;
}
