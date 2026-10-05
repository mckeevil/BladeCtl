using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BladeCtl.Tray.Power;

/// <summary>
/// Performance counters through pdh.dll (spec 4.3), English counter paths so a non-English Windows works too.
/// Q_cpu: Intel RAPL package energy (raw picowatt-hours, so the watts are exact for any tick spacing) and the processor
/// counters. Q_gpu: dGPU engine busy and the count of processes holding dGPU memory, opened only once the dGPU has been
/// seen in D0 and collected only while it is in D0 (GPU Glance measured these as wake-free on the Intel side).
/// </summary>
internal static class PdhNative
{
    public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct FmtValue { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RawCounter { public uint CStatus; public uint TsLow; public uint TsHigh; public long FirstValue; public long SecondValue; public uint MultiCount; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] public static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll")] public static extern int PdhGetRawCounterValue(IntPtr counter, out uint type, out RawCounter value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] public static extern int PdhCloseQuery(IntPtr query);

    public static double? Fmt(IntPtr c, uint fmt = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100)
    {
        if (c == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(c, fmt, out _, out var v) == 0 && (v.CStatus == 0 || v.CStatus == 1) ? v.Double : null;
    }

    public static long? Raw(IntPtr c)
    {
        if (c == IntPtr.Zero) return null;
        return PdhGetRawCounterValue(c, out _, out var r) == 0 && (r.CStatus == 0 || r.CStatus == 1) ? r.FirstValue : null;
    }

    /// <summary>All instances of a wildcard counter as (instance name, value).</summary>
    public static List<(string Name, double Value)> Array(IntPtr c)
    {
        var list = new List<(string, double)>();
        if (c == IntPtr.Zero) return list;
        uint size = 0;
        int r = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint n, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out n, buf) != 0) return list;
            int item = IntPtr.Size + 16;
            for (int i = 0; i < n; i++)
            {
                IntPtr p = buf + i * item;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "";
                uint st = (uint)Marshal.ReadInt32(p + IntPtr.Size);
                if (st != 0 && st != 1) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p + IntPtr.Size + 8));
                list.Add((name, v));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}

internal sealed class CpuCounters : IDisposable
{
    private IntPtr _q, _energy, _power, _pp0, _pp1, _actual, _utility, _perf, _limit, _flags, _nominal, _time;
    private bool _opened, _failed;
    private long? _e0, _p00, _p10; private long _t0;
    public double OpenMs { get; private set; }

    public sealed record Read(bool Ok, double PkgW, double Pp0W, double Pp1W, double FreqMHz, double UtilityPct, double PerfPct,
                              double PerfLimitPct, double PerfLimitFlags, double NominalMHz, double AllCorePct, double CollectMs, double PowerCounterW = -1);

