using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using EveConsole.Localization;
using EveConsole.Services.ClientLayouts;

namespace EveConsole.Views;

/// <summary>
/// One panel on screen. What goes on panels comes later, so for now it shows its name and size,
/// which is enough to see a layout working.
///
/// <para>⚠️ Everything here acts on this window's own handle. It stays out of Alt-Tab, and clicking
/// it doesn't take focus, so a game client keeps its keys. When panels get things to type in, an
/// item that needs typing will have to take focus for itself; the app never hands it back to a
/// client, because clicking back into the game is the user's own action.</para>
/// </summary>
public partial class ClientPanelWindow : Window
{
    private LayoutRect _rect;

    public ClientPanelWindow() => InitializeComponent();

    public void Show(PlacedPanel panel)
    {
        _rect = panel.Rect;
        NameText.Text = panel.Name.Length > 0 ? panel.Name : ClientLayoutsText.PanelDefaultName;
        SizeText.Text = string.Format(CultureInfo.CurrentCulture, ClientLayoutsText.CanvasSize, _rect.Width, _rect.Height);

        // A thin bar has no room for two lines.
        var thin = _rect.Height < 40;
        Label.Orientation = thin ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
        Label.Spacing = thin ? 8 : 2;

        if (!IsVisible)
        {
            Position = new Avalonia.PixelPoint(_rect.X, _rect.Y);
            Width = _rect.Width;
            Height = _rect.Height;
            Show();
            StayOutOfTheWay();
        }
        PutInPlace();
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

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOSENDCHANGING = 0x0400;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
}
