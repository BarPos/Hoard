using Hoard.Core.Analysis;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Tui.Rendering;

namespace Hoard.Tui.Browser;

public enum SortField { Size, Name, Files, Modified }

/// <summary>The views that exist so far, with their tab keys from plan.md §7.1a.</summary>
public enum ViewKind { Browse, Map, Junk }

/// <summary>Everything the browser shows, independent of any terminal library.</summary>
public sealed class BrowserModel
{
    public BrowserModel(ScanSession session, Theme? theme = null, Func<long>? now = null)
    {
        Session = session;
        Theme = theme ?? Theme.Hoard;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Refresh();
    }

    private readonly Func<long> _now;
    private List<int> _rows = [];

    public ScanSession Session { get; }
    public NodeStore Store => Session.Store;
    public string RootPath => Session.RootPath;
    /// <summary>True until the scan finishes; sizes and lists keep growing meanwhile.</summary>
    public bool Scanning => !Session.IsComplete;
    /// <summary>Animation frame counter, advanced by the host.</summary>
    public int Tick { get; set; }
    public long ErrorCount => Session.Result?.Errors.Count ?? Session.Progress.Errors;
    public long HardLinkDuplicates => Session.Result?.HardLinkDuplicates ?? 0;
    public TimeSpan Elapsed => Session.Result?.Elapsed ?? Session.Progress.Elapsed;

    public bool IsPending(int id) => (Store.Flags(id) & NodeFlags.Pending) != 0;

    /// <summary>Anything in this folder still being scanned (its totals are still growing).</summary>
    public bool IsGrowing(int id) => Scanning && Store.IsGrowing(id);

    /// <summary>
    /// False until the user moves the selection in this folder. While false, live refreshes keep
    /// the highlight on the top row instead of following whichever item happened to be first.
    /// </summary>
    public bool UserMoved { get; private set; }
    public int Current { get; private set; } = NodeStore.Root;
    /// <summary>Index into <see cref="Rows"/> of the highlighted entry.</summary>
    public int Selected { get; private set; }
    /// <summary>First visible row index (kept by the renderer so the selection stays on screen).</summary>
    public int Scroll { get; set; }
    public SortField Sort { get; private set; } = SortField.Size;
    public bool Descending { get; private set; } = true;
    public bool Apparent { get; private set; }
    public Theme Theme { get; set; }
    public bool HelpOpen { get; set; }
    /// <summary>The delete confirmation, while it's open.</summary>
    public DeleteDialog? Dialog { get; set; }

    // ── junk report (plan.md §6.1) ─────────────────────────────────────────
    private JunkReport? _junk;
    /// <summary>Rule ids whose items are included in "clean selected". SAFE ones start checked.</summary>
    public HashSet<string> JunkChecked { get; } = [];
    public HashSet<string> JunkExpanded { get; } = [];
    /// <summary>Individual items left out of an otherwise checked category.</summary>
    public HashSet<int> JunkSkipped { get; } = [];
    public int JunkSelected { get; set; }
    public int JunkScroll { get; set; }

    /// <summary>The report, computed once the scan has finished (null while scanning).</summary>
    public JunkReport? Junk
    {
        get
        {
            if (_junk is null && !Scanning)
            {
                _junk = JunkFinder.Find(Store, RootPath);
                foreach (JunkCategory c in _junk.Categories.Where(c => c.Rule.Safety == Safety.Safe))
                {
                    JunkChecked.Add(c.Rule.Id);
                }
            }
            return _junk;
        }
    }

    /// <summary>Visible junk rows: (category index, item index or -1 for the category line).</summary>
    public List<(int Cat, int Item)> JunkRows()
    {
        var rows = new List<(int, int)>();
        if (Junk is not { } report)
        {
            return rows;
        }
        for (int c = 0; c < report.Categories.Count; c++)
        {
            JunkCategory cat = report.Categories[c];
            if (cat.Count(Store) == 0)
            {
                continue;
            }
            rows.Add((c, -1));
            if (JunkExpanded.Contains(cat.Rule.Id))
            {
                for (int i = 0; i < cat.Items.Count; i++)
                {
                    if (!Store.IsDeleted(cat.Items[i]))
                    {
                        rows.Add((c, i));
                    }
                }
            }
        }
        return rows;
    }

