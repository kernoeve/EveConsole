using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One appraised line as the grid shows it, at the price percentage in force.</summary>
public sealed class AppraisalRowVm(AppraisalRow row, double factor)
{
    public string Name       => row.Name;
    public int    TypeId     => row.TypeId;
    public long   Quantity   => row.Quantity;
    public string QuantityText => row.Quantity.ToString("N0");
    public double UnitVolume  => row.UnitVolume;
    public double TotalVolume => row.TotalVolume;
    public string VolumeText  => row.TypeId > 0 ? $"{row.TotalVolume:N2} m³" : "";
    public double UnitBuy     => row.UnitBuy * factor;
    public double UnitSell    => row.UnitSell * factor;
    public double TotalBuy    => row.TotalBuy * factor;
    public double TotalSell   => row.TotalSell * factor;
    public string UnitBuyText   => Isk(UnitBuy);
    public string UnitSellText  => Isk(UnitSell);
    public string TotalBuyText  => Isk(TotalBuy);
    public string TotalSellText => Isk(TotalSell);
    public string Problem     => row.Problem;
    public bool   IsProblem   => row.Problem.Length > 0;
    public IBrush NameColor   => IsProblem ? Palette.Warn : Palette.TextPrimary;
    public bool   HasType     => row.TypeId > 0;

    private static string Isk(double v) => v > 0 ? v.ToString("N2") : "—";
}

