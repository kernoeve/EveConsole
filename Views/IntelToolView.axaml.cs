using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The Intel tool. See <see cref="IntelViewModel"/>. Live only while on screen: the main window swaps
/// tool views in and out, and a tool nobody is looking at reads nothing.
/// </summary>
public partial class IntelToolView : UserControl
{
    /// <summary>A follow-up's indent in the Feed.</summary>
    public static readonly IValueConverter IndentOf =
        new FuncValueConverter<bool, Thickness>(followUp => followUp ? new Thickness(22, 0, 0, 0) : default);

    public IntelToolView()
    {
        InitializeComponent();
        // Ctrl+Enter saves the paste. Before the box sees it, which would otherwise take it as a new line.
        PasteBox.AddHandler(KeyDownEvent, OnPasteKeyDown, RoutingStrategies.Tunnel);
    }

    private IntelViewModel? Vm => DataContext as IntelViewModel;
    private IntelViewModel? _shown;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show(Vm);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Show(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) Show(Vm);
    }

    private void Show(IntelViewModel? vm)
    {
        if (ReferenceEquals(vm, _shown)) return;
        if (_shown is not null)
        {
            _shown.FocusRequested -= OnFocusRequested;
            _shown.SetOnScreen(false);
        }
        _shown = vm;
        if (vm is not null)
        {
            vm.FocusRequested += OnFocusRequested;
            vm.SetOnScreen(true);
        }
    }

    private void OnFocusRequested(IntelBoardRowVm row) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (BoardList.ContainerFromItem(row) is { } container) container.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Background);

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private void OnTabBoard(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SelectedTab = IntelViewModel.TabBoard; }
    private void OnTabFeed(object? sender, RoutedEventArgs e)  { if (Vm is { } vm) vm.SelectedTab = IntelViewModel.TabFeed; }
    private void OnTabPaste(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SelectedTab = IntelViewModel.TabPaste; }

    // ── Pictures ─────────────────────────────────────────────────────────────

    private void OnOpenPicture(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is IntelPictureVm picture) picture.Open();
        e.Handled = true;
    }

    // ── The Board ────────────────────────────────────────────────────────────

    private void OnBoardRowTapped(object? sender, TappedEventArgs e)
    {
        // A link inside the row (the system, a hull, a pilot) does its own thing.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        // Only the row's own line opens and closes it, not the lines listed under it.
        if ((sender as Control)?.DataContext is IntelBoardRowVm row && e.Source is Visual v
            && v.FindAncestorOfType<IntelEventRow>(includeSelf: true) is null)
            row.Toggle();
    }

    private void OnBoardRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && (v.FindAncestorOfType<IntelEventRow>(includeSelf: true) is not null
                                     || v.FindAncestorOfType<Button>(includeSelf: true) is not null)) return;
        if ((sender as Control)?.DataContext is IntelBoardRowVm row) row.OpenSystem();
    }

    private void OnBoardRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<IntelEventRow>(includeSelf: true) is not null) return;
        if ((sender as Control)?.DataContext is not IntelBoardRowVm row) return;
        e.Handled = true;
        var items = new List<Control> { Item(IntelText.MenuOpenSystem, row.OpenSystem) };
        if (row.CanShowOnMap) items.Add(Item(IntelText.MenuShowOnMap, row.ShowOnMap));
        items.Add(Item(IntelText.MenuCopySystem, () => _ = CopyAsync(row.SystemName)));
        if (row.OnePilotId > 0) items.Add(Item(IntelText.MenuOpenPilot, row.OpenPilot));
        new ContextMenu { ItemsSource = items }.Open(sender as Control ?? this);
    }

    // ── The Feed ─────────────────────────────────────────────────────────────

    private void OnFeedLineContext(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not IntelFeedLineVm line) return;
        e.Handled = true;
        var row = line.Row;
        var items = new List<Control> { Item(IntelText.MenuOpenSystem, row.OpenSystem) };
        if (row.CanShowOnMap) items.Add(Item(IntelText.MenuShowOnMap, row.ShowOnMap));
        items.Add(Item(IntelText.MenuCopySystem, () => _ = CopyAsync(row.SystemName)));
        new ContextMenu { ItemsSource = items }.Open(sender as Control ?? this);
    }

    private static MenuItem Item(string header, Action act)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => act();
        return item;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // ── Paste ────────────────────────────────────────────────────────────────

    private void OnPasteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Vm is { } vm)
        {
            e.Handled = true;
            vm.Paste.SaveFromKeyboard();
        }
    }

    private void OnPastedByPicked(object? sender, EventArgs e) => Vm?.Paste.CharacterPicked();
}
