using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EveCortex.Models;
using EveCortex.Services;
using static EveCortex.Services.CorpActivityService;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using ReactiveUI;
using SkiaSharp;

namespace EveCortex.ViewModels;

// ── Shared period option ──────────────────────────────────────────────────────

public record ChartPeriodOption(string Label, int Days)
{
    public override string ToString() => Label;
}

// ── Row view-models ───────────────────────────────────────────────────────────

public sealed class CorpWalletMonthRowVm
{
    public string Month            { get; }
    public string RattingTaxText   { get; }
    public string MiningTaxText    { get; }
    public string DonationsText    { get; }
    public string IndustryTaxText  { get; }
    public string ContractIncText  { get; }
    public string MarketIncText    { get; }
    public string OtherIncText     { get; }
    public string TotalIncText     { get; }
    public string MarketExpText       { get; }
    public string ContractExpText     { get; }
    public string AcctWithdrawText    { get; }
    public string ProjectPayoutsText  { get; }
    public string OtherExpText        { get; }
    public string TotalExpText        { get; }

    public CorpWalletMonthRowVm(WalletMonthRow r)
    {
        Month                = r.Month;
        RattingTaxText       = Fmt(r.RattingTax);
        MiningTaxText        = Fmt(r.MiningTax);
        DonationsText        = Fmt(r.Donations);
        IndustryTaxText      = Fmt(r.IndustryTax);
        ContractIncText      = Fmt(r.ContractIncome);
        MarketIncText        = Fmt(r.MarketIncome);
        OtherIncText         = Fmt(r.OtherIncome);
        TotalIncText         = Fmt(r.TotalIncome);
        MarketExpText        = Fmt(r.MarketExpense);
        ContractExpText      = Fmt(r.ContractExpense);
        AcctWithdrawText     = Fmt(r.AccountWithdraw);
        ProjectPayoutsText   = Fmt(r.ProjectPayouts);
        OtherExpText         = Fmt(r.OtherExpense);
        TotalExpText         = Fmt(r.TotalExpense);
    }

    private static string Fmt(decimal v)
    {
        if (v == 0) return "—";
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{v / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)     return $"{v / 1_000_000m:F2}M";
        if (abs >= 1_000m)         return $"{v / 1_000m:F1}K";
        return $"{v:N0}";
    }
}

public sealed class CorpTopPlayerRowVm
{
    public int    Rank          { get; }
    public string CharacterName { get; }
    public string AmountText    { get; }
    public string PercentText   { get; }   // share of the category total

    public CorpTopPlayerRowVm(int rank, string name, decimal amount, bool isCount = false, double percent = 0)
    {
        Rank          = rank;
        CharacterName = name;
        AmountText    = isCount ? amount.ToString("N0")
                      : amount >= 1_000_000_000m ? $"{amount / 1_000_000_000m:F2}B"
                      : amount >= 1_000_000m     ? $"{amount / 1_000_000m:F2}M"
                      : amount >= 1_000m         ? $"{amount / 1_000m:F1}K"
                      : amount.ToString("N0");
        PercentText   = $"{percent:F1}%";
    }
}

public sealed class CorpKillCharRowVm
{
    public string CharacterName { get; }
    public string KillsText     { get; }
    public string LossesText    { get; }
    public int    KillsRaw      { get; }
    public int    LossesRaw     { get; }

    public CorpKillCharRowVm(KillCharRow r, string name)
    {
        CharacterName = name;
        KillsRaw      = r.Kills;
        LossesRaw     = r.Losses;
        KillsText     = r.Kills.ToString("N0");
        LossesText    = r.Losses.ToString("N0");
    }
}

public sealed class MonthlyActivityRowVm
{
    public string Month            { get; }
    public string TotalIncomeText  { get; }
    public string TotalExpenseText { get; }
    public string RattingTaxText   { get; }
    public string IndustryTaxText  { get; }
    public string ProjPayoutsText  { get; }
    public string UnitsMinedText   { get; }
    public string KillsText          { get; }
    public string LossesText         { get; }
    public string PlayersActiveText  { get; }
    public decimal TotalIncomeRaw  { get; }
    public decimal TotalExpenseRaw { get; }
    public decimal RattingTaxRaw   { get; }
    public decimal IndustryTaxRaw  { get; }
    public decimal ProjPayoutsRaw  { get; }
    public long   UnitsMinedRaw    { get; }
    public int    KillsRaw         { get; }
    public int    LossesRaw        { get; }
    public int    PlayersActiveRaw { get; }

    public MonthlyActivityRowVm(MonthlyActivityRow r)
    {
        Month             = r.Month;
        TotalIncomeRaw    = r.TotalIncome;
        TotalExpenseRaw   = r.TotalExpense;
        RattingTaxRaw     = r.RattingTax;
        IndustryTaxRaw    = r.IndustryTax;
        ProjPayoutsRaw    = r.ProjectPayouts;
        UnitsMinedRaw     = r.UnitsMined;
        KillsRaw          = r.Kills;
        LossesRaw         = r.Losses;
        PlayersActiveRaw  = r.PlayersActive;
        TotalIncomeText   = FmtIsk(r.TotalIncome);
        TotalExpenseText  = FmtIsk(r.TotalExpense);
        RattingTaxText    = FmtIsk(r.RattingTax);
        IndustryTaxText   = FmtIsk(r.IndustryTax);
        ProjPayoutsText   = FmtIsk(r.ProjectPayouts);
        UnitsMinedText    = r.UnitsMined.ToString("N0");
        KillsText         = r.Kills.ToString("N0");
        LossesText        = r.Losses.ToString("N0");
        PlayersActiveText = r.PlayersActive > 0 ? r.PlayersActive.ToString("N0") : "—";
    }

    private static string FmtIsk(decimal v)
    {
        if (v == 0) return "—";
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{v / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)     return $"{v / 1_000_000m:F2}M";
        if (abs >= 1_000m)         return $"{v / 1_000m:F1}K";
        return $"{v:N0}";
    }
}

public record ProjectFieldVm(string Label, string Value);

public sealed class ProjectContributorVm
{
    public int    Rank          { get; }
    public string CharacterName { get; }
    public string Contributed   { get; }
    public string PercentText   { get; }
    public string PayoutText    { get; }

    public ProjectContributorVm(int rank, string name, string contributed, string percent, string payout)
    {
        Rank          = rank;
        CharacterName = name;
        Contributed   = contributed;
        PercentText   = percent;
        PayoutText    = payout;
    }
}

public sealed class CorpProjectRowVm
{
    public CorpProject Source    { get; }
    public string Name           { get; }
    public string State          { get; }
    public string ConfigType     { get; }
    public string Career         { get; }
    public string ProgressText   { get; }
    public string RemainingRewardText { get; }
    public string CreatorText    { get; }
    public string CreatedText    { get; }
    public string CompletedText  { get; }

    public CorpProjectRowVm(CorpProject p)
    {
        Source     = p;
        Name       = p.Name;
        State      = p.State;
        ConfigType = p.ConfigType ?? "—";
        Career     = p.Career ?? "";

        double pct = p.ProgressDesired > 0
            ? (double)p.ProgressCurrent / p.ProgressDesired * 100.0
            : 0;
        ProgressText = $"{p.ProgressCurrent:N0} / {p.ProgressDesired:N0} ({pct:F0}%)";

        RemainingRewardText = p.RewardRemaining >= 1_000_000_000 ? $"{p.RewardRemaining / 1_000_000_000d:F2}B"
                            : p.RewardRemaining >= 1_000_000     ? $"{p.RewardRemaining / 1_000_000d:F1}M"
                            : p.RewardRemaining > 0              ? $"{p.RewardRemaining:N0}"
                            : "—";

        CreatorText   = p.CreatorName;
        CreatedText   = p.Created.HasValue
            ? p.Created.Value.UtcDateTime.ToString("yyyy-MM-dd") : "—";
        CompletedText = p.LastModified.UtcDateTime.ToString("yyyy-MM-dd");
    }
}

public sealed class StandingProjectRowVm
{
    public long   DbId              { get; }
    public string TypeDisplay       { get; }
    public string DescriptionText   { get; }
    public string LocationText      { get; }
    public string ProjectStatusText { get; }
    public string ProjectStatusColor { get; }
    public string RemainingText        { get; }
    public string RemainingPayoutText  { get; }
    public string RemainingPercentText { get; }
    public bool   IsLowRemaining       { get; }   // < 10% of the target left
    public string RemainingColor       { get; }
    public bool   IsDeliverItem       { get; }
    public int?   ItemTypeId          { get; }
    public string ItemTypeName        { get; }
    public ReactiveCommand<Unit, Unit> EditCommand   { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public StandingProjectRowVm(
        StandingProjectGridRow row,
        Action<long> onEdit,
        Action<long> onDelete)
    {
        DbId            = row.DbId;
        TypeDisplay     = row.TypeDisplay;
        DescriptionText = row.TargetDisplay;
        LocationText    = row.DestDisplay;

        // Less than 10% of the target left — flag the near-complete (often stuck) projects in orange.
        IsLowRemaining = row.RemainingPercentValue >= 0 && row.RemainingPercentValue < 10.0;

        string statusColor = row.MatchStatus switch
        {
            "matched"    => "#6aaa88",
            "no_systems" => "#888899",
            _            => "#cc4444",
        };
        ProjectStatusText = row.MatchStatus switch
        {
            "matched"    => row.MatchedName,
            "no_systems" => "no systems below the minimum ADM",
            _            => "project not active",
        };
        ProjectStatusColor = IsLowRemaining ? "#e0902e" : statusColor;

        RemainingText        = row.RemainingText;
        RemainingPayoutText  = row.RemainingPayoutText;
        RemainingPercentText = row.RemainingPercentText;
        RemainingColor       = IsLowRemaining ? "#e0902e" : "#c8c8d8";
        IsDeliverItem       = row.ItemTypeId.HasValue;
        ItemTypeId          = row.ItemTypeId;
        ItemTypeName        = row.ItemTypeName;
        EditCommand         = ReactiveCommand.Create(() => onEdit(row.DbId));
        DeleteCommand = ReactiveCommand.Create(() => onDelete(row.DbId));
    }
}

public record MiningMonthOption(int Year, int Month)
{
    public override string ToString() => $"{Year}-{Month:D2}";
}

public record Top10MonthOption(int Number, string Name)
{
    public override string ToString() => Name;
}

public sealed class MiningLedgerRowVm
{
    public string Date                 { get; }
    public string CharacterName        { get; }
    public string TypeName             { get; }
    public string QuantityText         { get; }
    public long   Quantity             { get; }
    public double ReprocessedValue     { get; }
    public string ReprocessedValueText { get; }

    public MiningLedgerRowVm(MiningLedgerRow r)
    {
        Date                 = r.Date;
        CharacterName        = r.CharacterName;
        TypeName             = r.TypeName;
        Quantity             = r.Quantity;
        QuantityText         = r.Quantity.ToString("N0");
        ReprocessedValue     = r.ReprocessedValue;
        ReprocessedValueText = r.ReprocessedValue > 0
            ? CorpActivityViewModel.FormatIskStatic((decimal)r.ReprocessedValue)
            : "";
    }
}

public sealed class TaxPayerRowVm
{
    public int    Rank   { get; }
    public string Name   { get; }
    public string Amount { get; }

