using System.Text.Json;
using EveConsole.Agent;

namespace EveConsole.Services;

public enum VoiceChangeReason
{
    /// <summary>Chosen at start or after the voices were edited — nothing is announced.</summary>
    Initial,
    /// <summary>The voice speaking failed and another took over at once.</summary>
    Failover,
    /// <summary>The preferred voice, back and steady, took over again between turns.</summary>
    Return,
}

/// <summary>A change of the voice speaking — and so of the persona's name — and what was said about it.</summary>
/// <param name="Announcement">What the new voice said about the change; empty when nothing was said.</param>
public sealed record VoiceChange(string PreviousName, string CurrentName, VoiceChangeReason Reason, string Announcement);

/// <summary>How a test of one voice from Settings went, in words for the settings tab.</summary>
public sealed record VoiceTestResult(bool Spoke, string Message);

/// <summary>
/// The agent's voices: a list in order of preference, one speaking at a time.
///
/// <para>A voice is part of the persona in a way the model behind it is not, so a change of voice
/// is never silent. The first voice that can speak is used; when it fails another takes over AT
/// ONCE — never a dead voice and silence — introduces itself if it has a different name, and says
/// the sentence that was lost. The preferred voice comes back only between turns, only after
/// passing health checks without a break for <see cref="_preferredUp"/>, and never sooner than
/// <see cref="_switchGap"/> after the last change: a flaky server cannot make the persona flip.</para>
///
/// <para>Every voice goes through one queue, one utterance at a time and in order — the answer
/// is spoken sentence by sentence while it is still being written, and without the queue the
/// sentences talked over each other and cut each other off.</para>
/// </summary>
public sealed class TtsService : IDisposable
{
    // ── The engines ─────────────────────────────────────────────────────────────

    /// <summary>One model however many Kokoro voices are listed — it is 320 MB.</summary>
    private readonly KokoroTtsService _kokoro = new();

    /// <summary>For the settings tab's downloads; the voices themselves each have their own.</summary>
    private readonly PiperTtsService _piperDownloads = new();

    public KokoroTtsService Kokoro => _kokoro;
    public PiperTtsService  Piper  => _piperDownloads;

    public static IReadOnlyList<string> OpenAiVoices     => OpenAiTtsService.Voices;
    public static IReadOnlyList<string> OpenAiModels     => OpenAiTtsService.Models;
    public static bool                  VlcAvailable     => OpenAiTtsService.IsVlcAvailable;
    public static IReadOnlyList<string> ElevenLabsModels => ElevenLabsTtsService.Models;

    /// <summary>A voice from the list, with its engine.</summary>
    private sealed class Voice(VoiceProfile profile,
                               Func<string, CancellationToken, Task> speak,
                               Func<CancellationToken, Task<bool>> isAvailable,
                               Action stop, Action<float> setVolume, Action dispose,
                               Func<TimeSpan?>? lastSynthesis = null)
    {
        public VoiceProfile Profile { get; } = profile;
        public Task SpeakAsync(string text, CancellationToken ct) => speak(text, ct);
        public Task<bool> IsAvailableAsync(CancellationToken ct) => isAvailable(ct);
        public void Stop() => stop();
        public void SetVolume(float v) => setVolume(v);
        public void Dispose() => dispose();

        /// <summary>How long the last utterance took to make before it could play — known only
        /// for the engines that make it apart from playing it.</summary>
        public TimeSpan? LastSynthesis => lastSynthesis?.Invoke();

        /// <summary>What the usage ledger bills it under.</summary>
        public string Model => Profile.Provider switch
        {
            TtsProvider.OpenAi      => Profile.OpenAiModel,
            TtsProvider.ElevenLabs  => Profile.ElevenLabsModel,
            TtsProvider.Kokoro      => Profile.KokoroVoice,
            TtsProvider.Piper       => Profile.PiperVoice,
            TtsProvider.LocalServer => $"{Profile.ServerModel}/{Profile.ServerVoice}".Trim('/'),
            _                       => "",
        };
    }

