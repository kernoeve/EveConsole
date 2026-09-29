using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class AddEditGroupDialog : Window
{
    /// <summary>
    /// A station as the box lists it: named as the screen names it, which the item template binds,
    /// over the station. ⚠️ The station keeps the English, and that is what the group saves.
    /// </summary>
    private sealed record ShownStation(MarketLevelStation Station, string Name)
    {
        public string Kind => Station.Kind;
    }

    public AddEditGroupDialog(
        string?                              existingName,
        long?                                existingStationId,
        int?                                 existingSourceId,
        double?                              existingMaxPct,
        IReadOnlyList<MarketLevelStation>    stations,
        IReadOnlyList<MarketSourceOptionVm>  sources,
        IReadOnlyList<CollectionOption>      collections,
        int?                                 existingCollectionId = null,
        int                                  existingMultiplier = 1)
    {
        InitializeComponent();

        var shown = stations
            .Select(s => new ShownStation(s, SdeNames.Location(s.Id, s.Name)))
            .OrderBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        StationBox.ItemsSource    = shown;
        SourceBox.ItemsSource     = sources;
        CollectionBox.ItemsSource = collections;

        if (existingName != null) NameBox.Text = existingName;

        if (existingStationId.HasValue)
            foreach (var s in shown)
                if (s.Station.Id == existingStationId)
                    { StationBox.SelectedItem = s; break; }

        foreach (var src in sources)
            if (src.Id == existingSourceId)
                { SourceBox.SelectedItem = src; break; }
        if (SourceBox.SelectedIndex < 0 && sources.Count > 0)
            SourceBox.SelectedIndex = 0;

        if (existingMaxPct.HasValue)
            MaxPctBox.Text = existingMaxPct.Value.ToString("G");

        MultiplierBox.Value = existingMultiplier;

        // Pre-select collection
        int collIdx = 0;
        for (int i = 0; i < collections.Count; i++)
            if (collections[i].CollectionId == existingCollectionId)
                { collIdx = i; break; }
        CollectionBox.SelectedIndex = collIdx;

        Title = existingName == null ? MarketText.TitleAddGroup : MarketText.TitleEditGroup;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Text = MarketText.ErrGroupNameRequired;
            return;
        }

        var station    = (StationBox.SelectedItem as ShownStation)?.Station;
        var source     = SourceBox.SelectedItem as MarketSourceOptionVm;
        var collection = CollectionBox.SelectedItem as CollectionOption;
        double? maxPct = double.TryParse(MaxPctBox.Text, out var p) ? p : null;
        int multiplier = (int)(MultiplierBox.Value ?? 1);
        if (multiplier < 1) multiplier = 1;

        Close(new GroupDialogResult(
            name,
            station?.Id ?? 0L,
            station?.Name ?? "",
            source?.Id,
            maxPct,
            multiplier,
            collection?.CollectionId));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
