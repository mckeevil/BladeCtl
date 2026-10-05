using System.Runtime.InteropServices;

namespace BladeCtl.Tray.Power;

/// <summary>Small read-only Windows queries for the Power card: battery saver and a service's run state (never sc.exe here).</summary>
internal static class SysPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    /// <summary>Battery saver on (SystemStatusFlag bit 0).</summary>
    public static bool BatterySaver() => GetSystemPowerStatus(out var s) && (s.SystemStatusFlag & 1) != 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr svc, int level, byte[] buf, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr h);
    private const uint SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_STATUS = 0x4;

    /// <summary>-1 unknown / not installed, 0 stopped, 1 running, 2 any other state (starting, stopping, paused).</summary>
    public static int ServiceState(string name) => ServiceState(name, out _);

    /// <summary>As above, plus the service's process id (SERVICE_STATUS_PROCESS.dwProcessId, offset 28; 0 when not running).</summary>
    public static int ServiceState(string name, out int pid)
    {
        pid = 0;
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return -1;
        try
        {
            IntPtr svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return -1;
            try
            {
                var buf = new byte[36];   // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, 0, buf, buf.Length, out _)) return -1;
                int state = BitConverter.ToInt32(buf, 4);
                pid = BitConverter.ToInt32(buf, 28);
                return state == 4 ? 1 : state == 1 ? 0 : 2;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
