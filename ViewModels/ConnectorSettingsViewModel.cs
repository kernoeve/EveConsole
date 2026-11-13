using System.Reactive;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Connector;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Settings → Connector: the address of the connector the user runs (or their corporation hosts), and
/// connecting to it with EVE's sign-in. Covers every client attached to this database.
/// </summary>
public class ConnectorSettingsViewModel : ReactiveObject
{
    private readonly ConnectorService     _connector;
    private readonly LinkedCharacterStore _store;

    // EVE's page sends nothing back if its tab is simply closed, so the wait is always bounded,
    // and can be abandoned.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _connectCts;

    public ConnectorSettingsViewModel(ConnectorService connector, LinkedCharacterStore store)
    {
        _connector = connector;
        _store     = store;

        // Another client, or the background refresh, connecting, disconnecting or storing links: an open
        // page follows. Never while this page is in the middle of something of its own; it reads back after.
        _connector.AccountChanged += Follow;
        _store.Changed            += Follow;

        ConnectCommand    = ReactiveCommand.CreateFromTask(ConnectAsync);
        CancelCommand     = ReactiveCommand.Create(() => { _connectCts?.Cancel(); });
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync);
        RefreshNowCommand = ReactiveCommand.CreateFromTask(RefreshNowAsync);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand    { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand     { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshNowCommand { get; }

    private void Follow() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!IsBusy) _ = LoadAsync(); });

    private string _url = "";
    public string Url { get => _url; set => this.RaiseAndSetIfChanged(ref _url, value); }

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
    /// <summary>Links are kept from a connector, connected or not: after it stopped knowing the key, only
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
    }

    private string _status = SettingsText.ConnectorNotConnected;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => this.RaiseAndSetIfChanged(ref _detail, value); }

    /// <summary>Reads the connection and what is stored again: the window opening, and after a change.
    /// A connection made from another client on this database turns up here. Asks the connector nothing.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var account = await _connector.AccountAsync();
            IsConnected = account is not null;
            // The address of what is connected, always; otherwise what was last used, unless something is typed.
            if (account is not null)                       Url = account.Url.ToString();
            else if (!IsConnecting && Url.Length == 0)     Url = await _connector.UrlAsync() ?? "";

            Status = account is null
                ? (IsConnecting ? Status : SettingsText.ConnectorNotConnected)
                : string.Format(SettingsText.ConnectorConnectedAs, account.ConnectorName, account.CharacterName);

            Detail = await DetailAsync(account);
        }
        catch (Exception ex)
        {
            // Nobody awaits this when the window opens, so the failure has to say so itself.
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
    }

    private async Task<string> DetailAsync(ConnectorAccount? account)
    {
        var lines = new List<string>();
        var sources = await _store.SourcesAsync();
        HasStoredLinks = sources.Count > 0;
        foreach (var source in sources)
            lines.Add(string.Format(SettingsText.ConnectorLinkedLine,
                source.Accounts, source.Characters, source.ImportedAt.ToLocalTime().ToString("g")));

        if (account is not null && lines.Count == 0
            && !account.Capabilities.Contains(ConnectorService.LinkedCharactersCapability))
            lines.Add(SettingsText.ConnectorNothingOffered);
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ConnectAsync()
    {
        using var cts = new CancellationTokenSource(ConnectTimeout);
        _connectCts  = cts;
        IsBusy       = true;
        IsConnecting = true;
        Status = SettingsText.OnlineWaitingForBrowser;
        try
        {
            var result = await _connector.ConnectAsync(Url, cts.Token);
            IsConnecting = false;
            await LoadAsync();
            // ⚠️ By what is true now, not by the result alone: the key can be stored and something after it fail.
            if (!IsConnected) Status = result.Message;
            else if (result.Message != SettingsText.ConnectorConnected) Status = $"{Status} {result.Message}";
        }
        catch (OperationCanceledException)
        {
            // ⚠️ Cancelled, or timed out, perhaps after the key was already stored: what is true now is read
            // back before anything is said.
            IsConnecting = false;
            await LoadAsync();
            if (!IsConnected) Status = SettingsText.OnlineSignInCancelled;
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

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _connector.DisconnectAsync();
            await LoadAsync();
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
            var result = await _connector.RefreshAsync();
            await LoadAsync();
            Status = IsConnected ? $"{Status} {result.Message}" : result.Message;
        }
        catch (Exception ex)
        {
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally { IsBusy = false; }
    }
}
