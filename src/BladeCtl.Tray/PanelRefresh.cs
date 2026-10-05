using System.Runtime.InteropServices;

namespace BladeCtl.Tray;

/// <summary>
/// Refresh rate of the BUILT-IN panel only (found via QueryDisplayConfig output technology, so an external
/// monitor is never touched). The Blade's TMX1560 panel advertises 60 and 360 Hz. Used by the battery profile:
/// 60 Hz on battery, the previous rate (normally 360) back on AC. (2.6.0)
/// </summary>
internal static class PanelRefresh
{
    // ---- QueryDisplayConfig ----
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct RATIONAL { public uint Num, Den; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_TARGET
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct MODE_INFO { public uint infoType, id; public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME { public uint type, size; public LUID adapterId; public uint id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME packet);

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint OUT_INTERNAL = 0x80000000, OUT_DP_EMBEDDED = 11, OUT_UDI_EMBEDDED = 13;

    // ---- display modes ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY; public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    private const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 1;

    /// <summary>GDI name (e.g. \\.\DISPLAY1) of the active built-in panel, or null when the lid is shut / it is not in use.</summary>
    public static string? FindInternal()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return null;
            var paths = new PATH_INFO[np]; var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
            for (int i = 0; i < np; i++)
            {
                uint tech = paths[i].target.outputTechnology;
                if (tech != OUT_INTERNAL && tech != OUT_DP_EMBEDDED && tech != OUT_UDI_EMBEDDED) continue;
                var sn = new SOURCE_NAME { type = 1 /* GET_SOURCE_NAME */, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = paths[i].source.adapterId, id = paths[i].source.id };
                if (DisplayConfigGetDeviceInfo(ref sn) == 0 && !string.IsNullOrEmpty(sn.gdiName)) return sn.gdiName;
            }
        }
        catch (Exception ex) { Log.Warn("panel refresh: display query failed: " + ex.Message); }
        return null;
    }

    private static DEVMODE New() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static int? Current(string device)
    {
        var dm = New();
        return EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : null;
    }

    /// <summary>Refresh rates the panel offers at its current resolution and colour depth.</summary>
    public static SortedSet<int> Rates(string device)
    {
        var set = new SortedSet<int>();
        var cur = New();
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref cur)) return set;
        for (int i = 0; ; i++)
        {
            var dm = New();
            if (!EnumDisplaySettingsW(device, i, ref dm)) break;
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmBitsPerPel == cur.dmBitsPerPel && dm.dmDisplayFrequency > 1)
                set.Add(dm.dmDisplayFrequency);
        }
        return set;
    }

    /// <summary>Switch the refresh rate (resolution unchanged). Returns null on success, else the reason.</summary>
    public static string? Set(string device, int hz)
    {
        var dm = New();
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm)) return "could not read the current mode";
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        return r == 0 ? null : $"ChangeDisplaySettingsEx returned {r}";
    }

    /// <summary>2.7.0: is any active display path driven by the adapter with this LUID (a monitor on the dGPU)? Null when the query fails.</summary>
    public static bool? AnyTargetOnAdapter(uint lowPart, int highPart)
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return null;
            var paths = new PATH_INFO[np]; var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
            for (int i = 0; i < np; i++)
                if (paths[i].target.adapterId.Low == lowPart && paths[i].target.adapterId.High == highPart) return true;
            return false;
        }
        catch { return null; }
    }

    public static string Describe()
    {
        var d = FindInternal();
        if (d == null) return "built-in panel not active";
        return $"{d} at {Current(d)?.ToString() ?? "?"} Hz (offers {string.Join("/", Rates(d))})";
    }
}
