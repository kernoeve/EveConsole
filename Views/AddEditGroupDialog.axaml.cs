using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AddEditGroupDialog : Window
{
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

        StationBox.ItemsSource    = stations;
        SourceBox.ItemsSource     = sources;
        CollectionBox.ItemsSource = collections;

        if (existingName != null) NameBox.Text = existingName;

        if (existingStationId.HasValue)
            foreach (var s in stations)
                if (s.Id == existingStationId)
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

        Title = existingName == null ? "Add Group" : "Edit Group";
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Text = "Group name is required.";
            return;
        }

        var station    = StationBox.SelectedItem as MarketLevelStation;
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
