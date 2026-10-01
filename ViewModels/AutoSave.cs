using System.Reactive.Disposables;

namespace EveConsole.ViewModels;

/// <summary>
/// Saves one thing on a Settings tab as it is changed: at once for a pick, a tick or a list edit,
/// and once typing pauses for a text box.
///
/// <para>Settings save as they are changed, on every tab. A Save button somewhere down a tab was
/// easy to miss, and a change left unsaved was lost when the window closed — with tabs that saved
/// at once beside tabs that did not, nothing said which was which.</para>
///
/// <para>Saves never overlap. A change made while one is running is saved by the next, which reads
/// the values as they are by then, so the last change always wins. <see cref="FlushAsync"/> saves
/// whatever is waiting now: the Settings window calls it when a text box loses focus and when it
/// closes, so nothing typed is lost to the pause.</para>
///
/// <para>⚠️ Called and completed on the UI thread: the save reads the view model and reports on it.
/// A save that touches the database moves that part off the thread itself.</para>
/// </summary>
public sealed class AutoSave
{
    /// <summary>How long typing has to pause before it is saved: long enough not to write every
    /// keystroke, short enough that a save is never far behind.</summary>
    public static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(800);

    private readonly Func<Task>        _save;
    private readonly Action<Exception> _failed;
    private readonly SemaphoreSlim     _gate = new(1, 1);

    private int _changes;      // bumped by every change asked for
    private int _saved;        // the count the last save covered
    private int _suspended;
    private int _flash;
    private CancellationTokenSource? _pause;

    /// <param name="save">Writes the current values. Run on the UI thread.</param>
    /// <param name="failed">Puts a failed save in the tab's status line.</param>
    public AutoSave(Func<Task> save, Action<Exception> failed)
    {
        _save   = save;
        _failed = failed;
    }

    /// <summary>A change has been asked for that no save has covered yet.</summary>
    public bool IsPending => _changes != _saved;

    /// <summary>Inside a <see cref="Suspend"/>: what changes now is being loaded, not edited.</summary>
    public bool IsSuspended => _suspended > 0;

    /// <summary>Something was typed: saved once typing pauses, or sooner by a flush.</summary>
    public void Typed() => Request(TypingPause);

    /// <summary>Something was picked, ticked, added or removed: saved now.</summary>
    public void Changed() => Request(TimeSpan.Zero);

    /// <summary>
    /// Changes made until this is disposed are not saves — a tab loading its values, or taking
    /// values somebody else saved. ⚠️ Synchronous code only: a change from anywhere else that lands
    /// while it is held would be dropped too.
    /// </summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return Disposable.Create(() => _suspended--);
    }

    /// <summary>Saves whatever is waiting now, after any save already running.</summary>
    public Task FlushAsync()
    {
        _pause?.Cancel();
        return RunAsync();
    }

    /// <summary>
    /// Shows <paramref name="text"/> through <paramref name="show"/> for two seconds, then clears it
    /// — unless a later save has put its own there meanwhile, which then gets its full two seconds.
    /// </summary>
    public async void Flash(Action<string> show, string text)
    {
        var mine = ++_flash;
        show(text);
        await Task.Delay(2000);
        if (mine == _flash) show("");
    }

    private void Request(TimeSpan delay)
    {
        if (_suspended > 0) return;
        _changes++;
        _ = RunAfterAsync(delay);
    }

    private async Task RunAfterAsync(TimeSpan delay)
    {
        _pause?.Cancel();
        var pause = _pause = new CancellationTokenSource();

        if (delay == TimeSpan.Zero)
        {
            // ⚠️ Yielded, not run here. A change is reported from inside a setter, and a setter
            // often goes on to set more (a region picked sets the id and the name): saving inline
            // would write the first half of the change.
            await Task.Yield();
        }
        else
        {
            try { await Task.Delay(delay, pause.Token); }
            catch (OperationCanceledException) { return; }   // more typing, or a flush took it
        }

        await RunAsync();
    }

    private async Task RunAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_changes == _saved) return;
            var covering = _changes;
            try   { await _save(); }
            catch (Exception ex) { _failed(ex); }

            // Covered even when it failed: the failure is on the tab, and retrying the same write
            // at every focus change would only repeat it. The next change tries again.
            _saved = covering;
        }
        finally { _gate.Release(); }
    }
}
