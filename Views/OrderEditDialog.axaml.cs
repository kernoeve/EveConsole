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
    private readonly Func<string, Task<List<BuyerResultVm>>>? _buyerSearchFunc;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _buyerCts;
    private int _typeId;
    private string _typeName = "";

    // The picked buyer. A zero id means the typed text stands on its own — see OrderDialogResult.
    /// <summary>Set while the dialog fills its fields in, so those assignments do not read as
    /// the user typing. See the Edit Order block below.</summary>
    private bool   _loading;

    private long   _buyerId;
    private string _buyerType = "";
    private string _buyerName = "";

    // Parameterless ctor for the XAML previewer only.
    public OrderEditDialog() : this(_ => Task.FromResult(new List<TypeResultVm>()), null) { }

    public OrderEditDialog(Func<string, Task<List<TypeResultVm>>> searchFunc, OrderDialogResult? initial,
                           Func<string, Task<List<BuyerResultVm>>>? buyerSearchFunc = null)
    {
        InitializeComponent();
        _searchFunc      = searchFunc;
        _buyerSearchFunc = buyerSearchFunc;

        if (initial is not null)
        {
            Title = "Edit Order";
            // ⚠️ Assigning BuyerBox.Text raises TextChanged, which runs the buyer search and opens
            // the results list — for a buyer the user already picked and has not touched.
            //
            // Cleared on Opened rather than at the end of this block: setting Text before the
            // window is shown does not necessarily raise TextChanged there and then, and a flag
            // already back to false by the time the event arrives guards nothing. Held until the
            // window is up, which is the earliest moment any change can be the user's doing.
            _loading = true;
            Opened += (_, _) => _loading = false;
            _typeId = initial.TypeId;
            _typeName = initial.TypeName;
            SelectedTypeText.Text = initial.TypeName;
            UnitsBox.Value = initial.Units;
            _buyerId   = initial.BuyerId;
            _buyerType = initial.BuyerType;
            _buyerName = initial.Buyer;
            SelectedBuyerText.Text = initial.Buyer.Length > 0 ? initial.Buyer : "(none selected)";
            BuyerBox.Text = initial.Buyer;
            EstDateBox.Text = initial.EstimatedDate ?? "";
            PriceBox.Value = (decimal)initial.PurchasePrice;
            StatusBox.SelectedIndex = initial.Status switch { "completed" => 1, "canceled" => 2, _ => 0 };
            PriorityBox.IsChecked = initial.IsPriority;
            ContractBox.Text  = initial.LinkedContractId?.ToString() ?? "";
            CompletedBox.Text = initial.CompletedOn ?? "";
        }

        UpdateOk();
    }

    private async void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
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

    // ── Buyer picker ──────────────────────────────────────────────────────────
    //
    // Same shape as the item search above. ⚠️ Typing clears the picked id: the text and the id
    // must not drift apart, or an order would carry one buyer's name against another's id.
    private async void OnBuyerSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var text = BuyerBox.Text ?? "";
        if (text != _buyerName)
        {
            _buyerId = 0; _buyerType = ""; _buyerName = text;
            SelectedBuyerText.Text = text.Length > 0 ? $"{text}  (not linked)" : "(none selected)";
        }

        // The picker belongs to typing. Anything that changes the text while the box is not focused
        // — filling the dialog in, or writing the chosen name back — must not open it.
        if (_buyerSearchFunc is null || !BuyerBox.IsFocused) return;

        _buyerCts?.Cancel();
        _buyerCts = new CancellationTokenSource();
        var ct = _buyerCts.Token;

        if (text.Length < 3) { BuyerResultsList.ItemsSource = null; BuyerResultsBox.IsVisible = false; return; }

        try
        {
            await Task.Delay(250, ct);
            var results = await _buyerSearchFunc(text);
            if (ct.IsCancellationRequested) return;
            BuyerResultsList.ItemsSource = results;
            BuyerResultsBox.IsVisible    = results.Count > 0;
        }
        catch (OperationCanceledException) { }
    }

    private void OnBuyerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (BuyerResultsList.SelectedItem is not BuyerResultVm b) return;

        _buyerId   = b.Id;
        _buyerType = b.EntityType;
        _buyerName = b.Name;
        SelectedBuyerText.Text  = b.Name;
        BuyerBox.Text           = b.Name;   // re-raises TextChanged; the name now matches, so the id survives
        BuyerResultsBox.IsVisible = false;
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
        var completed = string.IsNullOrWhiteSpace(CompletedBox.Text) ? null : CompletedBox.Text!.Trim();
        Close(new OrderDialogResult(
            _typeId, _typeName,
            (int)(UnitsBox.Value ?? 1m),
            BuyerBox.Text ?? "",
            est,
            (double)(PriceBox.Value ?? 0m),
            status,
            PriorityBox.IsChecked == true,
            _buyerId, _buyerType,
            int.TryParse(ContractBox.Text, out var contractId) && contractId > 0 ? contractId : null,
            completed));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
