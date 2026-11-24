using Hoard.Core.Actions;
using Hoard.Core.Scanning;
using Hoard.Platform;
using Hoard.Tui.Browser;
using Hoard.Tui.Rendering;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgTextStyle = Terminal.Gui.Drawing.TextStyle;

namespace Hoard.Tui;

/// <summary>
/// Entry point for the interactive UI. The scan starts in the background and the browser opens
/// at once: folders fill in and sizes grow while you look around (plan.md §5.1).
/// </summary>
public static class TuiApp
{
    /// <returns>Process exit code: 0, or 130 if interrupted with Ctrl-C.</returns>
    /// <exception cref="DirectoryNotFoundException">The root is missing or not a directory.</exception>
    /// <param name="scannerFor">Builds a scanner for another folder (its own firmlink aliases); null reuses <paramref name="scanner"/>.</param>
    public static int Run(string rootPath, Scanner scanner, Theme theme, Hoard.Core.Config.Settings? settings = null,
        string? settingsPath = null, bool apparent = false, string? startupNote = null, Func<string, Scanner>? scannerFor = null)
    {
        using IApplication app = Application.Create();
        app.Init();
        var view = new HoardView(app, scanner, rootPath, theme, scannerFor);
        view.Model.UseSettings(settings ?? new Hoard.Core.Config.Settings { Theme = theme.Name }, settingsPath, apparent);
        if (settingsPath is not null)
        {
            // Real runs keep snapshots; a run without a settings file (tests) doesn't write any.
            view.Model.DupCache = new Hoard.Core.Analysis.HashCache(Hoard.Core.Analysis.HashCache.DefaultFile());
            view.Model.Snapshots = new Hoard.Core.Snapshots.SnapshotStore(
                Hoard.Core.Snapshots.SnapshotStore.DefaultDirectory(), view.Model.Settings.KeepSnapshots);
        }
        if (startupNote is not null)
        {
            view.Model.ShowToast(startupNote);
        }
        // The splash: about 1.5 s (80 ms ticks), unless animations are off (setting, HOARD_ANIMATE=0, or --no-animate).
        bool animate = view.Model.Settings.Animations && Environment.GetEnvironmentVariable("HOARD_ANIMATE") != "0";
        if (Environment.GetEnvironmentVariable("HOARD_ANIMATE") == "1")
        {
            animate = true; // --animate
        }
        view.Model.SplashTicks = animate ? 19 : 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(80), view.OnTick);
        app.Run(view);
        view.Shutdown(); // stops the scan and any analysis still running
        return view.ExitCode;
    }
}

/// <summary>Adapts Terminal.Gui drawing calls to <see cref="ICanvas"/>.</summary>
internal sealed class TerminalCanvas(View view) : ICanvas
{
    public int Width => view.Viewport.Width;
    public int Height => view.Viewport.Height;

    private static TgColor C(Rgb c) => new(c.R, c.G, c.B);

    private void Set(Style s)
    {
        TgTextStyle ts = TgTextStyle.None;
        if ((s.Attr & TextAttr.Bold) != 0) ts |= TgTextStyle.Bold;
        if ((s.Attr & TextAttr.Underline) != 0) ts |= TgTextStyle.Underline;
        if ((s.Attr & TextAttr.Italic) != 0) ts |= TgTextStyle.Italic;
        if ((s.Attr & TextAttr.Faint) != 0) ts |= TgTextStyle.Faint;
        view.SetAttribute(new TgAttribute(C(s.Fg), C(s.Bg), ts));
    }

    public void Text(int x, int y, string text, Style style)
    {
        text = Glyphs.Apply(text);
        if (y < 0 || y >= Height || x >= Width || text.Length == 0)
        {
            return;
        }
        if (x < 0)
        {
            return;
        }
        int room = Width - x;
        if (Cells.Width(text) > room)
        {
            text = Cells.Fit(text, room);
        }
        Set(style);
        view.Move(x, y);
        view.AddStr(text);
    }

