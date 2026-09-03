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
    /// What went wrong the last time a sound was asked for, or null if the last attempt was
    /// fine. Distinct from <see cref="InitError"/>: LibVLC starting says nothing about whether
    /// a given file can actually be produced and played.
    /// </summary>
    public static string? LastError { get; private set; }

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
        if (string.IsNullOrWhiteSpace(key)) { LastError = "No sound selected."; return null; }

        if (key.Contains(Path.DirectorySeparatorChar) || key.Contains(Path.AltDirectorySeparatorChar))
        {
            if (File.Exists(key)) return key;
            LastError = $"Custom sound file not found: {key}";
            return null;
        }

        var cached = Path.Combine(CacheDir, key + ".wav");
        if (File.Exists(cached)) return cached;

        try
        {
            var uri = new Uri($"avares://EveConsole/Assets/Sounds/{key}.wav");
            if (!AssetLoader.Exists(uri)) { LastError = $"Bundled sound missing: {key}.wav"; return null; }

            Directory.CreateDirectory(CacheDir);
            using var src = AssetLoader.Open(uri);

            // Write via a temp name and move into place, so a second process reading the cache
            // never sees a half-written file.
            var tmp = cached + ".tmp";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            File.Move(tmp, cached, overwrite: true);

            return cached;
        }
        catch (Exception ex)
        {
            LastError = $"Could not unpack {key}.wav into {CacheDir}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Plays a sound. Returns once playback finishes, or immediately if it cannot start.</summary>
    /// <summary>
    /// ⚠️ Every way this can fail used to be silent. There are three of them and they mean
    /// different things: LibVLC never started, the sound file could not be produced, or VLC
    /// tried and failed. "No audio" was the same symptom for all three, and none of them said
    /// anything, so there was nothing to act on.
    /// </summary>
    public async Task PlayAsync(string key, int volume = 100, CancellationToken ct = default)
    {
        if (_vlc is null) { LastError = UnavailableReason; return; }

        var path = ResolvePath(key);
        if (path is null) return;          // ResolvePath has already said why

        LastError = null;

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

            // ⚠️ EncounteredError was wired to the same handler as EndReached, so a failure
            // completed exactly like a successful play — the one event VLC raises to say it
            // could not do the job was being read as "done". Usually a missing plugin or no
            // reachable audio output.
            void OnError(object? s, EventArgs e)
            {
                LastError = $"VLC could not play {Path.GetFileName(path)}. On Linux this is "
                          + "usually a missing plugin package or no audio output "
                          + "(check that other applications play sound).";
                tcs.TrySetResult(false);
            }

            player.EndReached       += OnEnd;
            player.EncounteredError += OnError;

            try
            {
                player.Play(media);
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { player.Stop(); }
            finally
            {
                player.EndReached       -= OnEnd;
                player.EncounteredError -= OnError;
                lock (_playerLock) { if (_player == player) _player = null; }
                player.Dispose();
            }
        }
        catch (Exception ex)
        {
            // A chime that will not play must never take down the alarm that raised it — but it
            // can say what happened on the way past.
            LastError = $"Playback failed: {ex.Message}";
        }
    }

    public void Stop()
    {
        lock (_playerLock)
        {
            try { _player?.Stop(); } catch { }
        }
    }
}