    public bool IsJunkItemIncluded(JunkCategory cat, int node) =>
        JunkChecked.Contains(cat.Rule.Id) && !JunkSkipped.Contains(node);

    /// <summary>Items "clean selected" would delete, and their total.</summary>
    public (List<int> Items, long Size) JunkSelection()
    {
        var items = new List<int>();
        if (Junk is { } report)
        {
            foreach (JunkCategory cat in report.Categories)
            {
                items.AddRange(cat.Items.Where(i => !Store.IsDeleted(i) && IsJunkItemIncluded(cat, i)));
            }
        }
        return (items, items.Sum(i => Store.Size(i)));
    }

    // ── marks (global across folders and views, plan.md §8.1) ──────────────
    private readonly HashSet<int> _marks = [];
    public IReadOnlySet<int> Marks => _marks;
    public bool IsMarked(int id) => _marks.Contains(id);

    public void ToggleMark(int id)
    {
        if (!_marks.Remove(id))
        {
            _marks.Add(id);
        }
    }

    public int MarkAllVisible()
    {
        int added = 0;
        foreach (int id in _rows)
        {
            if (_marks.Add(id))
            {
                added++;
            }
        }
        return added;
    }

    public void ClearMarks() => _marks.Clear();

    public void Unmark(IEnumerable<int> ids)
    {
        foreach (int id in ids)
        {
            _marks.Remove(id);
        }
        _marks.RemoveWhere(Store.IsDeleted);
    }

    /// <summary>Marked items and their total size, counting nested marks once.</summary>
    public (int Count, long Size) MarkedSummary()
    {
        int count = 0;
        long size = 0;
        foreach (int id in _marks)
        {
            if (Store.IsDeleted(id))
            {
                continue;
            }
            bool covered = false;
            for (int n = Store.Parent(id); n != NodeStore.None; n = Store.Parent(n))
            {
                if (_marks.Contains(n))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
            {
                count++;
                size += SizeOf(id);
            }
        }
        return (count, size);
    }

    /// <summary>After a delete: if the open folder (or a parent) is gone, move up to what remains.</summary>
    public void LeaveDeletedFolders()
    {
        int target = Current;
        for (int n = Current; n != NodeStore.None; n = Store.Parent(n))
        {
            if (Store.IsDeleted(n))
            {
                target = Store.Parent(n);
            }
        }
        if (target != Current)
        {
            Enter(target);
        }
        else
        {
            RefreshLive();
        }
    }
    public ViewKind View { get; set; } = ViewKind.Browse;
    /// <summary>Treemap levels drawn (1–3).</summary>
    public int MapDepth { get; private set; } = 2;

    public void ChangeMapDepth(int delta)
    {
        int d = Math.Clamp(MapDepth + delta, 1, 3);
        ShowToast(d == MapDepth ? $"depth stays at {d} (1–3)" : $"treemap depth {d}");
        MapDepth = d;
    }

    /// <summary>Selects an entry of the current folder by node id (from the treemap).</summary>
    public void SelectNode(int id)
    {
        int i = _rows.IndexOf(id);
        if (i >= 0)
        {
            Select(i);
        }
    }
    public string? Toast { get; private set; }
    public long Now => _now();

    public IReadOnlyList<int> Rows => _rows;
    public int? SelectedNode => _rows.Count > 0 ? _rows[Selected] : null;

    public long SizeOf(int id) => Apparent ? Store.Apparent(id) : Store.Size(id);

    public string FullPath(int id)
    {
        string rel = Store.RelativePath(id);
        if (rel.Length == 0)
        {
            return RootPath;
        }
        return RootPath == "/" ? "/" + rel : RootPath + "/" + rel;
    }

    public void ShowToast(string message) => Toast = message;
    public void ClearToast() => Toast = null;

    public void MoveBy(int delta) => Select(Selected + delta);

    /// <summary>Selects a row on the user's behalf.</summary>
    public void Select(int index)
    {
        UserMoved = true;
        Clamp(index);
    }

    private void Clamp(int index) =>
        Selected = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1);

