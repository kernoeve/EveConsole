using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class AddEditPostingDialog : Window
{
    private readonly Func<string, string, Task<IReadOnlyList<LocationOption>>> _searchFn;
    private readonly PostsEditorViewModel _postsVm;

    private long?  _selectedLocationId;
    private string _selectedLocationName = "";
    private bool   _loaded;

    /// <summary>
    /// A market as the box lists it: the station named as the screen names it, which the item
    /// template binds, over the station. ⚠️ The station keeps the English, and that is what the
    /// posting saves.
    /// </summary>
    private sealed record ShownMarket(StationOption Station, string Name);

    public AddEditPostingDialog(
        PostingDialogResult? existing,
        Func<string, string, Task<IReadOnlyList<LocationOption>>> searchFn,
        IReadOnlyList<StationOption> marketStations,
        IReadOnlyList<PostBlockDraft> existingPosts)
    {
        _searchFn = searchFn;
        InitializeComponent();
        Title = existing == null ? SalesText.TitleAddPosting : SalesText.TitleEditPosting;

        _postsVm    = new PostsEditorViewModel(existingPosts);
        DataContext = _postsVm;

        var markets = marketStations
            .Select(s => new ShownMarket(s, SdeNames.Location(s.LocationId, s.Name)))
            .OrderBy(m => m.Name, StringComparer.CurrentCulture)
            .ToList();
        MarketStationBox.ItemsSource = markets;
        NoStationsText.IsVisible     = marketStations.Count == 0;
        PriceTypeBox.ItemsSource     = Choice.PriceTypes;
        PriceTypeBox.SelectedItem    = PriceTypeChoice("Sell");

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
                SelectedLocationText.Text = InvLevelService.ScopePlaceName(existing.Scope, existing.LocationId, existing.LocationName);
            }

            switch (existing.PricingBasis)
            {
                case "Contract": BasisContract.IsChecked = true; break;
                case "Market":   BasisMarket.IsChecked   = true; break;
                default:         BasisBuild.IsChecked    = true; break;
            }
            if (existing.MarketStationId.HasValue)
            {
                MarketStationBox.SelectedItem = markets
                    .FirstOrDefault(m => m.Station.LocationId == existing.MarketStationId.Value);
            }
            if (!string.IsNullOrEmpty(existing.MarketPriceType))
                PriceTypeBox.SelectedItem = PriceTypeChoice(existing.MarketPriceType);

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
        LocationLabel.Text = scope == "Station" ? SalesText.LocStationLabel : scope == "System" ? SalesText.LocSolarSystemLabel : SalesText.LocRegionLabel;

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

        var scope   = GetScope();
        var results = await _searchFn(scope, text);

        // A region, system or NPC station is listed, in that name's order, as the screen names it.
        // The option keeps the English, which is what the posting saves. Tag and list stay in one
        // order: a pick is read back by its index.
        var shown = results
            .Select(r => (Option: r, Name: InvLevelService.ScopePlaceName(scope, r.Id, r.Name)))
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();

        LocationListBox.ItemsSource     = shown.Select(x => new ShownPlace(x.Name, x.Option.RegionLabel)).ToList();
        LocationResultsBorder.IsVisible = results.Count > 0;
        LocationListBox.Tag             = shown.Select(x => x.Option).ToList();
    }

    private void OnLocationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (LocationListBox.SelectedIndex < 0) return;
        if (LocationListBox.Tag is not IReadOnlyList<LocationOption> opts) return;
        if (LocationListBox.SelectedIndex >= opts.Count) return;

        var chosen = opts[LocationListBox.SelectedIndex];
        _selectedLocationId   = chosen.Id;
        _selectedLocationName = chosen.Name;
        SelectedLocationText.Text       = InvLevelService.ScopePlaceName(GetScope(), chosen.Id, chosen.Name);
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
        if (string.IsNullOrEmpty(name)) { ErrorText.Text = SalesText.PostingNameRequired; return; }

        var scope = GetScope();
        if (scope != "Everywhere" && _selectedLocationId is null)
        {
            ErrorText.Text = scope switch
            {
                "Station" => SalesText.ScopePickStation,
                "System"  => SalesText.ScopePickSystem,
                _         => SalesText.ScopePickRegion,
            };
            return;
        }

        var basis   = GetBasis();
        var station = (MarketStationBox.SelectedItem as ShownMarket)?.Station;
        if (basis == "Market" && station is null)
        {
            ErrorText.Text = SalesText.SelectMarketForBasis;
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
            basis == "Market" ? ((PriceTypeBox.SelectedItem as Choice<string>)?.Value ?? "Sell") : "Sell",
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

    // The price type a posting saved, as the entry in the box.
    private static Choice<string> PriceTypeChoice(string key) =>
        Choice.PriceTypes.FirstOrDefault(c => c.Value == key) ?? Choice.PriceTypes[^1];

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
