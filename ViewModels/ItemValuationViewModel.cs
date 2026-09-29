using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Avalonia.Media.Imaging;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>One of the three ways an item is valued, as a cell: unit, total, and how far below
/// the best of the three it sits.</summary>
public sealed class ValueCellVm(double? unit, long quantity, double factor)
{
    public double? Unit  => unit is { } u ? u * factor : null;
    public double? Total => unit is { } u ? u * factor * quantity : null;
    public bool    Has   => unit is { } u && u > 0;

    /// <summary>Set once the row knows its best value.</summary>
    public bool   IsBest  { get; set; }
    public double Pct     { get; set; }   // 0 for the best, negative below it

    public string UnitText   => Has ? IskText.Compact(Unit!.Value)  : "—";
    public string TotalText  => Has ? IskText.Compact(Total!.Value) : "—";
    public string UnitExact  => Has ? IskText.Exact(Unit!.Value)    : AssetsText.NoPrice;
    public string TotalExact => Has ? IskText.Exact(Total!.Value)   : AssetsText.NoPrice;
    public string PctText    => !Has ? "—" : IsBest ? AssetsText.Best : IskText.Pct(Pct);
    /// <summary>The standing's colour: green for the best, red a long way behind.</summary>
    public IBrush Color      => IskText.Colour(Has, IsBest, Pct);
    /// <summary>The figures' colour: plain, faint with no price. The standing beside them wears
    /// the colour, so a table is not columns of red.</summary>
    public IBrush FigureColor => IskText.FigureColour(Has);
}

/// <summary>One item on the Values tab: what it is worth three ways at the primary station.</summary>
public sealed class ValueRowVm : ReactiveObject
{
    public ValueRowVm(ItemValues v, double factor)
    {
        Name     = v.Item.Name;
        TypeId   = v.Item.TypeId;
        Quantity = v.Item.Quantity;
        Section  = v.Item.Section;
        Problem  = v.Item.Problem;
        TotalVolume = v.Item.TotalVolume;
        Market    = new ValueCellVm(v.MarketUnit, Quantity, factor);
        Build     = new ValueCellVm(v.BuildUnit, Quantity, factor);
        Reprocess = new ValueCellVm(v.ReprocessUnit, Quantity, factor);
        MarketFromContract = v.MarketFromContract;
        Blueprint = v.Item.Blueprint;

        var cells = new[] { Market, Build, Reprocess }.Where(c => c.Has).ToList();
        if (cells.Count > 0)
        {
            var best = cells.Max(c => c.Total!.Value);
            foreach (var c in cells)
            {
                c.IsBest = Math.Abs(c.Total!.Value - best) < 0.005;
                c.Pct    = best > 0 ? (c.Total.Value - best) / best * 100 : 0;
            }
        }
    }

    public string Name     { get; }
    public int    TypeId   { get; }
    public long   Quantity { get; }
    public string Section  { get; }
    public string Problem  { get; }
    public double TotalVolume { get; }
    public bool   MarketFromContract { get; }
    public bool   Blueprint { get; }

    private Bitmap? _icon;
    /// <summary>The item's picture, once the batch that fetches them has it.</summary>
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }

    public ValueCellVm Market    { get; }
    public ValueCellVm Build     { get; }
    public ValueCellVm Reprocess { get; }

    public string QuantityText => Quantity.ToString("N0");
    public string VolumeText   => TypeId > 0 ? $"{TotalVolume:N2} m³" : "";
    public bool   HasType      => TypeId > 0;
    public bool   IsProblem    => Problem.Length > 0;
    public bool   HasSection   => Section.Length > 0;
    public string Note         => MarketFromContract ? AssetsText.NoteContract : "";
    public bool   HasNote      => MarketFromContract;
    public IBrush NameColor    => IsProblem ? Palette.Warn : Palette.TextPrimary;

    // For the columns to sort on.
    public double MarketTotal    => Market.Total ?? -1;
    public double BuildTotal     => Build.Total ?? -1;
    public double ReprocessTotal => Reprocess.Total ?? -1;
    public double MarketUnit     => Market.Unit ?? -1;
    public double BuildUnit      => Build.Unit ?? -1;
    public double ReprocessUnit  => Reprocess.Unit ?? -1;
    // A per cent is nought for the best and negative below it, so a cell with no value sorts
    // under every real one.
    public double MarketPct      => Market.Has    ? Market.Pct    : -1000;
    public double BuildPct       => Build.Has     ? Build.Pct     : -1000;
    public double ReprocessPct   => Reprocess.Has ? Reprocess.Pct : -1000;
}

