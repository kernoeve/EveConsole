using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace EveConsole.Services;

/// <summary>
/// Loads libvlc, once, for everything that plays sound through it: the alarm chimes and the
/// ElevenLabs, OpenAI and Piper voices.
///
/// <para>Where it comes from differs by platform. Windows: the VideoLAN.LibVLC.Windows package's
/// copy, which LibVLCSharp finds by itself. Linux: the system's VLC. macOS: the
/// VideoLAN.LibVLC.Mac package's copy for this Mac's architecture, which the package lays out as
/// <c>libvlc/osx-arm64/lib</c> and <c>libvlc/osx-x64/lib</c> beside the app.</para>
///
/// <para>⚠️ On macOS the folder is named outright rather than left to LibVLCSharp's own search,
/// and VLC is told where its plugins are. The package carries Apple Silicon natives alongside
/// Intel ones, and a search that assumed one architecture would load nothing, or the wrong one;
/// without its plugins VLC starts but has no decoder and no CoreAudio output, and every sound
/// fails as "VLC could not play it".</para>
/// </summary>
public static class VlcRuntime
{
    private static readonly Lazy<ExceptionDispatchInfo?> _load = new(Load);

    /// <summary>Loads libvlc the first time; afterwards does nothing, or rethrows why it could not.</summary>
    public static void Initialize() => _load.Value?.Throw();

    /// <summary>
    /// The one LibVLC for everything that plays sound: alarm chimes and every voice. Players are
    /// made from it as needed; one instance serves any number.
    /// <para>⚠️ One, made once, and started off the UI thread by <see cref="WarmUp"/>. Each sound
    /// service used to make its own in a static constructor, and making one loads and scans all of
    /// VLC's plugins — about eleven seconds each, on the UI thread, while the app built its tools at
    /// startup. Fixing one only moved the wait to the next.</para>
    /// </summary>
    public static LibVLC? Shared => _shared.Value.Vlc;

    /// <summary>Why the shared LibVLC would not start, or null when it did.</summary>
    public static string? SharedError => _shared.Value.Error;

    private static readonly Lazy<(LibVLC? Vlc, string? Error)> _shared = new(() =>
    {
        try
        {
            Initialize();
            return (new LibVLC(enableDebugLogs: false), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Starts making the shared LibVLC in the background, so it's ready before anything plays.</summary>
    public static void WarmUp() => _ = Task.Run(() => _shared.Value);

    private static ExceptionDispatchInfo? Load()
    {
        try
        {
            if (OperatingSystem.IsMacOS() && MacLibraryDir() is { } dir)
            {
                Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(dir, "vlc", "plugins"));
                Core.Initialize(dir);
            }
            else
            {
                Core.Initialize();
            }
            return null;
        }
        catch (Exception ex)
        {
            return ExceptionDispatchInfo.Capture(ex);
        }
    }

    /// <summary>The packaged libvlc for this Mac's architecture, or null when it is not there.</summary>
    private static string? MacLibraryDir()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        var dir  = Path.Combine(AppContext.BaseDirectory, "libvlc", arch, "lib");
        return File.Exists(Path.Combine(dir, "libvlc.dylib")) ? dir : null;
    }
}
