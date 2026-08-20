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

        // Typing again means the earlier choice is being reconsidered, so the list comes back.
        ShowResults();

        var text = SearchBox.Text ?? "";
        if (text.Length < 2)
        {
            ResultsList.ItemsSource = null;
            OkButton.IsEnabled = false;
            _selected = null;   // so Add can never fire on a choice no longer on screen
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
        if (ResultsList.SelectedItem is not TypeResultVm picked)
        {
            OkButton.IsEnabled = false;
            HintText.IsVisible = true;
            return;
        }

        OkButton.IsEnabled = true;
        HintText.IsVisible = false;

        // Fold the list away and say what was chosen. The quantity field is the next thing the
        // reader wants, and it was previously below two hundred pixels of finished-with list.
        _selected             = picked;
        SelectedText.Text     = picked.Name;
        SelectedPanel.IsVisible = true;
        ResultsPanel.IsVisible  = false;
        QtyBox.Focus();
    }

    /// <summary>Reopens the list, keeping whatever was searched for.</summary>
    private void OnChangeClick(object? sender, RoutedEventArgs e)
    {
        ShowResults();
        SearchBox.Focus();
    }

    private void ShowResults()
    {
        SelectedPanel.IsVisible = false;
        ResultsPanel.IsVisible  = true;
    }

    private TypeResultVm? _selected;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        // Read from the remembered choice, not the list: the list is hidden once something is
        // picked, and a later re-search would clear its selection out from under this.
        if (_selected is not { } selected) return;
        int qty = (int)(QtyBox.Value ?? 1);
        Close(new AddItemDialogResult(selected.TypeId, selected.Name, qty));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
