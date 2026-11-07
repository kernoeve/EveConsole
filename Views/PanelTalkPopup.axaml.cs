using System.Collections;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace EveConsole.Views;

/// <summary>
/// Who is talking, in a small window under or above a single-line Mumble control: under it in the
/// top half of a monitor, above it in the bottom half, so it opens towards the middle.
///
/// <para>It sits over whatever is there, usually a game, and appears on its own while someone is
/// playing. So it stays on top, takes no focus, and clicks go straight through it to what's below.
/// Everything here acts on this window's own handle.</para>
/// </summary>
public partial class PanelTalkPopup : Window
{
    private Control? _anchor;
    private bool _styled;

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
        if (!_styled) ClickThrough();
        PutBesideAnchor();
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

    private void ClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle) return;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED));

        // ⚠️ Clicks only go through a layered window, and a layered window shows nothing until it's told how opaque to be.
        SetLayeredWindowAttributes(handle, 0, 255, LWA_ALPHA);
        _styled = true;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TRANSPARENT = 0x00000020, WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);
    private const uint LWA_ALPHA = 0x00000002;
}
