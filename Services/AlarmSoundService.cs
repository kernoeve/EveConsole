using Avalonia.Platform;
using LibVLCSharp.Shared;

namespace EveConsole.Services;

/// <summary>A selectable alarm sound: one of the bundled chimes, or a file the user supplied.</summary>
public sealed record AlarmSound(string Key, string DisplayName, bool IsCustom)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Plays alarm chimes. Bundled sounds are Avalonia resources compiled into the assembly; LibVLC
/// wants a path rather than a stream, so each is unpacked once into the app data folder and
/// played from there afterwards.
/// </summary>
public sealed class AlarmSoundService
{
    private static readonly LibVLC? _vlc;

    static AlarmSoundService()
    {
        try
        {
            Core.Initialize();
            _vlc = new LibVLC(enableDebugLogs: false);
        }
        catch (Exception ex)
        {
            _vlc = null;
            InitError = ex.Message;
        }
    }

    public static bool IsAvailable => _vlc is not null;

    /// <summary>Why LibVLC would not start, or null when it did.</summary>
    public static string? InitError { get; }

    /// <summary>
    /// What to tell the user when there is no audio, in terms they can act on.
    ///
    /// <para>⚠️ The failure used to be swallowed whole — the exception discarded and
    /// IsAvailable read by nobody — so a machine with no audio stack simply never made a
    /// sound and never said why. On Windows the native binaries come from a nuget package and
    /// this effectively cannot happen; on Linux LibVLCSharp loads the system libvlc, so the
    /// usual cause is that VLC is not installed. Naming the fix is the difference between a
    /// missing package and a broken app.</para>
    /// </summary>
    public static string UnavailableReason =>
        IsAvailable ? ""
        : OperatingSystem.IsLinux()
            ? $"Audio unavailable: libvlc could not be loaded. Install VLC and its plugins "
              + $"(the \"vlc\" package on Arch and Fedora; \"vlc\" or \"libvlc-dev\" on Debian and "
              + $"Ubuntu), then restart. [{InitError}]"
            : $"Audio unavailable: {InitError}";

    /// <summary>
    /// The sounds shipped with the app, in picker order: the quiet ones first, then the ones
    /// meant to be impossible to ignore.
    /// </summary>
    private static readonly (string Key, string Name)[] Bundled =
    [
        ("chime-soft",        "Chime — soft"),
        ("chime-triad",       "Chime — triad"),
        ("ping-glass",        "Ping — glass"),
        ("bell-brass",        "Bell — brass"),
        ("bell-deep",         "Bell — deep"),
        ("gong-low",          "Gong — low"),
        ("alert-double",      "Alert — double"),
        ("alarm-urgent",      "Alarm — urgent"),
        ("two-tone-alert",    "Warning — two-tone"),
        ("klaxon-industrial", "Warning — klaxon"),
        ("buzzer-harsh",      "Warning — buzzer"),
        ("siren-sweep",       "Warning — siren"),
        ("horn-low",          "Warning — horn"),
    ];

    /// <summary>
    /// Drop a .wav or .mp3 here and it shows up in the picker on the next restart, without a
    /// rebuild. (Files added to the repo's Assets\Sounds are compiled in and need one.)
    /// </summary>
    public static string CustomSoundDir => Path.Combine(AppConfig.AppDataDir, "Sounds");

    private static string CacheDir => Path.Combine(AppConfig.AppDataDir, "SoundCache");

    private readonly object _playerLock = new();
    private MediaPlayer?    _player;

    /// <summary>Bundled chimes first, then anything the user has dropped in <see cref="CustomSoundDir"/>.</summary>
    public IReadOnlyList<AlarmSound> List()
    {
        var list = Bundled.Select(b => new AlarmSound(b.Key, b.Name, false)).ToList();

        try
        {
            if (Directory.Exists(CustomSoundDir))
            {
                foreach (var f in Directory.EnumerateFiles(CustomSoundDir)
                             .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f))
                {
                    list.Add(new AlarmSound(f, Path.GetFileNameWithoutExtension(f), true));
                }
            }
        }
        catch { /* an unreadable folder just means no custom sounds */ }

        return list;
    }

    public static string DefaultKey => Bundled[0].Key;

    public static readonly string[] SupportedExtensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a"];

    /// <summary>
    /// Copies a file the user picked into the custom sounds folder and returns it, so it is
    /// available on every later run rather than depending on wherever they browsed to. A name
    /// collision gets a numeric suffix instead of overwriting.
    /// </summary>
    public AlarmSound? AddCustomSound(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

        var ext = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;

        Directory.CreateDirectory(CustomSoundDir);

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var target   = Path.Combine(CustomSoundDir, baseName + ext);
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(CustomSoundDir, $"{baseName} ({i}){ext}");

        File.Copy(sourcePath, target);
        return new AlarmSound(target, Path.GetFileNameWithoutExtension(target), true);
    }

    /// <summary>
    /// Resolves a stored key to a playable file. A key containing a directory separator is
    /// treated as a path the user chose; anything else is a bundled chime.
    /// </summary>
    private static string? ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (key.Contains(Path.DirectorySeparatorChar) || key.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(key) ? key : null;

        var cached = Path.Combine(CacheDir, key + ".wav");
        if (File.Exists(cached)) return cached;

        try
        {
            var uri = new Uri($"avares://EveConsole/Assets/Sounds/{key}.wav");
            if (!AssetLoader.Exists(uri)) return null;

            Directory.CreateDirectory(CacheDir);
            using var src = AssetLoader.Open(uri);

            // Write via a temp name and move into place, so a second process reading the cache
            // never sees a half-written file.
            var tmp = cached + ".tmp";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            File.Move(tmp, cached, overwrite: true);

            return cached;
        }
        catch { return null; }
    }

    /// <summary>Plays a sound. Returns once playback finishes, or immediately if it cannot start.</summary>
    public async Task PlayAsync(string key, int volume = 100, CancellationToken ct = default)
    {
        if (_vlc is null) return;

        var path = ResolvePath(key);
        if (path is null) return;

        try
        {
            using var media = new Media(_vlc, new Uri(path));

            MediaPlayer player;
            lock (_playerLock)
            {
                _player?.Dispose();
                _player = player = new MediaPlayer(_vlc) { Volume = Math.Clamp(volume, 0, 100) };
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            void OnEnd(object? s, EventArgs e) => tcs.TrySetResult(true);
            player.EndReached       += OnEnd;
            player.EncounteredError += OnEnd;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { player.Stop(); }
            finally
            {
                player.EndReached       -= OnEnd;
                player.EncounteredError -= OnEnd;
                lock (_playerLock) { if (_player == player) _player = null; }
                player.Dispose();
            }
        }
        catch { /* a chime that will not play must never take down the alarm that raised it */ }
    }

    public void Stop()
    {
        lock (_playerLock)
        {
            try { _player?.Stop(); } catch { }
        }
    }
}
