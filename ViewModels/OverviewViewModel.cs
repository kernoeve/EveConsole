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
                _ = LoadAsync();
            });

        // Auto-refresh every 60 seconds — overview only reads local DB so this is fast.
        Observable.Interval(TimeSpan.FromSeconds(60))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(n => _ = LoadAsync());
    }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading  = true;
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
            // Count kills/losses using raw SQL JOINs to:
            //   a) verify victim or attacker is one of our authenticated characters
            //   b) deduplicate kill mail IDs that appear in both character AND corp refs
            LoadStatus = "Counting kills and losses...";
            var countedKmIds = new HashSet<int>();
            int totalKills = 0, totalLosses = 0;

            foreach (var charId in charIds)
            {
                // Losses: our character is the victim (from character refs)
                var lossIds = await _db.Database.SqlQuery<KmIdRow>(
                    $"""
                    SELECT DISTINCT d."KillMailId"
                    FROM "KillMailDetails" d
                    JOIN "EsiKillMailRefs" r ON d."KillMailId" = r."KillMailId"
                    WHERE r."OwnerType" = {"character"} AND r."OwnerId" = {charId}
                      AND d."VictimCharId" = {charId}
                      AND d."KillMailTime" >= {cutoff}
                    """
                ).ToListAsync();
                foreach (var row in lossIds)
                    if (countedKmIds.Add(row.KillMailId)) totalLosses++;

                // Kills: our character appears in the attacker list (from character refs)
                var killIds = await _db.Database.SqlQuery<KmIdRow>(
                    $"""
                    SELECT DISTINCT d."KillMailId"
                    FROM "KillMailDetails" d
                    JOIN "EsiKillMailRefs" r ON d."KillMailId" = r."KillMailId"
                    JOIN "KillMailAttackers" a ON a."KillMailId" = d."KillMailId"
                    WHERE r."OwnerType" = {"character"} AND r."OwnerId" = {charId}
                      AND a."CharacterId" = {charId}
                      AND d."VictimCharId" != {charId}
                      AND d."KillMailTime" >= {cutoff}
                    """
                ).ToListAsync();
                foreach (var row in killIds)
                    if (countedKmIds.Add(row.KillMailId)) totalKills++;
            }

            // Corp refs: pick up any kills/losses not already counted via character refs.
            // Only count if one of our authenticated characters was the actual participant.
            foreach (var corpId in corpIds)
            {
                foreach (var charId in charIds)
                {
                    // Corp-sourced losses where our character was the victim
                    var corpLossIds = await _db.Database.SqlQuery<KmIdRow>(
                        $"""
                        SELECT DISTINCT d."KillMailId"
                        FROM "KillMailDetails" d
                        JOIN "EsiKillMailRefs" r ON d."KillMailId" = r."KillMailId"
                        WHERE r."OwnerType" = {"corporation"} AND r."OwnerId" = {corpId}
                          AND d."VictimCharId" = {charId}
                          AND d."KillMailTime" >= {cutoff}
                        """
                    ).ToListAsync();
                    foreach (var row in corpLossIds)
                        if (countedKmIds.Add(row.KillMailId)) totalLosses++;

                    // Corp-sourced kills where our character was an attacker
                    var corpKillIds = await _db.Database.SqlQuery<KmIdRow>(
                        $"""
                        SELECT DISTINCT d."KillMailId"
                        FROM "KillMailDetails" d
                        JOIN "EsiKillMailRefs" r ON d."KillMailId" = r."KillMailId"
                        JOIN "KillMailAttackers" a ON a."KillMailId" = d."KillMailId"
                        WHERE r."OwnerType" = {"corporation"} AND r."OwnerId" = {corpId}
                          AND a."CharacterId" = {charId}
                          AND d."KillMailTime" >= {cutoff}
                        """
                    ).ToListAsync();
                    foreach (var row in corpKillIds)
                        if (countedKmIds.Add(row.KillMailId)) totalKills++;
                }
            }

            ShipKillCount = totalKills.ToString("N0");
            ShipLossCount = totalLosses.ToString("N0");

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

            var bountyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "bounty_prizes", "npc_bounty", "bounty_prize", "corporate_reward", "agent_bounty_prize" };
            var contractIncTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "contract_reward", "contract_price", "contract_price_payment_corp",
                  "contract_reward_refund", "contract_auction_sold" };
            var knownExpenseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "broker_fee", "brokers_fee", "transaction_tax",
                  "industry_job_tax", "manufacturing_tax",
                  "contract_deposit", "contract_sales_tax", "contract_deposit_sales_tax",
                  "planetary_import_tax", "planetary_export_tax", "planetary_construction" };

            decimal mktSell      = 0m, mktBuy       = 0m;
            decimal npcBounty    = 0m, contractInc  = 0m, contractExp = 0m, otherIncome  = 0m;
            decimal brokerFees   = 0m, txnTax       = 0m, indyTax     = 0m, otherExpense = 0m;

            foreach (var (refType, total) in journalByType)
            {
                if (refType == "market_transaction")
                {
                    if (total > 0) mktSell += total;
                    else           mktBuy  += Math.Abs(total);
                }
                else if (bountyTypes.Contains(refType))
                    { if (total > 0) npcBounty += total; }
                else if (contractIncTypes.Contains(refType))
                {
                    if (total > 0) contractInc += total;
                    else           contractExp += Math.Abs(total);
                }
                else if (refType is "broker_fee" or "brokers_fee")
                    brokerFees += Math.Abs(total);
                else if (refType == "transaction_tax")
                    txnTax += Math.Abs(total);
                else if (refType is "industry_job_tax" or "manufacturing_tax")
                    indyTax += Math.Abs(total);
                else if (!knownExpenseTypes.Contains(refType))
                {
                    if (total > 0) otherIncome  += total;
                    else           otherExpense  += Math.Abs(total);
                }
            }

            LoadStatus = "Building charts...";
            BuildPieCharts(
                mktSell, npcBounty, contractInc, otherIncome,
                mktBuy, brokerFees, txnTax, indyTax, otherExpense, contractExp);

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
        finally
        {
            IsLoading = false;
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

    private sealed class KmIdRow { public int KillMailId { get; set; } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void BuildPieCharts(
        decimal mktSell,   decimal npcBounty,  decimal contractInc, decimal otherIncome,
        decimal mktBuy,    decimal brokerFee,  decimal txnTax,      decimal indyTax,
        decimal otherExpense, decimal contractExp)
    {
        static ISeries Slice(string name, decimal value, SKColor color) =>
            new PieSeries<double>
            {
                Name                  = name,
                Values                = [(double)value],
                Fill                  = new SolidColorPaint(color),
                Stroke                = null,
                DataLabelsPaint       = null,
                AnimationsSpeed       = TimeSpan.Zero,
                EasingFunction        = null,
                ToolTipLabelFormatter = cp => $"{name}: {FormatIsk((decimal)cp.Coordinate.PrimaryValue)}",
            };

        var incSlices = new List<ISeries>();
        if (mktSell     > 0) incSlices.Add(Slice("Market Sales",    mktSell,     new SKColor(200, 168,  75)));
        if (npcBounty   > 0) incSlices.Add(Slice("NPC Bounties",    npcBounty,   new SKColor(110, 190, 100)));
        if (contractInc > 0) incSlices.Add(Slice("Contract Sales",  contractInc, new SKColor( 91, 155, 213)));
        if (otherIncome > 0) incSlices.Add(Slice("Other Income",    otherIncome, new SKColor(155, 120, 200)));

        var expSlices = new List<ISeries>();
        if (mktBuy       > 0) expSlices.Add(Slice("Market Purchases",   mktBuy,       new SKColor(200,  90,  90)));
        if (contractExp  > 0) expSlices.Add(Slice("Contract Purchases", contractExp,  new SKColor(200, 120, 160)));
        if (brokerFee    > 0) expSlices.Add(Slice("Broker Fees",        brokerFee,    new SKColor(220, 150,  60)));
        if (txnTax       > 0) expSlices.Add(Slice("Transaction Tax",    txnTax,       new SKColor(180, 180,  60)));
        if (indyTax      > 0) expSlices.Add(Slice("Industry Tax",       indyTax,      new SKColor(100, 170, 200)));
        if (otherExpense > 0) expSlices.Add(Slice("Other Expenses",     otherExpense, new SKColor(160, 100, 120)));

        IncomeSeries   = [.. incSlices];
        ExpenseSeries  = [.. expSlices];
        HasIncomeData  = incSlices.Count > 0;
        HasExpenseData = expSlices.Count > 0;

        var incomeTotal  = mktSell + npcBounty + contractInc + otherIncome;
        var expenseTotal = mktBuy  + contractExp + brokerFee + txnTax + indyTax + otherExpense;
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