/// <summary>A price mode as the picker names it.</summary>
public sealed record PriceModeChoice(AppraisalPriceMode Mode, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Item Valuation: paste any list the client copies — a hangar, a cargo hold, a contract, a
/// fit, a multibuy — and see what it is worth at one of the app's market sources, buy, sell and
/// split, with the volume, the way an appraisal site shows it.
/// </summary>
public sealed class ItemValuationViewModel : ReactiveObject
{
    private const string SourceKey = "valuation.source";
    private const string ModeKey   = "valuation.mode";

    private readonly AppraisalService _service;
    private bool _loaded;

    public ItemValuationViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _service = new AppraisalService(dbFactory);
    }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Set by the main window: opens a type in the Item Browser.</summary>
    public Action<int>? NavigateToItemAction { get; set; }

    // ── Input and options ──────────────────────────────────────────────────

    private string _inputText = "";
    public string InputText
    {
        get => _inputText;
        set => this.RaiseAndSetIfChanged(ref _inputText, value);
    }

    public List<MarketPricingConfig> Sources { get; private set; } = [];

    private MarketPricingConfig? _selectedSource;
    public MarketPricingConfig? SelectedSource
    {
        get => _selectedSource;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSource, value);
            if (value is not null && _loaded) UiState.Set(SourceKey, value.Id.ToString(CultureInfo.InvariantCulture));
        }
    }

    public IReadOnlyList<PriceModeChoice> Modes { get; } =
    [
        new(AppraisalPriceMode.Immediate,  "Immediate"),
        new(AppraisalPriceMode.Percentile, "Top of book"),
    ];

    private PriceModeChoice? _selectedMode;
    public PriceModeChoice? SelectedMode
    {
        get => _selectedMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMode, value);
            if (value is not null && _loaded) UiState.Set(ModeKey, value.Mode.ToString());
        }
    }

    /// <summary>The percentage of the market price to value at: 100 is the price itself, 90 a
    /// buyback that pays nine tenths.</summary>
    private decimal _pricePercent = 100;
    public decimal PricePercent
    {
        get => _pricePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _pricePercent, Math.Clamp(value, 1, 500));
            if (_appraisal is not null) Present(_appraisal);
        }
    }

    // ── Result ─────────────────────────────────────────────────────────────

    private Appraisal? _appraisal;

    private IReadOnlyList<AppraisalRowVm> _rows = [];
    public IReadOnlyList<AppraisalRowVm> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    private bool _hasResult;
    public bool HasResult { get => _hasResult; private set => this.RaiseAndSetIfChanged(ref _hasResult, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private string _status = "Paste a list of items and press Appraise.";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _totalBuyText = "—", _totalSellText = "—", _totalSplitText = "—", _totalVolumeText = "—", _totalItemsText = "—";
    public string TotalBuyText    { get => _totalBuyText;    private set => this.RaiseAndSetIfChanged(ref _totalBuyText, value); }
    public string TotalSellText   { get => _totalSellText;   private set => this.RaiseAndSetIfChanged(ref _totalSellText, value); }
    public string TotalSplitText  { get => _totalSplitText;  private set => this.RaiseAndSetIfChanged(ref _totalSplitText, value); }
    public string TotalVolumeText { get => _totalVolumeText; private set => this.RaiseAndSetIfChanged(ref _totalVolumeText, value); }
    public string TotalItemsText  { get => _totalItemsText;  private set => this.RaiseAndSetIfChanged(ref _totalItemsText, value); }

    private string _totalBuyTip = "", _totalSellTip = "", _totalSplitTip = "";
    public string TotalBuyTip   { get => _totalBuyTip;   private set => this.RaiseAndSetIfChanged(ref _totalBuyTip, value); }
    public string TotalSellTip  { get => _totalSellTip;  private set => this.RaiseAndSetIfChanged(ref _totalSellTip, value); }
    public string TotalSplitTip { get => _totalSplitTip; private set => this.RaiseAndSetIfChanged(ref _totalSplitTip, value); }

    private string _unparsedText = "";
    public string UnparsedText { get => _unparsedText; private set => this.RaiseAndSetIfChanged(ref _unparsedText, value); }
    public bool HasUnparsed => UnparsedText.Length > 0;

    // ── Commands ───────────────────────────────────────────────────────────

    /// <summary>The market sources and the remembered choices. Once, when the tab first opens.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        try
        {
            var sources   = await _service.SourcesAsync();
            var defaultId = await _service.DefaultSourceIdAsync();
            var savedId   = UiState.GetLong(SourceKey, defaultId ?? 0);
            var savedMode = Enum.TryParse<AppraisalPriceMode>(UiState.Get(ModeKey), out var m) ? m : AppraisalPriceMode.Immediate;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Sources = sources;
                this.RaisePropertyChanged(nameof(Sources));
                SelectedSource = sources.FirstOrDefault(s => s.Id == savedId) ?? sources.FirstOrDefault(s => s.Id == defaultId) ?? sources.FirstOrDefault();
                SelectedMode   = Modes.First(x => x.Mode == savedMode);
                if (sources.Count == 0) Status = "No market source is set up. Add one under Settings > Market first.";
                _loaded = true;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Could not load the market sources: {ex.Message}");
        }
    }

    public async Task AppraiseAsync()
    {
        if (IsBusy) return;
        if (SelectedSource is null) { Status = "Pick a market source first."; return; }
        if (string.IsNullOrWhiteSpace(InputText)) { Status = "Nothing to appraise: paste a list of items first."; return; }

        IsBusy = true;
        Status = "Appraising…";
        try
        {
            var text = InputText;
            var mode = SelectedMode?.Mode ?? AppraisalPriceMode.Immediate;
            var appraisal = await Task.Run(() => _service.AppraiseAsync(text, SelectedSource.Id, mode));
            await Dispatcher.UIThread.InvokeAsync(() => Present(appraisal));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Appraisal failed: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    public void Clear()
    {
        InputText   = "";
        _appraisal  = null;
        Rows        = [];
        HasResult   = false;
        TotalBuyText = TotalSellText = TotalSplitText = TotalVolumeText = TotalItemsText = "—";
        TotalBuyTip = TotalSellTip = TotalSplitTip = "";
        UnparsedText = "";
        this.RaisePropertyChanged(nameof(HasUnparsed));
        Status = "Paste a list of items and press Appraise.";
    }

    /// <summary>The table as text, tab-separated, for a spreadsheet or a chat: every row, then
    /// the totals, priced as shown.</summary>
    public string ResultAsText()
    {
        if (_appraisal is null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("Item\tQuantity\tVolume m3\tUnit buy\tUnit sell\tTotal buy\tTotal sell");
        foreach (var r in Rows)
            sb.AppendLine($"{r.Name}\t{r.Quantity}\t{r.TotalVolume:0.##}\t{r.UnitBuy:0.##}\t{r.UnitSell:0.##}\t{r.TotalBuy:0.##}\t{r.TotalSell:0.##}");
        sb.AppendLine();
        sb.AppendLine($"Total buy\t{Rows.Sum(r => r.TotalBuy):0.##}");
        sb.AppendLine($"Total sell\t{Rows.Sum(r => r.TotalSell):0.##}");
        sb.AppendLine($"Total split\t{Rows.Sum(r => (r.TotalBuy + r.TotalSell) / 2):0.##}");
        sb.AppendLine($"Total volume m3\t{Rows.Sum(r => r.TotalVolume):0.##}");
        sb.AppendLine($"Priced at {_appraisal.MarketName}, {(_appraisal.Mode == AppraisalPriceMode.Immediate ? "immediate" : $"top {_appraisal.PercentilePercent:0.#}% of the book")}, {PricePercent:0.#}% of market, {(_appraisal.PricesAsOf is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "age unknown")}");
        return sb.ToString();
    }

    public async Task CopyAsync()
    {
        var text = ResultAsText();
        if (text.Length == 0 || CopyToClipboard is null) return;
        await CopyToClipboard(text);
        Status = "Copied to the clipboard.";
    }

    public void OpenItem(AppraisalRowVm row)
    {
        if (row.HasType) NavigateToItemAction?.Invoke(row.TypeId);
    }

    // ── Presentation ───────────────────────────────────────────────────────

    private void Present(Appraisal appraisal)
    {
        _appraisal = appraisal;
        var factor = (double)PricePercent / 100;
        Rows = appraisal.Rows.Select(r => new AppraisalRowVm(r, factor)).ToList();

        double buy = appraisal.TotalBuy * factor, sell = appraisal.TotalSell * factor, split = appraisal.TotalSplit * factor;
        TotalBuyText    = Compact(buy);
        TotalSellText   = Compact(sell);
        TotalSplitText  = Compact(split);
        TotalBuyTip     = $"{buy:N2} ISK";
        TotalSellTip    = $"{sell:N2} ISK";
        TotalSplitTip   = $"{split:N2} ISK";
        TotalVolumeText = $"{appraisal.TotalVolume:N0} m³";
        TotalItemsText  = $"{appraisal.Rows.Count:N0} / {appraisal.TotalUnits:N0}";
        UnparsedText    = appraisal.Unparsed.Count == 0 ? "" : "Not read: " + string.Join("  |  ", appraisal.Unparsed);
        this.RaisePropertyChanged(nameof(HasUnparsed));
        HasResult = appraisal.Rows.Count > 0;

        var age = appraisal.PricesAsOf is { } t ? Age(DateTimeOffset.UtcNow - t) : "never refreshed";
        var mode = appraisal.Mode == AppraisalPriceMode.Immediate ? "best orders" : $"top {appraisal.PercentilePercent:0.#}% of the book";
        var problems = appraisal.Unpriced > 0 ? $"; {appraisal.Unpriced} could not be priced" : "";
        Status = $"{appraisal.Rows.Count:N0} items at {appraisal.MarketName}, {mode}, prices {age}{problems}.";
    }

    /// <summary>ISK the way the summary boxes read: 1.23B, 45.6M, 987K.</summary>
    private static string Compact(double v) =>
        v >= 1e12 ? $"{v / 1e12:N2}T" :
        v >= 1e9  ? $"{v / 1e9:N2}B" :
        v >= 1e6  ? $"{v / 1e6:N2}M" :
        v >= 1e3  ? $"{v / 1e3:N1}K" :
        v > 0     ? v.ToString("N0") : "—";

    private static string Age(TimeSpan span) =>
        span.TotalMinutes < 1  ? "fetched just now" :
        span.TotalMinutes < 90 ? $"fetched {span.TotalMinutes:0} min ago" :
        span.TotalHours   < 36 ? $"fetched {span.TotalHours:0} h ago" :
                                 $"fetched {span.TotalDays:0} days ago";
}
