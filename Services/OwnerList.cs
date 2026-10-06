using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

/// <summary>
/// "The user's characters or corporations changed" — so every list that offers them as a choice
/// can read them again, rather than showing what was there when the tool was first opened.
/// </summary>
/// <remarks>
/// <para>⚠️ Through <see cref="ClientSignals"/>, not a plain event: on a shared PostgreSQL
/// database a character added on one client must reach the dropdowns of every client, and a
/// client hears its own signals, so the one that made the change refreshes by the same path as
/// everyone else. On SQLite the signal never leaves the process and arrives at once.</para>
///
/// <para>Raised for a character or a corporation added or removed. A token renewed or its scopes
/// changed alters nobody's name or presence in a list, so it is not announced.</para>
/// </remarks>
public static class OwnerList
{
    /// <summary>The signal's payload. Short and fixed: it carries no data, only "read them again".</summary>
    public const string ChangedSignal = "owners-changed";

    /// <summary>Raised on the UI thread when characters or corporations were added or removed,
    /// here or on another client of the same database.</summary>
    public static event Action? Changed;

    /// <summary>Tells every client — this one included — that the lists need reading again.</summary>
    public static void Announce()
    {
        var signals = App.Services?.GetService<ClientSignals>();
        if (signals is null) { Raise(); return; }
        _ = Task.Run(async () =>
        {
            try { await signals.PublishAsync(ChangedSignal); }
            // Not sent, so at least this client refreshes: it is the one the user is looking at.
            catch { Raise(); }
        });
    }

    /// <summary>For the signal listener: true, and the lists refreshed, when the signal is this one.</summary>
    public static bool TryApplySignal(string payload)
    {
        if (payload != ChangedSignal) return false;
        Raise();
        return true;
    }

    private static void Raise() => Dispatcher.UIThread.Post(() => Changed?.Invoke());
}