/// <summary>One station's cell on the compare tab.</summary>
public sealed class CompareCellVm(double? unit, long quantity, double factor)
{
    public double? Unit  => unit is { } u ? u * factor : null;
    public double? Total => unit is { } u ? u * factor * quantity : null;
    public bool    Has   => unit is { } u && u > 0;
    public bool    IsBest { get; set; }
    public double  Pct    { get; set; }

    public string UnitText   => Has ? IskText.Compact(Unit!.Value)  : "—";
    public string TotalText  => Has ? IskText.Compact(Total!.Value) : "—";
    public string UnitExact  => Has ? IskText.Exact(Unit!.Value)    : AssetsText.NoPrice;
    public string TotalExact => Has ? IskText.Exact(Total!.Value)   : AssetsText.NoPrice;
    public string PctText    => !Has ? "—" : IsBest ? AssetsText.Best : IskText.Pct(Pct);
    public IBrush Color      => IskText.Colour(Has, IsBest, Pct);
    public IBrush FigureColor => IskText.FigureColour(Has);
}

/// <summary>One item on the compare tab: its price at every station, best marked.</summary>
public sealed class CompareRowVm : ReactiveObject
{
    public CompareRowVm(ValuedItem item, IReadOnlyList<StationPrices> stations, double factor)
    {
        Name     = item.Name;
        TypeId   = item.TypeId;
        Quantity = item.Quantity;
        Blueprint = item.Blueprint;
        TotalVolume = item.TotalVolume;
        Cells = stations.Select(s => new CompareCellVm(item.TypeId > 0 && s.UnitByType.TryGetValue(item.TypeId, out var u) ? u : null, Quantity, factor)).ToList();
        var priced = Cells.Where(c => c.Has).ToList();
        if (priced.Count > 0)
        {
            var best = priced.Max(c => c.Total!.Value);
            foreach (var c in priced)
            {
                c.IsBest = Math.Abs(c.Total!.Value - best) < 0.005;
                c.Pct    = best > 0 ? (c.Total.Value - best) / best * 100 : 0;
            }
        }
    }

    public string Name     { get; }
    public int    TypeId   { get; }
    public long   Quantity { get; }
    public double TotalVolume { get; }
    public string QuantityText => Quantity.ToString("N0");
    public string VolumeText   => TypeId > 0 ? $"{TotalVolume:N2} m³" : "";
    public bool   HasType      => TypeId > 0;
    public List<CompareCellVm> Cells { get; }
    public bool Blueprint { get; }

    private Bitmap? _icon;
    /// <summary>The item's picture, once the batch that fetches them has it.</summary>
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

/// <summary>A station's line in the compare band: its total, how it stands against the best,
/// how much of the list it could price, and how old its prices are.</summary>
public sealed record CompareTotalVm(MarketStation Station, string TotalText, string PctText, IBrush Color,
                                    string CoverageText, string AgeText, bool Stale, bool IsPrimary);

/// <summary>One value's line in the Values band — market, build or reprocessed: its total, how
/// it stands against the best of the three, how much of the list it could price, and on the
/// market line how old the station's prices are.</summary>
public sealed record ValueTotalVm(string Name, string TotalText, string Exact, string PctText, IBrush Color,
                                  string CoverageText, string AgeText, bool Stale);

/// <summary>ISK as the tool prints it: compact in a cell, exact in a tip; and the colour of a
/// standing against the best of its row — the best in green, red only a long way behind, the
/// rest plain. The standing alone wears it and the figures beside it stay plain, so a row of four
/// stations is not three red cells and one green.</summary>
public static class IskText
{
    /// <summary>1.23B, 45.6M, 18.3K; below ten thousand the figure itself, pennies only under a thousand.</summary>
    public static string Compact(double v) =>
        v >= 1e12 ? $"{v / 1e12:N2}T" :
        v >= 1e9  ? $"{v / 1e9:N2}B" :
        v >= 1e6  ? $"{v / 1e6:N2}M" :
        v >= 1e4  ? $"{v / 1e3:N1}K" :
        v >= 1e3  ? v.ToString("N0") :
        v > 0     ? v.ToString("N2") : "—";

