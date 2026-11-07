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

    /// <summary>A key or button someone can press to talk.</summary>
    public sealed record AnyKey(string Name, int WinVk, uint X11Keysym, uint MacKeyCode, Avalonia.Input.Key Key = Avalonia.Input.Key.None);

    public const int VkMouseMiddle = 0x04, VkMouseBack = 0x05, VkMouseForward = 0x06;

    /// <summary>
    /// Everything that can be pressed to talk, on a keyboard or a mouse. Mouse buttons are Windows only so far: the
    /// other systems' hot keys are for the keyboard. Escape is not here, because it cancels choosing.
    /// </summary>
    public static readonly IReadOnlyList<AnyKey> AnyKeys = BuildAnyKeys();

    private static List<AnyKey> BuildAnyKeys()
    {
        // kVK_ANSI_A to Z, and 0 to 9, which a Mac does not number in order.
        uint[] macLetters = [0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06];
        uint[] macDigits  = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];
        uint[] macKeypad  = [0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C];
        uint[] macF       = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A];

        var keys = new List<AnyKey>();
        for (var i = 0; i < 26; i++)
            keys.Add(new(((char)('A' + i)).ToString(), 0x41 + i, (uint)(0x61 + i), macLetters[i], Avalonia.Input.Key.A + i));
        for (var i = 0; i < 10; i++)
            keys.Add(new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0x30 + i, (uint)(0x30 + i), macDigits[i], Avalonia.Input.Key.D0 + i));
        for (var i = 0; i < 10; i++)
            keys.Add(new($"Numpad {i}", 0x60 + i, (uint)(0xFFB0 + i), macKeypad[i], Avalonia.Input.Key.NumPad0 + i));
        for (var i = 0; i < 24; i++)
            keys.Add(new($"F{i + 1}", 0x70 + i, (uint)(0xFFBE + i), i < macF.Length ? macF[i] : NoMacKey, Avalonia.Input.Key.F1 + i));

        keys.AddRange(
        [
            new("Numpad *", 0x6A, 0xFFAA, 0x43, Avalonia.Input.Key.Multiply),
            new("Numpad +", 0x6B, 0xFFAB, 0x45, Avalonia.Input.Key.Add),
            new("Numpad -", 0x6D, 0xFFAD, 0x4E, Avalonia.Input.Key.Subtract),
            new("Numpad .", 0x6E, 0xFFAE, 0x41, Avalonia.Input.Key.Decimal),
            new("Numpad /", 0x6F, 0xFFAF, 0x4B, Avalonia.Input.Key.Divide),
            new("Space",       0x20, 0x0020, 0x31, Avalonia.Input.Key.Space),
            new("Tab",         0x09, 0xFF09, 0x30, Avalonia.Input.Key.Tab),
            new("Enter",       0x0D, 0xFF0D, 0x24, Avalonia.Input.Key.Enter),
            new("Backspace",   0x08, 0xFF08, 0x33, Avalonia.Input.Key.Back),
            new("Caps Lock",   0x14, 0xFFE5, NoMacKey, Avalonia.Input.Key.CapsLock),
            new("Left",        0x25, 0xFF51, 0x7B, Avalonia.Input.Key.Left),
            new("Up",          0x26, 0xFF52, 0x7E, Avalonia.Input.Key.Up),
            new("Right",       0x27, 0xFF53, 0x7C, Avalonia.Input.Key.Right),
            new("Down",        0x28, 0xFF54, 0x7D, Avalonia.Input.Key.Down),
            new("Insert",      0x2D, 0xFF63, 0x72, Avalonia.Input.Key.Insert),
            new("Delete",      0x2E, 0xFFFF, 0x75, Avalonia.Input.Key.Delete),
            new("Home",        0x24, 0xFF50, 0x73, Avalonia.Input.Key.Home),
            new("End",         0x23, 0xFF57, 0x77, Avalonia.Input.Key.End),
            new("Page Up",     0x21, 0xFF55, 0x74, Avalonia.Input.Key.PageUp),
            new("Page Down",   0x22, 0xFF56, 0x79, Avalonia.Input.Key.PageDown),
            new("Pause",       0x13, 0xFF13, NoMacKey, Avalonia.Input.Key.Pause),
            new("Scroll Lock", 0x91, 0xFF14, NoMacKey, Avalonia.Input.Key.Scroll),
            new("Num Lock",    0x90, 0xFF7F, NoMacKey, Avalonia.Input.Key.NumLock),
            new("Left Shift",  0xA0, 0xFFE1, NoMacKey, Avalonia.Input.Key.LeftShift),
            new("Right Shift", 0xA1, 0xFFE2, NoMacKey, Avalonia.Input.Key.RightShift),
            new("Left Ctrl",   0xA2, 0xFFE3, NoMacKey, Avalonia.Input.Key.LeftCtrl),
            new("Right Ctrl",  0xA3, 0xFFE4, NoMacKey, Avalonia.Input.Key.RightCtrl),
            new("Left Alt",    0xA4, 0xFFE9, NoMacKey, Avalonia.Input.Key.LeftAlt),
            new("Right Alt",   0xA5, 0xFFEA, NoMacKey, Avalonia.Input.Key.RightAlt),
            new("Left Win",    0x5B, 0xFFEB, NoMacKey, Avalonia.Input.Key.LWin),
            new("Right Win",   0x5C, 0xFFEC, NoMacKey, Avalonia.Input.Key.RWin),
            new(";",           0xBA, 0x003B, 0x29, Avalonia.Input.Key.OemSemicolon),
            new("=",           0xBB, 0x003D, 0x18, Avalonia.Input.Key.OemPlus),
            new(",",           0xBC, 0x002C, 0x2B, Avalonia.Input.Key.OemComma),
            new("-",           0xBD, 0x002D, 0x1B, Avalonia.Input.Key.OemMinus),
            new(".",           0xBE, 0x002E, 0x2F, Avalonia.Input.Key.OemPeriod),
            new("/",           0xBF, 0x002F, 0x2C, Avalonia.Input.Key.OemQuestion),
            new("`",           0xC0, 0x0060, 0x32, Avalonia.Input.Key.OemTilde),
            new("[",           0xDB, 0x005B, 0x21, Avalonia.Input.Key.OemOpenBrackets),
            new("\\",          0xDC, 0x005C, 0x2A, Avalonia.Input.Key.OemPipe),
            new("]",           0xDD, 0x005D, 0x1E, Avalonia.Input.Key.OemCloseBrackets),
            new("'",           0xDE, 0x0027, 0x27, Avalonia.Input.Key.OemQuotes),
            new("Mouse Middle",  VkMouseMiddle,  0, NoMacKey),
            new("Mouse Back",    VkMouseBack,    0, NoMacKey),
            new("Mouse Forward", VkMouseForward, 0, NoMacKey),
        ]);
        return keys;
    }

    /// <summary>The Windows key code for a key pressed in the app. Zero for one that is not offered, or not here.</summary>
    public static int VkOf(Avalonia.Input.Key key) =>
        key != Avalonia.Input.Key.None && AnyKeys.FirstOrDefault(k => k.Key == key) is { } found && AvailableHere(found.WinVk) ? found.WinVk : 0;

    /// <summary>Whether this machine can take a key as the push-to-talk key: on a Mac, only the
    /// ones its keyboard has; off Windows, not a mouse button.</summary>
    public static bool AvailableHere(int vk)
    {
        if (vk == 0 || OperatingSystem.IsWindows()) return true;
        return OperatingSystem.IsMacOS() ? VkToMacKeyCode(vk) != NoMacKey : VkToX11Keysym(vk) != 0;
    }

    public static uint VkToMacKeyCode(int vk) =>
        AnyKeys.FirstOrDefault(k => k.WinVk == vk)?.MacKeyCode ?? NoMacKey;

    public static string VkName(int vk) =>
        AnyKeys.FirstOrDefault(k => k.WinVk == vk)?.Name ?? KeyOptions.FirstOrDefault(k => k.WinVk == vk).Name ?? $"VK 0x{vk:X2}";

    public static uint VkToX11Keysym(int vk) =>
        AnyKeys.FirstOrDefault(k => k.WinVk == vk)?.X11Keysym ?? 0;

    /// <summary>What a Windows low-level mouse message says about a button that can talk, or null for anything else.</summary>
    public static (int Vk, bool Down)? MouseButton(int message, uint mouseData)
    {
        const int MiddleDown = 0x0207, MiddleUp = 0x0208, SideDown = 0x020B, SideUp = 0x020C;

        // The high word says which side button: 1 is the one browsers go back with.
        var side = (mouseData >> 16) switch { 1 => VkMouseBack, 2 => VkMouseForward, _ => 0 };

        return message switch
        {
            MiddleDown => (VkMouseMiddle, true),
            MiddleUp   => (VkMouseMiddle, false),
            SideDown when side != 0 => (side, true),
            SideUp when side != 0   => (side, false),
            _ => null,
        };
    }

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
    [DllImport(CarbonLib)] private static extern int GetEventParameter(IntPtr theEvent, uint name, uint desiredType,
        IntPtr actualType, nuint size, IntPtr actualSize, out EventHotKeyID value);

    private const uint KEventClassKeyboard  = 0x6B657962;   // 'keyb'
    private const uint KEventHotKeyPressed  = 5;
    private const uint KEventHotKeyReleased = 6;
    private const uint KEventParamDirectObject = 0x2D2D2D2D;   // '----'
    private const uint TypeEventHotKeyId       = 0x686B6964;   // 'hkid'
    private const int  EventNotHandled         = -9874;

    // ⚠️ More than one of these can be alive: the agent's push-to-talk and each Mumble server's keys. Carbon
    // hands every handler every hot key, so each keeps an id of its own and passes on what is not its.
    private static int _nextMacId;
    private readonly uint _macId = (uint)Interlocked.Increment(ref _nextMacId);

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

            // 'EVEC': a signature of our own, and an id for this instance.
            status = RegisterEventHotKey(code, 0, new EventHotKeyID { signature = 0x45564543, id = _macId },
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
        var read = GetEventParameter(theEvent, KEventParamDirectObject, TypeEventHotKeyId, IntPtr.Zero,
                                     (nuint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, out var pressed);
        if (read != 0 || pressed.id != _macId) return EventNotHandled;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int    x;
        public int    y;
        public uint   mouseData;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;
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
            // A mouse hook is called for every movement, so it is only there for someone who talks with a mouse button.
            var mouse = _configuredVk is VkMouseMiddle or VkMouseBack or VkMouseForward;
            _winHookProc   = mouse ? WinMouseCallback : WinHookCallback;
            _winHookHandle = SetWindowsHookEx(mouse ? WH_MOUSE_LL : WH_KEYBOARD_LL, _winHookProc, GetModuleHandle(null), 0);
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

    private IntPtr WinMouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const int MouseMove = 0x0200;

        if (nCode >= 0 && wParam != MouseMove
            && MouseButton((int)wParam, Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).mouseData) is { } button
            && button.Vk == _configuredVk)
        {
            if (button.Down && !_keyDown)
            {
                _keyDown = true;
                OnPress?.Invoke();
            }
            else if (!button.Down)
            {
                _keyDown = false;
                OnRelease?.Invoke();
            }
        }
        return CallNextHookEx(_winHookHandle, nCode, wParam, lParam);
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
