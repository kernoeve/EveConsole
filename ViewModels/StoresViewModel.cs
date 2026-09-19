using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.WebStore;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// One store in the list on the left.
///
/// <para>⚠️ Updated in place rather than replaced. The settings panel writes through as the user
/// types, and swapping the row object on each save would rebuild the list item and drop the
/// selection out from under them — mid-word, on every character.</para>
/// </summary>
public class StoreRowVm : ReactiveObject
{
    private Store _model;

    public StoreRowVm(Store model) => _model = model;

    public Store Model => _model;
    public int   Id    => _model.Id;

    public string Name          => _model.Name.Length > 0 ? _model.Name : "(unnamed)";
    public string CharacterName => _model.CharacterName;
    public bool   Enabled       => _model.Enabled;
    public bool   WebEnabled    => _model.WebEnabled;

    /// <summary>Which channels are open, said plainly — the list is the first place someone
    /// looks to find out why a buyer got no answer.</summary>
    public string StateText => (_model.Enabled, _model.WebEnabled) switch
    {
        (true,  true)  => "Mail and web open",
        (true,  false) => "Mail open",
        (false, true)  => "Web open",
        _              => "Closed",
    };

    public void Refresh(Store model)
    {
        _model = model;
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(CharacterName));
        this.RaisePropertyChanged(nameof(Enabled));
        this.RaisePropertyChanged(nameof(WebEnabled));
        this.RaisePropertyChanged(nameof(StateText));
    }
}

/// <summary>One message in the log.</summary>
public class StoreMailRowVm(StoreMail m)
{
    public string When      => m.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Direction => m.Direction == "in" ? "Received" : "Sent";
    public string Party     => m.PartyName.Length > 0 ? m.PartyName : m.PartyId.ToString();
    public string Command   => m.Command;
    public string Subject   => m.Subject;
    public string Outcome   => m.Outcome;
    public string Detail    => m.Detail;
    public string OrderRef  => m.OrderRef;
    public string Body      => StoreMailService.Strip(m.Body).Trim();

    /// <summary>Rejections and failures are the rows worth finding, so they say so rather than
    /// relying on the reader to notice a word in a column.</summary>
    public bool IsProblem => m.Outcome is "rejected" or "error" or "failed";
}

/// <summary>One thing the web site sent, and what the app did with it.</summary>
public class StoreWebEventRowVm(StoreWebEvent e)
{
    public int    Id       => e.Id;
    public string When     => e.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Kind     => e.Kind switch { "order" => "Order", "cancel" => "Cancel", _ => e.Kind };
    public string Buyer    => e.BuyerName.Length > 0 ? e.BuyerName : e.BuyerId.ToString();
    public string Outcome  => e.Outcome switch
    {
        "booked"   => "Booked",
        "applied"  => "Applied",
        "review"   => "Needs a decision",
        "rejected" => "Declined",
        "error"    => "Failed",
        _          => e.Outcome,
    };
    public string Detail   => e.Detail;
    public string OrderRef => e.OrderRef;

    /// <summary>Waiting on the owner: an order the checks would not book on their own.</summary>
    public bool IsHeld     => e.Kind == "order" && e.Outcome is "review" or "error";
    public bool IsProblem  => e.Outcome is "review" or "rejected" or "error";
}

/// <summary>One allow-list entry.</summary>
public class StoreSenderRowVm(StoreSender s)
{
    public int    Id   => s.Id;
    public string Name => s.Name.Length > 0 ? s.Name : s.EntityId.ToString();
    public string Kind => s.EntityType switch
    {
        "corporation" => "Corporation",
        "alliance"    => "Alliance",
        _             => "Character",
    };
}