    private Voice BuildVoice(VoiceProfile p, AgentSettings s)
    {
        switch (p.Provider)
        {
            case TtsProvider.Kokoro:
                // Synchronous inference and playback — off the queue's thread, and stoppable.
                return new Voice(p,
                    (text, ct) => Task.Run(() => _kokoro.Speak(text, p.KokoroVoice, ct), CancellationToken.None),
                    _ => Task.FromResult(_kokoro.IsAvailable),
                    _kokoro.Stop, _kokoro.SetVolume, () => { });

            case TtsProvider.Piper:
            {
                var piper = new PiperTtsService();
                piper.Configure(p.PiperVoice);
                return new Voice(p, piper.SpeakAsync, _ => Task.FromResult(piper.IsAvailable),
                                 piper.Stop, piper.SetVolume, piper.Dispose);
            }

            case TtsProvider.ElevenLabs:
            {
                var eleven = new ElevenLabsTtsService();
                eleven.Configure(s.ElevenLabsApiKey, p.ElevenLabsVoiceId, p.ElevenLabsModel);
                return new Voice(p, eleven.SpeakAsync, eleven.IsAvailableAsync, eleven.Stop, eleven.SetVolume, eleven.Dispose);
            }

            case TtsProvider.OpenAi:
            case TtsProvider.LocalServer:
            {
                var client = new OpenAiTtsService();
                if (p.Provider == TtsProvider.OpenAi)
                    client.Configure(s.OpenAiApiKey, p.OpenAiVoice, p.OpenAiModel, p.OpenAiSpeed);
                else
                    client.Configure(p.ServerUrl, p.ServerApiKey, p.ServerVoice, p.ServerModel, p.ServerSpeed);
                return new Voice(p, client.SpeakAsync, client.IsAvailableAsync, client.Stop, client.SetVolume, client.Dispose,
                                 () => client.LastSynthesis);
            }

            default:
                return new Voice(p,
                    (_, _) => Task.FromException(new InvalidOperationException($"No voice engine for {p.Provider}.")),
                    _ => Task.FromResult(false), () => { }, _ => { }, () => { });
        }
    }

    // ── Settings ────────────────────────────────────────────────────────────────

    private readonly object   _state = new();
    private Voice[]           _voices = [];
    private string            _voicesSignature = "";
    private bool              _speechOn;
    private bool              _announce = true;
    private string            _handoverMessage = "";
    private string            _returnMessage   = "";
    private string            _defaultName     = AgentSettings.DefaultAgentName;
    private TimeSpan          _switchGap   = TimeSpan.FromMinutes(10);
    private TimeSpan          _preferredUp = TimeSpan.FromMinutes(5);

    // ── Who is speaking ─────────────────────────────────────────────────────────

    private int              _active;
    private DateTimeOffset   _lastChange = DateTimeOffset.MinValue;
    /// <summary>When each more-preferred voice was first seen up, without a failed check since.</summary>
    private readonly Dictionary<int, DateTimeOffset> _upSince = [];
    private int?             _returnReady;

    /// <summary>Settles which voice speaks first; the queue waits on it, so an utterance at
    /// start-up never fails over — and announces — before the first choice is made.</summary>
    private Task _selection = Task.CompletedTask;

    /// <summary>Raised on any change of the voice speaking, from a pool thread.</summary>
    public event Action<VoiceChange>? ActiveVoiceChanged;

    /// <summary>The name of the voice speaking — the persona's name while speech is on — or null
    /// when speech is off, and the agent is known by its own name.</summary>
    public string? ActiveName
    {
        get { lock (_state) return _speechOn && _voices.Length > 0 ? NameOf(_active) : null; }
    }

    /// <summary>Speech is on and there is a voice to speak with.</summary>
    public bool IsSpeaking { get { lock (_state) return _speechOn && _voices.Length > 0; } }

