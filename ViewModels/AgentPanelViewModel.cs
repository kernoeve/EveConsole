using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using EveCortex.Agent;
using EveCortex.Services;
using ReactiveUI;

namespace EveCortex.ViewModels;

public sealed class AgentPanelViewModel : ReactiveObject
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveCortex", "aura-history.json");

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
        ? "Betty" : _service.Settings.AgentName.Trim();
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
                          _service.Settings.TtsProvider != EveCortex.Agent.TtsProvider.None;

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

    public async Task SendAsync()
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

        var userMsg = new AgentMessage(MessageRole.User, text);
        _history.Add(userMsg);
        Messages.Add(userMsg);

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _tts?.Stop();
        var ct = _cts.Token;

        var systemPrompt = BuildSystemPrompt();
        var sb = new StringBuilder();
        try
        {
            await foreach (var chunk in _service.Provider.StreamAsync(
                systemPrompt, _history, _service.Tools, ct))
            {
                sb.Append(chunk);
                var snapshot = sb.ToString();
                Dispatcher.UIThread.Post(() => StreamingText = snapshot);
            }

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

                if (_tts is not null && _service.Settings.TtsProvider != EveCortex.Agent.TtsProvider.None)
                    _tts.SpeakAsync(responseText);

                SaveHistory();

                // Fire background summarization if threshold is crossed.
                if (EstimateTokens() >= _service.Settings.SummarizationThreshold)
                    _summarizationTask = SummarizeAsync();
            }
        }
        catch (OperationCanceledException) { /* new message sent or panel closed */ }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StreamingText = "";
                ErrorText     = $"Error: {ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    private string BuildSystemPrompt()
    {
        var prompt  = AgentService.BuildSystemPrompt(_service.Settings);
        var context = _service.ContextProvider?.Invoke();
        if (string.IsNullOrEmpty(context))
            return prompt;
        return prompt + "\n\n## Current App State\n" + context;
    }

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
        try
        {
            await foreach (var chunk in _service.Provider.StreamAsync(
                AgentService.BuildSystemPrompt(_service.Settings), historySnapshot, tools: null,
                ct: CancellationToken.None))
            {
                sb.Append(chunk);
            }
        }
        catch { return; /* summarization failure is silent */ }

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
            foreach (var m in recentMessages)
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
            _history.AddRange(messages);
            foreach (var m in messages)
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
