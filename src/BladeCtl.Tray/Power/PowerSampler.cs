using System.Diagnostics;
using System.Globalization;
using BladeCtl.Core;
using BladeCtl.Core.Power;
using WinForms = System.Windows.Forms;

namespace BladeCtl.Tray.Power;

/// <summary>What the device monitor hands the sampler each tick (spec 12.2). Every EC value here was already read by the monitor.</summary>
public sealed record TickInput(BladeController? Ctl, int? DState, PerfMode? Mode, byte? CpuBoost, byte? GpuBoost, double? CpuC, double? GpuC);

/// <summary>
/// Power card orchestration (spec 3-6). Runs on the device monitor's timer thread inside its tick, so there is no new
/// periodic timer; the only extra timer is the one-shot post-plug charger burst. Live (window open on the Blade view):
/// every monitor tick reads and publishes a view. Closed: one cheap sample every 30 s for history and learning, and
/// nothing is rendered. NVML only through the gate and only in D0 (R1); EC GETs only (R2); no process names (R4).
/// </summary>
public sealed class PowerSampler : IDisposable
{
    private readonly Settings _settings;
    private readonly bool _probe;   // --power-probe: read-only, writes no learned data and no power-state.json
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public double Now => _clock.Elapsed.TotalSeconds;

    private readonly BatteryIoctl _bat = new();
    private readonly CpuCounters _cpu = new();
    private readonly GpuCounters _gpuPdh = new();
    private readonly ForegroundCpu _fg = new();
    private readonly NvmlSession _nvml = new();
    private readonly GpuGlanceLink _gg = new();
    private readonly PowerStatePublisher _pub = new();

    private readonly SampleCadence _cadence = new();
    private readonly SupplyTrackers _trackers = new();
    private readonly GpuVerdict _verdict = new();
    private readonly BatteryModel _battery = new();
    private readonly SupplyHysteresis _supplyH = new();
    private readonly CpuRowHold _cpuHold = new();
    private readonly PowerLearn _store = new();
    private readonly PowerLearner _learner;

    public static string LearnedPath => Path.Combine(Log.AppDataDir, "power-learned.json");

    /// <summary>Raised on the monitor thread with a fresh view after every Live sample.</summary>
    public event Action<PowerView>? Published;

    /// <summary>Supplied by the context: the session-only "full power until I plug in" flag.</summary>
    public Func<bool>? FullPowerFlag { get; set; }

    /// <summary>Where the burst timer gets the current controller (the monitor's, or the probe's own).</summary>
    public Func<BladeController?>? ControllerSource { get; set; }

    /// <summary>Called from a probe with every sample (live), for printing.</summary>
    public Action<PowerSample, VerdictOut?, CpuRowOut?, string>? ProbeSink { get; set; }

    public PowerSampler(Settings settings, bool probe = false)
    {
        _settings = settings; _probe = probe;
        _learner = new PowerLearner(_store);
        if (!probe) _store.Load(LearnedPath, m => Log.Warn("power: " + m));
        _lastSeenAC = !OnBatteryNow;
    }

    private static bool OnBatteryNow => WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Offline;

    // ---------- Live / pause ----------

    private volatile bool _live;
    /// <summary>Window visible, not minimized, Blade view, session unlocked. Set from the UI thread.</summary>
    public bool Live
    {
        get => _live;
        set
        {
            if (_live == value) return;
            if (value)
            {
                _ggRefreshWanted = true; _capsAt = DateTime.MinValue; _dispDirty = true;
                // spec 5.1: never show the view from when the window closed; "reading…" until the first Live tick.
                // Posted before _live flips, so the first Live tick's view is always queued after it.
                if (!_probe) { try { Published?.Invoke(ReadingView()); } catch (Exception ex) { Log.Error("power: view handler threw: " + ex.Message); } }
                _live = true;
            }
            else
            {
                _live = false;
                // On battery NVML goes at once; on AC it stays only if the closed-window gate would still be open
                // (a display on the dGPU, or something already using it). Otherwise shut down now rather than at the
                // next closed sample up to 30 s later (spec 11.5: within 6 s). Shutdown never blocks this (UI) thread.
                var s = _last;
                bool closedGate = s != null && PowerMath.GateWanted(s.DState, _paused, s.DBusy, s.DisplayOnD, s.OnAC, false);
                if (OnBatteryNow) _nvml.Shutdown("window closed on battery");
                else if (!closedGate) _nvml.Shutdown("window closed");
            }
        }
    }

    private volatile bool _locked, _suspended;
    private bool _paused => _locked || _suspended;
    /// <summary>Session locked (true) / unlocked (false): while locked NVML is shut down at once and stays closed.</summary>
    public void Pause(bool locked)
    {
        _locked = locked;
        if (locked) _nvml.Shutdown("session locked");
    }

    /// <summary>The monitor reads the D-state on battery only when this tick will sample (spec 12.2).</summary>
    public bool WantsDState => _cadence.WouldSample(_live && !_paused, Now);

    /// <summary>GPU temperature from this tick's NVML batch, so ReadGpuTemp does not start nvidia-smi (spec 4.5).</summary>
    public double? FreshNvmlTempC { get; private set; }

    // ---------- EC charger (spec 8) ----------

    private readonly object _ecGate = new();
    private SupplyClass _class = SupplyClass.AcUnknown;
    private int _adapterW, _recW = 230;
    private byte _lvl, _rec; private bool _ecAnswered;
    private DateTime _ecReadLocal = DateTime.MinValue;
    private double _lastEcAt = -1e9, _lastStatusReadAt = -1e9, _lastEcAnswerAt = -1e9;
    private int _ecMisses;
    private bool _ecStaleLogged;
    private string? _lastAnomaly;
    private System.Threading.Timer? _burst;
    private int _burstGen;
    private bool _burstActive, _startupBurstDone;
    private bool _lastSeenAC;
    private byte? _lastB7;
    private Func<BladeController?>? _ctlSource;
    /// <summary>
    /// Set by test B1 (command file `power-usbc confirmed`) when the dock-only reading is confirmed; until then USB-C is
    /// shown with a question mark in the header, the chips and the sentences. Persisted in power-learned.json.
    /// </summary>
    public bool UsbcConfirmed
    {
        get => _store.UsbcConfirmed;
        set { _store.UsbcConfirmed = value; if (!_probe) _store.Save(LearnedPath, force: true, m => Log.Warn("power: " + m)); }
    }

    private (SupplyClass Class, int W, int Rec) ClassNow() { lock (_ecGate) return (_class, _adapterW, _recW); }

    private void ReadAdapter(BladeController? ctl, string why, bool inBurst)
    {
        if (ctl == null) return;
        var r = ctl.GetAdapterWattage();
        bool onAC = !OnBatteryNow;
        var dec = RazerAdapter.Classify(onAC, r != null, r?.Level ?? 0, r?.Rec ?? 0, inBurst);
        lock (_ecGate)
        {
            _lastEcAt = Now;
            if (r != null) { _lastEcAnswerAt = Now; _ecStaleLogged = false; }
            if (dec.Anomaly != null && dec.Anomaly != _lastAnomaly) { _lastAnomaly = dec.Anomaly; Log.Warn("power: " + dec.Anomaly); }
            // Outside a burst, one 0 W or unanswered read does not throw away a charger identified a moment ago (a barrel
            // reads 0 for ~2.6 s after it is plugged in, F4; a read can time out under HID contention): keep it, and the
            // reading it came from, for up to two misses, so a real swap is still seen as a class change when the next valid
            // read arrives.
            if (dec.Class == SupplyClass.AcUnknown && SupplyTrackers.Identified(_class))
            {
                if (++_ecMisses < 3) return;
            }
            else _ecMisses = 0;
            _ecReadLocal = DateTime.Now; _ecAnswered = r != null;
            if (r != null) { _lvl = r.Value.Level; _rec = r.Value.Rec; }
            var old = (_class, _adapterW);
            // During a burst a 0 reading keeps "identifying"; a valid reading ends the uncertainty.
            _class = dec.Class; _adapterW = dec.AdapterW; _recW = dec.RecW;
            if (old != (_class, _adapterW) && _class != SupplyClass.Settling)
                Log.Write($"power source: {(onAC ? "AC" : "battery")}, {(r != null ? $"Razer EC adapter {(RazerAdapter.Watts(_lvl) is int w ? w + " W" : "level " + _lvl.ToString("X2"))} of {_recW} W recommended" : "Razer EC did not answer")}" +
                          $" ({SourceName(_class, _adapterW)}, was {SourceName(old._class, old._adapterW)}; {why})");
        }
        if (!onAC) Log.Write($"power: EC 0x07/0x8C on battery = {(r is { } rr ? $"{rr.Level:X2}-{rr.Rec:X2}" : "no answer")} ({why})");
        if (r != null && !_probe) _store.NoteEc(DateTime.UtcNow, r.Value.Level, r.Value.Rec, onAC);
    }

