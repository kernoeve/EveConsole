using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class StoresView : UserControl
{
    public StoresView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not StoresViewModel vm) return;
            vm.ConfirmDelete = async message =>
                TopLevel.GetTopLevel(this) is Window owner
                && await new ConfirmDialog(message).ShowDialog<bool>(owner);
            vm.ChooseAddress = async (preview, current) =>
                TopLevel.GetTopLevel(this) is Window owner
                    ? await new DeployAddressDialog(preview, current).ShowDialog<string?>(owner)
                    : "";
        };
    }

    // The row's own DataContext, not the grid selection: pressing Remove on one row while
    // another is selected would otherwise remove the wrong one without saying so.
    private async void OnRemoveSender(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StoreSenderRowVm row) return;
        if (DataContext is not StoresViewModel vm) return;
        await vm.RemoveSenderAsync(row);
    }

    private void OnResetUsage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StoresViewModel vm) vm.ResetUsage();
    }

    // The row's own DataContext, as for Remove above: a decision is about the row whose button
    // was pressed, whatever the grid's selection happens to be.
    private async void OnApproveWebEvent(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StoreWebEventRowVm row) return;
        if (DataContext is not StoresViewModel vm) return;
        await vm.ApproveWebEventAsync(row);
    }

    private async void OnDeclineWebEvent(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StoreWebEventRowVm row) return;
        if (DataContext is not StoresViewModel vm) return;
        await vm.DeclineWebEventAsync(row);
    }

    private async void OnCopySecret(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StoresViewModel vm || vm.WebSecret.Length == 0) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(vm.WebSecret);
    }

    private async void OnCopyCallback(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StoresViewModel vm || vm.WebCallbackUrl.Length == 0) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(vm.WebCallbackUrl);
    }

    /// <summary>A picture from disk for the site's banner; the view model makes it fit.</summary>
    private async void OnChooseBanner(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StoresViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Choose a banner picture",
            AllowMultiple  = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        await vm.SetWebBannerAsync(buffer.ToArray(), files[0].Name);
    }

}
