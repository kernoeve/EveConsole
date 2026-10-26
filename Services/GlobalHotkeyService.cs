using System.Runtime.InteropServices;
using EveConsole.Localization;

namespace EveConsole.Services;

// Global push-to-talk keyboard hook.
// Windows: low-level keyboard hook via user32.dll (works from any thread; fires on UI thread via callback thread).
// Linux X11: XGrabKey on root window with a dedicated event-loop thread.
// Linux Wayland: not supported (no standard mechanism without portal cooperation).
// macOS: a Carbon hot key on the main thread's event loop (see MacInstall).
// Must be installed from the UI thread on Windows (which runs the Win32 message loop).
public sealed class GlobalHotkeyService : IDisposable
{
    // ── Key option catalogue ───────────────────────────────────────────────────
    // WinVk: Windows Virtual Key code.  X11Keysym: X11 keysym constant.
    // MacKeyCode: macOS virtual key code (kVK_*); NoMacKey where a Mac keyboard has no such key.
    // ⚠️ Not 0 for "none": 0 is kVK_ANSI_A on a Mac.
    public const uint NoMacKey = uint.MaxValue;

    public static readonly IReadOnlyList<(string Name, int WinVk, uint X11Keysym, uint MacKeyCode)> KeyOptions =
    [
        (SettingsText.PttKeyDisabled,    0,     0x0000, NoMacKey),
        ("F13",         0x7C,  0xFFCA, 0x69),
        ("F14",         0x7D,  0xFFCB, 0x6B),
        ("F15",         0x7E,  0xFFCC, 0x71),
        ("F16",         0x7F,  0xFFCD, 0x6A),
        ("F17",         0x80,  0xFFCE, 0x40),
        ("F18",         0x81,  0xFFCF, 0x4F),
        ("F19",         0x82,  0xFFD0, 0x50),
        ("F20",         0x83,  0xFFD1, 0x5A),
        ("Scroll Lock", 0x91,  0xFF14, NoMacKey),
        ("Pause",       0x13,  0xFF13, NoMacKey),
        ("Insert",      0x2D,  0xFF63, 0x72),   // the Help key, where a Mac keyboard has Insert
        ("Numpad 0",    0x60,  0xFF9E, 0x52),
        ("Numpad *",    0x6A,  0xFFAA, 0x43),
        ("Numpad /",    0x6F,  0xFFAF, 0x4B),
        ("Numpad -",    0x6D,  0xFFAD, 0x4E),
    ];

    /// <summary>Whether this machine can take a key as the push-to-talk key: on a Mac, only the
    /// ones its keyboard has.</summary>
    public static bool AvailableHere(int vk) =>
        vk == 0 || !OperatingSystem.IsMacOS() || VkToMacKeyCode(vk) != NoMacKey;

    public static uint VkToMacKeyCode(int vk) =>
        KeyOptions.FirstOrDefault(k => k.WinVk == vk) is { Name: not null } k ? k.MacKeyCode : NoMacKey;

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

