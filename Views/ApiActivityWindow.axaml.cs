using Avalonia.Controls;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class ApiActivityWindow : Window
{
    public ApiActivityWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not ApiActivityViewModel vm) return;

        vm.Entries.CollectionChanged += (_, _) => UpdateCount(vm.Entries.Count);
        UpdateCount(vm.Entries.Count);

        _ = vm.LoadTokenOptionsAsync();
        _ = vm.LoadMarketScheduleAsync();

        var schedTc = this.FindControl<TabControl>("ScheduleTabControl");
        if (schedTc is not null)
            schedTc.SelectionChanged += (_, _) =>
            {
                if (schedTc.SelectedIndex == 1)
                    _ = vm.LoadMarketScheduleAsync();
            };
    }

    private void UpdateCount(int count)
    {
        if (CountLabel is not null)
            CountLabel.Text = $"{count:N0} entr{(count == 1 ? "y" : "ies")} (max 10,000)";
    }
}