    /// <summary>Tests replace the clock; everything else uses the real one.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // ── Runtime state (not persisted except Volume) ────────────────────────────
    private float _volume = 1f;   // 0.0–1.0
    private bool  _muted;

    public float Volume  => _volume;
    public bool  IsMuted => _muted;

    /// <summary>Where speech usage is recorded. Optional — absent means unmeasured, not broken.</summary>
    public Agent.AgentTelemetryService? Telemetry { get; set; }

    /// <summary>
    /// Where a voice that has stopped working is reported: the Error Log, once per distinct
    /// failure per session. The usage ledger records every failed utterance, but nobody reads
    /// that when the symptom is silence — Kokoro failed seventy times in four days unnoticed.
    /// </summary>
    public AppErrorLogger? Errors { get; set; }
    private readonly HashSet<string> _reported = [];

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _watcher;

    public void Configure(AgentSettings s)
    {
        s.NormalizeVoices();
        var profiles  = s.Voices.Select(v => v.Clone()).ToList();
        var signature = JsonSerializer.Serialize(new { profiles, s.OpenAiApiKey, s.ElevenLabsApiKey });

        bool rebuilt;
        lock (_state)
        {
            _speechOn        = s.SpeechOn;
            _announce        = s.AnnounceVoiceChanges;
            _handoverMessage = s.VoiceHandoverMessage;
            _returnMessage   = s.VoiceReturnMessage;
            _defaultName     = string.IsNullOrWhiteSpace(s.AgentName) ? AgentSettings.DefaultAgentName : s.AgentName.Trim();
            _switchGap       = TimeSpan.FromMinutes(Math.Max(0, s.VoiceSwitchGapMinutes));
            _preferredUp     = TimeSpan.FromMinutes(Math.Max(0, s.VoicePreferredUpMinutes));
            _volume          = Math.Clamp(s.TtsVolume, 0f, 1f);

            // ⚠️ The voices are rebuilt only when they changed. Configure runs on every Save in
            // Settings, and a save that touched nothing about the voices must not throw away a
            // session's failover — nor send it back to a voice that has just failed.
            rebuilt = signature != _voicesSignature;
            if (rebuilt)
            {
                foreach (var old in _voices) { old.Stop(); old.Dispose(); }
                _voices          = [.. profiles.Select(p => BuildVoice(p, s))];
                _voicesSignature = signature;
                _active          = 0;
                _upSince.Clear();
                _returnReady     = null;
            }
        }

        _kokoro.Configure(profiles.FirstOrDefault(p => p.Provider == TtsProvider.Kokoro)?.KokoroVoice ?? "af_heart");
        ApplyVolume();
        if (rebuilt) _selection = SelectFirstAsync();
        _watcher ??= Task.Run(() => WatchPreferredAsync(_lifetime.Token));
    }

    /// <summary>
    /// Picks the first voice that can speak — silently: at start the capsuleer simply hears
    /// whoever is speaking and sees their name. Warms the chosen engine.
    /// </summary>
    private async Task SelectFirstAsync()
    {
        Voice[] voices;
        lock (_state) voices = _voices;
        if (voices.Length == 0) return;

        var chosen = 0;
        for (var i = 0; i < voices.Length; i++)
            if (await SafeAvailableAsync(voices[i]))
            {
                chosen = i;
                break;
            }

        string previous, current;
        lock (_state)
        {
            if (!ReferenceEquals(voices, _voices)) return;   // edited again meanwhile
            previous = NameOf(_active);
            _active  = chosen;
            current  = NameOf(chosen);
        }

        if (voices[chosen].Profile.Provider == TtsProvider.Kokoro) _ = _kokoro.LoadAsync();
        ActiveVoiceChanged?.Invoke(new VoiceChange(previous, current, VoiceChangeReason.Initial, ""));
    }

    private string NameOf(int index) =>
        index >= 0 && index < _voices.Length && !string.IsNullOrWhiteSpace(_voices[index].Profile.Name)
            ? _voices[index].Profile.Name.Trim()
            : _defaultName;

