using System.Diagnostics;
using System.Runtime.InteropServices;
using BladeCtl.Core.Power;

namespace BladeCtl.Tray.Power;

/// <summary>
/// NVML, loaded only from System32 and only through the gate (spec 4.5, port of GPU Glance SPEC 5.3). Rule R1: nothing
/// here runs unless a wake-free D-state read says D0, and the D-state is read again right before init and before every
/// batch. States UNLOADED -> LOADED -> READY, BACKOFF (30 s doubling to 5 min) and FAILED (cleared only by the NVIDIA
/// devnode reappearing). Its own lock serialises NVML calls; the D-state re-reads (CfgMgr, up to ~2.7 s while the GPU
/// cycles, F18) run outside it, and a shutdown from another thread (UI, SystemEvents) never waits: when the monitor thread
/// holds the lock (an init or a batch in progress) the request is left for it and handled before that step returns.
/// </summary>
internal sealed class NvmlSession : IDisposable
{
    public const double ReleaseSec = 6;
    public const uint ExpectedPciId = 0x249D10DE;   // RTX 3070 Laptop

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DCount(out uint n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DHandle(uint idx, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DPtr(IntPtr dev, IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DDevStr(IntPtr dev, byte[] buf, uint len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DSysStr(byte[] buf, uint len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DDevIntU(IntPtr dev, int type, out uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DDevUU(IntPtr dev, out uint a, out uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DFields(IntPtr dev, int count, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DDevU64(IntPtr dev, out ulong v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DDevI(IntPtr dev, out int v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DDevU(IntPtr dev, out uint v);
    [StructLayout(LayoutKind.Sequential)] private struct NvUtil { public uint Gpu; public uint Mem; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DUtil(IntPtr dev, out NvUtil u);

    private IntPtr _lib, _dev;
    private D0? _init, _shutdown;
    private DCount? _count; private DHandle? _byIndex; private DPtr? _pci;
    private DDevStr? _uuid; private DSysStr? _driver; private DDevIntU? _thresh, _clock, _temp; private DDevUU? _constraints; private DUtil? _util;
    private DFields? _fields; private DDevU64? _reasons; private DDevI? _pstate, _psrc; private DDevU? _enforced;

    private readonly object _gate = new();
    private volatile NvState _nv = NvState.UNLOADED;   // written under _gate only; read without it by State
    private string? _reqShutdown;                       // pending cross-thread shutdown reason (Volatile / Interlocked)
    private double _backoffUntil, _backoffLen = 30, _lastInitAt = -1e9, _closedSince = -1;
    private int _timeouts, _lastErr;
    private uint _initSeq;
    private readonly bool[] _fieldNa = new bool[Nv.F_COUNT];
    private bool _naReasons, _naClock, _naTemp, _naPstate, _naPsrc;
    private volatile bool _reqArrival;
    private readonly Stopwatch _clock0 = Stopwatch.StartNew();
    private double Now => _clock0.Elapsed.TotalSeconds;

    // statics, once per READY session
    public string Driver { get; private set; } = "";
    public string Uuid { get; private set; } = "";
    public int TslowC { get; private set; } = -1;
    public int TtargetC { get; private set; } = -1;
    public uint LminMw { get; private set; }
    public uint LmaxMw { get; private set; }
    public string FailReason { get; private set; } = "";
    public double InitMs { get; private set; }
    public double LastBatchMs { get; private set; }
    public double MaxCallMs { get; private set; }
    public long Calls { get; private set; }
    /// <summary>Lock-free: the UI and the dump read it without waiting on an NVML call.</summary>
    public NvState State => _nv;
    public int LastError => _lastErr;

    /// <summary>The NVIDIA devnode reappeared (it was gone or not started): clears FAILED at the next step.</summary>
    public void NoteDeviceArrival() => _reqArrival = true;

    /// <summary>
    /// The NVIDIA devnode is gone or stopped (driver update, disabled in Device Manager): shut down and FreeLibrary at once so
    /// nvml.dll is not held open while the driver is replaced, then back off 30 s. Called on the monitor thread.
    /// </summary>
    public void FreeLibraryNow(string why)
    {
        lock (_gate)
        {
            if (_nv == NvState.READY && _shutdown != null) { try { _shutdown(); } catch { } }
            bool had = _lib != IntPtr.Zero;
            Unload(); _nv = NvState.BACKOFF; _backoffUntil = Now + 30; _backoffLen = 60;
            Interlocked.Exchange(ref _reqShutdown, null);
            if (had) Log.Write($"power: NVML unloaded: {why} (FreeLibrary so a driver install is not blocked)");
        }
    }

    private T? Bind<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    private string Load()
    {
        if (_lib != IntPtr.Zero) return "";
        string path = Path.Combine(Environment.SystemDirectory, "nvml.dll");   // never a bare-name load
        if (!File.Exists(path)) return "nvml.dll not found in System32";
        if (!NativeLibrary.TryLoad(path, out _lib)) { _lib = IntPtr.Zero; return "nvml.dll could not be loaded"; }
        _init = Bind<D0>("nvmlInit_v2"); _shutdown = Bind<D0>("nvmlShutdown");
        _count = Bind<DCount>("nvmlDeviceGetCount_v2"); _byIndex = Bind<DHandle>("nvmlDeviceGetHandleByIndex_v2"); _pci = Bind<DPtr>("nvmlDeviceGetPciInfo_v3");
        _fields = Bind<DFields>("nvmlDeviceGetFieldValues"); _util = Bind<DUtil>("nvmlDeviceGetUtilizationRates");
        string? missing = _init == null ? "nvmlInit_v2" : _shutdown == null ? "nvmlShutdown" : _count == null ? "nvmlDeviceGetCount_v2"
                        : _byIndex == null ? "nvmlDeviceGetHandleByIndex_v2" : _pci == null ? "nvmlDeviceGetPciInfo_v3" : _fields == null ? "nvmlDeviceGetFieldValues"
                        : _util == null ? "nvmlDeviceGetUtilizationRates" : null;
        if (missing != null) { Unload(); return $"export {missing} missing"; }
        _uuid = Bind<DDevStr>("nvmlDeviceGetUUID"); _driver = Bind<DSysStr>("nvmlSystemGetDriverVersion");
        _thresh = Bind<DDevIntU>("nvmlDeviceGetTemperatureThreshold"); _constraints = Bind<DDevUU>("nvmlDeviceGetPowerManagementLimitConstraints");
        _reasons = Bind<DDevU64>("nvmlDeviceGetCurrentClocksEventReasons") ?? Bind<DDevU64>("nvmlDeviceGetCurrentClocksThrottleReasons");
        _clock = Bind<DDevIntU>("nvmlDeviceGetClockInfo"); _temp = Bind<DDevIntU>("nvmlDeviceGetTemperature");
        _pstate = Bind<DDevI>("nvmlDeviceGetPerformanceState"); _psrc = Bind<DDevI>("nvmlDeviceGetPowerSource");
        _enforced = Bind<DDevU>("nvmlDeviceGetEnforcedPowerLimit");
        return "";
    }

    private void Unload()
    {
        if (_lib != IntPtr.Zero) { try { NativeLibrary.Free(_lib); } catch { } }
        _lib = IntPtr.Zero; _dev = IntPtr.Zero;
        _init = _shutdown = null; _count = null; _byIndex = null; _pci = null; _uuid = null; _driver = null; _thresh = _clock = _temp = null;
        _constraints = null; _util = null; _fields = null; _reasons = null; _pstate = _psrc = null; _enforced = null;
        Array.Clear(_fieldNa); _naReasons = _naClock = _naTemp = _naPstate = _naPsrc = false;   // a new driver may support more
    }

    private int Timed(string name, Func<int> fn)
    {
        long a = Stopwatch.GetTimestamp();
        int r = fn();
        double ms = (Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency;
        Calls++;
        if (ms > MaxCallMs) MaxCallMs = ms;
        if (ms > 50) Log.Warn($"power: NVML {name} took {ms:F0} ms (ret {r})");
        return r;
    }

    /// <summary>
    /// Shut NVML down (D left D0, suspend, lock, window closed on battery, unplugged, exit). Thread-safe and never blocks:
    /// if the lock is busy the monitor thread performs the shutdown before its current step returns.
    /// </summary>
    public void Shutdown(string why)
    {
        Volatile.Write(ref _reqShutdown, why);
        if (Monitor.TryEnter(_gate))
        {
            try { HandleRequests(); } finally { Monitor.Exit(_gate); }
        }
    }

    /// <summary>Under the lock: act on a pending shutdown request.</summary>
    private void HandleRequests()
    {
        string? why = Interlocked.Exchange(ref _reqShutdown, null);
        if (why != null) ShutdownLocked(why);
    }

    private void ShutdownLocked(string why)
    {
        if (_nv == NvState.READY && _shutdown != null) Timed("nvmlShutdown", () => _shutdown());
        _dev = IntPtr.Zero;
        if (_nv == NvState.READY) { _nv = _lib != IntPtr.Zero ? NvState.LOADED : NvState.UNLOADED; Log.Write($"power: NVML shutdown ({why})"); }
    }

    private void Backoff(int err, string why)
    {
        if (_nv == NvState.READY && _shutdown != null) Timed("nvmlShutdown", () => _shutdown());
        _dev = IntPtr.Zero; _lastErr = err;
        _backoffUntil = Now + _backoffLen;
        Log.Warn($"power: NVML error {err} ({Nv.ErrName(err)}) in {why}: backoff {_backoffLen:F0} s");
        _backoffLen = Math.Min(300, _backoffLen * 2);
        _nv = NvState.BACKOFF;
    }

    /// <summary>false = abandon the rest of this batch.</summary>
    private bool Check(int r, string call)
    {
        if (r == 0) return true;
        if (r == 10) { if (++_timeouts >= 3) { Backoff(r, call); _timeouts = 0; return false; } return true; }
        if (r is 1 or 9 or 15 or 16 or 999) { Backoff(r, call); return false; }
        if (r == 18)
        {
            if (_nv == NvState.READY && _shutdown != null) _shutdown();
            Unload(); _lastErr = r; _nv = NvState.UNLOADED; _lastInitAt = Now;
            Log.Warn("power: NVML driver and nvml.dll versions differ (18): unloaded");
            return false;
        }
        return true;   // 2, 3, 4, 6, 7: this call only
    }

    private bool Init()
    {
        long a = Stopwatch.GetTimestamp();
        _lastInitAt = Now;
        int r = Timed("nvmlInit_v2", () => _init!());
        if (r != 0) { _lastErr = r; if (r == 18) { Check(r, "nvmlInit_v2"); return false; } _nv = NvState.LOADED; Backoff(r, "nvmlInit_v2"); return false; }
        _nv = NvState.READY;   // from here every failure path must shut down
        _dev = IntPtr.Zero;
        uint n = 0;
        if (Timed("nvmlDeviceGetCount_v2", () => _count!(out n)) != 0 || n != 1) return Fail($"device count {n}, expected 1");
        IntPtr d = IntPtr.Zero;
        if (Timed("nvmlDeviceGetHandleByIndex_v2", () => _byIndex!(0, out d)) != 0 || d == IntPtr.Zero) return Fail("no handle for device 0");
        IntPtr pci = Marshal.AllocHGlobal(68);
        uint pciId = 0;
        try
        {
            for (int i = 0; i < 68; i++) Marshal.WriteByte(pci, i, 0);
            if (Timed("nvmlDeviceGetPciInfo_v3", () => _pci!(d, pci)) == 0) pciId = (uint)Marshal.ReadInt32(pci, 28);
        }
        finally { Marshal.FreeHGlobal(pci); }
        if (pciId != ExpectedPciId) return Fail($"PCI device id 0x{pciId:X8}, expected 0x{ExpectedPciId:X8}");
        _dev = d;
        // statics
        var buf = new byte[96];
        if (_driver != null && Timed("nvmlSystemGetDriverVersion", () => _driver(buf, (uint)buf.Length)) == 0) Driver = Str(buf);
        if (_uuid != null && Timed("nvmlDeviceGetUUID", () => _uuid(_dev, buf, (uint)buf.Length)) == 0) Uuid = Str(buf);
        if (_thresh != null)
        {
            uint t1 = 0, t7 = 0;
            if (Timed("nvmlDeviceGetTemperatureThreshold", () => _thresh(_dev, 1, out t1)) == 0) TslowC = (int)t1;
            if (Timed("nvmlDeviceGetTemperatureThreshold", () => _thresh(_dev, 7, out t7)) == 0) TtargetC = (int)t7;
        }
        if (_constraints != null) { uint lo = 0, hi = 0; if (Timed("nvmlDeviceGetPowerManagementLimitConstraints", () => _constraints(_dev, out lo, out hi)) == 0) { LminMw = lo; LmaxMw = hi; } }
        _initSeq++;
        InitMs = (Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency;
        Log.Write($"power: NVML ready in {InitMs:F0} ms, driver {Driver}, slowdown {TslowC} C, target {TtargetC} C, limits {LminMw}-{LmaxMw} mW");
        return true;

        bool Fail(string why)
        {
            if (_shutdown != null) Timed("nvmlShutdown", () => _shutdown());
            _dev = IntPtr.Zero; _nv = NvState.FAILED; FailReason = why;
            Log.Warn("power: NVML FAILED: " + why + " (retried only after a device arrival)");
            return false;
        }
    }

    private static string Str(byte[] b) { int n = Array.IndexOf(b, (byte)0); return System.Text.Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n); }

    private void Batch(PowerSample s)
    {
        long a = Stopwatch.GetTimestamp();
        s.NvRan = true;
        bool any = false;
        var idx = new List<int>();
        for (int i = 0; i < Nv.F_COUNT; i++) if (!_fieldNa[i]) idx.Add(i);
        if (idx.Count > 0)
        {
            IntPtr v = Marshal.AllocHGlobal(40 * idx.Count);
            try
            {
                for (int k = 0; k < idx.Count; k++)
                {
                    for (int b = 0; b < 40; b += 8) Marshal.WriteInt64(v, k * 40 + b, 0);
                    Marshal.WriteInt32(v, k * 40, (int)Nv.FieldIds[idx[k]]);
                }
                int r = Timed("nvmlDeviceGetFieldValues", () => _fields!(_dev, idx.Count, v));
                if (!Check(r, "nvmlDeviceGetFieldValues")) { s.NvErr = r; return; }
                if (r == 0)
                    for (int k = 0; k < idx.Count; k++)
                    {
                        int ret = Marshal.ReadInt32(v, k * 40 + 28);
                        if (ret == 0) { s.FOk[idx[k]] = true; s.FVal[idx[k]] = FieldValue(v + k * 40); any = true; }
                        else if (ret == 3) _fieldNa[idx[k]] = true;   // NOT_SUPPORTED: n/a for the session
                    }
            }
            finally { Marshal.FreeHGlobal(v); }
        }
        if (!s.FOk[Nv.F_LENF] && _enforced != null)
        {
            uint lim = 0;
            if (Timed("nvmlDeviceGetEnforcedPowerLimit", () => _enforced(_dev, out lim)) == 0) { s.FOk[Nv.F_LENF] = true; s.FVal[Nv.F_LENF] = lim; }
        }
        if (!s.FOk[Nv.F_LMAX] && LmaxMw > 0) { s.FOk[Nv.F_LMAX] = true; s.FVal[Nv.F_LMAX] = LmaxMw; }
        if (_reasons != null && !_naReasons)
        {
            ulong reasons = 0;
            int r = Timed("nvmlDeviceGetCurrentClocksEventReasons", () => _reasons(_dev, out reasons));
            if (r == 3) _naReasons = true; else if (!Check(r, "reasons")) { s.NvErr = r; return; }
            s.ReasonsOk = r == 0; s.Reasons = reasons;
        }
        {
            NvUtil u = default;
            int r = Timed("nvmlDeviceGetUtilizationRates", () => _util!(_dev, out u));
            if (!Check(r, "nvmlDeviceGetUtilizationRates")) { s.NvErr = r; return; }
            if (r == 0) { s.UtilOk = true; s.Util = u.Gpu; any = true; }
        }
        if (_clock != null && !_naClock)
        {
            uint g = 0;
            int r = Timed("nvmlDeviceGetClockInfo", () => _clock(_dev, 0, out g));
            if (r == 3) _naClock = true; else if (!Check(r, "clock")) { s.NvErr = r; return; }
            if (r == 0) { s.ClkOk = true; s.GfxClk = g; }
        }
        if (_temp != null && !_naTemp)
        {
            uint t = 0;
            int r = Timed("nvmlDeviceGetTemperature", () => _temp(_dev, 0, out t));
            if (r == 0) s.TempC = (int)t; else if (r == 3) _naTemp = true; else if (!Check(r, "temp")) { s.NvErr = r; return; }
        }
        if (_pstate != null && !_naPstate)
        {
            int p = -1; int r = Timed("nvmlDeviceGetPerformanceState", () => _pstate(_dev, out p));
            if (r == 0) s.PState = p; else if (r == 3) _naPstate = true;
        }
        if (_psrc != null && !_naPsrc)
        {
            int p = -1; int r = Timed("nvmlDeviceGetPowerSource", () => _psrc(_dev, out p));
            if (r == 0) s.PowerSource = p; else if (r == 3) _naPsrc = true;
        }
        if (any) { _timeouts = 0; _backoffLen = 30; _lastErr = 0; }
        s.NvOk = any;
        LastBatchMs = (Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency;
    }

    private static double FieldValue(IntPtr f)
    {
        int type = Marshal.ReadInt32(f, 24);
        IntPtr v = f + 32;
        return type switch
        {
            0 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(v)),
            1 or 2 => (uint)Marshal.ReadInt32(v),   // unsigned int / unsigned long (32-bit on Windows)
            3 => (ulong)Marshal.ReadInt64(v),
            4 => Marshal.ReadInt64(v),
            5 => Marshal.ReadInt32(v),
            6 => (ushort)Marshal.ReadInt16(v),
            _ => (ulong)Marshal.ReadInt64(v),
        };
    }

    /// <summary>
    /// One gate step (GPU Glance NvStep). <paramref name="wanted"/> is the gate formula already evaluated with a fresh
    /// D-state; <paramref name="readD"/> re-reads the D-state immediately before init and before the batch (R1). Those reads
    /// run outside the session lock. A READY session whose re-read says the GPU left D0 is shut down at once, and on a
    /// closed-window sample a closed gate releases the session at once (the 6 s release applies to Live ticks only).
    /// </summary>
    public void Step(PowerSample s, bool wanted, bool paused, Func<int?> readD)
    {
        double now = Now;
        var st = _nv;
        bool backoffOver = st == NvState.BACKOFF && now >= _backoffUntil;
        bool mayInit = (st is NvState.UNLOADED or NvState.LOADED || backoffOver) && now - _lastInitAt >= 20;
        int? dPre = wanted && (st == NvState.READY || mayInit) ? readD() : null;   // outside the lock
        bool batchAfterInit = false;
        lock (_gate)
        {
            HandleRequests();
            if (_reqArrival) { _reqArrival = false; if (_nv == NvState.FAILED) { _nv = NvState.UNLOADED; FailReason = ""; } }
            if (_nv == NvState.BACKOFF && now >= _backoffUntil) _nv = _lib != IntPtr.Zero ? NvState.LOADED : NvState.UNLOADED;
            bool gate = wanted && _nv != NvState.FAILED && _nv != NvState.BACKOFF;
            s.GateWanted = wanted; s.Gate = gate;
            if (gate)
            {
                _closedSince = -1;
                if (_nv == NvState.UNLOADED)
                {
                    string why = Load();
                    if (why.Length > 0) { _nv = NvState.FAILED; FailReason = why; Log.Warn("power: NVML failed: " + why); }
                    else _nv = NvState.LOADED;
                }
                bool inited = false;
                if (_nv == NvState.LOADED && now - _lastInitAt >= 20 && dPre == 1) inited = Init();
                if (_nv == NvState.READY)
                {
                    if (inited) batchAfterInit = true;                       // the init took time: re-read D before the batch
                    else if (dPre == 1) Batch(s);
                    else ShutdownLocked("GPU left D0 before the batch");
                }
            }
            else if (_nv == NvState.READY)
            {
                bool immediate = s.DState != 1 || paused || !s.Live;
                if (_closedSince < 0) _closedSince = now;
                if (immediate || now - _closedSince >= ReleaseSec)
                    ShutdownLocked(s.DState != 1 || paused ? "GPU left D0 or paused" : s.Live ? "gate closed" : "gate closed on a closed-window sample");
            }
            FillState(s, now);
        }
        if (batchAfterInit)
        {
            int? dB = readD();   // outside the lock
            lock (_gate)
            {
                HandleRequests();
                if (_nv == NvState.READY) { if (dB == 1) Batch(s); else ShutdownLocked("GPU left D0 before the batch"); }
                FillState(s, Now);
            }
        }
        // a shutdown requested while this step held the lock
        if (Volatile.Read(ref _reqShutdown) != null && Monitor.TryEnter(_gate))
        {
            try { HandleRequests(); s.NvState = _nv; } finally { Monitor.Exit(_gate); }
        }
    }

    private void FillState(PowerSample s, double now)
    {
        s.NvState = _nv;
        s.NvErr = s.NvErr != 0 ? s.NvErr : (_nv == NvState.BACKOFF ? _lastErr : 0);
        s.NvRetryIn = _nv == NvState.BACKOFF ? Math.Max(0, _backoffUntil - now) : 0;
        s.NvInitSeq = _initSeq;
        s.TslowC = TslowC; s.TtargetC = TtargetC; s.LmaxMw = LmaxMw; s.Driver = Driver;
    }

    /// <summary>Exit: shut down and FreeLibrary.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_nv == NvState.READY && _shutdown != null) { try { _shutdown(); } catch { } }
            Unload(); _nv = NvState.UNLOADED;
        }
    }
}
