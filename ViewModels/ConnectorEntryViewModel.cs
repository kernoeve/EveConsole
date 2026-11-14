using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Connector;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One connector in Settings → Connectors: its address, connecting to it with EVE's sign-in, and what the user
/// chose it for — corporations and alliances, and functions. Choices save as soon as they change.
/// </summary>
public class ConnectorEntryViewModel : ReactiveObject
{
    private readonly ConnectorService _connector;
    private readonly Func<Task>       _reload;
    private readonly Action           _stateChanged;

    // EVE's page sends nothing back if its tab is simply closed, so the wait is always bounded,
    // and can be abandoned.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(16);   // past the longest sign-in a connector may allow (15 min)

    private CancellationTokenSource? _connectCts;

    private bool                                 _applying;
    private readonly SemaphoreSlim               _choiceGate = new(1, 1);

    // ⚠️ Choice saves started and not yet finished. While there are any, the check boxes are not rebuilt from
    // the database: a save sends every box as it is, and boxes rebuilt from a read taken before an earlier save
    // committed would send that save's tick away again. Once the last one is done, what is stored is read back.
    // UI thread only.
    private int  _pendingSaves;
    private bool _rebuildSkipped;

    // Every connector's choices as they were when ChoiceMessage was set (null: the next ones applied). A
    // refusal is about choices on other connectors; once those change, it no longer applies.
    private string? _messageChoices;

