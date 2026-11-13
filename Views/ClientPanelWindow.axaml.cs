using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>What the controls on a panel need from outside it.</summary>
public sealed record PanelSurroundings(MainWindowViewModel? MainScreen, string MonitorKey, MonitorFocus Focus);

/// <summary>
/// One top-level panel on screen, with everything docked inside it drawn in this one window: any
/// panels within it, and the controls. A panel holding nothing shows its name and size.
///
/// <para>⚠️ Everything here acts on this window's own handle. It stays out of Alt-Tab, and clicking
/// it doesn't take focus, so a game client keeps its keys. When panels get things to type in, an
/// item that needs typing will have to take focus for itself; the app never hands it back to a
/// client, because clicking back into the game is the user's own action.</para>
/// </summary>
public partial class ClientPanelWindow : Window
{
    private LayoutRect _rect;
    private IReadOnlyList<PlacedPart>? _parts;
    private PanelSurroundings? _around;
    private bool _hasHoles;

    /// <summary>A click anywhere on the panel, whatever ends up handling it.</summary>
    public event Action? Pressed;

    /// <summary>The click let go, which is when a button acts.</summary>
    public event Action? Released;

    /// <summary>Asked for by <see cref="BringForwardWithClients"/>; the host raises the monitor.</summary>
    public event Action? MonitorForwardRequested;

    /// <summary>
    /// This panel, the others on its monitor and that monitor's clients in front, as a click on the
    /// panel does: for a tool shown in the panel from the main window, which would otherwise come
    /// forward alone, over clients still behind other windows. Focus stays where it was.
    /// </summary>
    public void BringForwardWithClients()
    {
        if (MonitorForwardRequested is null) BringForward();
        else MonitorForwardRequested();
    }

