using System.Runtime.InteropServices;

namespace EveConsole.Services;

// Global push-to-talk keyboard hook.
// Windows: low-level keyboard hook via user32.dll (works from any thread; fires on UI thread via callback thread).
// Linux X11: XGrabKey on root window with a dedicated event-loop thread.
// Linux Wayland: not supported (no standard mechanism without portal cooperation).
// Must be installed from the UI thread on Windows (which runs the Win32 message loop).
public sealed class GlobalHotkeyService : IDisposable
{
    // ── Key option catalogue ───────────────────────────────────────────────────
    // WinVk: Windows Virtual Key code.  X11Keysym: X11 keysym constant.
    public static readonly IReadOnlyList<(string Name, int WinVk, uint X11Keysym)> KeyOptions =
    [
        ("Disabled",    0,     0x0000),
        ("F13",         0x7C,  0xFFCA),
        ("F14",         0x7D,  0xFFCB),
        ("F15",         0x7E,  0xFFCC),
        ("F16",         0x7F,  0xFFCD),
        ("F17",         0x80,  0xFFCE),
        ("F18",         0x81,  0xFFCF),
        ("F19",         0x82,  0xFFD0),
        ("F20",         0x83,  0xFFD1),
        ("Scroll Lock", 0x91,  0xFF14),
        ("Pause",       0x13,  0xFF13),
        ("Insert",      0x2D,  0xFF63),
        ("Numpad 0",    0x60,  0xFF9E),
        ("Numpad *",    0x6A,  0xFFAA),
        ("Numpad /",    0x6F,  0xFFAF),
        ("Numpad -",    0x6D,  0xFFAD),
    ];

    public static string VkName(int vk) =>
        KeyOptions.FirstOrDefault(k => k.WinVk == vk).Name ?? $"VK 0x{vk:X2}";

    public static uint VkToX11Keysym(int vk) =>
        KeyOptions.FirstOrDefault(k => k.WinVk == vk).X11Keysym;

    // ── Shared state ──────────────────────────────────────────────────────────
    private int           _configuredVk;
    private volatile bool _keyDown;

    // Fired on a non-UI thread — callers must marshal to the UI thread.
    public Action? OnPress   { get; set; }
    public Action? OnRelease { get; set; }

    public bool IsInstalled => _winHookHandle != IntPtr.Zero || _x11Thread?.IsAlive == true;

    public void Configure(int vk)
    {
        Uninstall();
        _configuredVk = vk;
        _keyDown      = false;
        if (vk != 0) Install();
    }

    public void Install()
    {
        if (_configuredVk == 0) return;

        if (OperatingSystem.IsWindows())
            WinInstall();
        else if (OperatingSystem.IsLinux())
            LinuxInstall();
        // macOS: not yet implemented
    }

    public void Uninstall()
    {
        _keyDown = false;
        if (OperatingSystem.IsWindows())
            WinUninstall();
        else if (OperatingSystem.IsLinux())
            LinuxUninstall();
    }

    public void Dispose() => Uninstall();

    // ══════════════════════════════════════════════════════════════════════════
    // Windows implementation — low-level keyboard hook
    // ══════════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    private IntPtr                _winHookHandle;
    private LowLevelKeyboardProc? _winHookProc; // keep alive — prevents GC collecting the delegate

