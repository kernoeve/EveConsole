using System;
using Avalonia.Controls;
using Avalonia.Threading;
using EveCortex.ViewModels;
using ReactiveUI;

namespace EveCortex.Views;

public partial class CharacterView : UserControl
{
    private IDisposable? _vmSub;

    public CharacterView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vmSub?.Dispose();
        if (DataContext is not OverviewViewModel vm) return;

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
}