    public static string Exact(double v) => $"{v:N2} ISK";

    /// <summary>"-18.2%"; a hair under nought is "0.0%", not "-0.0%".</summary>
    public static string Pct(double pct)
    {
        var s = $"{pct:0.0}%";
        return s == "-0.0%" ? "0.0%" : s;
    }

    /// <summary>How far behind the best a value may fall before it shows in red.</summary>
    public const double FarBehindPct = -25;

    public static IBrush Colour(bool has, bool isBest, double pct) =>
        !has ? Palette.TextFaint : isBest ? Palette.Good : pct <= FarBehindPct ? Palette.Bad : Palette.TextPrimary;

    /// <summary>A figure's colour: plain, or faint where there is no price.</summary>
    public static IBrush FigureColour(bool has) => has ? Palette.TextPrimary : Palette.TextFaint;
}

public sealed record PriceBasisChoice(PriceBasis Basis, string Name)
{
    public override string ToString() => Name;
}

public sealed record ValueTargetChoice(bool Reprocess, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Item Valuation: paste any list the client copies — a hangar, a cargo hold, a contract, a fit,
/// a multibuy — and see what it is worth at a station of your choosing: at market, built, or
/// reprocessed, side by side; then the same list priced at other stations to compare.
/// </summary>
public sealed class ItemValuationViewModel : ReactiveObject
{
    private const string StationKey = "valuation.station";
    private const string BasisKey   = "valuation.basis";
    private const string CompareKey = "valuation.compare";
    private const string TargetKey  = "valuation.reprocess";

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

    /// <summary>Raised when the stations compared change, so the view rebuilds the grid's columns.</summary>
    public event Action? CompareColumnsChanged;

    // ── Options ────────────────────────────────────────────────────────────

    private string _inputText = "";
    public string InputText { get => _inputText; set => this.RaiseAndSetIfChanged(ref _inputText, value); }

    private MarketStation? _selectedStation;
    /// <summary>Where the list is valued. A station, not a market source: any station with
    /// orders in the app's books, whichever source fetched them.</summary>
    public MarketStation? SelectedStation
    {
        get => _selectedStation;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStation, value);
            if (value is not null && _loaded) UiState.Set(StationKey, value.LocationId.ToString(CultureInfo.InvariantCulture));
        }
    }

    private string _stationText = "";
    public string StationText { get => _stationText; set => this.RaiseAndSetIfChanged(ref _stationText, value); }

    /// <summary>Stations whose name holds what was typed, busiest first. ⚠️ AsyncPopulator with
    /// FilterMode None: the search already narrowed the list.</summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> StationPopulator =>
        async (text, ct) => (await _service.SearchStationsAsync(text ?? "", 30, ct)).Cast<object>().ToList();

    public IReadOnlyList<PriceBasisChoice> Bases { get; } =
    [
        new(PriceBasis.Sell,  CommonText.PriceTypeSell),
        new(PriceBasis.Buy,   CommonText.PriceTypeBuy),
        new(PriceBasis.Split, CommonText.PriceTypeSplit),
    ];

    /// <summary>The basis inside a sentence — "sell prices", "sell basis" — one phrase per basis,
    /// since a language may not make it from the picker's word. Sell when nothing is picked, as the
    /// appraisal itself does.</summary>
    private static string BasisPhrase(PriceBasis? basis, string sell, string buy, string split) => basis switch
    {
        PriceBasis.Buy   => buy,
        PriceBasis.Split => split,
        _                => sell,
    };