    public bool IsInstalled => _winHookHandle != IntPtr.Zero || _x11Thread?.IsAlive == true || _macHotKey != IntPtr.Zero;

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
        else if (OperatingSystem.IsMacOS())
            MacInstall();
    }

    public void Uninstall()
    {
        _keyDown = false;
        if (OperatingSystem.IsWindows())
            WinUninstall();
        else if (OperatingSystem.IsLinux())
            LinuxUninstall();
        else if (OperatingSystem.IsMacOS())
            MacUninstall();
    }

    public void Dispose() => Uninstall();

    // ══════════════════════════════════════════════════════════════════════════
    // macOS implementation — a Carbon hot key
    // ══════════════════════════════════════════════════════════════════════════
    //
    // ⚠️ RegisterEventHotKey, not a keyboard event tap. It reports the key going down AND coming up,
    // which push-to-talk needs, and asks for no permission; an event tap sees every key typed and
    // so needs the Accessibility permission granted in System Settings. Its events arrive on the
    // main thread's event loop, which AppKit (and so Avalonia) already runs, so it is installed
    // and removed on the UI thread and needs no thread of its own.

    private const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint eventClass; public uint eventKind; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID { public uint signature; public uint id; }

    private delegate int CarbonEventHandler(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [DllImport(CarbonLib)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(CarbonLib)] private static extern int InstallEventHandler(IntPtr target, IntPtr handler, nuint numTypes,
        [In] EventTypeSpec[] list, IntPtr userData, out IntPtr handlerRef);
    [DllImport(CarbonLib)] private static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(CarbonLib)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id,
        IntPtr target, uint options, out IntPtr hotKeyRef);
    [DllImport(CarbonLib)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);
    [DllImport(CarbonLib)] private static extern uint GetEventKind(IntPtr theEvent);

    private const uint KEventClassKeyboard  = 0x6B657962;   // 'keyb'
    private const uint KEventHotKeyPressed  = 5;
    private const uint KEventHotKeyReleased = 6;

    private IntPtr              _macHotKey;
    private IntPtr              _macHandler;
    private CarbonEventHandler? _macCallback;   // kept alive: Carbon holds only a function pointer

    /// <summary>Why the last attempt to register the Mac hot key failed, or null.</summary>
    public string? MacError { get; private set; }

    private static void OnUiThread(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Invoke(action);
    }

    private void MacInstall() => OnUiThread(() =>
    {
        if (_macHotKey != IntPtr.Zero) return;
        MacError = null;

        var code = VkToMacKeyCode(_configuredVk);
        if (code == NoMacKey) { MacError = "no such key on a Mac keyboard"; return; }

        try
        {
            _macCallback = MacCallback;
            var target = GetApplicationEventTarget();
            EventTypeSpec[] kinds =
            [
                new() { eventClass = KEventClassKeyboard, eventKind = KEventHotKeyPressed },
                new() { eventClass = KEventClassKeyboard, eventKind = KEventHotKeyReleased },
            ];

            var status = InstallEventHandler(target, Marshal.GetFunctionPointerForDelegate(_macCallback),
                                             (nuint)kinds.Length, kinds, IntPtr.Zero, out _macHandler);
            if (status != 0) { MacError = $"InstallEventHandler {status}"; _macHandler = IntPtr.Zero; _macCallback = null; return; }

            // 'EVEC': an id of our own; with one hot key there is nothing to tell apart.
            status = RegisterEventHotKey(code, 0, new EventHotKeyID { signature = 0x45564543, id = 1 },
                                         target, 0, out _macHotKey);
            if (status != 0)
            {
                MacError = $"RegisterEventHotKey {status}";
                _macHotKey = IntPtr.Zero;
                RemoveEventHandler(_macHandler);
                _macHandler  = IntPtr.Zero;
                _macCallback = null;
            }
        }
        catch (Exception ex)
        {
            MacError = ex.Message;
            _macHotKey = _macHandler = IntPtr.Zero;
            _macCallback = null;
        }
    });

    private void MacUninstall() => OnUiThread(() =>
    {
        try
        {
            if (_macHotKey  != IntPtr.Zero) UnregisterEventHotKey(_macHotKey);
            if (_macHandler != IntPtr.Zero) RemoveEventHandler(_macHandler);
        }
        catch { /* shutting down; nothing left to undo */ }
        _macHotKey   = IntPtr.Zero;
        _macHandler  = IntPtr.Zero;
        _macCallback = null;
    });

    private int MacCallback(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        switch (GetEventKind(theEvent))
        {
            case KEventHotKeyPressed when !_keyDown:
                _keyDown = true;
                OnPress?.Invoke();
                break;
            case KEventHotKeyReleased when _keyDown:
                _keyDown = false;
                OnRelease?.Invoke();
                break;
        }
        return 0;   // noErr: handled
    }

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
    private Thread?               _winThread;
    private uint                  _winThreadId;

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    private const uint WM_QUIT = 0x0012;

    /// <summary>
    /// ⚠️ The hook lives on a thread of its own, with its own message loop, not on the UI thread.
    /// Windows delivers a low-level keyboard hook through the message queue of the thread that
    /// installed it and gives that thread a few hundred milliseconds to answer; a thread busy
    /// with anything else — opening a microphone, laying out a grid, playing a voice — misses
    /// the keystroke, and one that misses often enough has the hook removed without a word.
    /// That is how a release could go unseen and a recording run on into the next press. This
    /// thread does nothing but pump, so the callback answers at once every time.
    /// </summary>
    private void WinInstall()
    {
        if (_winThread?.IsAlive == true) return;

        var ready = new ManualResetEventSlim(false);
        _winThread = new Thread(() =>
        {
            _winHookProc   = WinHookCallback;
            _winHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _winHookProc, GetModuleHandle(null), 0);
            _winThreadId   = GetCurrentThreadId();
            if (_winHookHandle == IntPtr.Zero)
                System.Diagnostics.Debug.WriteLine($"[Hotkey] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            ready.Set();
            if (_winHookHandle == IntPtr.Zero) return;

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            UnhookWindowsHookEx(_winHookHandle);
            _winHookHandle = IntPtr.Zero;
            _winHookProc   = null;
        }) { IsBackground = true, Name = "GlobalHotkey-Win" };
        _winThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void WinUninstall()
    {
        if (_winThread is null) return;
        if (_winThreadId != 0) PostThreadMessage(_winThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!_winThread.Join(TimeSpan.FromSeconds(2)))
            System.Diagnostics.Debug.WriteLine("[Hotkey] the hook thread did not stop in time");
        _winThread   = null;
        _winThreadId = 0;
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
