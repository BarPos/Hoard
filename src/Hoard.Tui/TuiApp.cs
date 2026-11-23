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
    public static int Run(string rootPath, Scanner scanner, Theme theme)
    {
        using var cancel = new CancellationTokenSource();
        ScanSession session = scanner.Start(rootPath, cancel.Token);
        using IApplication app = Application.Create();
        app.Init();
        var view = new HoardView(app, session, cancel, theme);
        app.AddTimeout(TimeSpan.FromMilliseconds(80), view.OnTick);
        app.Run(view);
        cancel.Cancel(); // quitting mid-scan stops the workers
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
    private readonly ScanSession _session;
    private readonly CancellationTokenSource _cancel;
    private readonly BrowserController _browser;
    private HitMap _hits = new();
    private bool _finished;
    private string? _error;

    public int ExitCode { get; private set; }

    public HoardView(IApplication app, ScanSession session, CancellationTokenSource cancel, Theme theme)
    {
        _app = app;
        _session = session;
        _cancel = cancel;
        var deleter = new Deleter(session.Store, session.RootPath, FileProbes.ForCurrentPlatform(), Trashes.ForCurrentPlatform());
        _browser = new BrowserController(new BrowserModel(session, theme), new SystemShell(), deleter);
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    /// <summary>Runs on the UI thread every 80 ms: animation, live refresh, and the end of the scan.</summary>
    public bool OnTick()
    {
        BrowserModel m = _browser.Model;
        m.Tick++;
        if (_browser.Poll() || m.Dialog is { Stage: DialogStage.Running })
        {
            SetNeedsDraw();
        }
        if (!_finished && _session.IsComplete)
        {
            _finished = true;
            if (_session.Completion.IsFaulted)
            {
                _error = _session.Completion.Exception?.GetBaseException().Message ?? "scan failed";
            }
            else if (_session.Result is { Cancelled: false } r)
            {
                m.RefreshLive();
                m.ShowToast($"scan complete · {Hoard.Core.Formatting.Units.Count(r.Store.Files(Hoard.Core.Model.NodeStore.Root))} files in {r.Elapsed.TotalSeconds:0.0} s");
            }
            SetNeedsDraw();
            return true;
        }
        if (!_finished)
        {
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
        if (_browser.WantsText)
        {
            // The permanent-delete confirmation takes plain typing.
            switch (key.KeyCode)
            {
                case KeyCode.Enter: _browser.Apply(Cmd.Open, 1); break;
                case KeyCode.Esc: _browser.Apply(Cmd.Escape, 1); break;
                case KeyCode.Backspace: _browser.Backspace(); break;
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
        if (mouse.Position is not { } pos)
        {
            return false;
        }
        bool changed = false;
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
