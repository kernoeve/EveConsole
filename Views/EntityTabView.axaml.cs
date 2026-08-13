using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class EntityTabView : ReactiveUserControl<EntityTabViewModel>
{
    public EntityTabView() => InitializeComponent();

    // Double-clicking a roster or history row opens that entity in its own tab. The target
    // kind depends on which tab is asking — an alliance's members are corporations, an NPC
    // corporation's are agents — so the view model decides rather than the view.
    private void OnMemberDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not EntityTabViewModel vm) return;
        if (sender is DataGrid { SelectedItem: EntityMemberRow row })
            vm.Open(vm.MemberLinkKind, row.Id);
    }

    /// <summary>Opens the killmail in the Killmail tool, where the full detail lives.</summary>
    private void OnKillmailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: KillmailListRowVm row }) row.OpenKillmail();
    }

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not EntityTabViewModel vm) return;
        if (sender is DataGrid { SelectedItem: EntityHistoryRow row })
            vm.Open(vm.HistoryLinkKind, row.LinkId);
    }
}