    private PriceBasisChoice? _selectedBasis;
    public PriceBasisChoice? SelectedBasis
    {
        get => _selectedBasis;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedBasis, value);
            if (value is not null && _loaded) UiState.Set(BasisKey, value.Basis.ToString());
        }
    }

    public IReadOnlyList<ValueTargetChoice> Targets { get; } =
    [
        new(false, AssetsText.TargetTheItems),
        new(true,  AssetsText.TargetReprocessedOutput),
    ];

    private ValueTargetChoice? _selectedTarget;
    public ValueTargetChoice? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTarget, value);
            if (value is not null && _loaded) UiState.SetBool(TargetKey, value.Reprocess);
        }
    }

    private decimal _pricePercent = 100;
    /// <summary>The share of the price to value at: 100 is the price itself, 90 a buyback that
    /// pays nine tenths. Re-presents the last result without re-pricing.</summary>
    public decimal PricePercent
    {
        get => _pricePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _pricePercent, Math.Clamp(value, 1, 500));
            if (_valuation is not null) Present(_valuation);
        }
    }

    // ── Compare ────────────────────────────────────────────────────────────

    public ObservableCollection<MarketStation> CompareStations { get; } = [];

    private string _compareText = "";
    public string CompareText { get => _compareText; set => this.RaiseAndSetIfChanged(ref _compareText, value); }

    private MarketStation? _compareCandidate;
    public MarketStation? CompareCandidate { get => _compareCandidate; set => this.RaiseAndSetIfChanged(ref _compareCandidate, value); }

    public Func<string?, CancellationToken, Task<IEnumerable<object>>> ComparePopulator => StationPopulator;

    /// <summary>Adds the station picked in the box, or the first match of what was typed when
    /// nothing was picked from the list. The view clears the box afterwards.</summary>
    public async Task AddCompareAsync()
    {
        var station = CompareCandidate
                      ?? (CompareText.Trim().Length > 0 ? (await _service.SearchStationsAsync(CompareText, 1)).FirstOrDefault() : null);
        CompareText = ""; CompareCandidate = null;
        if (station is null || station.LocationId == SelectedStation?.LocationId || CompareStations.Any(s => s.LocationId == station.LocationId))
            return;
        CompareStations.Add(station);
        SaveCompare();
        if (_valuation is not null) await AppraiseAsync();   // the new station needs pricing
    }

    public void RemoveCompare(MarketStation station)
    {
        CompareStations.Remove(station);
        SaveCompare();
        if (_valuation is not null) Present(_valuation with { Stations = _valuation.Stations.Where(s => s.Station.LocationId != station.LocationId).ToList() });
    }

    private void SaveCompare()
    {
        if (_loaded) UiState.Set(CompareKey, string.Join(",", CompareStations.Select(s => s.LocationId.ToString(CultureInfo.InvariantCulture))));
    }

    // ── Result ─────────────────────────────────────────────────────────────

    private Valuation? _valuation;

    private IReadOnlyList<ValueRowVm> _valueRows = [];
    public IReadOnlyList<ValueRowVm> ValueRows { get => _valueRows; private set => this.RaiseAndSetIfChanged(ref _valueRows, value); }

    private IReadOnlyList<CompareRowVm> _compareRows = [];
    public IReadOnlyList<CompareRowVm> CompareRows { get => _compareRows; private set => this.RaiseAndSetIfChanged(ref _compareRows, value); }

    // ── The filter: free text against the item's name, over both grids ───────
    private IReadOnlyList<ValueRowVm>   _allValueRows   = [];
    private IReadOnlyList<CompareRowVm> _allCompareRows = [];

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { this.RaiseAndSetIfChanged(ref _filter, value ?? ""); ApplyFilter(); }
    }

    private void ApplyFilter()
    {
        var f = _filter.Trim();
        ValueRows   = f.Length == 0 ? _allValueRows   : _allValueRows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        CompareRows = f.Length == 0 ? _allCompareRows : _allCompareRows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The stations the compare grid has columns for, primary first, as of the last result.</summary>
    public IReadOnlyList<MarketStation> CompareColumns { get; private set; } = [];

    private IReadOnlyList<CompareTotalVm> _compareTotals = [];
    public IReadOnlyList<CompareTotalVm> CompareTotals { get => _compareTotals; private set => this.RaiseAndSetIfChanged(ref _compareTotals, value); }

    private bool _hasResult;
    public bool HasResult { get => _hasResult; private set => this.RaiseAndSetIfChanged(ref _hasResult, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private string _status = AssetsText.StatusPasteList;
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _volumeText = "—", _itemsText = "—";
    public string VolumeText { get => _volumeText; private set => this.RaiseAndSetIfChanged(ref _volumeText, value); }
    public string ItemsText  { get => _itemsText;  private set => this.RaiseAndSetIfChanged(ref _itemsText, value); }

    /// <summary>The three values' lines in the Values band — market, build, reprocessed — each
    /// with its total, its standing against the best of the three, and its coverage of the list.</summary>
    private IReadOnlyList<ValueTotalVm> _valueTotals = [];
    public IReadOnlyList<ValueTotalVm> ValueTotals { get => _valueTotals; private set => this.RaiseAndSetIfChanged(ref _valueTotals, value); }

    private string _unparsedText = "";
    public string UnparsedText { get => _unparsedText; private set => this.RaiseAndSetIfChanged(ref _unparsedText, value); }
    public bool HasUnparsed => UnparsedText.Length > 0;

    // ── Commands ───────────────────────────────────────────────────────────

    /// <summary>The stations and the remembered choices. Once, when the tab first opens.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        try
        {
            var stations   = await _service.StationsAsync();
            var savedId    = UiState.GetLong(StationKey, 0);
            var savedBasis = Enum.TryParse<PriceBasis>(UiState.Get(BasisKey), out var b) ? b : PriceBasis.Sell;
            var savedTarget = UiState.GetBool(TargetKey, false);
            var savedCompare = (UiState.Get(CompareKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedStation = stations.FirstOrDefault(s => s.LocationId == savedId) ?? stations.FirstOrDefault();
                StationText     = SelectedStation?.Name ?? "";
                SelectedBasis   = Bases.First(x => x.Basis == savedBasis);
                SelectedTarget  = Targets.First(x => x.Reprocess == savedTarget);
                foreach (var id in savedCompare)
                    if (stations.FirstOrDefault(s => s.LocationId == id) is { } st && st.LocationId != SelectedStation?.LocationId) CompareStations.Add(st);
                if (stations.Count == 0) Status = AssetsText.StatusNoOrdersHeld;
                _loaded = true;
                CompareColumnsChanged?.Invoke();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = string.Format(AssetsText.ErrLoadStations, ex.Message));
        }
    }

    /// <summary>Set when an appraisal is asked for while one runs — a line typed as the last one
    /// was being priced — so the next runs as soon as this one is done rather than never.</summary>
    private bool _appraiseAgain;

    public async Task AppraiseAsync()
    {
        if (IsBusy) { _appraiseAgain = true; return; }
        if (SelectedStation is null) { Status = AssetsText.StatusPickStation; return; }
        if (string.IsNullOrWhiteSpace(InputText)) { Status = AssetsText.StatusNothingToAppraise; return; }

        IsBusy = true;
        Status = AssetsText.StatusAppraising;
        try
        {
            var text      = InputText;
            var station   = SelectedStation;
            var compare   = CompareStations.ToList();
            var basis     = SelectedBasis?.Basis ?? PriceBasis.Sell;
            var reprocess = SelectedTarget?.Reprocess ?? false;
            var valuation = await Task.Run(() => _service.ValueAsync(text, station, compare, basis, reprocess));
            await Dispatcher.UIThread.InvokeAsync(() => Present(valuation));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = string.Format(AssetsText.ErrAppraisalFailed, ex.Message));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
        if (_appraiseAgain) { _appraiseAgain = false; await AppraiseAsync(); }
    }

    public void Clear()
    {
        InputText  = "";
        _valuation = null;
        _allValueRows = []; _allCompareRows = [];
        ValueRows   = [];
        CompareRows = [];
        CompareTotals = [];
        CompareColumns = [];
        CompareColumnsChanged?.Invoke();   // the emptied grid does not keep the last stations' headers
        HasResult  = false;
        VolumeText = ItemsText = "—";
        ValueTotals = [];
        UnparsedText = "";
        this.RaisePropertyChanged(nameof(HasUnparsed));
        Status = AssetsText.StatusPasteList;
    }

    public void OpenItem(int typeId)
    {
        if (typeId > 0) NavigateToItemAction?.Invoke(typeId);
    }

    /// <summary>A word from the view for the status line: a paste that could not be read.</summary>
    public void ShowStatus(string text) => Status = text;

    /// <summary>Whether the text names at least one item the SDE knows: what tells a pasted list
    /// from whatever else was last copied.</summary>
    public Task<bool> NamesAnItemAsync(string text) => Task.Run(() => _service.NamesAnItemAsync(text));

    /// <summary>
    /// Every row's picture, fetched as one batch rather than row by row as they scroll into
    /// view: one request per distinct type, all queued at once behind the image cache's gate,
    /// and from the copy on disk after the first time. A row's icon lands as its fetch does.
    /// </summary>
    private async Task LoadIconsAsync(IReadOnlyList<ValueRowVm> values, IReadOnlyList<CompareRowVm> compare)
    {
        try
        {
            var byType        = values.Where(r => r.TypeId > 0).ToLookup(r => r.TypeId);
            var compareByType = compare.Where(r => r.TypeId > 0).ToLookup(r => r.TypeId);
            await Task.WhenAll(byType.Select(async group =>
            {
                var bmp = await ItemIcons.GetAsync(group.Key, group.First().Blueprint);
                if (bmp is null) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var r in group)                     r.Icon = bmp;
                    foreach (var c in compareByType[group.Key])  c.Icon = bmp;
                });
            }));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = AppErrorLogger.Line("Item icons", ex));
        }
    }

    /// <summary>Both tables as tab-separated text, for a spreadsheet or a chat.</summary>
    public string ResultAsText()
    {
        if (_valuation is null) return "";
        var v  = _valuation;
        var sb = new StringBuilder();
        var basis = BasisPhrase(SelectedBasis?.Basis, AssetsText.CopyBasisSell, AssetsText.CopyBasisBuy, AssetsText.CopyBasisSplit);
        sb.AppendLine(string.Format(v.Reprocessed ? AssetsText.CopyValuedAtReprocessed : AssetsText.CopyValuedAt,
            v.Stations[0].Station.Name, basis, PricePercent));
        sb.AppendLine(string.Join("\t",
            AssetsText.ColItem, AssetsText.AssetColQuantity, AssetsText.CopyColVolumeM3,
            AssetsText.CopyColMarketUnit, AssetsText.CopyColMarketTotal, AssetsText.CopyColMarketPct,
            AssetsText.CopyColBuildUnit, AssetsText.CopyColBuildTotal, AssetsText.CopyColBuildPct,
            AssetsText.CopyColReprocessUnit, AssetsText.CopyColReprocessTotal, AssetsText.CopyColReprocessPct,
            AssetsText.CopyColNote));
        foreach (var r in ValueRows)
            sb.AppendLine($"{r.Name}\t{r.Quantity}\t{r.TotalVolume:0.##}\t{Num(r.Market.Unit)}\t{Num(r.Market.Total)}\t{r.Market.PctText}\t{Num(r.Build.Unit)}\t{Num(r.Build.Total)}\t{r.Build.PctText}\t{Num(r.Reprocess.Unit)}\t{Num(r.Reprocess.Total)}\t{r.Reprocess.PctText}\t{string.Join(" ", new[] { r.Section, r.Problem, r.Note }.Where(s => s.Length > 0))}");
        sb.AppendLine();
        foreach (var t in ValueTotals) sb.AppendLine($"{TotalLabel(t.Name)}\t{t.Exact}\t{t.PctText}\t{t.CoverageText}");
        sb.AppendLine($"{AssetsText.CopyTotalVolumeM3}\t{v.TotalVolume:0.##}");
        if (CompareColumns.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join("\t", AssetsText.ColItem, AssetsText.AssetColQuantity, AssetsText.CopyColVolumeM3) + "\t"
                + string.Join("\t", CompareColumns.Select(s => string.Join("\t",
                    string.Format(AssetsText.CopyColStationUnit,  s.Name),
                    string.Format(AssetsText.CopyColStationTotal, s.Name),
                    string.Format(AssetsText.CopyColStationPct,   s.Name)))));
            foreach (var r in CompareRows)
                sb.AppendLine($"{r.Name}\t{r.Quantity}\t{r.TotalVolume:0.##}\t" + string.Join("\t", r.Cells.Select(c => $"{Num(c.Unit)}\t{Num(c.Total)}\t{c.PctText}")));
            sb.AppendLine();
            foreach (var t in CompareTotals) sb.AppendLine($"{t.Station.Name}\t{t.TotalText}\t{t.PctText}");
        }
        return sb.ToString();

        static string Num(double? d) => d is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";

        // A Values band line by its key (see Present), as the copy names it.
        static string TotalLabel(string name) => name switch
        {
            "market" => AssetsText.CopyTotalMarket,
            "build"  => AssetsText.CopyTotalBuild,
            _        => AssetsText.CopyTotalReprocessed,
        };
    }

    public async Task CopyAsync()
    {
        var text = ResultAsText();
        if (text.Length == 0 || CopyToClipboard is null) return;
        await CopyToClipboard(text);
        Status = AssetsText.StatusCopied;
    }

    // ── Presentation ───────────────────────────────────────────────────────

    private void Present(Valuation v)
    {
        _valuation = v;
        var factor = (double)PricePercent / 100;

        _allValueRows = v.Values.Select(x => new ValueRowVm(x, factor)).ToList();

        // The three values, each against the best of them. The market line carries the station's
        // age; the reprocessed materials are priced at the same station, so it is theirs too.
        double market = v.TotalMarket * factor, build = v.TotalBuild * factor, reprocess = v.TotalReprocess * factor;
        int marketPriced = v.Values.Count(x => x.MarketUnit > 0), buildPriced = v.Values.Count(x => x.BuildUnit > 0), reprocessPriced = v.Values.Count(x => x.ReprocessUnit > 0);
        var bestValue  = new[] { (market, marketPriced), (build, buildPriced), (reprocess, reprocessPriced) }.Where(t => t.Item2 > 0).Select(t => t.Item1).DefaultIfEmpty(0).Max();
        var stationAge = v.Stations[0].AsOf is { } at0 ? DateTimeOffset.UtcNow - at0 : (TimeSpan?)null;
        var ageText    = stationAge is { } sa ? Age(sa) : AssetsText.AgeNoOrdersHeld;
        var stale      = stationAge is null || stationAge.Value.TotalHours >= 2;
        ValueTotals =
        [
            ValueTotal("market",      market,    marketPriced,    v.Values.Count, bestValue, ageText, stale),
            ValueTotal("build",       build,     buildPriced,     v.Values.Count, bestValue, "", false),
            ValueTotal("reprocessed", reprocess, reprocessPriced, v.Values.Count, bestValue, "", false),
        ];
        VolumeText = $"{v.TotalVolume:N0} m³";
        ItemsText  = $"{v.Values.Count:N0} / {v.TotalUnits:N0}";

        // The compare tab: every station side by side, the primary first.
        var columnsChanged = !CompareColumns.Select(s => s.LocationId).SequenceEqual(v.Stations.Select(s => s.Station.LocationId));
        CompareColumns = v.Stations.Select(s => s.Station).ToList();
        _allCompareRows = v.Values.Select(x => new CompareRowVm(x.Item, v.Stations, factor)).ToList();
        ApplyFilter();
        _ = LoadIconsAsync(_allValueRows, _allCompareRows);
        var stationTotals = v.Stations.Select(s => v.Values.Sum(x => x.Item.TypeId > 0 && s.UnitByType.TryGetValue(x.Item.TypeId, out var u) ? u * factor * x.Item.Quantity : 0)).ToList();
        var bestStation = stationTotals.DefaultIfEmpty(0).Max();
        CompareTotals = v.Stations.Select((s, i) =>
        {
            var t      = stationTotals[i];
            var isBest = t > 0 && Math.Abs(t - bestStation) < 0.005;
            var pct    = t > 0 && bestStation > 0 ? (t - bestStation) / bestStation * 100 : 0;
            var priced = v.Values.Count(x => x.Item.TypeId > 0 && s.UnitByType.TryGetValue(x.Item.TypeId, out var u) && u > 0);
            var age    = s.AsOf is { } at ? DateTimeOffset.UtcNow - at : (TimeSpan?)null;
            return new CompareTotalVm(s.Station, t > 0 ? IskText.Compact(t) : "—",
                t <= 0 ? AssetsText.NoPrices : isBest ? AssetsText.Best : IskText.Pct(pct),
                IskText.Colour(t > 0, isBest, pct),
                string.Format(AssetsText.CoveragePriced, priced, v.Values.Count),
                age is { } a ? Age(a) : AssetsText.AgeNoOrdersHeld,
                Stale:     age is null || age.Value.TotalHours >= 2,
                IsPrimary: i == 0);
        }).ToList();
        if (columnsChanged) CompareColumnsChanged?.Invoke();

        UnparsedText = v.Unparsed.Count == 0 ? "" : string.Format(AssetsText.NotRead, string.Join("  |  ", v.Unparsed));
        this.RaisePropertyChanged(nameof(HasUnparsed));
        HasResult = v.Values.Count > 0;

        // "12 items at <station>, sell prices fetched 5 min ago", counted in the form the number
        // needs, with the unpriced count after it when there is one.
        var prices = BasisPhrase(SelectedBasis?.Basis, AssetsText.StatusPricesSell, AssetsText.StatusPricesBuy, AssetsText.StatusPricesSplit);
        var valued = Plurals.Format(AssetsText.ResourceManager,
            v.Reprocessed ? nameof(AssetsText.StatusValuedRowsOther) : nameof(AssetsText.StatusValuedItemsOther),
            v.Values.Count, v.Stations[0].Station.Name, prices, ageText);
        Status = v.Unpriced > 0
            ? string.Format(AssetsText.StatusValuedUnpriced, valued, v.Unpriced)
            : string.Format(AssetsText.StatusValued, valued);
    }

    /// <summary>One value's line for the Values band. A value that priced nothing has no total
    /// and no standing, only the coverage that says so.</summary>
    private static ValueTotalVm ValueTotal(string name, double total, int priced, int of, double best, string ageText, bool stale)
    {
        var has    = priced > 0;
        var isBest = has && Math.Abs(total - best) < 0.005;
        var pct    = has && best > 0 ? (total - best) / best * 100 : 0;
        return new ValueTotalVm(name,
            has ? IskText.Compact(total) : "—",
            has ? IskText.Exact(total)   : AssetsText.NothingToValue,
            !has ? "" : isBest ? AssetsText.Best : IskText.Pct(pct),
            IskText.Colour(has, isBest, pct),
            string.Format(AssetsText.CoveragePriced, priced, of),
            ageText, stale);
    }

    private static string Age(TimeSpan span) =>
        span.TotalMinutes < 1  ? AssetsText.AgeJustNow :
        span.TotalMinutes < 90 ? string.Format(AssetsText.AgeMinutes, span.TotalMinutes) :
        span.TotalHours   < 36 ? string.Format(AssetsText.AgeHours, span.TotalHours) :
                                 Plurals.Format(AssetsText.ResourceManager, nameof(AssetsText.AgeDaysOther),
                                     (long)Math.Round(span.TotalDays, MidpointRounding.AwayFromZero));
}