    public ClientPanelWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => Pressed?.Invoke(), RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => Released?.Invoke(), RoutingStrategies.Tunnel, handledEventsToo: true);
        ScalingChanged += (_, _) => BuildParts();
        Deactivated += (_, _) => StayOutOfTheWay();
        Closed += (_, _) => EveConsole.Services.UiScaleService.Changed -= OnUiScaleChanged;
        EveConsole.Services.UiScaleService.Changed += OnUiScaleChanged;
    }

    /// <summary>
    /// Drawn at the new scale at once. The layout is placed again for it too, which sizes this
    /// window and what's in it afresh.
    /// </summary>
    private void OnUiScaleChanged() => BuildParts();

    /// <summary>
    /// Takes the keyboard, for a tool or menu in this panel the user has just clicked, as a window
    /// of tabs would. Only until something else is clicked: from then on the panel goes back to
    /// never taking focus, so a click on the rest of it leaves a game client with its keys. Nothing
    /// here ever hands focus back to a client.
    /// </summary>
    public void TakeFocus()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        if ((style & WS_EX_NOACTIVATE) != 0) SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style & ~WS_EX_NOACTIVATE));
        if (!IsActive) Activate();
    }

    /// <summary>
    /// In front of other programs' windows, still without taking focus. Clicking won't do this by
    /// itself: a window that never activates doesn't get raised either.
    /// ⚠️ Through always-on-top and straight back, and it doesn't stay on top. HWND_TOP does nothing
    /// here, because Windows ignores it from a program that isn't the one in front.
    /// <para>⚠️ This panel only (SWP_NOOWNERZORDER). Every panel, on every monitor, is owned by the
    /// one hidden window Avalonia parks out-of-taskbar windows under, and Windows moves an owner
    /// with its owned windows: raising one monitor's panels raised all of them, over whatever was
    /// in front on the other monitors (Slack, say). Activating a client then had the taskbar find a
    /// marked panel on top of every monitor, and it dropped everywhere.</para>
    /// </summary>
    public void BringForward()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        const uint flags = SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
        SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    /// <param name="around">Where controls get their values and actions.</param>
    public void Show(PlacedPanel panel, PanelSurroundings around)
    {
        var moved = panel.Rect != _rect;
        var changed = !ReferenceEquals(panel.Parts, _parts) || !ReferenceEquals(around.MainScreen, _around?.MainScreen);
        _rect = panel.Rect;
        _parts = panel.Parts;
        _around = around;
        // A tool's tab finds what to do through its window, as in a window of tabs.
        DataContext = around.MainScreen;

        Surface.Look = panel.Look;
        Label.Children.Clear();
        if ((panel.Parts?.Count ?? 0) == 0) AddLabel(Label, panel.Name, _rect);

        if (!IsVisible)
        {
            Position = new PixelPoint(_rect.X, _rect.Y);
            Width = _rect.Width;
            Height = _rect.Height;
            Show();
            StayOutOfTheWay();
        }
        PutInPlace();
        if (moved || changed)
        {
            BuildParts();
            CutHolesForClients();
        }
    }

    /// <summary>
    /// A client sitting inside this panel has to show through it and get its own clicks, whichever
    /// of them is in front, so this window simply has no surface there. Our own window only: the
    /// client isn't touched.
    /// </summary>
    private void CutHolesForClients()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        var holes = _parts?.Where(p => p.Kind == PlacedPartKind.Client && !p.Rect.IsEmpty).ToList() ?? [];
        if (holes.Count == 0)
        {
            if (_hasHoles) SetWindowRgn(handle, IntPtr.Zero, true);
            _hasHoles = false;
            return;
        }

        var region = CreateRectRgn(0, 0, _rect.Width, _rect.Height);
        foreach (var hole in holes)
        {
            var cut = CreateRectRgn(hole.Rect.X - _rect.X, hole.Rect.Y - _rect.Y, hole.Rect.Right - _rect.X, hole.Rect.Bottom - _rect.Y);
            CombineRgn(region, region, cut, RGN_DIFF);
            DeleteObject(cut);
        }
        SetWindowRgn(handle, region, true);    // Windows owns the region from here on
        _hasHoles = true;
    }

    private void BuildParts()
    {
        Parts.Children.Clear();
        if (_parts == null) return;
        // ⚠️ The layout is in the screen's pixels, already sized for the UI scale (see
        // ClientLayoutCalculator.Calculate), so each part goes at exactly its pixels, and only what
        // it draws inside them is at the UI scale. Not the whole window scaled, as other windows
        // are (UiScaleService is off for this one): that also grew the window's frame, which is the
        // layout's, and pushed what was docked on the right off the end.
        var scale = RenderScaling;
        var uiScale = EveConsole.Services.UiScaleService.Scale;
        foreach (var part in _parts)
        {
            var view = BuildPart(part);
            if (view == null) continue;
            var host = new EveConsole.Services.UiScaleHost(view)
            {
                Scale = uiScale,
                Width = part.Rect.Width / scale,
                Height = part.Rect.Height / scale,
            };
            Canvas.SetLeft(host, (part.Rect.X - _rect.X) / scale);
            Canvas.SetTop(host, (part.Rect.Y - _rect.Y) / scale);
            Parts.Children.Add(host);
        }
    }

    private Control? BuildPart(PlacedPart part)
    {
        switch (part.Kind)
        {
            case PlacedPartKind.Panel:
                var panel = new Panel();
                panel.Children.Add(new ClientPanelSurface { Look = part.Look });
                if (part.Bare)
                {
                    var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    AddLabel(label, part.Name, part.Rect);
                    panel.Children.Add(label);
                }
                return panel;

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.Mumble:
                return new PanelMumbleControl(_around?.MainScreen, part.Options);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.Worklist:
                if (_around?.MainScreen is not { } main) return null;
                var worklist = new PanelWorklistVm(main.WorklistVm, main.CharacterVm.Characters, _around.Focus, _around.MonitorKey)
                {
                    OpenWorklist = () => main.ShowTool("worklist"),
                };
                return PanelControls.WorklistModeOf(part.Options) == WorklistControlMode.SingleLine
                    ? new PanelWorklistLine(worklist)
                    : new PanelWorklistControl { DataContext = worklist };

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.Intel:
                return _around?.MainScreen is not { } intelMain ? null : BuildIntel(intelMain, part.Options, _around);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.Menu:
                return _around?.MainScreen is not { } menuMain ? null : new PanelMenuControl(menuMain, part.Options, _around.MonitorKey);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.ToolContainer:
                return _around?.MainScreen is not { } toolsMain ? null : new PanelToolContainer(toolsMain, part.Id, part.Options, _around.MonitorKey);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.Agent:
                return _around?.MainScreen is not { } agentMain ? null : new PanelAgentControl(agentMain);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) is { } sectionKind && PanelControls.OverviewSectionOf(sectionKind) is { } section:
                return _around?.MainScreen is not { } sectionMain ? null : new PanelOverviewSection(sectionMain, section);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.GroupChatRoom:
                return _around?.MainScreen is not { } roomMain ? null : new PanelGroupChatControl(new PanelGroupChatVm(roomMain.GroupChatVm, part.Options?.GroupChatRoom));

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.DirectMessageChat:
                return _around?.MainScreen is not { } dmChatMain ? null : new PanelDmControl(new PanelDmVm(dmChatMain.MessagesVm));

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.Alerts:
                return _around?.MainScreen is not { } alertsMain ? null : new PanelAlertsControl(alertsMain);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.DirectMessages:
                return _around?.MainScreen is not { } dmMain ? null : PanelChatLine.Messages(dmMain);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) == PanelControlKind.GroupChat:
                return _around?.MainScreen is not { } groupMain ? null : PanelChatLine.GroupChat(groupMain);

            case PlacedPartKind.Control when PanelControls.Parse(part.Control) is { } kind && ClientPanelItemView.Shows(kind):
                var control = new ClientPanelItemView
                {
                    DataContext = _around?.MainScreen,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                control.ShowOnly(kind);

                // Centered in the room the layout gave it, and made smaller to fit when the room is
                // smaller than it — never larger. ⚠️ Not clipped: a gear too tall for a thin bar, or for
                // one whose edge keeps some of its height, was cut off at the top and the side.
                var room = new Panel { Background = Brushes.Transparent };
                room.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = control });
                return room;

            // A client's place is a hole in this window, and an empty cell is just more of the panel.
            default:
                return null;
        }
    }

    /// <summary>
    /// An Intel control: live intel with jumps from the character in this monitor's client, as a
    /// list or as one line that drops the list down. Its links open where any tool opens and bring
    /// that window forward over the clients, putting the dropped list away. Its icons open the Intel
    /// tool, and the tool's Paste tab with this panel's character and system chosen.
    /// </summary>
    private Control BuildIntel(MainWindowViewModel main, ControlOptions? options, PanelSurroundings around)
    {
        var vm = new PanelIntelVm(main.IntelSnapshots, main.IntelDistances, main.CharacterVm.Characters,
                                  around.Focus, around.MonitorKey, options);
        PanelIntelLine? line = null;
        vm.Actions = new IntelRowActions
        {
            ShowOnMap = id =>
            {
                main.ShowTool("universe");
                main.MapVm.ShowUniverseTab().Map.FocusSystems([id]);
            },
            AfterNavigate = () =>
            {
                line?.PutAway();
                Avalonia.Threading.Dispatcher.UIThread.Post(main.BringForwardLastOpened, Avalonia.Threading.DispatcherPriority.Background);
            },
            OpenInTool = id =>
            {
                line?.PutAway();
                main.ShowTool("intel");
                main.IntelVm.ShowSystem(id);
            },
            Watch = main.IntelWatch,
        };
        vm.OpenTool = () =>
        {
            line?.PutAway();
            main.ShowTool("intel");
        };
        // The panel never takes keyboard input: its paste icon opens the tool's Paste tab, with this
        // panel's character and the system it is in already chosen, and the pasting happens there.
        vm.OpenPaste = (characterId, systemId) =>
        {
            line?.PutAway();
            main.ShowTool("intel");
            main.IntelVm.ShowPaste(characterId, systemId);
        };
        vm.OpenChatLogSettings = () =>
        {
            line?.PutAway();
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen != null) main.OpenSettingsOnMonitor?.Invoke(screen.WorkingArea, SettingsText.TabChatLogs);
        };
        if (PanelControls.IntelModeOf(options) == IntelControlMode.SingleLine) return line = new PanelIntelLine(vm);
        return new PanelIntelControl { DataContext = vm };
    }

    /// <summary>Name and size, on one line if the panel is too thin for two.</summary>
    private static void AddLabel(StackPanel into, string name, LayoutRect rect)
    {
        var thin = rect.Height < 40;
        into.Orientation = thin ? Orientation.Horizontal : Orientation.Vertical;
        into.Spacing = thin ? 8 : 2;
        into.Children.Add(new TextBlock
        {
            Text = name.Length > 0 ? name : ClientLayoutsText.PanelDefaultName,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextSecondaryBrush"),
        });
        into.Children.Add(new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasSize, rect.Width, rect.Height),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextFaintBrush"),
        });
    }

    /// <summary>
    /// Exact real pixels, whatever the monitor's scaling makes of Avalonia's units.
    /// ⚠️ SWP_NOSENDCHANGING is what allows a thin bar: without it Windows holds the window to a
    /// minimum height, and a 25 px bar comes out 39 px and overlaps what's next to it. Neither
    /// MinHeight nor answering WM_GETMINMAXINFO lifted that.
    /// </summary>
    private void PutInPlace()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING;
        SetWindowPos(handle, IntPtr.Zero, _rect.X, _rect.Y, _rect.Width, _rect.Height, flags);
    }

    private void StayOutOfTheWay()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    private const int GWL_EXSTYLE = -20, RGN_DIFF = 4;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_NOSENDCHANGING = 0x0400;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr into, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
}