    public TaxPayerRowVm(TaxPayerRow r)
    {
        Rank   = r.Rank;
        Name   = r.Name;
        Amount = CorpActivityViewModel.FormatIskStatic(r.Amount);
    }
}

public sealed class WalletDetailRowVm
{
    public string DateText   { get; }
    public string TimeText   { get; }
    public string TypeName   { get; }
    public string Name       { get; }
    public string AmountText { get; }
    public string ReasonText { get; }

    public WalletDetailRowVm(WalletDetailRow r)
    {
        DateText   = r.Date.UtcDateTime.ToString("yyyy-MM-dd");
        TimeText   = r.Date.UtcDateTime.ToString("HH:mm");
        TypeName   = CorpActivityViewModel.FormatRefType(r.RefType);
        Name       = r.PartyName;
        AmountText = CorpActivityViewModel.FormatIskStatic(r.Amount);
        ReasonText = r.Reason;
    }
}

public sealed class WalletTypeRowVm
{
    public string  TypeName   { get; }
    public string  CountText  { get; }
    public string  AmountText { get; }
    public decimal AmountRaw  { get; }

    public WalletTypeRowVm(WalletTypeRow r)
    {
        TypeName   = CorpActivityViewModel.FormatRefType(r.RefType);
        CountText  = r.Count.ToString("N0");
        AmountRaw  = r.Amount;
        AmountText = CorpActivityViewModel.FormatIskStatic(r.Amount);
    }
}

public sealed class Activity24hPlayerRowVm
{
    public string Name      { get; }
    public string ValueText { get; }

    public Activity24hPlayerRowVm(Activity24hPlayerRow r)
    {
        Name      = r.CharacterName;
        ValueText = CorpActivityViewModel.FormatIskStatic(r.Value);
    }
}

public sealed class Activity24hKillRowVm : ReactiveObject
{
    private readonly int  _victimShipTypeId;
    private readonly long _victimCorpId;
    private readonly long _victimAllianceId;
    private readonly long _fbCorpId;
    private readonly long _fbAllianceId;

    public int            KillMailId        { get; }
    public bool           IsLoss            { get; }
    public string         DateText          { get; }
    public string         TimeText          { get; }
    public string         ShipName          { get; }
    public string         SystemName        { get; }
    public string         ConstellationName { get; }
    public string         RegionName        { get; }
    public string         SecurityText      { get; }
    public string         SecurityColor     { get; }
    public string         VictimName        { get; }
    public string         VictimCorp        { get; }
    public string         VictimAlliance    { get; }
    public string         FbName            { get; }
    public string         FbCorp            { get; }
    public string         FbAlliance        { get; }
    public string         TotalIskText      { get; }

    // Red tint for rows that are the viewer's own loss. Bound by the Overview "Personal
    // Killmails" grid; the Corp Activity grid leaves it unbound, so its rows are unaffected.
    public IBrush RowTint => IsLoss
        ? new SolidColorBrush(Color.FromArgb(0x26, 0xcc, 0x44, 0x44))
        : Brushes.Transparent;

    private Bitmap? _shipRender;
    private Bitmap? _victimLogo;
    private Bitmap? _fbLogo;
    public Bitmap? ShipRender { get => _shipRender; private set => this.RaiseAndSetIfChanged(ref _shipRender, value); }
    public Bitmap? VictimLogo { get => _victimLogo; private set => this.RaiseAndSetIfChanged(ref _victimLogo, value); }
    public Bitmap? FbLogo     { get => _fbLogo;     private set => this.RaiseAndSetIfChanged(ref _fbLogo,     value); }

    public Activity24hKillRowVm(Activity24hKillRow r)
    {
        KillMailId        = r.KillMailId;
        IsLoss            = r.IsLoss;
        DateText          = r.Time.UtcDateTime.ToString("yyyy-MM-dd");
        TimeText          = r.Time.UtcDateTime.ToString("HH:mm");
        ShipName          = r.ShipName;
        SystemName        = r.SystemName;
        ConstellationName = r.ConstellationName;
        RegionName        = r.RegionName;
        VictimName        = r.VictimName;
        VictimCorp        = r.VictimCorp;
        VictimAlliance    = r.VictimAlliance;
        FbName            = r.FbName;
        FbCorp            = r.FbCorp;
        FbAlliance        = r.FbAlliance;
        _victimShipTypeId = r.VictimShipTypeId;
        _victimCorpId     = r.VictimCorpId;
        _victimAllianceId = r.VictimAllianceId;
        _fbCorpId         = r.FbCorpId;
        _fbAllianceId     = r.FbAllianceId;

        var sec       = r.SecurityStatus;
        SecurityText  = sec >= 0.05 ? $"{sec:F1}" : "0.0";
        SecurityColor = sec >= 0.5 ? "#44bb44" : sec >= 0.1 ? "#cccc44" : "#cc4444";
        TotalIskText  = r.IskValue > 0 ? CorpActivityViewModel.FormatIskStatic(r.IskValue) : "";
    }

    public Task LoadImagesAsync() => Task.WhenAll(
        _victimShipTypeId > 0
            ? LoadAsync($"https://images.evetech.net/types/{_victimShipTypeId}/render?size=64", v => ShipRender = v)
            : Task.CompletedTask,
        _victimAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_victimAllianceId}/logo?size=32", v => VictimLogo = v)
            : _victimCorpId > 0
                ? LoadAsync($"https://images.evetech.net/corporations/{_victimCorpId}/logo?size=32", v => VictimLogo = v)
                : Task.CompletedTask,
        _fbAllianceId > 0
            ? LoadAsync($"https://images.evetech.net/alliances/{_fbAllianceId}/logo?size=32", v => FbLogo = v)
            : _fbCorpId > 0
                ? LoadAsync($"https://images.evetech.net/corporations/{_fbCorpId}/logo?size=32", v => FbLogo = v)
                : Task.CompletedTask
    );

