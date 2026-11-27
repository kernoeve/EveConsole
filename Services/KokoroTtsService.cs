using KokoroSharp;
using System.Text.RegularExpressions;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// Local TTS via Kokoro 82M (ONNX inference, fully offline after initial download).
/// The ONNX model (~320 MB full precision) is downloaded once, on the first call to
/// LoadAsync(), into the app's data folder. Voice embeddings and the espeak phonemiser ship
/// with the app. No executable code is downloaded — only the neural network weight file.
///
/// <para>⚠️ Everything is found by absolute path. Left to itself KokoroSharp reads "voices" and
/// "kokoro.onnx" relative to the WORKING directory, so what it found depended on how the app
/// was started; and in an installed app the working directory is the version folder, which the
/// next update replaces — taking a downloaded model with it.</para>
/// </summary>
public sealed class KokoroTtsService : IDisposable
{
    /// <summary>Beside the executable: the release carries them (see the csproj).</summary>
    private static string VoicesDir => Path.Combine(AppContext.BaseDirectory, "voices");

    /// <summary>In the data folder, which survives updates, as the Whisper models do.</summary>
    private static string ModelPath => Path.Combine(AppConfig.AppDataDir, "kokoro-models", "kokoro.onnx");

    /// <summary>The full-precision model, from where KokoroSharp's own download takes it.</summary>
    private const string ModelUrl = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro.onnx";
    // ── English voices bundled with KokoroSharp (via NuGet content) ─────────
    // Each voice's name is its own and stays as it is; the kind of voice it is, is translated.
    // Chosen by Id on the settings tab, never by these words.
    public static readonly IReadOnlyList<(string Id, string Label)> Voices =
    [
        // American Female
        ("af_heart",   AmericanFemaleDefault("Heart")),
        ("af_sky",     AmericanFemale("Sky")),
        ("af_bella",   AmericanFemale("Bella")),
        ("af_sarah",   AmericanFemale("Sarah")),
        ("af_nicole",  AmericanFemale("Nicole")),
        ("af_alloy",   AmericanFemale("Alloy")),
        ("af_nova",    AmericanFemale("Nova")),
        ("af_jessica", AmericanFemale("Jessica")),
        ("af_kore",    AmericanFemale("Kore")),
        ("af_aoede",   AmericanFemale("Aoede")),
        ("af_river",   AmericanFemale("River")),
        // American Male
        ("am_adam",    AmericanMale("Adam")),
        ("am_michael", AmericanMale("Michael")),
        ("am_echo",    AmericanMale("Echo")),
        ("am_eric",    AmericanMale("Eric")),
        ("am_liam",    AmericanMale("Liam")),
        ("am_onyx",    AmericanMale("Onyx")),
        ("am_puck",    AmericanMale("Puck")),
        // British Female
        ("bf_emma",     BritishFemale("Emma")),
        ("bf_isabella", BritishFemale("Isabella")),
        ("bf_alice",    BritishFemale("Alice")),
        ("bf_lily",     BritishFemale("Lily")),
        // British Male
        ("bm_george",  BritishMale("George")),
        ("bm_lewis",   BritishMale("Lewis")),
        ("bm_daniel",  BritishMale("Daniel")),
        ("bm_fable",   BritishMale("Fable")),
    ];

    private static string AmericanFemaleDefault(string name) => string.Format(SettingsText.KokoroVoiceAmericanFemaleDefault, name);
    private static string AmericanFemale(string name) => string.Format(SettingsText.KokoroVoiceAmericanFemale, name);
    private static string AmericanMale(string name)   => string.Format(SettingsText.KokoroVoiceAmericanMale, name);
    private static string BritishFemale(string name)  => string.Format(SettingsText.KokoroVoiceBritishFemale, name);
    private static string BritishMale(string name)    => string.Format(SettingsText.KokoroVoiceBritishMale, name);

    /// <summary>The model, making sound only: it is played through the app's own LibVLC, as every other voice is.</summary>
    /// <remarks>⚠️ Not KokoroSharp's KokoroTTS, which plays through its own NAudio player on a thread of its own and
    /// ends the process when that player fails: with no sound device to open (HDMI audio while the monitor sleeps, a
    /// headset switched off) its Init throws inside an <c>async void</c>, and so does a device pulled mid-sentence.
    /// Night alarms are spoken exactly when the monitor is asleep. KokoroTTS also opens that player as it loads.</remarks>
    private KokoroSharp.Utilities.KokoroWavSynthesizer? _tts;
    private string     _voiceId = "af_heart";
    private float      _volume  = 1f;

