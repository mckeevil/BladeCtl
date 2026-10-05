using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BladeCtl.Tray;

/// <summary>
/// Lights off while the session is locked, and after a period without keyboard or mouse input;
/// lights back the moment the session unlocks or input returns. Synapse offers this per device;
/// here it covers the Blade and every accessory in one go.
/// </summary>
public sealed class LockWatcher : IDisposable
{
    private readonly Settings _settings;
    private readonly Func<string, Task> _off;
    private readonly Func<string, Task> _restore;
    private readonly System.Threading.Timer _timer;
    private bool _offForLock, _offForIdle;

    public bool LightsAreOff => _offForLock || _offForIdle;

    public LockWatcher(Settings settings, Func<string, Task> off, Func<string, Task> restore)
    {
        _settings = settings; _off = off; _restore = restore;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer = new System.Threading.Timer(_ => Tick(), null, 15_000, 15_000);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                if (_settings.LightsOffWhenLocked && !_offForLock)
                {
                    _offForLock = true;
                    _ = _off("session locked");
                }
                break;
            case SessionSwitchReason.SessionUnlock:
                if (_offForLock || _offForIdle)
                {
                    _offForLock = false; _offForIdle = false;
                    _ = _restore("session unlocked");
                }
                break;
        }
    }

    private void Tick()
    {
        try
        {
            int minutes = _settings.IdleOffMinutes;
            double idle = IdleSeconds();
            if (_offForIdle)
            {
                if (idle < 20)
                {
                    _offForIdle = false;
                    if (!_offForLock) _ = _restore("input after idle");
                    _timer.Change(15_000, 15_000);
                }
            }
            else if (minutes > 0 && !_offForLock && idle >= minutes * 60)
            {
                _offForIdle = true;
                _ = _off($"idle {minutes} min");
                _timer.Change(2_000, 2_000);   // watch closely so the lights return quickly
            }
        }
        catch (Exception ex) { Log.Warn("lock watcher: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return 0;
        return (Environment.TickCount64 - li.dwTime) / 1000.0;
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        try { _timer.Dispose(); } catch { }
    }
}