    public void Fill(int x, int y, int width, int height, Style style, char ch = ' ')
    {
        ch = Glyphs.Apply(ch);
        int x0 = Math.Max(0, x), x1 = Math.Min(Width, x + width);
        if (x1 <= x0)
        {
            return;
        }
        string row = new(ch, x1 - x0);
        Set(style);
        for (int yy = Math.Max(0, y); yy < Math.Min(Height, y + height); yy++)
        {
            view.Move(x0, yy);
            view.AddStr(row);
        }
    }
}

/// <summary>The one full-screen view. All drawing goes through <see cref="Screens"/>.</summary>
internal sealed class HoardView : Runnable
{
    private readonly IApplication _app;
    private Scanner _scanner;
    private readonly Func<string, Scanner>? _scannerFor;
    private string _rootPath;
    private ScanSession _session;
    private CancellationTokenSource _cancel;
    private BrowserController _browser;
    private HitMap _hits = new();
    private bool _finished;
    private string? _error;

    public int ExitCode { get; private set; }

    public void Shutdown()
    {
        _cancel.Cancel();
        _browser.Model.CancelJunk();
    }

    public BrowserModel Model => _browser.Model;

    public HoardView(IApplication app, Scanner scanner, string rootPath, Theme theme, Func<string, Scanner>? scannerFor = null)
    {
        _app = app;
        _scanner = scanner;
        _scannerFor = scannerFor;
        _rootPath = rootPath;
        _cancel = new CancellationTokenSource();
        _session = scanner.Start(rootPath, _cancel.Token);
        _browser = NewController(_session, theme);
        _browser.Model.View = ViewKind.Overview;
        _browser.Model.ScanScreen = true;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        MousePositionTracking = true; // hover highlight
    }

    private BrowserController NewController(ScanSession session, Theme theme)
    {
        var deleter = new Deleter(session.Store, session.RootPath, FileProbes.ForCurrentPlatform(), Trashes.ForCurrentPlatform());
        var model = new BrowserModel(session, theme)
        {
            SharedBlocks = Hoard.Platform.SharedBlocks.ForCurrentPlatform(),
            Rescanner = (path, token) => _scanner.Scan(path, token),
        };
        return new BrowserController(model, new SystemShell(), deleter);
    }

    /// <summary>R: a whole new scan of the same folder, keeping the view, theme and settings.</summary>
    private void RescanAll()
    {
        BrowserModel old = _browser.Model;
        Shutdown();
        _cancel = new CancellationTokenSource();
        _session = _scanner.Start(_rootPath, _cancel.Token);
        _browser = NewController(_session, old.Theme);
        BrowserModel m = _browser.Model;
        m.UseSettings(old.Settings, old.SettingsPath, old.Apparent);
        m.Snapshots = old.Snapshots;
        m.DupCache = old.DupCache;
        m.View = old.View;
        _finished = false;
        _error = null;
        m.ShowToast("rescanning everything · the results fill in as it goes");
    }

