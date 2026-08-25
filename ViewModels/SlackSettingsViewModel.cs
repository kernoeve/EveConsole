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

        _top10Webhook       = slack.WebhookUrl(SlackService.AreaCorpTop10);
        _monthlyWebhook     = slack.WebhookUrl(SlackService.AreaCorpMonthly);
        _salePostingWebhook = slack.WebhookUrl(SlackService.AreaSalePosting);

        TestWebhooksCommand  = ReactiveCommand.CreateFromTask(TestWebhooksAsync);
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

    public ReactiveCommand<Unit, Unit> TestWebhooksCommand  { get; }
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
    // own Slack. A webhook is a URL bound to one channel by whoever made it, so setting one here
    // overrides the channel picker above for that area — the URL already decides where it lands.
    //
    // ⚠️ It also cannot thread. Nothing posted through a webhook comes back with a message id, so
    // a sale posting's detail arrives as a second message rather than a reply.

    private string _top10Webhook = "";
    public string Top10Webhook
    {
        get => _top10Webhook;
        set { this.RaiseAndSetIfChanged(ref _top10Webhook, value);
              _ = _slack.SetWebhookUrlAsync(SlackService.AreaCorpTop10, value); }
    }

    private string _monthlyWebhook = "";
    public string MonthlyWebhook
    {
        get => _monthlyWebhook;
        set { this.RaiseAndSetIfChanged(ref _monthlyWebhook, value);
              _ = _slack.SetWebhookUrlAsync(SlackService.AreaCorpMonthly, value); }
    }

    private string _salePostingWebhook = "";
    public string SalePostingWebhook
    {
        get => _salePostingWebhook;
        set { this.RaiseAndSetIfChanged(ref _salePostingWebhook, value);
              _ = _slack.SetWebhookUrlAsync(SlackService.AreaSalePosting, value); }
    }

    /// <summary>Sends a line to each configured webhook, so a bad URL is found here and not in
    /// the middle of posting a price list.</summary>
    private async Task TestWebhooksAsync()
    {
        var any = false;
        foreach (var (area, url, label) in new[]
                 {
                     (SlackService.AreaCorpTop10,   Top10Webhook,       "Top 10"),
                     (SlackService.AreaCorpMonthly, MonthlyWebhook,     "Monthly summary"),
                     (SlackService.AreaSalePosting, SalePostingWebhook, "Sale posting"),
                 })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            any = true;

            var res = await _slack.PostWebhookAsync(
                url.Trim(), $"EVE Console webhook test — {label}.");

            if (!res.Ok) { Status = $"{label} webhook failed: {res.Error}"; return; }
        }

        Status = any ? "Webhook test posted." : "No webhook URLs to test.";
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
