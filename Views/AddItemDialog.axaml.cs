using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AddItemDialog : Window
{
    private readonly Func<string, Task<List<TypeResultVm>>> _searchFunc;
    private CancellationTokenSource? _cts;

    // showQuantity: Inventory Levels needs a per-item target quantity; Sale Posting does not,
    // so it passes false to hide the field.
    public AddItemDialog(Func<string, Task<List<TypeResultVm>>> searchFunc, bool showQuantity = true)
    {
        InitializeComponent();
        _searchFunc = searchFunc;
        QtyPanel.IsVisible = showQuantity;
    }

    private async void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var text = SearchBox.Text ?? "";
        if (text.Length < 2)
        {
            ResultsList.ItemsSource = null;
            OkButton.IsEnabled = false;
            return;
        }

        try
        {
            await Task.Delay(200, ct);
            var results = await _searchFunc(text);
            if (ct.IsCancellationRequested) return;
            ResultsList.ItemsSource = results;
            OkButton.IsEnabled = false;
        }
        catch (OperationCanceledException) { }
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        OkButton.IsEnabled = ResultsList.SelectedItem is TypeResultVm;
        HintText.IsVisible = !OkButton.IsEnabled;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not TypeResultVm selected) return;
        int qty = (int)(QtyBox.Value ?? 1);
        Close(new AddItemDialogResult(selected.TypeId, selected.Name, qty));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
