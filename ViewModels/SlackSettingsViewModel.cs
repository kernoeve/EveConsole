using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using EveConsole.Auth;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

// Settings for the Slack integration. The capsuleer creates a private app in their own workspace,
// installs it with User Token Scopes, and pastes the resulting xoxp- token here — so posts are
// attributed to them, and no client secret ships with EVE Console.
public class SlackSettingsViewModel : ReactiveObject
{
    public const string AppsUrl   = "https://api.slack.com/apps";
    public const string ScopesDoc = "https://docs.slack.dev/reference/scopes";

    private readonly SlackService _slack;

    public SlackSettingsViewModel(SlackService slack)
    {
        _slack  = slack;
        _token  = slack.Token ?? "";

        var savedName = slack.ChannelName(SlackService.AreaCorpTop10);
        var savedId   = slack.ChannelId(SlackService.AreaCorpTop10);
        if (!string.IsNullOrEmpty(savedId))
        {
            // Show the saved channel before (or without) loading the full list.
            _corpTop10Channel = new SlackChannel { Id = savedId, Name = savedName ?? savedId };
            Channels.Add(_corpTop10Channel);
        }

        var savedMsName = slack.ChannelName(SlackService.AreaCorpMonthly);
        var savedMsId   = slack.ChannelId(SlackService.AreaCorpMonthly);
        if (!string.IsNullOrEmpty(savedMsId))
        {
            _corpMonthlyChannel = new SlackChannel { Id = savedMsId, Name = savedMsName ?? savedMsId };
            Channels.Add(_corpMonthlyChannel);
        }

        var savedSpName = slack.ChannelName(SlackService.AreaSalePosting);
        var savedSpId   = slack.ChannelId(SlackService.AreaSalePosting);
        if (!string.IsNullOrEmpty(savedSpId))
        {
            _salePostingChannel = new SlackChannel { Id = savedSpId, Name = savedSpName ?? savedSpId };
            Channels.Add(_salePostingChannel);
        }

        AddWebhookCommand    = ReactiveCommand.CreateFromTask(AddWebhookAsync);
        RemoveWebhookCommand = ReactiveCommand.CreateFromTask<int>(RemoveWebhookAsync);

        // ⚠️ After the commands exist, and not awaited. The constructor cannot block on the
        // database, and the pickers are empty until it returns either way.
        _ = ReloadWebhooksAsync();

        SaveAndTestCommand   = ReactiveCommand.CreateFromTask(SaveAndTestAsync);
        LoadChannelsCommand  = ReactiveCommand.CreateFromTask(LoadChannelsAsync);
        OpenSlackAppsCommand = ReactiveCommand.Create(() => OpenUrl(AppsUrl));
        ConnectCommand       = ReactiveCommand.CreateFromTask(ConnectAsync);
        DisconnectCommand    = ReactiveCommand.CreateFromTask(DisconnectAsync);
        CancelConnectCommand = ReactiveCommand.Create(CancelConnect);

        IsConnected = slack.HasToken;
        if (IsConnected && slack.TeamName is { Length: > 0 } team)
            Status = $"Connected to {team}.";
    }

    /// <summary>True when this build has a Slack Client ID, so one-click connect is possible.</summary>
    public bool CanConnect => SlackAuthService.IsAvailable;

    /// <summary>Manual token entry is the fallback when no Client ID is compiled in.</summary>
    public bool ShowManualToken => !SlackAuthService.IsAvailable;

    public ReactiveCommand<Unit, Unit> ConnectCommand       { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand    { get; }
    public ReactiveCommand<Unit, Unit> CancelConnectCommand { get; }

    // Slack only redirects back if the user clicks Cancel on its page; closing the tab (or an
    // error page that doesn't redirect) sends nothing. So the wait is always bounded, and the
    // user can abandon it explicitly.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _connectCts;

    private bool _isConnecting;
    public bool IsConnecting { get => _isConnecting; private set => this.RaiseAndSetIfChanged(ref _isConnecting, value); }

    private async Task ConnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        var cts = new CancellationTokenSource(ConnectTimeout);
        _connectCts = cts;

        IsBusy = IsConnecting = true;
        Status = "Waiting for Slack authorization in your browser… (Cancel if you closed it)";
        try
        {
            var res = await _slack.ConnectAsync(cts.Token);
            IsConnected = res.Ok;
            Status = res.Ok
                ? $"Connected — posting as {res.User} in {res.Team}."
                : $"Failed: {res.Error}";
            if (res.Ok)
            {
                Token = _slack.Token ?? "";
                await LoadChannelsAsync();
            }
        }
        finally
        {
            IsBusy = IsConnecting = false;
            if (ReferenceEquals(_connectCts, cts)) _connectCts = null;
            cts.Dispose();
        }
    }