    public ConnectorEntryViewModel(ConnectorService connector, int id, Func<Task> reload, Action stateChanged)
    {
        _connector    = connector;
        _reload       = reload;
        _stateChanged = stateChanged;
        Id            = id;

        ConnectCommand    = ReactiveCommand.CreateFromTask(ConnectAsync);
        CancelCommand     = ReactiveCommand.Create(() => { _connectCts?.Cancel(); });
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync);
        RefreshNowCommand = ReactiveCommand.CreateFromTask(RefreshNowAsync);
    }

    public int Id { get; }

    public ReactiveCommand<Unit, Unit> ConnectCommand    { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand     { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshNowCommand { get; }

    private string _url = "";
    public string Url { get => _url; set => this.RaiseAndSetIfChanged(ref _url, value); }

    private string _title = SettingsText.ConnectorNewEntry;
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    private string _connectedAs = SettingsText.ConnectorNotConnected;
    public string ConnectedAs { get => _connectedAs; private set => this.RaiseAndSetIfChanged(ref _connectedAs, value); }

    private string _scopesLine = SettingsText.ConnectorNoScope;
    public string ScopesLine { get => _scopesLine; private set => this.RaiseAndSetIfChanged(ref _scopesLine, value); }

    private string _functionsLine = SettingsText.ConnectorNoFunctions;
    public string FunctionsLine { get => _functionsLine; private set => this.RaiseAndSetIfChanged(ref _functionsLine, value); }

    // ── The list's badge ─────────────────────────────────────────────────────

    private string _statusLabel = SettingsText.ConnectorStatusNotConnected;
    public string StatusLabel { get => _statusLabel; private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }

    private string? _statusTip;
    public string? StatusTip { get => _statusTip; private set => this.RaiseAndSetIfChanged(ref _statusTip, value); }

    private bool _isGood;
    public bool IsGood { get => _isGood; private set => this.RaiseAndSetIfChanged(ref _isGood, value); }

    private bool _isWarn;
    public bool IsWarn { get => _isWarn; private set => this.RaiseAndSetIfChanged(ref _isWarn, value); }

    // ── The connection ───────────────────────────────────────────────────────

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { this.RaiseAndSetIfChanged(ref _isConnected, value); RaiseStates(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { this.RaiseAndSetIfChanged(ref _isBusy, value); RaiseStates(); }
    }

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        private set { this.RaiseAndSetIfChanged(ref _isConnecting, value); RaiseStates(); }
    }

    private bool _hasStoredLinks;
    /// <summary>Links are kept from this connector, connected or not: after it stopped knowing the key, only
    /// Disconnect can remove them.</summary>
    public bool HasStoredLinks
    {
        get => _hasStoredLinks;
        private set { this.RaiseAndSetIfChanged(ref _hasStoredLinks, value); RaiseStates(); }
    }

    public bool CanConnect    => !IsConnected && !IsBusy;
    public bool CanEditUrl    => !IsConnected && !IsBusy;
    public bool CanRefresh    => IsConnected && !IsBusy;
    public bool CanDisconnect => (IsConnected || HasStoredLinks) && !IsBusy;

    private void RaiseStates()
    {
        this.RaisePropertyChanged(nameof(CanConnect));
        this.RaisePropertyChanged(nameof(CanEditUrl));
        this.RaisePropertyChanged(nameof(CanRefresh));
        this.RaisePropertyChanged(nameof(CanDisconnect));
        _stateChanged();
    }

    private string _status = "";
    /// <summary>What the last thing done here came to: the sign-in's code while connecting, then the result.</summary>
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => this.RaiseAndSetIfChanged(ref _detail, value); }

    // ── The choices ──────────────────────────────────────────────────────────

    public ObservableCollection<ConnectorScopeOptionViewModel>    CorporationOptions { get; } = [];
    public ObservableCollection<ConnectorScopeOptionViewModel>    AllianceOptions    { get; } = [];
    public ObservableCollection<ConnectorFunctionOptionViewModel> FunctionOptions    { get; } = [];

    private bool _hasScopeOptions;
    public bool HasScopeOptions { get => _hasScopeOptions; private set => this.RaiseAndSetIfChanged(ref _hasScopeOptions, value); }

    private bool _hasCorporationOptions;
    public bool HasCorporationOptions { get => _hasCorporationOptions; private set => this.RaiseAndSetIfChanged(ref _hasCorporationOptions, value); }

    private bool _hasAllianceOptions;
    public bool HasAllianceOptions { get => _hasAllianceOptions; private set => this.RaiseAndSetIfChanged(ref _hasAllianceOptions, value); }

    private string _choiceMessage = "";
    /// <summary>Why the last choice was refused; empty when it was saved.</summary>
    public string ChoiceMessage { get => _choiceMessage; private set => this.RaiseAndSetIfChanged(ref _choiceMessage, value); }

    /// <summary>
    /// Shows what is stored for this entry. Never saves: check boxes set here do not count as the user's choice.
    /// While something of its own is running, the address and its status line are left alone, and while a choice
    /// is being saved, the check boxes. <paramref name="choices"/> is every connector's choices, as
    /// <see cref="ChoicesKey"/> writes them.
    /// </summary>
    internal void Apply(ConnectorSnapshot s, IReadOnlyList<ConnectorScopeChoice> scopes, LinkedSource? source, ConnectorRefreshResult? last,
                        string choices)
    {
        _applying = true;
        try
        {
            if (ChoiceMessage.Length > 0)
            {
                if (_messageChoices is null)            _messageChoices = choices;
                else if (_messageChoices != choices)    ChoiceMessage = "";
            }

            var wasConnected = IsConnected;
            Title          = s.Title;
            IsConnected    = s.Connected;
            HasStoredLinks = source is not null;
            // Not the connector's name: the title above it already is that.
            ConnectedAs    = s.Connected ? string.Format(SettingsText.ConnectorConnectedAs, s.CharacterName)
                                         : SettingsText.ConnectorNotConnected;

            var names = scopes.ToDictionary(c => (c.IsAlliance, c.Id), c => c.Name);
            var scopeNames = s.Corporations.Select(i => names.GetValueOrDefault((false, i)) ?? string.Format(CharactersText.SummaryCorpNumbered, i))
                .Concat(s.Alliances.Select(i => names.GetValueOrDefault((true, i)) ?? string.Format(CharactersText.SummaryAllianceNumbered, i)))
                .ToList();
            ScopesLine    = scopeNames.Count > 0 ? string.Format(SettingsText.ConnectorForLine, string.Join(", ", scopeNames)) : SettingsText.ConnectorNoScope;
            FunctionsLine = s.Functions.Count > 0
                ? string.Format(SettingsText.ConnectorFunctionsLine, string.Join(", ", ConnectorFunctions.All.Where(s.Chose).Select(ConnectorFunctions.Label)))
                : SettingsText.ConnectorNoFunctions;

            // The badge. A connector that stopped knowing the key is no longer connected, and says why: the last
            // refresh in this process found it so. Other problems only count while connected.
            var problem = s.Connected
                ? last?.Outcome is ConnectorRefreshOutcome.Unreachable or ConnectorRefreshOutcome.NotAllowed or ConnectorRefreshOutcome.Rejected
                : last?.Outcome is ConnectorRefreshOutcome.SignedOut;
            var usesSomething = ConnectorFunctions.All.Any(f => s.Chose(f) && s.Offers(f));
            if (problem)            Badge(SettingsText.ConnectorStatusProblem,      last!.Message,                  good: false, warn: true);
            else if (!s.Connected)  Badge(SettingsText.ConnectorStatusNotConnected, null,                           good: false, warn: false);
            else if (!s.HasScope)   Badge(SettingsText.ConnectorStatusNotUsed,      SettingsText.ConnectorNoScope,  good: false, warn: true);
            else if (!usesSomething)
                Badge(SettingsText.ConnectorStatusNotUsed,
                      s.Functions.Count == 0 ? SettingsText.ConnectorNoFunctions : SettingsText.ConnectorTickedNotOffered, good: false, warn: true);
            else                    Badge(SettingsText.ConnectorStatusConnected,    null,                           good: true,  warn: false);

            // The panel: why it has a problem first, then what it gave, or why it gave nothing.
            var detail = source is not null
                ? string.Format(SettingsText.ConnectorLinkedLine, source.Accounts, source.Characters, source.ImportedAt.ToLocalTime().ToString("g"))
                : !s.Connected                                            ? ""
                : !s.HasScope                                             ? SettingsText.ConnectorNoScope
                : !s.Offers(ConnectorFunctions.LinkedCharacters)          ? SettingsText.ConnectorNothingOffered
                : !s.Chose(ConnectorFunctions.LinkedCharacters)           ? SettingsText.ConnectorLinkedNotChosen
                : "";
            Detail = string.Join("\n", new[] { problem ? last!.Message : "", detail }.Where(l => l.Length > 0));

            if (_pendingSaves == 0) RebuildOptions(s, scopes);
            else                    _rebuildSkipped = true;

            if (!IsBusy)
            {
                // The address of what is connected, always; otherwise what was last used, unless something is typed.
                if (s.Url is { } u && (s.Connected || Url.Length == 0)) Url = u.ToString();
                // Connected or disconnected from elsewhere: what was said here before no longer fits.
                if (wasConnected != s.Connected) Status = "";
            }
        }
        finally { _applying = false; }
    }

    private void Badge(string label, string? tip, bool good, bool warn)
    {
        StatusLabel = label; StatusTip = tip; IsGood = good; IsWarn = warn;
    }

    private void RebuildOptions(ConnectorSnapshot s, IReadOnlyList<ConnectorScopeChoice> scopes)
    {
        CorporationOptions.Clear();
        foreach (var c in scopes.Where(c => !c.IsAlliance))
            CorporationOptions.Add(new ConnectorScopeOptionViewModel(this, c, s.Corporations.Contains(c.Id)));
        AllianceOptions.Clear();
        foreach (var c in scopes.Where(c => c.IsAlliance))
            AllianceOptions.Add(new ConnectorScopeOptionViewModel(this, c, s.Alliances.Contains(c.Id)));
        HasCorporationOptions = CorporationOptions.Count > 0;
        HasAllianceOptions    = AllianceOptions.Count > 0;
        HasScopeOptions       = HasCorporationOptions || HasAllianceOptions;

        FunctionOptions.Clear();
        foreach (var f in ConnectorFunctions.All)
            FunctionOptions.Add(new ConnectorFunctionOptionViewModel(this, f, s.Connected, s.Offers(f), s.Chose(f)));
    }

    /// <summary>Every connector's choices in one string, so a change to any of them shows.</summary>
    internal static string ChoicesKey(IEnumerable<ConnectorSnapshot> snaps) =>
        string.Join(";", snaps.OrderBy(s => s.Id).Select(s =>
            $"{s.Id}:{string.Join(",", s.Corporations)}/{string.Join(",", s.Alliances)}/{string.Join(",", s.Functions)}"));

    /// <summary>A check box changed: saves the choices, or puts them back and says why not.</summary>
    internal void ChoiceChanged()
    {
        if (_applying) return;
        _pendingSaves++;
        _ = SaveChoicesAsync();
    }

    private async Task SaveChoicesAsync()
    {
        var refused = false;
        try
        {
            // One save at a time: two quick clicks never interleave. Each sends the boxes as they are when its
            // turn comes, so the last one carries every click.
            await _choiceGate.WaitAsync();
            try
            {
                ChoiceMessage = "";
                var result = await _connector.SaveChoicesAsync(Id,
                    CorporationOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList(),
                    AllianceOptions.Where(o => o.IsChecked).Select(o => o.Id).ToList(),
                    FunctionOptions.Where(o => o.IsChecked).Select(o => o.Function).ToList());
                if (!result.Ok)
                {
                    ChoiceMessage   = result.Message;
                    _messageChoices = null;
                    refused         = true;
                }
            }
            catch (Exception ex)
            {
                ChoiceMessage   = string.Format(CommonText.ErrorWithMessage, ex.Message);
                _messageChoices = null;
                refused         = true;
            }
            finally { _choiceGate.Release(); }
        }
        finally
        {
            // The last save done: the boxes show what is stored again — the refused tick taken back, and any
            // rebuild held back meanwhile done now, from a fresh read.
            if (--_pendingSaves == 0 && (refused || _rebuildSkipped))
            {
                _rebuildSkipped = false;
                await _reload();
            }
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        using var cts = new CancellationTokenSource(ConnectTimeout);
        _connectCts  = cts;
        IsBusy       = true;
        IsConnecting = true;
        Status = SettingsText.ConnectorStarting;
        try
        {
            // The code arrives before the browser opens, so it is on screen when the page asks about it.
            var result = await _connector.ConnectAsync(Id, Url,
                code => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = string.Format(SettingsText.ConnectorConfirmCode, code)),
                cts.Token);
            IsConnecting = false;
            await _reload();
            // ⚠️ By what is true now, not by the result alone: the key can be stored and something after it fail.
            Status = Unsaid(result.Message);
        }
        catch (OperationCanceledException)
        {
            // ⚠️ Cancelled, or timed out, perhaps after the key was already stored: what is true now is read
            // back before anything is said.
            IsConnecting = false;
            await _reload();
            Status = IsConnected ? "" : SettingsText.OnlineSignInCancelled;
        }
        catch (Exception ex)
        {
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally
        {
            _connectCts  = null;
            IsConnecting = false;
            IsBusy       = false;
        }
    }

    /// <summary>The message, or "" when the panel's detail already says it: the same line twice reads as two things.</summary>
    private string Unsaid(string message) =>
        message.Length == 0 || Detail.Split('\n').Contains(message) ? "" : message;

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            // The buttons are hidden while busy, and the sign-out can wait until the request times out.
            Status = SettingsText.ConnectorDisconnecting;
            await _connector.DisconnectAsync(Id);
            await _reload();
            Status = "";
        }
        catch (Exception ex)
        {
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshNowAsync()
    {
        IsBusy = true;
        try
        {
            Status = SettingsText.ConnectorRefreshing;
            var result = await _connector.RefreshAsync(Id);
            await _reload();
            Status = Unsaid(result.Message);
        }
        catch (Exception ex)
        {
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally { IsBusy = false; }
    }
}

/// <summary>A corporation or alliance a connector can be chosen for, as a check box.</summary>
public class ConnectorScopeOptionViewModel : ReactiveObject
{
    private readonly ConnectorEntryViewModel _owner;

    internal ConnectorScopeOptionViewModel(ConnectorEntryViewModel owner, ConnectorScopeChoice choice, bool isChecked)
    {
        _owner     = owner;
        IsAlliance = choice.IsAlliance;
        Id         = choice.Id;
        Display    = choice.OfOwnCharacter ? choice.Name : string.Format(SettingsText.ConnectorScopeNoCharacter, choice.Name);
        _isChecked = isChecked;
    }

    public bool   IsAlliance { get; }
    public int    Id         { get; }
    public string Display    { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; this.RaiseAndSetIfChanged(ref _isChecked, value); _owner.ChoiceChanged(); }
    }
}

/// <summary>A function a connector can be used for, as a check box. Only one it offers can be ticked; one ticked
/// before it stopped offering it can still be unticked. Before it is connected, nothing is known to be offered.</summary>
public class ConnectorFunctionOptionViewModel : ReactiveObject
{
    private readonly ConnectorEntryViewModel _owner;

    internal ConnectorFunctionOptionViewModel(ConnectorEntryViewModel owner, string function, bool connected, bool isOffered, bool isChecked)
    {
        _owner     = owner;
        Function   = function;
        Label      = ConnectorFunctions.Label(function);
        IsOffered  = isOffered;
        IsEnabled  = isOffered || isChecked;
        Tip        = isOffered ? null : connected ? SettingsText.ConnectorFnNotOffered : SettingsText.ConnectorFnConnectFirst;
        _isChecked = isChecked;
    }

    public string  Function  { get; }
    public string  Label     { get; }
    public bool    IsOffered { get; }
    public bool    IsEnabled { get; }
    public string? Tip       { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; this.RaiseAndSetIfChanged(ref _isChecked, value); _owner.ChoiceChanged(); }
    }
}
