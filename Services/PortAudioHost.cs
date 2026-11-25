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

    /// <summary>One refresh at a time, from its Refreshing to its Refreshed.</summary>
    /// <remarks>⚠️ Two overlapping refreshes crashed the app (2026-11-12 and 11-14, both as the PC was picked up in the
    /// morning): the device watch and a dead Mumble stream each started one, the first one's Refreshed reopened
    /// Mumble's sound after the second one's Refreshing had closed it, and the device list was read while the second
    /// one had PortAudio stopped. Only taken by <see cref="Refresh"/>, and no caller of it holds a user's lock.</remarks>
    private static readonly object RefreshGate = new();

    private static volatile bool _initialized;
    private static Timer? _watch;
    private static long _lastCount = -1;

    /// <summary>Close every stream now, and open none until <see cref="Refreshed"/>: their handles die with the refresh.
    /// Raised outside any lock of this class, so a handler may take its own.</summary>
    public static event Action? Refreshing;

    /// <summary>The device list is new and its numbers have changed: look devices up again by name and reopen.</summary>
    public static event Action? Refreshed;

    /// <summary>Where a handler's failure or the watch's own is written, set at start-up. The watch runs on a timer
    /// thread, where anything not caught ends the process.</summary>
    public static AppErrorLogger? Errors { get; set; }

    /// <summary>Every device PortAudio lists now, read in one go under its lock so a refresh cannot stop PortAudio
    /// halfway through. Empty when it is not running.</summary>
    public static List<(int Index, DeviceInfo Info)> DeviceInfos()
    {
        var found = new List<(int Index, DeviceInfo Info)>();
        lock (Gate)
        {
            if (!_initialized) return found;
            var count = PortAudio.DeviceCount;
            for (var i = 0; i < count; i++)
                if (TryInfo(i, out var info)) found.Add((i, info));
        }
        return found;
    }

    /// <summary>One device's details, or false when PortAudio no longer lists it (stopped, or the list changed).</summary>
    public static bool TryDeviceInfo(int device, out DeviceInfo info)
    {
        lock (Gate)
        {
            if (_initialized) return TryInfo(device, out info);
            info = default;
            return false;
        }
    }

    /// <remarks>⚠️ PortAudioSharp turns PortAudio's "no such device" (a null pointer) into a NullReferenceException of
    /// its own; there is no other way to ask.</remarks>
    private static bool TryInfo(int device, out DeviceInfo info)
    {
        try { info = PortAudio.GetDeviceInfo(device); return true; }
        catch (NullReferenceException) { info = default; return false; }
    }

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

        lock (RefreshGate)
        {
            // ⚠️ Before taking the gate: a handler takes its user's own lock, and a thread holding that lock may be
            // waiting here to start PortAudio. Handlers also stop their users opening anything until Refreshed, which
            // closes the window between this and the restart below.
            Raise(Refreshing, nameof(Refreshing));
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
            Raise(Refreshed, nameof(Refreshed));
        }
    }

    private static void Watch()
    {
        try
        {
            var now = SystemDeviceCount();
            if (now == Interlocked.Exchange(ref _lastCount, now)) return;
            Refresh();
        }
        catch (Exception e) { Report("device watch", e); }
    }

    private static void Raise(Action? handlers, string what)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action>())
        {
            // Everything: one user's trouble is not another's, and this may be the watch's timer thread.
            try { h(); }
            catch (Exception e) { Report(what, e); }
        }
    }

    private static void Report(string context, Exception e)
    {
        try { Errors?.Log(nameof(PortAudioHost), context, e); } catch { /* the log is no reason to stop */ }
    }

    /// <summary>Playback and recording devices as Windows has them right now, read without PortAudio. Cheap.</summary>
    private static long SystemDeviceCount() => (long)waveOutGetNumDevs() + waveInGetNumDevs();

    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
}
