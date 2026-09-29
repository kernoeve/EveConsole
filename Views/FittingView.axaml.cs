using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.Models;
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
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { SelectedResult: { } entry } vm) _ = vm.AddAsync(entry);
    }

    /// <summary>The module last clicked is where a charge picked in the finder is loaded.</summary>
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.DataContext is FittingModuleRowVm row && !row.IsEmpty)
            vm.SelectedModule = row;
    }
}
