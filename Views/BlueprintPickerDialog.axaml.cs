using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class BlueprintPickerDialog : Window
{
    private readonly Func<string, Task<List<BlueprintSearchResult>>> _searchFn;
    private          List<BlueprintSearchResult>                     _currentResults = [];
    private          BlueprintSearchResult?                          _selected;
    private          bool                                            _ignoreSearch;

    public BlueprintPickerDialog(
        Func<string, Task<List<BlueprintSearchResult>>> searchFn,
        List<IndyPark>                                  parks)
    {
        _searchFn = searchFn;
        InitializeComponent();

        // Populate park ComboBox; pre-select the default park
        ParkBox.Items.Clear();
        ParkBox.Items.Add(new ComboBoxItem { Content = CommonText.NoPark, Tag = (int?)null });
        int defaultIdx = 0;
        for (int i = 0; i < parks.Count; i++)
        {
            ParkBox.Items.Add(new ComboBoxItem { Content = parks[i].Name, Tag = (int?)parks[i].Id });
            if (parks[i].IsDefault) defaultIdx = i + 1; // +1 for "No Park" entry
        }
        ParkBox.SelectedIndex = defaultIdx;
    }

    private async void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_ignoreSearch) return;

        var text = SearchBox.Text ?? "";
        if (text.Length < 2)
        {
            ResultsBorder.IsVisible = false;
            return;
        }

        // Listed by the name shown. The search takes the first forty in English order.
        _currentResults            = [.. (await _searchFn(text)).OrderBy(r => r.DisplayName, StringComparer.CurrentCulture)];
        ResultsListBox.ItemsSource = _currentResults;
        ResultsBorder.IsVisible    = _currentResults.Count > 0;
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsListBox.SelectedIndex < 0 || ResultsListBox.SelectedIndex >= _currentResults.Count)
            return;

        _selected = _currentResults[ResultsListBox.SelectedIndex];

        // Show selection in the search box without re-triggering search
        _ignoreSearch  = true;
        SearchBox.Text = _selected.DisplayName;
        _ignoreSearch  = false;

        ResultsBorder.IsVisible      = false;
        ResultsListBox.SelectedIndex = -1;

        SelectedBlueprintText.Text      = _selected.DisplayName;
        SelectedBlueprintText.IsVisible = true;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;

        if (_selected is null)
        {
            ErrorText.Text      = CommonText.SelectBlueprintFirst;
            ErrorText.IsVisible = true;
            return;
        }

        int me   = (int)(MeBox.Value ?? 10);
        int runs = (int)(RunsBox.Value ?? 1);
        bool wholeChain = ScopeBox.SelectedIndex == 1;

        int? parkId = null;
        if (ParkBox.SelectedItem is ComboBoxItem ci && ci.Tag is int pid)
            parkId = pid;

        Close(new BlueprintPickerResult(
            _selected.BlueprintTypeId,
            _selected.ProductTypeId,
            _selected.ProductName,   // English: the caller keeps it
            me, runs, wholeChain, parkId));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