    private void Open()
    {
        if (_opened || _failed) return;
        var sw = Stopwatch.StartNew();
        if (PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) { _failed = true; return; }
        IntPtr Add(string p) => PdhNative.PdhAddEnglishCounterW(_q, p, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;
        _energy = Add(@"\Energy Meter(RAPL_Package0_PKG)\Energy");
        _power = Add(@"\Energy Meter(RAPL_Package0_PKG)\Power");
        _pp0 = Add(@"\Energy Meter(RAPL_Package0_PP0)\Energy");
        _pp1 = Add(@"\Energy Meter(RAPL_Package0_PP1)\Energy");
        _actual = Add(@"\Processor Information(_Total)\Actual Frequency");
        _utility = Add(@"\Processor Information(_Total)\% Processor Utility");
        _perf = Add(@"\Processor Information(_Total)\% Processor Performance");
        _limit = Add(@"\Processor Information(_Total)\% Performance Limit");
        _flags = Add(@"\Processor Information(_Total)\Performance Limit Flags");
        _nominal = Add(@"\Processor Information(_Total)\Processor Frequency");
        _time = Add(@"\Processor Information(_Total)\% Processor Time");
        _opened = true;
        OpenMs = sw.Elapsed.TotalMilliseconds;
        PdhNative.PdhCollectQueryData(_q);   // prime the rate counters
        _t0 = Stopwatch.GetTimestamp();
        _e0 = PdhNative.Raw(_energy); _p00 = PdhNative.Raw(_pp0); _p10 = PdhNative.Raw(_pp1);
    }

    /// <summary>Collect once; values are averages since the previous collect (the energy delta makes watts exact for any spacing).</summary>
    public Read Collect()
    {
        Open();
        if (_failed || !_opened) return new Read(false, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0);
        var sw = Stopwatch.StartNew();
        if (PdhNative.PdhCollectQueryData(_q) != 0) return new Read(false, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, sw.Elapsed.TotalMilliseconds);
        long t1 = Stopwatch.GetTimestamp();
        double dt = (t1 - _t0) / (double)Stopwatch.Frequency;
        double W(ref long? prev, IntPtr c)
        {
            long? e = PdhNative.Raw(c);
            double w = -1;
            if (e is long e1 && prev is long e0 && dt > 0.2 && e1 >= e0) w = (e1 - e0) * 3.6e-9 / dt;   // pWh -> J -> W
            prev = e;
            return w;
        }
        double pkg = W(ref _e0, _energy), pp0 = W(ref _p00, _pp0), pp1 = W(ref _p10, _pp1);
        _t0 = t1;
        double nominal = PdhNative.Fmt(_nominal) ?? -1;
        double actual = PdhNative.Fmt(_actual) ?? -1;
        double perf = PdhNative.Fmt(_perf) ?? -1;
        if (actual <= 0 && nominal > 0 && perf > 0) actual = nominal * perf / 100.0;   // older builds: no Actual Frequency
        return new Read(pkg >= 0 || actual > 0, pkg, pp0, pp1, actual, PdhNative.Fmt(_utility) ?? -1, perf,
                        PdhNative.Fmt(_limit) ?? -1, PdhNative.Fmt(_flags) ?? -1, nominal, PdhNative.Fmt(_time) ?? -1, sw.Elapsed.TotalMilliseconds,
                        PdhNative.Fmt(_power) is double mw ? mw / 1000.0 : -1);
    }

    public void Dispose() { if (_q != IntPtr.Zero) PdhNative.PdhCloseQuery(_q); _q = IntPtr.Zero; _opened = false; }
}

internal sealed class GpuCounters : IDisposable
{
    private IntPtr _q, _engine, _mem;
    private bool _opened, _failed;
    public double OpenMs { get; private set; }
    public bool Opened => _opened;

    public sealed record Read(bool Ok, double Busy, int MemHolders, double CollectMs);

    /// <summary>Opened only the first time the dGPU is seen in D0 (costs about 0.5 s once).</summary>
    public void Open()
    {
        if (_opened || _failed) return;
        var sw = Stopwatch.StartNew();
        if (PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) { _failed = true; return; }
        PdhNative.PdhAddEnglishCounterW(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine);
        PdhNative.PdhAddEnglishCounterW(_q, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _mem);
        PdhNative.PdhCollectQueryData(_q);
        _opened = true;
        OpenMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// dGPU busy = max over engine types of the summed utilisation of that type (instances containing the dGPU's LUID),
    /// and the number of processes holding more than 1 MB of dGPU memory, not counting <paramref name="excludePids"/>
    /// (NVIDIA's display service and BladeCtl itself: they are not "an app using the GPU"). Counts only: pids, no names.
    /// </summary>
    public Read Collect(string luidTag, bool wantHolders, IReadOnlyCollection<int>? excludePids = null)
    {
        if (!_opened || luidTag.Length == 0) return new Read(false, 0, -1, 0);
        var sw = Stopwatch.StartNew();
        if (PdhNative.PdhCollectQueryData(_q) != 0) return new Read(false, 0, -1, sw.Elapsed.TotalMilliseconds);
        var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, v) in PdhNative.Array(_engine))
        {
            if (name.IndexOf(luidTag, StringComparison.OrdinalIgnoreCase) < 0) continue;
            int i = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            string type = i >= 0 ? name[(i + 8)..] : "?";
            byType[type] = (byType.TryGetValue(type, out var s) ? s : 0) + v;
        }
        double busy = byType.Count == 0 ? 0 : Math.Min(100, byType.Values.Max());
        int holders = -1;
        if (wantHolders)
        {
            var pids = new HashSet<string>();
            foreach (var (name, v) in PdhNative.Array(_mem))
            {
                if (v <= 1024 * 1024 || name.IndexOf(luidTag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int u = name.IndexOf("_luid", StringComparison.OrdinalIgnoreCase);
                string key = u > 0 ? name[..u] : name;   // "pid_1234"
                if (excludePids != null && key.StartsWith("pid_", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(key.AsSpan(4), out var pid) && excludePids.Contains(pid)) continue;
                pids.Add(key);
            }
            holders = pids.Count;
        }
        return new Read(true, busy, holders, sw.Elapsed.TotalMilliseconds);
    }

    public void Dispose() { if (_q != IntPtr.Zero) PdhNative.PdhCloseQuery(_q); _q = IntPtr.Zero; _opened = false; }
}