    private static async Task LoadAsync(string url, Action<Bitmap?> set)
    {
        var bmp = await EveImageCache.GetAsync(url);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => set(bmp));
    }
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public class CorpActivityViewModel : ReactiveObject
{
    private readonly CorpActivityService     _service;
    private readonly CorpTop10ExcludeService _excludeSvc;
    private CancellationTokenSource          _top10Cts = new();
    private int                              _refreshTick;

    // ── Corp selection ────────────────────────────────────────────────────────
    public ObservableCollection<Corporation> Corps { get; }

    private Corporation? _selectedCorp;
    public Corporation? SelectedCorp
    {
        get => _selectedCorp;
        set => this.RaiseAndSetIfChanged(ref _selectedCorp, value);
    }

    // ── Status ────────────────────────────────────────────────────────────────
    private string _status = "Select a corporation above to load data.";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private bool _isTop10Loading;
    public bool IsTop10Loading
    {
        get => _isTop10Loading;
        private set => this.RaiseAndSetIfChanged(ref _isTop10Loading, value);
    }

    // ── Wallet section ────────────────────────────────────────────────────────
    public ObservableCollection<CorpWalletMonthRowVm> WalletMonths { get; } = [];

    private bool _hasWalletData;
    public bool HasWalletData
    {
        get => _hasWalletData;
        private set => this.RaiseAndSetIfChanged(ref _hasWalletData, value);
    }

    // ── Daily chart ───────────────────────────────────────────────────────────
    public IReadOnlyList<ChartPeriodOption> ChartPeriods { get; } =
    [
        new("Last 30 Days",  30),
        new("Last 60 Days",  60),
        new("Last 90 Days",  90),
        new("Last 6 Months", 180),
        new("Last Year",     365),
    ];

    private ChartPeriodOption _selectedChartPeriod;
    public ChartPeriodOption SelectedChartPeriod
    {
        get => _selectedChartPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedChartPeriod, value);
    }

    private ISeries[] _walletDailySeries = [];
    public ISeries[] WalletDailySeries
    {
        get => _walletDailySeries;
        private set => this.RaiseAndSetIfChanged(ref _walletDailySeries, value);
    }

    private Axis[] _walletDailyXAxes = [];
    public Axis[] WalletDailyXAxes
    {
        get => _walletDailyXAxes;
        private set => this.RaiseAndSetIfChanged(ref _walletDailyXAxes, value);
    }

    private Axis[] _walletDailyYAxes = [];
    public Axis[] WalletDailyYAxes
    {
        get => _walletDailyYAxes;
        private set => this.RaiseAndSetIfChanged(ref _walletDailyYAxes, value);
    }

    private ISeries[] _walletExpenseSeries = [];
    public ISeries[] WalletExpenseSeries
    {
        get => _walletExpenseSeries;
        private set => this.RaiseAndSetIfChanged(ref _walletExpenseSeries, value);
    }

    private Axis[] _walletExpenseXAxes = [];
    public Axis[] WalletExpenseXAxes
    {
        get => _walletExpenseXAxes;
        private set => this.RaiseAndSetIfChanged(ref _walletExpenseXAxes, value);
    }

    private Axis[] _walletExpenseYAxes = [];
    public Axis[] WalletExpenseYAxes
    {
        get => _walletExpenseYAxes;
        private set => this.RaiseAndSetIfChanged(ref _walletExpenseYAxes, value);
    }

    // ── Ratting / Industry tax tabs ───────────────────────────────────────────
    public IReadOnlyList<ChartPeriodOption> TaxPeriods { get; } =
    [
        new("Last 7 Days",    7),
        new("Last 30 Days",  30),
        new("Last 90 Days",  90),
        new("Last 6 Months", 180),
        new("Last Year",     365),
    ];

    // Ratting
    private ChartPeriodOption _selectedRattingPeriod = null!;
    public ChartPeriodOption SelectedRattingPeriod
    {
        get => _selectedRattingPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedRattingPeriod, value);
    }

    public ObservableCollection<TaxPayerRowVm>      RattingTaxRows   { get; } = [];
    public ObservableCollection<WalletDetailRowVm>  RattingDetailRows { get; } = [];

    private ISeries[] _rattingDailySeries = [];
    public ISeries[] RattingDailySeries { get => _rattingDailySeries; private set => this.RaiseAndSetIfChanged(ref _rattingDailySeries, value); }
    private Axis[] _rattingDailyXAxes = [];
    public Axis[] RattingDailyXAxes    { get => _rattingDailyXAxes;  private set => this.RaiseAndSetIfChanged(ref _rattingDailyXAxes,  value); }
    private Axis[] _rattingDailyYAxes = [];
    public Axis[] RattingDailyYAxes    { get => _rattingDailyYAxes;  private set => this.RaiseAndSetIfChanged(ref _rattingDailyYAxes,  value); }

    // Donations
    private ChartPeriodOption _selectedDonationPeriod = null!;
    public ChartPeriodOption SelectedDonationPeriod
    {
        get => _selectedDonationPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedDonationPeriod, value);
    }

    public ObservableCollection<TaxPayerRowVm>      DonationRows       { get; } = [];
    public ObservableCollection<WalletDetailRowVm>  DonationDetailRows  { get; } = [];

    private ISeries[] _donationDailySeries = [];
    public ISeries[] DonationDailySeries { get => _donationDailySeries; private set => this.RaiseAndSetIfChanged(ref _donationDailySeries, value); }
    private Axis[] _donationDailyXAxes = [];
    public Axis[] DonationDailyXAxes    { get => _donationDailyXAxes;  private set => this.RaiseAndSetIfChanged(ref _donationDailyXAxes,  value); }
    private Axis[] _donationDailyYAxes = [];
    public Axis[] DonationDailyYAxes    { get => _donationDailyYAxes;  private set => this.RaiseAndSetIfChanged(ref _donationDailyYAxes,  value); }

    // Industry
    private ChartPeriodOption _selectedIndustryPeriod = null!;
    public ChartPeriodOption SelectedIndustryPeriod
    {
        get => _selectedIndustryPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedIndustryPeriod, value);
    }

    public ObservableCollection<TaxPayerRowVm>      IndustryTaxRows    { get; } = [];
    public ObservableCollection<WalletDetailRowVm>  IndustryDetailRows  { get; } = [];

    private ISeries[] _industryDailySeries = [];
    public ISeries[] IndustryDailySeries { get => _industryDailySeries; private set => this.RaiseAndSetIfChanged(ref _industryDailySeries, value); }
    private Axis[] _industryDailyXAxes = [];
    public Axis[] IndustryDailyXAxes    { get => _industryDailyXAxes;  private set => this.RaiseAndSetIfChanged(ref _industryDailyXAxes,  value); }
    private Axis[] _industryDailyYAxes = [];
    public Axis[] IndustryDailyYAxes    { get => _industryDailyYAxes;  private set => this.RaiseAndSetIfChanged(ref _industryDailyYAxes,  value); }

    // ── Killmail section ──────────────────────────────────────────────────────
    public ObservableCollection<CorpKillCharRowVm>     KillCharRows   { get; } = [];
    public ObservableCollection<Activity24hKillRowVm>  KillDetailRows { get; } = [];

    private ChartPeriodOption _selectedKillGridPeriod = null!;
    public ChartPeriodOption SelectedKillGridPeriod
    {
        get => _selectedKillGridPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedKillGridPeriod, value);
    }

    private IEnumerable<ISeries> _killDailySeries = [];
    public IEnumerable<ISeries> KillDailySeries { get => _killDailySeries; private set => this.RaiseAndSetIfChanged(ref _killDailySeries, value); }
    private Axis[] _killDailyXAxes = [];
    public Axis[] KillDailyXAxes   { get => _killDailyXAxes;  private set => this.RaiseAndSetIfChanged(ref _killDailyXAxes,  value); }
    private Axis[] _killDailyYAxes = [];
    public Axis[] KillDailyYAxes   { get => _killDailyYAxes;  private set => this.RaiseAndSetIfChanged(ref _killDailyYAxes,  value); }

    private bool _hasKillData;
    public bool HasKillData
    {
        get => _hasKillData;
        private set => this.RaiseAndSetIfChanged(ref _hasKillData, value);
    }

    // ── Monthly Activity section ──────────────────────────────────────────────
    public ObservableCollection<MonthlyActivityRowVm> MonthlyActivityRows { get; } = [];

    private IEnumerable<ISeries> _monthlyIskSeries = [];
    public IEnumerable<ISeries> MonthlyIskSeries { get => _monthlyIskSeries; private set => this.RaiseAndSetIfChanged(ref _monthlyIskSeries, value); }
    private IEnumerable<ISeries> _monthlyCountSeries = [];
    public IEnumerable<ISeries> MonthlyCountSeries { get => _monthlyCountSeries; private set => this.RaiseAndSetIfChanged(ref _monthlyCountSeries, value); }
    private Axis[] _monthlyXAxes = [];
    public Axis[] MonthlyXAxes   { get => _monthlyXAxes;  private set => this.RaiseAndSetIfChanged(ref _monthlyXAxes,  value); }
    private Axis[] _monthlyIskYAxes = [];
    public Axis[] MonthlyIskYAxes   { get => _monthlyIskYAxes;  private set => this.RaiseAndSetIfChanged(ref _monthlyIskYAxes,  value); }
    private Axis[] _monthlyCountAndMineYAxes = [];
    public Axis[] MonthlyCountAndMineYAxes { get => _monthlyCountAndMineYAxes; private set => this.RaiseAndSetIfChanged(ref _monthlyCountAndMineYAxes, value); }

    private bool _hasMonthlyData;
    public bool HasMonthlyData
    {
        get => _hasMonthlyData;
        private set => this.RaiseAndSetIfChanged(ref _hasMonthlyData, value);
    }

    // ── Mining ledger ─────────────────────────────────────────────────────────
    public ObservableCollection<MiningLedgerRowVm>  MiningLedgerRows  { get; } = [];
    public ObservableCollection<MiningLedgerRowVm>  MiningDetailRows  { get; } = [];

    private ChartPeriodOption _selectedMiningPeriod = null!;
    public ChartPeriodOption SelectedMiningPeriod
    {
        get => _selectedMiningPeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedMiningPeriod, value);
    }

    private bool _hasMiningData;
    public bool HasMiningData
    {
        get => _hasMiningData;
        private set => this.RaiseAndSetIfChanged(ref _hasMiningData, value);
    }

    // ── Income / Expense by type ─────────────────────────────────────────────
    public ObservableCollection<WalletTypeRowVm>    IncomeTypeRows    { get; } = [];
    public ObservableCollection<WalletTypeRowVm>    ExpenseTypeRows   { get; } = [];
    public ObservableCollection<WalletDetailRowVm>  ExpenseDetailRows { get; } = [];
    public ObservableCollection<WalletDetailRowVm>  IncomeDetailRows  { get; } = [];

    private ChartPeriodOption _selectedIncomePeriod = null!;
    public ChartPeriodOption SelectedIncomePeriod
    {
        get => _selectedIncomePeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedIncomePeriod, value);
    }

    private ChartPeriodOption _selectedExpensePeriod = null!;
    public ChartPeriodOption SelectedExpensePeriod
    {
        get => _selectedExpensePeriod;
        set => this.RaiseAndSetIfChanged(ref _selectedExpensePeriod, value);
    }

    private IEnumerable<ISeries> _incomeSeries = [];
    public IEnumerable<ISeries> IncomeSeries { get => _incomeSeries; private set => this.RaiseAndSetIfChanged(ref _incomeSeries, value); }
    private IEnumerable<Axis> _incomeXAxes = [];
    public IEnumerable<Axis> IncomeXAxes   { get => _incomeXAxes;  private set => this.RaiseAndSetIfChanged(ref _incomeXAxes,  value); }
    private IEnumerable<Axis> _incomeYAxes = [];
    public IEnumerable<Axis> IncomeYAxes   { get => _incomeYAxes;  private set => this.RaiseAndSetIfChanged(ref _incomeYAxes,  value); }

    private IEnumerable<ISeries> _expenseSeries = [];
    public IEnumerable<ISeries> ExpenseSeries { get => _expenseSeries; private set => this.RaiseAndSetIfChanged(ref _expenseSeries, value); }
    private IEnumerable<Axis> _expenseXAxes = [];
    public IEnumerable<Axis> ExpenseXAxes   { get => _expenseXAxes;  private set => this.RaiseAndSetIfChanged(ref _expenseXAxes,  value); }
    private IEnumerable<Axis> _expenseYAxes = [];
    public IEnumerable<Axis> ExpenseYAxes   { get => _expenseYAxes;  private set => this.RaiseAndSetIfChanged(ref _expenseYAxes,  value); }

    // ── 24h Activity ─────────────────────────────────────────────────────────
    public ObservableCollection<Activity24hPlayerRowVm> Activity24hRatters  { get; } = [];
    public ObservableCollection<Activity24hPlayerRowVm> Activity24hIndustry { get; } = [];
    public ObservableCollection<Activity24hPlayerRowVm> Activity24hMiners   { get; } = [];
    public ObservableCollection<Activity24hKillRowVm>   Activity24hKills    { get; } = [];

    private string _activity24hPlayerCountText = "—";
    public string Activity24hPlayerCountText
    {
        get => _activity24hPlayerCountText;
        private set => this.RaiseAndSetIfChanged(ref _activity24hPlayerCountText, value);
    }

    private string _activity24hIncomeText = "—";
    public string Activity24hIncomeText
    {
        get => _activity24hIncomeText;
        private set => this.RaiseAndSetIfChanged(ref _activity24hIncomeText, value);
    }

    private string _activity24hExpenseText = "—";
    public string Activity24hExpenseText
    {
        get => _activity24hExpenseText;
        private set => this.RaiseAndSetIfChanged(ref _activity24hExpenseText, value);
    }

    public Action<int>? RequestOpenKillmail { get; set; }

    // ── Projects section ──────────────────────────────────────────────────────
    public ObservableCollection<CorpProjectRowVm> ActiveProjects  { get; } = [];
    public ObservableCollection<CorpProjectRowVm> HistoryProjects { get; } = [];

    private string _activeTotalRewardText  = "";
    private string _activeRemainingRewardText = "";
    public string ActiveTotalRewardText
    {
        get => _activeTotalRewardText;
        private set => this.RaiseAndSetIfChanged(ref _activeTotalRewardText, value);
    }
    public string ActiveRemainingRewardText
    {
        get => _activeRemainingRewardText;
        private set => this.RaiseAndSetIfChanged(ref _activeRemainingRewardText, value);
    }

    private bool _hasProjectData;
    public bool HasProjectData
    {
        get => _hasProjectData;
        private set => this.RaiseAndSetIfChanged(ref _hasProjectData, value);
    }

    // ── Project detail panel ──────────────────────────────────────────────────
    private CorpProjectRowVm? _selectedActiveProject;
    public CorpProjectRowVm? SelectedActiveProject
    {
        get => _selectedActiveProject;
        set => this.RaiseAndSetIfChanged(ref _selectedActiveProject, value);
    }

    private CorpProjectRowVm? _selectedHistoryProject;
    public CorpProjectRowVm? SelectedHistoryProject
    {
        get => _selectedHistoryProject;
        set => this.RaiseAndSetIfChanged(ref _selectedHistoryProject, value);
    }

    private CorpProjectRowVm? _selectedProject;
    public CorpProjectRowVm? SelectedProject
    {
        get => _selectedProject;
        private set => this.RaiseAndSetIfChanged(ref _selectedProject, value);
    }

    private bool _hasSelectedProject;
    public bool HasSelectedProject
    {
        get => _hasSelectedProject;
        private set => this.RaiseAndSetIfChanged(ref _hasSelectedProject, value);
    }

    public ObservableCollection<ProjectFieldVm>       ProjectInfoFields   { get; } = [];
    public ObservableCollection<ProjectFieldVm>       ProjectConfigFields { get; } = [];
    public ObservableCollection<ProjectContributorVm> ProjectContributors { get; } = [];

    private bool _hasProjectConfig;
    public bool HasProjectConfig
    {
        get => _hasProjectConfig;
        private set => this.RaiseAndSetIfChanged(ref _hasProjectConfig, value);
    }

    // ── Standing projects (MAINTAIN tab) ─────────────────────────────────────
    public ObservableCollection<StandingProjectRowVm> StandingProjectRows { get; } = [];
    public bool HasNoStandingProjects => StandingProjectRows.Count == 0;

    private bool _isLoadingMaintain;
    public bool IsLoadingMaintain
    {
        get => _isLoadingMaintain;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingMaintain, value);
    }

    private int _projectsInnerTabIndex;
    public int ProjectsInnerTabIndex
    {
        get => _projectsInnerTabIndex;
        set => this.RaiseAndSetIfChanged(ref _projectsInnerTabIndex, value);
    }

    // Index of the outer TabControl's "PROJECTS" tab — used to jump straight there from
    // an Overview alert (e.g. inactive standing projects).
    public const int ProjectsOuterTabIndex = 9;

    private int _selectedOuterTabIndex;
    public int SelectedOuterTabIndex
    {
        get => _selectedOuterTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedOuterTabIndex, value);
    }

    public void ShowStandingProjectsTab()
    {
        SelectedOuterTabIndex = ProjectsOuterTabIndex;
        ProjectsInnerTabIndex = 2;
    }

    public bool ShowProjectDetailPanel => _projectsInnerTabIndex != 2;

    public Func<CorpStandingProject?, Task<CorpStandingProject?>>? ShowStandingProjectDialog { get; set; }
    public Func<Task<bool>>? ConfirmDelete { get; set; }
    internal CorpActivityService Service => _service;

    public ReactiveCommand<Unit, Unit> AddStandingProjectCommand          { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CloneStandingProjectCommand        { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RefreshMaintainCommand             { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenMaintainItemInBrowserCommand   { get; private set; } = null!;

    public Action<int, string>? RequestOpenInItemBrowser { get; set; }

    private StandingProjectRowVm? _selectedMaintainRow;
    public StandingProjectRowVm? SelectedMaintainRow
    {
        get => _selectedMaintainRow;
        set => this.RaiseAndSetIfChanged(ref _selectedMaintainRow, value);
    }

    // ── Top 10 lists ──────────────────────────────────────────────────────────
    public ObservableCollection<CorpTopPlayerRowVm> TopRatters      { get; } = [];
    public ObservableCollection<CorpTopPlayerRowVm> TopDonors       { get; } = [];
    public ObservableCollection<CorpTopPlayerRowVm> TopMiners       { get; } = [];
    public ObservableCollection<CorpTopPlayerRowVm> TopKillers      { get; } = [];
    public ObservableCollection<CorpTopPlayerRowVm> TopContributors { get; } = [];
    public ObservableCollection<CorpTopPlayerRowVm> TopIndustry     { get; } = [];

    // ── Top 10 period selector ────────────────────────────────────────────────
    public IReadOnlyList<int>              Top10Years  { get; }
    public IReadOnlyList<Top10MonthOption> Top10Months { get; }

    private int _selectedTop10Year;
    public int SelectedTop10Year
    {
        get => _selectedTop10Year;
        set => this.RaiseAndSetIfChanged(ref _selectedTop10Year, value);
    }

    private Top10MonthOption? _selectedTop10Month;
    public Top10MonthOption? SelectedTop10Month
    {
        get => _selectedTop10Month;
        set => this.RaiseAndSetIfChanged(ref _selectedTop10Month, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public CorpActivityViewModel(CorpActivityService service,
                                 ObservableCollection<Corporation> corps,
                                 CorpTop10ExcludeService? excludeSvc = null)
    {
        _service    = service;
        _excludeSvc = excludeSvc!;
        Corps       = corps;
        _selectedChartPeriod    = ChartPeriods[2];  // default 90 days
        _selectedRattingPeriod  = TaxPeriods[1];    // default 30 days
        _selectedDonationPeriod = TaxPeriods[1];    // default 30 days
        _selectedIndustryPeriod = TaxPeriods[1];    // default 30 days
        _selectedMiningPeriod   = TaxPeriods[1];    // default 30 days
        _selectedKillGridPeriod = TaxPeriods[1];    // default 30 days
        _selectedIncomePeriod   = TaxPeriods[1];    // default 30 days
        _selectedExpensePeriod  = TaxPeriods[1];    // default 30 days

        var now     = DateTimeOffset.UtcNow;
        Top10Years  = Enumerable.Range(now.Year - 2, 3).OrderByDescending(y => y).ToList();
        Top10Months = Enumerable.Range(1, 12)
            .Select(m => new Top10MonthOption(m, new DateTime(2000, m, 1).ToString("MMMM")))
            .ToList();
        _selectedTop10Year  = now.Year;
        _selectedTop10Month = Top10Months[now.Month - 1];

        // Auto-select first corp if already available
        if (Corps.Count > 0)
            SelectedCorp = Corps[0];

        // Also auto-select when the corps list first populates (e.g. after auth)
        Corps.CollectionChanged += (_, _) =>
        {
            if (SelectedCorp is null && Corps.Count > 0)
                SelectedCorp = Corps[0];
        };

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });

        AddStandingProjectCommand = ReactiveCommand.CreateFromTask(AddStandingProjectAsync);
        AddStandingProjectCommand.ThrownExceptions.Subscribe(_ => { });

        CloneStandingProjectCommand = ReactiveCommand.CreateFromTask(
            CloneStandingProjectAsync,
            this.WhenAnyValue(x => x.SelectedMaintainRow).Select(r => r is not null));
        CloneStandingProjectCommand.ThrownExceptions.Subscribe(_ => { });

        OpenMaintainItemInBrowserCommand = ReactiveCommand.Create(
            () => { if (SelectedMaintainRow is { IsDeliverItem: true, ItemTypeId: { } id })
                        RequestOpenInItemBrowser?.Invoke(id, SelectedMaintainRow.ItemTypeName); },
            this.WhenAnyValue(x => x.SelectedMaintainRow)
                .Select(r => r is { IsDeliverItem: true }));
        OpenMaintainItemInBrowserCommand.ThrownExceptions.Subscribe(_ => { });

        RefreshMaintainCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedCorp is not null)
                await LoadStandingProjectsAsync((long)SelectedCorp.Id);
        });
        RefreshMaintainCommand.ThrownExceptions.Subscribe(_ => { });

        this.WhenAnyValue(x => x.ProjectsInnerTabIndex)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowProjectDetailPanel)));

        StandingProjectRows.CollectionChanged += (_, _)
            => this.RaisePropertyChanged(nameof(HasNoStandingProjects));

        this.WhenAnyValue(x => x.SelectedCorp)
            .Where(c => c is not null)
            .Subscribe(c => { _ = LoadAsync(); });

        this.WhenAnyValue(x => x.SelectedChartPeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null && !IsLoading)
            .Subscribe(p => { _ = LoadDailyChartAsync((long)SelectedCorp!.Id); });

        this.WhenAnyValue(x => x.SelectedMiningPeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => _ = ReloadTabSafeAsync("mining", () => LoadMiningLedgerAsync((long)SelectedCorp!.Id)));

        this.WhenAnyValue(x => x.SelectedRattingPeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => { _ = ReloadTabSafeAsync("ratting", () => LoadRattingTabAsync((long)SelectedCorp!.Id)); });

        this.WhenAnyValue(x => x.SelectedIndustryPeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => { _ = ReloadTabSafeAsync("industry", () => LoadIndustryTabAsync((long)SelectedCorp!.Id)); });

        this.WhenAnyValue(x => x.SelectedDonationPeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => { _ = ReloadTabSafeAsync("donations", () => LoadDonationTabAsync((long)SelectedCorp!.Id)); });

        this.WhenAnyValue(x => x.SelectedKillGridPeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => _ = ReloadTabSafeAsync("kills", () => LoadKillsTabAsync((long)SelectedCorp!.Id, default)));

        this.WhenAnyValue(x => x.SelectedIncomePeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => _ = ReloadTabSafeAsync("income", () => LoadIncomeByTypeAsync((long)SelectedCorp!.Id)));

        this.WhenAnyValue(x => x.SelectedExpensePeriod)
            .Skip(1)
            .Where(p => p is not null && SelectedCorp is not null)
            .Subscribe(p => _ = ReloadTabSafeAsync("expense", () => LoadExpenseByTypeAsync((long)SelectedCorp!.Id)));

        // Project selection — either grid drives the unified detail panel
        this.WhenAnyValue(x => x.SelectedActiveProject)
            .Where(p => p is not null)
            .Subscribe(p =>
            {
                _selectedHistoryProject = null;
                this.RaisePropertyChanged(nameof(SelectedHistoryProject));
                SelectedProject = p;
            });

        this.WhenAnyValue(x => x.SelectedHistoryProject)
            .Where(p => p is not null)
            .Subscribe(p =>
            {
                _selectedActiveProject = null;
                this.RaisePropertyChanged(nameof(SelectedActiveProject));
                SelectedProject = p;
            });

        this.WhenAnyValue(x => x.SelectedProject)
            .Subscribe(p =>
            {
                HasSelectedProject = p is not null;
                if (p is not null && SelectedCorp is not null)
                    _ = LoadProjectDetailAsync((long)SelectedCorp.Id, p);
                else
                {
                    ProjectInfoFields.Clear();
                    ProjectConfigFields.Clear();
                    ProjectContributors.Clear();
                    HasProjectConfig = false;
                }
            });

        this.WhenAnyValue(x => x.SelectedTop10Year, x => x.SelectedTop10Month)
            .Skip(1)
            .Where(t => SelectedCorp is not null && t.Item2 is not null)
            .Subscribe(t =>
            {
                _top10Cts.Cancel();
                _top10Cts.Dispose();
                _top10Cts = new CancellationTokenSource();
                _ = SwitchTop10PeriodAsync((long)SelectedCorp!.Id, _top10Cts.Token);
            });

        // Light refresh every 60 s; full refresh (including detail tabs) every 5 min (tick 5).
        Observable.Interval(TimeSpan.FromSeconds(60))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(_ => SelectedCorp is not null && !IsLoading)
            .Subscribe(_ =>
            {
                _refreshTick++;
                var t = _refreshTick % 5 == 0 ? LoadAsync() : LoadLightAsync();
            });
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoading || SelectedCorp is null) return;
        IsLoading = true;
        var corpId     = (long)SelectedCorp.Id;
        var excludeIds = _excludeSvc.GetExcludeIds();
        try
        {
            await RunStep("wallet",           () => LoadWalletAsync(corpId, ct));
            await RunStep("daily chart",      () => LoadDailyChartAsync(corpId, ct));
            await RunStep("kills",            () => LoadKillsTabAsync(corpId, ct));
            await RunStep("monthly activity", () => LoadMonthlyActivityAsync(corpId, ct));
            await RunStep("projects",    () => LoadProjectsAsync(corpId, ct));

            var (since, until) = GetTop10DateRange();
            await RunStep("top 10",   () => LoadAllTop10Async(corpId, excludeIds, since, until, ct));
            await RunStep("mining",   () => LoadMiningLedgerAsync(corpId, ct));
            await RunStep("ratting",   () => LoadRattingTabAsync(corpId, ct));
            await RunStep("donations", () => LoadDonationTabAsync(corpId, ct));
            await RunStep("industry",  () => LoadIndustryTabAsync(corpId, ct));
            await RunStep("income by type",  () => LoadIncomeByTypeAsync(corpId, ct));
            await RunStep("expense by type", () => LoadExpenseByTypeAsync(corpId, ct));
            await RunStep("24h activity",    () => Load24hActivityAsync(corpId, ct));

            Status = $"Loaded — {SelectedCorp.Name}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Light refresh for the 60s timer — skips detail journal rows in each tab.
    private async Task LoadLightAsync(CancellationToken ct = default)
    {
        if (IsLoading || SelectedCorp is null) return;
        IsLoading = true;
        var corpId     = (long)SelectedCorp.Id;
        var excludeIds = _excludeSvc.GetExcludeIds();
        try
        {
            await RunStep("wallet",           () => LoadWalletAsync(corpId, ct));
            await RunStep("daily chart",      () => LoadDailyChartAsync(corpId, ct));
            await RunStep("kills",            () => LoadKillsTabAsync(corpId, ct));
            await RunStep("monthly activity", () => LoadMonthlyActivityAsync(corpId, ct));
            await RunStep("projects",         () => LoadProjectsAsync(corpId, ct));

            var (since, until) = GetTop10DateRange();
            await RunStep("top 10",  () => LoadAllTop10Async(corpId, excludeIds, since, until, ct));
            await RunStep("mining",  () => LoadMiningLedgerAsync(corpId, ct));
            await RunStep("24h activity", () => Load24hActivityAsync(corpId, ct));

            Status = $"Loaded — {SelectedCorp.Name}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RunStep(string name, Func<Task> step)
    {
        try
        {
            Status = $"Loading {name}...";
            await step();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CorpActivity] {name} step failed: {ex}");
            Status = $"Warning: {name} failed — {ex.Message}";
        }
    }

    private async Task ReloadTabSafeAsync(string name, Func<Task> load)
    {
        try
        {
            Status = $"Refreshing {name}...";
            await load();
            Status = SelectedCorp is not null ? $"Loaded — {SelectedCorp.Name}" : "Ready";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CorpActivity] period reload {name} failed: {ex}");
            Status = $"Warning: {name} reload failed — {ex.Message}";
        }
    }

    private async Task LoadWalletAsync(long corpId, CancellationToken ct)
    {
        var months = await _service.GetWalletMonthsAsync(corpId, 12, ct);
        WalletMonths.Clear();
        foreach (var m in months) WalletMonths.Add(new CorpWalletMonthRowVm(m));
        HasWalletData = WalletMonths.Count > 0;
    }

    private async Task LoadDailyChartAsync(long corpId, CancellationToken ct = default)
    {
        var incRows = await _service.GetDailyWalletAsync(corpId, SelectedChartPeriod.Days, ct);
        var expRows = await _service.GetDailyExpenseWalletAsync(corpId, SelectedChartPeriod.Days, ct);
        BuildDailyChart(incRows);
        BuildExpenseChart(expRows);
    }

    private static (Axis[] xAxes, Axis[] yAxes) BuildChartAxes(int rowCount)
    {
        bool moreThan60 = rowCount > 60;
        return
        (
            [
                new DateTimeAxis(TimeSpan.FromDays(1),
                    d => d.ToString(moreThan60 ? "MMM yy" : "MM/dd"))
                {
                    TextSize        = 10,
                    LabelsPaint     = new SolidColorPaint(new SKColor(140, 140, 155)),
                    SeparatorsPaint = new SolidColorPaint(new SKColor(40,  40,  60)),
                },
            ],
            [
                new Axis
                {
                    TextSize        = 10,
                    LabelsPaint     = new SolidColorPaint(new SKColor(140, 140, 155)),
                    SeparatorsPaint = new SolidColorPaint(new SKColor(40,  40,  60)),
                    Labeler         = v => FormatIsk(v),
                },
            ]
        );
    }

    private void BuildDailyChart(List<WalletDayRow> rows)
    {
        if (rows.Count == 0) { WalletDailySeries = []; WalletDailyXAxes = []; WalletDailyYAxes = []; return; }
        var totals = rows.Select(r => new DailyAmountRow(r.Day,
            r.RattingTax + r.MiningTax + r.Donations + r.IndustryTax +
            r.ContractIncome + r.MarketIncome + r.OtherIncome)).ToList();
        (WalletDailySeries, WalletDailyXAxes, WalletDailyYAxes) =
            BuildTaxChart(totals, new SKColor(106, 170, 136));
    }

    private void BuildExpenseChart(List<WalletExpenseDayRow> rows)
    {
        if (rows.Count == 0) { WalletExpenseSeries = []; WalletExpenseXAxes = []; WalletExpenseYAxes = []; return; }
        var totals = rows.Select(r => new DailyAmountRow(r.Day,
            r.MarketExpense + r.ContractExpense + r.AccountWithdraw + r.ProjectPayouts + r.OtherExpense)).ToList();
        (WalletExpenseSeries, WalletExpenseXAxes, WalletExpenseYAxes) =
            BuildTaxChart(totals, new SKColor(204, 119, 102));
    }

    private (DateTimeOffset Since, DateTimeOffset Until) GetTop10DateRange()
    {
        var month = SelectedTop10Month ?? Top10Months[DateTimeOffset.UtcNow.Month - 1];
        var since = new DateTimeOffset(SelectedTop10Year, month.Number, 1, 0, 0, 0, TimeSpan.Zero);
        return (since, since.AddMonths(1));
    }

    private void ClearTop10Lists()
    {
        TopRatters.Clear();
        TopIndustry.Clear();
        TopKillers.Clear();
        TopMiners.Clear();
        TopContributors.Clear();
    }

    // includeIsk true → "rank  name\tamount"; false → "rank  name\t%" (name + share only).
    public string BuildTop10Export() => BuildTop10Export(includeIsk: true);
    public string BuildTop10ExportNoIsk() => BuildTop10Export(includeIsk: false);

    private string BuildTop10Export(bool includeIsk)
    {
        var month = SelectedTop10Month?.Name ?? "?";
        var year  = SelectedTop10Year;
        var header = $"Top 10 — {month} {year}";

        var sb = new System.Text.StringBuilder();

        // alwaysAmount forces the count column even in the no-ISK export — Kills has no ISK value,
        // so its "amount" is the kill count and there is nothing meaningful to show as a percentage.
        void AppendList(string title, IEnumerable<CorpTopPlayerRowVm> rows, bool alwaysAmount = false)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('=', Math.Max(title.Length, 32)));
            foreach (var r in rows)
            {
                var rank = $"{r.Rank,2}.";
                var name = r.CharacterName.PadRight(28);
                sb.AppendLine($"{rank}  {name}\t{(includeIsk || alwaysAmount ? r.AmountText : r.PercentText)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(header);
        sb.AppendLine(new string('=', Math.Max(header.Length, 32)));
        sb.AppendLine();

        AppendList("Ratting Tax",           TopRatters);
        AppendList("Mining — Reprocessed Value", TopMiners);
        AppendList("Kills",                 TopKillers, alwaysAmount: true);
        AppendList("Project Contributors",  TopContributors);
        AppendList("Industry Tax",          TopIndustry);

        return sb.ToString().TrimEnd();
    }

    private async Task SwitchTop10PeriodAsync(long corpId, CancellationToken ct)
    {
        ClearTop10Lists();
        IsTop10Loading = true;
        try
        {
            var (since, until) = GetTop10DateRange();
            await LoadAllTop10Async(corpId, _excludeSvc.GetExcludeIds(), since, until, ct);
        }
        catch (OperationCanceledException) { /* user switched month again — discard */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CorpActivity] Top 10 switch failed: {ex}");
        }
        finally
        {
            IsTop10Loading = false;
        }
    }

    private async Task LoadAllTop10Async(long corpId, IReadOnlySet<long> excludeIds,
        DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default)
    {
        List<RankedPlayerRow> rattingRows  = [];
        List<RankedPlayerRow> industryRows = [];
        List<RankedPlayerRow> killerRows   = [];
        List<RankedPlayerRow> minerRows    = [];
        List<(long CharacterId, string Name, decimal IskPayout, double Percent)> contribRows = [];

        try { rattingRows  = await _service.GetTopRattersAsync(corpId, since, until, excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Top10] ratters failed: {ex.Message}"); }

        try { industryRows = await _service.GetTopIndustryAsync(corpId, since, until, excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Top10] industry failed: {ex.Message}"); }

        try { killerRows   = await _service.GetTopKillersAsync(corpId, since, until, excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Top10] killers failed: {ex.Message}"); }

        try { minerRows    = await _service.GetTopMinersAsync(corpId, since, until, excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Top10] miners failed: {ex.Message}"); }

        try { contribRows  = await _service.GetTopProjectContributorsAsync(corpId, since, until, excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Top10] contributors failed: {ex.Message}"); }

        var walletIds  = rattingRows.Concat(industryRows).Concat(killerRows)
                                    .Select(r => r.CharacterId);
        var allNames   = await _service.ResolveNamesAsync(walletIds, ct);
        string Resolve(long id) => allNames.TryGetValue(id, out var n) ? n : id.ToString();

        PopulateTopList(TopRatters,  rattingRows,  Resolve, isCount: false);
        PopulateTopList(TopIndustry, industryRows, Resolve, isCount: false);
        PopulateTopList(TopKillers,  killerRows,   Resolve, isCount: true);

        var minerNames = await _service.ResolveNamesAsync(minerRows.Select(r => r.CharacterId), ct);
        string ResolveMiner(long id) => minerNames.TryGetValue(id, out var n) ? n : id.ToString();
        PopulateTopList(TopMiners, minerRows, ResolveMiner, isCount: false);

        TopContributors.Clear();
        for (int i = 0; i < contribRows.Count; i++)
        {
            var (_, name, iskPayout, pct) = contribRows[i];
            int rank = contribRows.Count(r => r.IskPayout > iskPayout) + 1;
            TopContributors.Add(new CorpTopPlayerRowVm(rank, name, iskPayout, isCount: false, pct));
        }
    }

    private async Task LoadMiningLedgerAsync(long corpId, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-SelectedMiningPeriod.Days);
        var rows  = await _service.GetMiningLedgerAsync(corpId, since, ct);
        MiningLedgerRows.Clear();
        foreach (var r in rows.OrderByDescending(r => r.ReprocessedValue))
            MiningLedgerRows.Add(new MiningLedgerRowVm(r));
        HasMiningData = MiningLedgerRows.Count > 0;

        MiningDetailRows.Clear();
        foreach (var r in rows.OrderByDescending(r => r.Date))
            MiningDetailRows.Add(new MiningLedgerRowVm(r));
    }

    private async Task LoadRattingTabAsync(long corpId, CancellationToken ct = default)
    {
        var since    = DateTimeOffset.UtcNow.AddDays(-SelectedRattingPeriod.Days);
        var gridRows = await _service.GetRattingTaxPayersAsync(corpId, since, DateTimeOffset.UtcNow, ct);
        RattingTaxRows.Clear();
        foreach (var r in gridRows) RattingTaxRows.Add(new TaxPayerRowVm(r));

        var chartRows = await _service.GetDailyRattingTaxAsync(corpId, SelectedRattingPeriod.Days, ct);
        (RattingDailySeries, RattingDailyXAxes, RattingDailyYAxes) =
            BuildTaxChart(chartRows, new SKColor(110, 190, 100));

        var detailRows = await _service.GetRattingJournalAsync(corpId, since, ct);
        RattingDetailRows.Clear();
        foreach (var r in detailRows) RattingDetailRows.Add(new WalletDetailRowVm(r));
    }

    private async Task LoadDonationTabAsync(long corpId, CancellationToken ct = default)
    {
        var since    = DateTimeOffset.UtcNow.AddDays(-SelectedDonationPeriod.Days);
        var gridRows = await _service.GetDonationPayersAsync(corpId, since, DateTimeOffset.UtcNow, ct);
        DonationRows.Clear();
        foreach (var r in gridRows) DonationRows.Add(new TaxPayerRowVm(r));

        var chartRows = await _service.GetDailyDonationsAsync(corpId, SelectedDonationPeriod.Days, ct);
        (DonationDailySeries, DonationDailyXAxes, DonationDailyYAxes) =
            BuildTaxChart(chartRows, new SKColor(100, 160, 210));

        var detailRows = await _service.GetDonationJournalAsync(corpId, since, ct);
        DonationDetailRows.Clear();
        foreach (var r in detailRows) DonationDetailRows.Add(new WalletDetailRowVm(r));
    }

    private async Task LoadIndustryTabAsync(long corpId, CancellationToken ct = default)
    {
        var since    = DateTimeOffset.UtcNow.AddDays(-SelectedIndustryPeriod.Days);
        var gridRows = await _service.GetIndustryTaxPayersAsync(corpId, since, DateTimeOffset.UtcNow, ct);
        IndustryTaxRows.Clear();
        foreach (var r in gridRows) IndustryTaxRows.Add(new TaxPayerRowVm(r));

        var chartRows = await _service.GetDailyIndustryTaxAsync(corpId, SelectedIndustryPeriod.Days, ct);
        (IndustryDailySeries, IndustryDailyXAxes, IndustryDailyYAxes) =
            BuildTaxChart(chartRows, new SKColor(200, 140, 60));

        var detailRows = await _service.GetIndustryJournalAsync(corpId, since, ct);
        IndustryDetailRows.Clear();
        foreach (var r in detailRows) IndustryDetailRows.Add(new WalletDetailRowVm(r));
    }

    private async Task LoadIncomeByTypeAsync(long corpId, CancellationToken ct = default)
    {
        var rows = await _service.GetIncomeByTypeAsync(corpId, SelectedIncomePeriod.Days, ct);
        IncomeTypeRows.Clear();
        foreach (var r in rows) IncomeTypeRows.Add(new WalletTypeRowVm(r));
        BuildTypeBarChart(rows, new SKColor(106, 170, 136),
            out var series, out var xAxes, out var yAxes);
        IncomeSeries = series;
        IncomeXAxes  = xAxes;
        IncomeYAxes  = yAxes;

        var detailRows = await _service.GetIncomeJournalAsync(corpId, SelectedIncomePeriod.Days, ct);
        IncomeDetailRows.Clear();
        foreach (var r in detailRows) IncomeDetailRows.Add(new WalletDetailRowVm(r));
    }

    private async Task LoadExpenseByTypeAsync(long corpId, CancellationToken ct = default)
    {
        var rows = await _service.GetExpenseByTypeAsync(corpId, SelectedExpensePeriod.Days, ct);
        ExpenseTypeRows.Clear();
        foreach (var r in rows) ExpenseTypeRows.Add(new WalletTypeRowVm(r));
        BuildTypeBarChart(rows, new SKColor(204, 119, 102),
            out var series, out var xAxes, out var yAxes);
        ExpenseSeries = series;
        ExpenseXAxes  = xAxes;
        ExpenseYAxes  = yAxes;

        var detail = await _service.GetExpenseJournalAsync(corpId, SelectedExpensePeriod.Days, ct);
        ExpenseDetailRows.Clear();
        foreach (var r in detail) ExpenseDetailRows.Add(new WalletDetailRowVm(r));
    }

    private static void BuildTypeBarChart(List<WalletTypeRow> rows, SKColor color,
        out IEnumerable<ISeries> series, out IEnumerable<Axis> xAxes, out IEnumerable<Axis> yAxes)
    {
        if (rows.Count == 0) { series = []; xAxes = []; yAxes = []; return; }

        var labels  = rows.Select(r => FormatRefType(r.RefType)).ToArray();
        var amounts = rows.Select(r => (double)r.Amount).ToArray();

        series = [
            new ColumnSeries<double>
            {
                Name   = "Amount",
                Values = amounts,
                Fill   = new SolidColorPaint(color),
                YToolTipLabelFormatter = p => FormatIsk(p.Coordinate.PrimaryValue),
            }
        ];
        xAxes = [
            new Axis
            {
                Labels          = labels,
                LabelsRotation  = -35,
                TextSize        = 10,
                LabelsPaint     = new SolidColorPaint(new SKColor(140, 140, 155)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(40,  40,  60)),
            }
        ];
        yAxes = [
            new Axis
            {
                TextSize        = 10,
                MinLimit        = 0,
                LabelsPaint     = new SolidColorPaint(new SKColor(140, 140, 155)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(40,  40,  60)),
                Labeler         = v => FormatIsk(v),
            }
        ];
    }

    private async Task Load24hActivityAsync(long corpId, CancellationToken ct = default)
    {
        var excludeIds = _excludeSvc.GetExcludeIds();

        Activity24hSummary summary = new(0, 0, 0);
        List<Activity24hPlayerRow> ratters  = [];
        List<Activity24hPlayerRow> industry = [];
        List<Activity24hPlayerRow> miners   = [];
        List<Activity24hKillRow>   kills    = [];

        try { summary  = await _service.Get24hSummaryAsync(corpId, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[24h] summary failed: {ex.Message}"); }

        try { ratters  = await _service.Get24hTopRattersAsync(corpId,  excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[24h] ratters failed: {ex.Message}"); }

        try { industry = await _service.Get24hTopIndustryAsync(corpId, excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[24h] industry failed: {ex.Message}"); }

        try { miners   = await _service.Get24hTopMinersAsync(corpId,   excludeIds, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[24h] miners failed: {ex.Message}"); }

        try { kills    = await _service.Get24hKillsAsync(corpId, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[24h] kills failed: {ex.Message}"); }

        Activity24hPlayerCountText = summary.PlayerCount.ToString("N0");
        Activity24hIncomeText      = FormatIskStatic(summary.TotalIncome);
        Activity24hExpenseText     = FormatIskStatic(summary.TotalExpense);

        Activity24hRatters.Clear();
        foreach (var r in ratters)  Activity24hRatters.Add(new Activity24hPlayerRowVm(r));

        Activity24hIndustry.Clear();
        foreach (var r in industry) Activity24hIndustry.Add(new Activity24hPlayerRowVm(r));

        Activity24hMiners.Clear();
        foreach (var r in miners)   Activity24hMiners.Add(new Activity24hPlayerRowVm(r));

        Activity24hKills.Clear();
        foreach (var r in kills) Activity24hKills.Add(new Activity24hKillRowVm(r));
        _ = Task.WhenAll(Activity24hKills.Select(k => k.LoadImagesAsync()));
    }

    internal static string FormatRefType(string r) => r switch
    {
        "bounty_prizes" or "bounty_prize"    => "Bounty Prizes",
        "ess_escrow_transfer"                => "ESS Transfer",
        "daily_goal_payouts"                 => "Daily Goal Payout",
        "mining_tax"                         => "Mining Tax",
        "player_donation"                    => "Player Donation",
        "corporate_reward_payout"            => "Corp Reward",
        "industry_job_tax"                   => "Industry Tax",
        "manufacturing_tax"                  => "Manufacturing Tax",
        "reprocessing_tax"                   => "Reprocessing Tax",
        "contract_price"                     => "Contract Income",
        "contract_price_payment_corp"        => "Corp Contract",
        "market_transaction"                 => "Market Transaction",
        "market_escrow"                      => "Market Escrow",
        "project_payouts"                    => "Project Payouts",
        _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo
                   .ToTitleCase(r.Replace('_', ' ')),
    };

    private static (ISeries[] Series, Axis[] XAxes, Axis[] YAxes) BuildTaxChart(
        List<DailyAmountRow> rows, SKColor color)
    {
        if (rows.Count == 0) return ([], [], []);

        ISeries[] series =
        [
            new LineSeries<DateTimePoint>
            {
                Name           = "Total",
                Values         = rows.Select(r => new DateTimePoint(
                                     DateTime.Parse(r.Day), (double)r.Amount)).ToList(),
                Stroke         = new SolidColorPaint(color) { StrokeThickness = 2 },
                Fill           = null,
                GeometrySize   = 0,
                GeometryFill   = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => FormatIsk(p.Coordinate.PrimaryValue),
            }
        ];
        var (xAxes, yAxes) = BuildChartAxes(rows.Count);
        return (series, xAxes, yAxes);
    }

    private async Task LoadKillsTabAsync(long corpId, CancellationToken ct)
    {
        var days     = _selectedKillGridPeriod?.Days ?? 30;
        var charRows = await _service.GetKillCharactersAsync(corpId, days, ct);
        var allIds   = charRows.Select(r => r.CharacterId).ToList();
        var names    = allIds.Count > 0
            ? await _service.ResolveNamesAsync(allIds, ct)
            : new Dictionary<long, string>();

        KillCharRows.Clear();
        foreach (var r in charRows)
        {
            var name = names.TryGetValue(r.CharacterId, out var n) ? n : $"Character {r.CharacterId}";
            KillCharRows.Add(new CorpKillCharRowVm(r, name));
        }
        HasKillData = KillCharRows.Count > 0;

        var dailyRows = await _service.GetKillDailyAsync(corpId, days, ct);
        BuildKillChart(dailyRows);

        var killDetail = await _service.GetKillsForPeriodAsync(corpId, days, ct);
        KillDetailRows.Clear();
        foreach (var r in killDetail)
        {
            var vm = new Activity24hKillRowVm(r);
            KillDetailRows.Add(vm);
        }
        _ = Task.WhenAll(KillDetailRows.Select(k => k.LoadImagesAsync()));
    }

    private void BuildKillChart(List<KillDayRow> rows)
    {
        if (rows.Count == 0) { KillDailySeries = []; KillDailyXAxes = []; KillDailyYAxes = []; return; }

        var labels = rows.Select(r => r.Day).ToArray();
        var killVals  = rows.Select(r => (double)r.Kills).ToArray();
        var lossVals  = rows.Select(r => (double)r.Losses).ToArray();

        KillDailySeries =
        [
            new LineSeries<double>
            {
                Name = "Kills", Values = killVals,
                Stroke = new SolidColorPaint(new SKColor(106, 170, 136), 2),
                Fill   = null, GeometrySize = 0, EasingFunction = null,
            },
            new LineSeries<double>
            {
                Name = "Losses", Values = lossVals,
                Stroke = new SolidColorPaint(new SKColor(204, 100, 100), 2),
                Fill   = null, GeometrySize = 0, EasingFunction = null,
            },
        ];
        KillDailyXAxes =
        [
            new Axis
            {
                Labels = labels, LabelsRotation = -45,
                TextSize = 9,
                SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 42)),
                LabelsPaint     = new SolidColorPaint(new SKColor(85, 85, 102)),
            }
        ];
        KillDailyYAxes =
        [
            new Axis
            {
                TextSize    = 9,
                MinLimit    = 0,
                LabelsPaint = new SolidColorPaint(new SKColor(85, 85, 102)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 42)),
            }
        ];
    }

    private async Task LoadMonthlyActivityAsync(long corpId, CancellationToken ct)
    {
        var rows = await _service.GetMonthlyActivityAsync(corpId, 12, ct);
        MonthlyActivityRows.Clear();
        foreach (var r in rows) MonthlyActivityRows.Add(new MonthlyActivityRowVm(r));
        HasMonthlyData = MonthlyActivityRows.Count > 0;
        BuildMonthlyCharts(rows);
    }

    private void BuildMonthlyCharts(List<MonthlyActivityRow> rows)
    {
        if (rows.Count == 0)
        {
            MonthlyIskSeries = []; MonthlyCountSeries = [];
            MonthlyXAxes = []; MonthlyIskYAxes = []; MonthlyCountAndMineYAxes = [];
            return;
        }

        // Oldest-first for charts
        var ordered = rows.OrderBy(r => r.Month).ToList();
        var labels  = ordered.Select(r => r.Month).ToArray();

        static LineSeries<double> Line(string name, IEnumerable<double> vals, SKColor color, int scaleY = 0) =>
            new LineSeries<double>
            {
                Name = name, Values = vals.ToArray(),
                Stroke = new SolidColorPaint(color, 2), Fill = null, GeometrySize = 0,
                EasingFunction = null, ScalesYAt = scaleY,
            };

        MonthlyIskSeries =
        [
            Line("Income",       ordered.Select(r => (double)(r.TotalIncome  / 1_000_000_000m)), new SKColor(106, 170, 136)),
            Line("Expenses",     ordered.Select(r => (double)(r.TotalExpense / 1_000_000_000m)), new SKColor(204, 100, 100)),
            Line("Ratting Tax",  ordered.Select(r => (double)(r.RattingTax   / 1_000_000_000m)), new SKColor(200, 168,  75)),
            Line("Industry Tax", ordered.Select(r => (double)(r.IndustryTax  / 1_000_000_000m)), new SKColor( 91, 155, 213)),
            Line("Proj Payouts", ordered.Select(r => (double)(r.ProjectPayouts / 1_000_000_000m)), new SKColor(155, 120, 200)),
        ];

        MonthlyCountSeries =
        [
            Line("Kills",        ordered.Select(r => (double)r.Kills),       new SKColor(106, 170, 136), 0),
            Line("Losses",       ordered.Select(r => (double)r.Losses),      new SKColor(204, 100, 100), 0),
            Line("Units Mined",  ordered.Select(r => (double)r.UnitsMined),  new SKColor(200, 168,  75), 1),
        ];

        static Axis XAx(string[] labs) => new Axis
        {
            Labels = labs, LabelsRotation = -45, TextSize = 9,
            SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 42)),
            LabelsPaint     = new SolidColorPaint(new SKColor(85, 85, 102)),
        };
        static Axis YAx(string unit) => new Axis
        {
            TextSize = 9, MinLimit = 0,
            LabelsPaint     = new SolidColorPaint(new SKColor(85, 85, 102)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 42)),
            Labeler = v => $"{v:F1}{unit}",
        };

        MonthlyXAxes = [XAx(labels)];
        MonthlyIskYAxes = [YAx("B")];
        MonthlyCountAndMineYAxes =
        [
            new Axis { TextSize = 9, MinLimit = 0, LabelsPaint = new SolidColorPaint(new SKColor(85, 85, 102)), SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 42)) },
            new Axis { TextSize = 9, MinLimit = 0, Position = LiveChartsCore.Measure.AxisPosition.End, LabelsPaint = new SolidColorPaint(new SKColor(85, 85, 102)), SeparatorsPaint = new SolidColorPaint(SKColors.Transparent), Labeler = v => v >= 1_000_000 ? $"{v/1_000_000:F0}M" : $"{v:N0}" },
        ];
    }

    private async Task LoadProjectsAsync(long corpId, CancellationToken ct)
    {
        var active  = await _service.GetProjectsActiveAsync(corpId, ct);
        var history = await _service.GetProjectsHistoryAsync(corpId, ct);

        ActiveProjects.Clear();
        foreach (var p in active)  ActiveProjects.Add(new CorpProjectRowVm(p));
        HistoryProjects.Clear();
        foreach (var p in history) HistoryProjects.Add(new CorpProjectRowVm(p));

        HasProjectData = ActiveProjects.Count > 0 || HistoryProjects.Count > 0;

        var sumTotal     = (decimal)active.Sum(p => p.RewardInitial);
        var sumRemaining = (decimal)active.Sum(p => p.RewardRemaining);
        ActiveTotalRewardText     = active.Count > 0 ? FormatIskStatic(sumTotal)     + " ISK" : "";
        ActiveRemainingRewardText = active.Count > 0 ? FormatIskStatic(sumRemaining) + " ISK" : "";

        _ = LoadStandingProjectsAsync(corpId, ct);
    }

    private async Task LoadProjectDetailAsync(long corpId, CorpProjectRowVm vm,
        CancellationToken ct = default)
    {
        var p = vm.Source;

        // Common info fields
        ProjectInfoFields.Clear();
        ProjectInfoFields.Add(new("Name",         p.Name));
        ProjectInfoFields.Add(new("State",         p.State));
        ProjectInfoFields.Add(new("Type",          FormatConfigType(p.ConfigType)));
        ProjectInfoFields.Add(new("Career",        string.IsNullOrEmpty(p.Career) ? "—" : p.Career));
        if (p.Created.HasValue)
            ProjectInfoFields.Add(new("Created",   p.Created.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm")));
        ProjectInfoFields.Add(new("Last Modified", p.LastModified.UtcDateTime.ToString("yyyy-MM-dd HH:mm")));
        if (!string.IsNullOrEmpty(p.CreatorName))
            ProjectInfoFields.Add(new("Creator",   p.CreatorName));
        ProjectInfoFields.Add(new("Progress",         $"{p.ProgressCurrent:N0} / {p.ProgressDesired:N0}"));
        if (p.RewardInitial > 0)
            ProjectInfoFields.Add(new("Total Reward",     FormatIskStatic((decimal)p.RewardInitial) + " ISK"));
        ProjectInfoFields.Add(new("Remaining Reward", p.RewardRemaining > 0 ? FormatIskStatic((decimal)p.RewardRemaining) + " ISK" : "—"));
        if (!string.IsNullOrEmpty(p.Description))
            ProjectInfoFields.Add(new("Description", p.Description));

        // Configuration fields — async, resolves IDs to names
        ProjectConfigFields.Clear();
        HasProjectConfig = false;
        if (!string.IsNullOrWhiteSpace(p.ConfigType) && !string.IsNullOrWhiteSpace(p.ConfigurationJson))
        {
            var authCharId   = SelectedCorp?.AuthCharacterId ?? 0;
            var configFields = await ParseConfigFieldsAsync(p.ConfigType, p.ConfigurationJson, ct, authCharId, corpId);
            foreach (var f in configFields) ProjectConfigFields.Add(f);
            HasProjectConfig = ProjectConfigFields.Count > 0;
        }

        // Contributors with % of target and payout
        var contributors = await _service.GetProjectContributorsAsync(corpId, p.ProjectId, ct);
        var charIds      = contributors.Select(c => c.CharacterId).ToList();
        var charNames    = await _service.ResolveNamesAsync(charIds, ct);

        // Sort by ISK payout desc; fall back to units when no per-contrib reward
        var sorted = p.RewardPerContrib > 0
            ? contributors.OrderByDescending(c => c.Contributed * p.RewardPerContrib).ToList()
            : contributors.OrderByDescending(c => c.Contributed).ToList();

        ProjectContributors.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            var c    = sorted[i];
            var name = charNames.TryGetValue(c.CharacterId, out var n) ? n
                     : !string.IsNullOrEmpty(c.Name)                  ? c.Name
                     : c.CharacterId.ToString();
            var pct  = p.ProgressDesired > 0
                     ? $"{(double)c.Contributed / p.ProgressDesired * 100:F1}%"
                     : "—";
            var payout = p.RewardPerContrib > 0
                       ? FormatIskStatic((decimal)(c.Contributed * p.RewardPerContrib)) + " ISK"
                       : "—";
            ProjectContributors.Add(new(i + 1, name, c.Contributed.ToString("N0"), pct, payout));
        }
    }

    private static string FormatConfigType(string? type) => type switch
    {
        "capture_fw_complex"  => "Capture FW Complex",
        "damage_ship"         => "Damage Ship",
        "defend_fw_complex"   => "Defend FW Complex",
        "deliver_item"        => "Deliver Item",
        "destroy_npc"         => "Destroy Non-Capsuleers",
        "destroy_ship"        => "Destroy Ship",
        "earn_loyalty_points" => "Earn Loyalty Points",
        "lost_ship"           => "Lose Ship",
        "manual"              => "Manual",
        "manufacture_item"    => "Manufacture Item",
        "mine_material"       => "Mine Material",
        "remote_boost_shield" => "Remote Boost Shield",
        "remote_repair_armor" => "Remote Repair Armor",
        "salvage_wreck"       => "Salvage Wreck",
        "scan_signature"      => "Scan Signature",
        "ship_insurance"      => "Ship Insurance",
        "unknown"             => "Unknown",
        null or ""            => "—",
        var s                 => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.Replace('_', ' ')),
    };

    private async Task<IReadOnlyList<ProjectFieldVm>> ParseConfigFieldsAsync(
        string configType, string json, CancellationToken ct, long authCharId = 0, long corpId = 0)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // JSON wraps the inner config under the type key: { "deliver_item": { ... } }
            if (!root.TryGetProperty(configType, out var inner)) return [];

            // ── Collect all IDs for batch name resolution ──────────────────
            var idsToResolve = new HashSet<long>();

            void CollectIds(JsonElement arr, params string[] keys)
            {
                foreach (var el in arr.EnumerateArray())
                    foreach (var key in keys)
                        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                            idsToResolve.Add(v.GetInt64());
            }

            // items / materials: type_id OR group_id
            if (inner.TryGetProperty("items",           out var items))    CollectIds(items,    "type_id", "group_id");
            if (inner.TryGetProperty("materials",       out var mats))     CollectIds(mats,     "type_id", "group_id");
            if (inner.TryGetProperty("ships",           out var ships))    CollectIds(ships,    "type_id");

            // locations: solar_system_id | constellation_id | region_id
            if (inner.TryGetProperty("locations",       out var locs))     CollectIds(locs,     "solar_system_id", "constellation_id", "region_id");
            if (inner.TryGetProperty("docking_locations", out var dlocs))  CollectIds(dlocs,    "station_id", "structure_id");

            // identities: character_id | corporation_id | alliance_id | faction_id
            if (inner.TryGetProperty("identities",      out var idents))   CollectIds(idents,   "character_id", "corporation_id", "alliance_id", "faction_id");
            if (inner.TryGetProperty("factions",        out var facs))     CollectIds(facs,     "faction_id");
            if (inner.TryGetProperty("corporations",    out var corps))    CollectIds(corps,    "corporation_id");

            // office_id → resolve to location_id via corp office map so we can name it
            var officeToLocation = new Dictionary<long, long>();
            if (inner.TryGetProperty("office_id", out var oid) && oid.ValueKind == JsonValueKind.Number)
            {
                var officeId = oid.GetInt64();
                if (corpId > 0)
                {
                    var officeMap = await _service.GetCorpOfficeMapAsync(corpId, ct);
                    if (officeMap.TryGetValue(officeId, out var locId))
                    {
                        officeToLocation[officeId] = locId;
                        idsToResolve.Add(locId);
                    }
                    else
                    {
                        idsToResolve.Add(officeId);
                    }
                }
                else
                {
                    idsToResolve.Add(officeId);
                }
            }

            // ── Resolve all IDs in one batch ───────────────────────────────
            var names = idsToResolve.Count > 0
                ? await _service.ResolveNamesAsync(idsToResolve, ct, authCharId)
                : new Dictionary<long, string>();

            string Resolve(long id) => names.TryGetValue(id, out var n) ? n
                : id > 1_000_000_000_000L ? $"Structure {id}"
                : id.ToString();

            string ResolveOfficeId(long officeId)
            {
                var targetId = officeToLocation.TryGetValue(officeId, out var locId) ? locId : officeId;
                return Resolve(targetId);
            }

            // Resolve an array where each element has one of several possible ID keys
            string ResolveList(JsonElement arr, params string[] keys)
            {
                var parts = arr.EnumerateArray().Select(el =>
                {
                    foreach (var key in keys)
                        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                            return Resolve(v.GetInt64());
                    return null;
                }).Where(s => s is not null);
                return string.Join(", ", parts);
            }

            // Docking locations: try station_id then structure_id; both now in the names dict
            string ResolveDockingList(JsonElement arr)
            {
                var parts = arr.EnumerateArray().Select(el =>
                {
                    if (el.TryGetProperty("station_id",   out var s)) return Resolve(s.GetInt64());
                    if (el.TryGetProperty("structure_id",  out var r)) return Resolve(r.GetInt64());
                    return null;
                }).Where(s => s is not null);
                return string.Join(", ", parts);
            }

            // ── Build fields per type ──────────────────────────────────────
            var fields = new List<ProjectFieldVm>();

            void AddLocations(JsonElement? el = null)
            {
                var arr = el ?? (inner.TryGetProperty("locations", out var l) ? l : (JsonElement?)null);
                if (arr is null) return;
                var val = ResolveList(arr.Value, "solar_system_id", "constellation_id", "region_id");
                if (!string.IsNullOrEmpty(val)) fields.Add(new("Location(s)", val));
            }

            void AddIdentities(string label = "Target(s)")
            {
                if (!inner.TryGetProperty("identities", out var arr) || arr.GetArrayLength() == 0) return;
                var val = ResolveList(arr, "character_id", "corporation_id", "alliance_id", "faction_id");
                if (!string.IsNullOrEmpty(val)) fields.Add(new(label, val));
            }

            switch (configType)
            {
                case "deliver_item":
                    if (inner.TryGetProperty("items", out var dItems) && dItems.GetArrayLength() > 0)
                        fields.Add(new("Item(s)", ResolveList(dItems, "type_id", "group_id")));
                    // Modern API: docking_locations (structure/station); legacy: office_id
                    if (inner.TryGetProperty("docking_locations", out var dDock) && dDock.GetArrayLength() > 0)
                        fields.Add(new("Destination(s)", ResolveDockingList(dDock)));
                    else if (inner.TryGetProperty("office_id", out var oId) && oId.ValueKind == JsonValueKind.Number)
                        fields.Add(new("Destination", ResolveOfficeId(oId.GetInt64())));
                    break;

                case "manufacture_item":
                    if (inner.TryGetProperty("items", out var mfItems) && mfItems.GetArrayLength() > 0)
                        fields.Add(new("Item(s)", ResolveList(mfItems, "type_id", "group_id")));
                    if (inner.TryGetProperty("docking_locations", out var mfDock) && mfDock.GetArrayLength() > 0)
                        fields.Add(new("Location(s)", ResolveDockingList(mfDock)));
                    if (inner.TryGetProperty("owner", out var owner))
                        fields.Add(new("Owner", owner.GetString() ?? "—"));
                    break;

                case "destroy_npc":
                    AddLocations();
                    break;

                case "destroy_ship":
                    AddLocations();
                    if (inner.TryGetProperty("ships", out var dsShips) && dsShips.GetArrayLength() > 0)
                        fields.Add(new("Ship Type(s)", ResolveList(dsShips, "type_id")));
                    AddIdentities("Target(s)");
                    break;

                case "damage_ship":
                    AddLocations();
                    AddIdentities("Target(s)");
                    break;

                case "lost_ship":
                    if (inner.TryGetProperty("ships", out var lsShips) && lsShips.GetArrayLength() > 0)
                        fields.Add(new("Ship Type(s)", ResolveList(lsShips, "type_id")));
                    AddLocations();
                    AddIdentities("Killed By");
                    break;

                case "ship_insurance":
                    if (inner.TryGetProperty("conflict_type", out var ct2))
                        fields.Add(new("Conflict Type", ct2.GetString() ?? "—"));
                    AddLocations();
                    AddIdentities("Killed By");
                    break;

                case "mine_material":
                    AddLocations();
                    if (inner.TryGetProperty("materials", out var mineMats) && mineMats.GetArrayLength() > 0)
                        fields.Add(new("Material(s)", ResolveList(mineMats, "type_id", "group_id")));
                    break;

                case "salvage_wreck":
                    AddLocations();
                    break;

                case "scan_signature":
                    AddLocations();
                    // signature_type_id is an attribute ID — not resolvable via /universe/names/
                    if (inner.TryGetProperty("signatures", out var sigs) && sigs.GetArrayLength() > 0)
                    {
                        var sigIds = string.Join(", ", sigs.EnumerateArray()
                            .Where(el => el.TryGetProperty("signature_type_id", out _))
                            .Select(el => el.GetProperty("signature_type_id").GetInt64().ToString()));
                        if (!string.IsNullOrEmpty(sigIds)) fields.Add(new("Signature Type(s)", sigIds));
                    }
                    break;

                case "capture_fw_complex":
                case "defend_fw_complex":
                    AddLocations();
                    if (inner.TryGetProperty("factions", out var fwFacs) && fwFacs.GetArrayLength() > 0)
                        fields.Add(new("Faction(s)", ResolveList(fwFacs, "faction_id")));
                    // archetype_id is a game-internal ID not resolvable via /universe/names/
                    if (inner.TryGetProperty("archetypes", out var arcs) && arcs.GetArrayLength() > 0)
                    {
                        var arcIds = string.Join(", ", arcs.EnumerateArray()
                            .Where(el => el.TryGetProperty("archetype_id", out _))
                            .Select(el => el.GetProperty("archetype_id").GetInt64().ToString()));
                        if (!string.IsNullOrEmpty(arcIds)) fields.Add(new("Archetype(s)", arcIds));
                    }
                    break;

                case "remote_boost_shield":
                case "remote_repair_armor":
                    AddLocations();
                    AddIdentities("Target(s)");
                    break;

                case "earn_loyalty_points":
                    if (inner.TryGetProperty("corporations", out var lpCorps) && lpCorps.GetArrayLength() > 0)
                        fields.Add(new("Corporation(s)", ResolveList(lpCorps, "corporation_id")));
                    break;

                case "manual":
                    // No configuration fields
                    break;

                case "unknown":
                    if (inner.TryGetProperty("type", out var uType))
                        fields.Add(new("Sub-type", uType.GetString() ?? "—"));
                    break;

                default:
                    // Future unknown type — render all non-empty fields generically
                    foreach (var prop in inner.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            fields.Add(new(prop.Name, prop.Value.GetString() ?? ""));
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                        {
                            var vals = prop.Value.EnumerateArray().Select(el =>
                                el.ValueKind == JsonValueKind.Object
                                    ? string.Join("/", el.EnumerateObject()
                                        .Where(p => p.Value.ValueKind == JsonValueKind.Number)
                                        .Select(p => Resolve(p.Value.GetInt64())))
                                    : el.ToString());
                            var joined = string.Join(", ", vals.Where(v => !string.IsNullOrEmpty(v)));
                            if (!string.IsNullOrEmpty(joined)) fields.Add(new(prop.Name, joined));
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            fields.Add(new(prop.Name, Resolve(prop.Value.GetInt64())));
                        }
                    }
                    break;
            }

            return fields;
        }
        catch { return []; }
    }

    private static void PopulateTopList(
        ObservableCollection<CorpTopPlayerRowVm> list,
        List<RankedPlayerRow> rows,
        Func<long, string> resolveName,
        bool isCount)
    {
        list.Clear();
        foreach (var r in rows)
            list.Add(new CorpTopPlayerRowVm(r.Rank, resolveName(r.CharacterId), r.Amount, isCount, r.Percent));
    }

    internal static string FormatIskStatic(decimal v) => FormatIsk((double)v);

    private static string FormatIsk(double v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000) return $"{v / 1_000_000_000:F2}B";
        if (abs >= 1_000_000)     return $"{v / 1_000_000:F2}M";
        if (abs >= 1_000)         return $"{v / 1_000:F1}K";
        return $"{v:N0}";
    }

    // ── Standing projects load ─────────────────────────────────────────────────

    private async Task LoadStandingProjectsAsync(long corpId, CancellationToken ct = default)
    {
        IsLoadingMaintain = true;
        try
        {
            _standingProjectCache = await _service.GetStandingProjectsAsync(corpId, ct);
            var rows = await _service.BuildMaintainGridRowsAsync(corpId, ct);
            StandingProjectRows.Clear();
            foreach (var r in rows)
                StandingProjectRows.Add(new StandingProjectRowVm(r,
                    dbId => { _ = EditStandingProjectAsync(dbId); },
                    dbId => { _ = DeleteStandingProjectAsync(dbId); }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Maintain] load failed: {ex}");
            Status = $"MAINTAIN load failed: {ex.Message}";
        }
        finally { IsLoadingMaintain = false; }
    }

    private async Task AddStandingProjectAsync()
    {
        var corpId = SelectedCorp?.Id;
        if (corpId is null || ShowStandingProjectDialog is null) return;
        var result = await ShowStandingProjectDialog(null);
        if (result is null) return;
        result.CorporationId = (long)corpId;
        await _service.AddStandingProjectAsync(result);
        _ = LoadStandingProjectsAsync((long)corpId);
    }

    private List<CorpStandingProject> _standingProjectCache = [];

    private async Task EditStandingProjectAsync(long dbId)
    {
        var corpId = SelectedCorp?.Id;
        if (corpId is null || ShowStandingProjectDialog is null) return;
        var existing = _standingProjectCache.FirstOrDefault(p => p.Id == dbId)
                       ?? (await _service.GetStandingProjectsAsync((long)corpId))
                              .FirstOrDefault(p => p.Id == dbId);
        if (existing is null) return;
        var result = await ShowStandingProjectDialog(existing);
        if (result is null) return;
        result.Id            = dbId;
        result.CorporationId = (long)corpId;
        result.CreatedAt     = existing.CreatedAt;
        await _service.UpdateStandingProjectAsync(result);
        _ = LoadStandingProjectsAsync((long)corpId);
    }

    private async Task CloneStandingProjectAsync()
    {
        var corpId = SelectedCorp?.Id;
        if (corpId is null || ShowStandingProjectDialog is null || SelectedMaintainRow is null) return;
        var source = _standingProjectCache.FirstOrDefault(p => p.Id == SelectedMaintainRow.DbId)
                     ?? (await _service.GetStandingProjectsAsync((long)corpId))
                            .FirstOrDefault(p => p.Id == SelectedMaintainRow.DbId);
        if (source is null) return;
        var result = await ShowStandingProjectDialog(source);
        if (result is null) return;
        result.CorporationId = (long)corpId;
        await _service.AddStandingProjectAsync(result);
        _ = LoadStandingProjectsAsync((long)corpId);
    }

    private async Task DeleteStandingProjectAsync(long dbId)
    {
        var corpId = SelectedCorp?.Id;
        if (corpId is null || ConfirmDelete is null) return;
        if (!await ConfirmDelete()) return;
        await _service.DeleteStandingProjectAsync(dbId);
        _ = LoadStandingProjectsAsync((long)corpId);
    }
}