    private static LibVLCSharp.Shared.LibVLC? Vlc => VlcRuntime.Shared;
    private LibVLCSharp.Shared.MediaPlayer? _player;
    private readonly object _playerLock = new();

    /// <summary>0.0–1.0. Applied to each utterance as it starts, and to the one playing now.</summary>
    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        lock (_playerLock) { try { if (_player is not null) _player.Volume = VlcVolume; } catch { /* finishing */ } }
    }

    private int VlcVolume => (int)Math.Round(_volume * 100);

    public bool IsReady => _tts is not null;

    public void Configure(string voiceId)
    {
        _voiceId = string.IsNullOrEmpty(voiceId) ? "af_heart" : voiceId;
    }

    // Load (and download if necessary) the Kokoro ONNX model — ~320 MB the first time.
    private Task? _load;

    /// <summary>
    /// Loads the voices and the model once; later calls return the same task, so an utterance
    /// that arrives while the engine is still loading has something to wait on. A failed load is
    /// retried on the next call rather than remembered.
    /// </summary>
    public Task LoadAsync()
    {
        if (_tts is not null) return Task.CompletedTask;
        // Locked: two utterances arriving together each started a load, a second 320 MB model.
        lock (_loadLock)
        {
            if (_load is { IsFaulted: true } or { IsCanceled: true }) _load = null;
            return _load ??= Task.Run(async () =>
            {
                if (KokoroVoiceManager.Voices.Count == 0)
                {
                    if (!Directory.Exists(VoicesDir))
                        throw new DirectoryNotFoundException(string.Format(SettingsText.KokoroVoicesMissing, VoicesDir));
                    KokoroVoiceManager.LoadVoicesFromPath(VoicesDir);
                }
                _tts = new KokoroSharp.Utilities.KokoroWavSynthesizer(await EnsureModelAsync(), null);
            });
        }
    }

    private readonly object _loadLock = new();

    /// <summary>The model's path, fetching it first if this machine has never had it.</summary>
    private static async Task<string> EnsureModelAsync()
    {
        var path = ModelPath;
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Where KokoroSharp's own download put it: the working directory — beside the executable
        // for an installed app, and bin\ for a development run. Moved rather than fetched again.
        foreach (var old in new[] { Path.Combine(AppContext.BaseDirectory, "kokoro.onnx"),
                                    Path.Combine(Environment.CurrentDirectory, "kokoro.onnx") }.Distinct())
        {
            if (!File.Exists(old)) continue;
            try { File.Move(old, path); return path; }
            catch (IOException) { /* in use, or another volume that refused: download instead */ }
        }

        // Written under a temporary name and renamed when complete, so a download cut short is
        // never mistaken for the model on the next start.
        var temp = path + ".download";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(temp);
            await source.CopyToAsync(target);
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }


    /// <summary>
    /// Speaks one utterance and does not return until it has finished playing.
    ///
    /// <para>⚠️ Blocking on purpose, and this is the whole fix for speech being skipped.
    /// <c>SpeakFast</c> hands back a handle immediately — it SUBMITS the work, it does not play
    /// it — and a later call arriving while the previous one is still speaking cancels it. That
    /// did not matter while the entire answer was spoken in one go. It matters completely now that
    /// sentences are handed over as they stream, because each new one cut off its predecessor and
    /// the listener lost whole passages.</para>
    ///
    /// <para>⚠️ Serialising the CALLS is not enough, which is why the first attempt at this did
    /// not work: they return in microseconds, so every sentence was submitted almost at once and
    /// each cancelled the one before. What has to be serialised is the PLAYBACK, and the only
    /// thing that knows when that ends is the handle.</para>
    ///
    /// <para>Callers arrive through TtsService's queue, which already runs this on a pool thread,
    /// so blocking holds up nothing but the next utterance — which is the point.</para>
    /// </summary>
    /// <summary>Can speak, or can once it has loaded: the voices are here and the last load did
    /// not fail. A model still to be downloaded counts as available — it is fetched on first use.</summary>
    public bool IsAvailable => Directory.Exists(VoicesDir) && _load is not { IsFaulted: true };

    /// <param name="voiceId">Which voice — several Kokoro voices share the one loaded model.
    /// Null: the configured one.</param>
    public void Speak(string text, string? voiceId = null, CancellationToken ct = default)
    {
        using var stopOnCancel = ct.Register(Stop);
        if (ct.IsCancellationRequested) return;

        // ⚠️ An utterance that arrives while the model is still loading WAITS for it rather than
        // being dropped. The model takes seconds to load at startup, and an alarm that fired in
        // the first minute — a store order transition caught by the startup polls — was written
        // as an alert and never spoken. Bounded, so a load that never finishes cannot hold the
        // speech queue for ever; and on the queue's pool thread, so nothing else waits with it.
        if (_tts is null)
        {
            try { LoadAsync().Wait(TimeSpan.FromSeconds(120)); }
            catch { /* the load's own failure, reported below */ }

            // ⚠️ Thrown, not returned: TtsService records a throw as a failed utterance. A plain
            // return was recorded as speech that worked, and the only sign of a voice that could
            // not load was silence.
            if (_tts is null)
                throw new InvalidOperationException(
                    _load?.Exception?.GetBaseException().Message is { } why
                        ? string.Format(SettingsText.KokoroCouldNotLoad, why)
                        : SettingsText.KokoroStillLoading);
        }
        var stripped = StripMarkdown(text);

        if (string.IsNullOrWhiteSpace(stripped)) return;

        var vlc = Vlc ?? throw new InvalidOperationException(SettingsText.PiperNoVlc);
        var voice = KokoroVoiceManager.GetVoice(string.IsNullOrEmpty(voiceId) ? _voiceId : voiceId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        // ⚠️ A stop ends the wait at once, also while the sentence is still being made: the first moment of a
        // sentence is exactly when an interruption lands. The sound being made then is thrown away when it is done.
        // A ceiling as well, so a synthesis that never finishes cannot hold the speech queue for ever.
        byte[] pcm;
        try
        {
            var making = _tts.SynthesizeAsync(stripped, voice);
            if (!making.Wait(TimeSpan.FromMinutes(2), linked.Token))
                throw new TimeoutException(SettingsText.PiperPlaybackFailed);
            pcm = making.Result;
        }
        catch (OperationCanceledException) { return; }

        if (pcm.Length == 0 || linked.IsCancellationRequested) return;

        var temp = Path.Combine(Path.GetTempPath(), $"kokoro_{Guid.NewGuid():N}.wav");
        try
        {
            _tts.SaveAudioToFile(pcm, temp);
            try { PlayAsync(vlc, temp, linked.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* stopped */ }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>Plays one file and returns when it has ended, been stopped or failed.</summary>
    /// <remarks>⚠️ Only this call frees the player it made; anything else only stops it (see
    /// OpenAiTtsService.PlayAsync): two threads freeing and using one libvlc player end the process.</remarks>
    private async Task PlayAsync(LibVLCSharp.Shared.LibVLC vlc, string path, CancellationToken ct)
    {
        using var media = new LibVLCSharp.Shared.Media(vlc, new Uri(path));

        LibVLCSharp.Shared.MediaPlayer player;
        lock (_playerLock)
        {
            try { _player?.Stop(); } catch { }
            _player = player = new LibVLCSharp.Shared.MediaPlayer(vlc) { Volume = VlcVolume };
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        void OnEnd(object? s, EventArgs e) => tcs.TrySetResult(true);
        void OnError(object? s, EventArgs e) => tcs.TrySetException(new InvalidOperationException(SettingsText.PiperPlaybackFailed));
        player.EndReached       += OnEnd;
        player.Stopped          += OnEnd;   // a stop raises only this
        player.EncounteredError += OnError;

        try
        {
            player.Play(media);
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { player.Stop(); throw; }
        finally
        {
            player.EndReached       -= OnEnd;
            player.Stopped          -= OnEnd;
            player.EncounteredError -= OnError;
            lock (_playerLock) { if (_player == player) _player = null; }
            player.Dispose();
        }
    }

    private CancellationTokenSource _stop = new();

    /// <summary>
    /// Stops whatever is currently being spoken, or being made to be spoken.
    ///
    /// <para>⚠️ This was once empty, with a comment saying there was no way to stop, so asking the agent to stop
    /// talking did nothing, and the previous answer carried on over the next question.</para>
    /// </summary>
    public void Stop()
    {
        // Cancelled, not disposed: an utterance just starting may still be reading the token.
        Interlocked.Exchange(ref _stop, new CancellationTokenSource()).Cancel();
        lock (_playerLock) { try { _player?.Stop(); } catch { } }
    }

    /// <remarks>Stops only, and leaves the model: a sentence still being made would be using it, and the model
    /// lives as long as the app does.</remarks>
    public void Dispose() => Stop();

    private static string StripMarkdown(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = Regex.Replace(text, @"\*([^*]+)\*", "$1");
        text = Regex.Replace(text, @"__([^_]+)__", "$1");
        text = Regex.Replace(text, @"_([^_]+)_", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[>*\-+]\s", "", RegexOptions.Multiline);
        return text.Trim();
    }
}
