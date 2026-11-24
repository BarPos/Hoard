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
    ViewJunk, Clean, Why, ViewOverview, ViewTypes, Group, ViewAge, AxisModified, AxisAccessed, SkipScan, ViewBigOld, Filters, Settings, ViewEmpty, KeepFiles, ViewDupes, ViewSnaps, SnapshotNow, Forget, Export, Find, Rescan, RescanAll, ExcludeIt, Palette, Volumes, Watch, Home, PathEntry, ToggleHidden,
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
    /// <summary>R was pressed: the app starts a whole new scan of the same folder.</summary>
    public bool RescanAllRequested { get; set; }
    /// <summary>A different folder or volume to scan (from the volumes screen): the app starts over there.</summary>
    public string? ScanPathRequested { get; set; }
    /// <summary>The last delete that went to the Trash, for undo (<c>u</c>).</summary>
    public DeleteReport? LastDelete { get; private set; }
    private Task<DeleteReport>? _running;
    private DeleteDialog? _runningDialog;
    /// <summary>What was on screen last frame; the treemap needs it to move between blocks.</summary>
    public HitMap LastHits { get; set; } = new();

    public static Cmd FromChar(char c) => c switch
    {
        'j' => Cmd.Down,
        'k' => Cmd.Up,
        'l' => Cmd.Right,
        'h' => Cmd.Left,
        '1' => Cmd.ViewOverview,
        '2' => Cmd.ViewBrowse,
        '3' => Cmd.ViewMap,
        '4' => Cmd.ViewTypes,
        '5' => Cmd.ViewAge,
        'm' => Cmd.AxisModified,
        'b' => Cmd.SkipScan,
        '8' or 'O' => Cmd.ViewBigOld,
        'f' => Cmd.Filters,
        ',' => Cmd.Settings,
        '9' or 'E' => Cmd.ViewEmpty,
        '6' or 'D' => Cmd.ViewDupes,
        '0' => Cmd.ViewSnaps,
        'P' => Cmd.SnapshotNow,
        'x' => Cmd.Export,
        '/' => Cmd.Find,
        'r' => Cmd.Rescan,
        'R' => Cmd.RescanAll,
        'X' => Cmd.ExcludeIt,
        ':' => Cmd.Palette,
        'v' => Cmd.Volumes,
        'w' => Cmd.Watch,
        '~' => Cmd.Home,
        'p' => Cmd.PathEntry,
        'H' => Cmd.ToggleHidden,
        'K' => Cmd.KeepFiles,
        'a' => Cmd.AxisAccessed,
        'e' => Cmd.Group,
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

    /// <summary>Answers the quit question with a typed letter: y quits, n (or anything else) stays.</summary>
    public bool AnswerQuit(char c)
    {
        if (!Model.QuitConfirmOpen)
        {
            return false;
        }
        Model.QuitConfirmOpen = false;
        if (c is 'y' or 'Y' or 'q')
        {
            QuitRequested = true;
        }
        return true;
    }

    /// <summary>True while the dialog wants raw typing (the permanent-delete confirmation).</summary>
    /// <summary>Plain keys are text right now (a confirmation word, a path, a search).</summary>
    public bool WantsText => Model.Dialog is { Stage: DialogStage.Confirm } || Model.Export is { EditingPath: true } || Model.Search is not null
        || Model.PatternInput is not null || Model.Palette is not null || Model.PathInput is not null;

    public void TypeChar(char c)
    {
        if (char.IsControl(c))
        {
            return;
        }
        if (Model.Dialog is { Stage: DialogStage.Confirm } d && d.Typed.Length < 255)
        {
            d.Typed += c;
            d.Hint = null;
        }
        else if (Model.Export is { EditingPath: true } e && e.Path.Length < 1024)
        {
            e.Path += c;
            e.CustomPath = true;
        }
        else if (Model.PathInput is { } typedPath && typedPath.Length < 1024)
        {
            Model.PathInput = typedPath + c;
        }
        else if (Model.Palette is { } pal && pal.Query.Length < 100)
        {
            pal.Query += c;
            pal.Selected = 0;
        }
        else if (Model.PatternInput is { } pattern && pattern.Length < 500)
        {
            Model.PatternInput = pattern + c;
        }
        else if (Model.Search is { } box && box.Query.Length < 200)
        {
            box.Query += c;
            box.Selected = 0;
            box.Scroll = 0;
        }
    }

    public void Backspace()
    {
        if (Model.Dialog is { Stage: DialogStage.Confirm, Typed.Length: > 0 } d)
        {
            d.Typed = d.Typed[..^1];
        }
        else if (Model.Export is { EditingPath: true, Path.Length: > 0 } e)
        {
            e.Path = e.Path[..^1];
            e.CustomPath = true;
        }
        else if (Model.PathInput is { Length: > 0 } typedPath)
        {
            Model.PathInput = typedPath[..^1];
        }
        else if (Model.Palette is { Query.Length: > 0 } pal)
        {
            pal.Query = pal.Query[..^1];
            pal.Selected = 0;
        }
        else if (Model.PatternInput is { Length: > 0 } pattern)
        {
            Model.PatternInput = pattern[..^1];
        }
        else if (Model.Search is { Query.Length: > 0 } box)
        {
            box.Query = box.Query[..^1];
            box.Selected = 0;
            box.Scroll = 0;
        }
    }

    /// <summary>Keys of the finder. Plain keys are the query, so actions are ↑↓ ↵ esc tab and Ctrl keys.</summary>
    private bool SearchKey(Cmd cmd, SearchBox box)
    {
        BrowserModel m = Model;
        SearchResult? r = m.SearchResults;
        int count = r?.Hits.Count ?? 0;
        int? node = count > 0 ? r!.Hits[Math.Clamp(box.Selected, 0, count - 1)].Node : null;
        switch (cmd)
        {
            case Cmd.Up: box.Selected = Math.Max(0, box.Selected - 1); return true;
            case Cmd.Down: box.Selected = Math.Min(Math.Max(0, count - 1), box.Selected + 1); return true;
            case Cmd.PageUp: box.Selected = Math.Max(0, box.Selected - 10); return true;
            case Cmd.PageDown: box.Selected = Math.Min(Math.Max(0, count - 1), box.Selected + 10); return true;
            case Cmd.NextView: // tab: what to look for
                box.Kind = box.Kind switch { SearchKind.Any => SearchKind.Folders, SearchKind.Folders => SearchKind.Files, _ => SearchKind.Any };
                box.Selected = 0;
                return true;
            case Cmd.SortNext: // ^s: how big
                box.MinSize = SearchBox.SizeSteps[(Array.IndexOf(SearchBox.SizeSteps, box.MinSize) + 1) % SearchBox.SizeSteps.Length];
                box.Selected = 0;
                return true;
            case Cmd.Escape:
                m.Search = null;
                return true;
            case Cmd.Open when node is int id:
                m.Search = null;
                m.View = ViewKind.Browse;
                m.Enter(m.Store.Parent(id));
                m.SelectNode(id);
                return true;
            case Cmd.Mark when node is int mark:
                m.ToggleMark(mark);
                box.Selected = Math.Min(count - 1, box.Selected + 1);
                return true;
            case Cmd.MarkAll when r is not null:
            {
                int added = 0;
                foreach (SearchHit h in r.Hits.Where(h => !m.IsMarked(h.Node)))
                {
                    m.ToggleMark(h.Node);
                    added++;
                }
                m.ShowToast($"marked {added} more · u clears marks" + (r.Total > r.Hits.Count ? $" (the best {r.Hits.Count} of {r.Total})" : ""));
                return true;
            }
            case Cmd.Filters: // ^f: filter the open list instead
                m.Search = null;
                m.View = ViewKind.Browse;
                m.SetListFilter(box.Query);
                m.ShowToast(m.ListFilter is null ? "filter cleared" : $"showing entries matching “{m.ListFilter}” · esc clears");
                return true;
        }
        return true; // modal
    }

    /// <summary>Keys of the export dialog: ↑↓ field, ←→ value, ↵ export (or edit the path), esc closes.</summary>
    private bool ExportKey(Cmd cmd, ExportDialog e)
    {
        BrowserModel m = Model;
        if (e.Writing is { IsCompleted: false })
        {
            if (cmd == Cmd.Escape)
            {
                m.ShowToast("the export keeps going in the background");
                m.Export = null;
            }
            return true;
        }
        if (e.EditingPath)
        {
            if (cmd is Cmd.Open or Cmd.Escape or Cmd.Up or Cmd.Down)
            {
                e.EditingPath = false;
                if (e.Path.StartsWith("~/", StringComparison.Ordinal))
                {
                    e.Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), e.Path[2..]);
                }
            }
            return true;
        }
        void Step(int d)
        {
            e.Error = null;
            switch (e.Field)
            {
                case 0:
                {
                    var all = Enum.GetValues<Hoard.Core.Export.ExportFormat>();
                    e.Format = all[(Array.IndexOf(all, e.Format) + d + all.Length) % all.Length];
                    e.FixExtension();
                    break;
                }
                case 1:
                {
                    ExportScope[] scopes = m.Marks.Count > 0 ? [ExportScope.Folder, ExportScope.Everything, ExportScope.Marked] : [ExportScope.Folder, ExportScope.Everything];
                    e.Scope = scopes[(Math.Max(0, Array.IndexOf(scopes, e.Scope)) + d + scopes.Length) % scopes.Length];
                    break;
                }
                case 2: e.IncludeFiles = !e.IncludeFiles; break;
                case 3: e.Depth = ExportDialog.DepthSteps[Math.Clamp(Array.IndexOf(ExportDialog.DepthSteps, e.Depth) + d, 0, ExportDialog.DepthSteps.Length - 1)]; break;
                case 4: e.MinSize = ExportDialog.MinSizeSteps[Math.Clamp(Array.IndexOf(ExportDialog.MinSizeSteps, e.MinSize) + d, 0, ExportDialog.MinSizeSteps.Length - 1)]; break;
            }
        }
        switch (cmd)
        {
            case Cmd.Up: e.Field = Math.Max(0, e.Field - 1); return true;
            case Cmd.Down or Cmd.NextView: e.Field = Math.Min(ExportDialog.FieldCount - 1, e.Field + 1); return true;
            case Cmd.Left: Step(-1); return true;
            case Cmd.Right or Cmd.Mark: Step(1); return true;
            case Cmd.Escape or Cmd.Export or Cmd.Quit:
                m.Export = null;
                return true;
            case Cmd.Open when e.Field == 5:
                e.EditingPath = true;
                return true;
            case Cmd.Open:
                StartExport(e);
                return true;
            case Cmd.CopyPath:
                m.ShowToast(shell.Copy(e.Path));
                return true;
        }
        return true; // the dialog is modal
    }

    private void StartExport(ExportDialog e)
    {
        BrowserModel m = Model;
        if (m.Scanning)
        {
            e.Error = "wait for the scan to finish (sizes are still growing)";
            return;
        }
        if (e.Scope == ExportScope.Marked && m.Marks.Count == 0)
        {
            e.Error = "nothing is marked";
            return;
        }
        string path = e.Path;
        Hoard.Core.Export.ExportData data = m.ExportData(e.Scope);
        Hoard.Core.Export.ExportOptions options = e.Options(m.Apparent);
        e.Error = null;
        e.Writing = Task.Run(() =>
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                Hoard.Core.Export.Exporter.Write(data, options, fs);
            }
            File.Move(tmp, path, overwrite: true);
            return new FileInfo(path).Length;
        });
    }

    /// <summary>Call regularly: finishes an export. True when one just finished.</summary>
    public bool PollExport()
    {
        if (Model.Export is not { Writing: { IsCompleted: true } t } e)
        {
            return false;
        }
        e.Writing = null;
        if (t.IsFaulted)
        {
            e.Error = t.Exception?.GetBaseException().Message ?? "export failed";
            return true;
        }
        Model.Export = null;
        Model.ShowToast($"exported {Hoard.Core.Export.Exporter.Name(e.Format)} to {Rendering.Screens.Shorten(e.Path)} · {Units.Size(t.Result)} · o reveals it");
        LastExport = e.Path;
        return true;
    }

    /// <summary>Call regularly: opens the delete dialog once the byte comparison of duplicates is done.</summary>
    public bool PollDupVerify()
    {
        // While another dialog is open or a delete is running, the result waits: it must never
        // replace a dialog the user is looking at, or one that's in the middle of deleting.
        if (Model.Dialog is not null || _running is not null || Model.PollDupVerify() is not { } r)
        {
            return false;
        }
        if (Model.View != ViewKind.Dupes)
        {
            Model.ShowToast("the duplicate check finished · press d in Dupes (6) to delete the copies");
            return true;
        }
        r = (r.Ok.Where(id => !Model.Store.IsDeleted(id)).ToList(), r.Different);
        if (r.Different.Count > 0)
        {
            Model.ShowToast($"{r.Different.Count} copy(ies) aren't identical after all (changed since?): {System.IO.Path.GetFileName(r.Different[0])} · rescan with r");
            return true;
        }
        if (r.Ok.Count > 0)
        {
            OpenDeleteDialog(r.Ok);
        }
        return true;
    }

    /// <summary>The file the last export wrote (o reveals it while the toast is up).</summary>
    public string? LastExport { get; private set; }

    public void WaitForExport()
    {
        try
        {
            Model.Export?.Writing?.Wait();
        }
        catch (AggregateException)
        {
        }
        PollExport();
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
                m.ShowToast(m.Scanning ? "the junk report is ready when the scan finishes" : "still looking for junk, one moment…");
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
        // Without marks, only the list and the map have a highlighted entry of their own; anywhere
        // else Browse's selection isn't on screen, so it must never be what gets deleted.
        bool listOnScreen = m.View is ViewKind.Browse or ViewKind.Map;
        List<int> targets = explicitTargets
            ?? (m.Marks.Count > 0 ? [.. m.Marks] : listOnScreen && m.SelectedNode is int sel ? [sel] : []);
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
        _runningDialog = d;
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
        // The dialog this delete was started from, not whatever dialog happens to be open now.
        if (_running is not { IsCompleted: true } task || _runningDialog is not { } d)
        {
            return false;
        }
        _running = null;
        _runningDialog = null;
        if (Model.Dialog == d)
        {
            Model.Dialog = null;
        }
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
        Model.RefreshTypes();
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

    /// <summary>The Overview's list of biggest entries: move, and open one in the browser.</summary>
    private bool OverviewKey(Cmd cmd)
    {
        BrowserModel m = Model;
        int count = Math.Max(1, m.OverviewShown);
        switch (cmd)
        {
            case Cmd.Up: m.OverviewSelected = Math.Max(0, m.OverviewSelected - 1); return true;
            case Cmd.Down: m.OverviewSelected = Math.Min(count - 1, m.OverviewSelected + 1); return true;
            case Cmd.Top or Cmd.PageUp: m.OverviewSelected = 0; return true;
            case Cmd.Bottom or Cmd.PageDown: m.OverviewSelected = count - 1; return true;
            case Cmd.Right or Cmd.Open:
                OpenFromOverview(m.OverviewSelected);
                return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                return true; // already at the top
            case Cmd.Clean:
                m.View = ViewKind.Junk;
                return true;
            case Cmd.Mark or Cmd.Delete or Cmd.Reveal or Cmd.CopyPath:
            {
                // These act on the highlighted entry here, never on Browse's hidden selection.
                List<int> top = m.TopEntries();
                if (top.Count == 0)
                {
                    m.ShowToast("nothing here yet");
                    return true;
                }
                int id = top[Math.Clamp(m.OverviewSelected, 0, top.Count - 1)];
                switch (cmd)
                {
                    case Cmd.Mark:
                        m.ToggleMark(id);
                        break;
                    case Cmd.Delete:
                        OpenDeleteDialog(m.Marks.Count > 0 ? null : [id]);
                        break;
                    case Cmd.Reveal:
                        m.ShowToast(shell.Reveal(m.FullPath(id)));
                        break;
                    default:
                        m.ShowToast(shell.Copy(m.FullPath(id)));
                        break;
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Keys of a file list (Types and Age, ↵): move, mark, delete, reveal, show in Browse, back.</summary>
    private bool FileListKey(FileList list, Cmd cmd, int pageSize, Action close)
    {
        BrowserModel m = Model;
        NodeStore s = m.Store;
        List<int> items = list.Items(s) ?? [];
        int last = Math.Max(0, items.Count - 1);
        int? node = items.Count > 0 ? items[Math.Clamp(list.Selected, 0, last)] : null;
        switch (cmd)
        {
            case Cmd.Up: list.Selected = Math.Max(0, list.Selected - 1); return true;
            case Cmd.Down: list.Selected = Math.Min(last, list.Selected + 1); return true;
            case Cmd.PageUp: list.Selected = Math.Max(0, list.Selected - Math.Max(1, pageSize - 1)); return true;
            case Cmd.PageDown: list.Selected = Math.Min(last, list.Selected + Math.Max(1, pageSize - 1)); return true;
            case Cmd.Top: list.Selected = 0; return true;
            case Cmd.Bottom: list.Selected = last; return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                close();
                return true;
            case Cmd.Right or Cmd.Open when node is int open:
                m.Enter(s.Parent(open));
                m.SelectNode(open);
                m.View = ViewKind.Browse;
                return true;
            case Cmd.Mark when node is int mark:
                m.ToggleMark(mark);
                list.Selected = Math.Min(last, list.Selected + 1);
                return true;
            case Cmd.MarkAll:
            {
                int added = 0;
                foreach (int id in items.Where(id => !m.IsMarked(id)))
                {
                    m.ToggleMark(id);
                    added++;
                }
                m.ShowToast($"marked {added} more · u clears marks");
                return true;
            }
            case Cmd.Delete:
                if (m.Marks.Count > 0 || node is null)
                {
                    OpenDeleteDialog();
                }
                else
                {
                    OpenDeleteDialog([node.Value]);
                }
                return true;
            case Cmd.Reveal when node is int r:
                m.ShowToast(shell.Reveal(m.FullPath(r)));
                return true;
            case Cmd.CopyPath when node is int cp:
                m.ShowToast(shell.Copy(m.FullPath(cp)));
                return true;
            default:
                return cmd is Cmd.Right or Cmd.Open or Cmd.Mark or Cmd.Reveal or Cmd.CopyPath;
        }
    }

    /// <summary>
    /// Keys on the scanning screen. Returns false for keys that leave it: those then act as usual
    /// (a number opens that tab), except "show results now" keys, which only leave.
    /// </summary>
    private bool ScanScreenKey(Cmd cmd)
    {
        BrowserModel m = Model;
        switch (cmd)
        {
            case Cmd.Quit:
                m.QuitConfirmOpen = true;
                return true;
            case Cmd.Help:
                m.HelpOpen = true;
                return true;
            case Cmd.NextTheme:
                m.NextTheme();
                return true;
            case Cmd.SkipScan or Cmd.Open or Cmd.NextView or Cmd.Right:
                m.ScanScreen = false;
                m.ShowToast("still scanning · sizes keep growing");
                return true;
            case Cmd.ViewOverview or Cmd.ViewBrowse or Cmd.ViewMap or Cmd.ViewTypes or Cmd.ViewAge or Cmd.ViewJunk or Cmd.ViewBigOld or Cmd.ViewEmpty or Cmd.ViewDupes or Cmd.ViewSnaps:
                return false;
            default:
                m.ShowToast("b or ↵ shows the results now · they keep filling in");
                return true;
        }
    }

    /// <summary>Keys of the Snapshots view: the list on the left, the changes on the right (→ / ←).</summary>
    private bool SnapsKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        var rows = m.SnapRows;
        int last = rows.Count - 1;
        if (cmd == Cmd.Filters)
        {
            m.SnapOnlyGrowth = !m.SnapOnlyGrowth;
            m.SnapDiffSelected = 0;
            m.ShowToast(m.SnapOnlyGrowth ? "only what grew" : "everything that changed");
            return true;
        }
        if (cmd != Cmd.Forget)
        {
            m.SnapDeletePending = null;
        }
        if (m.SnapDiffFocus)
        {
            var changes = m.SnapChanges();
            int lastChange = Math.Max(0, changes.Count - 1);
            switch (cmd)
            {
                case Cmd.Up: m.SnapDiffSelected = Math.Max(0, m.SnapDiffSelected - 1); return true;
                case Cmd.Down: m.SnapDiffSelected = Math.Min(lastChange, m.SnapDiffSelected + 1); return true;
                case Cmd.PageUp or Cmd.Top: m.SnapDiffSelected = 0; return true;
                case Cmd.PageDown or Cmd.Bottom: m.SnapDiffSelected = lastChange; return true;
                case Cmd.Open or Cmd.Right:
                    if (changes.Count > 0 && changes[Math.Clamp(m.SnapDiffSelected, 0, lastChange)] is { Path.Length: > 0 } ch)
                    {
                        m.SnapEnter(ch.Path);
                    }
                    return true;
                case Cmd.Back:
                    if (!m.SnapUp())
                    {
                        m.SnapDiffFocus = false;
                    }
                    return true;
                case Cmd.Left or Cmd.Escape:
                    m.SnapDiffFocus = false;
                    return true;
            }
            return false;
        }
        switch (cmd)
        {
            case Cmd.Up: m.SnapSelected = Math.Max(0, m.SnapSelected - 1); return true;
            case Cmd.Down: m.SnapSelected = Math.Min(last, m.SnapSelected + 1); return true;
            case Cmd.Top or Cmd.PageUp: m.SnapSelected = 0; return true;
            case Cmd.Bottom or Cmd.PageDown: m.SnapSelected = last; return true;
            case Cmd.Open:
                m.SnapCompare = m.SnapSelected;
                if (m.SnapBase == m.SnapCompare)
                {
                    m.SnapBase = Math.Min(last, m.SnapCompare + 1);
                }
                m.SnapEnter("");
                return true;
            case Cmd.SkipScan: // b: set the base
                m.SnapBase = m.SnapSelected;
                m.SnapEnter("");
                m.ShowToast(m.SnapBase == m.SnapCompare ? "base and compare are the same snapshot" : "base set");
                return true;
            case Cmd.Right:
                m.SnapDiffFocus = rows.Count > 1;
                return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                return true;
            case Cmd.Forget or Cmd.Delete:
            {
                if (rows[Math.Clamp(m.SnapSelected, 0, last)] is not { } snap)
                {
                    m.ShowToast("“now” is this scan, not a saved snapshot");
                    return true;
                }
                if (m.SnapDeletePending != snap.File)
                {
                    m.SnapDeletePending = snap.File;
                    m.ShowToast($"press del again to delete the snapshot of {snap.TakenAt.ToLocalTime():yyyy-MM-dd HH:mm}");
                    return true;
                }
                m.SnapDeletePending = null;
                m.ForgetSnapshot(snap);
                m.ShowToast("snapshot deleted");
                return true;
            }
        }
        return false;
    }

    /// <summary>Keys of the Duplicates view. Marking and deleting never take the last copy of a file.</summary>
    private bool DupesKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        NodeStore s = m.Store;
        if (cmd == Cmd.KeepFiles)
        {
            m.CycleDupKeep();
            return true;
        }
        if (m.Dupes is not { } d)
        {
            if (cmd is Cmd.Open && m.StartDupes())
            {
                return true;
            }
            if (cmd is Cmd.Open or Cmd.Mark or Cmd.MarkAll or Cmd.AxisAccessed or Cmd.Delete)
            {
                m.ShowToast(m.Scanning ? "duplicates can be looked for when the scan finishes" : "still comparing files, one moment…");
                return true;
            }
            return false;
        }
        List<(int Group, int File)> rows = m.DupRows();
        if (rows.Count == 0)
        {
            return cmd is Cmd.Up or Cmd.Down or Cmd.Open or Cmd.Mark;
        }
        int last = rows.Count - 1;
        m.DupSelected = Math.Clamp(m.DupSelected, 0, last);
        var (gi, file) = rows[m.DupSelected];
        DupGroup g = d.Report.Groups[gi];
        List<int> copies = m.DupFiles(g);
        int keep = m.DupKeeper(g);
        switch (cmd)
        {
            case Cmd.Up: m.DupSelected = Math.Max(0, m.DupSelected - 1); return true;
            case Cmd.Down: m.DupSelected = Math.Min(last, m.DupSelected + 1); return true;
            case Cmd.PageUp: m.DupSelected = Math.Max(0, m.DupSelected - Math.Max(1, pageSize - 1)); return true;
            case Cmd.PageDown: m.DupSelected = Math.Min(last, m.DupSelected + Math.Max(1, pageSize - 1)); return true;
            case Cmd.Top: m.DupSelected = 0; return true;
            case Cmd.Bottom: m.DupSelected = last; return true;
            case Cmd.Right when file < 0:
                m.DupExpanded.Add(gi);
                return true;
            case Cmd.Open when file < 0:
                if (!m.DupExpanded.Add(gi))
                {
                    m.DupExpanded.Remove(gi);
                }
                return true;
            case Cmd.Right or Cmd.Open:
                m.Enter(s.Parent(file));
                m.SelectNode(file);
                m.View = ViewKind.Browse;
                return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                if (m.DupExpanded.Remove(gi))
                {
                    m.DupSelected = Math.Max(0, m.DupRows().IndexOf((gi, -1)));
                }
                return true;
            case Cmd.Mark when file < 0:
            {
                bool pending = copies.Any(id => id != keep && !m.IsMarked(id)) || m.IsMarked(keep);
                foreach (int id in copies)
                {
                    bool want = pending && id != keep;
                    if (m.IsMarked(id) != want)
                    {
                        m.ToggleMark(id);
                    }
                }
                m.DupSelected = Math.Min(last, m.DupSelected + 1);
                return true;
            }
            case Cmd.Mark:
                if (!m.IsMarked(file) && copies.Count(id => !m.IsMarked(id)) == 1)
                {
                    m.ShowToast("that's the last unticked copy: one copy always stays");
                    return true;
                }
                m.ToggleMark(file);
                m.DupSelected = Math.Min(last, m.DupSelected + 1);
                return true;
            case Cmd.MarkAll or Cmd.AxisAccessed:
            {
                int n = m.DupAutoSelect();
                m.ShowToast($"selected {Units.Count(n)} {(n == 1 ? "copy" : "copies")}, keeping the {m.DupKeepName} one of each · frees {Units.Size(m.DupReclaimable())}");
                return true;
            }
            case Cmd.Clean:
                m.ShowToast("turning copies into APFS clones isn't there yet · d moves the selected copies to the Trash");
                return true;
            case Cmd.Filters when file >= 0: // f: keep the copies in this folder
                m.KeepInside(System.IO.Path.GetDirectoryName(m.FullPath(file)) ?? "/");
                return true;
            case Cmd.Delete:
            {
                if (m.DupGroupsFullyMarked() is { Count: > 0 } whole)
                {
                    m.ShowToast($"every copy of {s.Name(m.DupFiles(whole[0])[0])} is ticked: untick one to keep it");
                    return true;
                }
                List<int> targets = m.Marks.Count > 0 ? [.. m.Marks.Where(id => !s.IsDeleted(id))]
                    : file < 0 ? [.. copies.Where(id => id != keep)]
                    : copies.Count > 1 ? [file] : [];
                if (targets.Count == 0)
                {
                    return true;
                }
                // Hashes say they're the same; the bytes have the final word before anything goes.
                m.VerifyDupes(targets);
                m.ShowToast($"comparing {Units.Count(targets.Count)} {(targets.Count == 1 ? "copy" : "copies")} byte for byte before deleting…");
                return true;
            }
            case Cmd.Reveal when file >= 0:
                m.ShowToast(shell.Reveal(m.FullPath(file)));
                return true;
            case Cmd.CopyPath when file >= 0:
                m.ShowToast(shell.Copy(m.FullPath(file)));
                return true;
        }
        return false;
    }

    /// <summary>Keys of the Empty view: groups expand, marks work on groups and items.</summary>
    private bool EmptyKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        NodeStore s = m.Store;
        if (cmd == Cmd.KeepFiles)
        {
            m.ToggleEmptyKeep();
            return true;
        }
        if (m.Empty is not { } report)
        {
            if (cmd is Cmd.Open or Cmd.Mark or Cmd.MarkAll or Cmd.Delete)
            {
                m.ShowToast(m.Scanning ? "ready when the scan finishes" : "still looking, one moment…");
                return true;
            }
            return false;
        }
        List<EmptyRow> rows = m.EmptyRows();
        if (rows.Count == 0)
        {
            return cmd is Cmd.Up or Cmd.Down or Cmd.Open or Cmd.Mark;
        }
        int last = rows.Count - 1;
        m.EmptySelected = Math.Clamp(m.EmptySelected, 0, last);
        EmptyRow row = rows[m.EmptySelected];
        bool isGroup = row.Kind is EmptyRowKind.FolderGroup or EmptyRowKind.ZeroGroup;
        string key = row.Kind is EmptyRowKind.FolderGroup or EmptyRowKind.Folder ? "f:" + row.Group : "z:" + report.Zero[row.Group].Pattern;
        bool kept = row.Kind is EmptyRowKind.ZeroGroup or EmptyRowKind.ZeroFile && report.Zero[row.Group].Safety == ZeroSafety.Keep;
        int? one = row.Kind switch
        {
            EmptyRowKind.Folder or EmptyRowKind.ZeroFile => row.Item,
            EmptyRowKind.FolderGroup when row.Group != NodeStore.Root => row.Group,
            _ => null,
        };
        void ShowInBrowser(int node)
        {
            m.Enter(s.Parent(node));
            m.SelectNode(node);
            m.View = ViewKind.Browse;
        }
        switch (cmd)
        {
            case Cmd.Up: m.EmptySelected = Math.Max(0, m.EmptySelected - 1); return true;
            case Cmd.Down: m.EmptySelected = Math.Min(last, m.EmptySelected + 1); return true;
            case Cmd.PageUp: m.EmptySelected = Math.Max(0, m.EmptySelected - Math.Max(1, pageSize - 1)); return true;
            case Cmd.PageDown: m.EmptySelected = Math.Min(last, m.EmptySelected + Math.Max(1, pageSize - 1)); return true;
            case Cmd.Top: m.EmptySelected = 0; return true;
            case Cmd.Bottom: m.EmptySelected = last; return true;
            case Cmd.Right when isGroup:
                m.EmptyExpanded.Add(key);
                return true;
            case Cmd.Open when isGroup:
                if (!m.EmptyExpanded.Add(key))
                {
                    m.EmptyExpanded.Remove(key);
                }
                return true;
            case Cmd.Right or Cmd.Open:
                ShowInBrowser(row.Item);
                return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                if (m.EmptyExpanded.Remove(key))
                {
                    m.EmptySelected = Math.Max(0, m.EmptyRows().FindIndex(r =>
                        r.Kind is EmptyRowKind.FolderGroup or EmptyRowKind.ZeroGroup && r.Group == row.Group
                        && (r.Kind == EmptyRowKind.FolderGroup) == (row.Kind is EmptyRowKind.FolderGroup or EmptyRowKind.Folder)));
                }
                return true;
            case Cmd.Mark:
            {
                if (kept)
                {
                    m.ShowToast("macOS uses these files, so Hoard keeps them");
                    return true;
                }
                List<int> nodes = m.EmptyRowNodes(row);
                bool all = nodes.All(m.IsMarked);
                foreach (int id in nodes.Where(id => m.IsMarked(id) == all))
                {
                    m.ToggleMark(id);
                }
                m.EmptySelected = Math.Min(last, m.EmptySelected + 1);
                return true;
            }
            case Cmd.MarkAll:
            {
                var targets = report.Folders.Concat(report.Zero.Where(g => g.Safety == ZeroSafety.Safe).SelectMany(g => g.Files))
                    .Where(id => !s.IsDeleted(id) && !m.IsMarked(id)).ToList();
                foreach (int id in targets)
                {
                    m.ToggleMark(id);
                }
                m.ShowToast($"marked {Units.Count(targets.Count)} more (empty folders and SAFE files) · u clears marks");
                return true;
            }
            case Cmd.Delete:
                if (m.Marks.Count > 0)
                {
                    OpenDeleteDialog();
                }
                else if (kept)
                {
                    m.ShowToast("macOS uses these files, so Hoard keeps them");
                }
                else
                {
                    OpenDeleteDialog(m.EmptyRowNodes(row));
                }
                return true;
            case Cmd.Reveal when one is int r:
                m.ShowToast(shell.Reveal(m.FullPath(r)));
                return true;
            case Cmd.CopyPath when one is int cp:
                m.ShowToast(shell.Copy(m.FullPath(cp)));
                return true;
            case Cmd.Reveal or Cmd.CopyPath:
                m.ShowToast("→ opens the group; then o and y work on one item");
                return true;
        }
        return false;
    }

    /// <summary>Keys of the volumes screen: pick a disk (or home, or a typed path) to scan.</summary>
    private bool VolumesKey(Cmd cmd)
    {
        BrowserModel m = Model;
        if (m.PathInput is { } typed)
        {
            if (cmd == Cmd.Escape)
            {
                m.PathInput = null;
            }
            else if (cmd == Cmd.Open)
            {
                string path = typed.Trim();
                if (path.StartsWith('~'))
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
                }
                if (Directory.Exists(path))
                {
                    m.PathInput = null;
                    m.VolumesOpen = false;
                    ScanPathRequested = Path.GetFullPath(path);
                }
                else
                {
                    m.ShowToast("no such folder: " + typed);
                }
            }
            return true;
        }
        var list = m.VolumeList;
        switch (cmd)
        {
            case Cmd.Up: m.VolumeSelected = Math.Max(0, m.VolumeSelected - 1); return true;
            case Cmd.Down: m.VolumeSelected = Math.Min(Math.Max(0, list.Count - 1), m.VolumeSelected + 1); return true;
            case Cmd.Open or Cmd.Right when list.Count > 0:
                m.VolumesOpen = false;
                ScanPathRequested = list[Math.Clamp(m.VolumeSelected, 0, list.Count - 1)].Mount;
                return true;
            case Cmd.Home:
                m.VolumesOpen = false;
                ScanPathRequested = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return true;
            case Cmd.PathEntry:
                m.PathInput = "";
                return true;
            case Cmd.ToggleHidden:
                m.ShowSystemVolumes = !m.ShowSystemVolumes;
                m.RefreshVolumes();
                m.VolumeSelected = 0;
                return true;
            case Cmd.Escape or Cmd.Volumes or Cmd.Back:
                m.VolumesOpen = false;
                return true;
            case Cmd.Quit:
                m.QuitConfirmOpen = true;
                return true;
            case Cmd.Help:
                m.HelpOpen = true;
                return true;
            case Cmd.NextTheme:
                m.NextTheme();
                return true;
        }
        return true;
    }

    /// <summary>Keys of watch mode: move in the feed, jump to a folder, pause, clear, stop.</summary>
    private bool WatchKey(Cmd cmd)
    {
        BrowserModel m = Model;
        if (m.Watching is not { } w)
        {
            return false;
        }
        switch (cmd)
        {
            case Cmd.Up: m.WatchSelected = Math.Max(0, m.WatchSelected - 1); return true;
            case Cmd.Down: m.WatchSelected = Math.Min(Math.Max(0, w.Feed.Count - 1), m.WatchSelected + 1); return true;
            case Cmd.Open or Cmd.Right when w.Feed.Count > 0:
            {
                int folder = w.Feed[Math.Clamp(m.WatchSelected, 0, w.Feed.Count - 1)].Folder;
                if (!m.Store.IsDeleted(folder))
                {
                    m.View = ViewKind.Browse;
                    m.Enter(folder);
                    m.ShowToast("still watching · w goes back to the feed");
                }
                return true;
            }
            case Cmd.PathEntry: // p: pause
                w.Paused = !w.Paused;
                m.ShowToast(w.Paused ? "paused: changes wait until you press p again" : "watching again");
                return true;
            case Cmd.Clean: // c: clear the feed
                w.Feed.Clear();
                m.WatchSelected = 0;
                return true;
            case Cmd.Escape or Cmd.Back:
                m.StopWatch();
                return true;
        }
        return false;
    }

    /// <summary>Keys of the Big & old list.</summary>
    private bool BigOldKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        if (cmd == Cmd.Filters)
        {
            m.FilterOpen = true;
            return true;
        }
        if (cmd == Cmd.SortNext)
        {
            m.CycleBigOldSort();
            m.ShowToast("sorted by " + m.BigOldSort switch
            {
                BigOldSort.Size => "size",
                BigOldSort.Age => "age, oldest first",
                BigOldSort.Path => "path",
                _ => "size × age",
            });
            return true;
        }
        if (cmd == Cmd.SortReverse)
        {
            m.ShowToast("s changes the order: size × age, size, age, path");
            return true;
        }
        List<int> items = m.BigOldItems();
        if (items.Count == 0)
        {
            if (cmd is Cmd.Open or Cmd.Mark or Cmd.MarkAll or Cmd.Delete or Cmd.Reveal or Cmd.CopyPath)
            {
                m.ShowToast(m.Scanning ? "the list is ready when the scan finishes" : "nothing in the list · f loosens the filters");
                return true;
            }
            return cmd is Cmd.Up or Cmd.Down;
        }
        int last = items.Count - 1;
        int node = items[Math.Clamp(m.BigOldSelected, 0, last)];
        switch (cmd)
        {
            case Cmd.Up: m.BigOldSelected = Math.Max(0, m.BigOldSelected - 1); return true;
            case Cmd.Down: m.BigOldSelected = Math.Min(last, m.BigOldSelected + 1); return true;
            case Cmd.PageUp: m.BigOldSelected = Math.Max(0, m.BigOldSelected - Math.Max(1, pageSize - 1)); return true;
            case Cmd.PageDown: m.BigOldSelected = Math.Min(last, m.BigOldSelected + Math.Max(1, pageSize - 1)); return true;
            case Cmd.Top: m.BigOldSelected = 0; return true;
            case Cmd.Bottom: m.BigOldSelected = last; return true;
            case Cmd.Right or Cmd.Open:
                m.Enter(m.Store.Parent(node));
                m.SelectNode(node);
                m.View = ViewKind.Browse;
                return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                return true;
            case Cmd.Mark:
                m.ToggleMark(node);
                m.BigOldSelected = Math.Min(last, m.BigOldSelected + 1);
                return true;
            case Cmd.MarkAll:
            {
                int added = 0;
                foreach (int id in items.Where(id => !m.IsMarked(id)))
                {
                    m.ToggleMark(id);
                    added++;
                }
                m.ShowToast($"marked {added} more · u clears marks");
                return true;
            }
            case Cmd.Delete:
                if (m.Marks.Count > 0)
                {
                    OpenDeleteDialog();
                }
                else
                {
                    OpenDeleteDialog([node]);
                }
                return true;
            case Cmd.Reveal:
                m.ShowToast(shell.Reveal(m.FullPath(node)));
                return true;
            case Cmd.CopyPath:
                m.ShowToast(shell.Copy(m.FullPath(node)));
                return true;
        }
        return false;
    }

    /// <summary>Keys of the age heatmap: move between cells, open folders, list a cell's files.</summary>
    private bool AgeKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        NodeStore s = m.Store;
        if (m.AgeFiles is { } list)
        {
            return FileListKey(list, cmd, pageSize, m.CloseAgeFiles);
        }
        switch (cmd)
        {
            case Cmd.AxisModified or Cmd.AxisAccessed:
                m.SetAgeAxis(cmd == Cmd.AxisAccessed ? AgeAxis.Accessed : AgeAxis.Modified);
                return true;
            case Cmd.Clean:
                m.ShowToast("creation times aren't recorded · m: last modified · a: last access");
                return true;
            case Cmd.Back:
                if (m.Current == NodeStore.Root)
                {
                    m.ShowToast("already at the top of the scan");
                    return true;
                }
                int from = m.Current;
                m.Up();
                m.AgeSelected = Math.Max(0, m.Store.Children(m.Current).OrderByDescending(id => s.Size(id)).ToList().IndexOf(from));
                return true;
        }
        if (m.Age is not { } report)
        {
            if (cmd is Cmd.Open or Cmd.Right or Cmd.Left or Cmd.Up or Cmd.Down or Cmd.Mark or Cmd.Delete)
            {
                if (m.Scanning)
                {
                    m.ShowToast("the heatmap is ready when the scan finishes");
                }
                return true;
            }
            return false;
        }
        int last = Math.Max(0, report.Rows.Count - 1);
        int? node = report.Rows.Count > 0 ? report.Rows[Math.Clamp(m.AgeSelected, 0, last)].Node : null;
        switch (cmd)
        {
            case Cmd.Up: m.AgeSelected = Math.Max(0, m.AgeSelected - 1); return true;
            case Cmd.Down: m.AgeSelected = Math.Min(last, m.AgeSelected + 1); return true;
            case Cmd.PageUp or Cmd.Top: m.AgeSelected = 0; return true;
            case Cmd.PageDown or Cmd.Bottom: m.AgeSelected = last; return true;
            case Cmd.Left: m.AgeColumn = Math.Max(-1, m.AgeColumn - 1); return true;
            case Cmd.Right: m.AgeColumn = Math.Min(AgeBuckets.Count - 1, m.AgeColumn + 1); return true;
            case Cmd.Escape: return true;
            case Cmd.Open when node is int id:
                OpenAgeCell(report, Math.Clamp(m.AgeSelected, 0, last));
                return true;
            case Cmd.Mark when node is int mark:
                m.ToggleMark(mark);
                m.AgeSelected = Math.Min(last, m.AgeSelected + 1);
                return true;
            case Cmd.Delete:
                if (m.Marks.Count > 0 || node is null)
                {
                    OpenDeleteDialog();
                }
                else
                {
                    OpenDeleteDialog([node.Value]);
                }
                return true;
            case Cmd.Reveal when node is int r:
                m.ShowToast(shell.Reveal(m.FullPath(r)));
                return true;
            case Cmd.CopyPath when node is int cp:
                m.ShowToast(shell.Copy(m.FullPath(cp)));
                return true;
            case Cmd.MarkAll:
                m.ShowToast("␣ marks a row · ↵ on a cell lists its files, where A marks them all");
                return true;
        }
        return false;
    }

    /// <summary>↵ in the heatmap: the name opens the folder, a cell lists its files.</summary>
    private void OpenAgeCell(AgeReport report, int index)
    {
        BrowserModel m = Model;
        AgeRow row = report.Rows[index];
        if (m.AgeColumn < 0)
        {
            if (m.Store.Kind(row.Node) == NodeKind.Directory && m.Store.Children(row.Node).Any())
            {
                m.Enter(row.Node);
                m.AgeSelected = 0;
            }
            else if (m.Store.Kind(row.Node) == NodeKind.Directory)
            {
                m.ShowToast("this folder is empty");
            }
            else
            {
                m.SelectNode(row.Node);
                m.View = ViewKind.Browse;
            }
            return;
        }
        int b = m.AgeColumn;
        if (row.Files[b] == 0)
        {
            m.ShowToast($"nothing in {m.Store.Name(row.Node)} was {(m.AgeAxis == AgeAxis.Accessed ? "opened" : "modified")} {AgeBuckets.Phrases[b]}");
            return;
        }
        m.OpenAgeFiles(row.Node, b, row.Files[b]);
    }

    /// <summary>Keys of the Types view: the table, and the file list ↵ opens.</summary>
    private bool TypesKey(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        NodeStore s = m.Store;
        if (m.TypeFiles is { } list)
        {
            if (cmd == Cmd.Group)
            {
                m.CloseTypeFiles(); // and switch the grouping below
            }
            else
            {
                return FileListKey(list, cmd, pageSize, m.CloseTypeFiles);
            }
        }
        if (cmd == Cmd.Group)
        {
            m.TypesByExtension = !m.TypesByExtension;
            m.TypesSelected = 0;
            m.TypesScroll = 0;
            return true;
        }
        if (m.Types is not { } report)
        {
            if (cmd is Cmd.Open or Cmd.Right or Cmd.Mark or Cmd.Delete)
            {
                m.ShowToast(m.Scanning ? "file types are added up when the scan finishes" : "still adding up file types, one moment…");
                return true;
            }
            return false;
        }
        int rowLast = Math.Max(0, m.TypeRowCount - 1);
        switch (cmd)
        {
            case Cmd.Up: m.TypesSelected = Math.Max(0, m.TypesSelected - 1); return true;
            case Cmd.Down: m.TypesSelected = Math.Min(rowLast, m.TypesSelected + 1); return true;
            case Cmd.PageUp: m.TypesSelected = Math.Max(0, m.TypesSelected - Math.Max(1, pageSize - 1)); return true;
            case Cmd.PageDown: m.TypesSelected = Math.Min(rowLast, m.TypesSelected + Math.Max(1, pageSize - 1)); return true;
            case Cmd.Top: m.TypesSelected = 0; return true;
            case Cmd.Bottom: m.TypesSelected = rowLast; return true;
            case Cmd.Right or Cmd.Open:
                OpenTypeRow(report, m.TypesSelected);
                return true;
            case Cmd.Left or Cmd.Back or Cmd.Escape:
                return true;
            case Cmd.Mark or Cmd.MarkAll or Cmd.Delete:
                m.ShowToast("↵ lists the files; mark and delete them there");
                return true;
        }
        return false;
    }

    private void OpenTypeRow(TypeReport report, int index)
    {
        BrowserModel m = Model;
        if (m.TypesByExtension)
        {
            if (index < report.Extensions.Count)
            {
                ExtensionTotal e = report.Extensions[index];
                string label = e.Extension == FileTypes.NoExtension ? "Files without an extension" : "." + e.Extension + " files";
                m.OpenTypeFiles(null, e.Extension, label, e.Files);
            }
        }
        else if (index < report.Categories.Count)
        {
            TypeTotal c = report.Categories[index];
            m.OpenTypeFiles(c.Category, null, FileTypes.Label(c.Category), c.Files);
        }
    }

    /// <summary>Shows the n-th biggest entry in the browser (folders open, files get selected).</summary>
    private void OpenFromOverview(int index)
    {
        BrowserModel m = Model;
        List<int> top = m.TopEntries();
        m.View = ViewKind.Browse;
        if (m.Current != NodeStore.Root)
        {
            m.Enter(NodeStore.Root);
        }
        if (index < 0 || index >= top.Count)
        {
            return;
        }
        m.SelectNode(top[index]);
        if (m.Store.Kind(top[index]) == NodeKind.Directory)
        {
            m.Open();
        }
    }

    /// <returns>True if the command did something (so the screen needs a redraw).</returns>
    public bool Apply(Cmd cmd, int pageSize)
    {
        BrowserModel m = Model;
        if (cmd == Cmd.None)
        {
            return false;
        }
        if (m.SplashOpen)
        {
            m.SkipSplash(); // any key skips it
            return true;
        }
        if (m.TourStep is int step && !m.QuitConfirmOpen)
        {
            switch (cmd)
            {
                case Cmd.Right or Cmd.Open or Cmd.Mark or Cmd.Down:
                    if (step >= 2)
                    {
                        m.FinishTour();
                    }
                    else
                    {
                        m.TourStep = step + 1;
                        m.View = step + 1 == 1 ? ViewKind.Junk : ViewKind.Browse;
                    }
                    return true;
                case Cmd.Left or Cmd.Up or Cmd.Back when step > 0:
                    m.TourStep = step - 1;
                    m.View = step - 1 == 1 ? ViewKind.Junk : ViewKind.Browse;
                    return true;
                case Cmd.Quit:
                    m.FinishTour();
                    m.QuitConfirmOpen = true;
                    return true;
                default: // esc and anything else: skip the tour
                    m.FinishTour();
                    m.View = ViewKind.Overview;
                    return true;
            }
        }
        if (m.Dialog is { } dialog)
        {
            return DialogKey(cmd, dialog);
        }
        if (m.QuitConfirmOpen)
        {
            // q again or ↵ quits; anything else (n, esc) stays.
            m.QuitConfirmOpen = false;
            if (cmd is Cmd.Quit or Cmd.Open)
            {
                QuitRequested = true;
            }
            return true;
        }
        if (m.VolumesOpen && !m.HelpOpen && !m.QuitConfirmOpen)
        {
            return VolumesKey(cmd);
        }
        if (cmd == Cmd.Volumes && m.Palette is null && m.Search is null && m.Export is null && !m.SettingsOpen)
        {
            m.RefreshVolumes();
            m.VolumesOpen = true;
            m.ClearToast();
            return true;
        }
        if (m.Palette is { } palette && !m.HelpOpen && !m.QuitConfirmOpen)
        {
            List<PaletteEntry> found = palette.Matches();
            switch (cmd)
            {
                case Cmd.Up: palette.Selected = Math.Max(0, palette.Selected - 1); return true;
                case Cmd.Down: palette.Selected = Math.Min(Math.Max(0, found.Count - 1), palette.Selected + 1); return true;
                case Cmd.Escape: m.Palette = null; return true;
                case Cmd.Open when found.Count > 0:
                    m.Palette = null;
                    Apply(found[Math.Clamp(palette.Selected, 0, found.Count - 1)].Cmd, pageSize);
                    return true;
            }
            return true;
        }
        if (cmd == Cmd.Palette && m.Search is null && m.Export is null && !m.SettingsOpen)
        {
            m.Palette = new PaletteBox();
            m.ClearToast();
            return true;
        }
        if (m.Search is { } search && !m.HelpOpen && !m.QuitConfirmOpen)
        {
            return SearchKey(cmd, search);
        }
        if (cmd == Cmd.Find && !m.SettingsOpen && m.Export is null)
        {
            m.Search = new SearchBox();
            m.ClearToast();
            return true;
        }
        if (m.Export is { } export && !m.HelpOpen && !m.QuitConfirmOpen)
        {
            return ExportKey(cmd, export);
        }
        if (cmd == Cmd.Export && !m.SettingsOpen)
        {
            m.OpenExport();
            m.ClearToast();
            return true;
        }
        if (m.SettingsOpen && m.PatternInput is { } typed)
        {
            if (cmd == Cmd.Open)
            {
                m.AddPattern(typed);
                m.PatternInput = null;
                m.SettingsRow = m.SettingsRowCount - 1; // stay on "+ add pattern" to add another
            }
            else if (cmd == Cmd.Escape)
            {
                m.PatternInput = null;
            }
            return true;
        }
        if (m.SettingsOpen && !m.HelpOpen)
        {
            int patternRow = m.SettingsRow - SettingsItems.All.Length;
            switch (cmd)
            {
                case Cmd.Up: m.SettingsRow = Math.Max(0, m.SettingsRow - 1); break;
                case Cmd.Down: m.SettingsRow = Math.Min(m.SettingsRowCount - 1, m.SettingsRow + 1); break;
                case Cmd.Top or Cmd.PageUp: m.SettingsRow = 0; break;
                case Cmd.Bottom or Cmd.PageDown: m.SettingsRow = m.SettingsRowCount - 1; break;
                case Cmd.Open when patternRow == m.Settings.Exclude.Count:
                    m.PatternInput = "";
                    break;
                case Cmd.Forget or Cmd.Delete when patternRow >= 0 && patternRow < m.Settings.Exclude.Count:
                    m.RemovePattern(patternRow);
                    m.SettingsRow = Math.Min(m.SettingsRow, m.SettingsRowCount - 1);
                    break;
                case Cmd.RescanAll:
                    m.SettingsOpen = false;
                    RescanAllRequested = true;
                    break;
                case Cmd.Left: m.ChangeSetting(-1); break;
                case Cmd.Right or Cmd.Mark or Cmd.Open: m.ChangeSetting(1); break;
                case Cmd.NextTheme: m.NextTheme(); break;
                case Cmd.Help: m.HelpOpen = true; break;
                case Cmd.Quit: m.QuitConfirmOpen = true; break;
                case Cmd.Escape or Cmd.Settings or Cmd.Back:
                    m.SettingsOpen = false;
                    m.ClearToast();
                    break;
                default:
                    m.ShowToast("←→ changes the highlighted setting · esc closes");
                    break;
            }
            return true;
        }
        if (cmd == Cmd.Settings)
        {
            m.FilterOpen = false;
            m.SettingsOpen = true;
            m.ClearToast();
            return true;
        }
        if (m.FilterOpen)
        {
            switch (cmd)
            {
                case Cmd.Up: m.FilterRow = Math.Max(0, m.FilterRow - 1); break;
                case Cmd.Down: m.FilterRow = Math.Min(2, m.FilterRow + 1); break;
                case Cmd.Left or Cmd.Right or Cmd.Mark:
                    m.StepFilter(cmd == Cmd.Left ? -1 : 1);
                    m.Settings.BigOld = m.BigOldOptions; // remembered for next time
                    m.SaveSettings();
                    break;
                case Cmd.Quit: m.FilterOpen = false; m.QuitConfirmOpen = true; break;
                default: m.FilterOpen = false; break; // ↵, esc, f: done
            }
            return true;
        }
        if (m.HelpOpen)
        {
            // Any key closes help; q asks to quit.
            m.HelpOpen = false;
            if (cmd == Cmd.Quit)
            {
                m.QuitConfirmOpen = true;
            }
            return true;
        }
        if (m.ScanScreen && m.Scanning && ScanScreenKey(cmd))
        {
            return true;
        }
        m.ScanScreen = false;
        string? toastBefore = m.Toast;
        m.ClearToast();
        switch (cmd)
        {
            case Cmd.SkipScan when m.View != ViewKind.Snaps: // b: only means something on the scan screen and in Snaps (set base)
                return false;
            case Cmd.NextView:
                m.View = m.View switch
                {
                    ViewKind.Overview => ViewKind.Browse,
                    ViewKind.Browse => ViewKind.Map,
                    ViewKind.Map => ViewKind.Types,
                    ViewKind.Types => ViewKind.Age,
                    ViewKind.Age => ViewKind.Dupes,
                    ViewKind.Dupes => ViewKind.Junk,
                    ViewKind.Junk => ViewKind.BigOld,
                    ViewKind.BigOld => ViewKind.Empty,
                    ViewKind.Empty => ViewKind.Snaps,
                    _ => ViewKind.Overview,
                };
                if (m.View == ViewKind.Dupes)
                {
                    m.StartDupes();
                }
                return true;
            case Cmd.ViewOverview:
                m.View = ViewKind.Overview;
                return true;
            case Cmd.ViewTypes:
                m.View = ViewKind.Types;
                return true;
            case Cmd.ViewAge:
                m.View = ViewKind.Age;
                return true;
            case Cmd.ViewBigOld:
                m.View = ViewKind.BigOld;
                return true;
            case Cmd.ViewEmpty:
                m.View = ViewKind.Empty;
                return true;
            case Cmd.ViewSnaps:
                m.View = ViewKind.Snaps;
                return true;
            case Cmd.ExcludeIt:
            {
                int? target = m.View == ViewKind.Browse ? m.SelectedNode : null;
                m.ShowToast(target is int id ? m.ExcludeNode(id) : "X excludes the highlighted entry in the list (2)");
                return true;
            }
            case Cmd.Watch:
                if (m.Watching is not null)
                {
                    m.View = ViewKind.Watch;
                }
                else
                {
                    m.ShowToast(m.StartWatch());
                }
                return true;
            case Cmd.Rescan:
            {
                // The highlighted folder in the list; elsewhere the open folder.
                int target = m.View == ViewKind.Browse && m.SelectedNode is int sel && m.Store.Kind(sel) == NodeKind.Directory ? sel : m.Current;
                m.ShowToast(m.Rescan(target));
                return true;
            }
            case Cmd.RescanAll:
                if (m.Scanning)
                {
                    m.ShowToast("the scan is still running");
                }
                else
                {
                    RescanAllRequested = true;
                }
                return true;
            case Cmd.SnapshotNow:
                m.ShowToast(m.Snapshots is null ? "snapshots are off here"
                    : m.Scanning ? "a snapshot is saved when the scan finishes"
                    : m.SaveSnapshot() ? "saving a snapshot…" : "already saving one");
                return true;
            case Cmd.ViewDupes:
                m.View = ViewKind.Dupes;
                m.StartDupes(); // opening the tab is the go-ahead to read files
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
                else if (m.View == ViewKind.Age)
                {
                    m.ChangeAgeRows(cmd == Cmd.DepthUp ? 2 : -2);
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
        if (m.View == ViewKind.Overview && OverviewKey(cmd))
        {
            return true;
        }
        if (m.View == ViewKind.Types && TypesKey(cmd, pageSize))
        {
            return true;
        }
        if (m.View == ViewKind.Age && AgeKey(cmd, pageSize))
        {
            return true;
        }
        if (m.View == ViewKind.BigOld && BigOldKey(cmd, pageSize))
        {
            return true;
        }
        if (m.View == ViewKind.Empty && EmptyKey(cmd, pageSize))
        {
            return true;
        }
        if (m.View == ViewKind.Dupes && DupesKey(cmd, pageSize))
        {
            return true;
        }
        if (m.View == ViewKind.Snaps && SnapsKey(cmd, pageSize))
        {
            return true;
        }
        if (m.View == ViewKind.Watch && WatchKey(cmd))
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
            case Cmd.Reveal when LastExport is { } file && toastBefore?.StartsWith("exported", StringComparison.Ordinal) == true:
                m.ShowToast(shell.Reveal(file));
                break;
            case Cmd.Reveal: m.ShowToast(shell.Reveal(m.FullPath(m.SelectedNode ?? m.Current))); break;
            case Cmd.CopyPath: m.ShowToast(shell.Copy(m.FullPath(m.SelectedNode ?? m.Current))); break;
            case Cmd.Escape when m.ListFilter is not null && m.View == ViewKind.Browse:
                m.SetListFilter(null);
                m.ShowToast("filter cleared");
                break;
            case Cmd.Escape:
                if (!m.Up())
                {
                    m.ShowToast("press q to quit");
                }
                break;
            case Cmd.Quit: m.QuitConfirmOpen = true; break;
            case Cmd.Mark when m.View is not (ViewKind.Browse or ViewKind.Map):
                m.ShowToast("nothing to mark here");
                break;
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
        if (m.SplashOpen)
        {
            m.SkipSplash();
            return true;
        }
        if (m.SettingsOpen)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.SettingsRow = item;
                    if (doubleClick)
                    {
                        m.ChangeSetting(1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.ScanScreen && m.Scanning)
        {
            m.ScanScreen = false;
            foreach (var (x0, x1, view) in hits.Tabs)
            {
                if (y == 0 && x >= x0 && x < x1)
                {
                    m.View = view;
                }
            }
            return true;
        }
        if (y == 0)
        {
            foreach (var (x0, x1, view) in hits.Tabs)
            {
                if (x >= x0 && x < x1)
                {
                    m.View = view;
                    if (view == ViewKind.Dupes)
                    {
                        m.StartDupes();
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Snaps)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    if (item >= 1000)
                    {
                        m.SnapDiffFocus = true;
                        m.SnapDiffSelected = item - 1000;
                    }
                    else
                    {
                        m.SnapDiffFocus = false;
                        m.SnapSelected = item;
                    }
                    if (doubleClick)
                    {
                        SnapsKey(Cmd.Open, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Dupes)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    m.DupSelected = item;
                    if (doubleClick)
                    {
                        DupesKey(Cmd.Open, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Empty)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    m.EmptySelected = item;
                    if (doubleClick)
                    {
                        EmptyKey(Cmd.Open, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.BigOld)
        {
            if (m.FilterOpen)
            {
                m.FilterOpen = false;
                return true;
            }
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    m.BigOldSelected = item;
                    if (doubleClick)
                    {
                        BigOldKey(Cmd.Open, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Age)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    if (m.AgeFiles is { } files)
                    {
                        files.Selected = item;
                    }
                    else
                    {
                        (m.AgeSelected, m.AgeColumn) = Screens.AgeHitCell(item);
                    }
                    if (doubleClick)
                    {
                        AgeKey(Cmd.Open, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Types)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    if (m.TypeFiles is { } list)
                    {
                        list.Selected = item;
                    }
                    else
                    {
                        m.TypesSelected = item;
                    }
                    if (doubleClick)
                    {
                        TypesKey(Cmd.Open, 1);
                    }
                    return true;
                }
            }
            return false;
        }
        if (m.View == ViewKind.Overview)
        {
            foreach (var (iy, x0, x1, item) in hits.Items)
            {
                if (y == iy && x >= x0 && x < x1)
                {
                    m.ClearToast();
                    m.OverviewSelected = item;
                    if (doubleClick)
                    {
                        OpenFromOverview(item);
                    }
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
        if (Model.SettingsOpen)
        {
            Model.SettingsRow = Math.Clamp(Model.SettingsRow + Math.Sign(delta), 0, SettingsItems.All.Length - 1);
            return true;
        }
        if (Model.View == ViewKind.Overview)
        {
            return OverviewKey(delta > 0 ? Cmd.Down : Cmd.Up);
        }
        if (Model.View is ViewKind.Types or ViewKind.Age or ViewKind.BigOld or ViewKind.Empty or ViewKind.Dupes or ViewKind.Snaps)
        {
            for (int i = 0; i < Math.Abs(delta); i++)
            {
                Cmd move = delta > 0 ? Cmd.Down : Cmd.Up;
                _ = Model.View switch
                {
                    ViewKind.Types => TypesKey(move, 1),
                    ViewKind.Age => AgeKey(move, 1),
                    ViewKind.Empty => EmptyKey(move, 1),
                    ViewKind.Dupes => DupesKey(move, 1),
                    ViewKind.Snaps => SnapsKey(move, 1),
                    _ => BigOldKey(move, 1),
                };
            }
            return true;
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
