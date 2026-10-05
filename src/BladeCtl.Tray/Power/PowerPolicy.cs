using System.Runtime.InteropServices;

namespace BladeCtl.Tray.Power;

/// <summary>
/// Windows' own CPU caps, read-only (spec 4.4): the active scheme's maximum processor state and boost mode for AC and
/// DC, and the configured power-mode overlay labels. Never the DTT/ESIF WMI primitives (undocumented and able to SET).
/// </summary>
internal static class PowerPolicy
{
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

    public sealed record Caps(bool Ok, int AcMaxPct, int DcMaxPct, int AcBoost, int DcBoost, string AcMode, string DcMode, Guid AcModeGuid, Guid DcModeGuid);

    public static Caps Read()
    {
        int acMax = 100, dcMax = 100, acB = -1, dcB = -1; bool ok = false;
        Guid acG = Guid.Empty, dcG = Guid.Empty; string acM = "?", dcM = "?";
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
            {
                var scheme = Marshal.PtrToStructure<Guid>(p);
                LocalFree(p);
                var sub = SubProcessor; var ms = MaxState; var bm = BoostMode;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref ms, out var v1) == 0) { acMax = (int)v1; ok = true; }
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref ms, out var v2) == 0) dcMax = (int)v2;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref bm, out var v3) == 0) acB = (int)v3;
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref bm, out var v4) == 0) dcB = (int)v4;
            }
        }
        catch (Exception ex) { Log.Trace("power policy read failed: " + ex.Message); }
        try { if (PowerGetUserConfiguredACPowerMode(out acG) == 0) acM = ModeName(acG); } catch (EntryPointNotFoundException) { }
        try { if (PowerGetUserConfiguredDCPowerMode(out dcG) == 0) dcM = ModeName(dcG); } catch (EntryPointNotFoundException) { }
        return new Caps(ok, acMax, dcMax, acB, dcB, acM, dcM, acG, dcG);
    }

    /// <summary>961cc777... = "Best power efficiency", ded574b5... = "Best performance", all zeros = Balanced, anything else "custom".</summary>
    public static string ModeName(Guid g)
    {
        string s = g.ToString();
        if (s.StartsWith("961cc777", StringComparison.OrdinalIgnoreCase)) return "Best power efficiency";
        if (s.StartsWith("ded574b5", StringComparison.OrdinalIgnoreCase)) return "Best performance";
        if (g == Guid.Empty) return "Balanced";
        return "custom";
    }
}
