namespace BladeCtl.Core.Power;

/// <summary>
/// GPU verdict (spec 7.2): GPU Glance's R0-R10 (SPEC 6.2, src\verdict.cpp VerdictEngine::Push as built 2026-10-05) ported
/// 1:1, with BladeCtl's deltas:
///  D1 field 269 is ignored when it mirrors field 74 (driver 616.64 reports them identical, which would make every
///     power-capped load THERMAL). Field 269 is the duration counter of reason bit 0x20, so while it mirrors 74 the 0x20
///     bit counts as thermal only in samples without 0x4 (0x40, the hardware slowdown bit, always counts); whether 0x20
///     co-occurs with 0x4 is still counted for the dump (A1-6);
///  D2 brake terms ignore samples within 5 s after an AC/DC or charger-class change;
///  D3 Lref and the CPU reference come from BladeCtl's learned data (handed in through <see cref="GpuRefs"/>). The CPU
///     reference is per load shape, not per app, so R10's package-watts comparison also needs the clock below 90% of
///     the reference (a lighter app at full turbo is not "slowed");
///  D4 charger / profile / Windows clauses are wording only and never change the state.
/// Pure: no Win32, so the fixtures replay exactly the code the app runs. Only live samples are pushed.
/// </summary>
public sealed class GpuVerdict
{
    private const double Eps = 0.01;
    private readonly List<PowerSample> _win = new();
    private bool _havePrev, _prevAC = true; private uint _prevInit; private double _prevT;
    private double _acdcAt = -1e9, _flipAt = -1e9, _classAt = -1e9;
    private double _lowUseSince = -1, _src1Since = -1, _cappedSince = -1;
    // wake episodes on battery with no display; only those that ended with nothing using the GPU count (GG SPEC 22.2)
    private int _prevD = -1;
    private readonly Queue<double> _wakes = new();
    private bool _epOpen, _epIdle; private double _epStart;
    public Hysteresis Main { get; } = new();
    public Hysteresis GpuOnly { get; } = new();

    public double AcDcAt => _acdcAt;

    private void ResetWakes() { _wakes.Clear(); _prevD = -1; _epOpen = false; }

    /// <summary>Resume (GPU Glance OnResume): the window starts empty after sleep; pre-sleep wakes do not count.</summary>
    public void OnResume()
    {
        _win.Clear();
        _lowUseSince = _src1Since = _cappedSince = -1; ResetWakes();
    }

    /// <summary>Charger class changed without an AC/DC flip: clear the window and restart hysteresis as for an AC/DC event.</summary>
    public void OnClassChange(double now)
    {
        _win.Clear(); _flipAt = now; _classAt = now; _lowUseSince = _src1Since = _cappedSince = -1;
        Main.AcDcPending = GpuOnly.AcDcPending = true;
    }

