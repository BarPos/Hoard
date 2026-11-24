namespace Hoard.Tui.Browser;

/// <summary>
/// One analysis running off the UI thread (plan.md §7.10: never freeze, show progress).
/// The UI polls it each tick; <see cref="Restart"/> throws away the old result.
/// </summary>
public sealed class BackgroundJob<T>(Func<CancellationToken, T> work) where T : class
{
    private Task<T>? _task;
    private CancellationTokenSource _cancel = new();
    private T? _result;

    public bool Started => _task is not null;
    public T? Result => _result;
    /// <summary>True from start until a result or an error.</summary>
    public bool Running => _task is { IsCompleted: false };
    public string? Error => _task is { IsFaulted: true } t ? t.Exception?.GetBaseException().Message ?? "unknown error" : null;

    public void Start()
    {
        if (_task is null)
        {
            CancellationToken token = _cancel.Token;
            _task = Task.Run(() => work(token), token);
        }
    }

    /// <summary>Adopts a finished result. Returns true when it just became available.</summary>
    public bool Poll()
    {
        if (_result is not null || _task is not { IsCompletedSuccessfully: true } t)
        {
            return false;
        }
        _result = t.Result;
        return true;
    }

    public T? Wait()
    {
        Start();
        try
        {
            _task!.Wait();
        }
        catch (AggregateException)
        {
            // reported through Error
        }
        Poll();
        return _result;
    }

    /// <summary>Cancels any run in progress and starts again (e.g. after a delete).</summary>
    public void Restart()
    {
        Cancel();
        _cancel = new CancellationTokenSource();
        _task = null;
        _result = null;
        Start();
    }

    public void Cancel() => _cancel.Cancel();
}
