using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SalePostingView : UserControl
{
    public SalePostingView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not SalePostingViewModel vm) return;

        vm.ShowAddPostingDialog = async () =>
        {
            var stations = await vm.GetMarketStationsAsync();
            var dialog = new AddEditPostingDialog(null, (scope, text) => vm.SearchLocationsAsync(scope, text),
                stations, new List<PostBlockDraft>());
            return await dialog.ShowDialog<PostingDialogResult?>(GetWindow());
        };

        vm.ShowEditPostingDialog = async (row) =>
        {
            var m = row.Model;
            var existing = new PostingDialogResult(
                m.Name, m.Scope, m.LocationId, m.LocationName,
                m.PricingBasis, m.PricePercent, m.MarketStationId, m.MarketStationName, m.MarketPriceType,
                m.ShowInStock, m.ShowInBuild, m.ShowReserved, m.IncludeCompletionDate, m.OnlyPackaged,
                m.ColorByState, m.ColorInStock, m.ColorInBuild, m.ColorNone,
                new List<PostBlockDraft>());
            var stations = await vm.GetMarketStationsAsync();
            var posts    = await vm.GetPostsAsync(row.PostingId);
            var dialog = new AddEditPostingDialog(existing, (scope, text) => vm.SearchLocationsAsync(scope, text),
                stations, posts);
            return await dialog.ShowDialog<PostingDialogResult?>(GetWindow());
        };

        vm.ShowAddSectionDialog = async () =>
        {
            var stations = await vm.GetMarketStationsAsync();
            var dialog = new AddEditSectionDialog(null, (scope, text) => vm.SearchLocationsAsync(scope, text), stations);
            return await dialog.ShowDialog<SectionDialogResult?>(GetWindow());
        };

        vm.ShowEditSectionDialog = async (row) =>
        {
            var m = row.Model;
            var existing = new SectionDialogResult(
                m.Name, m.Prefix, m.HeaderColor, m.RowColor,
                m.OverrideScope, m.Scope, m.LocationId, m.LocationName,
                m.OverridePricing, m.PricingBasis, m.PricePercent,
                m.MarketStationId, m.MarketStationName, m.MarketPriceType,
                m.OverrideOnlyPackaged, m.OnlyPackaged);
            var stations = await vm.GetMarketStationsAsync();
            var dialog = new AddEditSectionDialog(existing, (scope, text) => vm.SearchLocationsAsync(scope, text), stations);
            return await dialog.ShowDialog<SectionDialogResult?>(GetWindow());
        };

        vm.ShowAddItemDialog = async () =>
        {
            var dialog = new AddItemDialog(async text =>
            {
                var results = await vm.SearchTypesAsync(text);
                return results.Select(r => new TypeResultVm(r.TypeId, r.Name)).ToList();
            }, showQuantity: false);
            return await dialog.ShowDialog<AddItemDialogResult?>(GetWindow());
        };

        vm.ShowMarketGroupPickerDialog = async () =>
        {
            var svc = vm.GetBatchAddService();
            if (svc is null) return null;
            var pickerVm = new MarketGroupPickerViewModel(svc, showQuantity: false);
            var win = new MarketGroupPickerWindow(pickerVm);
            return await win.ShowDialog<MarketGroupPickerResult?>(GetWindow());
        };

        vm.ConfirmSlackRepost = async (message) =>
        {
            var dlg = new ConfirmDialog(message);
            return await dlg.ShowDialog<bool>(GetWindow());
        };
    }

    private async void OnCopyBlock(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RenderedBlock block }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(block.RawText);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is SalePostingViewModel vm)
        {
            vm.DeleteSelectedItemCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    // ── Row links (Definitions tab) ───────────────────────────────────────────
    //
    // One template covers posting, section and item rows in the first column, so both handlers
    // live here and each casts to the row kind its own button belongs to.
    private void OnOpenItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as SalePostingItemRow)?.OpenItem();

    private void OnOpenPostingLocation(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as SalePostingRow)?.OpenLocation();

    // ── Export / import ───────────────────────────────────────────────────────
    //
    // In the code-behind because both open a file picker, which needs the window. The view model
    // is handed a stream and knows nothing about pickers, which is also what lets it be tested
    // and reused without one.

    private async void OnExportPosting(object? sender, RoutedEventArgs e)
    {
        // The row the button sits on, not the grid selection: pressing Export on one posting
        // while another is selected would otherwise export the wrong one, silently.
        if ((sender as Control)?.DataContext is not SalePostingRow row) return;
        if (DataContext is not SalePostingViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Export Posting",
            SuggestedFileName = $"{Sanitise(row.PostingName)}.json",
            DefaultExtension  = "json",
            FileTypeChoices   = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await vm.ExportPostingAsync(row.PostingId, stream);
    }

    private async void OnImportPosting(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SalePostingViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import Posting",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        await vm.ImportPostingAsync(stream);
    }

    /// <summary>A posting name is free text and can hold characters a filename cannot.</summary>
    private static string Sanitise(string name)
    {
        var clean = new string(name.Select(
            c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "posting" : clean;
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