    public VerdictOut Push(PowerSample s, GpuRefs lv)
    {
        var o = new VerdictOut(); var d = o.D;
        double now = s.T; bool onAC = s.OnAC;
        // A long gap (window was closed): the window, the continuous trackers and the wake history are stale, and so is the
        // shown state (the hysteresis seeds a calm state again). An AC/DC change that happened while the window was closed
        // is not a fresh event: take its time from the trackers, which see every sample.
        if (_havePrev && now - _prevT > 10)
        {
            _win.Clear(); ResetWakes(); _lowUseSince = _src1Since = _cappedSince = -1; Main.Reset(); GpuOnly.Reset();
            _prevAC = onAC;
            if (s.AcDcAt > _acdcAt && s.AcDcAt <= now) _acdcAt = s.AcDcAt;
        }
        if (_havePrev && onAC != _prevAC)
        {
            _win.Clear(); _acdcAt = now; _flipAt = now; Main.AcDcPending = GpuOnly.AcDcPending = true;
            _lowUseSince = _src1Since = _cappedSince = -1; ResetWakes();
        }
        if (_havePrev && s.NvInitSeq != _prevInit) _win.Clear();
        if (s.FlipAt > _flipAt) _flipAt = s.FlipAt;
        _prevAC = onAC; _prevInit = s.NvInitSeq; _havePrev = true; _prevT = now;
        if (now - _acdcAt > 15 && now - _flipAt > 15) { Main.AcDcPending = false; GpuOnly.AcDcPending = false; }
        Main.AcDcAt = GpuOnly.AcDcAt = Math.Max(_acdcAt, _classAt);
        _win.Add(s);
        while (_win.Count > 0 && now - _win[0].T >= 5.5) _win.RemoveAt(0);
        // "keeps waking with nothing to do" (GG SPEC 22.2): a D3 -> D0 edge on battery with no display starts an episode;
        // it counts only if, until the GPU went back to D3, PDH stayed under 2% and no process held dGPU memory
        {
            bool batNoDisp = !onAC && !s.DisplayOnD;
            if (s.DState == 1)
            {
                if (_prevD >= 0 && _prevD != 1 && batNoDisp) { _epOpen = true; _epIdle = true; _epStart = now; }
                if (_epOpen && (!batNoDisp || s.DBusy >= 2.0 || s.DMemHolders > 0)) _epIdle = false;
            }
            else if (_epOpen)
            {
                if (_epIdle) _wakes.Enqueue(_epStart);
                _epOpen = false;
            }
            _prevD = s.DState;
            while (_wakes.Count > 0 && now - _wakes.Peek() > 120) _wakes.Dequeue();
        }
        d.WakeEdges = _wakes.Count;
        d.OnBattery = !onAC; d.Display = s.DisplayOnD; d.DBusy = s.DBusy;
        d.NvState = s.NvState; d.NvErr = s.NvErr; d.NvRetryIn = s.NvRetryIn;
        d.Tslow = s.TslowC > 0 ? s.TslowC : 98;
        d.Ttarget = s.TtargetC > 0 ? s.TtargetC : -1;
        d.DrainingOnAC = s.DrainingOnAC; d.DrainW = s.DrainW;

        // ---- CPU side: 6 s means (spec 6) ----
        {
            double c = 0, pk = 0, fr = 0, ac = 0, hot = 0; int nc = 0, np = 0, nf = 0, na = 0, nh = 0;
            double pmin = double.MaxValue, pmax = double.MinValue;
            foreach (var w in _win)
            {
                if (w.FgKnown && w.FgPid == s.FgPid) { c += w.FgCores; nc++; }
                if (w.CpuOk && w.PkgW >= 0) { pk += w.PkgW; np++; pmin = Math.Min(pmin, w.PkgW); pmax = Math.Max(pmax, w.PkgW); if (d.PkgFirst < 0) d.PkgFirst = w.PkgW; d.PkgLast = w.PkgW; }
                if (w.CpuOk && w.FreqMHz >= 0) { fr += w.FreqMHz; nf++; }
                if (w.CpuOk && w.AllCorePct >= 0) { ac += w.AllCorePct; na++; }
                if (w.EcCpuC is double t) { nh++; if (t >= 97) hot++; }
            }
            d.FgCores = nc > 0 ? c / nc : 0; d.PkgW = np > 0 ? pk / np : -1; d.Freq = nf > 0 ? fr / nf : -1; d.AllCore = na > 0 ? ac / na : -1;
            d.PkgRange = np > 0 ? pmax - pmin : 0; d.TcpuHotFrac = nh > 0 ? hot / nh : 0;
            d.PkgFell = np >= 2 && d.PkgFirst - d.PkgLast >= 3;
            bool fgOk = s.FgKnown && !s.FgPrivate;
            d.Busy1T = d.FgCores >= 0.85 && fgOk && !s.FgSelf && !s.FgShell;
            d.BusyMT = d.AllCore >= 70 && fgOk && d.FgCores >= 0.5 * (d.AllCore / 100.0 * Math.Max(1, s.LogicalCpus));
            d.CpuBusy = d.Busy1T || d.BusyMT;
            d.Plateau = np > 0 && d.PkgRange <= 3 && d.PkgW >= 15;
        }

        // ---- NVML side ----
        var nvs = _win.Where(w => w.NvOk).ToList();
        if (s.NvOk && nvs.Count > 0)
        {
            d.Nv = true; d.N = nvs.Count;
            var a = nvs[0]; var b = nvs[^1];
            d.Span = b.T - a.T;
            bool backwards = false;
            if (nvs.Count >= 2 && d.Span > 0.5)
            {
                bool Duty(PowerSample x, PowerSample y, int f, out double outv, out double deltaNs)
                {
                    outv = 0; deltaNs = -1;
                    if (!(x.FOk[f] && y.FOk[f])) return false;
                    double dc = y.FVal[f] - x.FVal[f];
                    if (dc < 0) { backwards = true; return false; }
                    double span = y.T - x.T;
                    if (span <= 0) return false;
                    deltaNs = dc;
                    outv = Math.Clamp(dc / (span * 1e9), 0, 1);
                    return true;
                }
                bool any = false;
                any |= Duty(a, b, Nv.F_CAP74, out d.Duty74, out var d74ns);
                any |= Duty(a, b, Nv.F_BOARD77, out d.Duty77, out _);
                any |= Duty(a, b, Nv.F_LOWUTIL78, out d.Duty78, out _);
                any |= Duty(a, b, Nv.F_SWTH269, out d.Duty269, out var d269ns);
                any |= Duty(a, b, Nv.F_HWTH270, out d.Duty270, out _);
                // D2: brake duty only over samples at least 5 s after the last AC/DC or charger-class change
                var a2 = nvs.FirstOrDefault(w => w.T >= _flipAt + 5);
                if (a2 != null && b.T - a2.T > 0.5) any |= Duty(a2, b, Nv.F_BRAKE271, out d.Duty271, out _);
                d.HaveDuty = any;
                d.D74Ms = d74ns >= 0 ? d74ns / 1e6 : -1; d.D269Ms = d269ns >= 0 ? d269ns / 1e6 : -1;
                // D1: field 269 mirrors 74 on driver 616.64
                d.Alias269 = d74ns >= 0 && d269ns >= 0 && Math.Abs(d269ns - d74ns) <= 1e6;
                if (a.FOk[Nv.F_ENERGY] && b.FOk[Nv.F_ENERGY])
                {
                    double de = b.FVal[Nv.F_ENERGY] - a.FVal[Nv.F_ENERGY];   // mJ
                    if (de < 0) backwards = true; else { d.P = de / d.Span / 1000.0; d.HaveP = true; }
                }
            }
            if (backwards)
            {
                _win.RemoveRange(0, _win.Count - 1);
                d.HaveDuty = false; d.HaveP = false; d.Alias269 = false;
                d.Duty74 = d.Duty77 = d.Duty78 = d.Duty269 = d.Duty270 = d.Duty271 = 0;
                nvs.Clear(); nvs.Add(_win[^1]); d.N = 1; d.Span = 0;
            }
            if (!d.HaveP)
            {
                double sum = 0; int n = 0;
                foreach (var w in nvs) if (w.FOk[Nv.F_PAVG]) { sum += w.FVal[Nv.F_PAVG]; n++; }
                if (n > 0) { d.P = sum / n / 1000.0; d.HaveP = true; }
            }
            var L = nvs[^1];
            if (L.FOk[Nv.F_LENF]) d.L = L.FVal[Nv.F_LENF] / 1000.0;
            if (L.FOk[Nv.F_LDEF]) d.Ldef = L.FVal[Nv.F_LDEF] / 1000.0;
            if (L.FOk[Nv.F_LMAX]) d.Lmax = L.FVal[Nv.F_LMAX] / 1000.0;
            else if (s.LmaxMw > 0) d.Lmax = s.LmaxMw / 1000.0;
            double u = 0, cl = 0; int nu = 0, ncl = 0, nr = 0, th = 0, thStrict = 0, br = 0, nrb = 0, id = 0, pw = 0;
            foreach (var w in nvs)
            {
                if (w.UtilOk) { u += w.Util; nu++; }
                if (w.ClkOk) { cl += w.GfxClk; ncl++; }
                if (w.ReasonsOk)
                {
                    nr++; d.ReasonsOr |= w.Reasons;
                    bool hw = (w.Reasons & Nv.RB_HWTHERM) != 0, sw = (w.Reasons & Nv.RB_SWTHERM) != 0, swPower = (w.Reasons & Nv.RB_SWPOWER) != 0;
                    if (hw || sw) th++;
                    if (hw || (sw && !swPower)) thStrict++;   // D1: 0x20 next to 0x4 is not counted while 269 mirrors 74
                    if (w.T >= _flipAt + 5) { nrb++; if ((w.Reasons & Nv.RB_BRAKE) != 0) br++; }   // D2
                    if ((w.Reasons & Nv.RB_IDLE) != 0) id++;
                    if (swPower) { pw++; d.With04++; if (sw) d.With04And20++; }
                }
            }
            d.U = nu > 0 ? u / nu : 0; d.Clk = ncl > 0 ? cl / ncl : 0;
            if (nr > 0) { d.FracThermal = (double)(d.Alias269 ? thStrict : th) / nr; d.FracIdle = (double)id / nr; d.FracPower = (double)pw / nr; }
            if (nrb > 0) d.FracBrake = (double)br / nrb;
            d.T = L.TempC; d.PState = L.PState; d.Psrc = L.PowerSource;
        }

        // ---- trackers beyond the window ----
        bool lowNow = s.NvOk && d.Nv ? d.U < 10 : s.DBusy < 2.0;
        if (s.DState == 1 && d.OnBattery && !s.DisplayOnD && lowNow) { if (_lowUseSince < 0) _lowUseSince = now; }
        else _lowUseSince = -1;
        d.LowUseSec = _lowUseSince >= 0 ? now - _lowUseSince : 0;
        bool src1Now = onAC && s.NvOk && s.PowerSource == 1;
        if (src1Now) { if (_src1Since < 0) _src1Since = now; } else _src1Since = -1;
        int src2 = nvs.Count(w => w.PowerSource == 2);

        // ---- Lref (D3: BladeCtl's learned data, resolved by the caller) ----
        if (lv.LrefW > 0) { d.Lref = lv.LrefW; d.LrefLearned = lv.LrefLearned; }
        else d.Lref = d.Ldef;

        // ---- raw verdict, first match wins (GPU Glance SPEC 6.2) ----
        St raw = St.UNKNOWN; Sub sub = Sub.None; double held = -1;
        bool wakeLoop = d.OnBattery && !s.DisplayOnD && d.WakeEdges >= 5;
        Sub KeptSub() => wakeLoop ? Sub.WakeLoop : Sub.None;
        if (!s.Present || !s.Started || s.Problem) raw = St.GPU_MISSING;                                        // R0
        else if (s.DState != 1 && wakeLoop) { raw = St.KEPT_AWAKE; sub = Sub.WakeLoop; }
        else if (s.DState != 1) raw = St.ASLEEP;                                                                 // R1
        else if (!s.Gate)                                                                                        // R2
        {
            if (s.GateWanted && (s.NvState == NvState.FAILED || s.NvState == NvState.BACKOFF)) { raw = St.UNKNOWN; sub = Sub.NvError; }
            else if (d.OnBattery && !s.DisplayOnD && d.LowUseSec >= 120 - Eps) { raw = St.KEPT_AWAKE; sub = KeptSub(); held = _lowUseSince; }
            else if (wakeLoop) { raw = St.KEPT_AWAKE; sub = Sub.WakeLoop; }
            else raw = St.AWAKE_IDLE;
        }
        else if (!s.NvOk || !d.Nv) { raw = St.UNKNOWN; sub = s.NvState == NvState.READY ? Sub.NvError : Sub.NotReady; }   // R3
        else
        {
            // D1: duty269 counts only when it is not a copy of duty74
            bool thermal = d.Duty270 > 0 || (!d.Alias269 && d.Duty269 > 0.05) || d.FracThermal >= 0.5 || (d.T >= 0 && d.T >= d.Tslow - 5);
            Sub supply = Sub.None;
            if (onAC)
            {
                if (src2 >= 2) supply = Sub.Undersized;
                else if (_src1Since >= 0 && now - _src1Since >= 10 - Eps) supply = Sub.Source1;
                else if (d.DrainingOnAC) supply = Sub.Draining;
                else if (d.Duty271 > 0.01 || d.FracBrake >= 0.5) supply = Sub.Brake;
            }
            bool batSupply = !onAC && (d.Duty271 > 0.01 || d.FracBrake >= 0.5);
            d.Busy = d.U >= 60 && d.Duty78 < 0.5;
            d.Capped = d.HaveP && d.L > 0 && ((d.Duty74 >= 0.5 && d.P >= 0.85 * d.L) || d.P >= d.L - 5);
            if (thermal) raw = St.THERMAL;                                                                       // R4
            else if (supply != Sub.None) { raw = St.CHARGER_LIMITED; sub = supply; }                             // R5
            else if (batSupply) { raw = St.BATTERY_LIMITED; sub = Sub.Supply; }
            else if (!d.Busy)                                                                                    // R6
            {
                if (d.OnBattery && !s.DisplayOnD && d.LowUseSec >= 120 - Eps) { raw = St.KEPT_AWAKE; sub = KeptSub(); held = _lowUseSince; }
                else raw = St.LIGHT;
            }
            else if (d.Capped)                                                                                   // R7 + R8
            {
                if (d.OnBattery)
                {
                    if (d.Lref > 0 && d.Lref - d.L >= 5 - Eps) { raw = St.BATTERY_LIMITED; sub = Sub.Power; }
                    else { raw = St.FULL_POWER; sub = Sub.OnBattery; }
                }
                else
                {
                    if (d.Ldef > 0 && d.L < d.Ldef - 3) { raw = St.LIMITED_ON_AC; sub = (d.Ttarget > 0 && d.T >= d.Ttarget - 2) ? Sub.Hot : Sub.ModeFw; }
                    else raw = St.FULL_POWER;
                }
            }
            else                                                                                                 // R9
            {
                if (d.OnBattery && lv.ClkRefMHz > 0 && d.Clk > 0 && d.Clk < 0.8 * lv.ClkRefMHz && d.U >= 90) { raw = St.BATTERY_LIMITED; sub = Sub.Clock; }
                else raw = St.WORKING;
            }
        }
        if (d.Busy && d.Capped) { if (_cappedSince < 0) _cappedSince = now; } else _cappedSince = -1;
        d.CappedSec = _cappedSince >= 0 ? now - _cappedSince : 0;

        // GPU-only state (the GPU row, and what immediacy is decided on), before the CPU overlay
        St gRaw = raw; Sub gSub = sub; double gHeld = held;

        // ---- R10 CPU overlay (BladeCtl D3: learned AC reference for a single-thread load, not per app) ----
        if (raw is St.ASLEEP or St.AWAKE_IDLE or St.KEPT_AWAKE or St.LIGHT or St.WORKING or St.FULL_POWER)
        {
            if (d.OnBattery && d.Busy1T && lv.CpuRefValid)
            {
                bool fq = d.Freq > 0 && d.Freq < 0.8 * lv.CpuRefMHz;
                bool pk = d.PkgW >= 0 && d.PkgW <= lv.CpuRefW - 5 && d.Freq > 0 && lv.CpuRefMHz > 0 && d.Freq < 0.9 * lv.CpuRefMHz;
                if (pk || fq) { raw = St.CPU_LIMITED_BATTERY; sub = pk ? Sub.CpuPkg : Sub.CpuFreq; held = -1; }
            }
        }

        // the calm state to show when an AC/DC event ends an attention state while raw is a different attention state
        // (GPU Glance verdict.cpp, same rule), and its GPU-only twin for the GPU row
        St gCalm; Sub gCalmSub = Sub.None;
        if (d.Nv && s.NvOk) { gCalm = d.Busy ? (d.Capped ? St.FULL_POWER : St.WORKING) : St.LIGHT; if (gCalm == St.FULL_POWER && d.OnBattery) gCalmSub = Sub.OnBattery; }
        else gCalm = s.DState == 1 ? St.AWAKE_IDLE : St.ASLEEP;
        St calm = gCalm; Sub calmSub = gCalmSub;
        if (raw != gRaw) { calm = Hysteresis.IsAttention(gRaw) ? St.AWAKE_IDLE : gRaw; calmSub = Hysteresis.IsAttention(gRaw) ? Sub.None : gSub; }

        bool changed = Main.Push(raw, sub, gRaw, calm, calmSub, now, held);
        GpuOnly.Push(gRaw, gSub, gRaw, gCalm, gCalmSub, now, gHeld);
        o.Raw = raw; o.RawSub = sub; o.Shown = Main.Shown; o.ShownSub = Main.ShownSub; o.ShownSince = Main.ShownSince;
        o.GpuShown = GpuOnly.Shown; o.GpuShownSub = GpuOnly.ShownSub; o.Changed = changed; o.AcDcAt = _acdcAt;
        o.Reading = Main.HoldingSeed;
        return o;
    }
}
