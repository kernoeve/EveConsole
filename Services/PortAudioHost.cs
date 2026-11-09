using System.Runtime.InteropServices;
using PortAudioSharp;

namespace EveConsole.Services;

/// <summary>
/// The app's one PortAudio, shared by Mumble voice and speech input, and the one place its device list is read again.
/// </summary>
/// <remarks>
/// <para>⚠️ PortAudio reads the device list once, when it starts, and keeps it. Measured with a USB headset on Windows
/// (2026-11-09, DirectSound): unplugged, its stream stops calling back but still reports itself active, and stopping it
/// takes 3.5 s and then fails. "The default device" still means the headset, and opening it succeeds and plays into
/// nothing. Only stopping PortAudio and starting it again lists what is really there (82 ms), and a headset plugged back
/// in is listed again the same way.</para>
/// <para>Stopping PortAudio closes every stream in the process under its users' feet, so each user closes its own first
/// (<see cref="Refreshing"/>), refuses to open any until <see cref="Refreshed"/>, and then opens them again, looking its
/// devices up by name: the numbers change.</para>
/// <para>On Windows the system's own device count is watched, so a device arriving or leaving refreshes within half a
/// second, and a headset plugged back in is used again. Elsewhere a refresh happens when a stream is found dead
/// (<see cref="Refresh"/>), where the system's own default device usually follows the change by itself.</para>
/// </remarks>
public static class PortAudioHost
{
    private static readonly object Gate = new();
    private static volatile bool _initialized;
    private static Timer? _watch;
    private static long _lastCount = -1;

    /// <summary>Close every stream now, and open none until <see cref="Refreshed"/>: their handles die with the refresh.
    /// Raised outside any lock of this class, so a handler may take its own.</summary>
    public static event Action? Refreshing;

    /// <summary>The device list is new and its numbers have changed: look devices up again by name and reopen.</summary>
    public static event Action? Refreshed;

    /// <summary>Starts PortAudio if it is not running. False when it cannot start (no native library).</summary>
    public static bool EnsureInitialized()
    {
        if (_initialized) return true;
        lock (Gate)
        {
            if (_initialized) return true;
            try { PortAudio.Initialize(); }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or PortAudioException or InvalidOperationException) { return false; }
            _initialized = true;

            if (OperatingSystem.IsWindows() && _watch is null)
            {
                Interlocked.Exchange(ref _lastCount, SystemDeviceCount());
                _watch = new Timer(_ => Watch(), null, 500, 500);
            }
            return true;
        }
    }

    /// <summary>Reads the device list again: every stream is closed for it, and reopened after.</summary>
    public static void Refresh()
    {
        if (!_initialized) return;

        // ⚠️ Before taking the gate: a handler takes its user's own lock, and a thread holding that lock may be waiting
        // here to start PortAudio. Handlers also stop their users opening anything until Refreshed, which closes the
        // window between this and the restart below.
        Raise(Refreshing);
        lock (Gate)
        {
            try
            {
                PortAudio.Terminate();
                PortAudio.Initialize();
            }
            catch (Exception e) when (e is PortAudioException or InvalidOperationException) { _initialized = false; }
            if (OperatingSystem.IsWindows()) Interlocked.Exchange(ref _lastCount, SystemDeviceCount());
        }
        Raise(Refreshed);
    }

    private static void Watch()
    {
        var now = SystemDeviceCount();
        if (now == Interlocked.Exchange(ref _lastCount, now)) return;
        Refresh();
    }

    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action>())
        {
            try { h(); }
            catch (Exception e) when (e is PortAudioException or InvalidOperationException or ObjectDisposedException) { /* one user's trouble is not another's */ }
        }
    }

    /// <summary>Playback and recording devices as Windows has them right now, read without PortAudio. Cheap.</summary>
    private static long SystemDeviceCount() => (long)waveOutGetNumDevs() + waveInGetNumDevs();

    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
}
