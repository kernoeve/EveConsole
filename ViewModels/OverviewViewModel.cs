using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveCortex.ViewModels;

public record ActivityPeriodOption(string Label, int Hours)
{
    public override string ToString() => Label;
}

public class NewsItemVm : ReactiveObject
{
    public string Title       { get; }
    public string Link        { get; }
    public string PubDateText { get; }
    public string PreviewText { get; }
    public string FullText    { get; }
    public bool   HasBody     { get; }
    public bool   CanExpand   { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(IsCollapsed));
            this.RaisePropertyChanged(nameof(ExpandLabel));
        }
    }
    public bool   IsCollapsed => !IsExpanded;
    public string ExpandLabel => IsExpanded ? "▲ Less" : "▼ More";

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand   { get; }

    public NewsItemVm(NewsItem item)
    {
        Title       = item.Title;
        Link        = item.Link;
        PubDateText = item.PubDateText;

        FullText    = HtmlToText(item.DescriptionHtml);
        HasBody     = FullText.Length > 0;
        PreviewText = MakePreview(FullText, maxChars: 300);
        CanExpand   = HasBody && FullText.Length > PreviewText.Length;

        ToggleCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
        OpenCommand   = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrEmpty(Link))
                Process.Start(new ProcessStartInfo(Link) { UseShellExecute = true });
        });
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // Headers → upper-case line
        html = Regex.Replace(html, @"<h[1-6][^>]*>(.*?)</h[1-6]>",
            m => "\n" + WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).ToUpperInvariant() + "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Horizontal rules → divider
        html = Regex.Replace(html, @"<hr\s*/?>", "\n──────────────\n", RegexOptions.IgnoreCase);

        // Block tags → newlines
        html = Regex.Replace(html, @"<(p|div|li|tr)[^>]*>", "\n",   RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</(p|div|ul|ol)[^>]*>", "\n",  RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>", "\n",               RegexOptions.IgnoreCase);

        // Keep link text, discard tag
        html = Regex.Replace(html, @"<a[^>]*>(.*?)</a>", "$1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Strip all remaining tags
        html = Regex.Replace(html, @"<[^>]+>", "");

        // Decode HTML entities (&#39; → ', &nbsp; → space, etc.)
        html = WebUtility.HtmlDecode(html);

        // Normalise whitespace
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n[ \t]+", "\n");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }

    private static string MakePreview(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;

        // Prefer breaking at a paragraph boundary
        var paraEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (paraEnd > 30 && paraEnd <= maxChars) return text[..paraEnd] + "…";

        // Fall back to the last sentence end before maxChars
        var sentEnd = text.LastIndexOf(". ", maxChars, StringComparison.Ordinal);
        if (sentEnd > 30) return text[..(sentEnd + 1)] + "…";

        return text[..maxChars].TrimEnd() + "…";
    }

}

public class AlertRowVm : ReactiveObject
{
    public string Message       { get; init; } = "";
    public bool   IsDismissible { get; init; } = false;

    public ReactiveCommand<Unit, Unit>? DismissCommand { get; init; }

    public ReactiveCommand<Unit, Unit>? NavigateCommand { get; init; }
    public bool IsClickable => NavigateCommand is not null;

    // Alert-specific leading icon. When set (e.g. a character portrait for skill-queue
    // alerts) it replaces the default warning glyph.
    public Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;
    public bool NoIcon  => Icon is null;
}

// A recent notification rendered in the in-game style: icon + one-liner + age,
// with the full detail in a tooltip.
public class NotificationBoxVm
{
    public string OneLiner    { get; init; } = "";
    public string AgeText     { get; init; } = "";
    public string TooltipText { get; init; } = "";
    public bool   IsUnread    { get; init; }
    public string UnreadDot   => IsUnread ? "●" : "";

    public Bitmap? Icon          { get; init; }
    public string  FallbackGlyph { get; init; } = "✉";
    public bool HasIcon => Icon is not null;
    public bool NoIcon  => Icon is null;
}

public class OverviewViewModel : ReactiveObject
{
    private readonly AppDbContext           _db;
    private readonly AlertSettingsViewModel _alertSettings;
    private readonly AppErrorLogger         _errorLogger;
    private readonly NewsService            _newsService;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly ContractNameResolver?  _names;

    // ── Period selection ──────────────────────────────────────────────────────
    public IReadOnlyList<ActivityPeriodOption> Periods { get; } =
    [
        new("Last 24 Hours",  24),
        new("Last 7 Days",    168),
        new("Last 30 Days",   720),
        new("Last 90 Days",   2160),
    ];

