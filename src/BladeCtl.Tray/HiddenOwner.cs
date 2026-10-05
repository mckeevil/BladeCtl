using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BladeCtl.Tray;

/// <summary>
/// An invisible top-level window that hosts the global RESTORE AUTO hotkey (Ctrl+Alt+F).
/// WinForms NativeWindow: works under any Win32 message pump, including WPF's Dispatcher.
/// </summary>
public sealed class HiddenOwner : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB1AD;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const uint VK_F = 0x46;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private readonly Action _onHotkey;
    private bool _hotkeyRegistered;

    /// <summary>A device was plugged or unplugged (raised on the UI thread, bursty).</summary>
    public event Action? DeviceChanged;

    public HiddenOwner(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams { Caption = "BladeCtlHiddenOwner", X = 0, Y = 0, Width = 0, Height = 0, Style = 0 });
    }

    public bool HotkeyRegistered => _hotkeyRegistered;

    /// <summary>Ctrl+Alt+F -> RESTORE AUTO FAN, reachable even when the tray icon is hidden.</summary>
    public bool RegisterRestoreHotkey()
    {
        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F);
        if (!_hotkeyRegistered)
            Log.Warn($"could not register Ctrl+Alt+F hotkey (win32 {Marshal.GetLastWin32Error()}) - another app probably owns it");
        return _hotkeyRegistered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Log.Write("Ctrl+Alt+F pressed -> RESTORE AUTO FAN");
            try { _onHotkey(); } catch (Exception ex) { Log.Error("hotkey handler: " + ex.Message); }
            return;
        }
        if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED)
        {
            try { DeviceChanged?.Invoke(); } catch { }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        try { if (_hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId); } catch { }
        try { DestroyHandle(); } catch { }
    }
}
