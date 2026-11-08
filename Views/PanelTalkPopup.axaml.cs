using System.Collections;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Who is talking, in a small window under or above a single-line Mumble control: under it in the
/// top half of a monitor, above it in the bottom half, so it opens towards the middle.
///
/// <para>It sits over whatever is there, usually a game, and appears on its own while someone is
/// playing. So it stays on top and takes no focus. Clicks land on it rather than going through: a
/// right-click on someone opens the person menu (user, 2026-11-07). Everything here acts on this
/// window's own handle.</para>
/// </summary>
public partial class PanelTalkPopup : Window
{
    private Control? _anchor;
    private bool _styled;

    /// <summary>Someone in it was right-clicked.</summary>
    public event Action<MumbleOverlayLine>? PersonMenuRequested;

    /// <summary>What the person menu hangs from: this window itself, which stays while the people in it come and go.</summary>
    public Control MenuAnchor => Root;

    /// <summary>The person menu is open on it: it stays up, even once nobody is talking.</summary>
    public bool MenuOpen { get; set; }

    public PanelTalkPopup()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PutBesideAnchor();
    }

    public void Show(IEnumerable talkers, Control anchor)
    {
        _anchor = anchor;
        Talkers.ItemsSource = talkers;
        if (!IsVisible) Show();
        if (!_styled) NoFocus();
        PutBesideAnchor();
    }

    private void OnPersonMenu(object? sender, Avalonia.Controls.ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: MumbleOverlayLine line }) return;
        e.Handled = true;
        PersonMenuRequested?.Invoke(line);
    }

    private void PutBesideAnchor()
    {
        if (_anchor == null || !IsVisible || TopLevel.GetTopLevel(_anchor) is not { } top) return;
        var at = _anchor.TranslatePoint(new Point(0, 0), top);
        if (at == null) return;
        var corner = top.PointToScreen(at.Value);
        var scale = top.RenderScaling;
        var width = (int)(Bounds.Width * RenderScaling);
        var height = (int)(Bounds.Height * RenderScaling);
        var anchorHeight = (int)(_anchor.Bounds.Height * scale);

        var monitor = Screens.ScreenFromPoint(corner)?.Bounds ?? new PixelRect(corner, new PixelSize(1, 1));
        var opensDown = corner.Y + anchorHeight / 2 < monitor.Y + monitor.Height / 2;
        var x = Math.Clamp(corner.X, monitor.X, Math.Max(monitor.X, monitor.Right - width));
        Position = new PixelPoint(x, opensDown ? corner.Y + anchorHeight : corner.Y - height);
    }

    /// <summary>Takes no focus from the game by being shown or clicked, and stays out of the taskbar and Alt+Tab.</summary>
    private void NoFocus()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        _styled = true;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
}