    private void WinInstall()
    {
        if (_winHookHandle != IntPtr.Zero) return;
        _winHookProc   = WinHookCallback;
        _winHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _winHookProc, GetModuleHandle(null), 0);
        if (_winHookHandle == IntPtr.Zero)
            System.Diagnostics.Debug.WriteLine($"[Hotkey] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    private void WinUninstall()
    {
        if (_winHookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_winHookHandle);
        _winHookHandle = IntPtr.Zero;
        _winHookProc   = null;
    }

    private IntPtr WinHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _configuredVk != 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kbd.vkCode == (uint)_configuredVk)
            {
                if ((wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) && !_keyDown)
                {
                    _keyDown = true;
                    OnPress?.Invoke();
                }
                else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP)
                {
                    _keyDown = false;
                    OnRelease?.Invoke();
                }
            }
        }
        return CallNextHookEx(_winHookHandle, nCode, wParam, lParam);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Linux X11 implementation — XGrabKey on the root window
    // Wayland note: XGrabKey requires an X11 display ($DISPLAY must be set).
    // On Wayland sessions running with XWayland this typically works;
    // on pure Wayland it will fail gracefully with a log message.
    // ══════════════════════════════════════════════════════════════════════════

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers,
        IntPtr grab_window, bool owner_events, int pointer_mode, int keyboard_mode);

    [DllImport("libX11.so.6")]
    private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers,
        IntPtr grab_window);

    [DllImport("libX11.so.6")]
    private static extern int XKeysymToKeycode(IntPtr display, uint keysym);

    [DllImport("libX11.so.6")]
    private static extern int XSelectInput(IntPtr display, IntPtr window, long event_mask);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(IntPtr display, out XEvent event_return);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    // XEvent is a 192-byte union; we only need type (offset 0) and keycode (offset 84 on 64-bit).
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)]  public int  type;
        [FieldOffset(84)] public uint keycode;
    }

    private const int   GrabModeAsync   = 1;
    private const uint  AnyModifier     = 0x8000;
    private const long  KeyPressMask    = 1L;
    private const long  KeyReleaseMask  = 2L;
    private const int   KeyPress        = 2;
    private const int   KeyRelease      = 3;

    private IntPtr  _x11Display = IntPtr.Zero;
    private IntPtr  _x11Root    = IntPtr.Zero;
    private int     _x11Keycode;
    private Thread? _x11Thread;
    private volatile bool _x11Running;

    private void LinuxInstall()
    {
        if (_x11Thread?.IsAlive == true) return;

        var keysym = VkToX11Keysym(_configuredVk);
        if (keysym == 0)
        {
            System.Diagnostics.Debug.WriteLine("[Hotkey/Linux] No X11 keysym for configured key.");
            return;
        }

        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[Hotkey/Linux] XOpenDisplay failed — DISPLAY not set or Wayland-only session.");
            return;
        }

        var root    = XDefaultRootWindow(display);
        var keycode = XKeysymToKeycode(display, keysym);
        if (keycode == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[Hotkey/Linux] XKeysymToKeycode returned 0 for keysym 0x{keysym:X}.");
            XCloseDisplay(display);
            return;
        }

        // Grab with AnyModifier so CapsLock/NumLock don't block the grab.
        XSelectInput(display, root, KeyPressMask | KeyReleaseMask);
        XGrabKey(display, keycode, AnyModifier, root, false, GrabModeAsync, GrabModeAsync);
        XFlush(display);

        _x11Display = display;
        _x11Root    = root;
        _x11Keycode = keycode;
        _x11Running = true;

        _x11Thread = new Thread(X11EventLoop)
        {
            IsBackground = true,
            Name         = "GlobalHotkey-X11",
        };
        _x11Thread.Start();
    }

    private void LinuxUninstall()
    {
        _x11Running = false;

        if (_x11Display != IntPtr.Zero)
        {
            try
            {
                if (_x11Keycode != 0)
                    XUngrabKey(_x11Display, _x11Keycode, AnyModifier, _x11Root);
                XCloseDisplay(_x11Display); // causes XNextEvent to unblock
            }
            catch { }
            _x11Display = IntPtr.Zero;
            _x11Root    = IntPtr.Zero;
            _x11Keycode = 0;
        }

        _x11Thread = null;
    }

    private void X11EventLoop()
    {
        var display = _x11Display;
        while (_x11Running && display != IntPtr.Zero)
        {
            try
            {
                if (XNextEvent(display, out var evt) != 0)
                    break; // display closed

                if (evt.keycode != (uint)_x11Keycode) continue;

                if (evt.type == KeyPress && !_keyDown)
                {
                    _keyDown = true;
                    OnPress?.Invoke();
                }
                else if (evt.type == KeyRelease)
                {
                    _keyDown = false;
                    OnRelease?.Invoke();
                }
            }
            catch
            {
                break;
            }
        }
    }
}
