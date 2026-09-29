using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using EveConsole.Agent;
using EveConsole.Agent.Tools.Actions;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;
using EveConsole.Localization;

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

    /// <summary>
    /// Who the agent is right now. While it speaks, the voice speaking — each voice can be a
    /// persona of its own, and the name follows it through a failover and back. Otherwise, and
    /// for a voice with no name of its own, the name from Personalisation.
    /// </summary>
    public string AgentName => _tts?.ActiveName is { Length: > 0 } voiceName
        ? voiceName
        : string.IsNullOrWhiteSpace(_service.Settings.AgentName)
            ? AgentSettings.DefaultAgentName : _service.Settings.AgentName.Trim();
    public string AgentNameUpper => AgentName.ToUpperInvariant();
    public string AskWatermark   => string.Format(AgentText.AskWatermark, AgentName);

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
        if (_speech.BeginCapture()) OnCaptureBegan();
        else
        {
            StatusText = "";
            ErrorText  = AgentText.ErrMicrophoneFailed;
        }
    }

    private CancellationTokenSource? _captureGuard;
    private static readonly TimeSpan LongestHold = TimeSpan.FromSeconds(45);

    /// <summary>The capture has begun, by key or by button: the panel says so, and a guard is set
    /// for a release the hook never sees, which would otherwise leave the microphone recording
    /// until the next one. Nobody dictates for longer than this in one breath.</summary>
    private void OnCaptureBegan()
    {
        IsRecording = true;
        ErrorText   = "";
        StatusText  = AgentText.StatusRecording;

        _captureGuard?.Cancel();
        var guard = _captureGuard = new CancellationTokenSource();
        _ = Task.Delay(LongestHold, guard.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || _speech?.EndCapture() != true) return;
            Dispatcher.UIThread.Post(() => _ = FinishCaptureAsync());
        }, TaskScheduler.Default);
    }

    public async Task StopAndTranscribeAsync()
    {
        if (_speech is null || !IsRecording) return;
        if (_speech.EndCapture()) await FinishCaptureAsync();
        else IsRecording = false;
    }

    /// <summary>After the key came up: waits out the tail, transcribes, and sends what was said.</summary>
    private async Task FinishCaptureAsync()
    {
        if (_speech is null) return;
        _captureGuard?.Cancel();
        IsRecording = false;
        StatusText  = AgentText.StatusTranscribing;
        ErrorText   = "";
        try
        {
            var pcm  = await _speech.CollectAsync();
            var text = pcm is null ? null : await _speech.TranscribeAsync(pcm);
            StatusText = "";
            if (!string.IsNullOrWhiteSpace(text) && !IsBlankAudioResult(text))
            {
                Input = text;
                _ = SendAsync();
            }
            else
            {
                StatusText = pcm is null
                    ? AgentText.StatusNoSpeechCaptured
                    : AgentText.StatusNoSpeechDetected;
            }
        }
        catch (Exception ex)
        {
            StatusText = "";
            ErrorText  = string.Format(AgentText.ErrTranscriptionFailed, ex.Message);
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
    public bool HasTts => _tts?.IsSpeaking == true;

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
            // ⚠️ The flag flips happen HERE, on the hook's own thread, not after a hop to the UI
            // thread: the capture window is the key hold itself, however busy the window is.
            _hotkey.OnPress   = () => { if (_speech?.BeginCapture() == true) Dispatcher.UIThread.Post(OnCaptureBegan); };
            _hotkey.OnRelease = () => { if (_speech?.EndCapture()   == true) Dispatcher.UIThread.Post(() => _ = FinishCaptureAsync()); };
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

                   // A different model may have a different window; the next turn measures again.
                   var models = JsonSerializer.Serialize(new { s.Models, s.ConversationRole, s.AnalystRole });
                   if (models != _modelsSeen)
                   {
                       _modelsSeen    = models;
                       _localWindow   = 0;
                       _localOverhead = 0;
                   }
               });

        if (service.Settings.PersistHistory)
            LoadHistory();

        if (_tts is not null) _tts.ActiveVoiceChanged += OnVoiceChanged;

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
    /// <summary>
    /// Says a text exactly as given and keeps it in the chat as the agent's own line — no model
    /// round trip. What an alarm uses when its condition composed the words itself.
    ///
    /// <para>⚠️ Interrupts whatever is being said. An intel call is worth more than the tail of
    /// an answer about last month's wallet, and the capsuleer may have seconds.</para>
    /// </summary>
    public async Task AnnounceAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Kept and shown whether or not the agent is on: the line is the application's, and
        // the panel only opens when there is an agent to open. The voice does not need one.
        var line = new AgentMessage(MessageRole.Assistant, text) { ToolsUsed = "alarm" };
        _history.Add(line);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isAgentEnabled) IsOpen = true;
            Messages.Add(line);
        });
        SaveHistory();

        if (_tts?.IsSpeaking == true)
        {
            _tts.Stop();
            _tts.SpeakAsync(text);
        }
    }

    // ── A change of voice, and so of persona ────────────────────────────────────

    /// <summary>
    /// The voice speaking changed. The name in the title bar and the panel follows it at once;
    /// a failover's announcement goes into the chat and the history as the new persona's own
    /// line. (A return is recorded by <see cref="SendAsync"/>, which makes it happen.)
    /// </summary>
    private void OnVoiceChanged(VoiceChange change) => Dispatcher.UIThread.Post(() =>
    {
        // The voices may have been configured after this panel was made; the first choice,
        // announced as Initial, is when the volume and mute controls learn there is speech.
        this.RaisePropertyChanged(nameof(HasTts));
        this.RaisePropertyChanged(nameof(AgentName));
        this.RaisePropertyChanged(nameof(AgentNameUpper));
        this.RaisePropertyChanged(nameof(AskWatermark));
        if (change.Reason == VoiceChangeReason.Failover) RecordVoiceChange(change, immediate: !IsBusy);
    });

    /// <summary>Voice-change notes that arrived mid-turn, held until the turn has stopped reading
    /// the history — a note added while the provider is walking it would break the walk.</summary>
    private readonly List<AgentMessage> _deferredNotes = [];

    /// <summary>
    /// What the model is told about a change of voice, so it answers as the persona now speaking
    /// and knows why: the announcement as that persona's own line, or — when nothing was said
    /// aloud — a hidden note. Nothing when the name did not change.
    /// </summary>
    private void RecordVoiceChange(VoiceChange change, bool immediate)
    {
        if (string.Equals(change.PreviousName, change.CurrentName, StringComparison.OrdinalIgnoreCase)) return;

        var note = change.Announcement.Length > 0
            ? new AgentMessage(MessageRole.Assistant, change.Announcement) { ToolsUsed = "voice change" }
            : new AgentMessage(MessageRole.User,
                $"[{change.PreviousName} has stepped away and you are now {change.CurrentName}. " +
                $"Carry on the conversation as {change.CurrentName}.]") { ShowInChat = false };

        if (note.ShowInChat) Messages.Add(note);
        if (immediate)
        {
            _history.Add(note);
            SaveHistory();
        }
        else lock (_deferredNotes) _deferredNotes.Add(note);
    }

    private void FlushDeferredNotes()
    {
        List<AgentMessage> notes;
        lock (_deferredNotes)
        {
            if (_deferredNotes.Count == 0) return;
            notes = [.. _deferredNotes];
            _deferredNotes.Clear();
        }
        _history.AddRange(notes);
    }

    /// <summary>
    /// A staged alarm has asked the capsuleer something; whatever they say next acknowledges it.
    /// Deliberately not "if they say all is well": a person answering at all is awake, which is
    /// the fact the alarm exists to establish, and no model has to remember to call anything.
    /// </summary>
    private AlarmAck? _pendingAck;
    private string?   _pendingAckText;

    /// <param name="spokenText">
    /// What the app said aloud, when it was the app and not the model that said it. Handed to
    /// the model with the reply, so it knows what the reply is to: a line the app put in its
    /// mouth reads, to it, like an announcement it happened to make, and "I'm awake" after it
    /// was being answered as a morning greeting.
    /// </param>
    public void ExpectReply(AlarmAck ack, string? spokenText = null)
    {
        _pendingAck     = ack;
        _pendingAckText = spokenText;
    }

    /// <summary>Set by MainWindow: takes the acknowledgement back to the alarm runner.</summary>
    public Func<AlarmAck, Task>? AcknowledgeCallback { get; set; }

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
        (["background processes", "background tab"],                 "background"),
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

    /// <summary>Which model a turn goes to.</summary>
    private enum Part
    {
        /// <summary>One model does everything — the whole prompt and every tool — as it always did.</summary>
        Whole,
        /// <summary>The conversation model: the app's own tools, and a hand-off for data questions.</summary>
        Conversation,
        /// <summary>The model that reads the capsuleer's data.</summary>
        Analyst,
    }

    /// <summary>How one model's attempt at a turn ended.</summary>
    /// <param name="Answered">It produced a reply, which is now in the conversation.</param>
    /// <param name="HandedOff">The conversation model passed the message to the data model.</param>
    /// <param name="Acted">
    /// It wrote or did something — text shown and perhaps spoken, a tool run — so the turn cannot
    /// simply be given to another model and started over.
    /// </param>
    private sealed record Attempt(bool Answered, bool HandedOff, Exception? Error, bool Acted);

    /// <param name="showUserMessage">
    /// False when the app is speaking on the capsuleer's behalf — an alarm handing over
    /// something to report. The text still goes to the model, since the reply is meaningless
    /// without it, but the panel shows only the reply.
    /// </param>
    private async Task SendAsync(bool showUserMessage)
    {
        var text = Input.Trim();
        if (string.IsNullOrEmpty(text) || IsBusy) return;

        // "/data …" or "/chat …": this one message goes where the capsuleer says.
        var forced = TakeOverride(ref text);
        if (string.IsNullOrEmpty(text)) return;

        // Only a message the capsuleer wrote counts; an alarm's own prompt arrives this way too.
        string? replyingTo = null;
        if (showUserMessage && _pendingAck is { } ack)
        {
            _pendingAck     = null;
            replyingTo      = _pendingAckText;
            _pendingAckText = null;
            if (AcknowledgeCallback is { } acknowledge)
                _ = acknowledge(ack);
        }

        ApplyNavigationIntent(text);

        var roles = _service.Roles;
        if (roles.Conversation is not { CanAnswer: true })
        {
            ErrorText = _service.Settings.Enabled
                ? AgentText.ErrNoModel
                : AgentText.ErrAgentDisabled;
            return;
        }

        ErrorText = "";
        Input     = "";
        IsBusy    = true;

        // If a background summarization is still running, wait for it first.
        if (_summarizationTask is { IsCompleted: false })
        {
            StatusText = AgentText.StatusOrganizingContext;
            try   { await _summarizationTask; }
            catch { /* summarization failure is non-fatal */ }
            StatusText = "";
        }
        _summarizationTask = null;

        // Between turns is where the voice may change of its own accord. A note held from a
        // failover mid-answer goes in first; then a preferred voice that is back and has stayed up
        // takes over — announced — so the model knows who it is before it writes a word.
        FlushDeferredNotes();
        if (_tts?.ApplyPendingReturn() is { } returned) RecordVoiceChange(returned, immediate: true);

        // A reply to something the app said aloud goes to the model with a note of what that
        // was — hidden, like an alarm's own prompt, because the capsuleer already heard it.
        if (replyingTo is not null)
            _history.Add(new AgentMessage(MessageRole.User,
                $"[The capsuleer is answering the alarm the app just spoke aloud: \"{replyingTo}\" " +
                "Their answer has already reset that alarm — nothing to do about it. Reply to " +
                "them in that light: the ship it names is still undocked unless they say otherwise.]")
            { ShowInChat = false });

        // Stamped with what they had on screen as they wrote it — see AgentMessage.OnScreen.
        var userMsg = new AgentMessage(MessageRole.User, text)
        {
            ShowInChat = showUserMessage,
            OnScreen   = _service.OnScreenProvider?.Invoke(),
        };
        _history.Add(userMsg);
        if (userMsg.ShowInChat) Messages.Add(userMsg);

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _tts?.Stop();
        var ct = _cts.Token;

        // The standing instructions and names may have been changed on another client since
        // this one started; the prompt is built from what the database says now.
        await _service.RefreshSharedAsync();

        // Says what is happening while nothing is on screen. A question needing discovery can run
        // six round trips over twenty seconds, and an empty panel through all of it is
        // indistinguishable from a hang — which is exactly how it was read.
        SetStatus(AgentText.StatusThinking);

        var answered = false;
        var failure  = "";
        try
        {
            // Also between turns: a role's own model that is back, and has stayed up, takes over
            // again — and says so, if the role says such things.
            foreach (var change in await roles.ApplyPendingReturnsAsync(ct)) await ShowModelChangeAsync(change);

            // Who answers. One model: that one, as always. Two: where the capsuleer said, else where
            // the router sends it. An alarm's report needs no data — the alarm brought it.
            var part = !roles.IsSplit ? Part.Whole
                     : forced ?? (showUserMessage ? await RouteAsync(text, ct) : Part.Conversation);

            for (var attempt = 0; attempt < 4 && !ct.IsCancellationRequested; attempt++)
            {
                var seat   = part == Part.Analyst ? roles.Analyst! : roles.Conversation!;
                var result = await RunAsync(part, seat, text.Length, ct);

                if (result.HandedOff)
                {
                    part = Part.Analyst;
                    SetStatus(AgentText.StatusLookingIntoIt);
                    continue;
                }

                if (result.Error is { } error)
                {
                    // The model is not there to answer. Its fallback takes over at once — and
                    // answers this very message, when nothing has been said or done yet. After
                    // that, the error stands and the next message goes to the fallback.
                    if (ModelFailure.IsUnavailable(error, ct) && roles.FailOver(seat) is { } change)
                    {
                        await ShowModelChangeAsync(change);
                        if (!result.Acted) continue;
                    }

                    failure = error is OperationCanceledException && ct.IsCancellationRequested ? "cancelled" : error.Message;
                    if (failure != "cancelled")
                        await Dispatcher.UIThread.InvokeAsync(() => ErrorText = string.Format(CommonText.ErrorWithMessage, error.Message));
                }

                answered = result.Answered;
                break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { failure = "cancelled"; }
        catch (Exception ex)
        {
            failure = ex.Message;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StreamingText = "";
                ErrorText     = string.Format(CommonText.ErrorWithMessage, ex.Message);
            });
        }
        finally
        {
            // ⚠️ Always leave something in the conversation. A turn that produced no text at all —
            // it failed, or the model stopped mid-tool — otherwise looks identical to the app
            // having hung, and the capsuleer is left watching a panel that will never change.
            if (!answered && !ct.IsCancellationRequested)
            {
                var note = failure.Length > 0
                    ? $"That did not complete: {failure}"
                    : "That finished without producing an answer. Worth asking again — the "
                      + "detail of what happened is in Settings → Error Log.";

                var noteMsg = new AgentMessage(MessageRole.Assistant, note);
                FlushDeferredNotes();
                _history.Add(noteMsg);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Messages.Add(noteMsg);
                    StreamingText = "";
                });
                SaveHistory();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText = "";
                IsBusy     = false;
            });
        }
    }

    /// <summary>
    /// One model's attempt at the turn: streams its answer to the screen and the voice, and puts a
    /// finished reply into the conversation, tagged with the model that wrote it.
    /// </summary>
    private async Task<Attempt> RunAsync(Part part, ModelRoles.Seat seat, int userChars, CancellationToken turn)
    {
        var model = seat.Model;
        if (seat.Provider is not { IsConfigured: true } provider)
            return new Attempt(false, false, new HttpRequestException(
                $"{model.Label} is not set up — it needs its key or its server's address in Settings → AI Agent.",
                null, System.Net.HttpStatusCode.Unauthorized), Acted: false);

        var scope        = part == Part.Conversation ? PromptScope.Conversation : PromptScope.Full;
        var tools        = part == Part.Conversation ? _service.ConversationTools : _service.Tools;
        var systemPrompt = BuildSystemPrompt(scope);

        // The conversation model may hand the message over. That stops its stream at once, so
        // nothing it goes on to write — a guess, most likely — is shown or said.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(turn);
        var handedOff = 0;
        _service.HandOffRequested = part == Part.Conversation
            ? _ => { Interlocked.Exchange(ref handedOff, 1); attemptCts.Cancel(); }
            : null;

        // Every tool this model calls, in the order first called, for the line under its reply.
        var toolCounts = new Dictionary<string, int>();
        _service.ToolActivity = tool =>
        {
            if (tool == HandOffTool.ToolName) return;
            SetStatus(ToolStatus(tool));
            lock (toolCounts) toolCounts[tool] = toolCounts.GetValueOrDefault(tool) + 1;
        };

        var telemetry = _service.Telemetry;
        telemetry?.Begin(_conversationId, provider.ProviderName, model.ModelName, userChars);
        var failure = "";
        var sb      = new StringBuilder();

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
            var speaking = _tts?.IsSpeaking == true;
            var pending  = new StringBuilder();

            // The first round's prompt, and the window it went into when the server says: what this
            // model carries besides the history — see the check after the turn.
            long firstPrompt = 0; int? window = null;
            var historyBefore = EstimateTokens();

            await foreach (var chunk in provider.StreamAsync(
                systemPrompt, _history, tools,
                onUsage: u =>
                {
                    telemetry?.Usage(u);
                    if (firstPrompt == 0) firstPrompt = u.InputTokens + u.CacheReadTokens;
                    window = u.ContextLength ?? window;
                },
                volatileContext: CurrentAppState(),
                ct: attemptCts.Token).ConfigureAwait(false))
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

            if (turn.IsCancellationRequested || finalText.Length == 0)
            {
                bool actedHere;
                lock (toolCounts) actedHere = finalText.Length > 0 || toolCounts.Count > 0;
                return new Attempt(false, false, null, actedHere);
            }

            string toolsUsed;
            lock (toolCounts) toolsUsed = ToolUseSummary.Describe(toolCounts);
            var assistantMsg = new AgentMessage(MessageRole.Assistant, finalText)
            {
                ToolsUsed  = toolsUsed,
                AnsweredBy = model.Label,
                AnsweredAs = part switch
                {
                    Part.Analyst      => ModelRoleKind.Analyst,
                    Part.Conversation => ModelRoleKind.Conversation,
                    _                 => null,
                },
            };
            // A voice or a model that took over mid-answer said so before this answer ended.
            FlushDeferredNotes();
            _history.Add(assistantMsg);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Messages.Add(assistantMsg);
                StreamingText = "";
            });

            // ⚠️ Not spoken here any more. The sentences went to TTS as they were produced,
            // and speaking the finished text again would say the whole answer twice.

            SaveHistory();

            // A model of our own that says what its window is: remember that, and what it carries
            // besides the history — prompt, tools, app state — so the check below is made after
            // EVERY turn, the data model's too.
            if (window is > 0 && firstPrompt > 0)
            {
                _localWindow   = window.Value;
                _localOverhead = Math.Max(0, firstPrompt - historyBefore);
            }

            if (NeedsSummary()) _summarizationTask = SummarizeAsync();

            return new Attempt(true, false, null, Acted: true);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref handedOff) == 1 && !turn.IsCancellationRequested)
        {
            failure = "handed off";
            await Dispatcher.UIThread.InvokeAsync(() => StreamingText = "");
            return new Attempt(false, true, null, Acted: false);
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException && turn.IsCancellationRequested ? "cancelled" : ex.Message;
            await Dispatcher.UIThread.InvokeAsync(() => StreamingText = "");
            bool acted;
            lock (sb) acted = sb.Length > 0;
            lock (toolCounts) acted |= toolCounts.Count > 0;
            return new Attempt(false, false, ex, acted);
        }
        finally
        {
            // ⚠️ In the finally, so a cancelled or failed attempt is still recorded. Those are the
            // ones worth having: a turn that burned four round trips and then threw is exactly
            // the spend that would otherwise never appear in the total.
            int length;
            lock (sb) length = sb.Length;
            telemetry?.Complete(length, failure);

            _service.HandOffRequested = null;
            _service.ToolActivity     = null;
        }
    }

    /// <summary>The last window a model of our own reported, and what it carries besides the
    /// history. Forgotten when the models or roles change: the model may have.</summary>
    private int    _localWindow;
    private long   _localOverhead;
    private string _modelsSeen = "";

    /// <summary>
    /// Whether the history should be summarised now: past the threshold, or closing on the window
    /// of a model of our own.
    ///
    /// <para>⚠️ The threshold is set blind to the window, and a local server does not refuse a
    /// prompt that outgrows it. Ollama drops the OLDEST messages to make room, and the oldest
    /// message in this layout is the system prompt: measured on a 2k window, the instructions went
    /// first, whole, while the chat stayed, and the model went on answering with none. Eighty
    /// percent leaves room for the next question and the reply.</para>
    ///
    /// <para>⚠️ Judged on the NEXT prompt that model will be sent — what it carries besides the
    /// history plus the history as it now stands — not on the turn just answered. With a model of
    /// its own for the data, a long data answer grows the shared history in a turn the local model
    /// never saw: checked only after its own turns, its next prompt could outgrow the window with
    /// nothing having noticed.</para>
    /// </summary>
    private bool NeedsSummary() =>
        EstimateTokens() >= _service.Settings.SummarizationThreshold
        || (_localWindow > 0 && _localOverhead + EstimateTokens() > _localWindow * 0.8);

    /// <summary>
    /// "/data …" sends one message to the model that reads the data, "/chat …" to the conversation
    /// model, whatever the router would have decided. The prefix is taken off the message.
    /// </summary>
    private static Part? TakeOverride(ref string text)
    {
        foreach (var (prefix, part) in new[] { ("/data", Part.Analyst), ("/chat", Part.Conversation) })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length])) continue;
            text = text[prefix.Length..].Trim();
            return part;
        }
        return null;
    }

    /// <summary>
    /// Asks the conversation model which model should answer — one word about one message. When
    /// it cannot say, the conversation model answers, with its hand-off tool to fall back on.
    /// </summary>
    private async Task<Part> RouteAsync(string text, CancellationToken ct)
    {
        var seat = _service.Roles.Conversation;
        if (seat?.Provider is not { IsConfigured: true } provider) return Part.Conversation;

        // The exchange before this message: the last thing the capsuleer said, and the reply.
        var earlier   = _history.Take(_history.Count - 1).Where(m => m.ShowInChat && !m.IsSummary).ToList();
        var lastReply = earlier.LastOrDefault(m => m.Role == MessageRole.Assistant && m.ToolsUsed is not ("model change" or "voice change" or "alarm"));
        var lastUser  = earlier.LastOrDefault(m => m.Role == MessageRole.User);
        var fromData  = lastReply?.AnsweredAs switch
        {
            ModelRoleKind.Analyst      => true,
            ModelRoleKind.Conversation => (bool?)false,
            _                          => null,
        };

        var prompt = ModelRouter.Prompt(_service.Settings.HandOffWhenText, lastUser?.Content, lastReply?.Content,
                                        fromData, text, local: seat.Model.IsLocal);

        var telemetry = _service.Telemetry;
        telemetry?.Begin($"{_conversationId}:route", provider.ProviderName, seat.Model.ModelName, text.Length);
        var failure = "";
        var reply   = new StringBuilder();
        try
        {
            // A model of our own may have to load first; beyond this, the conversation model
            // answers and hands off if it must.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await foreach (var chunk in provider.StreamAsync(
                               ModelRouter.SystemPrompt, [new AgentMessage(MessageRole.User, prompt)],
                               tools: null, onUsage: u => telemetry?.Usage(u), ct: timeout.Token).ConfigureAwait(false))
                reply.Append(chunk);

            return ModelRouter.Parse(reply.ToString()) == ModelRoleKind.Analyst ? Part.Analyst : Part.Conversation;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failure = ex.Message;
            return Part.Conversation;
        }
        finally { telemetry?.Complete(reply.Length, failure); }
    }

    /// <summary>
    /// A role's model changed — it fell over to its fallback, or came back. Said in the chat as the
    /// agent's own line, and aloud when it speaks. The model hears of it too, in the history just
    /// before its reply — not before the reply is written, where it would read as the reply begun.
    /// </summary>
    private async Task ShowModelChangeAsync(ModelChange change)
    {
        if (change.Announcement.Length == 0) return;   // the role says nothing; the tag under the reply still shows it
        var line = new AgentMessage(MessageRole.Assistant, change.Announcement) { ToolsUsed = "model change" };
        await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(line));
        lock (_deferredNotes) _deferredNotes.Add(line);
        if (_tts?.IsSpeaking == true) _tts.SpeakAsync(change.Announcement);
    }

    /// <summary>
    /// The STABLE half of the system prompt — identical on every call.
    ///
    /// <para>⚠️ Live app state is deliberately NOT appended here any more. It is passed to the
    /// provider separately so it lands after the prompt-cache breakpoint: the cache is keyed on a
    /// byte-identical prefix, and a tail that changes each turn would invalidate roughly 33k
    /// tokens of tool schemas and app reference every single time.</para>
    /// </summary>
    private string BuildSystemPrompt(PromptScope scope)
    {
        // The persona speaking is the persona writing: a voice with a name of its own gives the
        // model that name, or it would go on introducing itself as someone the capsuleer is not
        // hearing. The cached prefix changes with it — once, at the change.
        var settings = _service.Settings;
        if (AgentName != settings.AgentName)
        {
            settings = settings.Clone();
            settings.AgentName = AgentName;
        }
        return AgentService.BuildSystemPrompt(settings, scope == PromptScope.Full ? _service.Schema?.Prompt : null, scope);
    }

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
        "query_database"        => AgentText.ToolStatusQueryDatabase,
        "describe_tables"       => AgentText.ToolStatusDescribeTables,
        "get_assets"            => AgentText.ToolStatusGetAssets,
        "get_industry_jobs"     => AgentText.ToolStatusGetIndustryJobs,
        "get_character_info"    => AgentText.ToolStatusGetCharacterInfo,
        "get_market_prices"     => AgentText.ToolStatusGetMarketPrices,
        "search_items"          => AgentText.ToolStatusSearchItems,
        "capture_tab"           => AgentText.ToolStatusCaptureTab,
        "manage_alarms"         => AgentText.ToolStatusManageAlarms,
        _                       => AgentText.ToolStatusWorking,
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

    /// <summary>What a summary model of our own is told, in place of the agent's whole prompt.</summary>
    private const string SummaryPrompt =
        "You summarise a conversation between a capsuleer and their EVE Online companion, so the " +
        "companion can carry on from the summary alone.";

    private async Task SummarizeAsync()
    {
        // Its own model when one is chosen for it, otherwise whatever answers the conversation now.
        if (_service.Roles.Summary is not { Provider: { IsConfigured: true } provider } target) return;

        // The prompt its model already has cached: the whole of it for a model that reads the
        // data, the conversation model's for the conversation model.
        var roles = _service.Roles;
        var scope = !roles.IsSplit ? PromptScope.Full
                  : target.OwnModel && target.Model.Id == roles.Analyst?.Primary.Id ? PromptScope.Full
                  : PromptScope.Conversation;

        // ⚠️ A model of our own gets one line, not the agent's prompt. The long prompt earns its
        // place only through Anthropic's cache, which a summary shares with the turns before it;
        // Ollama has no such cache to share, and the prompt took 8k tokens of a 16k window — the
        // room the history being summarised needed.
        var systemPrompt = target.Model.IsLocal
            ? SummaryPrompt
            : AgentService.BuildSystemPrompt(_service.Settings, scope: scope);

        // Build a one-shot summarization call using current history.
        // We do NOT pass tools — summarization should be cheap and focused.
        var historySnapshot = _history.ToList();
        historySnapshot.Add(new AgentMessage(MessageRole.User,
            "Summarize our conversation so far in under 400 words. Cover: key topics discussed, " +
            "any EVE data retrieved (assets, jobs, prices), decisions or recommendations made, " +
            "and any unresolved questions. Be concise — this will replace the older messages as a context anchor. " +
            // ⚠️ The messages carry when they were sent, and the summary is all that survives of
            // them. Without the dates, a question from a month ago reads afterwards as though it
            // were asked just now.
            "Open with the dates this covers, and keep the date beside anything that was asked or " +
            "found at a particular time — the messages are timestamped, and later turns need to " +
            "know how long ago each thing was."));

        var sb = new StringBuilder();

        // ⚠️ Measured like any other turn. This one is spend the capsuleer never asked for and
        // never sees — it fires on a threshold, sends the whole history, and would otherwise be
        // missing from the total with nothing to hint that a chunk of the bill was unaccounted.
        // Its own conversation id, so it does not read as a turn in the thread it summarises.
        var telemetry = _service.Telemetry;
        telemetry?.Begin($"{_conversationId}:summarize", provider.ProviderName, target.Model.ModelName, 0);
        var failure = "";

        try
        {
            await foreach (var chunk in provider.StreamAsync(
                systemPrompt, historySnapshot, tools: null,
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
