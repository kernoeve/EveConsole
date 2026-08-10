using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Models;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>
/// Add/edit dialog for a standing buy order.
///
/// Mirrors StandingProjectDialog, and reuses the same two search helpers on
/// CorpActivityService — SearchSdeTypesAsync for the item, and
/// SearchSdeStationsAsync for the location, which already spans NPC stations,
/// player structures and corp structures.
/// </summary>
public partial class StandingBuyOrderDialog : Window
{
    private readonly CorpActivityService _service;
    private readonly StandingBuyOrder?   _existing;

    private int?   _selectedTypeId;
    private string _selectedTypeName = "";
    private long?  _selectedLocationId;
    private string _selectedLocationName = "";

    private CancellationTokenSource? _cts;

    // Parameterless ctor for the XAML designer only.
    public StandingBuyOrderDialog() : this(null!, null) { }

    public StandingBuyOrderDialog(CorpActivityService service, StandingBuyOrder? existing)
    {
        InitializeComponent();
        _service  = service;
        _existing = existing;

        if (existing is null) return;

        Title = "Edit Standing Buy Order";

        _selectedTypeId   = existing.TypeId;
        _selectedTypeName = existing.TypeName;
        ItemSearchBox.Text          = existing.TypeName;
        ItemSelectedLabel.Text      = existing.TypeName;
        ItemSelectedLabel.IsVisible = true;

        _selectedLocationId   = existing.LocationId;
        _selectedLocationName = existing.LocationName;
        StationSearchBox.Text          = existing.LocationName;
        StationSelectedLabel.Text      = existing.LocationName;
        StationSelectedLabel.IsVisible = true;
    }

    // ── Item search ──────────────────────────────────────────────────────────

    private async void OnItemSearchChanged(object? sender, TextChangedEventArgs e)
    {
        // Typing invalidates any previous pick, so a stale id can't be saved with
        // freshly typed text.
        _selectedTypeId   = null;
        _selectedTypeName = "";
        ItemSelectedLabel.IsVisible = false;
        ItemResultsBorder.IsVisible = false;

        var text = ItemSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);   // debounce
            var results = await _service.SearchSdeTypesAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            ItemResultsList.ItemsSource = results;
            ItemResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ItemResultsList.SelectedItem is not SdeTypeResult r) return;
        _selectedTypeId   = r.TypeId;
        _selectedTypeName = r.Name;
        ItemResultsBorder.IsVisible = false;
        ItemSelectedLabel.Text      = r.Name;
        ItemSelectedLabel.IsVisible = true;
    }

    // ── Station / structure search ───────────────────────────────────────────

    private async void OnStationSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _selectedLocationId   = null;
        _selectedLocationName = "";
        StationSelectedLabel.IsVisible = false;
        StationResultsBorder.IsVisible = false;

        var text = StationSearchBox.Text ?? "";
        if (text.Length < 2) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await Task.Delay(250, ct);
            var results = await _service.SearchSdeStationsAsync(text, ct);
            if (ct.IsCancellationRequested) return;
            StationResultsList.ItemsSource = results;
            StationResultsBorder.IsVisible = results.Count > 0;
        }
        catch (Exception) { }
    }

    private void OnStationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (StationResultsList.SelectedItem is not SdeStationResult r) return;
        _selectedLocationId   = r.StationId;
        _selectedLocationName = r.Name;
        StationResultsBorder.IsVisible = false;
        StationSelectedLabel.Text      = r.Name;
        StationSelectedLabel.IsVisible = true;
    }

    // ── Buttons ──────────────────────────────────────────────────────────────

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_selectedTypeId is null)
        {
            ShowError("Pick an item type from the search results.");
            return;
        }
        if (_selectedLocationId is null)
        {
            ShowError("Pick a station or structure from the search results.");
            return;
        }

        var result = _existing ?? new StandingBuyOrder();
        result.TypeId       = _selectedTypeId.Value;
        result.TypeName     = _selectedTypeName;
        result.LocationId   = _selectedLocationId.Value;
        result.LocationName = _selectedLocationName;

        Close(result);
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text      = message;
        ErrorLabel.IsVisible = true;
    }
}
