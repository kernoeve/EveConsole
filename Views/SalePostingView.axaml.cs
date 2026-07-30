using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
                m.Name, m.Prefix,
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

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
