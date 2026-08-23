using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AddEditPostingDialog : Window
{
    private readonly Func<string, string, Task<IReadOnlyList<LocationOption>>> _searchFn;
    private readonly PostsEditorViewModel _postsVm;

    private long?  _selectedLocationId;
    private string _selectedLocationName = "";
    private bool   _loaded;

    public AddEditPostingDialog(
        PostingDialogResult? existing,
        Func<string, string, Task<IReadOnlyList<LocationOption>>> searchFn,
        IReadOnlyList<StationOption> marketStations,
        IReadOnlyList<PostBlockDraft> existingPosts)
    {
        _searchFn = searchFn;
        InitializeComponent();
        Title = existing == null ? "Add Posting" : "Edit Posting";

        _postsVm    = new PostsEditorViewModel(existingPosts);
        DataContext = _postsVm;

        MarketStationBox.ItemsSource = marketStations;
        NoStationsText.IsVisible     = marketStations.Count == 0;
        PriceTypeBox.ItemsSource     = new[] { "Buy", "Midpoint", "Sell" };
        PriceTypeBox.SelectedItem    = "Sell";

        ScopeStation.IsCheckedChanged    += OnScopeChanged;
        ScopeSystem.IsCheckedChanged     += OnScopeChanged;
        ScopeRegion.IsCheckedChanged     += OnScopeChanged;
        ScopeEverywhere.IsCheckedChanged += OnScopeChanged;
        LocationSearchBox.TextChanged    += OnLocationSearchChanged;
        LocationListBox.SelectionChanged += OnLocationSelected;

        BasisBuild.IsCheckedChanged      += OnBasisChanged;
        BasisContract.IsCheckedChanged   += OnBasisChanged;
        BasisMarket.IsCheckedChanged     += OnBasisChanged;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            switch (existing.Scope)
            {
                case "Station": ScopeStation.IsChecked = true; break;
                case "System":  ScopeSystem.IsChecked  = true; break;
                case "Region":  ScopeRegion.IsChecked  = true; break;
                default:        ScopeEverywhere.IsChecked = true; break;
            }
            if (existing.LocationId.HasValue)
            {
                _selectedLocationId       = existing.LocationId;
                _selectedLocationName     = existing.LocationName;
                SelectedLocationText.Text = existing.LocationName;
            }

            switch (existing.PricingBasis)
            {
                case "Contract": BasisContract.IsChecked = true; break;
                case "Market":   BasisMarket.IsChecked   = true; break;
                default:         BasisBuild.IsChecked    = true; break;
            }
            if (existing.MarketStationId.HasValue)
            {
                MarketStationBox.SelectedItem = marketStations
                    .FirstOrDefault(s => s.LocationId == existing.MarketStationId.Value);
            }
            if (!string.IsNullOrEmpty(existing.MarketPriceType))
                PriceTypeBox.SelectedItem = existing.MarketPriceType;

            PercentBox.Value    = (decimal)existing.PricePercent;
            ShowInStockBox.IsChecked      = existing.ShowInStock;
            ShowInBuildBox.IsChecked      = existing.ShowInBuild;
            ShowReservedBox.IsChecked     = existing.ShowReserved;
            IncludeCompletionBox.IsChecked = existing.IncludeCompletionDate;
            OnlyPackagedBox.IsChecked     = existing.OnlyPackaged;
            ColorByStateBox.IsChecked     = existing.ColorByState;
            ColorInStockField.Value       = existing.ColorInStock;
            ColorInBuildField.Value       = existing.ColorInBuild;
            ColorNoneField.Value          = existing.ColorNone;
        }

        _loaded = true;
    }

    // ── Inventory scope ───────────────────────────────────────────────────────
    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        var everywhere = ScopeEverywhere.IsChecked == true;
        LocationPanel.IsVisible = !everywhere;

        var scope = GetScope();
        LocationLabel.Text = scope == "Station" ? "STATION" : scope == "System" ? "SOLAR SYSTEM" : "REGION";

        _selectedLocationId   = null;
        _selectedLocationName = "";
        LocationSearchBox.Text          = "";
        SelectedLocationText.Text       = "";
        LocationResultsBorder.IsVisible = false;
    }

    private async void OnLocationSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var text = LocationSearchBox.Text ?? "";
        if (text.Length < 2) { LocationResultsBorder.IsVisible = false; return; }

        var results = await _searchFn(GetScope(), text);
        LocationListBox.ItemsSource     = results.Select(r => r.Name).ToList();
        LocationResultsBorder.IsVisible = results.Count > 0;
        LocationListBox.Tag             = results;
    }

    private void OnLocationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (LocationListBox.SelectedIndex < 0) return;
        if (LocationListBox.Tag is not IReadOnlyList<LocationOption> opts) return;
        if (LocationListBox.SelectedIndex >= opts.Count) return;

        var chosen = opts[LocationListBox.SelectedIndex];
        _selectedLocationId   = chosen.Id;
        _selectedLocationName = chosen.Name;
        SelectedLocationText.Text       = chosen.Name;
        LocationSearchBox.Text          = "";
        LocationResultsBorder.IsVisible = false;
        LocationListBox.SelectedIndex   = -1;
    }

    // ── Pricing basis ─────────────────────────────────────────────────────────
    private void OnBasisChanged(object? sender, RoutedEventArgs e)
    {
        MarketPanel.IsVisible = BasisMarket.IsChecked == true;

        // Default the percent to the basis default, but never clobber a value we loaded for edit.
        if (_loaded)
            PercentBox.Value = BasisBuild.IsChecked == true ? 110m : 100m;
    }

    // ── OK / Cancel ───────────────────────────────────────────────────────────
    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { ErrorText.Text = "Posting name is required."; return; }

        var scope = GetScope();
        if (scope != "Everywhere" && _selectedLocationId is null)
        {
            ErrorText.Text = $"Select a {scope.ToLowerInvariant()} or choose Everywhere.";
            return;
        }

        var basis   = GetBasis();
        var station = MarketStationBox.SelectedItem as StationOption;
        if (basis == "Market" && station is null)
        {
            ErrorText.Text = "Select a market for the Specific Market basis.";
            return;
        }

        Close(new PostingDialogResult(
            name,
            scope,
            scope == "Everywhere" ? null : _selectedLocationId,
            scope == "Everywhere" ? ""   : _selectedLocationName,
            basis,
            (double)(PercentBox.Value ?? 100m),
            basis == "Market" ? station!.LocationId : null,
            basis == "Market" ? station!.Name       : "",
            basis == "Market" ? (PriceTypeBox.SelectedItem as string ?? "Sell") : "Sell",
            ShowInStockBox.IsChecked  == true,
            ShowInBuildBox.IsChecked  == true,
            ShowReservedBox.IsChecked == true,
            IncludeCompletionBox.IsChecked == true,
            OnlyPackagedBox.IsChecked == true,
            ColorByStateBox.IsChecked == true,
            Hex(ColorInStockField.Value, "#4a9a5a"),
            Hex(ColorInBuildField.Value, "#c8a84b"),
            Hex(ColorNoneField.Value,    "#888899"),
            _postsVm.ToDrafts()));
    }

    /// <summary>A hex colour, or the default when the box is empty or not one.</summary>
    private static string Hex(string? text, string fallback)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return fallback;
        if (!s.StartsWith('#')) s = "#" + s;
        return System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")
            ? s : fallback;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private string GetScope()
    {
        if (ScopeStation.IsChecked == true) return "Station";
        if (ScopeSystem.IsChecked  == true) return "System";
        if (ScopeRegion.IsChecked  == true) return "Region";
        return "Everywhere";
    }

    private string GetBasis()
    {
        if (BasisContract.IsChecked == true) return "Contract";
        if (BasisMarket.IsChecked   == true) return "Market";
        return "Build";
    }
}