    private static string SourceName(SupplyClass c, int w) => c switch
    {
        SupplyClass.Barrel => $"{w} W charger", SupplyClass.UsbC => $"USB-C {w} W", SupplyClass.Battery => "battery",
        SupplyClass.Settling => "identifying", _ => "not identified",
    };

    /// <summary>
    /// Post-plug burst (spec 8.2): to AC, reads at +1, +2, +4, +8 s, stopping at the first valid non-zero, then a confirm
    /// read at +10 s; to DC, one read at +2 s (logged). One-shot timer, disposed when the burst ends.
    /// </summary>
    private void StartBurst(bool toAC, int initialDelayMs = 0)
    {
        int gen = Interlocked.Increment(ref _burstGen);
        _burst?.Dispose();
        double[] steps = toAC ? new[] { 1.0, 2, 4, 8, 10 } : new[] { 2.0 };
        if (toAC) lock (_ecGate) { _class = SupplyClass.Settling; _adapterW = 0; _burstActive = true; }
        else lock (_ecGate) { _class = SupplyClass.Battery; _adapterW = 0; _burstActive = false; }
        int i = 0; double elapsed = 0; bool found = false;
        System.Threading.Timer? t = null;
        void Next()
        {
            if (gen != _burstGen) { t?.Dispose(); return; }
            while (i < steps.Length && found && steps[i] < 10) i++;   // a valid reading skips straight to the confirm read
            if (i >= steps.Length)
            {
                lock (_ecGate) { _burstActive = false; if (_class == SupplyClass.Settling) _class = SupplyClass.AcUnknown; }
                t?.Dispose(); if (_burst == t) _burst = null;
                return;
            }
            double due = steps[i] - elapsed; elapsed = steps[i];
            t!.Change(TimeSpan.FromMilliseconds(Math.Max(0, due * 1000)), Timeout.InfiniteTimeSpan);
        }
        t = new System.Threading.Timer(_ =>
        {
            try
            {
                if (gen != _burstGen) return;
                var ctl = _ctlSource?.Invoke();
                bool last = i == steps.Length - 1;
                ReadAdapter(ctl, toAC ? $"plug burst +{steps[i]:F0} s" : "unplug read +2 s", inBurst: toAC && !last);
                if (toAC) { var c = ClassNow(); if (c.Class is SupplyClass.Barrel or SupplyClass.UsbC) found = true; }
                if (i == 0 || !toAC) ReadB7(ctl);
                i++;
                Next();
            }
            catch (Exception ex) { Log.Error("power: charger burst: " + ex.Message); }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _burst = t;
        elapsed = -initialDelayMs / 1000.0;
        Next();
    }

    private void ReadB7(BladeController? ctl)
    {
        if (ctl == null) return;
        var b = ctl.GetExternalPowerStatus();
        _lastB7 = b;
        Log.Write($"power: EC 0x00/0xB7 external power status = {(b is byte v ? v.ToString("X2") : "no answer")} (Windows says {(OnBatteryNow ? "battery" : "AC")}; log only)");
    }

    /// <summary>PowerModeChanged StatusChange (fires on battery-% changes too). Spec 8.2.</summary>
    public void OnPowerStatusChange()
    {
        bool onAC = !OnBatteryNow;
        if (onAC != _lastSeenAC)
        {
            _lastSeenAC = onAC;
            StartBurst(onAC);
            _capsAt = DateTime.MinValue;
            // unplugged with the window closed: a session kept for a display on the dGPU must not ride into battery time
            if (!onAC && !_live) _nvml.Shutdown("unplugged with the window closed");
            return;
        }
        // A status change without an AC/DC flip (a barrel plugged in or pulled while docked, or just a battery-% tick):
        // one read at the first monitor tick from +3 s, after the ~2.6 s a freshly plugged barrel reads 0 W (F4). The read
        // runs on the monitor thread (spec 3: only the post-plug burst reads the EC off it); nothing is read here.
        if (onAC && !_burstActive && Now - _lastStatusReadAt >= 5)
        {
            _lastStatusReadAt = Now;
            Volatile.Write(ref _statusReadDue, Now + 3);
        }
    }

    private double _statusReadDue = -1;

    public void OnSuspend()
    {
        _suspended = true;
        _nvml.Shutdown("system suspending");
        FlushPublisher();
        if (!_probe) _store.Save(LearnedPath, force: true, m => Log.Warn("power: " + m));
    }

    /// <summary>Resume (SystemEvents thread): the verdict / learner / tracker resets are applied by the next tick, on the monitor thread.</summary>
    public void OnResume()
    {
        _suspended = false;   // a session that is still locked stays paused until it unlocks
        _resumeReq = true;
        _lastSeenAC = !OnBatteryNow;
        StartBurst(_lastSeenAC, initialDelayMs: 2000);   // first read at +3 s
    }

    /// <summary>
    /// Display configuration changed. With the window closed, a session held open for a display on the dGPU is released at
    /// once (the next closed sample, up to 30 s away, reopens it if a display is still there): NVML is never held for a
    /// display that just went away.
    /// </summary>
    public void OnDisplayChanged()
    {
        _dispDirty = true;
        if (!_live && _nvml.State == NvState.READY) _nvml.Shutdown("display change with the window closed");
    }

    /// <summary>WM_DEVICECHANGE (any devnode, UI thread): the next tick checks whether the NVIDIA devnode itself went or came back.</summary>
    public void OnDeviceChanged() => _devReq = true;

    private volatile bool _resumeReq, _devReq;
    private bool? _gpuOkPrev;

    /// <summary>Monitor thread, under the tick lock: resets requested by other threads.</summary>
    private void ApplyPending(double now)
    {
        if (_resumeReq)
        {
            _resumeReq = false;
            _verdict.OnResume();
            _learner.MarkResume(now);
            _trackers.MarkFlip(now);
            _cpuHold.Reset();
            _luidAt = DateTime.UtcNow.AddSeconds(5 - 600);   // LUID re-read at +5 s
            _luid = null; _dispDirty = true;
        }
        if (_devReq)
        {
            _devReq = false;
            var st = GpuPower.NvidiaStatus();
            bool ok = st is { Started: true, Problem: false };
            // spec 4.5: the NVIDIA devnode gone or stopped -> FreeLibrary; it coming back -> FAILED may be retried.
            // Any other device churn (USB, dock) changes nothing here.
            if (_gpuOkPrev == true && !ok) _nvml.FreeLibraryNow("NVIDIA GPU removed or stopped");
            else if (_gpuOkPrev != true && ok) _nvml.NoteDeviceArrival();
            NoteGpuStatus(st);
            _luid = null; _luidAt = DateTime.MinValue; _dispDirty = true;
        }
    }

    private void NoteGpuStatus((bool Started, bool Problem, uint ProblemCode)? st)
    {
        _gpuStatus = st is { } gs ? (gs.Started, gs.Problem, gs.ProblemCode) : null; _gpuStatusAt = DateTime.UtcNow;
        _gpuOkPrev = st is { Started: true, Problem: false };
    }

    /// <summary>Write a change power-state.json is holding back (10 s rate limit) before suspend / exit.</summary>
    private void FlushPublisher()
    {
        if (_probe) return;
        if (!Monitor.TryEnter(_tickLock, 2000)) return;
        try { _pub.FlushNow(); } finally { Monitor.Exit(_tickLock); }
    }

    public void FlushLearned() { if (!_probe) _store.Save(LearnedPath, force: true, m => Log.Warn("power: " + m)); }

    public void ResetLearned()
    {
        _store.Reset();
        if (!_probe) _store.Save(LearnedPath, force: true, m => Log.Warn("power: " + m));
        Log.Write("power: learned power data reset");
    }

    // ---------- cached slow reads ----------

    private uint _fullMwh, _designMwh; private DateTime _infoAt = DateTime.MinValue;
    private int _winEst = -1; private DateTime _winEstAt = DateTime.MinValue;
    private PowerPolicy.Caps? _caps; private DateTime _capsAt = DateTime.MinValue;
    private (uint Low, int High)? _luid; private string _luidTag = ""; private DateTime _luidAt = DateTime.MinValue;
    private bool _dispOnD, _dispKnown, _panelOn; private int _panelHz; private DateTime _dispAt = DateTime.MinValue; private volatile bool _dispDirty = true;
    private int _nvSvc = -1; private DateTime _svcAt = DateTime.MinValue;
    private bool _ggRefreshWanted = true;
    private (bool Started, bool Problem, uint Code)? _gpuStatus; private DateTime _gpuStatusAt = DateTime.MinValue;
    private int? _fan1, _fan2;

    // ---------- history (sparkline) ----------

    private readonly record struct HistPt(double T, double W, bool OnAC, bool Closed);
    private readonly List<HistPt> _hist = new();
    private readonly object _histGate = new();

    // ---------- last state for the dump / heartbeat ----------

    private PowerSample? _last;
    private VerdictOut? _lastVo;
    private CpuRowOut? _lastCpu;
    private SupplyState _lastSupply = SupplyState.PLUGGED_IN;
    private double _lastLiveT = -1e9;
    private string _verdictSource = "BladeCtl";
    private St _loggedShown = St.UNKNOWN;
    private readonly Dictionary<(St, St), double> _verdictLogAt = new();
    private double _errAt = -1e9;
    private double _usbcLowSince = -1;

    // ---------- the tick ----------

    public void OnTick(TickInput x)
    {
        FreshNvmlTempC = null;
        if (_disposed) return;
        if (ControllerSource != null) _ctlSource = ControllerSource;
        else if (x.Ctl != null) { var c = x.Ctl; _ctlSource = () => c; }
        double now = Now;
        lock (_tickLock)
        {
            if (_disposed) return;
            try
            {
                ApplyPending(now);
                if (!_probe) _pub.Flush();
                // R1: on AC the monitor reads the D-state every tick; a READY session dies the moment the GPU leaves D0.
                if (x.DState is int dd && dd != 1 && _nvml.State == NvState.READY) _nvml.Shutdown("GPU left D0");
                if (!_startupBurstDone && x.Ctl != null) { _startupBurstDone = true; _lastSeenAC = !OnBatteryNow; StartBurst(_lastSeenAC); }
                double due = Volatile.Read(ref _statusReadDue);
                if (due > 0 && now >= due)
                {
                    Volatile.Write(ref _statusReadDue, -1);
                    if (!_burstActive && !OnBatteryNow) ReadAdapter(x.Ctl, "status change", false);
                }
                var kind = _cadence.Decide(_live && !_paused, now);
                if (kind == SampleCadence.Kind.None) return;
                Sample(x, now, kind == SampleCadence.Kind.Live);
            }
            catch (Exception ex)
            {
                if (now - _errAt > 60) { _errAt = now; Log.Error($"power sampler: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }

    private readonly object _tickLock = new();
    private volatile bool _disposed;

    private void Sample(TickInput x, double now, bool live)
    {
        bool onAC = !OnBatteryNow;
        var s = new PowerSample { T = now, Utc = DateTime.UtcNow, Live = live, OnAC = onAC, LogicalCpus = Environment.ProcessorCount };
        s.BatterySaver = SysPower.BatterySaver();

        // 1. D-state (wake-free; shared with the monitor when it already read it this tick)
        int d = x.DState ?? GpuPower.NvidiaDState() ?? 0;
        s.DState = d;
        if (_gpuStatus == null || DateTime.UtcNow - _gpuStatusAt > TimeSpan.FromSeconds(30)) NoteGpuStatus(GpuPower.NvidiaStatus());
        s.Present = _gpuStatus != null || d != 0; s.Started = _gpuStatus?.Started ?? true; s.Problem = _gpuStatus?.Problem ?? false; s.ProblemCode = (int)(_gpuStatus?.Code ?? 0);
        if (!live) _cadence.NoteClosedD(d);

        // 2. battery IOCTL
        var b = _bat.QueryStatus();
        if (DateTime.UtcNow - _infoAt > TimeSpan.FromMinutes(10) || _fullMwh == 0) { var inf = _bat.QueryInformation(); if (inf != null) { _fullMwh = inf.FullMwh; _designMwh = inf.DesignMwh; } _infoAt = DateTime.UtcNow; }
        if (live && !onAC && DateTime.UtcNow - _winEstAt > TimeSpan.FromSeconds(30)) { _winEst = _bat.QueryEstimatedSec(); _winEstAt = DateTime.UtcNow; }
        if (onAC) _winEst = -1;
        s.BatOk = b.Ok; s.BatState = b.PowerState; s.RateKnown = b.RateKnown; s.RateMw = b.RateMw; s.CapMwh = b.CapKnown ? b.CapMwh : 0; s.CapKnown = b.Ok && b.CapKnown;
        s.FullMwh = _fullMwh; s.DesignMwh = _designMwh; s.WinEstSec = _winEst;
        if (b.CapKnown && _fullMwh > 0) s.SocPct = (int)Math.Clamp(Math.Round(100.0 * b.CapMwh / _fullMwh), 0, 100);
        // cross-check: the battery's own ON_LINE bit against Windows' power line status (logged on change only)
        bool? online = b.Ok ? (b.PowerState & 0x1) != 0 : null;
        if (online.HasValue && online.Value != onAC && _onlineMismatch != true) { _onlineMismatch = true; Log.Warn($"power: battery ON_LINE bit says {(online.Value ? "AC" : "battery")} while Windows says {(onAC ? "AC" : "battery")}"); }
        else if (online.HasValue && online.Value == onAC) _onlineMismatch = false;

        // 3. CPU counters
        var c = _cpu.Collect();
        s.CpuOk = c.Ok; s.PkgW = c.PkgW; s.Pp0W = c.Pp0W; s.Pp1W = c.Pp1W; s.FreqMHz = c.FreqMHz; s.UtilityPct = c.UtilityPct;
        s.PerfLimitPct = c.PerfLimitPct; s.PerfLimitFlags = c.PerfLimitFlags; s.NominalMHz = c.NominalMHz; s.AllCorePct = c.AllCorePct;
        _lastPowerCounterW = c.PowerCounterW;

        // 4. foreground app (cores only; the private-apps list decides whether it may count at all)
        var f = _fg.Sample(now, live ? 6 : 25);
        s.FgKnown = f.Known; s.FgPid = f.Pid; s.FgCores = f.Cores; s.FgPrivate = f.Private; s.FgSelf = f.Self; s.FgShell = f.Shell;

        // 5. dGPU LUID, display on the dGPU, built-in panel
        if (_luid == null && DateTime.UtcNow - _luidAt > TimeSpan.FromMinutes(10))
        {
            _luidAt = DateTime.UtcNow; _luid = GpuPower.NvidiaLuid();
            _luidTag = _luid is { } l ? $"luid_0x{(uint)l.High:X8}_0x{l.Low:X8}" : "";
        }
        if (_dispDirty || (live && DateTime.UtcNow - _dispAt > TimeSpan.FromSeconds(30)))
        {
            _dispDirty = false; _dispAt = DateTime.UtcNow;
            if (_luid is { } l2 && PanelRefresh.AnyTargetOnAdapter(l2.Low, l2.High) is bool on) { _dispOnD = on; _dispKnown = true; } else _dispKnown = false;
            var panel = PanelRefresh.FindInternal();
            _panelOn = panel != null; _panelHz = panel != null ? PanelRefresh.Current(panel) ?? 0 : 0;
        }
        s.DisplayKnown = _dispKnown; s.DisplayOnD = _dispOnD; s.PanelOn = _panelOn; s.PanelHz = _panelHz;

        // 6. dGPU PDH, only in D0 (opened the first time D0 is seen)
        if (d == 1 && _luidTag.Length > 0)
        {
            _gpuPdh.Open();
            var g = _gpuPdh.Collect(_luidTag, wantHolders: !onAC || live, excludePids: _nvSvcPid > 0 ? new[] { _selfPid, _nvSvcPid } : new[] { _selfPid });
            s.GpuPdh = g.Ok; s.DBusy = g.Busy; s.DMemHolders = g.MemHolders;
            if (g.MemHolders >= 0) _lastD0Holders = g.MemHolders;
        }

        // 7. Razer EC: charger (every 10 s Live / each closed sample on AC), real fan speed (Live only)
        var ctl = x.Ctl;
        if (onAC && !_burstActive && (now - _lastEcAt >= 10 || (!live && now - _lastEcAt >= 5))) ReadAdapter(ctl, live ? "periodic" : "closed sample", false);
        if (!onAC && !_burstActive) lock (_ecGate) { _class = SupplyClass.Battery; _adapterW = 0; _ecMisses = 0; }
        // no EC answer for 60 s on AC (controller dropped, Synapse holding the interface): the last charger is no longer known
        if (onAC && !_burstActive)
            lock (_ecGate)
                if (SupplyTrackers.Identified(_class) && now - _lastEcAnswerAt >= 60)
                {
                    if (!_ecStaleLogged) { _ecStaleLogged = true; Log.Write($"power source: no Razer EC charger reading for {now - _lastEcAnswerAt:F0} s; was {SourceName(_class, _adapterW)}, now not identified"); }
                    _class = SupplyClass.AcUnknown; _adapterW = 0;
                }
        if (live && ctl != null) { _fan1 = ctl.GetFanCurrentRpm(1); _fan2 = ctl.GetFanCurrentRpm(2); }
        var cls = ClassNow();
        s.Class = onAC ? cls.Class : SupplyClass.Battery; s.AdapterW = cls.W; s.RecW = cls.Rec;
        if (onAC && s.Class == SupplyClass.Battery) s.Class = SupplyClass.Settling;   // Windows flipped before the burst started
        s.FanRpm1 = live ? _fan1 : null; s.FanRpm2 = live ? _fan2 : null;
        s.RazerMode = x.Mode is PerfMode m ? Settings.PowerModeName(m) : "?";
        s.CpuBoost = x.CpuBoost; s.GpuBoost = x.GpuBoost; s.EcCpuC = x.CpuC; s.EcGpuC = x.GpuC;
        s.ProfileEngaged = _settings.BatteryProfileEngaged; s.FullPowerUntilAc = FullPowerFlag?.Invoke() ?? false;

        // 8. NVIDIA display service (read-only), every 10 s on battery
        if (!onAC && DateTime.UtcNow - _svcAt > TimeSpan.FromSeconds(10)) { _svcAt = DateTime.UtcNow; _nvSvc = SysPower.ServiceState(NvSvcName, out _nvSvcPid); }
        else if (onAC) { _nvSvc = -1; _nvSvcPid = 0; }
        s.NvSvc = _nvSvc;

        // 9. trackers, battery model, Windows caps, GPU Glance ini
        _trackers.Note(s);
        bool classChanged = _trackers.ClassChangedNow;
        if (classChanged) { _verdict.OnClassChange(now); _supplyH.Reset(); _battery.OnPlugEvent(now); }
        if (_trackers.FlipAt == now) _supplyH.Reset();
        if (_caps == null || (live && DateTime.UtcNow - _capsAt > TimeSpan.FromMinutes(5)) || _capsOnAC != onAC)
        { _caps = PowerPolicy.Read(); _capsAt = DateTime.UtcNow; _capsOnAC = onAC; }
        if (live) { _gg.Refresh(_ggRefreshWanted); _ggRefreshWanted = false; }
        var curve = _gg.Curve; string curveSrc = "GG";
        if (curve == null) { curve = _store.ChgCurve(RazerAdapter.SrcKey(s.Class, s.AdapterW)); curveSrc = curve != null ? "BladeCtl" : "model"; }
        _battery.Curve = curve; _battery.CurveSource = curveSrc;
        _battery.Update(now, s);

        // 10. NVML gate (Mode S: GPU Glance's verdict is fresh -> no own NVML while Live)
        GpuGlanceLink.ModeS? ms = live ? GpuGlanceLink.ReadModeS() : null;
        _verdictSource = ms != null ? "GPU Glance" : "BladeCtl";
        bool wanted = ms == null && PowerMath.GateWanted(d, _paused, s.DBusy, s.DisplayOnD, onAC, live);
        if (!live && !onAC) wanted &= _cadence.ConsecutiveClosedD0 >= 2;
        _nvml.Step(s, wanted, _paused, () => GpuPower.NvidiaDState());
        if (!live && !onAC && _nvml.State == NvState.READY) _nvml.Shutdown("closed sample on battery: never held across 30 s");
        if (s.NvOk && s.TempC >= 0) FreshNvmlTempC = s.TempC;
        if (_nvml.Driver.Length > 0) _store.Driver = _nvml.Driver;
        if (s.Driver.Length == 0) s.Driver = _store.Driver ?? "";

        // 11. history point
        if (s.RateKnown)
            lock (_histGate)
            {
                _hist.Add(new HistPt(now, s.RateMw / 1000.0, onAC, !live));
                while (_hist.Count > 4000 || (_hist.Count > 0 && now - _hist[0].T > 7200)) _hist.RemoveAt(0);
            }

        // 12. verdict (Live only), learning, publishing
        VerdictOut? vo = null; CpuRowOut? cpuRow = null;
        double dt = live ? Math.Clamp(now - _lastLiveT, 0.5, 5) : 30;
        if (live)
        {
            // window was closed (or on another view) for more than 10 s: the supply row and the CPU row start fresh too
            if (now - _lastLiveT > 10) { _supplyH.Reset(); _usbcLowSince = -1; _cpuHold.Reset(); }
            var refs = Refs(s);
            vo = _verdict.Push(s, refs);
            if (ms != null) ApplyModeS(vo, ms, s);
            cpuRow = _cpuHold.Push(CpuRow(s, vo.D));
            LogVerdict(vo, now);
            _lastLiveT = now;
            var view = BuildView(s, vo, refs, cpuRow, now);
            try { Published?.Invoke(view); } catch (Exception ex) { Log.Error("power: view handler threw: " + ex.Message); }
            ProbeSink?.Invoke(s, vo, cpuRow, view.Strip.Status + " | " + view.Strip.Action);
        }
        if (!_probe)
        {
            _learner.Learn(s, live ? vo!.D : null, live ? vo!.Shown : null, dt);
            _store.Save(LearnedPath, force: false, m => Log.Warn("power: " + m));
            _pub.Publish(new PowerStatePublisher.Doc(onAC, s.AdapterW, s.RecW, s.Class.ToString(), s.RazerMode, s.CpuBoost, s.GpuBoost,
                _settings.BatteryProfileEngaged, s.FullPowerUntilAc, _settings.BatteryServicesStopped.Count > 0), force: classChanged);
            foreach (var promo in _store.TakePromotions()) Log.Write("power: " + promo);
        }
        else if (!live) ProbeSink?.Invoke(s, null, null, "closed sample");
        _last = s; _lastVo = vo ?? _lastVo; _lastCpu = cpuRow ?? _lastCpu;
    }

    private double _lastPowerCounterW = -1;
    private const string NvSvcName = "NVDisplay.ContainerLocalSystem";
    private int _nvSvcPid;
    private readonly int _selfPid = Environment.ProcessId;
    private int _lastD0Holders = -1;   // dGPU memory holders on the last D0 tick (D3 ticks read no PDH)
    private bool? _capsOnAC;
    private bool? _onlineMismatch;

    private void ApplyModeS(VerdictOut vo, GpuGlanceLink.ModeS ms, PowerSample s)
    {
        vo.Shown = ms.Shown; vo.ShownSub = ms.ShownSub; vo.Raw = ms.Raw; vo.RawSub = ms.RawSub;
        var d = vo.D;
        d.Nv = true; d.HaveP = ms.P >= 0; d.P = ms.P; d.L = ms.L; d.Ldef = ms.Ldef; d.Lmax = ms.Lmax; d.Lref = ms.Lref; d.LrefLearned = ms.LrefLearned;
        d.U = ms.U; d.T = (int)Math.Round(ms.T); d.Duty74 = ms.Duty74; d.Duty270 = ms.Duty270; d.Duty271 = ms.Duty271; d.FracBrake = ms.FracBrake;
        // The GPU row follows GPU Glance's verdict too (BladeCtl read no NVML in Mode S, so its own GPU-only state is blind).
        // GPU Glance's shown state includes the CPU overlay: under CPU_LIMITED_BATTERY the GPU row shows the calm GPU state.
        if (ms.Shown != St.CPU_LIMITED_BATTERY) { vo.GpuShown = ms.Shown; vo.GpuShownSub = ms.ShownSub; }
        else
        {
            bool capped = ms.P >= 0 && ms.L > 0 && ms.P >= ms.L - 5;
            vo.GpuShown = s.DState != 1 ? St.ASLEEP : ms.P < 0 ? St.AWAKE_IDLE : ms.U >= 60 ? (capped ? St.FULL_POWER : St.WORKING) : St.LIGHT;
            vo.GpuShownSub = vo.GpuShown == St.FULL_POWER && !s.OnAC ? Sub.OnBattery : Sub.None;
        }
    }

    private void LogVerdict(VerdictOut vo, double now)
    {
        if (vo.Shown == _loggedShown) return;
        var key = (_loggedShown, vo.Shown);
        if (_verdictLogAt.TryGetValue(key, out var at) && now - at < 10) { _loggedShown = vo.Shown; return; }
        _verdictLogAt[key] = now;
        Log.Write($"power verdict: {_loggedShown} -> {vo.Shown}{(vo.ShownSub != Sub.None ? $" ({vo.ShownSub})" : "")}");
        _loggedShown = vo.Shown;
    }

    // ---------- learned references (spec 7.2 D3) ----------

    private string SavedMode => _settings.PowerMode;
    private string SavedBst => PowerKeys.Bst(_settings.PowerMode, _settings.CpuBoost, _settings.GpuBoost);

    private GpuRefs Refs(PowerSample s)
    {
        string drv = s.Driver;
        string savedMode = SavedMode, savedBst = SavedBst;
        // Lref: the saved AC mode on the barrel (this driver, then any driver), then the best barrel limit of any mode,
        // then any AC limit. BarrelLacW (the USB-C comparison) comes from the saved mode only.
        (double W, string Date)? lref = _store.GpuLimit(PowerKeys.Gpu(drv, "B230", savedMode, savedBst, "P0")) is { } v ? (v.W, v.Date) : null;
        lref ??= _store.GpuLimitBest(p => p.Src.StartsWith('B') && p.Mode == savedMode && p.Bst == savedBst && p.Prof == "P0");
        double barrel = lref?.W ?? -1;
        lref ??= _store.GpuLimitBest(p => p.Src.StartsWith('B') && p.Prof == "P0");
        lref ??= _store.GpuLimitBest(p => p.Src != "DC");
        bool learned = lref != null;
        if (lref == null && _gg.AcP95W > 0) { lref = (_gg.AcP95W, ""); learned = true; }
        // Lbat: the live mode and profile state (this driver, then any driver), then any DC limit with the same profile state
        string liveBst = PowerKeys.Bst(s.RazerMode, s.CpuBoost, s.GpuBoost), liveProf = PowerKeys.Prof(s.ProfileEngaged);
        (double W, string Date)? lbat = _store.GpuLimit(PowerKeys.Gpu(drv, "DC", s.RazerMode, liveBst, liveProf)) is { } lb ? (lb.W, lb.Date) : null;
        lbat ??= _store.GpuLimitBest(p => p.Src == "DC" && p.Mode == s.RazerMode && p.Bst == liveBst && p.Prof == liveProf);
        lbat ??= _store.GpuLimitBest(p => p.Src == "DC" && p.Prof == liveProf);
        var clk = _store.GClk(PowerKeys.GClk(drv, "B230", savedMode, savedBst, "P0"));
        var cpu1 = _store.CpuBest(p => p.Src.StartsWith('B') && p.Mode == savedMode && p.Bst == savedBst && p.Prof == "P0" && p.Shape == "1T");
        return new GpuRefs
        {
            LrefW = lref?.W ?? -1, LrefLearned = learned, LrefDate = lref?.Date ?? "", BarrelLacW = barrel,
            LbatW = lbat?.W ?? -1, LbatDate = lbat?.Date ?? "", ClkRefMHz = clk?.MHz ?? -1,
            CpuRefValid = cpu1 != null && cpu1.W > 0, CpuRefW = cpu1?.W ?? 0, CpuRefMHz = cpu1?.MHz ?? 0, CpuRefDate = cpu1?.Updated ?? "",
        };
    }

    private (double Ceiling, int Pct, bool BoostOff) WindowsCap(PowerSample s)
    {
        var caps = _caps;
        if (caps is not { Ok: true } || s.NominalMHz <= 0) return (-1, 100, false);
        int pct = s.OnAC ? caps.AcMaxPct : caps.DcMaxPct, boost = s.OnAC ? caps.AcBoost : caps.DcBoost;
        return (CpuVerdict.Ceiling(s.NominalMHz, pct, boost < 0 ? 3 : boost), pct, boost == 0);
    }

    private CpuRowOut CpuRow(PowerSample s, Derived d)
    {
        string shape = d.Busy1T ? "1T" : "MT";
        string src = RazerAdapter.SrcKey(s.OnAC ? s.Class : SupplyClass.Battery, s.AdapterW);
        var refAC = _store.CpuBest(p => p.Src.StartsWith('B') && p.Mode == SavedMode && p.Bst == SavedBst && p.Prof == "P0" && p.Shape == shape);
        var refSrc = _store.Cpu(PowerKeys.Cpu(src, s.RazerMode, PowerKeys.Bst(s.RazerMode, s.CpuBoost, s.GpuBoost), PowerKeys.Prof(s.ProfileEngaged), d.Busy1T));
        var cap = WindowsCap(s);
        return CpuVerdict.Evaluate(new CpuInputs
        {
            CpuOk = s.CpuOk, FgPrivate = s.FgPrivate, D = d, OnAC = s.OnAC, Class = s.Class, AdapterW = s.AdapterW, DrainingOnAC = s.DrainingOnAC,
            ProfileEngaged = s.ProfileEngaged, BatteryCpuBoostName = Wording.BoostName(_settings.BatteryCpuBoost, true),
            RefAC = refAC, RefSrc = refSrc, CeilingMHz = cap.Ceiling, CapPct = cap.Pct, BoostOff = cap.BoostOff,
            CpuC = s.EcCpuC, FanRpm = s.MaxFanRpm, UsbcConfirmed = UsbcConfirmed,
        });
    }

    // ---------- the view ----------

    private PowerView BuildView(PowerSample s, VerdictOut vo, GpuRefs refs, CpuRowOut cpu, double now)
    {
        var d = vo.D;
        bool onAC = s.OnAC;
        int soc = s.Soc;
        double gpuW = PowerMath.GpuW(s, d);
        double restLearned = _store.RestFor(s.PanelOn, s.PanelHz);
        double useW = PowerMath.UseW(s, gpuW, restLearned);
        double supplyEst = PowerMath.SupplyEst(s, useW);

        // supply row state
        int bin = s.SocFrac >= 0 ? Math.Min(BatteryEta.Bins - 1, (int)(s.SocFrac * BatteryEta.Bins)) : 0;
        double barrelBand = _store.ChgBand("B230", bin);
        var rawSup = SupplyTable.Raw(s, barrelBand, out var held);
        var sup = _supplyH.Push(rawSup, now, held);
        _lastSupply = sup;

        // header
        string glyph = !onAC ? "battery" : s.Class switch { SupplyClass.Barrel => "plug", SupplyClass.UsbC => "usbc", _ => "unknown" };
        string headText = Wording.SourceText(onAC, s.Class, s.AdapterW, UsbcConfirmed);
        string tip;
        lock (_ecGate)
            tip = !onAC ? "Windows reports battery power." + (_ecAnswered ? $" Razer EC adapter level {_lvl:X2} (read {_ecReadLocal:HH:mm:ss})." : "")
                : _ecAnswered ? $"Razer EC: adapter {(RazerAdapter.Watts(_lvl) is int aw ? aw + " W" : "level " + _lvl.ToString("X2"))}, recommended {_recW} W (read {_ecReadLocal:HH:mm:ss})"
                : "The Razer EC has not identified the charger.";
        if (onAC && s.Class == SupplyClass.UsbC && !UsbcConfirmed)
            tip = $"Razer EC reports a {s.AdapterW} W supply (recommended {s.RecW} W). USB-C is inferred: Synapse's logs on this laptop show 65 W on USB-C";
        // behavioural hint when the class is unknown: much slower charging than the barrel learned for this band
        bool slow = onAC && s.Class == SupplyClass.AcUnknown && soc is >= 0 and < 80 && barrelBand > 0 && s.RateKnown && s.RateMw < 0.5 * barrelBand;
        if (slow) { if (_usbcLowSince < 0) _usbcLowSince = now; } else _usbcLowSince = -1;
        if (_usbcLowSince >= 0 && now - _usbcLowSince >= 60) tip += " Charging is much slower than on the 230 W charger: probably a smaller charger.";
        bool usbcOk = UsbcConfirmed;
        var header = new HeaderModel(glyph, headText, tip, s.Class == SupplyClass.Settling || (s.Class == SupplyClass.UsbC && !usbcOk && onAC), onAC ? "Razer EC" : "Windows");

        // wording
        var cap = WindowsCap(s);
        var caps = _caps;
        int banner = now - vo.AcDcAt < 4.0 && now >= vo.AcDcAt ? (onAC ? 2 : 1) : 0;
        bool reading = vo.Reading && banner == 0;   // just opened: an attention state is serving its hold
        var ctx = new WordCtx
        {
            St = vo.Shown, Sub = vo.ShownSub, D = d, Lv = refs, OnAC = onAC, Class = s.Class, AdapterW = s.AdapterW, RecW = s.RecW, Supply = sup,
            LiveMode = s.RazerMode, LiveGpuBoost = s.GpuBoost, ProfileEnabled = _settings.BatteryProfile, ProfileEngaged = s.ProfileEngaged,
            BatteryCpuBoostName = Wording.BoostName(_settings.BatteryCpuBoost, true), BatteryGpuBoostName = Wording.BoostName(_settings.BatteryGpuBoost, false),
            Cpu = cpu, DcMaxPct = caps?.DcMaxPct ?? 100, DcBoostOff = caps?.DcBoost == 0, FanRpm = s.MaxFanRpm, NvSvc = s.NvSvc,
            DMemHolders = s.DMemHolders >= 0 ? s.DMemHolders : _lastD0Holders,   // D3 ticks read no PDH: the last D0 count
            Elevated = AutoStart.IsElevated, StopsService = _settings.BatteryStopServices.Any(n => n.Equals(NvSvcName, StringComparison.OrdinalIgnoreCase)),
            UseW = useW, ProblemCode = s.ProblemCode, AcDcBanner = banner, UsbcConfirmed = usbcOk,
        };
        double gamingLac = _store.GpuLimit(PowerKeys.Gpu(s.Driver, "B230", "Gaming", "x", "P0"))?.W ?? -1;
        double balancedLac = _store.GpuLimit(PowerKeys.Gpu(s.Driver, "B230", "Balanced", "x", "P0"))?.W ?? -1;
        string? button = banner != 0 ? null : PowerButtons.Decide(vo.Shown, vo.ShownSub, onAC, s.MaxFanRpm, s.ProfileEngaged, cpu.Code, s.RazerMode, d.CappedSec, gamingLac, balancedLac);
        if (reading) button = null;
        var strip = new StripModel(banner != 0 || reading ? "grey" : Wording.StripKind(vo.Shown, vo.ShownSub), banner != 0 || reading ? "unknown" : Wording.StripGlyph(vo.Shown),
                                   reading ? "Reading the power state…" : Wording.Status(ctx), reading ? "" : Wording.Action(ctx), !reading && Wording.Probably(ctx), button, PowerButtons.Label(button));

        // flow
        double batW = _battery.EmaW ?? (s.RateKnown ? s.RateMw / 1000.0 : 0);
        double eta = _battery.EtaSec(now, s);
        string etaText = onAC ? (_battery.Settling(now) && batW > 0.5 ? "settling…" : eta > 0 ? "full " + CompactDuration(eta) : soc >= 95 ? "full" : "")
                              : eta > 0 ? CompactDuration(eta) + " left" : "";
        bool drainAC = onAC && batW <= -0.5;
        double knownLower = Math.Max(0, s.PkgW) + Math.Max(0, gpuW) + Math.Max(0, batW);
        double supW = supplyEst > 0 ? supplyEst : knownLower;
        double restW = !onAC ? (useW > 0 && gpuW >= 0 && s.PkgW >= 0 ? Math.Max(0, useW - s.PkgW - gpuW) : -1) : (useW > 0 ? restLearned : -1);
        string stack = $"CPU {(s.PkgW >= 0 ? Wording.W0(d.PkgW >= 0 ? d.PkgW : s.PkgW) : "?")} · GPU {(gpuW >= 0 ? Wording.W0(gpuW) : "?")} · " + (restW >= 0 ? (onAC ? "~" : "") + Wording.W0(restW) : "rest ?");
        var flow = new FlowModel(onAC, glyph,
            supW, supplyEst > 0, !onAC ? "" : supplyEst > 0 ? "~" + Wording.Watts(supplyEst) : s.AdapterW > 0 ? Wording.Watts(s.AdapterW) : "AC",
            !onAC ? "" : supplyEst > 0 ? "est. supply" : s.AdapterW > 0 ? "rated" : "plugged in",
            onAC && (SupplyTable.IsAmber(sup) || vo.Shown == St.CHARGER_LIMITED),
            batW, Math.Abs(batW) < 0.5 ? (soc >= 95 && onAC ? "full" : "idle") : (batW > 0 ? "+" : "−") + Wording.Watts(Math.Abs(batW)),
            Math.Abs(batW) < 0.5 ? "" : BatteryModel.PctPerHour(batW, s.FullMwh), drainAC, batW >= 0.5, soc, etaText,
            d.PkgW >= 0 ? d.PkgW : s.PkgW, gpuW, restW, onAC, stack,
            Wording.FlowSummary(onAC, s.Class, s.AdapterW, supplyEst, batW, soc, etaText));

        // rows
        double cpuNow = d.PkgW >= 0 ? d.PkgW : s.PkgW;
        string cpuShape = d.Busy1T ? "1T" : "MT";
        var refAC = _store.CpuBest(p => p.Src.StartsWith('B') && p.Mode == SavedMode && p.Bst == SavedBst && p.Prof == "P0" && p.Shape == cpuShape);
        double cpuRef = refAC?.W ?? -1;
        var cpuBar = new BarModel(Math.Max(60, 1.15 * Math.Max(cpuRef, cpuNow)), cpuNow, false, -1, false, "", cpuRef, false, !s.CpuOk, "cpu",
            $"CPU package {(cpuNow >= 0 ? Wording.Watts(cpuNow) : "not read")}" + (cpuRef > 0 ? $", ~{Wording.Watts(cpuRef)} on the 230 W charger for this kind of load ({PowerLearn.LearnedLabel(refAC!.Updated)})" : "") + " (Intel RAPL)");
        var cpuModel = new RowModel(true, "CPU", cpu.Numbers, cpu.Word, cpu.Kind, cpu.Code == CpuCode.C3_Hot ? "heat" : cpu.Kind == "warn" ? "cap" : cpu.Kind == "ok" ? "check" : cpu.Code == CpuCode.C2_Idle ? "sleep" : "unknown", cpu.Reason, cpuBar);

        var gw = Wording.GpuRow(vo.GpuShown, vo.GpuShownSub, d, refs, onAC, s.Class, s.AdapterW, d.Nv, usbcOk);
        double lmax = d.Lmax > 0 ? d.Lmax : (s.LmaxMw > 0 ? s.LmaxMw / 1000.0 : 105);
        bool asleep = vo.GpuShown == St.ASLEEP;
        // spec 11.3: asleep = empty track and the reference marker only; the "watts you're missing" zone only when the GPU
        // row is actually held back by power (not on an asleep GPU, not on the barrel at light load)
        double lim = asleep ? -1 : d.L > 0 ? d.L : (!onAC && refs.LbatW > 0 ? refs.LbatW : -1);
        bool gpuMissing = vo.GpuShown is St.BATTERY_LIMITED or St.CHARGER_LIMITED || (vo.GpuShown == St.LIMITED_ON_AC && vo.GpuShownSub == Sub.ModeFw);
        var gpuBar = new BarModel(lmax, asleep ? 0 : (d.Nv && d.HaveP ? d.P : -1), false, lim, !asleep && d.L <= 0 && lim > 0, d.L > 0 ? "limit" : "learned",
            refs.LrefW > 0 ? refs.LrefW : -1, asleep, !asleep && !(d.Nv && d.HaveP), "gpu",
            asleep ? "NVIDIA GPU asleep (0 W)" : d.Nv ? $"GPU {Wording.Watts(d.P)} of a {Wording.Watts(d.L)} limit" + (refs.LrefW > 0 ? $"; ~{Wording.Watts(refs.LrefW)} on the 230 W charger" : "") : "GPU watts are read only while it is awake and in use",
            gpuMissing);
        var gpuModel = new RowModel(true, "GPU", gw.Numbers, gw.Word, gw.Kind, gw.Glyph, gw.Reason, gpuBar);

        RowModel supModel = RowModel.Hidden;
        if (SupplyTable.RowVisible(s, sup))
        {
            string kind = SupplyTable.IsAmber(sup) ? "warn" : sup is SupplyState.CHARGING or SupplyState.FULL ? "accent" : "info";
            string nums = supplyEst > 0 ? (s.AdapterW > 0 ? $"~{Wording.W0(supplyEst)} / {Wording.Watts(s.AdapterW)}" : $"~{Wording.Watts(supplyEst)}") : s.AdapterW > 0 ? Wording.Watts(s.AdapterW) : "--";
            string reason = sup switch
            {
                SupplyState.CANT_KEEP_UP => Wording.DrainSentence(s.DrainW),
                SupplyState.AT_ITS_LIMIT => $"Charging {Wording.Watts(Math.Max(0, s.RateMw / 1000.0))} vs ~{Wording.Watts(barrelBand / 1000.0)} on the 230 W charger at this charge (learned)",
                SupplyState.NOT_CHARGING => "Plugged in but not charging",
                _ => s.Class == SupplyClass.UsbC ? $"The whole laptop shares {s.AdapterW} W (Razer EC)" : "",
            };
            // the 230 W reference marker means nothing on the 230 W barrel itself, and "watts you're missing" only shows while amber
            double supRef = s.Class == SupplyClass.Barrel || (s.AdapterW > 0 && s.AdapterW >= s.RecW) ? -1 : 230;
            var supBar = new BarModel(240, supplyEst, true, s.AdapterW > 0 ? s.AdapterW * 0.92 : -1, false, "~usable", supRef, false, supplyEst <= 0, "supply",
                $"Estimated supply {(supplyEst > 0 ? "~" + Wording.Watts(supplyEst) : "unknown")}" +
                (s.AdapterW > 0 ? $"; ~usable {Wording.Watts(s.AdapterW * 0.92)} of {Wording.Watts(s.AdapterW)} (Razer EC)" : "; the Razer EC has not identified the charger") +
                (supRef > 0 ? "; 230 W charger for reference" : ""),
                SupplyTable.IsAmber(sup));
            supModel = new RowModel(true, "Supply", nums, SupplyTable.Word(sup), kind, kind == "warn" ? "cap" : "plug", reason, supBar);
        }

        // cause chips: Heat > Charger > Battery > Battery profile > Razer mode > Windows, at most 4
        var chips = new List<ChipModel>();
        bool gpuHot = vo.GpuShown == St.THERMAL || (vo.GpuShown == St.LIMITED_ON_AC && vo.GpuShownSub == Sub.Hot);
        if (gpuHot) chips.Add(new ChipModel($"Hot · GPU {(d.T >= 0 ? d.T : (int)(s.EcGpuC ?? 0))} °C", true));
        else if (cpu.Code == CpuCode.C3_Hot) chips.Add(new ChipModel($"Hot · CPU {(s.EcCpuC ?? 0):F0} °C", true));
        if (onAC && s.Class == SupplyClass.UsbC)
            chips.Add(new ChipModel($"{Wording.UsbC(usbcOk)} · {s.AdapterW} W (Razer EC)", cpu.Code == CpuCode.C4_Charger || vo.Shown == St.CHARGER_LIMITED || SupplyTable.IsAmber(sup)));
        if (s.BatterySaver) chips.Add(new ChipModel("Battery saver on", false));
        if (!onAC && _settings.BatteryServicesStopped.Count > 0) chips.Add(new ChipModel("NVIDIA service stopped (saves ~20 W)", false));
        if (s.ProfileEngaged)
            chips.Add(new ChipModel($"Battery profile · CPU {Wording.BoostName(_settings.BatteryCpuBoost, true)} · GPU {Wording.BoostName(_settings.BatteryGpuBoost, false)}" +
                                    (_settings.BatteryRefreshHz > 0 ? $" · {_settings.BatteryRefreshHz} Hz" : ""),
                                    (vo.Shown == St.BATTERY_LIMITED && vo.ShownSub is Sub.Power or Sub.Clock) || cpu.Code == CpuCode.C6_Profile));
        if (vo.Shown == St.LIMITED_ON_AC && vo.ShownSub == Sub.ModeFw) chips.Add(new ChipModel($"Razer {s.RazerMode}", true));
        if (cap.Ceiling > 0)
            chips.Add(new ChipModel(onAC ? $"Windows cap · CPU {cap.Pct}%{(cap.BoostOff ? " · boost off" : "")}" : $"Windows battery cap · CPU {cap.Pct}%{(cap.BoostOff ? " · boost off" : "")}", cpu.Code == CpuCode.C5_Windows));
        // at most 4: the current causes first (stable order otherwise), so the amber cause is never the one dropped
        if (chips.Count > 4) chips = chips.OrderByDescending(c => c.Cause).Take(4).ToList();

        // battery line + sparkline
        string batLine = _battery.Line(now, s, useW);
        string winEst = BatteryModel.WindowsEstimate(s);
        return new PowerView
        {
            Header = header, Strip = strip, Flow = flow, Cpu = cpuModel, Gpu = gpuModel, Supply = supModel, Chips = chips,
            Spark = Spark(now), BatteryLine = batLine, BatteryTip = winEst.Length > 0 ? winEst : "ETA curve: " + (_battery.CurveSource == "GG" ? "GPU Glance's learned charge curve" : _battery.CurveSource == "BladeCtl" ? "BladeCtl's learned charge curve" : "charge model (80% knee)"),
            HealthLine = BatteryModel.HealthLine(s),
            Footer = "Battery: Windows · CPU: Intel RAPL · GPU: NVIDIA, only while awake · charger: Razer EC · verdict: " + _verdictSource,
        };
    }

    /// <summary>"~45 min" / "~1 h 05" for the flow diagram: the unit is dropped only where the hour form keeps one.</summary>
    private static string CompactDuration(double sec)
    {
        string t = BatteryEta.FmtDuration(sec);
        return t.Contains(" h ") ? t.Replace(" min", "") : t;
    }

    /// <summary>What the card shows the moment it becomes Live again, until the first Live tick (spec 5.1).</summary>
    private PowerView ReadingView()
    {
        var reading = new RowModel(true, "", "reading…", "", "none", "", "", BarModel.Empty);
        return new PowerView
        {
            Header = new HeaderModel("unknown", "Reading…", "Waiting for the first reading", false, ""),
            Strip = new StripModel("grey", "unknown", "Reading the power state…", "", false, null, ""),
            Flow = null, Cpu = reading with { Name = "CPU" }, Gpu = reading with { Name = "GPU" }, Supply = RowModel.Hidden,
            Chips = Array.Empty<ChipModel>(), Spark = Spark(Now), BatteryLine = "", BatteryTip = "Waiting for the first reading", HealthLine = "",
            Footer = "Battery: Windows · CPU: Intel RAPL · GPU: NVIDIA, only while awake · charger: Razer EC",
        };
    }

    private SparkModel Spark(double now)
    {
        List<HistPt> pts;
        lock (_histGate) pts = _hist.Where(h => now - h.T <= 600).ToList();
        if (pts.Count == 0) return SparkModel.Empty;
        var outp = new List<SparkPt>(pts.Count);
        bool? prevAC = null;
        foreach (var h in pts)
        {
            int kind = h.W >= 0 ? 0 : h.OnAC ? 2 : 1;
            outp.Add(new SparkPt(Math.Round(now - h.T, 1), Math.Round(h.W, 1), kind, h.Closed, prevAC.HasValue && prevAC.Value != h.OnAC));
            prevAC = h.OnAC;
        }
        double maxAbs = pts.Max(p => Math.Abs(p.W));
        double range = Math.Max(10, Math.Ceiling(maxAbs / 10) * 10);
        double hi = pts.Max(p => p.W), lo = pts.Min(p => p.W);
        string rt = $"{(hi >= 0 ? "+" : "−")}{Wording.W0(Math.Abs(hi))} / {(lo >= 0 ? "+" : "−")}{Wording.W0(Math.Abs(lo))} W";
        return new SparkModel(outp, range, rt, $"battery rate, last 10 minutes, from {(lo >= 0 ? "+" : "−")}{Wording.W0(Math.Abs(lo))} to {(hi >= 0 ? "+" : "−")}{Wording.W0(Math.Abs(hi))} W");
    }

    // ---------- dump / heartbeat ----------

    public string HeartbeatSuffix
    {
        get
        {
            var s = _last;
            if (s == null) return "";
            string src = !s.OnAC ? "DC" : s.Class switch { SupplyClass.Barrel => "Barrel" + s.AdapterW, SupplyClass.UsbC => "UsbC" + s.AdapterW, _ => s.Class.ToString() };
            return $" src={src} rate={(s.RateKnown ? (s.RateMw >= 0 ? "+" : "") + (s.RateMw / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "W" : "?")} soc={(s.Soc >= 0 ? s.Soc.ToString() : "?")} gpuD={s.DState}";
        }
    }

    /// <summary>The power: lines for the dump and Copy diagnostics (spec 14). No process names.</summary>
    public IEnumerable<string> DumpLines()
    {
        var s = _last; var vo = _lastVo; var d = vo?.D;
        var inv = CultureInfo.InvariantCulture;
        if (s == null) { yield return $"power: no sample yet live={(_live ? 1 : 0)} learned: {_store.Summary()}"; yield break; }
        string F(double v, string f = "F1") => v.ToString(f, inv);
        var cls = ClassNow();
        double eta = _battery.EtaSec(Now, s);
        yield return $"power: mode={(_verdictSource == "GPU Glance" ? "S" : "O")} live={(_live ? 1 : 0)} paused={(_paused ? 1 : 0)} src={s.Class} {(s.AdapterW > 0 ? s.AdapterW + "W" : "")}(ec {_lvl:X2}/{_rec:X2} @{_ecReadLocal:HH:mm:ss}) ac={(s.OnAC ? 1 : 0)} " +
                     $"soc={s.Soc} rate={(s.RateKnown ? (s.RateMw >= 0 ? "+" : "") + s.RateMw + "mW" : "?")} ema={(_battery.EmaW is double e ? (e >= 0 ? "+" : "") + F(e) + "W" : "?")} " +
                     $"eta={(eta > 0 ? F(eta / 60, "F0") + "m" : "-")}(curve={_battery.CurveSource}) notChg={(s.NotChargingOnAC ? 1 : 0)} drain={(s.DrainingOnAC ? 1 : 0)} supply={_lastSupply} saver={(s.BatterySaver ? 1 : 0)} " +
                     $"full={s.FullMwh} design={s.DesignMwh} winEst={(s.WinEstSec > 0 ? s.WinEstSec / 60 + "m" : "-")} gg={_gg.IniState}";
        var caps = _caps;
        var cap = WindowsCap(s);
        yield return $"power-cpu: pkg={F(s.PkgW)}W counter={F(_lastPowerCounterW)}W pp0={F(s.Pp0W)} pp1={F(s.Pp1W)} f={F(s.FreqMHz, "F0")}MHz nominal={F(s.NominalMHz, "F0")} util={F(s.UtilityPct, "F0")}% allCore={F(s.AllCorePct, "F0")}% " +
                     $"perfLim={F(s.PerfLimitPct, "F0")} flags={F(s.PerfLimitFlags, "F0")} caps=AC {caps?.AcMaxPct}%/boost{caps?.AcBoost} DC {caps?.DcMaxPct}%/boost{caps?.DcBoost} " +
                     $"modes=AC {caps?.AcMode} {caps?.AcModeGuid} DC {caps?.DcMode} {caps?.DcModeGuid} ceilingNow={(cap.Ceiling > 0 ? F(cap.Ceiling, "F0") + "MHz" : "none")} " +
                     $"{PowerDump.FgFields(s, _lastCpu?.Code, d)} cpuC={s.EcCpuC?.ToString("F0", inv) ?? "?"} fans={_fan1?.ToString() ?? "?"}/{_fan2?.ToString() ?? "?"}rpm";
        yield return $"power-gpu: D={s.DState} luid={(_luid is { } l ? $"0x{((ulong)(uint)l.High << 32 | l.Low):X}" : "?")} disp={(s.DisplayKnown ? (s.DisplayOnD ? 1 : 0).ToString() : "?")} busy={F(s.DBusy, "F0")}% holders={s.DMemHolders}(lastD0 {_lastD0Holders}) wakes={d?.WakeEdges.ToString() ?? "?"} " +
                     $"nv={_nvml.State} gate={(s.Gate ? 1 : 0)}/{(s.GateWanted ? 1 : 0)} err={_nvml.LastError} {(_nvml.FailReason.Length > 0 ? "fail='" + _nvml.FailReason + "' " : "")}" +
                     (d is { Nv: true } ? $"P={F(d.P)} L={F(d.L)} Ldef={F(d.Ldef, "F0")} Lmax={F(d.Lmax, "F0")} U={F(d.U, "F0")} clk={F(d.Clk, "F0")} reasons=0x{d.ReasonsOr:X} d74={F(d.Duty74, "F2")} d269={F(d.Duty269, "F2")}{(d.Alias269 ? "(alias)" : "")} d270={F(d.Duty270, "F2")} d271={F(d.Duty271, "F2")} brake={F(d.FracBrake, "F2")} 0x20with0x4={d.With04And20}/{d.With04} src={d.Psrc} T={d.T} " : "") +
                     $"raw={vo?.Raw.ToString() ?? "-"} shown={vo?.Shown.ToString() ?? "-"} gpuRow={vo?.GpuShown.ToString() ?? "-"} drv={_nvml.Driver} slow={_nvml.TslowC} target={_nvml.TtargetC} calls={_nvml.Calls} maxCall={F(_nvml.MaxCallMs, "F1")}ms batch={F(_nvml.LastBatchMs, "F2")}ms init={F(_nvml.InitMs, "F0")}ms svc={s.NvSvc}";
        yield return "power-learn: " + _store.Summary();
        var hist = _store.EcHistory();
        yield return $"power-ec: lastB7={(_lastB7 is byte b7 ? b7.ToString("X2") : "?")} burst={(_burstActive ? 1 : 0)} misses={_ecMisses} lastAnswer={(_lastEcAnswerAt > -1e8 ? F(Now - _lastEcAnswerAt, "F0") + "s ago" : "never")} usbcConfirmed={(UsbcConfirmed ? 1 : 0)} state={_pub.State} history=[" +
                     string.Join(", ", hist.AsEnumerable().Reverse().Take(6).Select(h => $"{(h.Utc.Length >= 16 ? h.Utc[11..16] + "Z" : h.Utc)} {h.Lvl:X2}/{h.Rec:X2} {(h.Ac ? "ac" : "dc")}")) + "]";
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _burstGen);
        lock (_tickLock) { if (!_probe) { try { _pub.FlushNow(); } catch { } } }   // let a tick in progress finish, then write a held-back change
        try { _burst?.Dispose(); } catch { }
        try { _nvml.Dispose(); } catch { }
        try { FlushLearned(); } catch { }
        _cpu.Dispose(); _gpuPdh.Dispose(); _bat.Dispose(); _fg.Dispose();
    }
}
