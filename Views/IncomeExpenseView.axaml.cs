using System;
using Avalonia.Controls;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class IncomeExpenseView : UserControl
{
    private const double RowHeight = 21;   // approx height of one category row

    public IncomeExpenseView()
    {
        InitializeComponent();
        IncomeList.SizeChanged   += (_, _) => ApplyRowLimit();
        DataContextChanged       += (_, _) => ApplyRowLimit();
    }

    // Show as many categories as fit in the list area; the rest roll into "Other".
    private void ApplyRowLimit()
    {
        if (DataContext is IncomeExpenseViewModel vm && IncomeList.Bounds.Height > 0)
            vm.SetMaxRows(Math.Max(1, (int)(IncomeList.Bounds.Height / RowHeight)));
    }
}
