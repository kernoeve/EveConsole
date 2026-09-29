using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EveConsole.Services.Fitting;
using Avalonia.Input;
using EveConsole.Models;
using EveConsole.ViewModels;
using ReactiveUI;

namespace EveConsole.Views;

public partial class FittingView : UserControl
{
    private readonly List<IDisposable> _handlers = [];

    public FittingView()
    {
        InitializeComponent();
        SplitDropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Get(FitPaneView.TabFormat) is FitTabViewModel ? DragDropEffects.Move : DragDropEffects.None);
        SplitDropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (Vm is { } vm && e.Data.Get(FitPaneView.TabFormat) is FitTabViewModel tab) vm.MoveTab(tab, vm.RightPane);
            e.Handled = true;
        });
    }

    private FittingViewModel? Vm => DataContext as FittingViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is not { } vm) return;
        _ = vm.EnsureLoadedAsync();
        _handlers.Add(vm.WhenAnyValue(x => x.IsSplit).Subscribe(split =>
            Sides.ColumnDefinitions[1].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0)));

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
        _handlers.Add(vm.AskSave.RegisterHandler(async ctx =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            ctx.SetOutput(owner is null ? null : await new SaveFitDialog(ctx.Input).ShowDialog<SaveChoice?>(owner));
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

    /// <summary>A hull chosen under New fit starts a fit on it; the box empties for the next one.</summary>
    private void OnHullPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || HullPicker.SelectedItem is not CatalogEntry hull) return;
        _ = vm.NewFitAsync(hull);
        Dispatcher.UIThread.Post(() => { HullPicker.SelectedItem = null; HullPicker.Text = ""; });
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { SelectedResult: { } entry } vm) _ = vm.AddAsync(entry);
    }
}
