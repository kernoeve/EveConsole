using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class PlayerEntitiesView : ReactiveUserControl<PlayerEntitiesViewModel>
{
    public PlayerEntitiesView()
    {
        InitializeComponent();
    }

    // Double-click opens the entity's killmails — the one thing there is enough data to
    // show about a player entity beyond its name. Alliances have no double-click because
    // the Killmail Browser has no alliance filter to point at.
    private void OnPilotDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Row<PilotRow>(sender) is { } r)
            Vm?.ShowKillmailsFor(KillmailFilterKind.Character, r.Name);
    }

    private void OnCorpDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Row<PlayerCorpRow>(sender) is { } r)
            Vm?.ShowKillmailsFor(KillmailFilterKind.Corporation, r.Name);
    }

    private void OnAllianceDoubleTapped(object? sender, TappedEventArgs e) { }

    private PlayerEntitiesViewModel? Vm => DataContext as PlayerEntitiesViewModel;

    private static T? Row<T>(object? sender) where T : class =>
        sender is DataGrid { SelectedItem: T row } ? row : null;
}
