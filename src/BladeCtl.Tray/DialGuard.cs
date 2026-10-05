using System.Runtime.InteropServices;
using System.Text;

namespace BladeCtl.Tray;

/// <summary>
/// In driver mode the Huntsman V2 Analog reports its volume dial as mouse-wheel ticks (and the dial press
/// as a middle click) on its own mouse interface; Synapse's engine used to translate those. This does the
/// same while the analog engine runs: Raw Input says which device a wheel tick came from, a low-level mouse
/// hook swallows the scroll when it is the keyboard's, and a volume key goes out instead.
/// </summary>
public sealed class DialGuard : IDisposable
{
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd, _hook;

    // The window class is registered ONCE per process and its callbacks live in static fields, so a
    // second engine start in the same process never hands Windows a collected delegate. (2.4.0 did exactly
    // that: the second start crashed the whole app with the keyboard left in driver mode - 2026-09-06 14:12.)
    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static readonly HookProc s_hookProc = StaticHook;
    private static bool s_classRegistered;
    private static DialGuard? s_current;

    private readonly Dictionary<IntPtr, bool> _isHuntsman = new();
    private readonly object _gate = new();
    private readonly Queue<(int Sign, long Tick)> _pendingWheel = new();
    private readonly Queue<(bool Down, long Tick)> _pendingMiddle = new();
    private int _logged;

    public long Translated { get; private set; }
    public string Status { get; private set; } = "";

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "dial-guard" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            s_current = this;
            var hInst = GetModuleHandleW(null);
            if (!s_classRegistered)
            {
                var cls = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = s_wndProc, hInstance = hInst, lpszClassName = "BladeCtlDialGuard" };
                s_classRegistered = RegisterClassExW(ref cls) != 0;
                if (!s_classRegistered) { Status = "dial guard: class registration failed " + Marshal.GetLastWin32Error(); Log.Warn(Status); return; }
            }
            _hwnd = CreateWindowExW(0, "BladeCtlDialGuard", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { Status = "dial guard: window failed " + Marshal.GetLastWin32Error(); Log.Warn(Status); return; }
            var rid = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100, hwndTarget = _hwnd } };
            if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) { Status = "dial guard: raw input failed " + Marshal.GetLastWin32Error(); Log.Warn(Status); }
            _hook = SetWindowsHookExW(14, s_hookProc, hInst, 0);
            if (_hook == IntPtr.Zero) { Status = "dial guard: mouse hook failed " + Marshal.GetLastWin32Error(); Log.Warn(Status); }
            else { Status = "dial guard active (dial -> volume)"; Log.Write("analog engine: " + Status); }
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        }
        catch (Exception ex) { Status = "dial guard failed: " + ex.Message; Log.Error("dial guard: " + ex); }
        finally
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            if (s_current == this) s_current = null;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var me = s_current;
        return me != null ? me.WndProc(hWnd, msg, wParam, lParam) : DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static IntPtr StaticHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var me = s_current;
        return me != null ? me.Hook(nCode, wParam, lParam) : CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x00FF)
        {
            try
            {
                uint hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
                uint size = 0;
                GetRawInputData(lParam, 0x10000003, IntPtr.Zero, ref size, hdr);
                if (size > 0)
                {
                    var buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(lParam, 0x10000003, buf, ref size, hdr) == size)
                        {
                            var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                            if (h.dwType == 0 && h.hDevice != IntPtr.Zero && FromHuntsman(h.hDevice))
                            {
                                var m = Marshal.PtrToStructure<RAWMOUSE>(buf + (int)hdr);
                                ushort flags = (ushort)(m.ulButtons & 0xFFFF);
                                short data = (short)(m.ulButtons >> 16);
                                long tick = Environment.TickCount64;
                                lock (_gate)
                                {
                                    if ((flags & 0x0400) != 0 && data != 0) { _pendingWheel.Enqueue((Math.Sign(data), tick)); if (_pendingWheel.Count > 8) _pendingWheel.Dequeue(); }
                                    if ((flags & 0x0010) != 0) { _pendingMiddle.Enqueue((true, tick)); if (_pendingMiddle.Count > 8) _pendingMiddle.Dequeue(); }
                                    if ((flags & 0x0020) != 0) { _pendingMiddle.Enqueue((false, tick)); if (_pendingMiddle.Count > 8) _pendingMiddle.Dequeue(); }
                                }
                                if (_logged < 6) { _logged++; Log.Write($"dial guard: raw wheel from keyboard flags 0x{flags:X4} data {data} at {tick}"); }
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch (Exception ex) { if (_logged < 6) { _logged++; Log.Warn("dial guard raw input: " + ex.Message); } }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private bool FromHuntsman(IntPtr hDevice)
    {
        if (_isHuntsman.TryGetValue(hDevice, out var b)) return b;
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, 0x20000007, IntPtr.Zero, ref size);
        string name = "";
        if (size > 0)
        {
            var buf = Marshal.AllocHGlobal((int)(size * 2 + 2));
            try { GetRawInputDeviceInfoW(hDevice, 0x20000007, buf, ref size); name = Marshal.PtrToStringUni(buf) ?? ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }
        b = name.Contains("VID_1532&PID_0266", StringComparison.OrdinalIgnoreCase);
        _isHuntsman[hDevice] = b;
        return b;
    }

    private IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { if (nCode >= 0) return HookCore(nCode, wParam, lParam); }
        catch (Exception ex) { if (_logged < 6) { _logged++; Log.Warn("dial guard hook: " + ex.Message); } }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr HookCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        {
            uint msg = (uint)wParam;
            if (msg == 0x020A)   // WM_MOUSEWHEEL
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((info.flags & 1) == 0)   // not injected
                {
                    int delta = (short)(info.mouseData >> 16);
                    if (Take(_pendingWheel, e => e.Sign == Math.Sign(delta), e => e.Tick))
                    {
                        SendVolume(delta > 0 ? (ushort)0xAF : (ushort)0xAE);
                        Translated++;
                        if (_logged < 6) { _logged++; Log.Write($"dial guard: wheel {delta} swallowed -> volume {(delta > 0 ? "up" : "down")}"); }
                        return (IntPtr)1;
                    }
                }
            }
            else if (msg == 0x0207 || msg == 0x0208)   // WM_MBUTTONDOWN / UP
            {
                bool down = msg == 0x0207;
                if (Take(_pendingMiddle, e => e.Down == down, e => e.Tick))
                {
                    if (down) { SendVolume(0xAD); Translated++; }
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool Take<T>(Queue<T> q, Func<T, bool> match, Func<T, long> tick)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            while (q.Count > 0 && now - tick(q.Peek()) > 150) q.Dequeue();
            if (q.Count > 0 && match(q.Peek())) { q.Dequeue(); return true; }
            return false;
        }
    }

    private static void SendVolume(ushort vk)
    {
        var down = new INPUT { type = 1 }; down.u.ki.wVk = vk; down.u.ki.dwExtraInfo = (IntPtr)0xB1ADE;
        var up = new INPUT { type = 1 }; up.u.ki.wVk = vk; up.u.ki.dwFlags = 2; up.u.ki.dwExtraInfo = (IntPtr)0xB1ADE;
        SendInput(1, ref down, Marshal.SizeOf<INPUT>());
        SendInput(1, ref up, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        var t = _thread; _thread = null;
        if (t == null) return;
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT
        try { t.Join(1500); } catch { }
        Status = "";
    }

    // ---------- native ----------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX { public uint cbSize, style; public WndProcDelegate lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWMOUSE { public ushort usFlags; public ushort pad; public uint ulButtons; public uint ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, ref INPUT input, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