    /// <summary>Opens the selected directory. Returns false (with a hint) if it can't be opened.</summary>
    public bool Open()
    {
        if (SelectedNode is not int id)
        {
            return false;
        }
        switch (Store.Kind(id))
        {
            case NodeKind.Directory when Store.ChildCount(id) > 0 || IsPending(id):
                Enter(id);
                return true;
            case NodeKind.Directory:
                ShowToast((Store.Flags(id) & NodeFlags.Error) != 0 ? "can't read this folder (permission denied)" : "this folder is empty");
                return false;
            case NodeKind.MountPoint:
                ShowToast("on another disk, so not scanned (use --cross-fs)");
                return false;
            case NodeKind.Alias:
                ShowToast("same folder as another path in this scan, so it is counted there");
                return false;
            case NodeKind.Excluded:
                ShowToast("excluded by a pattern, so not scanned");
                return false;
            case NodeKind.Symlink:
                ShowToast("a symlink: hoard doesn't follow links");
                return false;
            default:
                ShowToast("a file, not a folder");
                return false;
        }
    }

    public void Enter(int dir)
    {
        Current = dir;
        Selected = 0;
        Scroll = 0;
        UserMoved = false;
        Refresh();
    }

    /// <summary>Goes to the parent folder, keeping the folder we came from selected.</summary>
    public bool Up()
    {
        if (Current == NodeStore.Root)
        {
            return false;
        }
        int from = Current;
        Current = Store.Parent(Current);
        Refresh();
        Select(Math.Max(0, _rows.IndexOf(from))); // keeps following the folder we came from
        Scroll = Math.Max(0, Selected - 3);
        return true;
    }

    public void CycleSort()
    {
        Sort = Sort switch
        {
            SortField.Size => SortField.Name,
            SortField.Name => SortField.Files,
            SortField.Files => SortField.Modified,
            _ => SortField.Size,
        };
        Descending = Sort != SortField.Name;
        ResortKeepingSelection();
    }

    public void ReverseSort()
    {
        Descending = !Descending;
        ResortKeepingSelection();
    }

    public void ToggleSizeMode()
    {
        Apparent = !Apparent;
        ResortKeepingSelection();
        ShowToast(Apparent ? "showing apparent sizes (logical file length)" : "showing on-disk sizes (space actually used)");
    }

    public void NextTheme()
    {
        int i = Array.IndexOf(Theme.All, Theme);
        Theme = Theme.All[(i + 1) % Theme.All.Length];
        ShowToast("theme: " + Theme.Name);
    }

    /// <summary>Re-reads the current folder (new entries, grown sizes) while keeping the selection.</summary>
    public void RefreshLive() => ResortKeepingSelection();

    private void ResortKeepingSelection()
    {
        int? keep = SelectedNode;
        Refresh();
        if (!UserMoved)
        {
            Clamp(0);
        }
        else if (keep is int k)
        {
            Clamp(Math.Max(0, _rows.IndexOf(k)));
        }
    }

    private void Refresh()
    {
        NodeStore s = Store;
        IEnumerable<int> kids = s.Children(Current);
        IOrderedEnumerable<int> ordered = Sort switch
        {
            SortField.Name => kids.OrderBy(id => s.Name(id), StringComparer.OrdinalIgnoreCase),
            SortField.Files => kids.OrderBy(id => s.Files(id)),
            SortField.Modified => kids.OrderBy(id => s.MTime(id)),
            _ => kids.OrderBy(SizeOf),
        };
        // Ties always break by name so the order is stable.
        ordered = ordered.ThenBy(id => s.Name(id), StringComparer.Ordinal);
        _rows = [.. ordered];
        if (Descending)
        {
            _rows.Reverse();
        }
        Clamp(Selected);
    }
}
