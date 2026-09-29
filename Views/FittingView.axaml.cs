using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Controls;
using EveConsole.Models;
using EveConsole.Services.Fitting;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class FittingView : UserControl
{
    private readonly List<IDisposable> _handlers = [];

    public FittingView() => InitializeComponent();

    private FittingViewModel? Vm => DataContext as FittingViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is not { } vm) return;
        _ = vm.EnsureLoadedAsync();
        Ring.SlotContextRequested += OnRingContext;

        // The dialogs the view model asks for, owned by whichever window this view is in —
        // the main window, or its own after being detached.
        _handlers.Add(vm.AskEft.RegisterHandler(async ctx =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            ctx.SetOutput(owner is null ? null : await new EftPasteDialog().ShowDialog<string?>(owner));
        }));
        _handlers.Add(vm.CopyText.RegisterHandler(async ctx =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(ctx.Input);
            ctx.SetOutput(Unit.Default);
        }));
        _handlers.Add(vm.ConfirmGame.RegisterHandler(async ctx =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            ctx.SetOutput(owner is not null
                && await new ConfirmDialog(ctx.Input, title: "Update fitting in the game").ShowDialog<bool>(owner));
        }));
        _handlers.Add(vm.PickEsiFit.RegisterHandler(async ctx =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var result = owner is null ? null : await new FitSelectorWindow(ctx.Input).ShowDialog<FitSelectorResult?>(owner);
            ctx.SetOutput(result?.Fitting);
        }));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var h in _handlers) h.Dispose();
        _handlers.Clear();
        Ring.SlotContextRequested -= OnRingContext;
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { SelectedResult: { } entry } vm) _ = vm.AddAsync(entry);
    }

    /// <summary>Right-click on the ring: what can be done to that module, as in the game.</summary>
    private void OnRingContext(FittingSlot slot, Point at)
    {
        if (Vm is not { } vm || slot.Tag is not FittingModuleRowVm { IsEmpty: false } row) return;
        vm.SelectedModule = row;

        var items = new List<MenuItem>();
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            return mi;
        }
        if (row.CanToggle)
        {
            items.Add(row.IsOff
                ? Item("Put online", () => vm.SetState(row, ModuleState.Online))
                : Item("Put offline", () => vm.SetState(row, ModuleState.Offline)));
            if (row.CanActivate)
                items.Add(row.State >= ModuleState.Active
                    ? Item("Deactivate", () => vm.SetState(row, ModuleState.Online))
                    : Item("Activate", () => vm.SetState(row, ModuleState.Active)));
            if (row.CanOverheat)
                items.Add(row.IsHeated
                    ? Item("Stop overheating", () => vm.SetState(row, ModuleState.Active))
                    : Item("Overheat", () => vm.SetState(row, ModuleState.Overheated)));
        }
        if (row.Charge is { } charge)
            items.Add(Item($"Unload {charge.Name}", () => row.Charge = null));
        items.Add(Item("Remove", () => vm.Remove(row)));

        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(Ring);
    }

    /// <summary>The module last clicked is where a charge picked in the finder is loaded.</summary>
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is FittingModuleRowVm row && !row.IsEmpty)
            vm.SelectedModule = row;
    }
}