    /// <summary>Runs on the UI thread every 80 ms: animation, live refresh, and the end of the scan.</summary>
    public bool OnTick()
    {
        if (_browser.RescanAllRequested)
        {
            _browser.RescanAllRequested = false;
            RescanAll();
        }
        if (_browser.ScanPathRequested is { } path)
        {
            _browser.ScanPathRequested = null;
            _rootPath = path;
            if (_scannerFor is not null)
            {
                _scanner = _scannerFor(path);
            }
            RescanAll();
            _browser.Model.View = ViewKind.Overview;
            _browser.Model.ShowToast("scanning " + Screens.Shorten(path) + " · the results fill in as it goes");
        }
        BrowserModel m = _browser.Model;
        m.Tick++;
        if (_browser.Poll() | m.PollSnapshotSave() | _browser.PollExport() | m.PollRescan() | m.PollWatch() | _browser.PollDupVerify() || m.DupVerifying || m.SplashOpen || m.Tick == m.SplashTicks || m.Rescanning || m.View == ViewKind.Watch || m.Export is { Writing: not null } || m.Dialog is { Stage: DialogStage.Running } || m.PollJunk() | m.PollTypes()
            || (m.View == ViewKind.Junk && m.JunkLoading)
            || (m.View == ViewKind.Overview && (m.JunkLoading || m.TypesLoading))
            || (m.View == ViewKind.Types && (m.TypesLoading || m.TypeFiles is { Job.Result: null, Job.Error: null }))
            || (m.View == ViewKind.Age && (m.AgeLoading || m.AgeFiles is { Job.Result: null, Job.Error: null }))
            || (m.View is ViewKind.BigOld or ViewKind.Overview && (m.BigOldLoading || m.EmptyLoading || m.DupesRunning))
            || (m.View == ViewKind.Empty && m.EmptyLoading)
            || (m.View == ViewKind.Dupes && m.DupesRunning)
            || (m.View == ViewKind.Snaps && m.SnapLoading)
            || m.SearchRunning)
        {
            SetNeedsDraw();
        }
        if (!_finished && _session.IsComplete)
        {
            _finished = true;
            m.ScanScreen = false; // the results take over when the scan is done
            if (!m.Settings.TourDone && m.SettingsPath is not null && _session.Result is { Cancelled: false })
            {
                m.TourStep = 0;
                m.View = ViewKind.Browse;
            }
            if (_session.Completion.IsFaulted)
            {
                _error = _session.Completion.Exception?.GetBaseException().Message ?? "scan failed";
            }
            else if (_session.Result is { Cancelled: false } r)
            {
                m.StartJunk(); // usually ready before anyone opens the Junk tab
                m.StartTypes();
                if (m.Settings.AutoSnapshot)
                {
                    m.SaveSnapshot(quiet: true); // the "scan complete" note stays
                }
                if (m.View == ViewKind.Dupes)
                {
                    m.StartDupes(); // they opened the tab while scanning
                }
                m.RefreshLive();
                m.ShowToast($"scan complete · {Hoard.Core.Formatting.Units.Count(r.Store.Files(Hoard.Core.Model.NodeStore.Root))} files in {r.Elapsed.TotalSeconds:0.0} s");
            }
            SetNeedsDraw();
            return true;
        }
        if (!_finished)
        {
            m.SampleScan(Environment.TickCount64);
            // Re-read the open folder ~4 times a second; redraw every tick for the spinners.
            if (m.Tick % 3 == 0)
            {
                m.RefreshLive();
            }
            SetNeedsDraw();
        }
        return true;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var canvas = new TerminalCanvas(this);
        if (_error is not null)
        {
            Screens.TooSmall(canvas, _browser.Model.Theme); // reuse the centred-message layout
            canvas.Text(2, 1, Cells.Fit("hoard: " + _error + "  (press q to quit)", canvas.Width - 4), new Style(_browser.Model.Theme.Bad, _browser.Model.Theme.Bg));
            return true;
        }
        _hits = Screens.Browser(canvas, _browser.Model);
        _browser.LastHits = _hits;
        return true;
    }

    private Layout CurrentLayout => new(Viewport.Width, Viewport.Height);

