using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class OrderEditDialog : Window
{
    private readonly Func<string, Task<List<TypeResultVm>>> _searchFunc;
    private CancellationTokenSource? _cts;
    private int _typeId;
    private string _typeName = "";

    // Parameterless ctor for the XAML previewer only.
    public OrderEditDialog() : this(_ => Task.FromResult(new List<TypeResultVm>()), null) { }

    public OrderEditDialog(Func<string, Task<List<TypeResultVm>>> searchFunc, OrderDialogResult? initial)
    {
        InitializeComponent();
        _searchFunc = searchFunc;

        if (initial is not null)
        {
            Title = "Edit Order";
            _typeId = initial.TypeId;
            _typeName = initial.TypeName;
            SelectedTypeText.Text = initial.TypeName;
            UnitsBox.Value = initial.Units;
            BuyerBox.Text = initial.Buyer;
            EstDateBox.Text = initial.EstimatedDate ?? "";
            PriceBox.Value = (decimal)initial.PurchasePrice;
            StatusBox.SelectedIndex = initial.Status switch { "completed" => 1, "canceled" => 2, _ => 0 };
        }

        UpdateOk();
    }

    private async void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var text = SearchBox.Text ?? "";
        if (text.Length < 2) { ResultsList.ItemsSource = null; ResultsBox.IsVisible = false; return; }

        try
        {
            await Task.Delay(200, ct);
            var results = await _searchFunc(text);
            if (ct.IsCancellationRequested) return;
            ResultsList.ItemsSource = results;
            ResultsBox.IsVisible = results.Count > 0;
        }
        catch (OperationCanceledException) { }
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is TypeResultVm t)
        {
            _typeId = t.TypeId;
            _typeName = t.Name;
            SelectedTypeText.Text = t.Name;
            ResultsBox.IsVisible = false;   // collapse the results once an item is chosen
            UpdateOk();
        }
    }

    private void UpdateOk()
    {
        OkButton.IsEnabled = _typeId > 0;
        HintText.IsVisible = _typeId <= 0;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_typeId <= 0) return;
        var status = StatusBox.SelectedIndex switch { 1 => "completed", 2 => "canceled", _ => "pending" };
        var est = string.IsNullOrWhiteSpace(EstDateBox.Text) ? null : EstDateBox.Text!.Trim();
        Close(new OrderDialogResult(
            _typeId, _typeName,
            (int)(UnitsBox.Value ?? 1m),
            BuyerBox.Text ?? "",
            est,
            (double)(PriceBox.Value ?? 0m),
            status));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