    private void CancelConnect()
    {
        _connectCts?.Cancel();
        Status = "Connection cancelled.";
    }

    private async Task DisconnectAsync()
    {
        await _slack.DisconnectAsync();
        Token       = "";
        IsConnected = false;
        Channels.Clear();
        _corpTop10Channel = null;
        this.RaisePropertyChanged(nameof(CorpTop10Channel));
        _corpMonthlyChannel = null;
        this.RaisePropertyChanged(nameof(CorpMonthlyChannel));
        _salePostingChannel = null;
        this.RaisePropertyChanged(nameof(SalePostingChannel));
        Status = "Disconnected.";
    }

    // ── Token ────────────────────────────────────────────────────────────────

    private string _token;
    public string Token
    {
        get => _token;
        set => this.RaiseAndSetIfChanged(ref _token, value);
    }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => this.RaiseAndSetIfChanged(ref _isConnected, value); }

    public ReactiveCommand<Unit, Unit> SaveAndTestCommand   { get; }
    public ReactiveCommand<Unit, Unit> LoadChannelsCommand  { get; }
    public ReactiveCommand<Unit, Unit> OpenSlackAppsCommand { get; }

    private async Task SaveAndTestAsync()
    {
        IsBusy = true;
        Status = "Checking token…";
        try
        {
            await _slack.SetTokenAsync(Token);

            if (string.IsNullOrWhiteSpace(Token))
            {
                IsConnected = false;
                Status      = "Token cleared.";
                return;
            }

            var res = await _slack.TestAuthAsync();
            IsConnected = res.Ok;
            Status = res.Ok
                ? $"Connected — posting as {res.User} in {res.Team}."
                : $"Failed: {res.Error}";

            if (res.Ok) await LoadChannelsAsync();
        }
        finally { IsBusy = false; }
    }

    // ── Channels ─────────────────────────────────────────────────────────────

    public ObservableCollection<SlackChannel> Channels { get; } = [];

    private SlackChannel? _corpTop10Channel;
    public SlackChannel? CorpTop10Channel
    {
        get => _corpTop10Channel;
        set
        {
            this.RaiseAndSetIfChanged(ref _corpTop10Channel, value);
            _ = _slack.SetChannelAsync(SlackService.AreaCorpTop10, value);
        }
    }

    private SlackChannel? _corpMonthlyChannel;
    public SlackChannel? CorpMonthlyChannel
    {
        get => _corpMonthlyChannel;
        set
        {
            this.RaiseAndSetIfChanged(ref _corpMonthlyChannel, value);
            _ = _slack.SetChannelAsync(SlackService.AreaCorpMonthly, value);
        }
    }

    private SlackChannel? _salePostingChannel;
    public SlackChannel? SalePostingChannel
    {
        get => _salePostingChannel;
        set
        {
            this.RaiseAndSetIfChanged(ref _salePostingChannel, value);
            _ = _slack.SetChannelAsync(SlackService.AreaSalePosting, value);
        }
    }

    // ── Webhooks ─────────────────────────────────────────────────────────────
    //
    // ⚠️ The reason these exist: a user token is granted by the workspace that issued it, and an
    // alliance will hand out an incoming webhook where it would never hand out a token for its
    // own Slack.
    //
    // ⚠️ A webhook cannot thread. Nothing posted through one comes back with a message id, so a
    // sale posting's detail arrives as a second message rather than a reply.

    /// <summary>The named webhooks, as the management grid shows them.</summary>
    public ObservableCollection<Models.SlackWebhook> Webhooks { get; } = [];

    /// <summary>⚠️ This section's own line. These messages were going to Status, which is the
    /// connection label under the Connect buttons — so "removed webhook" appeared where the
    /// workspace name belongs, and replaced it.</summary>
    private string _webhookStatus = "";
    public string WebhookStatus
    {
        get => _webhookStatus;
        private set => this.RaiseAndSetIfChanged(ref _webhookStatus, value);
    }

    private string _newWebhookName = "";
    public string NewWebhookName
    {
        get => _newWebhookName;
        set => this.RaiseAndSetIfChanged(ref _newWebhookName, value);
    }

    private string _newWebhookUrl = "";
    public string NewWebhookUrl
    {
        get => _newWebhookUrl;
        set => this.RaiseAndSetIfChanged(ref _newWebhookUrl, value);
    }

    public ReactiveCommand<Unit, Unit> AddWebhookCommand    { get; private set; } = null!;
    public ReactiveCommand<int,  Unit> RemoveWebhookCommand { get; private set; } = null!;

    private async Task AddWebhookAsync()
    {
        var name = NewWebhookName.Trim();
        var url  = NewWebhookUrl.Trim();

        // Both, and a URL that is actually one. A row with an empty name is unpickable in a
        // dropdown, and a row with an empty URL posts nowhere.
        if (name.Length == 0 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            WebhookStatus = "A webhook needs a name and an https:// URL.";
            return;
        }

        await _slack.AddWebhookAsync(name, url);
        NewWebhookName = "";
        NewWebhookUrl  = "";
        await ReloadWebhooksAsync();
        WebhookStatus = $"Added \"{name}\".";
    }

    /// <summary>
    /// Removes a webhook, unless something is still posting through it.
    ///
    /// <para>⚠️ Refused rather than cascaded. The URL is stored per area, so deleting the row it
    /// came from would leave those areas posting to a hook that is no longer listed — working,
    /// but with nothing on screen saying where. Clearing them silently is the other way to be
    /// surprising. Naming the areas lets the choice be made deliberately.</para>
    /// </summary>
    private async Task RemoveWebhookAsync(int id)
    {
        var hook = Webhooks.FirstOrDefault(w => w.Id == id);
        if (hook is null) return;

        var areas = new List<string>();
        if (InUseBy(SlackService.AreaCorpTop10,   hook)) areas.Add("Corp Top 10");
        if (InUseBy(SlackService.AreaCorpMonthly, hook)) areas.Add("Monthly Summary");
        if (InUseBy(SlackService.AreaSalePosting, hook)) areas.Add("Sale Posting");

        if (areas.Count > 0)
        {
            WebhookStatus = $"\"{hook.Name}\" is still set for {string.Join(", ", areas)}. "
                          + "Point those somewhere else first.";
            return;
        }

        await _slack.RemoveWebhookAsync(id);
        await ReloadWebhooksAsync();
        WebhookStatus = $"Removed \"{hook.Name}\".";
    }

    /// <summary>
    /// Whether an area posts through this webhook.
    ///
    /// <para>⚠️ Resolved the same way the sender resolves it, and for the same reason: an id
    /// where one is stored, and otherwise the URL written against the area before webhooks were
    /// named. Asking only about the id let a webhook every area was using be deleted, because a
    /// configuration made before this existed carries no id to compare.</para>
    ///
    /// <para>On the URL path a row cannot be distinguished from another carrying the same URL, so
    /// every one of them reads as in use. That is the safe direction: the alternative is deleting
    /// the row the sender was about to resolve to.</para>
    /// </summary>
    private bool InUseBy(string area, Models.SlackWebhook hook)
    {
        if (_slack.WebhookId(area) is int id) return id == hook.Id;

        var url = _slack.WebhookUrl(area);
        return url.Length > 0 && url == hook.Url;
    }

    private async Task ReloadWebhooksAsync()
    {
        var rows = await _slack.WebhooksAsync();
        _slack.InvalidateWebhooks();

        Webhooks.Clear();
        foreach (var w in rows) Webhooks.Add(w);

        RebuildDestinations();
    }

    // — Destinations —————————————————————————————————

    /// <summary>Channels and webhooks in one list, which is what every picker binds to.</summary>
    public ObservableCollection<SlackDestination> Destinations { get; } = [];

    /// <summary>
    /// Rebuilds the picker list and re-points each area at its current setting.
    ///
    /// <para>⚠️ The selections are re-resolved by value, not kept. These are new objects every
    /// time the list is rebuilt, so a ComboBox holding the old instance would show blank the
    /// moment channels were reloaded.</para>
    /// </summary>
    /// <summary>
    /// Set while the picker list is being replaced.
    ///
    /// <para>⚠️ Clearing the list makes every ComboBox bound to it push null into its
    /// SelectedItem, which lands in the setters below and looks exactly like somebody choosing
    /// the empty entry. Adding a webhook was silently clearing whichever channels happened to be
    /// selected — not always the same ones, because it depends on which pickers had resolved a
    /// selection by then.</para>
    /// </summary>
    private bool _rebuilding;

    private void RebuildDestinations()
    {
        _rebuilding = true;
        try
        {
        Destinations.Clear();

        foreach (var c in Channels)
            Destinations.Add(new SlackDestination(SlackDestination.KindChannel, c.Id, "#" + c.Name));

        foreach (var w in Webhooks)
            Destinations.Add(new SlackDestination(
                SlackDestination.KindWebhook, w.Id.ToString(), "Webhook: " + w.Name, w.Url));

        _corpTop10Dest    = Resolve(SlackService.AreaCorpTop10);
        _corpMonthlyDest  = Resolve(SlackService.AreaCorpMonthly);
        _salePostingDest  = Resolve(SlackService.AreaSalePosting);

        this.RaisePropertyChanged(nameof(CorpTop10Dest));
        this.RaisePropertyChanged(nameof(CorpMonthlyDest));
        this.RaisePropertyChanged(nameof(SalePostingDest));
        }
        finally { _rebuilding = false; }
    }

    /// <summary>What an area is set to now. A webhook URL wins where one is stored, because that
    /// is what the sender has always done with it.</summary>
    private SlackDestination? Resolve(string area)
    {
        if (_slack.WebhookId(area) is int hookId)
            return Destinations.FirstOrDefault(
                d => d.IsWebhook && d.Id == hookId.ToString());

        // Written before webhooks were named: a URL against the area and no id. Matched loosely
        // because that is all there is to match on.
        var url = _slack.WebhookUrl(area);
        if (url.Length > 0)
            return Destinations.FirstOrDefault(d => d.IsWebhook && d.Url == url);

        var id = _slack.ChannelId(area);
        return id is null ? null
             : Destinations.FirstOrDefault(d => !d.IsWebhook && d.Id == id);
    }

    /// <summary>
    /// Points an area at one destination, and clears the other kind.
    ///
    /// <para>⚠️ Both settings are written on every change. Leaving the old webhook URL in place
    /// while setting a channel would have the sender keep using the webhook, and the picker would
    /// then be showing a channel the post never reaches.</para>
    /// </summary>
    private async Task SetDestinationAsync(string area, SlackDestination? dest)
    {
        // The legacy per-area URL is cleared in every branch. Left behind it would keep winning
        // in WebhookUrl for a configuration written before webhooks were named.
        if (dest is null)
        {
            await _slack.SetChannelAsync(area, null);
            await _slack.SetWebhookIdAsync(area, null);
            await _slack.SetWebhookUrlAsync(area, "");
            return;
        }

        if (dest.IsWebhook)
        {
            await _slack.SetChannelAsync(area, null);
            await _slack.SetWebhookUrlAsync(area, "");
            await _slack.SetWebhookIdAsync(
                area, int.TryParse(dest.Id, out var hookId) ? hookId : null);
            return;
        }

        await _slack.SetWebhookIdAsync(area, null);
        await _slack.SetWebhookUrlAsync(area, "");
        await _slack.SetChannelAsync(area, Channels.FirstOrDefault(c => c.Id == dest.Id));
    }

    private SlackDestination? _corpTop10Dest;
    public SlackDestination? CorpTop10Dest
    {
        get => _corpTop10Dest;
        set { this.RaiseAndSetIfChanged(ref _corpTop10Dest, value);
              if (!_rebuilding) _ = SetDestinationAsync(SlackService.AreaCorpTop10, value); }
    }

    private SlackDestination? _corpMonthlyDest;
    public SlackDestination? CorpMonthlyDest
    {
        get => _corpMonthlyDest;
        set { this.RaiseAndSetIfChanged(ref _corpMonthlyDest, value);
              if (!_rebuilding) _ = SetDestinationAsync(SlackService.AreaCorpMonthly, value); }
    }

    private SlackDestination? _salePostingDest;
    public SlackDestination? SalePostingDest
    {
        get => _salePostingDest;
        set { this.RaiseAndSetIfChanged(ref _salePostingDest, value);
              if (!_rebuilding) _ = SetDestinationAsync(SlackService.AreaSalePosting, value); }
    }

    private async Task LoadChannelsAsync()
    {
        if (!_slack.HasToken) { Status = "Enter a token first."; return; }

        IsBusy = true;
        try
        {
            var (channels, error) = await _slack.ListChannelsAsync();
            if (error is not null) { Status = $"Could not load channels: {error}"; return; }

            // Keep the current selections by id — the list is rebuilt from Slack each time.
            var selectedId   = _corpTop10Channel?.Id;
            var selectedMsId = _corpMonthlyChannel?.Id;
            var selectedSpId = _salePostingChannel?.Id;
            Channels.Clear();
            foreach (var c in channels) Channels.Add(c);

            if (selectedId is not null)
            {
                var match = Channels.FirstOrDefault(c => c.Id == selectedId);
                if (match is not null)
                {
                    _corpTop10Channel = match;
                    this.RaisePropertyChanged(nameof(CorpTop10Channel));
                }
            }
            if (selectedMsId is not null)
            {
                var match = Channels.FirstOrDefault(c => c.Id == selectedMsId);
                if (match is not null)
                {
                    _corpMonthlyChannel = match;
                    this.RaisePropertyChanged(nameof(CorpMonthlyChannel));
                }
            }
            if (selectedSpId is not null)
            {
                var match = Channels.FirstOrDefault(c => c.Id == selectedSpId);
                if (match is not null)
                {
                    _salePostingChannel = match;
                    this.RaisePropertyChanged(nameof(SalePostingChannel));
                }
            }
            // The pickers list channels and webhooks together, so a channel reload rebuilds both.
            RebuildDestinations();

            Status = $"{Channels.Count:N0} channel(s) available.";
        }
        finally { IsBusy = false; }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing sensible to do if no browser is available */ }
    }
}
