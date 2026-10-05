using System.Runtime.InteropServices;

namespace BladeCtl.Tray;

/// <summary>
/// Is the NVIDIA dGPU powered up right now? Read from the PnP device's power data (CfgMgr32), which never
/// wakes the GPU. Anything that talks to the NVIDIA driver (nvidia-smi, NVML) can pull an Optimus dGPU out of
/// D3cold, so BladeCtl only uses nvidia-smi when this says the GPU is already in D0. (2.6.0)
/// </summary>
internal static class GpuPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100, CM_GETIDLIST_FILTER_CLASS = 0x200;
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static DEVPROPKEY PowerData = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 32 };

    private static string? _nvidiaId;
    private static DateTime _lookedUp = DateTime.MinValue;

    /// <summary>D-state of the NVIDIA GPU: 1 = D0 (awake) ... 4 = D3 (off); null = no NVIDIA GPU or unreadable.</summary>
    public static int? NvidiaDState()
    {
        try
        {
            if (!EnsureId()) return null;
            if (CM_Locate_DevNodeW(out var dev, _nvidiaId!, 0) != 0) { _nvidiaId = null; return null; }
            var data = new byte[64]; uint size = (uint)data.Length;
            if (CM_Get_DevNode_PropertyW(dev, ref PowerData, out _, data, ref size, 0) != 0 || size < 8) return null;
            return BitConverter.ToInt32(data, 4);   // CM_POWER_DATA.PD_MostRecentPowerState
        }
        catch { return null; }
    }

    /// <summary>Find the NVIDIA display devnode (looked up at most every 5 minutes while missing).</summary>
    private static bool EnsureId()
    {
        if (_nvidiaId == null && DateTime.Now - _lookedUp > TimeSpan.FromMinutes(5))
        {
            _lookedUp = DateTime.Now;
            if (CM_Get_Device_ID_List_SizeW(out var len, DisplayClass, CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT) != 0) return false;
            var buf = new char[len + 2];
            if (CM_Get_Device_ID_ListW(DisplayClass, buf, len, CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT) != 0) return false;
            _nvidiaId = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(i => i.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase));
        }
        return _nvidiaId != null;
    }

    /// <summary>True only when the dGPU is known to be awake. Unknown counts as asleep: never risk waking it.</summary>
    public static bool NvidiaAwake => NvidiaDState() == 1;

    // ---------- 2.7.0 Power card: more wake-free CfgMgr reads of the same devnode ----------

    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    private const uint DN_STARTED = 0x8, DN_HAS_PROBLEM = 0x400;
    private static DEVPROPKEY GpuLuidKey = new() { fmtid = new Guid("60b193cb-5276-4d0f-96fc-f173abad3ec6"), pid = 2 };

    /// <summary>Present / started / problem code of the NVIDIA devnode (GPU_MISSING), wake-free. Null when no NVIDIA GPU is listed.</summary>
    public static (bool Started, bool Problem, uint ProblemCode)? NvidiaStatus()
    {
        try
        {
            if (!EnsureId() || CM_Locate_DevNodeW(out var dev, _nvidiaId!, 0) != 0) return null;
            if (CM_Get_DevNode_Status(out var st, out var prob, dev, 0) != 0) return null;
            return ((st & DN_STARTED) != 0, (st & DN_HAS_PROBLEM) != 0, prob);
        }
        catch { return null; }
    }

    /// <summary>The dGPU's adapter LUID from DEVPKEY_Gpu_Luid (UInt64), wake-free. Used to pick its PDH instances and display paths.</summary>
    public static (uint Low, int High)? NvidiaLuid()
    {
        try
        {
            if (!EnsureId() || CM_Locate_DevNodeW(out var dev, _nvidiaId!, 0) != 0) return null;
            var data = new byte[16]; uint size = (uint)data.Length;
            if (CM_Get_DevNode_PropertyW(dev, ref GpuLuidKey, out _, data, ref size, 0) != 0 || size < 8) return null;
            ulong v = BitConverter.ToUInt64(data, 0);
            return ((uint)(v & 0xFFFFFFFF), (int)(v >> 32));
        }
        catch { return null; }
    }
}