    protected override bool OnKeyDown(Key key)
    {
        if (key.IsCtrl && key.NoCtrl.KeyCode == KeyCode.C)
        {
            _cancel.Cancel();
            ExitCode = 130;
            _app.RequestStop();
            return true;
        }
        if (_browser.Model.QuitConfirmOpen && key.AsRune.Value is > 31 and < 0x10000 && !key.IsCtrl
            && _browser.AnswerQuit((char)key.AsRune.Value))
        {
            if (_browser.QuitRequested)
            {
                _cancel.Cancel();
                _app.RequestStop();
            }
            SetNeedsDraw();
            return true;
        }
        if (_browser.WantsText && key.IsCtrl)
        {
            // In text fields plain keys are text, so actions there use Ctrl: ^t mark, ^a all, ^f filter, ^s size.
            Cmd ctrl = key.NoCtrl.KeyCode switch
            {
                KeyCode.T => Cmd.Mark,
                KeyCode.A => Cmd.MarkAll,
                KeyCode.F => Cmd.Filters,
                KeyCode.S => Cmd.SortNext,
                _ => Cmd.None,
            };
            if (ctrl != Cmd.None && _browser.Apply(ctrl, 1))
            {
                SetNeedsDraw();
            }
            return true;
        }
        if (_browser.WantsText)
        {
            // The permanent-delete confirmation takes plain typing.
            switch (key.KeyCode)
            {
                case KeyCode.Enter: _browser.Apply(Cmd.Open, 1); break;
                case KeyCode.Esc: _browser.Apply(Cmd.Escape, 1); break;
                case KeyCode.Backspace: _browser.Backspace(); break;
                case KeyCode.CursorUp: _browser.Apply(Cmd.Up, 1); break;
                case KeyCode.CursorDown: _browser.Apply(Cmd.Down, 1); break;
                case KeyCode.Tab: _browser.Apply(Cmd.NextView, 1); break;
                case KeyCode.PageUp: _browser.Apply(Cmd.PageUp, 1); break;
                case KeyCode.PageDown: _browser.Apply(Cmd.PageDown, 1); break;
                default:
                    if (key.AsRune.Value is > 31 and < 0x10000 && !key.IsCtrl && !key.IsAlt)
                    {
                        _browser.TypeChar((char)key.AsRune.Value);
                    }
                    break;
            }
            SetNeedsDraw();
            return true;
        }
        Cmd cmd = key.KeyCode switch
        {
            KeyCode.CursorUp => Cmd.Up,
            KeyCode.CursorDown => Cmd.Down,
            KeyCode.CursorRight => Cmd.Right,
            KeyCode.CursorLeft => Cmd.Left,
            KeyCode.Enter => Cmd.Open,
            KeyCode.Backspace => Cmd.Back,
            KeyCode.Tab => Cmd.NextView,
            KeyCode.PageUp => Cmd.PageUp,
            KeyCode.PageDown => Cmd.PageDown,
            KeyCode.Home => Cmd.Top,
            KeyCode.End => Cmd.Bottom,
            KeyCode.Esc => Cmd.Escape,
            KeyCode.Delete => Cmd.Forget,
            _ => key.AsRune.Value is > 0 and < 0x10000 && !key.IsCtrl && !key.IsAlt
                ? BrowserController.FromChar((char)key.AsRune.Value)
                : Cmd.None,
        };
        if (_error is not null)
        {
            if (cmd is Cmd.Quit or Cmd.Escape)
            {
                ExitCode = 1;
                _app.RequestStop();
            }
            return true;
        }
        if (_browser.Apply(cmd, CurrentLayout.VisibleRows))
        {
            SetNeedsDraw();
        }
        if (_browser.QuitRequested)
        {
            _cancel.Cancel();
            _app.RequestStop();
        }
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Position is not { } pos || !_browser.Model.Settings.Mouse)
        {
            return false; // mouse switched off in settings: leave it to the terminal
        }
        bool changed = false;
        if (mouse.Flags.HasFlag(MouseFlags.PositionReport) && !mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            int? before = _browser.Model.HoverY;
            _browser.Model.HoverY = pos.Y;
            if (before != pos.Y)
            {
                SetNeedsDraw();
            }
            return true;
        }
        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            changed = _browser.Wheel(3);
        }
        else if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            changed = _browser.Wheel(-3);
        }
        else if (mouse.IsDoubleClicked)
        {
            changed = _browser.Click(pos.X, pos.Y, true, CurrentLayout, _hits);
        }
        else if (mouse.IsSingleClicked)
        {
            changed = _browser.Click(pos.X, pos.Y, false, CurrentLayout, _hits);
        }
        if (changed)
        {
            SetNeedsDraw();
        }
        return changed;
    }
}
