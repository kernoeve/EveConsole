using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// A Worklist control as one line: the character, and how many tasks of each kind are ready for
/// them. Clicked, the list drops down under it (or above it, at the bottom of a monitor), the same
/// list the control shows in its list mode; clicking anywhere else puts it away.
/// </summary>
public sealed class PanelWorklistLine : Border
{
    private const double DropWidth = 420, DropHeight = 400;

    private readonly PanelWorklistVm _vm;
    private readonly Popup _drop;
    private ClientPanelWindow? _window;

    public PanelWorklistLine(PanelWorklistVm vm)
    {
        _vm = vm;
        DataContext = vm;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        Padding = new Thickness(6, 0);

        var name = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!TextBlock.TextProperty] = new Binding(nameof(PanelWorklistVm.LineName)),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextBrightBrush"),
        };
        var counts = new ItemsControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = vm.Counts,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }),
            ItemTemplate = new FuncDataTemplate<WorklistKindCount>((count, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = count?.Kind, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush"),
                    },
                    new TextBlock
                    {
                        Text = count?.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = 11,
                        FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AccentBrush"),
                    },
                },
            }),
        };
        // Drawn, not a ▾ character: not every font has one.
        var chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Width = 8, Height = 5, VerticalAlignment = VerticalAlignment.Center,
            [!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("TextMutedBrush"),
        };
        // Stretched, so a click anywhere on the control lands on it; its parts are centred themselves.
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Right);
        name.Margin = new Thickness(0, 0, 10, 0);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        // The full tool, for everything this leaves out. A button, so its click doesn't drop the list down.
        var open = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Command = vm.OpenWorklistCommand,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M7 1h4v4M11 1L5.5 6.5M9 7v4H1V3h4"),
                StrokeThickness = 1.2, Width = 12, Height = 12,
                [!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("TextMutedBrush"),
            },
        };
        ToolTip.SetTip(open, EveConsole.Localization.ClientLayoutsText.WorklistOpenTool);
        DockPanel.SetDock(open, Dock.Right);
        line.Children.Add(name);
        line.Children.Add(chevron);
        line.Children.Add(open);
        line.Children.Add(counts);

        _drop = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Width = DropWidth,
                Height = DropHeight,
                BorderThickness = new Thickness(1),
                [!Border.BackgroundProperty] = new DynamicResourceExtension("SurfaceBaseBrush"),
                [!Border.BorderBrushProperty] = new DynamicResourceExtension("BorderStrongBrush"),
                Child = new PanelWorklistControl { DataContext = vm, ManagesLifetime = false },
            },
        };
        _drop.Opened += OnDropOpened;
        _drop.Closed += OnDropClosed;

        // The popup takes no room on the line itself.
        Child = new Panel { Children = { line, _drop } };

        // ⚠️ On the line, not on this control: a press inside the dropped list is routed up through
        // the popup to here, so a click on its character dropdown, or a row, closed the list.
        line.Background = Brushes.Transparent;
        line.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // Its dropdown of characters needs the keyboard and to know when it's clicked away from.
            _window?.TakeFocus();
            if (!_drop.IsOpen && _drop.Child is Border box) box.Width = Math.Max(DropWidth, Bounds.Width);
            _drop.IsOpen = !_drop.IsOpen;
            e.Handled = true;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as ClientPanelWindow;
        // A click on a game client or anything else outside the app: the panel has lost focus.
        if (_window != null) _window.Deactivated += OnWindowDeactivated;
        _vm.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window != null) _window.Deactivated -= OnWindowDeactivated;
        _window = null;
        _drop.IsOpen = false;
        _watch?.Stop();
        _vm.Dispose();
    }

    /// <summary>
    /// Put away when another program comes to the front, such as a game client: looked at a few
    /// times a second while the list is down, and when the panel stops being active.
    /// <para>⚠️ Not just because the panel stopped being active: the character dropdown in the list
    /// opens a window of its own, which takes that, and the list closed under the click that opened
    /// it. Nor only then: after that dropdown, the panel may not be the active window any more, so a
    /// click on the game wouldn't say so.</para>
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(PutAwayIfElsewhere, Avalonia.Threading.DispatcherPriority.Background);

    private Avalonia.Threading.DispatcherTimer? _watch;

    private bool _oursSeen;

    private void OnDropOpened(object? sender, EventArgs e)
    {
        _oursSeen = false;
        _watch ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => Watch());
        _watch.Start();
    }

    private void OnDropClosed(object? sender, EventArgs e) => _watch?.Stop();

    /// <summary>
    /// Only once the app has been in front with the list down: if Windows didn't let the panel come
    /// forward, nothing says the user went elsewhere, and a click elsewhere in the app still puts it away.
    /// </summary>
    private void Watch()
    {
        if (OursInFront()) _oursSeen = true;
        else if (_oursSeen) _drop.IsOpen = false;
    }

    private void PutAwayIfElsewhere()
    {
        if (_drop.IsOpen && !OursInFront()) _drop.IsOpen = false;
    }

    private static bool OursInFront()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero) return false;
        GetWindowThreadProcessId(front, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
