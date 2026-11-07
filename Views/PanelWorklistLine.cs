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
        var line = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Right);
        name.Margin = new Thickness(0, 0, 10, 0);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(name);
        line.Children.Add(chevron);
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
        // The popup takes no room on the line itself.
        Child = new Panel { Children = { line, _drop } };

        PointerPressed += (_, e) =>
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
        _vm.Dispose();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => _drop.IsOpen = false;
}
