using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ItemValuationView : UserControl
{
    public ItemValuationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ItemValuationViewModel vm) return;
            vm.CopyToClipboard = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(text);
            };
            _ = vm.LoadAsync();
        };
    }

    private void OnAppraiseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ItemValuationViewModel vm) _ = vm.AppraiseAsync();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ItemValuationViewModel vm) _ = vm.CopyAsync();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ItemValuationViewModel vm) vm.Clear();
    }

    /// <summary>Ctrl+Enter appraises without leaving the text box, since a paste is usually
    /// followed by nothing else.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is ItemValuationViewModel vm)
        {
            e.Handled = true;
            _ = vm.AppraiseAsync();
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ItemValuationViewModel vm && ResultGrid.SelectedItem is AppraisalRowVm row)
            vm.OpenItem(row);
    }
}