    private ActivityPeriodOption _selectedPeriod;
    public ActivityPeriodOption SelectedPeriod
    {
        get => _selectedPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedPeriod, value);
    }

    // ── Period metrics (grid) ─────────────────────────────────────────────────
    private string _mktSellCount      = "—"; public string MktSellCount      { get => _mktSellCount;      set => this.RaiseAndSetIfChanged(ref _mktSellCount,      value); }
    private string _mktSellIsk        = "—"; public string MktSellIsk        { get => _mktSellIsk;        set => this.RaiseAndSetIfChanged(ref _mktSellIsk,        value); }
    private string _mktBuyCount       = "—"; public string MktBuyCount       { get => _mktBuyCount;       set => this.RaiseAndSetIfChanged(ref _mktBuyCount,       value); }
    private string _mktBuyIsk         = "—"; public string MktBuyIsk         { get => _mktBuyIsk;         set => this.RaiseAndSetIfChanged(ref _mktBuyIsk,         value); }
    private string _completedJobCount = "—"; public string CompletedJobCount { get => _completedJobCount; set => this.RaiseAndSetIfChanged(ref _completedJobCount, value); }
    private string _shipKillCount     = "—"; public string ShipKillCount     { get => _shipKillCount;     set => this.RaiseAndSetIfChanged(ref _shipKillCount,     value); }
    private string _shipLossCount     = "—"; public string ShipLossCount     { get => _shipLossCount;     set => this.RaiseAndSetIfChanged(ref _shipLossCount,     value); }

    // ── Period income / expense totals ────────────────────────────────────────
    private string _incomeTotalText  = ""; public string IncomeTotalText  { get => _incomeTotalText;  private set => this.RaiseAndSetIfChanged(ref _incomeTotalText,  value); }
    private string _expenseTotalText = ""; public string ExpenseTotalText { get => _expenseTotalText; private set => this.RaiseAndSetIfChanged(ref _expenseTotalText, value); }

    // ── Current-state metrics (grid) ──────────────────────────────────────────
    private string _sellOrderCount = "—"; public string SellOrderCount { get => _sellOrderCount; set => this.RaiseAndSetIfChanged(ref _sellOrderCount, value); }
    private string _sellOrderIsk   = "—"; public string SellOrderIsk   { get => _sellOrderIsk;   set => this.RaiseAndSetIfChanged(ref _sellOrderIsk,   value); }
    private string _buyOrderCount  = "—"; public string BuyOrderCount  { get => _buyOrderCount;  set => this.RaiseAndSetIfChanged(ref _buyOrderCount,  value); }
    private string _buyOrderIsk    = "—"; public string BuyOrderIsk    { get => _buyOrderIsk;    set => this.RaiseAndSetIfChanged(ref _buyOrderIsk,    value); }
    private string _ctrActiveCount = "—"; public string CtrActiveCount { get => _ctrActiveCount; set => this.RaiseAndSetIfChanged(ref _ctrActiveCount, value); }
    private string _activeJobCount = "—"; public string ActiveJobCount { get => _activeJobCount; set => this.RaiseAndSetIfChanged(ref _activeJobCount, value); }

    // ── Status ────────────────────────────────────────────────────────────────
    private string _loadStatus = "";
    public string LoadStatus
    {
        get => _loadStatus;
        private set
        {
            this.RaiseAndSetIfChanged(ref _loadStatus, value);
            this.RaisePropertyChanged(nameof(HasLoadStatus));
            this.RaisePropertyChanged(nameof(HasLoadError));
        }
    }
    public bool HasLoadError  => LoadStatus.StartsWith("Error:");
    public bool HasLoadStatus => LoadStatus.Length > 0;

    // ── Pie charts ────────────────────────────────────────────────────────────
    private ISeries[] _incomeSeries = [];
    public ISeries[] IncomeSeries  { get => _incomeSeries;  private set => this.RaiseAndSetIfChanged(ref _incomeSeries,  value); }

    private ISeries[] _expenseSeries = [];
    public ISeries[] ExpenseSeries { get => _expenseSeries; private set => this.RaiseAndSetIfChanged(ref _expenseSeries, value); }

    private bool _hasIncomeData;
    public bool HasIncomeData  { get => _hasIncomeData;  private set => this.RaiseAndSetIfChanged(ref _hasIncomeData,  value); }

    private bool _hasExpenseData;
    public bool HasExpenseData { get => _hasExpenseData; private set => this.RaiseAndSetIfChanged(ref _hasExpenseData, value); }

    // ── Alerts ────────────────────────────────────────────────────────────────
    public ObservableCollection<AlertRowVm> Alerts { get; } = [];

    private bool _hasAlerts;
    public bool HasAlerts { get => _hasAlerts; private set => this.RaiseAndSetIfChanged(ref _hasAlerts, value); }
    public bool NoAlerts  => !HasAlerts;

    // EVE image-server images (portraits, corp/alliance logos, type icons) used as alert
    // and notification icons, cached by path across polls so each is fetched at most once.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, Bitmap> _imageCache = [];

    // path is relative to https://images.evetech.net/ (e.g. "characters/123/portrait?size=64").
    private async Task<Bitmap?> GetImageAsync(string path)
    {
        if (_imageCache.TryGetValue(path, out var cached))
            return cached;
        try
        {
            var bytes = await _http.GetByteArrayAsync($"https://images.evetech.net/{path}");
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _imageCache[path] = bmp;
            return bmp;
        }
        catch { return null; }   // icon is optional; retry on the next poll
    }

    private Task<Bitmap?> GetPortraitAsync(long characterId) =>
        GetImageAsync($"characters/{characterId}/portrait?size=64");

    // ── News feed ─────────────────────────────────────────────────────────────
    public ObservableCollection<NewsItemVm> NewsItems { get; } = [];

    private bool _hasNews;
    public bool HasNews { get => _hasNews; private set => this.RaiseAndSetIfChanged(ref _hasNews, value); }
    public bool NoNews  => !HasNews;

    // ── Recent notifications ────────────────────────────────────────────────────
    public ObservableCollection<NotificationBoxVm> RecentNotifications { get; } = [];

    private bool _hasNotifications;
    public bool HasNotifications { get => _hasNotifications; private set => this.RaiseAndSetIfChanged(ref _hasNotifications, value); }
    public bool NoNotifications  => !HasNotifications;

    // ── Personal killmails section ──────────────────────────────────────────────
    public ObservableCollection<Activity24hKillRowVm> PersonalKills { get; } = [];

    private bool _hasPersonalKills;
    public bool HasPersonalKills { get => _hasPersonalKills; private set => this.RaiseAndSetIfChanged(ref _hasPersonalKills, value); }
    public bool NoPersonalKills  => !HasPersonalKills;

    private string _personalKillCount = "—"; public string PersonalKillCount { get => _personalKillCount; private set => this.RaiseAndSetIfChanged(ref _personalKillCount, value); }
    private string _personalKillIsk   = "—"; public string PersonalKillIsk   { get => _personalKillIsk;   private set => this.RaiseAndSetIfChanged(ref _personalKillIsk,   value); }
    private string _personalLossCount = "—"; public string PersonalLossCount { get => _personalLossCount; private set => this.RaiseAndSetIfChanged(ref _personalLossCount, value); }
    private string _personalLossIsk   = "—"; public string PersonalLossIsk   { get => _personalLossIsk;   private set => this.RaiseAndSetIfChanged(ref _personalLossIsk,   value); }

    private HashSet<int> _lastPersonalKillIds = [];

    // ── Standing projects section ───────────────────────────────────────────────
    public ObservableCollection<StandingProjectRowVm> StandingProjects { get; } = [];
    private bool _hasStandingProjects;
    public bool HasStandingProjects { get => _hasStandingProjects; private set => this.RaiseAndSetIfChanged(ref _hasStandingProjects, value); }
    public bool NoStandingProjects  => !HasStandingProjects;

    // ── Loading state ─────────────────────────────────────────────────────────
    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private const string PeriodPrefKey = "overview.period_hours";
    private readonly AppPreferencesService? _prefs;
    private readonly CorpActivityService?   _corpActivity;

    // Wired by MainWindowViewModel after construction — lets alert rows jump to the
    // relevant UI (character skills tab, corp activity standing projects tab).
    public Action<string>? NavigateToCharacterSkills               { get; set; }
    public Action?          NavigateToStandingProjects              { get; set; }
    public Action<int>?     RequestOpenKillmail                     { get; set; }
    public Action<string>?  OpenToolRequested                       { get; set; }  // open a tool by id
    public Action?          OpenAlertSettingsRequested              { get; set; }  // Settings ▸ Alerts

    // Shared Sale Listing tool VMs, injected by MainWindowViewModel, so the Overview can embed
    // those grids as sections without loading the data a second time.
    private SaleListingViewModel? _saleListingBuild;
    public SaleListingViewModel? SaleListingBuild
    {
        get => _saleListingBuild;
        set { this.RaiseAndSetIfChanged(ref _saleListingBuild, value); value?.SetPeriodDays(CurrentPeriodDays); }
    }
    private SaleListingViewModel? _saleListingMarket;
    public SaleListingViewModel? SaleListingMarket
    {
        get => _saleListingMarket;
        set { this.RaiseAndSetIfChanged(ref _saleListingMarket, value); value?.SetPeriodDays(CurrentPeriodDays); }
    }

    // The Income & Expense tool VM, injected by MainWindowViewModel so it can be embedded as a
    // section. It keeps its own period selector.
    private IncomeExpenseViewModel? _incomeExpense;
    public IncomeExpenseViewModel? IncomeExpense
    {
        get => _incomeExpense;
        set { this.RaiseAndSetIfChanged(ref _incomeExpense, value); value?.SetPeriodDays(CurrentPeriodDays); }
    }

    private int CurrentPeriodDays => Math.Max(1, SelectedPeriod.Hours / 24);

    // ── Customizable section layout ─────────────────────────────────────────────
    private const string LayoutPrefKey = "overview.layout";
    private OverviewLayout _layout = OverviewLayout.Default();
    public OverviewLayout Layout => _layout;

    // Raised when the layout changes; the view rebuilds its section grid in response.
    public event Action? LayoutChanged;

    public async Task ApplyLayoutAsync(OverviewLayout layout)
    {
        _layout = layout;
        if (_prefs is not null)
            await _prefs.SetAsync(LayoutPrefKey, layout.ToJson());
        LayoutChanged?.Invoke();
        // A newly-added section (e.g. Personal Killmails) needs its data loaded now rather
        // than waiting for the next refresh tick.
        _ = LoadAsync();
    }

    public OverviewViewModel(AppDbContext db, AlertSettingsViewModel alertSettings,
                             AppErrorLogger errorLogger, NewsService newsService,
                             AppPreferencesService? prefs = null,
                             CorpActivityService? corpActivity = null,
                             IDbContextFactory<AppDbContext>? dbFactory = null,
                             EsiClient? esi = null)
    {
        _db             = db;
        _alertSettings  = alertSettings;
        _errorLogger    = errorLogger;
        _newsService    = newsService;
        _prefs          = prefs;
        _corpActivity   = corpActivity;
        _dbFactory      = dbFactory;
        _layout         = OverviewLayout.FromJsonOrDefault(prefs?.Get(LayoutPrefKey));
        if (dbFactory is not null && esi is not null)
            _names = new ContractNameResolver(dbFactory, esi, errorLogger);

        // Restore saved period, defaulting to 30 days
        var savedHours  = prefs?.GetLong(PeriodPrefKey, 720) ?? 720;
        _selectedPeriod = Periods.FirstOrDefault(p => p.Hours == (int)savedHours) ?? Periods[2];

        this.WhenAnyValue(x => x.SelectedPeriod)
            .Skip(1)
            .Subscribe(p =>
            {
                if (_prefs is not null)
                    _ = _prefs.SetLongAsync(PeriodPrefKey, p.Hours);
                SaleListingBuild?.SetPeriodDays(CurrentPeriodDays);
                SaleListingMarket?.SetPeriodDays(CurrentPeriodDays);
                IncomeExpense?.SetPeriodDays(CurrentPeriodDays);
                _ = LoadAsync();
            });

        // Auto-refresh every 60 seconds — overview only reads local DB so this is fast.
        Observable.Interval(TimeSpan.FromSeconds(60))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(n => _ = LoadAsync());
    }

    private bool _loadPending;

    public async Task LoadAsync()
    {
        // If a load is already running, mark that another is needed and let the current run
        // re-run when it finishes — so changing the period (or a refresh tick) always applies.
        if (IsLoading) { _loadPending = true; return; }
        IsLoading = true;
        try
        {
            do
            {
                _loadPending = false;
                await LoadCoreAsync();
            } while (_loadPending);
        }
        finally { IsLoading = false; }
    }

    private async Task LoadCoreAsync()
    {
        LoadStatus = "Querying scope...";
        try
        {
            await _alertSettings.LoadAsync();

            // ── Scope ─────────────────────────────────────────────────────────
            // Only characters we have auth tokens for (i.e. authenticated characters).
            var charIds = await _db.Characters.AsNoTracking()
                .Where(c => c.RefreshToken != "")
                .Select(c => c.Id)
                .ToListAsync();

            // Only personal corporations (IsPersonal = true). Corp data is stored
            // with OwnerType = "corporation" (not "corp").
            var corpIds = await _db.Corporations.AsNoTracking()
                .Where(c => c.IsPersonal)
                .Select(c => (long)c.Id)
                .ToListAsync();

            if (charIds.Count == 0 && corpIds.Count == 0)
            {
                ResetAllMetrics();
                LoadStatus = "No characters found.";
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.AddHours(-_selectedPeriod.Hours);

            // Start news fetch in the background immediately — it hits the network and is
            // the slowest step. We await it last so everything else renders first.
            var newsTask = _newsService.GetNewsAsync();

            // Per-owner list — avoids List<long>.Contains() which EF Core 9 SQLite
            // fails to translate. Corp OwnerType must be "corporation" to match the DB.
            var owners = charIds.Select(id => ("character", id))
                                .Concat(corpIds.Select(id => ("corporation", id)))
                                .ToList();

            // ── Market transactions ────────────────────────────────────────────
            LoadStatus = "Loading market transactions...";
            // Aggregate in SQL with date filter — avoids loading all rows and the
            // DateTimeOffset LINQ translation bug. UnitPrice stored as TEXT so CAST
            // to REAL for arithmetic; result arrives as double, converted to decimal.
            decimal mktSellTotal = 0m, mktBuyTotal = 0m;
            int     mktSellCnt   = 0,  mktBuyCnt   = 0;
            foreach (var (ot, oid) in owners)
            {
                var s = await _db.Database.SqlQuery<TxnSummary>(
                    $"""
                    SELECT
                        COALESCE(SUM(CASE WHEN "IsBuy" = 0 THEN "Quantity" * CAST("UnitPrice" AS REAL) ELSE 0.0 END), 0.0) AS "SellTotal",
                        COALESCE(SUM(CASE WHEN "IsBuy" = 0 THEN 1 ELSE 0 END), 0)                                          AS "SellCount",
                        COALESCE(SUM(CASE WHEN "IsBuy" = 1 THEN "Quantity" * CAST("UnitPrice" AS REAL) ELSE 0.0 END), 0.0) AS "BuyTotal",
                        COALESCE(SUM(CASE WHEN "IsBuy" = 1 THEN 1 ELSE 0 END), 0)                                          AS "BuyCount"
                    FROM "EsiWalletTransactions"
                    WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                    """
                ).FirstOrDefaultAsync();

                if (s != null)
                {
                    mktSellTotal += (decimal)s.SellTotal;
                    mktSellCnt   += s.SellCount;
                    mktBuyTotal  += (decimal)s.BuyTotal;
                    mktBuyCnt    += s.BuyCount;
                }
            }
            MktSellCount = mktSellCnt.ToString("N0");
            MktSellIsk   = FormatIsk(mktSellTotal);
            MktBuyCount  = mktBuyCnt.ToString("N0");
            MktBuyIsk    = FormatIsk(mktBuyTotal);

            // ── Active market orders ───────────────────────────────────────────
            LoadStatus = "Loading market orders...";
            var orders = new List<(bool IsBuy, int VolRemain, decimal Price)>();
            foreach (var (ot, oid) in owners)
                orders.AddRange((await _db.EsiMarketOrders.AsNoTracking()
                    .Where(o => !o.IsHistory && o.OwnerType == ot && o.OwnerId == oid)
                    .Select(o => new { o.IsBuyOrder, o.VolumeRemain, o.Price })
                    .ToListAsync())
                    .Select(o => (o.IsBuyOrder, o.VolumeRemain, o.Price)));

            decimal sellOrderIsk = 0m, buyOrderIsk = 0m;
            int     sellOrderCnt = 0,  buyOrderCnt  = 0;
            foreach (var (isBuy, vol, price) in orders)
            {
                if (isBuy) { buyOrderCnt++;  buyOrderIsk  += price * vol; }
                else       { sellOrderCnt++; sellOrderIsk += price * vol; }
            }
            SellOrderCount = sellOrderCnt.ToString("N0");
            SellOrderIsk   = FormatIsk(sellOrderIsk);
            BuyOrderCount  = buyOrderCnt.ToString("N0");
            BuyOrderIsk    = FormatIsk(buyOrderIsk);

            // ── Contracts ─────────────────────────────────────────────────────
            LoadStatus = "Loading contracts...";
            var contracts = new List<string>();
            foreach (var (ot, oid) in owners)
                contracts.AddRange(await _db.EsiContracts.AsNoTracking()
                    .Where(c => c.OwnerType == ot && c.OwnerId == oid)
                    .Select(c => c.Status)
                    .ToListAsync());

            CtrActiveCount = contracts.Count(s => s == "outstanding").ToString("N0");

            // ── Industry jobs ──────────────────────────────────────────────────
            LoadStatus = "Loading industry jobs...";
            var jobs = new List<(string Status, DateTimeOffset? Completed)>();
            foreach (var (ot, oid) in owners)
                jobs.AddRange((await _db.EsiIndustryJobs.AsNoTracking()
                    .Where(j => j.OwnerType == ot && j.OwnerId == oid)
                    .Select(j => new { j.Status, j.CompletedDate })
                    .ToListAsync())
                    .Select(j => (j.Status, j.CompletedDate)));

            ActiveJobCount    = jobs.Count(j => j.Status == "active").ToString("N0");
            CompletedJobCount = jobs.Count(j => j.Status == "delivered" &&
                                               j.Completed.HasValue && j.Completed.Value >= cutoff)
                                    .ToString("N0");

            // ── Kill mails ─────────────────────────────────────────────────────
            // Losses = distinct killmails where one of our characters is the victim; kills =
            // where one of our characters is an attacker but not the victim. KillMailDetails
            // only exist for killmails we hold (from character OR corp refs), so two aggregate
            // queries replace the old per-character/per-corp loop (much faster).
            LoadStatus = "Counting kills and losses...";
            int totalKills = 0, totalLosses = 0;
            if (charIds.Count > 0)
            {
                var cutoffStr  = DateTimeOffset.UtcNow.AddHours(-SelectedPeriod.Hours)
                    .UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                var charIdList = string.Join(",", charIds);
#pragma warning disable EF1002
                totalLosses = await _db.Database.SqlQueryRaw<int>($"""
                    SELECT COUNT(DISTINCT d."KillMailId") AS "Value"
                    FROM "KillMailDetails" d
                    WHERE d."KillMailTime" >= '{cutoffStr}' AND d."VictimCharId" IN ({charIdList})
                    """).FirstAsync();
                // Non-correlated IN-subquery: computes the attacker killmail set once. A
                // correlated EXISTS here scans the 100k-row attackers table per killmail
                // (~26s); this is ~20ms.
                totalKills = await _db.Database.SqlQueryRaw<int>($"""
                    SELECT COUNT(DISTINCT d."KillMailId") AS "Value"
                    FROM "KillMailDetails" d
                    WHERE d."KillMailTime" >= '{cutoffStr}'
                      AND d."VictimCharId" NOT IN ({charIdList})
                      AND d."KillMailId" IN (SELECT a."KillMailId" FROM "KillMailAttackers" a
                                             WHERE a."CharacterId" IN ({charIdList}))
                    """).FirstAsync();
#pragma warning restore EF1002
            }

            ShipKillCount = totalKills.ToString("N0");
            ShipLossCount = totalLosses.ToString("N0");

            // ── Personal killmails section (bound to the same period) ───────────
            await LoadPersonalKillsAsync(charIds, Math.Max(1, SelectedPeriod.Hours / 24));

            // ── Standing projects section ───────────────────────────────────────
            await LoadStandingProjectsAsync();

            // ── Wallet journal — pie chart categorisation ──────────────────────
            LoadStatus = "Loading journal data...";
            // Group by RefType in SQL with date filter — avoids loading all rows.
            // Amount stored as TEXT; CAST to REAL for SUM. Aggregated per RefType.
            var journalGroups = new List<(string RefType, decimal Total)>();
            foreach (var (ot, oid) in owners)
            {
                var rows = await _db.Database.SqlQuery<JournalGroup>(
                    $"""
                    SELECT "RefType", COALESCE(SUM(CAST("Amount" AS REAL)), 0.0) AS "TotalAmount"
                    FROM "EsiWalletJournal"
                    WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                    GROUP BY "RefType"
                    """
                ).ToListAsync();
                journalGroups.AddRange(rows.Select(r => (r.RefType, (decimal)r.TotalAmount)));
            }

            // Merge duplicate RefTypes across owners
            var journalByType = journalGroups
                .GroupBy(g => g.RefType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Total), StringComparer.OrdinalIgnoreCase);

            LoadStatus = "Building charts...";
            BuildPieCharts(WalletCategorizer.Categorize(journalByType));

            LoadStatus = "Evaluating alerts...";
            await EvaluateAlertsAsync(charIds);

            LoadStatus = "Loading news...";
            var newsItems = await newsTask;
            NewsItems.Clear();
            foreach (var item in newsItems) NewsItems.Add(new NewsItemVm(item));
            HasNews = NewsItems.Count > 0;
            this.RaisePropertyChanged(nameof(NoNews));

            LoadStatus = "Loading notifications...";
            await LoadNotificationsAsync();

            LoadStatus = $"Loaded — {owners.Count} owner(s), period: {_selectedPeriod.Label}";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("OverviewViewModel", "LoadAsync", ex);
            LoadStatus = $"Error: {ex.Message}";
        }
    }

    // ── Recent notifications (last 25, one per NotificationId) ────────────────────
    private async Task LoadNotificationsAsync()
    {
        if (_names is null || _dbFactory is null) return;   // not wired for name resolution/formatting
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

#pragma warning disable EF1002
            var rows = await db.EsiNotifications.FromSqlRaw(
                    "SELECT MIN(CharacterId) AS CharacterId, NotificationId, Type, SenderId, SenderType, " +
                    "Timestamp, MIN(IsRead) AS IsRead, Text FROM EsiNotifications " +
                    "GROUP BY NotificationId ORDER BY Timestamp DESC LIMIT 25")
                .AsNoTracking().ToListAsync();

            var ids = rows.Select(r => r.NotificationId).ToList();
            var recipients = ids.Count == 0
                ? new List<(long NotificationId, long CharacterId)>()
                : (await db.EsiNotifications.FromSqlRaw(
                        $"SELECT * FROM EsiNotifications WHERE NotificationId IN ({string.Join(",", ids)})")
                    .AsNoTracking().Select(n => new { n.NotificationId, n.CharacterId }).ToListAsync())
                  .Select(x => (x.NotificationId, x.CharacterId)).ToList();
#pragma warning restore EF1002

            var recipientsByNotif = recipients.GroupBy(x => x.NotificationId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CharacterId).Distinct().ToList());

            // Parse each notification's fields once, up front.
            var parsed = rows.ToDictionary(r => r.NotificationId, r => NotificationSummary.Parse(r.Text));

            // Resolve every entity (sender, recipients, and per-notification fields) in one batch.
            var entityIds = new HashSet<long>();
            foreach (var r in rows) if (r.SenderId > 0) entityIds.Add(r.SenderId);
            foreach (var (_, cid) in recipients) entityIds.Add(cid);
            foreach (var f in parsed.Values)
                foreach (var id in NotificationSummary.EntityIds(f)) entityIds.Add(id);

            var names = await _names.ResolveAsync(entityIds);

            // Resolve structure names for one-liners (structure life-cycle / ownership notifications).
            var structIds = parsed.Values
                .Where(f => f.StructureId.HasValue).Select(f => f.StructureId!.Value).Distinct().ToList();
            var structNames = structIds.Count == 0
                ? new Dictionary<long, string>()
                : await db.EsiStructureNames.AsNoTracking()
                    .Where(s => structIds.Contains(s.StructureId))
                    .ToDictionaryAsync(s => s.StructureId, s => s.Name);

            var boxes = new List<NotificationBoxVm>(rows.Count);
            foreach (var r in rows)
            {
                var f        = parsed[r.NotificationId];
                var oneLiner = NotificationSummary.OneLiner(r.Type, f, names, structNames);
                var (iconPath, glyph) = NotificationSummary.Icon(r.Type, r.SenderId, r.SenderType, f);
                var icon     = iconPath is null ? null : await GetImageAsync(iconPath);

                var chars = recipientsByNotif.TryGetValue(r.NotificationId, out var cids)
                    ? string.Join(", ", cids.Select(id => names.TryGetValue(id, out var cn) && cn.Length > 0 ? cn : $"ID {id}").OrderBy(s => s))
                    : "";
                var sender = r.SenderId > 0
                    ? (names.TryGetValue(r.SenderId, out var sn) && sn.Length > 0 ? sn : $"ID {r.SenderId}")
                    : "—";
                var body = await NotificationFormatter.FormatAsync(r.Text, _names, _dbFactory);

                var tip = new StringBuilder();
                tip.Append(NotificationFormatter.Humanize(r.Type)).Append('\n');
                tip.Append(r.Timestamp.ToLocalTime().ToString("MMM d, yyyy HH:mm"));
                if (chars.Length > 0) tip.Append("\nTo: ").Append(chars);
                if (sender != "—")    tip.Append("\nFrom: ").Append(sender);
                if (body.Length > 0)  tip.Append("\n\n").Append(body);

                boxes.Add(new NotificationBoxVm
                {
                    OneLiner      = oneLiner,
                    AgeText       = NotificationSummary.Age(r.Timestamp),
                    TooltipText   = tip.ToString(),
                    IsUnread      = !r.IsRead,
                    Icon          = icon,
                    FallbackGlyph = glyph,
                });
            }

            RecentNotifications.Clear();
            foreach (var b in boxes) RecentNotifications.Add(b);
            HasNotifications = RecentNotifications.Count > 0;
            this.RaisePropertyChanged(nameof(NoNotifications));
        }
        catch (Exception ex) { _errorLogger.Log("OverviewViewModel", "LoadNotifications", ex); }
    }

    // ── Personal killmails ────────────────────────────────────────────────────
    private async Task LoadPersonalKillsAsync(List<long> charIds, int days)
    {
        // Skip the (heavier) listing query entirely unless the section is on the grid.
        bool sectionEnabled = _layout.Sections.Any(s => s.Key == "PersonalKillmails" && s.Enabled);
        if (!sectionEnabled || _corpActivity is null || charIds.Count == 0)
        {
            _lastPersonalKillIds = [];
            PersonalKills.Clear();
            HasPersonalKills = false;
            this.RaisePropertyChanged(nameof(NoPersonalKills));
            PersonalKillCount = PersonalLossCount = "0";
            PersonalKillIsk = PersonalLossIsk = CorpActivityViewModel.FormatIskStatic(0m);
            return;
        }

        List<CorpActivityService.Activity24hKillRow> rows;
        try { rows = await _corpActivity.GetPersonalKillsForPeriodAsync(charIds, days); }
        catch (Exception ex) { _errorLogger.Log("OverviewViewModel", "LoadPersonalKills", ex); return; }

        int kills  = rows.Count(r => !r.IsLoss);
        int losses = rows.Count(r => r.IsLoss);
        PersonalKillCount = kills.ToString("N0");
        PersonalLossCount = losses.ToString("N0");
        PersonalKillIsk   = CorpActivityViewModel.FormatIskStatic(rows.Where(r => !r.IsLoss).Sum(r => r.IskValue));
        PersonalLossIsk   = CorpActivityViewModel.FormatIskStatic(rows.Where(r => r.IsLoss).Sum(r => r.IskValue));

        // If the set of killmails is unchanged since the last refresh, keep the existing rows
        // (and their already-loaded images) instead of rebuilding + re-downloading every 60s.
        var ids = rows.Select(r => r.KillMailId).ToHashSet();
        if (ids.SetEquals(_lastPersonalKillIds))
        {
            HasPersonalKills = rows.Count > 0;
            this.RaisePropertyChanged(nameof(NoPersonalKills));
            return;
        }
        _lastPersonalKillIds = ids;

        PersonalKills.Clear();
        foreach (var r in rows) PersonalKills.Add(new Activity24hKillRowVm(r));
        HasPersonalKills = PersonalKills.Count > 0;
        this.RaisePropertyChanged(nameof(NoPersonalKills));
        _ = Task.WhenAll(PersonalKills.Select(k => k.LoadImagesAsync()));
    }

    // ── Standing projects ─────────────────────────────────────────────────────
    private async Task LoadStandingProjectsAsync()
    {
        bool enabled = _corpActivity is not null
                    && _layout.Sections.Any(s => s.Key == "StandingProjects" && s.Enabled);
        if (!enabled)
        {
            StandingProjects.Clear();
            HasStandingProjects = false;
            this.RaisePropertyChanged(nameof(NoStandingProjects));
            return;
        }

        try
        {
            var corpIds = await _db.CorpStandingProjects.AsNoTracking()
                .Select(sp => sp.CorporationId).Distinct().ToListAsync();

            var rows = new List<StandingProjectGridRow>();
            foreach (var corpId in corpIds)
                rows.AddRange(await _corpActivity!.BuildMaintainGridRowsAsync(corpId));

            StandingProjects.Clear();
            foreach (var r in rows)
                StandingProjects.Add(new StandingProjectRowVm(r, _ => { }, _ => { }));
            HasStandingProjects = StandingProjects.Count > 0;
            this.RaisePropertyChanged(nameof(NoStandingProjects));
        }
        catch (Exception ex) { _errorLogger.Log("OverviewViewModel", "LoadStandingProjects", ex); }
    }

    // ── DTOs for raw SQL results ──────────────────────────────────────────────

    private sealed class TxnSummary
    {
        public double SellTotal { get; set; }
        public int    SellCount { get; set; }
        public double BuyTotal  { get; set; }
        public int    BuyCount  { get; set; }
    }

    private sealed class JournalGroup
    {
        public string RefType     { get; set; } = "";
        public double TotalAmount { get; set; }
    }


    // ── Helpers ───────────────────────────────────────────────────────────────

    private void BuildPieCharts(List<WalletCategory> cats)
    {
        static ISeries Slice(WalletCategory c) =>
            new PieSeries<double>
            {
                Name                  = c.Name,
                Values                = [(double)c.Amount],
                Fill                  = new SolidColorPaint(c.Color),
                Stroke                = null,
                DataLabelsPaint       = null,
                AnimationsSpeed       = TimeSpan.Zero,
                EasingFunction        = null,
                ToolTipLabelFormatter = cp => $"{c.Name}: {FormatIsk((decimal)cp.Coordinate.PrimaryValue)}",
            };

        var inc = cats.Where(c => c.IsIncome).ToList();
        var exp = cats.Where(c => !c.IsIncome).ToList();

        IncomeSeries   = inc.Select(Slice).ToArray();
        ExpenseSeries  = exp.Select(Slice).ToArray();
        HasIncomeData  = inc.Count > 0;
        HasExpenseData = exp.Count > 0;

        var incomeTotal  = inc.Sum(c => c.Amount);
        var expenseTotal = exp.Sum(c => c.Amount);
        IncomeTotalText  = incomeTotal  > 0 ? FormatIsk(incomeTotal)  : "";
        ExpenseTotalText = expenseTotal > 0 ? FormatIsk(expenseTotal) : "";
    }

    private async Task EvaluateAlertsAsync(List<long> charIds)
    {
        var newAlerts = new List<AlertRowVm>();

        bool checkEmpty    = _alertSettings.SkillQueueEmpty;
        bool checkPaused   = _alertSettings.SkillQueuePaused;
        bool checkDays     = _alertSettings.SkillQueueEmptyInDays;
        bool checkSafety   = _alertSettings.AssetSafety;
        bool checkInactive = _alertSettings.InactiveStandingProjects;

        var characters = await _db.Characters.AsNoTracking()
            .Where(c => charIds.Contains(c.Id))
            .ToListAsync();

        int warnDays   = (int)Math.Max(1, _alertSettings.SkillQueueEmptyDays);
        var warnCutoff = DateTimeOffset.UtcNow.AddDays(warnDays);
        var now        = DateTimeOffset.UtcNow;

        if (checkEmpty || checkPaused || checkDays)
        {
            foreach (var ch in characters)
            {
                var queue = await _db.EsiSkillQueue.AsNoTracking()
                    .Where(q => q.CharacterId == ch.Id && q.QueuePosition >= 0)
                    .OrderByDescending(q => q.QueuePosition)
                    .ToListAsync();

                var skillsNavCommand = NavigateToCharacterSkills is not null
                    ? ReactiveCommand.Create(() => NavigateToCharacterSkills!(ch.Name))
                    : null;

                if (checkEmpty && queue.Count == 0)
                {
                    newAlerts.Add(new AlertRowVm
                    {
                        Message = $"{ch.Name}: Skill queue is empty.",
                        NavigateCommand = skillsNavCommand,
                        Icon = await GetPortraitAsync(ch.Id)
                    });
                    continue;
                }

                if (checkPaused && queue.Count > 0)
                {
                    bool anyActive = queue.Any(q => q.FinishDate.HasValue && q.FinishDate.Value > now);
                    if (!anyActive)
                        newAlerts.Add(new AlertRowVm
                        {
                            Message = $"{ch.Name}: Skill queue is paused.",
                            NavigateCommand = skillsNavCommand,
                            Icon = await GetPortraitAsync(ch.Id)
                        });
                }

                if (checkDays && queue.Count > 0)
                {
                    var lastFinish = queue
                        .Where(q => q.FinishDate.HasValue)
                        .Select(q => q.FinishDate!.Value)
                        .DefaultIfEmpty()
                        .Max();

                    if (lastFinish != default && lastFinish <= warnCutoff)
                    {
                        var remaining = lastFinish - now;
                        string when = remaining.TotalDays >= 1
                            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
                            : $"{remaining.Hours}h {remaining.Minutes}m";
                        newAlerts.Add(new AlertRowVm
                        {
                            Message = $"{ch.Name}: Skill queue ends in {when} (within {warnDays}-day threshold).",
                            NavigateCommand = skillsNavCommand,
                            Icon = await GetPortraitAsync(ch.Id)
                        });
                    }
                }
            }
        }

        if (checkSafety)
        {
            var dismissedIds = await _db.DismissedAlerts.AsNoTracking()
                .Where(d => charIds.Contains(d.CharacterId))
                .Select(d => d.NotificationId)
                .ToHashSetAsync();

            var safetyNotifs = (await _db.EsiNotifications.AsNoTracking()
                .Where(n => charIds.Contains(n.CharacterId) &&
                            n.Type == "StructureItemsMovedIntoSafety" &&
                            !dismissedIds.Contains(n.NotificationId))
                .ToListAsync())
                .OrderBy(n => n.Timestamp)
                .ToList();

            var charMap = characters.ToDictionary(c => c.Id, c => c.Name);

            foreach (var notif in safetyNotifs)
            {
                var charName   = charMap.TryGetValue(notif.CharacterId, out var cn) ? cn : notif.CharacterId.ToString();
                var dateText   = notif.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
                var notifId    = notif.NotificationId;
                var charId     = notif.CharacterId;

                AlertRowVm? row = null;
                row = new AlertRowVm
                {
                    Message       = $"{charName}: Items moved to Asset Safety on {dateText}.",
                    IsDismissible = true,
                    DismissCommand = ReactiveCommand.CreateFromTask(async () =>
                    {
                        await _db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT OR IGNORE INTO "DismissedAlerts" ("CharacterId","NotificationId")
                            VALUES ({charId},{notifId})
                            """);
                        var toRemove = Alerts.FirstOrDefault(a => ReferenceEquals(a, row));
                        if (toRemove is not null)
                        {
                            Alerts.Remove(toRemove);
                            HasAlerts = Alerts.Count > 0;
                            this.RaisePropertyChanged(nameof(NoAlerts));
                        }
                    }),
                };
                newAlerts.Add(row);
            }
        }

        if (checkInactive && _corpActivity is not null)
        {
            // Scope to corps that actually have standing projects configured, not just
            // "personal" ones — the corp running standing projects may not be flagged personal.
            var standingCorpIds = await _db.CorpStandingProjects.AsNoTracking()
                .Select(sp => sp.CorporationId)
                .Distinct()
                .ToListAsync();

            int inactiveCount = 0;
            foreach (var corpId in standingCorpIds)
                inactiveCount += await _corpActivity.CountInactiveStandingProjectsAsync(corpId);

            if (inactiveCount > 0)
                newAlerts.Add(new AlertRowVm
                {
                    Message = inactiveCount == 1
                        ? "There is 1 standing project not currently active."
                        : $"There are {inactiveCount} standing projects not currently active.",
                    NavigateCommand = NavigateToStandingProjects is not null
                        ? ReactiveCommand.Create(NavigateToStandingProjects)
                        : null
                });
        }

        Alerts.Clear();
        foreach (var a in newAlerts) Alerts.Add(a);
        HasAlerts = Alerts.Count > 0;
        this.RaisePropertyChanged(nameof(NoAlerts));
    }

    private void ResetAllMetrics()
    {
        MktSellCount = MktSellIsk = MktBuyCount = MktBuyIsk = "—";
        CompletedJobCount = ShipKillCount = ShipLossCount = "—";
        SellOrderCount = SellOrderIsk = BuyOrderCount = BuyOrderIsk = "—";
        CtrActiveCount = ActiveJobCount = "—";
        IncomeTotalText = ExpenseTotalText = "";
        IncomeSeries = []; ExpenseSeries = [];
        HasIncomeData = HasExpenseData = false;
        Alerts.Clear(); HasAlerts = false;
        this.RaisePropertyChanged(nameof(NoAlerts));
        NewsItems.Clear(); HasNews = false;
        this.RaisePropertyChanged(nameof(NoNews));
    }

    private static string FormatIsk(decimal v) => v switch
    {
        >= 1_000_000_000m => $"{v / 1_000_000_000m:N2}B",
        >= 1_000_000m     => $"{v / 1_000_000m:N2}M",
        >= 1_000m         => $"{v / 1_000m:N1}K",
        _                 => $"{v:N0}",
    };
}
