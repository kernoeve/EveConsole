using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AddEditSectionDialog : Window
{
    private readonly Func<string, string, Task<IReadOnlyList<LocationOption>>> _searchFn;

    private long?  _selectedLocationId;
    private string _selectedLocationName = "";
    private bool   _loaded;

    public AddEditSectionDialog(
        SectionDialogResult? existing,
        Func<string, string, Task<IReadOnlyList<LocationOption>>> searchFn,
        IReadOnlyList<StationOption> marketStations)
    {
        _searchFn = searchFn;
        InitializeComponent();
        Title = existing == null ? "Add Section" : "Edit Section";

        MarketStationBox.ItemsSource = marketStations;
        NoStationsText.IsVisible     = marketStations.Count == 0;
        PriceTypeBox.ItemsSource     = new[] { "Buy", "Midpoint", "Sell" };
        PriceTypeBox.SelectedItem    = "Sell";

        OverrideScopeBox.IsCheckedChanged        += (_, _) => ScopePanel.IsVisible        = OverrideScopeBox.IsChecked        == true;
        OverridePricingBox.IsCheckedChanged      += (_, _) => PricingPanel.IsVisible      = OverridePricingBox.IsChecked      == true;
        OverrideOnlyPackagedBox.IsCheckedChanged += (_, _) => OnlyPackagedPanel.IsVisible = OverrideOnlyPackagedBox.IsChecked == true;

        ScopeStation.IsCheckedChanged    += OnScopeChanged;
        ScopeSystem.IsCheckedChanged     += OnScopeChanged;
        ScopeRegion.IsCheckedChanged     += OnScopeChanged;
        ScopeEverywhere.IsCheckedChanged += OnScopeChanged;
        LocationSearchBox.TextChanged    += OnLocationSearchChanged;
        LocationListBox.SelectionChanged += OnLocationSelected;

        BasisBuild.IsCheckedChanged    += OnBasisChanged;
        BasisContract.IsCheckedChanged += OnBasisChanged;
        BasisMarket.IsCheckedChanged   += OnBasisChanged;

        if (existing is not null)
        {
            NameBox.Text   = existing.Name;
            PrefixBox.Text = existing.Prefix;
            HeaderColorField.Value = existing.HeaderColor;
            RowColorField.Value    = existing.RowColor;

            OverrideScopeBox.IsChecked = existing.OverrideScope;
            ScopePanel.IsVisible       = existing.OverrideScope;
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

            OverridePricingBox.IsChecked = existing.OverridePricing;
            PricingPanel.IsVisible       = existing.OverridePricing;
            switch (existing.PricingBasis)
            {
                case "Contract": BasisContract.IsChecked = true; break;
                case "Market":   BasisMarket.IsChecked   = true; break;
                default:         BasisBuild.IsChecked    = true; break;
            }
            if (existing.MarketStationId.HasValue)
                MarketStationBox.SelectedItem = marketStations.FirstOrDefault(s => s.LocationId == existing.MarketStationId.Value);
            if (!string.IsNullOrEmpty(existing.MarketPriceType))
                PriceTypeBox.SelectedItem = existing.MarketPriceType;
            PercentBox.Value = (decimal)existing.PricePercent;

            OverrideOnlyPackagedBox.IsChecked = existing.OverrideOnlyPackaged;
            OnlyPackagedPanel.IsVisible       = existing.OverrideOnlyPackaged;
            OnlyPackagedBox.IsChecked         = existing.OnlyPackaged;
        }

        _loaded = true;
    }

    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        LocationPanel.IsVisible = ScopeEverywhere.IsChecked != true;
        var scope = GetScope();
        LocationLabel.Text = scope == "Station" ? "STATION" : scope == "System" ? "SOLAR SYSTEM" : "REGION";
        _selectedLocationId = null; _selectedLocationName = "";
        LocationSearchBox.Text = ""; SelectedLocationText.Text = ""; LocationResultsBorder.IsVisible = false;
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
        _selectedLocationId = chosen.Id; _selectedLocationName = chosen.Name;
        SelectedLocationText.Text = chosen.Name; LocationSearchBox.Text = "";
        LocationResultsBorder.IsVisible = false; LocationListBox.SelectedIndex = -1;
    }

    private void OnBasisChanged(object? sender, RoutedEventArgs e)
    {
        MarketPanel.IsVisible = BasisMarket.IsChecked == true;
        if (_loaded)
            PercentBox.Value = BasisBuild.IsChecked == true ? 110m : 100m;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { ErrorText.Text = "Section name is required."; return; }

        bool ovScope = OverrideScopeBox.IsChecked == true;
        var scope = ovScope ? GetScope() : "Everywhere";
        if (ovScope && scope != "Everywhere" && _selectedLocationId is null)
        {
            ErrorText.Text = $"Select a {scope.ToLowerInvariant()} or choose Everywhere.";
            return;
        }

        bool ovPrice = OverridePricingBox.IsChecked == true;
        var basis = ovPrice ? GetBasis() : "Build";
        var station = MarketStationBox.SelectedItem as StationOption;
        if (ovPrice && basis == "Market" && station is null)
        {
            ErrorText.Text = "Select a market for the Specific Market basis.";
            return;
        }

        Close(new SectionDialogResult(
            name,
            PrefixBox.Text?.Trim() ?? "",
            HeaderColorField.Value.Trim(),
            RowColorField.Value.Trim(),
            ovScope,
            scope,
            ovScope && scope != "Everywhere" ? _selectedLocationId : null,
            ovScope && scope != "Everywhere" ? _selectedLocationName : "",
            ovPrice,
            basis,
            (double)(PercentBox.Value ?? 100m),
            ovPrice && basis == "Market" ? station!.LocationId : null,
            ovPrice && basis == "Market" ? station!.Name       : "",
            ovPrice && basis == "Market" ? (PriceTypeBox.SelectedItem as string ?? "Sell") : "Sell",
            OverrideOnlyPackagedBox.IsChecked == true,
            OnlyPackagedBox.IsChecked == true));
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