    // ── Volume and mute ─────────────────────────────────────────────────────────

    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        ApplyVolume();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (muted) Stop();
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        var effective = _muted ? 0f : _volume;
        Voice[] voices;
        lock (_state) voices = _voices;
        foreach (var v in voices) v.SetVolume(effective);
    }

    // ── Speaking ────────────────────────────────────────────────────────────────

    private readonly object _queueGate = new();
    private Task _speechChain = Task.CompletedTask;

    /// <summary>Bumped by <see cref="Stop"/>: anything queued under an older generation is dropped
    /// rather than spoken, so stopping silences the backlog as well as the sentence playing.</summary>
    private int _generation;
    private CancellationTokenSource _stop = new();

    public void SpeakAsync(string text)
    {
        if (_muted || !IsSpeaking) return;

        // Say system names the way capsuleers do — "C-FD0D" as "C tac F D zero D" — rather than
        // however the engine guesses. Done here, on the way out, so the text shown is unaffected.
        text = EvePronunciation.Expand(text);
        Enqueue(generation => SayAsync(text, generation));
    }

    private void Enqueue(Func<int, Task> work)
    {
        lock (_queueGate)
        {
            var generation = Volatile.Read(ref _generation);
            _speechChain = _speechChain.ContinueWith(async _ =>
            {
                // One failed utterance must not break the chain for every later one.
                try { await work(generation); }
                catch (Exception ex) { Errors?.Log("TtsService", "Speech queue", ex); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private bool Stale(int generation) => Volatile.Read(ref _generation) != generation || _muted;

    private async Task SayAsync(string text, int generation)
    {
        await _selection;
        if (Stale(generation)) return;

        Voice voice; int index;
        lock (_state)
        {
            if (!_speechOn || _voices.Length == 0) return;
            index = Math.Clamp(_active, 0, _voices.Length - 1);
            voice = _voices[index];
        }

        if (await TrySpeakAsync(voice, text, generation)) return;
        await FailOverAsync(index, text, generation);
    }

    /// <summary>Speaks with one voice and records it. False when it could not speak; true when it
    /// spoke — or was stopped, which is not a failure.</summary>
    private async Task<bool> TrySpeakAsync(Voice voice, string text, int generation) =>
        await SpeakOrWhyNotAsync(voice, text, generation) is null;

    /// <summary>Speaks with one voice and records it. Null when it spoke, or was stopped;
    /// otherwise why it could not.</summary>
    private async Task<string?> SpeakOrWhyNotAsync(Voice voice, string text, int generation)
    {
        if (Stale(generation)) return null;
        var started = Environment.TickCount64;
        var token   = _stop.Token;
        try
        {
            await voice.SpeakAsync(text, token);
            if (!token.IsCancellationRequested) Record(voice, text, started);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            var why = ex.GetBaseException().Message;
            Record(voice, text, started, why);
            return why;
        }
    }

    /// <summary>
    /// The voice speaking has failed: hand over to the next that can speak, in order of
    /// preference, and have it say what was lost. Immediate — the gap and the steadiness rules
    /// are for coming BACK, never for leaving a voice that does not work.
    /// </summary>
    private async Task FailOverAsync(int failed, string text, int generation)
    {
        Voice[] voices;
        lock (_state) voices = _voices;

        for (var i = 0; i < voices.Length; i++)
        {
            if (i == failed || Stale(generation)) continue;
            if (!await SafeAvailableAsync(voices[i])) continue;

            string previous, current;
            lock (_state)
            {
                if (!ReferenceEquals(voices, _voices)) return;   // edited meanwhile
                previous    = NameOf(_active);
                _active     = i;
                _lastChange = Clock();
                _upSince.Clear();
                _returnReady = null;
                current     = NameOf(i);
            }

            var announcement = await AnnounceAsync(voices[i], _handoverMessage, previous, current, generation);
            ActiveVoiceChanged?.Invoke(new VoiceChange(previous, current, VoiceChangeReason.Failover, announcement));

            // The sentence that was lost. If this voice fails too, the next in line is tried.
            if (await TrySpeakAsync(voices[i], text, generation)) return;
            failed = i;
        }
    }

    /// <summary>
    /// Says the change out loud with the voice taking over — only when announcements are on and
    /// the persona's name actually changes: "Eden had to step away, I'm Eden" says nothing.
    /// </summary>
    private async Task<string> AnnounceAsync(Voice voice, string template, string previous, string current, int generation)
    {
        bool announce;
        lock (_state) announce = _announce;
        if (!announce || string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return "";

        var message = FormatMessage(template, previous, current);
        if (message.Length > 0) await TrySpeakAsync(voice, EvePronunciation.Expand(message), generation);
        return message;
    }

    internal static string FormatMessage(string template, string previous, string current) =>
        (template ?? "").Replace("{previous}", previous, StringComparison.OrdinalIgnoreCase)
                        .Replace("{current}", current, StringComparison.OrdinalIgnoreCase)
                        .Trim();

    // ── Coming back ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Watches the voices preferred over the one speaking, every half minute while one is.
    /// ⚠️ Never a billed request: a cloud voice is checked on its free account endpoint, a server
    /// of our own by making one word on its own GPU. Reachability alone let a server that could
    /// no longer speak come back — see OpenAiTtsService.IsAvailableAsync.
    /// </summary>
    private async Task WatchPreferredAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
            try { await CheckPreferredAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Errors?.Log("TtsService", "Voice watcher", ex); }
        }
    }

    /// <summary>One round of the watch: which preferred voices are up, since when, and whether one
    /// has been up long enough, and long enough after the last change, to come back.</summary>
    internal async Task CheckPreferredAsync(CancellationToken ct = default)
    {
        Voice[] voices; int active;
        lock (_state)
        {
            voices = _voices;
            active = _active;
            if (!_speechOn || active <= 0) { _upSince.Clear(); _returnReady = null; return; }
        }

        var up = new bool[active];
        for (var i = 0; i < active; i++) up[i] = await SafeAvailableAsync(voices[i], ct);

        lock (_state)
        {
            if (!ReferenceEquals(voices, _voices) || active != _active) return;   // moved on meanwhile
            var now = Clock();
            _returnReady = null;
            for (var i = 0; i < active; i++)
            {
                if (!up[i]) { _upSince.Remove(i); continue; }   // any failed check starts the clock again
                if (!_upSince.TryGetValue(i, out var since)) _upSince[i] = since = now;
                if (_returnReady is null && now - since >= _preferredUp && now - _lastChange >= _switchGap)
                    _returnReady = i;
            }
        }
    }

    /// <summary>
    /// Between turns: if a preferred voice is back and has stayed up, switch to it and let it say
    /// so. Called by the agent panel before each turn, so the switch never lands mid-answer and
    /// the model is told its own name before it writes a word. Returns the change, or null.
    /// </summary>
    public VoiceChange? ApplyPendingReturn()
    {
        Voice voice; string previous, current;
        lock (_state)
        {
            if (_returnReady is not int target || target >= _active || !_speechOn) return null;
            if (Clock() - _lastChange < _switchGap) return null;

            previous     = NameOf(_active);
            _active      = target;
            _lastChange  = Clock();
            _returnReady = null;
            _upSince.Clear();
            current      = NameOf(target);
            voice        = _voices[target];
        }

        bool announce;
        lock (_state) announce = _announce;
        var message = announce && !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)
            ? FormatMessage(_returnMessage, previous, current)
            : "";
        if (message.Length > 0)
        {
            var spoken = EvePronunciation.Expand(message);
            Enqueue(generation => TrySpeakAsync(voice, spoken, generation));
        }

        var change = new VoiceChange(previous, current, VoiceChangeReason.Return, message);
        ActiveVoiceChanged?.Invoke(change);
        return change;
    }

    private static async Task<bool> SafeAvailableAsync(Voice voice, CancellationToken ct = default)
    {
        try { return await voice.IsAvailableAsync(ct); }
        catch { return false; }
    }

    // ── Trying a voice from Settings ───────────────────────────────────────────

    /// <summary>
    /// Speaks with one profile as it stands on the settings tab — not through the list, so a test
    /// neither fails over nor disturbs the voice a session is using. Queued like any utterance,
    /// and completes when it has been heard or has failed.
    ///
    /// <para>⚠️ The result is for the settings tab to SHOW. A failed test used to go only to the
    /// Error Log and the usage ledger, so on the tab the button simply did nothing — a voice
    /// file named without its ".wav" looked exactly like a server that was not there.</para>
    /// </summary>
    public Task<VoiceTestResult> TestVoiceAsync(VoiceProfile profile, AgentSettings keys, string text)
    {
        if (_muted)
            return Task.FromResult(new VoiceTestResult(false, "Speech is muted — unmute it in the agent panel to hear the test."));

        var voice  = BuildVoice(profile.Clone(), keys);
        voice.SetVolume(_volume);
        var spoken = EvePronunciation.Expand(text);
        var result = new TaskCompletionSource<VoiceTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async generation =>
        {
            try
            {
                var why = await SpeakOrWhyNotAsync(voice, spoken, generation);
                result.TrySetResult(
                    why is not null   ? new VoiceTestResult(false, why)
                  : Stale(generation) ? new VoiceTestResult(false, "Stopped before it finished.")
                  : voice.LastSynthesis is { } made
                      ? new VoiceTestResult(true, $"Spoke. It took {made.TotalSeconds:0.0} s to make before it could play — about the wait before each sentence of an answer.")
                      : new VoiceTestResult(true, "Spoke."));
            }
            catch (Exception ex) { result.TrySetResult(new VoiceTestResult(false, ex.GetBaseException().Message)); }
            finally { voice.Dispose(); }
        });
        return result.Task;
    }

    // ── Stopping ─────────────────────────────────────────────────────────────────

    public void Stop()
    {
        // Drops anything still queued. Without this, stopping silences the current utterance and
        // the backlog simply carries on — into the next question's answer.
        Interlocked.Increment(ref _generation);
        var old = Interlocked.Exchange(ref _stop, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();

        Voice[] voices;
        lock (_state) voices = _voices;
        foreach (var v in voices) v.Stop();
        _kokoro.Stop();
    }

    // ── What it cost ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Records an utterance in the usage ledger when it is DONE, against the voice that spoke it.
    /// Counted after the EVE pronunciation pass, because a provider bills what it is SENT. Local
    /// voices are logged too, with IsLocal set: they cost nothing, but the volume and latency
    /// still answer "what would this cost on a paid voice".
    /// </summary>
    private void Record(Voice voice, string billedText, long startedTicks, string error = "")
    {
        var provider = voice.Profile.Provider;
        Telemetry?.ServiceCall(
            kind:       "tts",
            provider:   provider.ToString(),
            model:      voice.Model,
            isLocal:    provider is TtsProvider.Kokoro or TtsProvider.Piper or TtsProvider.LocalServer,
            unitKind:   "characters",
            units:      billedText.Length,
            durationMs: (int)(Environment.TickCount64 - startedTicks),
            error:      error);

        if (error.Length > 0 && Errors is { } errors)
        {
            bool first;
            lock (_reported) first = _reported.Add($"{provider}|{error}");
            if (first) errors.Log("TtsService", $"{provider} voice ({voice.Model})", $"Speech failed: {error}");
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        Stop();
        Voice[] voices;
        lock (_state) voices = _voices;
        foreach (var v in voices) v.Dispose();
        _kokoro.Dispose();
        _piperDownloads.Dispose();
    }
}
