using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using BladeCtl.Core.Power;

namespace BladeCtl.Tray.Power;

/// <summary>
/// How many cores the app in front is using (spec 4.7), and whether it is on the optional private-apps list
/// (<see cref="PrivateApps"/>). The process is opened only to read its CPU time and to check its image against that list.
/// No process name or path is shown, logged, dumped or stored: the per-path result lives in memory for this session only.
/// </summary>
internal sealed class ForegroundCpu : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle h, uint flags, StringBuilder buf, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int size);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public sealed record Read(bool Known, int Pid, double Cores, bool Private, bool Self, bool Shell);

    public static string ListPath => Path.Combine(Log.AppDataDir, "private-apps.txt");

    private uint _pid;
    private SafeProcessHandle? _h;
    private bool _private, _shell, _known;
    private readonly List<(double T, long Cpu)> _hist = new();
    private readonly Dictionary<string, bool> _privCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint _self = (uint)Environment.ProcessId;
    private PrivateApps _list = PrivateApps.None;
    private DateTime _listStamp = DateTime.MinValue;

    /// <param name="windowSec">averaging window: 6 s while live, ~35 s for closed samples (two reads 30 s apart)</param>
    public Read Sample(double now, double windowSec)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) { Reset(0); return new Read(true, 0, 0, false, false, true); }
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) { Reset(0); return new Read(true, 0, 0, false, false, true); }
        if (pid != _pid) Reset(pid);
        if (!_known || _h == null) return new Read(false, (int)pid, 0, false, pid == _self, false);
        if (!GetProcessTimes(_h, out _, out _, out long k, out long u)) { Reset(0); return new Read(false, (int)pid, 0, false, false, false); }
        _hist.Add((now, k + u));
        while (_hist.Count > 2 && now - _hist[1].T >= windowSec) _hist.RemoveAt(0);
        double cores = 0;
        if (_hist.Count >= 2)
        {
            var a = _hist[0]; var b = _hist[^1];
            double dt = b.T - a.T;
            if (dt > 0.2) cores = Math.Max(0, (b.Cpu - a.Cpu) / 1e7 / dt);
        }
        return new Read(true, (int)pid, cores, _private, pid == _self, _shell);
    }

    private void Reset(uint pid)
    {
        try { _h?.Dispose(); } catch { }
        _h = null; _hist.Clear(); _pid = pid; _private = false; _shell = pid == 0; _known = false;
        if (pid == 0) return;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) { h.Dispose(); return; }
        _h = h; _known = true;
        var sb = new StringBuilder(32768); int size = sb.Capacity;   // long paths too (up to 32K chars)
        string path = QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
        string baseName = Path.GetFileNameWithoutExtension(path);
        _shell = baseName.Equals("explorer", StringComparison.OrdinalIgnoreCase);   // basename only, never logged
        RefreshList();
        // fail closed when a list is in use: an image path that cannot be read cannot be checked, so the app counts as private
        _private = (path.Length == 0 && (_list.Count > 0 || _list.FailedClosed)) || (path.Length > 0 && IsPrivate(path, baseName));
    }

    /// <summary>Re-reads the list when the file appears, changes or goes away (checked on each foreground switch).</summary>
    private void RefreshList()
    {
        DateTime stamp;
        try { stamp = File.Exists(ListPath) ? File.GetLastWriteTimeUtc(ListPath) : DateTime.MinValue; } catch { stamp = DateTime.MaxValue; }
        if (stamp == _listStamp && _listStamp != DateTime.MaxValue) return;
        _listStamp = stamp;
        _list = PrivateApps.Load(ListPath);
        _privCache.Clear();
    }

    /// <summary>Basename without .exe, full path, its long form (8.3 names expanded) and FileDescription.</summary>
    private bool IsPrivate(string path, string baseName)
    {
        if (_list.Count == 0 && !_list.FailedClosed) return false;
        if (_privCache.TryGetValue(path, out var hit)) return hit;
        string desc = "";
        try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { }
        bool p;
        try { p = _list.Is(baseName, path, LongPath(path), desc); }
        catch { p = true; }   // a failed check never exposes anything: treat as private
        _privCache[path] = p;
        return p;
    }

    // 8.3 short names (C:\PROGRA~1\...) -> long form, so name matching sees the real folder names
    private static string LongPath(string p)
    {
        if (string.IsNullOrEmpty(p) || p.IndexOf('~') < 0) return p ?? "";
        try { var sb = new StringBuilder(2048); int n = GetLongPathName(p, sb, sb.Capacity); return n > 0 && n < sb.Capacity ? sb.ToString() : p; } catch { return p; }
    }

    public void Dispose() { try { _h?.Dispose(); } catch { } _h = null; }
}
