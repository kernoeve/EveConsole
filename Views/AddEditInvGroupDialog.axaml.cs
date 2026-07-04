using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveCortex.Services;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class AddEditInvGroupDialog : Window
{
    private readonly Func<string, string, Task<IReadOnlyList<LocationOption>>> _searchFn;
    private readonly IReadOnlyList<CollectionOption>                           _collections;
    private long?  _selectedLocationId;
    private string _selectedLocationName = "";

    public AddEditInvGroupDialog(
        InvGroupDialogResult?                                                 existing,
        Func<string, string, Task<IReadOnlyList<LocationOption>>>             searchFn,
        IReadOnlyList<CollectionOption>                                       collections)
    {
        _searchFn    = searchFn;
        _collections = collections;
        InitializeComponent();
        Title = existing == null ? "Add Group" : "Edit Group";

        // Wire scope radio buttons
        ScopeStation.IsCheckedChanged    += OnScopeChanged;
        ScopeSystem.IsCheckedChanged     += OnScopeChanged;
        ScopeRegion.IsCheckedChanged     += OnScopeChanged;
        ScopeEverywhere.IsCheckedChanged += OnScopeChanged;

        // Wire location search
        LocationSearchBox.TextChanged    += OnLocationSearchChanged;
        LocationListBox.SelectionChanged += OnLocationSelected;

        // Populate collection ComboBox
        CollectionBox.ItemsSource = collections;
        int collIdx = 0;
        if (existing?.CollectionId != null)
            for (int i = 0; i < collections.Count; i++)
                if (collections[i].CollectionId == existing.CollectionId)
                    { collIdx = i; break; }
        CollectionBox.SelectedIndex = collIdx;

        // Pre-populate for edit mode
        if (existing is null) return;

        NameBox.Text = existing.Name;
        switch (existing.Scope)
        {
            case "Station":    ScopeStation.IsChecked    = true; break;
            case "System":     ScopeSystem.IsChecked     = true; break;
            case "Region":     ScopeRegion.IsChecked     = true; break;
            default:           ScopeEverywhere.IsChecked = true; break;
        }

        IncludeAssetsBox.IsChecked     = existing.IncludeAssets;
        IncludeJobsBox.IsChecked       = existing.IncludeIndustryJobs;
        IncludeBuyOrdersBox.IsChecked  = existing.IncludeMarketBuyOrders;
        MultiplierBox.Value            = existing.Multiplier;

        if (existing.LocationId.HasValue)
        {
            _selectedLocationId   = existing.LocationId;
            _selectedLocationName = existing.LocationName;
            SelectedLocationText.Text = existing.LocationName;
        }
    }

    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        var everywhere = ScopeEverywhere.IsChecked == true;
        LocationPanel.IsVisible = !everywhere;

        var scope = GetScope();
        LocationLabel.Text = scope == "Station" ? "STATION" : scope == "System" ? "SOLAR SYSTEM" : "REGION";

        _selectedLocationId   = null;
        _selectedLocationName = "";
        LocationSearchBox.Text         = "";
        SelectedLocationText.Text      = "";
        LocationResultsBorder.IsVisible = false;
    }

    private async void OnLocationSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var text = LocationSearchBox.Text ?? "";
        if (text.Length < 2)
        {
            LocationResultsBorder.IsVisible = false;
            return;
        }

        var scope   = GetScope();
        var results = await _searchFn(scope, text);

        LocationListBox.ItemsSource      = results.Select(r => r.Name).ToList();
        LocationResultsBorder.IsVisible  = results.Count > 0;
        LocationListBox.Tag              = results;
    }

    private void OnLocationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (LocationListBox.SelectedIndex < 0) return;
        if (LocationListBox.Tag is not IReadOnlyList<LocationOption> opts) return;
        if (LocationListBox.SelectedIndex >= opts.Count) return;

        var chosen = opts[LocationListBox.SelectedIndex];
        _selectedLocationId   = chosen.Id;
        _selectedLocationName = chosen.Name;

        SelectedLocationText.Text      = chosen.Name;
        LocationSearchBox.Text         = "";
        LocationResultsBorder.IsVisible = false;
        LocationListBox.SelectedIndex  = -1;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Text = "Group name is required.";
            return;
        }

        var scope = GetScope();
        if (scope != "Everywhere" && _selectedLocationId is null)
        {
            ErrorText.Text = $"Select a {scope.ToLowerInvariant()} or choose Everywhere.";
            return;
        }

        var selectedCollection = CollectionBox.SelectedItem as CollectionOption;

        Close(new InvGroupDialogResult(
            name,
            scope,
            scope == "Everywhere" ? null : _selectedLocationId,
            scope == "Everywhere" ? ""   : _selectedLocationName,
            IncludeAssetsBox.IsChecked    == true,
            IncludeJobsBox.IsChecked      == true,
            IncludeBuyOrdersBox.IsChecked == true,
            false, // Contracts Buying not yet implemented
            (int)(MultiplierBox.Value ?? 1),
            selectedCollection?.CollectionId));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private string GetScope()
    {
        if (ScopeStation.IsChecked  == true) return "Station";
        if (ScopeSystem.IsChecked   == true) return "System";
        if (ScopeRegion.IsChecked   == true) return "Region";
        return "Everywhere";
    }
}
