using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using EveConsole.Agent;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

public sealed class AgentPanelViewModel : ReactiveObject
{
    private static readonly string HistoryPath = Path.Combine(
        AppConfig.AppDataDir, "aura-history.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        Converters    = { new JsonStringEnumConverter() },
    };

    private readonly AgentService        _service;
    private readonly TtsService?         _tts;
    private readonly SpeechInputService? _speech;
    private readonly GlobalHotkeyService? _hotkey;
    private CancellationTokenSource _cts = new();

    /// <summary>
    /// Groups this panel's turns in the telemetry so a thread can be read back in order.
    ///
    /// <para>Per panel instance rather than persisted: the point is to tell one sitting's turns
    /// from another's when reading back why an answer was poor, and a conversation that survives a
    /// restart is a different conversation for that purpose.</para>
    /// </summary>
    private string _conversationId = Guid.NewGuid().ToString("N");

    // Parallel lists — Messages drives the UI, _history drives the API context.
    public  ObservableCollection<AgentMessage> Messages { get; } = [];
    private readonly List<AgentMessage>        _history = [];

    // Background summarization — started after each assistant response when threshold is crossed.
    private Task? _summarizationTask;

    private bool _isAgentEnabled;
    public bool IsAgentEnabled
    {
        get => _isAgentEnabled;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isAgentEnabled, value);
            this.RaisePropertyChanged(nameof(IsPanelVisible));
            // If agent is disabled while panel is open, close it.
            if (!value) _isOpen = false;
        }
    }

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            this.RaiseAndSetIfChanged(ref _isOpen, value);
            this.RaisePropertyChanged(nameof(IsPanelVisible));
            _service.Settings.PanelOpen = value;
            _service.Save();
        }
    }

    // Single property for panel visibility — both conditions must be true.
    public bool IsPanelVisible => _isOpen && _isAgentEnabled;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string _input = "";
    public string Input
    {
        get => _input;
        set => this.RaiseAndSetIfChanged(ref _input, value);
    }

    private string _streamingText = "";
    public string StreamingText
    {
        get => _streamingText;
        private set => this.RaiseAndSetIfChanged(ref _streamingText, value);
    }

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        private set => this.RaiseAndSetIfChanged(ref _errorText, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public AgentService Service => _service;

    public string AgentName      => string.IsNullOrWhiteSpace(_service.Settings.AgentName)
        ? AgentSettings.DefaultAgentName : _service.Settings.AgentName.Trim();
    public string AgentNameUpper => AgentName.ToUpperInvariant();
    public string AskWatermark   => $"Ask {AgentName}…";

    // ── Speech input (push-to-talk) ───────────────────────────────────────────
    public bool HasSpeechInput => _speech?.IsAvailable == true;

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set => this.RaiseAndSetIfChanged(ref _isRecording, value);
    }

    public void StartRecording()
    {
        if (_speech is null || IsRecording) return;
        ErrorText  = "";
        StatusText = "Recording…";
        if (_speech.StartRecording())
        {
            IsRecording = true;
        }
        else
        {
            StatusText = "";
            ErrorText  = "Microphone recording failed to start. Check your microphone in Settings → Agent.";
        }
    }

    public async Task StopAndTranscribeAsync()
    {
        if (_speech is null || !IsRecording) return;
        IsRecording = false;
        StatusText  = "Transcribing…";
        ErrorText   = "";
        try
        {
            var text = await _speech.StopAndTranscribeAsync();
            StatusText = "";
            if (!string.IsNullOrWhiteSpace(text) && !IsBlankAudioResult(text))
            {
                Input = text;
                _ = SendAsync();
            }
            else
            {
                StatusText = "No speech detected — try speaking a bit longer.";
            }
        }
        catch (Exception ex)
        {
            StatusText = "";
            ErrorText  = $"Transcription failed: {ex.Message}";
        }
    }

    private void ConfigureHotkey(int vk)
    {
        if (_hotkey is null) return;
        _hotkey.Configure(vk);
    }

    // Whisper returns [BLANK_AUDIO], (Blank Audio), [silence], etc. for silence or noise.
    // Any result that is entirely wrapped in [ ] or ( ) is treated as blank.
    private static bool IsBlankAudioResult(string text)
    {
        var t = text.Trim();
        return string.IsNullOrEmpty(t) ||
               System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\[\(][^\n]*[\]\)]$");
    }

    // ── TTS mute / volume (session controls, separate from Settings) ──────────
    // These are only visible when a TTS provider is active.
    public bool HasTts => _tts is not null &&
                          _service.Settings.TtsProvider != EveConsole.Agent.TtsProvider.None;

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMuted, value);
            _tts?.SetMuted(value);
        }
    }

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set
        {
            this.RaiseAndSetIfChanged(ref _volume, value);
            _tts?.SetVolume(value);
            ScheduleVolumeSave(value);
        }
    }

    private CancellationTokenSource? _volSaveCts;
    private void ScheduleVolumeSave(float volume)
    {
        _volSaveCts?.Cancel();
        _volSaveCts = new CancellationTokenSource();
        var ct = _volSaveCts.Token;
        _ = Task.Delay(600, ct).ContinueWith(_ =>
        {
            if (!ct.IsCancellationRequested)
            {
                _service.Settings.TtsVolume = volume;
                _service.Save();
            }
        }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public AgentPanelViewModel(AgentService service, TtsService? tts = null,
        SpeechInputService? speech = null, GlobalHotkeyService? hotkey = null)
    {
        _service        = service;
        _tts            = tts;
        _speech         = speech;
        _hotkey         = hotkey;
        _isAgentEnabled = service.Settings.Enabled;
        _volume         = service.Settings.TtsVolume;

        // Wire global hotkey callbacks — hook fires on hook thread, must marshal to UI thread.
        if (_hotkey is not null)
        {
            _hotkey.OnPress   = () => Dispatcher.UIThread.Post(StartRecording);
            _hotkey.OnRelease = () => Dispatcher.UIThread.Post(() => _ = StopAndTranscribeAsync());
            ConfigureHotkey(service.Settings.PushToTalkKey);
        }

        // React when settings are saved — update agent enable state and TTS/speech visibility.
        service.WhenAnyValue(s => s.Settings)
               .Subscribe(s =>
               {
                   IsAgentEnabled = s.Enabled;
                   this.RaisePropertyChanged(nameof(HasTts));
                   this.RaisePropertyChanged(nameof(HasSpeechInput));
                   this.RaisePropertyChanged(nameof(AgentName));
                   this.RaisePropertyChanged(nameof(AgentNameUpper));
                   this.RaisePropertyChanged(nameof(AskWatermark));
                   ConfigureHotkey(s.PushToTalkKey);
               });

        if (service.Settings.PersistHistory)
            LoadHistory();

        // Restore panel open state from last session (only if agent is enabled)
        if (service.Settings.PanelOpen && service.Settings.Enabled)
            _isOpen = true;
    }

    public void ToggleOpen()
    {
        if (!_isAgentEnabled) return;
        IsOpen = !IsOpen;
    }

    /// <summary>
    /// Pushes a message into the conversation on the app's behalf — used by the AgentNotify
    /// alarm action, so a fired alarm is phrased by the agent and spoken aloud when TTS is on.
    /// Waits briefly for an in-flight reply rather than dropping the notification.
    /// </summary>
    public async Task NotifyAsync(string message)
    {
        if (!_isAgentEnabled || string.IsNullOrWhiteSpace(message)) return;

        for (var i = 0; i < 60 && IsBusy; i++)
            await Task.Delay(500);
        if (IsBusy) return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            IsOpen = true;
            Input  = message;

            // Not shown: the capsuleer did not write it and does not need to read the
            // instructions the alarm gave. They see the agent's reply, which is the point.
            await SendAsync(showUserMessage: false);
        });
    }

    // Window names the local intent detector recognises — must match open_window enum values.
    private static readonly (string[] Keywords, string Window)[] _navPatterns =
    [
        (["assets tab", "asset tab", "assets window", " assets"],   "assets"),
        (["industry tab", "industry window", " industry"],           "industry"),
        (["items tab", "item tab", "items window", "item browser"],  "items"),
        (["characters tab", "character tab", "characters window"],   "characters"),
        (["data tab", "data window"],                                "data"),
    ];
    private static readonly string[] _navVerbs =
        ["open", "show", "pull up", "switch to", "go to", "navigate to", "take me to", "bring up"];

    // Fires the WindowOpenRequested event locally so the tab switches immediately,
    // without waiting for the model to decide to call the open_window tool.
    private void ApplyNavigationIntent(string text)
    {
        var lower = text.ToLowerInvariant();
        if (!_navVerbs.Any(v => lower.Contains(v))) return;
        foreach (var (keywords, window) in _navPatterns)
        {
            if (keywords.Any(k => lower.Contains(k)))
            {
                _service.RequestWindowOpen(window);
                return;
            }
        }
    }

    public Task SendAsync() => SendAsync(showUserMessage: true);

    /// <param name="showUserMessage">
    /// False when the app is speaking on the capsuleer's behalf — an alarm handing over
    /// something to report. The text still goes to the model, since the reply is meaningless
    /// without it, but the panel shows only the reply.
    /// </param>
    private async Task SendAsync(bool showUserMessage)
    {
        var text = Input.Trim();
        if (string.IsNullOrEmpty(text) || IsBusy) return;

        ApplyNavigationIntent(text);

        if (_service.Provider is null || !_service.Provider.IsConfigured)
        {
            ErrorText = _service.Settings.Enabled
                ? "API key not configured. Add your key in Settings → Agent."
                : "Agent is disabled. Enable it in Settings → Agent.";
            return;
        }

        ErrorText = "";
        Input     = "";
        IsBusy    = true;

        // If a background summarization is still running, wait for it first.
        if (_summarizationTask is { IsCompleted: false })
        {
            StatusText = "Organizing context…";
            try   { await _summarizationTask; }
            catch { /* summarization failure is non-fatal */ }
            StatusText = "";
        }
        _summarizationTask = null;

        var userMsg = new AgentMessage(MessageRole.User, text) { ShowInChat = showUserMessage };
        _history.Add(userMsg);
        if (userMsg.ShowInChat) Messages.Add(userMsg);

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _tts?.Stop();
        var ct = _cts.Token;

        var systemPrompt = BuildSystemPrompt();
        var sb = new StringBuilder();

        var telemetry = _service.Telemetry;
        telemetry?.Begin(_conversationId, _service.Provider.ProviderName, "", text.Length);
        var failure = "";

        // Says what is happening while nothing is on screen. A question needing discovery can run
        // six round trips over twenty seconds, and an empty panel through all of it is
        // indistinguishable from a hang — which is exactly how it was read.
        SetStatus("Thinking…");
        _service.ToolActivity = tool => SetStatus(ToolStatus(tool));

        try
        {
            // ⚠️ ConfigureAwait(false) on the enumeration, so the streaming loop and everything
            // the provider does inside it stay off the UI thread. Without it each yielded chunk
            // hops back to the UI thread — hundreds of hops for one answer — and the window stops
            // responding while the agent is working. Everything below that touches UI state is
            // already marshalled explicitly, which is what makes this safe.
            // ⚠️ At most one streaming-text update is ever queued, and it reads the CURRENT text
            // when it runs rather than a snapshot taken when it was posted. The previous throttle
            // — post at most every 50 ms, skip the rest — lost whichever chunks arrived inside a
            // window and never posted them: a sentence the model wrote in one burst and then
            // followed with a twenty-second tool call sat on screen as its first word, while the
            // voice had already read the whole thing. The cost the throttle existed to avoid, one
            // ToString per token and a dispatcher job for each, is avoided here by the coalescing:
            // ToString runs once per UI update, and the UI paces those itself.
            var postQueued = 0;
            void PostStreamingText()
            {
                if (Interlocked.Exchange(ref postQueued, 1) == 1) return;   // one already waiting
                Dispatcher.UIThread.Post(() =>
                {
                    Interlocked.Exchange(ref postQueued, 0);
                    string current;
                    lock (sb) current = sb.ToString();
                    StreamingText = current;
                });
            }

            // Speech runs alongside the stream rather than after it. The agent often writes a
            // sentence, calls a tool, thinks, and writes more — so waiting for the end meant
            // silence through all of that and then a wall of text read at once.
            var speaking = _tts is not null
                        && _service.Settings.TtsProvider != EveConsole.Agent.TtsProvider.None;
            var pending  = new StringBuilder();

            await foreach (var chunk in _service.Provider.StreamAsync(
                systemPrompt, _history, _service.Tools,
                onUsage: u => telemetry?.Usage(u),
                volatileContext: CurrentAppState(),
                ct: ct).ConfigureAwait(false))
            {
                lock (sb) sb.Append(chunk);
                PostStreamingText();

                if (speaking)
                {
                    pending.Append(chunk);
                    SpeakCompleteSentences(pending, flush: false);
                }
            }

            // Once more at the end, so the finished text is on screen before the message is moved
            // into the history — the queued update above may not have run yet.
            string finalText;
            lock (sb) finalText = sb.ToString();
            Dispatcher.UIThread.Post(() => StreamingText = finalText);

            // Whatever is left has no closing punctuation and never will.
            if (speaking) SpeakCompleteSentences(pending, flush: true);

            if (!ct.IsCancellationRequested && sb.Length > 0)
            {
                var responseText = sb.ToString();
                var assistantMsg = new AgentMessage(MessageRole.Assistant, responseText);
                _history.Add(assistantMsg);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Messages.Add(assistantMsg);
                    StreamingText = "";
                });

                // ⚠️ Not spoken here any more. The sentences went to TTS as they were produced,
                // and speaking the finished text again would say the whole answer twice.

                SaveHistory();

                // Fire background summarization if threshold is crossed.
                if (EstimateTokens() >= _service.Settings.SummarizationThreshold)
                    _summarizationTask = SummarizeAsync();
            }
        }
        catch (OperationCanceledException) { failure = "cancelled"; }
        catch (Exception ex)
        {
            failure = ex.Message;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StreamingText = "";
                ErrorText     = $"Error: {ex.Message}";
            });
        }
        finally
        {
            // ⚠️ Always leave something in the conversation. A turn that produced no text at all —
            // it failed, or the model stopped mid-tool — otherwise looks identical to the app
            // having hung, and the capsuleer is left watching a panel that will never change.
            if (sb.Length == 0 && !ct.IsCancellationRequested)
            {
                var note = failure.Length > 0
                    ? $"That did not complete: {failure}"
                    : "That finished without producing an answer. Worth asking again — the "
                      + "detail of what happened is in Settings → Error Log.";

                var noteMsg = new AgentMessage(MessageRole.Assistant, note);
                _history.Add(noteMsg);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Messages.Add(noteMsg);
                    StreamingText = "";
                });
                SaveHistory();
            }

            // ⚠️ In the finally, so a cancelled or failed turn is still recorded. Those are the
            // ones worth having: a turn that burned four round trips and then threw is exactly
            // the spend that would otherwise never appear in the total.
            telemetry?.Complete(sb.Length, failure);

            _service.ToolActivity = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = "";
                IsBusy     = false;
            });
        }
    }

    /// <summary>
    /// The STABLE half of the system prompt — identical on every call.
    ///
    /// <para>⚠️ Live app state is deliberately NOT appended here any more. It is passed to the
    /// provider separately so it lands after the prompt-cache breakpoint: the cache is keyed on a
    /// byte-identical prefix, and a tail that changes each turn would invalidate roughly 33k
    /// tokens of tool schemas and app reference every single time.</para>
    /// </summary>
    private string BuildSystemPrompt()
        => AgentService.BuildSystemPrompt(_service.Settings, _service.Schema?.Prompt);

    /// <summary>What the capsuleer is looking at right now. Changes per turn, so it is never
    /// part of the cached prefix.</summary>
    private string? CurrentAppState() => _service.ContextProvider?.Invoke();

    /// <summary>Speaks whatever sentences are finished, keeping the unfinished tail back.</summary>
    private void SpeakCompleteSentences(StringBuilder pending, bool flush)
    {
        if (_tts is null) return;
        if (SpeechSegmenter.Take(pending, flush) is { } ready) _tts.SpeakAsync(ready);
    }

    /// <summary>Status is bound to the UI, and tool callbacks arrive on a pool thread.</summary>
    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => StatusText = text);

    /// <summary>
    /// What a tool is doing, in the capsuleer's terms rather than the tool's name. "query_database"
    /// tells them nothing; "Reading the database…" tells them it is still working.
    /// </summary>
    private static string ToolStatus(string tool) => tool switch
    {
        "query_database"        => "Reading the database…",
        "describe_tables"       => "Checking the schema…",
        "get_assets"            => "Looking up assets…",
        "get_industry_jobs"     => "Looking up industry jobs…",
        "get_character_info"    => "Looking up the character…",
        "get_market_prices"     => "Checking market prices…",
        "search_items"          => "Searching items…",
        "capture_tab"           => "Looking at the screen…",
        "manage_alarms"         => "Setting up the alarm…",
        _                       => "Working…",
    };

    public void ClearHistory()
    {
        _summarizationTask = null;
        _history.Clear();
        Messages.Clear();
        ErrorText     = "";
        StreamingText = "";
        StatusText    = "";
        DeleteHistoryFile();
    }

    // ── Token estimation ─────────────────────────────────────────────────────
    // 1 token ≈ 4 characters — close enough for a soft threshold.
    private int EstimateTokens() => _history.Sum(m => m.Content.Length / 4);

    // ── Background summarization ─────────────────────────────────────────────
    private async Task SummarizeAsync()
    {
        if (_service.Provider is null || !_service.Provider.IsConfigured) return;

        // Build a one-shot summarization call using current history.
        // We do NOT pass tools — summarization should be cheap and focused.
        var historySnapshot = _history.ToList();
        historySnapshot.Add(new AgentMessage(MessageRole.User,
            "Summarize our conversation so far in under 400 words. Cover: key topics discussed, " +
            "any EVE data retrieved (assets, jobs, prices), decisions or recommendations made, " +
            "and any unresolved questions. Be concise — this will replace the older messages as a context anchor."));

        var sb = new StringBuilder();

        // ⚠️ Measured like any other turn. This one is spend the capsuleer never asked for and
        // never sees — it fires on a threshold, sends the whole history, and would otherwise be
        // missing from the total with nothing to hint that a chunk of the bill was unaccounted.
        // Its own conversation id, so it does not read as a turn in the thread it summarises.
        var telemetry = _service.Telemetry;
        telemetry?.Begin($"{_conversationId}:summarize", _service.Provider.ProviderName, "", 0);
        var failure = "";

        try
        {
            await foreach (var chunk in _service.Provider.StreamAsync(
                AgentService.BuildSystemPrompt(_service.Settings), historySnapshot, tools: null,
                onUsage: u => telemetry?.Usage(u),
                ct: CancellationToken.None))
            {
                sb.Append(chunk);
            }
        }
        catch (Exception ex) { failure = ex.Message; return; /* summarization failure is silent */ }
        finally { telemetry?.Complete(sb.Length, failure); }

        if (sb.Length == 0) return;

        // Keep the 4 most recent messages intact for immediate context continuity.
        var recentMessages = _history.TakeLast(4).ToList();

        var summary = AgentMessage.Summary(sb.ToString());

        _history.Clear();
        _history.Add(summary);
        _history.AddRange(recentMessages);

        // Update the UI on the UI thread.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Messages.Clear();
            Messages.Add(summary);
            foreach (var m in recentMessages.Where(m => m.ShowInChat))
                Messages.Add(m);
        });

        SaveHistory();
    }

    // ── Persistence ──────────────────────────────────────────────────────────
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var messages = JsonSerializer.Deserialize<List<AgentMessage>>(
                File.ReadAllText(HistoryPath), _jsonOpts);
            if (messages is null || messages.Count == 0) return;
            // The whole history goes back to the model; only the visible part goes on screen.
            _history.AddRange(messages);
            foreach (var m in messages.Where(m => m.ShowInChat))
                Messages.Add(m);
        }
        catch { /* corrupt file — start fresh */ }
    }

    private void SaveHistory()
    {
        if (!_service.Settings.PersistHistory) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history, _jsonOpts));
        }
        catch { /* non-fatal */ }
    }

    private static void DeleteHistoryFile()
    {
        try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); }
        catch { }
    }
}
