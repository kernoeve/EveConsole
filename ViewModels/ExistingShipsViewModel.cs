using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.Services.Fitting;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One assembled ship in the Existing ships picker.</summary>
public sealed class ExistingShipRowVm(ExistingShip ship)
{
    public ExistingShip Ship { get; } = ship;

    /// <summary>The name its owner gave it; blank when it has none (the hull beside it says what it is).</summary>
    public string ShipName   { get; } = ship.Name ?? "";
    public string HullName   { get; } = SdeNames.Type(ship.HullTypeId, ship.HullName);
    public string OwnerName  { get; } = ship.OwnerName;
    public string SystemName { get; } = ship.SolarSystemId is { } s ? SdeNames.SolarSystem(s, ship.SystemName) : "";

    public double HullValue  => Ship.HullValue;
    public double FitValue   => Ship.FitValue;
    public double TotalValue => Ship.TotalValue;
    public string HullValueText  { get; } = MarketFmt.Isk(ship.HullValue);
    public string FitValueText   { get; } = MarketFmt.Isk(ship.FitValue);
    public string TotalValueText { get; } = MarketFmt.Isk(ship.TotalValue);

    /// <summary>Every word typed is found in the ship's name, its hull's (as shown or in English),
    /// its system's (likewise) or its owner's.</summary>
    public bool Matches(string text)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!(ItemNameFilter.Matches(ShipName, word) || ItemNameFilter.Matches(HullName, word)
                  || ItemNameFilter.Matches(Ship.HullName, word) || ItemNameFilter.Matches(SystemName, word)
                  || ItemNameFilter.Matches(Ship.SystemName, word) || ItemNameFilter.Matches(OwnerName, word)))
                return false;
        return true;
    }
}

/// <summary>
/// The fitting tool's Existing ships picker: every assembled ship the characters and personal
/// corporations own, filtered as typed, most valuable first. The one chosen opens in a new tab as
/// a fit not saved anywhere yet.
/// </summary>
public sealed class ExistingShipsViewModel : ReactiveObject
{
    public ObservableCollection<ExistingShipRowVm> Rows { get; } = [];

    /// <summary>The rows as the grid shows them: filtered, and sorted by its headers (Total value,
    /// highest first, to start).</summary>
    public DataGridCollectionView RowsView { get; }

    private string _filterText = "";
    /// <summary>Part of a ship's name, its hull, its system or its owner; every word must be found.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText == (value ?? "")) return;
            this.RaiseAndSetIfChanged(ref _filterText, value ?? "");
            RowsView.Refresh();
            UpdateCount();
        }
    }

    private ExistingShipRowVm? _selected;
    public ExistingShipRowVm? Selected
    {
        get => _selected;
        set { this.RaiseAndSetIfChanged(ref _selected, value); this.RaisePropertyChanged(nameof(CanOpen)); }
    }
    public bool CanOpen => _selected is not null;

    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _countText = "";
    public string CountText { get => _countText; private set => this.RaiseAndSetIfChanged(ref _countText, value); }

    private string _status = FittingText.ShipsLoading;
    /// <summary>What the values are and what the fit includes — or why the list is empty.</summary>
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ExistingShipsViewModel(Func<CancellationToken, Task<ExistingShipList>> load)
    {
        RowsView = new DataGridCollectionView(Rows) { Filter = o => o is ExistingShipRowVm r && r.Matches(_filterText) };
        RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ExistingShipRowVm.TotalValue), ListSortDirection.Descending));
        _ = LoadAsync(load);
    }

    private async Task LoadAsync(Func<CancellationToken, Task<ExistingShipList>> load)
    {
        try
        {
            await SdeNames.EnsureLoadedAsync();
            // Off the UI thread: the asset rows, a call to ESI per owner for the names, the prices.
            var list = await Task.Run(() => load(CancellationToken.None));
            foreach (var s in list.Ships) Rows.Add(new ExistingShipRowVm(s));
            UpdateCount();

            if (list.Ships.Count == 0) { Status = FittingText.ShipsNone; return; }
            var what = list.PriceBasis.Length > 0 ? string.Format(FittingText.ShipsNote, list.PriceBasis) : FittingText.ShipsNoteNoPrices;
            Status = list.NamesMissing ? what + " " + FittingText.ShipsNamesMissing : what;
        }
        catch (Exception ex)
        {
            Status = string.Format(CommonText.ErrorWithMessage, ex.Message);
        }
        finally { IsLoading = false; }
    }

    private void UpdateCount()
        => CountText = _filterText.Trim().Length == 0 || Rows.Count == 0
            ? string.Format(FittingText.ShipsCount, Rows.Count)
            : string.Format(CommonText.ItemNameFilterShowing, RowsView.Count, Rows.Count);
}
