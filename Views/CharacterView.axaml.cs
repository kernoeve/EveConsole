using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Models;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class CharacterView : UserControl
{
    private IDisposable? _vmSub;
    private OverviewViewModel? _vm;

    public CharacterView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vmSub?.Dispose();
        if (_vm is not null) _vm.LayoutChanged -= RebuildLayout;

        if (DataContext is not OverviewViewModel vm) { _vm = null; return; }
        _vm = vm;
        _vm.LayoutChanged += RebuildLayout;
        RebuildLayout();

        // LiveCharts doesn't re-render when IsVisible flips false→true without a nudge.
        // Double-post so the invalidate runs after the layout pass triggered by the IsVisible
        // binding, not during it (which would result in a 0×0 chart).
        _vmSub = vm.WhenAnyValue(x => x.HasIncomeData, x => x.HasExpenseData)
            .Subscribe(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        IncomeChart?.InvalidateMeasure();
                        IncomeChart?.InvalidateVisual();
                        ExpenseChart?.InvalidateMeasure();
                        ExpenseChart?.InvalidateVisual();
                    }));
            });
    }

    private Border? SectionFor(string key) => key switch
    {
        "ActivitySummary"   => SectionActivitySummary,
        "Alerts"            => SectionAlerts,
        "Notifications"     => SectionNotifications,
        "News"              => SectionNews,
        "PersonalKillmails" => SectionPersonalKillmails,
        "SaleListingBuild"  => SectionSaleListingBuild,
        "SaleListingMarket" => SectionSaleListingMarket,
        "IncomePie"         => SectionIncomePie,
        "ExpensePie"        => SectionExpensePie,
        "IncomeExpense"     => SectionIncomeExpense,
        "StandingProjects"  => SectionStandingProjects,
        "StandingBuyOrders" => SectionStandingBuyOrders,
        _                   => null,
    };

    // Rebuilds the LayoutHost grid from the current layout, moving section controls into it.
    private void RebuildLayout()
    {
        if (_vm is null || LayoutHost is null || SectionStore is null) return;
        var layout = _vm.Layout;

        // Detach every section from wherever it currently lives.
        foreach (var (key, _) in OverviewLayout.KnownSections)
        {
            if (SectionFor(key) is { } b)
            {
                LayoutHost.Children.Remove(b);
                SectionStore.Children.Remove(b);
            }
        }

        int rows = Math.Max(1, layout.Rows);
        int cols = Math.Max(1, layout.Cols);

        LayoutHost.RowDefinitions.Clear();
        LayoutHost.ColumnDefinitions.Clear();
        for (int r = 0; r < rows; r++)
            LayoutHost.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        for (int c = 0; c < cols; c++)
            LayoutHost.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        foreach (var p in layout.Sections)
        {
            if (!p.Enabled) continue;
            if (SectionFor(p.Key) is not { } b) continue;

            int row     = Math.Clamp(p.Row - 1, 0, rows - 1);
            int col     = Math.Clamp(p.Col - 1, 0, cols - 1);
            int rowSpan = Math.Clamp(p.RowSpan, 1, rows - row);
            int colSpan = Math.Clamp(p.ColSpan, 1, cols - col);

            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetRowSpan(b, rowSpan);
            Grid.SetColumnSpan(b, colSpan);
            b.Margin = new Thickness(6);
            LayoutHost.Children.Add(b);
        }

        // Nudge everything that was just (re)placed to measure again.
        //
        // ⚠️ Reparenting is what makes this necessary. A section is measured wherever it happens
        // to be sitting, and SectionStore is a full-width panel while a layout cell is a fraction
        // of that — so a control that sizes itself from the width it was last given keeps the
        // wrong one. LiveCharts showed this first by measuring to 0×0; DataGrid shows it by
        // computing its star column against the store's width and then overflowing the card,
        // which is why resizing the window "fixed" it. The double Post is deliberate: the first
        // gets past the layout pass this method triggers, the second past the one that settles it.
        Dispatcher.UIThread.Post(() =>
            Dispatcher.UIThread.Post(() =>
            {
                IncomeChart?.InvalidateMeasure();
                IncomeChart?.InvalidateVisual();
                ExpenseChart?.InvalidateMeasure();
                ExpenseChart?.InvalidateVisual();

                foreach (var child in LayoutHost.Children)
                {
                    child.InvalidateMeasure();
                    foreach (var grid in child.GetVisualDescendants().OfType<DataGrid>())
                    {
                        grid.InvalidateMeasure();
                        grid.InvalidateArrange();
                    }
                }
            }));
    }

    private async void OnCustomizeClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dlg = new OverviewCustomizeWindow
        {
            DataContext = new OverviewCustomizeViewModel(_vm.Layout),
        };
        var result = await dlg.ShowDialog<OverviewLayout?>(GetWindow());
        if (result is not null)
            await _vm.ApplyLayoutAsync(result);
    }

    private void OnKillmailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null && (sender as ListBox)?.SelectedItem is Activity24hKillRowVm row)
            _vm.RequestOpenKillmail?.Invoke(row.KillMailId);
    }

    private void OnOpenNotifications(object? sender, RoutedEventArgs e)
        => _vm?.OpenToolRequested?.Invoke("notifications");

    private void OnOpenKillmailsTool(object? sender, RoutedEventArgs e)
        => _vm?.OpenToolRequested?.Invoke("killmails");

    private void OnOpenAlertSettings(object? sender, RoutedEventArgs e)
        => _vm?.OpenAlertSettingsRequested?.Invoke();

    private void OnOpenStandingProjects(object? sender, RoutedEventArgs e)
        => _vm?.NavigateToStandingProjects?.Invoke();

    private void OnOpenStandingBuyOrders(object? sender, RoutedEventArgs e)
        => _vm?.NavigateToStandingBuyOrders?.Invoke();

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
