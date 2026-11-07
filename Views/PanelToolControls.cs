using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The app's tools from a panel: three lines that open a menu of the left navigation, a submenu
/// for each of its sections. Where a tool opens is chosen for the control: a window of its own, the
/// window of tabs worked in last, or a Tool Container.
/// </summary>
public sealed class PanelMenuControl : Panel
{
    private readonly MainWindowViewModel _main;
    private readonly ControlOptions? _options;
    private readonly string _monitorKey;
    private readonly Button _button;

    public PanelMenuControl(MainWindowViewModel main, ControlOptions? options, string monitorKey)
    {
        _main = main;
        _options = options;
        _monitorKey = monitorKey;
        Background = Brushes.Transparent;

        _button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M0,1 H16 M0,7 H16 M0,13 H16"),
                StrokeThickness = 2,
                Width = 16,
                Height = 14,
                [!Shape.StrokeProperty] = new DynamicResourceExtension("TextMutedBrush"),
            },
        };
        ToolTip.SetTip(_button, ClientLayoutsText.MenuTip);
        _button.Click += (_, _) => Open();
        Children.Add(_button);
    }

    private void Open()
    {
        // A menu from a window that never takes focus may not close when clicked away from.
        (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();
        BuildMenu().Open(_button);
    }

    /// <summary>The navigation's sections as submenus, each tool opening where this control is set to.</summary>
    public ContextMenu BuildMenu()
    {
        var target = ToolTargets.Of(_options);
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in _main.NavGroups)
        {
            var section = new MenuItem { Header = group.Title };
            foreach (var item in group.Items)
            {
                var entry = new MenuItem
                {
                    Header = item.Title,
                    Icon = item.IsOpen ? new Ellipse { Width = 5, Height = 5, [!Shape.FillProperty] = new DynamicResourceExtension("AccentBrush") } : null,
                };
                var toolId = item.ToolId;
                entry.Click += (_, _) => _main.OpenToolAt(toolId, target, _monitorKey);
                section.Items.Add(entry);
            }
            menu.Items.Add(section);
        }
        return menu;
    }
}

/// <summary>
/// A Tool Container: the app's own tabs, as in a window of tabs, but with no border, so it stays
/// where the layout puts it. It holds one tool, or tabs that take more dragged in from any window
/// and give theirs up the same way, split in two or not as chosen. Pinned tabs stay, as the
/// Overview does in the main window.
/// </summary>
public sealed class PanelToolContainer : Panel
{
    private readonly MainWindowViewModel _main;
    private readonly string _controlId;
    private readonly ControlOptions? _options;
    private readonly string _monitorKey;
    private readonly WorkspaceView _view = new();
    private TabWorkspace? _workspace;

    public PanelToolContainer(MainWindowViewModel main, string controlId, ControlOptions? options, string monitorKey)
    {
        _main = main;
        _controlId = controlId;
        _options = options;
        _monitorKey = monitorKey;
        this[!BackgroundProperty] = new DynamicResourceExtension("SurfaceBaseBrush");
        Children.Add(_view);

        // Clicked into, it's the window of tabs worked in last, and its tool can be typed in.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (_workspace is { } ws) _main.LastUsedWorkspace = ws;
            (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _workspace = _main.ClaimContainer(_monitorKey, _controlId, _options);
        _view.DataContext = _workspace;
        if (TopLevel.GetTopLevel(this) is Window window) MainWindow.Current?.AddContainerView(window, _view);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MainWindow.Current?.RemoveContainerView(_view);
        _view.DataContext = null;
        if (_workspace is { } ws) _main.ReleaseContainer(ws);
        _workspace = null;
    }
}
