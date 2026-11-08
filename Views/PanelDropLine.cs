using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace EveConsole.Views;

/// <summary>
/// A control on a panel that is one line, and drops a panel down under it when clicked (or above
/// it, at the bottom of a monitor). Clicking anywhere else, a game client included, puts it away.
/// </summary>
public class PanelDropLine : Border
{
    private readonly Control _line;
    private readonly Popup _drop;
    private readonly double _dropWidth;
    private ClientPanelWindow? _window;

    /// <param name="line">What shows on the panel. Stretched, so a click anywhere on the control lands on it.</param>
    /// <param name="dropped">What drops down.</param>
    protected PanelDropLine(Control line, Control dropped, double dropWidth, double dropHeight)
    {
        _line = line;
        _dropWidth = dropWidth;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);

        _drop = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Width = dropWidth,
                Height = dropHeight,
                BorderThickness = new Thickness(1),
                [!Border.BackgroundProperty] = new DynamicResourceExtension("SurfaceBaseBrush"),
                [!Border.BorderBrushProperty] = new DynamicResourceExtension("BorderStrongBrush"),
                Child = dropped,
            },
        };
        _drop.Opened += OnDropOpened;
        _drop.Closed += OnDropClosed;

        // The popup takes no room on the line itself.
        Child = new Panel { Children = { line, _drop } };

        // ⚠️ On the line, not on this control: a press inside the dropped panel is routed up through
        // the popup to here, so a click on a dropdown in it, or a row, closed it.
        if (line is Panel { Background: null } p) p.Background = Brushes.Transparent;
        if (line is Border { Background: null } b) b.Background = Brushes.Transparent;
        line.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // Whatever is in the panel may need the keyboard, and to know when it's clicked away from.
            _window?.TakeFocus();
            if (!_drop.IsOpen && _drop.Child is Border box) box.Width = Math.Max(_dropWidth, Bounds.Width);
            _drop.IsOpen = !_drop.IsOpen;
            e.Handled = true;
        };
    }

    public bool IsDropped => _drop.IsOpen;

    /// <summary>Puts the dropped panel away, as after following a link in it.</summary>
    public void PutAway() => _drop.IsOpen = false;

    /// <summary>The control is on screen: start whatever keeps it up to date.</summary>
    protected virtual void Shown() { }

    /// <summary>The control has left the screen.</summary>
    protected virtual void Hidden() { }

    /// <summary>The panel has just dropped down.</summary>
    protected virtual void Dropped() { }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as ClientPanelWindow;
        // A click on a game client or anything else outside the app: the panel has lost focus.
        if (_window != null) _window.Deactivated += OnWindowDeactivated;
        Shown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window != null) _window.Deactivated -= OnWindowDeactivated;
        _window = null;
        _drop.IsOpen = false;
        _watch?.Stop();
        Hidden();
    }

    /// <summary>
    /// Put away when another program comes to the front, such as a game client: looked at a few
    /// times a second while the panel is down, and when the panel window stops being active.
    /// <para>⚠️ Not just because the panel window stopped being active: a dropdown in the dropped
    /// panel opens a window of its own, which takes that, and the panel closed under the click that
    /// opened it. Nor only then: after that dropdown, the panel window may not be the active window
    /// any more, so a click on the game wouldn't say so.</para>
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
        Dropped();
    }

    private void OnDropClosed(object? sender, EventArgs e) => _watch?.Stop();

    /// <summary>
    /// Only once the app has been in front with the panel down: if Windows didn't let the panel come
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
