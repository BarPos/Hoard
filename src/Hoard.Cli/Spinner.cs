using Hoard.Core.Formatting;
using Hoard.Core.Scanning;

namespace Hoard.Cli;

/// <summary>Live "scanning…" line on stderr, only when stderr is a terminal.</summary>
internal sealed class Spinner : IDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly Timer? _timer;
    private readonly Lock _gate = new();
    private bool _disposed;
    private int _frame;

    private Spinner(ScanProgress progress, Tty term, bool iec)
    {
        _timer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                string f = Frames[_frame++ % Frames.Length];
                string line = $"{f} scanning… {Units.Count(progress.Files)} files · {Units.Size(progress.Bytes, iec)} · {progress.Elapsed.TotalSeconds:0.0} s";
                Console.Error.Write("\r" + term.Fg(Tty.Accent, line) + "\e[K");
            }
        }, null, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(100));
    }

    private Spinner() { }

    public static Spinner Start(ScanProgress progress, Tty term, bool iec) =>
        Console.IsErrorRedirected ? new Spinner() : new Spinner(progress, term, iec);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_timer is not null)
            {
                _timer.Dispose();
                Console.Error.Write("\r\e[K");
            }
        }
    }
}