/// <summary>
/// The shop front: which stores exist, who may write to them, and everything that has been said.
///
/// <para>Deliberately not a place where an order is edited. Orders live in the Order Tracker and
/// always have — a store order is an ordinary tracked order with a reference on it — and a second
/// screen that edited them would be a second set of rules about what an order is.</para>
/// </summary>
public class StoresViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SalePostingService              _postings;
    private readonly StoreMailService                _storeMail;
    private readonly OrderLabelService               _labels;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly WebStoreSyncService             _webSync;
    private readonly WorkerLease                     _lease;
    private readonly CloudflareDeployService         _deploy;


    public ObservableCollection<StoreRowVm>          Stores    { get; } = [];
    public ObservableCollection<StoreMailRowVm>      Mails     { get; } = [];
    public ObservableCollection<OrderSummaryRowVm>   Orders    { get; } = [];
    public ObservableCollection<StoreSenderRowVm>    Senders   { get; } = [];
    public ObservableCollection<StoreWebEventRowVm>  WebEvents { get; } = [];

    /// <summary>The app's themes, for the web site — the store's own choice, nothing to do
    /// with the theme this desktop wears.</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        WebThemes.All.Select(t => new ThemeOption(t.Key, t.Name)).ToList();

    public sealed record ThemeOption(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Characters we hold a token for — the only ones that can be a shop's address.</summary>
    public ObservableCollection<CharacterOption> CharacterOptions { get; } = [];
    public ObservableCollection<PostingOption>   PostingOptions   { get; } = [];

    public IReadOnlyList<string> PolicyOptions { get; } = ["List", "Anyone"];

    public sealed record CharacterOption(long Id, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record PostingOption(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    public StoresViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        SalePostingService              postings,
        StoreMailService                storeMail,
        OrderLabelService               labels,
        AppErrorLogger                  errorLogger,
        WebStoreSyncService             webSync,
        WorkerLease                     lease,
        CloudflareDeployService         deploy)

    {
        _dbFactory   = dbFactory;
        _postings    = postings;
        _storeMail   = storeMail;
        _labels      = labels;
        _errorLogger = errorLogger;
        _webSync     = webSync;
        _lease       = lease;
        _deploy      = deploy;


        AddStoreCommand    = ReactiveCommand.CreateFromTask(AddStoreAsync);
        DeleteStoreCommand = ReactiveCommand.CreateFromTask(DeleteStoreAsync);
        RefreshCommand     = ReactiveCommand.CreateFromTask(LoadAsync);
        CheckMailCommand   = ReactiveCommand.CreateFromTask(CheckMailNowAsync);
        AddSenderCommand   = ReactiveCommand.CreateFromTask(AddSenderAsync);
        SyncWebNowCommand  = ReactiveCommand.CreateFromTask(SyncWebNowAsync);
        NewSecretCommand   = ReactiveCommand.CreateFromTask(NewSecretAsync);
        SaveCloudflareTokenCommand   = ReactiveCommand.CreateFromTask(SaveCloudflareTokenAsync);
        ForgetCloudflareTokenCommand = ReactiveCommand.Create(ForgetCloudflareToken);
        DeploySiteCommand            = ReactiveCommand.CreateFromTask(DeploySiteAsync);
        CheckSiteCommand             = ReactiveCommand.CreateFromTask(CheckSiteAsync);
        RefreshTokenText();


        foreach (var c in new[] { AddStoreCommand, DeleteStoreCommand, RefreshCommand,
                                  CheckMailCommand, AddSenderCommand, SyncWebNowCommand, NewSecretCommand,
                                  SaveCloudflareTokenCommand, ForgetCloudflareTokenCommand, DeploySiteCommand, CheckSiteCommand })

            c.ThrownExceptions.Subscribe(ex => errorLogger.Log(nameof(StoresViewModel), "command", ex));

        this.WhenAnyValue(x => x.SelectedStore)
            .Skip(1)
            .SubscribeAsyncSafe(_ => LoadSelectedAsync(), errorLogger, "Stores.SelectStore");

        // The log is the only sign the shop is doing anything, and it changes without anyone
        // touching this screen.
        Observable.Interval(TimeSpan.FromSeconds(30))
            .ObserveOnUi("Stores.AutoRefresh")
            .SubscribeAsyncSafe(_ => LoadSelectedAsync(), errorLogger, "Stores.AutoRefresh");

        _ = LoadAsync();
    }

    public ReactiveCommand<Unit, Unit> AddStoreCommand    { get; }
    public ReactiveCommand<Unit, Unit> DeleteStoreCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand     { get; }
    public ReactiveCommand<Unit, Unit> CheckMailCommand   { get; }
    public ReactiveCommand<Unit, Unit> AddSenderCommand   { get; }
    public ReactiveCommand<Unit, Unit> SyncWebNowCommand  { get; }
    public ReactiveCommand<Unit, Unit> SaveCloudflareTokenCommand   { get; }
    public ReactiveCommand<Unit, Unit> ForgetCloudflareTokenCommand { get; }
    public ReactiveCommand<Unit, Unit> DeploySiteCommand            { get; }
    public ReactiveCommand<Unit, Unit> CheckSiteCommand             { get; }

    public ReactiveCommand<Unit, Unit> NewSecretCommand   { get; }

    private StoreRowVm? _selectedStore;
    public StoreRowVm? SelectedStore
    {
        get => _selectedStore;
        set => this.RaiseAndSetIfChanged(ref _selectedStore, value);
    }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public bool HasSelection => SelectedStore is not null;

    // ── Editable settings for the selected store ──────────────────────────────
    //
    // Written straight through on change. A shop has four settings and a Save button would be one
    // more thing to forget; the one that matters — Enabled — is the switch that makes it live, so
    // it is better applied the moment it is flipped than left pending.

    private string _storeName = "";
    public string StoreName
    {
        get => _storeName;
        set { this.RaiseAndSetIfChanged(ref _storeName, value); _ = SaveAsync(s => s.Name = value?.Trim() ?? ""); }
    }

    private CharacterOption? _storeCharacter;
    public CharacterOption? StoreCharacter
    {
        get => _storeCharacter;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeCharacter, value);
            if (value is null) return;
            _ = SaveAsync(s => { s.CharacterId = value.Id; s.CharacterName = value.Name; });
        }
    }

    private PostingOption? _storePosting;
    public PostingOption? StorePosting
    {
        get => _storePosting;
        set
        {
            this.RaiseAndSetIfChanged(ref _storePosting, value);
            _ = MeasurePostingAsync();
            if (value is null) return;
            _ = SaveAsync(s => s.PostingId = value.Id);
        }
    }

    private string _senderPolicy = "List";
    public string SenderPolicy
    {
        get => _senderPolicy;
        set { this.RaiseAndSetIfChanged(ref _senderPolicy, value); _ = SaveAsync(s => s.SenderPolicy = value); }
    }

    private bool _storeEnabled;
    public bool StoreEnabled
    {
        get => _storeEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeEnabled, value);
            _ = SaveAsync(s =>
            {
                s.Enabled = value;
                // ⚠️ The listening mark moves forward every time the shop opens. Without this,
                // reopening after a week would answer the week's backlog at once — real mail, to
                // real people, that cannot be recalled.
                if (value) s.ListenFrom = DateTimeOffset.UtcNow;
            });
        }
    }

    private string _postingSizeText = "";

    /// <summary>The rendered price list's size against what one mail can carry.</summary>
    public string PostingSizeText
    {
        get => _postingSizeText;
        private set => this.RaiseAndSetIfChanged(ref _postingSizeText, value);
    }

    private bool _postingSplits;

    /// <summary>True when the price list would arrive as more than one mail.</summary>
    public bool PostingSplits
    {
        get => _postingSplits;
        private set => this.RaiseAndSetIfChanged(ref _postingSplits, value);
    }

    /// <summary>
    /// Measures the price list this store would send.
    ///
    /// <para>Shown while the posting is being edited, because the alternative is finding out from
    /// a buyer who received two mails. The figure is in bytes rather than characters: that is the
    /// unit the limit is enforced in, and a posting full of × and — weighs more than it reads.</para>
    /// </summary>
    private async Task MeasurePostingAsync()
    {
        if (SelectedStore is not StoreRowVm row) { Clear(); return; }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == row.Id);
            if (store is null) { Clear(); return; }

            var size = await _storeMail.MeasurePriceListAsync(store);
            if (size is null) { Clear(); return; }

            var text = size.Splits
                ? $"⚠  Price list is {size.Bytes:N0} of {size.Limit:N0} bytes — "
                + $"{size.Over:N0} over, so it will arrive as {size.Parts} mails. "
                + "Shorten the posting to send it as one."
                : $"Price list is {size.Bytes:N0} of {size.Limit:N0} bytes — fits in one mail "
                + $"with {size.Limit - size.Bytes:N0} to spare.";

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PostingSizeText = text;
                PostingSplits   = size.Splits;
            });
        }
        catch (Exception ex)
        {
            _errorLogger.Log("StoresViewModel", "MeasurePosting", ex);
            Clear();
        }

        void Clear() => Dispatcher.UIThread.Post(() =>
        {
            PostingSizeText = "";
            PostingSplits   = false;
        });
    }

    private readonly ObservableCollection<string> _knownLabels = [];

    /// <summary>What the label box offers: every label already in use anywhere, plus the ones
    /// other stores are set to apply.</summary>
    public ObservableCollection<string> KnownLabels => _knownLabels;

    private bool _useCustomUsage;
    public bool UseCustomUsage
    {
        get => _useCustomUsage;
        set
        {
            this.RaiseAndSetIfChanged(ref _useCustomUsage, value);
            _ = SaveAsync(s => s.UseCustomUsage = value);

            // Turning it on with nothing written puts the stock message in the box. Starting from
            // an empty field means rebuilding the markup, the links and the command list from
            // nothing, when the point is usually to change two paragraphs of it.
            if (value && CustomUsage.Trim().Length == 0) CustomUsage = DefaultUsageText();
        }
    }

    private string _customUsage = "";
    public string CustomUsage
    {
        get => _customUsage;
        set
        {
            this.RaiseAndSetIfChanged(ref _customUsage, value);
            _ = SaveAsync(s => s.CustomUsage = value ?? "");
        }
    }

    /// <summary>Puts the stock message back in the box, discarding what was written.</summary>
    public void ResetUsage() => CustomUsage = DefaultUsageText();

    private string _storeInfo = "";

    /// <summary>
    /// What INFO sends. Empty means the command is not offered at all — there is no flag, the
    /// text is the flag.
    /// </summary>
    public string StoreInfo
    {
        get => _storeInfo;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeInfo, value ?? "");
            _ = SaveAsync(s => s.Info = value ?? "");
        }
    }

    /// <summary>
    /// The stock usage message for the selected store, exactly as a buyer would receive it.
    ///
    /// <para>⚠️ Built from the real generator rather than a copy kept here. Two versions of the
    /// same message drift, and the one in the box would quietly become the one nobody sends.</para>
    /// </summary>
    private string DefaultUsageText()
    {
        if (SelectedStore is not StoreRowVm row) return "";

        return StoreMailService.DefaultUsageForEditing(new Store
        {
            Id            = row.Id,
            Name          = StoreName,
            MessageHeader = MessageHeader,
            MessageFooter = MessageFooter,
        });
    }

    private string _storeOrderLabels = "";
    public string StoreOrderLabels
    {
        get => _storeOrderLabels;
        set
        {
            this.RaiseAndSetIfChanged(ref _storeOrderLabels, value);
            _ = SaveAsync(s => s.OrderLabels =
                string.Join(", ", OrderLabelService.Split(value)));
        }
    }

    private string _messageHeader = "";
    public string MessageHeader
    {
        get => _messageHeader;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageHeader, value);
            _ = SaveAsync(s => s.MessageHeader = value?.Trim() ?? "");
        }
    }

    private string _messageHeaderColor = "";
    public string MessageHeaderColor
    {
        get => _messageHeaderColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageHeaderColor, value);
            _ = SaveAsync(s => s.MessageHeaderColor = value?.Trim() ?? "");
        }
    }

    private string _messageFooter = "";
    public string MessageFooter
    {
        get => _messageFooter;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageFooter, value);
            _ = SaveAsync(s => s.MessageFooter = value?.Trim() ?? "");
        }
    }

    private string _messageFooterColor = "";
    public string MessageFooterColor
    {
        get => _messageFooterColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageFooterColor, value);
            _ = SaveAsync(s => s.MessageFooterColor = value?.Trim() ?? "");
        }
    }

    private bool _autoEstimate = true;
    public bool AutoEstimate
    {
        get => _autoEstimate;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoEstimate, value);
            _ = SaveAsync(s => s.AutoEstimateInStock = value);
        }
    }

    private int _autoEstimateDays = 1;
    public int AutoEstimateDays
    {
        get => _autoEstimateDays;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoEstimateDays, value);
            _ = SaveAsync(s => s.AutoEstimateDays = Math.Max(0, value));
        }
    }

    // ── The web channel ───────────────────────────────────────────────────────
    //
    // Written through like everything else here, and each save nudges the sync loop so the
    // site shows the change on its next call rather than at the next interval.

    private bool _webEnabled;
    public bool WebEnabled
    {
        get => _webEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _webEnabled, value);
            _ = SaveAsync(s => s.WebEnabled = value, nudge: true);
            RefreshSsoWarning();

        }
    }

    private string _webUrl = "";
    public string WebUrl
    {
        get => _webUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _webUrl, value ?? "");
            _ = SaveAsync(s => s.WebUrl = (value ?? "").Trim().TrimEnd('/'), nudge: true);
            RefreshCallbackText();

        }
    }

    private string _webSecret = "";

    /// <summary>Shown so it can be copied into the site's secrets by hand; generated here.</summary>
    public string WebSecret
    {
        get => _webSecret;
        private set => this.RaiseAndSetIfChanged(ref _webSecret, value);
    }

    private ThemeOption? _webTheme;
    public ThemeOption? WebTheme
    {
        get => _webTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _webTheme, value);
            if (value is null) return;
            _ = SaveAsync(s => s.WebTheme = value.Key, nudge: true);
        }
    }

    private bool _webBuyerMaySwitch = true;
    public bool WebBuyerMaySwitch
    {
        get => _webBuyerMaySwitch;
        set
        {
            this.RaiseAndSetIfChanged(ref _webBuyerMaySwitch, value);
            _ = SaveAsync(s => s.WebBuyerMaySwitch = value, nudge: true);
        }
    }

    private bool _webMailUpdates = true;
    public bool WebMailUpdates
    {
        get => _webMailUpdates;
        set
        {
            this.RaiseAndSetIfChanged(ref _webMailUpdates, value);
            _ = SaveAsync(s => s.WebMailUpdates = value, nudge: true);
        }
    }

    private string _webBlurb = "";
    public string WebBlurb
    {
        get => _webBlurb;
        set
        {
            this.RaiseAndSetIfChanged(ref _webBlurb, value ?? "");
            _ = SaveAsync(s => s.WebBlurb = (value ?? "").Trim(), nudge: true);
        }
    }

    private string _webStatusText = "";

    /// <summary>When the site was last reached, what it runs, and why not if not.</summary>
    public string WebStatusText
    {
        get => _webStatusText;
        private set => this.RaiseAndSetIfChanged(ref _webStatusText, value);
    }

    private bool _webHasError;
    public bool WebHasError
    {
        get => _webHasError;
        private set => this.RaiseAndSetIfChanged(ref _webHasError, value);
    }

    private static string DescribeWeb(Store s)
    {
        if (!s.WebEnabled) return "The web channel is closed.";
        if (s.WebUrl.Length == 0 || s.WebSecret.Length == 0) return "Needs a site address and a secret before it can sync.";
        var last = s.WebLastSyncAt is { } t ? $"Last synced {t.ToLocalTime():yyyy-MM-dd HH:mm}" : "Not synced yet";
        var ver  = s.WebSiteVersion.Length > 0 ? $", site version {s.WebSiteVersion}" : "";
        return s.WebLastError.Length > 0 ? $"{last}{ver}. ⚠ {s.WebLastError}" : $"{last}{ver}.";
    }

    /// <summary>
    /// Pushes and pulls this store now, on this client.
    ///
    /// <para>⚠️ Only from the client holding the worker lease. The loop there keeps the cursor
    /// and the ledger; a second client syncing the same store would race it over both.</para>
    /// </summary>
    private async Task SyncWebNowAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;
        if (!_lease.IsHolder)
        {
            Status = "Another client is running the background work and syncs the web site; it will pick the change up on its next cycle.";
            _webSync.Nudge();
            return;
        }

        Status = "Syncing the web site…";
        var line = await _webSync.SyncStoreNowAsync(row.Id);
        await LoadSelectedAsync();
        Status = line;
    }

    /// <summary>A fresh secret. The site keeps working only once the new one is set there too.</summary>
    private async Task NewSecretAsync()
    {
        var secret = WebStoreSigner.NewSecret();
        await SaveAsync(s => s.WebSecret = secret);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            WebSecret = secret;
            Status    = "New secret generated. Set the same value on the site, or the next sync is refused.";
        });
    }

    public async Task ApproveWebEventAsync(StoreWebEventRowVm row)
    {
        Status = await _webSync.ApproveAsync(row.Id);
        await LoadSelectedAsync();
    }

    public async Task DeclineWebEventAsync(StoreWebEventRowVm row)
    {
        Status = await _webSync.RejectAsync(row.Id, "Declined by the store.");
        await LoadSelectedAsync();
    }

    // ── Hosting on Cloudflare ─────────────────────────────────────────────────
    //
    // The token is this machine's (AppConfig); the account, worker name and EVE application id
    // are the store's; the EVE application's secret key passes through once, to the site.

    public sealed record AccountOption(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public ObservableCollection<AccountOption> CloudflareAccounts { get; } = [];

    private IReadOnlyDictionary<string, string> _subdomains = new Dictionary<string, string>();

    private string _cloudflareToken = "";

    /// <summary>Typed here and saved by the button; never read back out of the machine's store.</summary>
    public string CloudflareToken
    {
        get => _cloudflareToken;
        set => this.RaiseAndSetIfChanged(ref _cloudflareToken, value ?? "");
    }

    private string _cloudflareTokenText = "";
    public string CloudflareTokenText
    {
        get => _cloudflareTokenText;
        private set => this.RaiseAndSetIfChanged(ref _cloudflareTokenText, value);
    }

    private AccountOption? _cloudflareAccount;
    public AccountOption? CloudflareAccount
    {
        get => _cloudflareAccount;
        set
        {
            this.RaiseAndSetIfChanged(ref _cloudflareAccount, value);
            if (value is not null) _ = SaveAsync(s => s.WebCloudflareAccountId = value.Id);
            RefreshCallbackText();
        }
    }

    private string _webWorkerName = "";
    public string WebWorkerName
    {
        get => _webWorkerName;
        set
        {
            var name = (value ?? "").Trim().ToLowerInvariant();
            this.RaiseAndSetIfChanged(ref _webWorkerName, name);
            _ = SaveAsync(s => s.WebWorkerName = name);
            RefreshCallbackText();
        }
    }

    private string _webEveClientId = "";
    public string WebEveClientId
    {
        get => _webEveClientId;
        set
        {
            this.RaiseAndSetIfChanged(ref _webEveClientId, value ?? "");
            _ = SaveEveKeyAsync(s => s.WebEveClientId = (value ?? "").Trim());
            RefreshSsoWarning();


        }
    }

    private string _webEveClientSecret = "";

    /// <summary>Kept with the store, like the sync secret: the Deploy button places it on the site each time.</summary>
    public string WebEveClientSecret
    {
        get => _webEveClientSecret;
        set
        {
            this.RaiseAndSetIfChanged(ref _webEveClientSecret, value ?? "");
            _ = SaveEveKeyAsync(s => s.WebEveClientSecret = (value ?? "").Trim());
            RefreshSsoWarning();

        }
    }


    private string _webCallbackUrl = "";

    /// <summary>The callback the EVE application must be registered with, once the address is known.</summary>
    public string WebCallbackUrl
    {
        get => _webCallbackUrl;
        private set => this.RaiseAndSetIfChanged(ref _webCallbackUrl, value);
    }

    private bool _webSsoMissing;

    /// <summary>Red on the screen: the web channel is open and the site has no way to sign anyone in.</summary>
    public bool WebSsoMissing
    {
        get => _webSsoMissing;
        private set => this.RaiseAndSetIfChanged(ref _webSsoMissing, value);
    }

    public string WebSsoWarningText =>
        "Buyers cannot sign in until the site has the EVE application's Client ID and Secret Key. Enter them here: "
        + "the Deploy button places them on the site, and a site set up by hand needs the same values as its "
        + "EVE_CLIENT_ID and EVE_CLIENT_SECRET secrets.";


    private string _deployStatusText = "";
    public string DeployStatusText
    {
        get => _deployStatusText;
        private set => this.RaiseAndSetIfChanged(ref _deployStatusText, value);
    }

    private void RefreshTokenText()
    {
        CloudflareTokenText = AppConfig.HasCloudflareToken
            ? AppConfig.CloudflareTokenProtection switch
            {
                SecretProtection.Dpapi     => "A token is saved on this machine, encrypted by Windows for your account.",
                SecretProtection.LibSecret => "A token is saved in this machine's keyring.",
                _                          => "A token is saved on this machine in config.json as typed; no keyring was available.",
            }
            : "No token is saved on this machine. Deploying and updating need one; syncing does not.";
    }

    /// <summary>The callback address, from the site's address or from where a deploy would put it.</summary>
    private void RefreshCallbackText()
    {
        var url = _webUrl.Length > 0 ? _webUrl.TrimEnd('/')
            : CloudflareAccount is { } a && _subdomains.TryGetValue(a.Id, out var sub) && _webWorkerName.Length > 0
                ? $"https://{_webWorkerName}.{sub}.workers.dev"
                : "";
        WebCallbackUrl = url.Length > 0 ? url + "/auth/callback" : "";
    }

    private void RefreshSsoWarning() =>
        WebSsoMissing = _webEnabled && (_webEveClientId.Trim().Length == 0 || _webEveClientSecret.Trim().Length == 0);


    /// <summary>The option for a saved account id — the listed one, or a stand-in until the token is checked again.</summary>
    private AccountOption? AccountFor(string id)
    {
        if (id.Length == 0) return null;
        var known = CloudflareAccounts.FirstOrDefault(a => a.Id == id);
        if (known is not null) return known;
        var placeholder = new AccountOption(id, $"Account {id[..Math.Min(8, id.Length)]}…");
        CloudflareAccounts.Add(placeholder);
        return placeholder;
    }

    private async Task SaveCloudflareTokenAsync()
    {
        var token = CloudflareToken.Trim();
        if (token.Length == 0) { DeployStatusText = "Paste the token first."; return; }

        DeployStatusText = "Checking the token…";
        var check = await _deploy.CheckTokenAsync(token);
        if (!check.Ok) { DeployStatusText = check.Text; return; }

        AppConfig.SetCloudflareToken(token);
        CloudflareToken = "";
        _subdomains     = check.Subdomains;

        var wanted = CloudflareAccount?.Id ?? "";
        CloudflareAccounts.Clear();
        foreach (var a in check.Accounts) CloudflareAccounts.Add(new AccountOption(a.Id, a.Name));
        CloudflareAccount = CloudflareAccounts.FirstOrDefault(a => a.Id == wanted)
                         ?? (CloudflareAccounts.Count == 1 ? CloudflareAccounts[0] : null);

        RefreshTokenText();
        RefreshCallbackText();
        DeployStatusText = check.Text;
    }

    private void ForgetCloudflareToken()
    {
        AppConfig.SetCloudflareToken(null);
        _subdomains = new Dictionary<string, string>();
        RefreshTokenText();
        DeployStatusText = "The token is gone from this machine. The site keeps running; only deploying and updating from here need one.";
    }

    /// <summary>
    /// Saves a key and, once both are present on a site deployed from here, puts them on the site
    /// at once — sign-in then works without another deploy. Left alone while a store is loading.
    /// </summary>
    private async Task SaveEveKeyAsync(Action<Store> apply)
    {
        if (_suppressSave || SelectedStore is not StoreRowVm row) return;
        await SaveAsync(apply);
        if (_webEveClientId.Trim().Length == 0 || _webEveClientSecret.Trim().Length == 0) return;
        if (!AppConfig.HasCloudflareToken || CloudflareAccount is null) return;
        var r = await _deploy.PutEveKeysAsync(row.Id);
        await Dispatcher.UIThread.InvokeAsync(() => DeployStatusText = r.Text);
    }

    /// <summary>Deploys, or updates, the site; the same button for both.</summary>
    private async Task DeploySiteAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;
        var progress = new Progress<string>(s => DeployStatusText = s);
        var r = await _deploy.DeployAsync(row.Id, progress);
        await LoadSelectedAsync();

        DeployStatusText = r.Text;
        RefreshCallbackText();
    }

    private async Task CheckSiteAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;
        DeployStatusText = "Asking the site…";
        var r = await _deploy.CheckSiteAsync(row.Id);
        DeployStatusText = r.Text;
    }

    /// <summary>Typed name for the allow list, resolved when added.</summary>
    private string _senderName = "";
    public string SenderName { get => _senderName; set => this.RaiseAndSetIfChanged(ref _senderName, value); }

    private string _senderKind = "Character";
    public string SenderKind { get => _senderKind; set => this.RaiseAndSetIfChanged(ref _senderKind, value); }

    public IReadOnlyList<string> SenderKinds { get; } = ["Character", "Corporation", "Alliance"];

    /// <summary>One suggestion in the name box.</summary>
    public sealed record SenderOption(long Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>What the user picked from the dropdown, if they picked rather than typed.</summary>
    private SenderOption? _senderMatch;
    public SenderOption? SenderMatch
    {
        get => _senderMatch;
        set => this.RaiseAndSetIfChanged(ref _senderMatch, value);
    }

    /// <summary>How many suggestions the name box offers at once. Anyone after a particular
    /// corporation types more of its name; nobody scrolls forty thousand rows.</summary>
    private const int NameMatchLimit = 50;

    /// <summary>
    /// Names matching what has been typed so far, from the ids the app has already resolved.
    ///
    /// <para><b>⚠️ AsyncPopulator, not ItemsSource.</b> The name cache holds 267,000 characters
    /// and 39,000 corporations; handing the box that list would have it lay out every one.
    /// FilterMode must be None to match — this has already narrowed the list, and letting the box
    /// filter again would drop matches it never received.</para>
    ///
    /// <para>The point is being able to see whether a name is right before pressing Add. Without
    /// it the only feedback was Add working or not working, with nothing to say which character
    /// of a long corporation name was wrong.</para>
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> SenderPopulator => async (text, ct) =>
    {
        var needle = (text ?? "").Trim();
        if (needle.Length < 2) return [];

        var category = CategoryOf(SenderKind);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // ⚠️ LIKE, not ==. SQLite compares text with = case-sensitively, which is the whole
            // reason this box needed fixing; LIKE is case-insensitive for ASCII and is what makes
            // "ytiri" find "Ytiri". A contained match rather than a prefix because the
            // distinctive word of a corporation name is often not its first.
            //
            // No index exists on this table, so this is a scan — but LIMIT stops it early and it
            // measured between 1 and 28 ms across the categories, which is well inside what a
            // keystroke can absorb.
            var hits = await db.UniverseNames.AsNoTracking()
                .Where(n => n.Category == category && EF.Functions.Like(n.Name, $"%{needle}%"))
                .OrderBy(n => n.Name)
                .Take(NameMatchLimit)
                .Select(n => new { n.EntityId, n.Name })
                .ToListAsync(ct);

            return hits.Select(h => (object)new SenderOption(h.EntityId, h.Name)).ToList();
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(SenderPopulator), ex);
            return [];
        }
    };

    private static string CategoryOf(string kind) => kind switch
    {
        "Corporation" => "corporation",
        "Alliance"    => "alliance",
        _             => "character",
    };

    private StoreSenderRowVm? _selectedSender;
    public StoreSenderRowVm? SelectedSender
    {
        get => _selectedSender;
        set => this.RaiseAndSetIfChanged(ref _selectedSender, value);
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    private string _statInquiries = "0", _statActive = "0", _statCompleted = "0", _statCancelled = "0";
    public string StatInquiries { get => _statInquiries; private set => this.RaiseAndSetIfChanged(ref _statInquiries, value); }
    public string StatActive    { get => _statActive;    private set => this.RaiseAndSetIfChanged(ref _statActive,    value); }
    public string StatCompleted { get => _statCompleted; private set => this.RaiseAndSetIfChanged(ref _statCompleted, value); }
    public string StatCancelled { get => _statCancelled; private set => this.RaiseAndSetIfChanged(ref _statCancelled, value); }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var stores = await db.Stores.AsNoTracking()
                .Where(s => !s.IsDeleted)
                .OrderBy(s => s.Name).ToListAsync();

            // Offered in the label box below. Loaded here rather than on demand because this list
            // barely changes and the box needs it the moment a store is selected.
            var known = await _labels.AllAsync();
            _knownLabels.Clear();
            foreach (var label in known) _knownLabels.Add(label);

            // Only characters we hold a token for: a shop's address has to be a mailbox we can
            // read and send from, and one we cannot is a store that silently never answers.
            var chars = await db.Characters.AsNoTracking()
                .Where(c => c.RefreshToken != "")
                .Select(c => new { c.Id, c.Name })
                .OrderBy(c => c.Name).ToListAsync();

            var postings = await db.SalePostings.AsNoTracking()
                .Select(p => new { p.Id, p.Name }).OrderBy(p => p.Name).ToListAsync();

            var keepId = SelectedStore?.Id;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CharacterOptions.Clear();
                foreach (var c in chars) CharacterOptions.Add(new CharacterOption(c.Id, c.Name));

                PostingOptions.Clear();
                foreach (var p in postings) PostingOptions.Add(new PostingOption(p.Id, p.Name));

                Stores.Clear();
                foreach (var s in stores) Stores.Add(new StoreRowVm(s));

                SelectedStore = keepId is int id
                    ? Stores.FirstOrDefault(s => s.Id == id) ?? Stores.FirstOrDefault()
                    : Stores.FirstOrDefault();

                Status = Stores.Count == 0
                    ? "No stores yet — add one to let buyers ask by EVE mail."
                    : "";
            });

            await LoadSelectedAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(LoadAsync), ex);
            Status = $"Load failed: {ex.Message}";
        }
    }

    private async Task LoadSelectedAsync()
    {
        if (SelectedStore is not StoreRowVm row)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { Mails.Clear(); Senders.Clear(); });
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == row.Id);
            if (store is null) return;

            var mails = await db.StoreMails.AsNoTracking()
                .Where(m => m.StoreId == row.Id)
                .OrderByDescending(m => m.Id)
                .Take(200)
                .ToListAsync();

            var senders = await db.StoreSenders.AsNoTracking()
                .Where(s => s.StoreId == row.Id).OrderBy(s => s.Name).ToListAsync();

            var webEvents = await db.StoreWebEvents.AsNoTracking()
                .Where(e => e.StoreId == row.Id)
                .OrderByDescending(e => e.Id)
                .Take(100)
                .ToListAsync();

            // ⚠️ Counted off orders, not off the mail log. A mail says what was asked for; only
            // the order says what became of it, and an order cancelled in the Order Tracker by
            // hand never produced a mail at all.
            // ⚠️ Only StoreId is pushed into SQL. CreatedAt is a DateTimeOffset, which EF cannot
            // translate against SQLite, so every date decision below happens in memory.
            var orders = await db.TrackedOrders.AsNoTracking()
                .Where(o => o.StoreId == row.Id)
                .ToListAsync();

            var orderTypeIds = orders.Select(o => o.TypeId).Distinct().ToList();
            var orderTypeNames = await db.SdeTypes.AsNoTracking()
                .Where(t => orderTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            // ⚠️ By item, not by order reference. A reference is how the mail tool addresses a
            // conversation — a buyer who asked for three things in one message gets one — and it
            // is not a unit of anything worth counting. Everything else that summarises this
            // data, the Order Tracker and the Sales Tracker both, counts items.
            //
            // Grouping by it also gave an order with one item delivered and another cancelled
            // nowhere to go: "all completed" and "all cancelled" were both false, so it fell out
            // of the counts entirely and a store whose only order was that showed zeroes.
            var active    = orders.Count(o => o.Status == "pending");
            var completed = orders.Count(o => o.Status == "completed");
            var cancelled = orders.Count(o => o.Status == "canceled");

            var inquiries = mails.Count(m => m.Direction == "in");

            // ⚠️ Built from the same list the "Active items" count above is built from, so the
            // number and the rows behind it cannot drift apart.
            //
            // Soonest promised first, because this is a list of what the shop still owes people;
            // an order nobody has dated sorts last rather than sorting as blank-is-earliest, and
            // sits together with the others nobody has answered. Every column is click-sortable
            // for any other question.
            var orderRows = orders
                .Where(o => o.Status == "pending")
                .OrderBy(o => o.EstimatedDate is { Length: > 0 } ? 0 : 1)
                .ThenBy(o => o.EstimatedDate, StringComparer.Ordinal)
                .ThenBy(o => o.CreatedAt)
                .Select(o => new OrderSummaryRowVm(
                    o, orderTypeNames.GetValueOrDefault(o.TypeId, "")))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _suppressSave = true;
                try
                {
                    StoreName      = store.Name;
                    StoreCharacter = CharacterOptions.FirstOrDefault(c => c.Id == store.CharacterId);
                    StorePosting   = PostingOptions.FirstOrDefault(p => p.Id == store.PostingId);
                    SenderPolicy   = store.SenderPolicy;
                    StoreEnabled     = store.Enabled;
                    AutoEstimate     = store.AutoEstimateInStock;
                    AutoEstimateDays = store.AutoEstimateDays;
                    StoreOrderLabels   = store.OrderLabels;
                    UseCustomUsage     = store.UseCustomUsage;
                    StoreInfo          = store.Info;

                    // ⚠️ The stock text when nothing is saved, so the box always shows what this
                    // store actually sends. Assigned after the flag, since setting the flag is
                    // what would otherwise fill it — and inside the save-suppressed block, so
                    // merely selecting a store does not write anything back.
                    CustomUsage        = store.CustomUsage.Length > 0
                                       ? store.CustomUsage
                                       : StoreMailService.DefaultUsageForEditing(store);
                    MessageHeader      = store.MessageHeader;
                    MessageHeaderColor = store.MessageHeaderColor;
                    MessageFooter      = store.MessageFooter;
                    MessageFooterColor = store.MessageFooterColor;

                    WebEnabled        = store.WebEnabled;
                    WebUrl            = store.WebUrl;
                    WebSecret         = store.WebSecret;
                    WebTheme          = ThemeOptions.FirstOrDefault(t => t.Key == store.WebTheme) ?? ThemeOptions[0];
                    WebBuyerMaySwitch = store.WebBuyerMaySwitch;
                    WebMailUpdates    = store.WebMailUpdates;
                    WebBlurb          = store.WebBlurb;
                    WebStatusText     = DescribeWeb(store);
                    WebHasError       = store.WebEnabled && store.WebLastError.Length > 0;
                    WebWorkerName     = store.WebWorkerName.Length > 0 ? store.WebWorkerName : CloudflareDeployService.DefaultWorkerName(store.Name);
                    WebEveClientId    = store.WebEveClientId;
                    WebEveClientSecret = store.WebEveClientSecret;
                    CloudflareAccount = AccountFor(store.WebCloudflareAccountId);
                    RefreshCallbackText();
                    RefreshSsoWarning();


                }
                finally { _suppressSave = false; }

                Mails.Clear();
                foreach (var m in mails) Mails.Add(new StoreMailRowVm(m));

                Orders.Clear();
                foreach (var o in orderRows) Orders.Add(o);

                Senders.Clear();
                foreach (var s in senders) Senders.Add(new StoreSenderRowVm(s));

                WebEvents.Clear();
                foreach (var e in webEvents) WebEvents.Add(new StoreWebEventRowVm(e));

                StatInquiries = inquiries.ToString("N0");
                StatActive    = active.ToString("N0");
                StatCompleted = completed.ToString("N0");
                StatCancelled = cancelled.ToString("N0");

                this.RaisePropertyChanged(nameof(HasSelection));
            });
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(LoadSelectedAsync), ex);
            Status = AppErrorLogger.Line("Error loading the selected store", ex);
        }
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    /// <summary>⚠️ Set while the fields are being filled from the database. Without it, loading a
    /// store writes every one of its own settings straight back — and worse, writes the previous
    /// store's values onto it in the instant before the rest arrive.</summary>
    private bool _suppressSave;

    private async Task SaveAsync(Action<Store> apply, bool nudge = false)
    {
        if (_suppressSave || SelectedStore is not StoreRowVm row) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == row.Id);
            if (store is null) return;

            apply(store);
            await db.SaveChangesAsync();

            // The list shows the name and whether it is open, so it has to follow — in place, so
            // the row the user is editing stays the row that is selected.
            await Dispatcher.UIThread.InvokeAsync(() => row.Refresh(store));

            // A web setting changed: the site should show it on the next call, not the next
            // interval. Only the lease holder's loop is listening, which is the point.
            if (nudge) _webSync.Nudge();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(SaveAsync), ex);
            Status = $"Save failed: {ex.Message}";
        }
    }

    private async Task AddStoreAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var store = new Store
        {
            Name       = "New store",
            CreatedAt  = DateTimeOffset.UtcNow,
            // Closed, with nothing before now to answer. Both are the safe position: a shop is
            // configured first and opened deliberately.
            Enabled    = false,
            ListenFrom = DateTimeOffset.UtcNow,
        };
        db.Stores.Add(store);
        await db.SaveChangesAsync();

        await LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
            SelectedStore = Stores.FirstOrDefault(s => s.Id == store.Id));
    }

    /// <summary>
    /// Asked before a store is deleted. Set by the view, which owns the dialog.
    ///
    /// <para>⚠️ Null means no confirmation, and the delete proceeds. That is deliberate — a view
    /// model that refused to work without a dialog wired up would be a worse failure than the one
    /// this guards against — but every view that shows the button should set it.</para>
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    private async Task DeleteStoreAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;

        // Naming what survives is half the point. Deleting a shop reads as though it might take
        // the orders with it, and someone hesitating over that deserves the answer in the prompt
        // rather than after.
        if (ConfirmDelete is { } ask)
        {
            var confirmed = await ask(
                $"Delete the store \"{row.Name}\"?\n\n" +
                "It closes and disappears from this list.\n\n" +
                "Nothing is destroyed: its orders stay in the Order Tracker, and its settings, " +
                "allow list and message history are kept so anything referring to it still " +
                "resolves. It simply stops reading and answering mail.");

            if (!confirmed) return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == row.Id);
        if (store is null) return;

        // ⚠️ Hidden, not removed. Orders keep their StoreId for life — they outlive the shop on
        // purpose — and deleting the row left that id pointing at nothing, so an order could no
        // longer say which shop took it. The senders and the message log stay for the same
        // reason: they are the record of what was agreed and with whom.
        store.IsDeleted = true;

        // Closed as well as hidden. The poll already skips deleted stores, but a shop that is
        // invisible and still marked open is a state waiting to be misread by the next thing
        // that queries this table.
        store.Enabled = false;

        await db.SaveChangesAsync();
        await LoadAsync();
    }

    private async Task AddSenderAsync()
    {
        if (SelectedStore is not StoreRowVm row) return;
        if (string.IsNullOrWhiteSpace(SenderName)) return;

        var kind = CategoryOf(SenderKind);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Resolved to an id now rather than matched by name later. Not because names change
            // — in EVE they cannot, short of a petition — but because a name is not unique
            // across categories and is easy to mistype. An id either matches the sender or does
            // not, which is what an authorisation check needs.
            //
            // A pick from the dropdown already carries its id, so it is trusted over a fresh
            // lookup — but only while the text still matches it. Picking a suggestion and then
            // editing the box would otherwise add the entity that was picked rather than the one
            // now written, which is the sort of thing nobody notices until the wrong person is
            // being served.
            var typed = SenderName.Trim();
            var resolved = SenderMatch is { } picked
                        && string.Equals(picked.Name, typed, StringComparison.OrdinalIgnoreCase)
                ? (picked.Id, picked.Name)
                : await ResolveAsync(db, typed, kind);
            if (resolved is null)
            {
                Status = $"Could not find a {kind} called \"{typed}\".";
                return;
            }

            var (id, name) = resolved.Value;

            if (await db.StoreSenders.AnyAsync(s => s.StoreId == row.Id && s.EntityId == id))
            {
                Status = $"{name} is already on the list.";
                return;
            }

            db.StoreSenders.Add(new StoreSender
            {
                StoreId = row.Id, EntityId = id, EntityType = kind, Name = name,
            });
            await db.SaveChangesAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SenderName  = "";
                SenderMatch = null;
                Status      = $"Added {name}.";
            });
            await LoadSelectedAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(StoresViewModel), nameof(AddSenderAsync), ex);
            Status = $"Could not add: {ex.Message}";
        }
    }

    public async Task RemoveSenderAsync(StoreSenderRowVm sender)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.StoreSenders.Where(s => s.Id == sender.Id).ExecuteDeleteAsync();
        await LoadSelectedAsync();
    }

    /// <summary>
    /// A name to an id, from what the app already knows.
    ///
    /// <para>UniverseNames is the app's own cache of every id it has ever resolved, which for a
    /// corporation or alliance the user deals with is almost always a hit. It is checked before
    /// anything is asked of ESI.</para>
    /// </summary>
    private static async Task<(long Id, string Name)?> ResolveAsync(
        AppDbContext db, string name, string kind)
    {
        // ⚠️ NOT `n.Name == name`. SQLite compares TEXT with = case-sensitively, so "ytiri" found
        // nothing while "Ytiri" found the corporation — the box appeared to reject a name that
        // was perfectly correct apart from a capital letter. LIKE with no wildcards is an exact
        // match that ignores ASCII case, which is what a name box should do.
        //
        // ⚠️ Escaped first: a name containing % or _ would otherwise become a pattern and match
        // something else entirely. EVE allows neither today, but a lookup that silently matches
        // the wrong entity is not a thing to leave resting on that.
        var category = kind switch
        {
            "corporation" => "corporation",
            "alliance"    => "alliance",
            _             => "character",
        };

        var pattern = name.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        var hits = await db.UniverseNames.AsNoTracking()
            .Where(n => n.Category == category && EF.Functions.Like(n.Name, pattern, "\\"))
            .Select(n => new { n.EntityId, n.Name })
            .Take(5)
            .ToListAsync();

        if (hits.Count == 0) return null;

        // An exact-case match wins if there is one; otherwise the single case-insensitive hit.
        // Two entities differing only in case is not something EVE allows, so more than one hit
        // means the pattern escaped — and picking arbitrarily between them would be a guess.
        var exact = hits.FirstOrDefault(h => h.Name == name);
        if (exact is not null) return (exact.EntityId, exact.Name);

        return hits.Count == 1 ? (hits[0].EntityId, hits[0].Name) : null;
    }

    private async Task CheckMailNowAsync()
    {
        Status = "Checking…";
        await _storeMail.RunOnceAsync();
        await LoadSelectedAsync();
        Status = _storeMail.StatusText;
    }
}
