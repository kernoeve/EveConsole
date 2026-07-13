using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using EveCortex.Models;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class CorpActivityView : UserControl
{
    public CorpActivityView()
    {
        InitializeComponent();
        ExportTop10Button.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            var text = vm.BuildTop10Export();
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        };

        ExportTop10NoIskButton.Click += (_, _) =>
        {
            if (DataContext is not CorpActivityViewModel vm) return;
            var text = vm.BuildTop10ExportNoIsk();
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        };

        Kill24hList.DoubleTapped += OnKill24hDoubleTapped;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not CorpActivityViewModel vm) return;

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CorpActivityViewModel.ShowProjectDetailPanel))
                UpdateProjectsGridRows(vm.ShowProjectDetailPanel);
        };
        UpdateProjectsGridRows(vm.ShowProjectDetailPanel);

        vm.ShowStandingProjectDialog = async (existing) =>
        {
            var dialog = new StandingProjectDialog(vm.Service, existing);
            return await dialog.ShowDialog<CorpStandingProject?>(GetWindow());
        };

        vm.ConfirmDelete = async () =>
        {
            var dlg = new ConfirmDialog("Are you sure you want to delete this standing project?");
            return await dlg.ShowDialog<bool>(GetWindow());
        };
    }

    private void OnKill24hDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not CorpActivityViewModel vm) return;
        if (Kill24hList.SelectedItem is not Activity24hKillRowVm row) return;
        vm.RequestOpenKillmail?.Invoke(row.KillMailId);
    }

    private void UpdateProjectsGridRows(bool showDetail)
    {
        ProjectsOuterGrid.RowDefinitions[1].Height = showDetail ? new GridLength(4)       : GridLength.Auto;
        ProjectsOuterGrid.RowDefinitions[2].Height = showDetail ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;
}
