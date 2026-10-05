namespace BladeCtl.Core.Power;

/// <summary>
/// Display hysteresis, ported from GPU Glance as built 2026-10-05 (SPEC 6.3, src\verdict.cpp VerdictEngine::Hysteresis):
/// immediate to/from ASLEEP, GPU_MISSING and UNKNOWN, but only when the GPU-only state itself changed (the R10 CPU overlay
/// on an asleep GPU uses the attention holds); calm states after 2 consecutive ticks; attention states enter after 6 s
/// (THERMAL 4 s) and leave after 12 s; a move to a more severe attention state takes 4 s, to a less severe one 6 s.
/// An AC/DC event leaves an attention state at once, but never straight into another attention state (that one shows the
/// calm state and starts its entry hold from the event), and a low AC limit (LIMITED_ON_AC / CHARGER_LIMITED) is not
/// trusted for the first 4 s after the event.
/// BladeCtl delta: on the first push after start or after a reset (the window was closed for more than 10 s) a calm raw
/// state shows at once, while an attention state must hold for its normal entry time (meanwhile the GPU state shows when
/// only the CPU overlay is attention, else UNKNOWN, "reading"): a single sample never raises an amber verdict on reopen.
/// </summary>
public sealed class Hysteresis
{
    private const double Eps = 0.01;

    public St Shown { get; private set; } = St.UNKNOWN;
    public Sub ShownSub { get; private set; } = Sub.None;
    public double ShownSince { get; private set; }
    /// <summary>Set by the owner on an AC/DC (or charger-class) event; consumed by the next push.</summary>
    public bool AcDcPending { get; set; }
    /// <summary>Time of the last AC/DC (or charger-class) event: a low AC limit is not trusted for 4 s after it.</summary>
    public double AcDcAt { get; set; } = -1e9;

    private St _shownGpu = St.UNKNOWN;
    private St? _cand;
    private double _candSince;
    private int _candTicks;
    private double _differSince = -1;
    private bool _seed = true, _holdingSeed;

    public static bool IsAttention(St s) => s is St.KEPT_AWAKE or St.CPU_LIMITED_BATTERY or St.BATTERY_LIMITED or St.LIMITED_ON_AC or St.CHARGER_LIMITED or St.THERMAL;
    public static bool IsImmediate(St s) => s is St.ASLEEP or St.GPU_MISSING or St.UNKNOWN;
    public static int Severity(St s) => s switch
    {
        St.THERMAL => 6, St.CHARGER_LIMITED => 5, St.LIMITED_ON_AC => 4, St.BATTERY_LIMITED => 3, St.CPU_LIMITED_BATTERY => 2, St.KEPT_AWAKE => 1, _ => 0,
    };

    /// <summary>Single-state convenience (tests): the GPU state is the raw state, and the calm fallback is AWAKE_IDLE.</summary>
    public bool Push(St raw, Sub rsub, double now, double heldSince = -1) =>
        Push(raw, rsub, raw, IsAttention(raw) ? St.AWAKE_IDLE : raw, IsAttention(raw) ? Sub.None : rsub, now, heldSince);

    /// <summary>
    /// Returns true when the shown state changed. <paramref name="gpuRaw"/> = the GPU verdict before the R10 CPU overlay;
    /// <paramref name="calm"/> = what to show when an event ends an attention state while raw is another attention state;
    /// <paramref name="heldSince"/> &gt;= 0 when the raw rule was already time-qualified.
    /// </summary>
    public bool Push(St raw, Sub rsub, St gpuRaw, St calm, Sub calmSub, double now, double heldSince = -1)
    {
        St before = Shown;
        if (_cand != raw) { _cand = raw; _candSince = now; _candTicks = 0; }
        _candTicks++;
        // first push after start / reopen: nothing is held over, and a single sample never shows an attention state. A calm
        // raw state shows at once. An attention state serves its normal entry hold, showing meanwhile the GPU state when only
        // the R10 CPU overlay is attention (that GPU state is true), else UNKNOWN ("reading"): a calm fallback there could be
        // false, e.g. FULL_POWER for a GPU stuck at 35 W on AC.
        if (_seed)
        {
            _seed = false; AcDcPending = false;
            if (IsAttention(raw) && IsAttention(gpuRaw))
            {
                Shown = St.UNKNOWN; ShownSub = Sub.NotReady; _shownGpu = gpuRaw; ShownSince = now; _differSince = now;
                _candSince = now; _candTicks = 1; _holdingSeed = true;
            }
            else if (IsAttention(raw)) ShowCalmOrRaw(raw, rsub, gpuRaw, calm, calmSub, now);
            else { Shown = raw; ShownSub = rsub; _shownGpu = gpuRaw; ShownSince = now; _differSince = -1; }
            return before != Shown;
        }
        // AC/DC event: an attention state is left at once, but never straight into another attention state
        if (AcDcPending)
        {
            AcDcPending = false;
            if (IsAttention(Shown) && raw != Shown)
            {
                ShowCalmOrRaw(raw, rsub, gpuRaw, calm, calmSub, now);
                return before != Shown;
            }
        }
        double since = _candSince;
        if (heldSince >= 0 && heldSince < since) since = heldSince;   // the raw rule itself was already time-qualified
        // "Plugged in: reading the new limit": the firmware applies the AC limit asynchronously, so a low AC limit is not
        // trusted for the first 4 s after an AC/DC change
        if ((raw is St.LIMITED_ON_AC or St.CHARGER_LIMITED) && since < AcDcAt + 4.0) since = AcDcAt + 4.0;
        double held = now - since;
        if (raw == Shown) { ShownSub = rsub; _shownGpu = gpuRaw; _differSince = -1; _holdingSeed = false; return false; }
        if (_differSince < 0) _differSince = now;
        bool sw;
        // immediate only when the GPU itself goes to / from ASLEEP, GPU_MISSING or UNKNOWN (never out of the seeded
        // "reading" state into an attention state: that one serves its hold)
        if ((IsImmediate(raw) || IsImmediate(Shown)) && gpuRaw != _shownGpu && !(_holdingSeed && IsAttention(raw))) sw = true;
        else if (IsAttention(Shown))
        {
            if (IsAttention(raw)) sw = held >= (Severity(raw) > Severity(Shown) ? 4.0 : 6.0) - Eps;
            else sw = now - _differSince >= 12.0 - Eps;                                   // recover slowly
        }
        else
        {
            if (IsAttention(raw)) sw = held >= (raw == St.THERMAL ? 4.0 : 6.0) - Eps;
            else sw = _candTicks >= 2;                                                     // calm states: 2 ticks
        }
        if (sw) { Shown = raw; ShownSub = rsub; _shownGpu = gpuRaw; ShownSince = now; _differSince = -1; _holdingSeed = false; }
        return before != Shown;
    }

    /// <summary>True while the shown UNKNOWN is the seeded "reading" state holding back an attention state (the card says "Reading…").</summary>
    public bool HoldingSeed => _holdingSeed && Shown == St.UNKNOWN;

    private void ShowCalmOrRaw(St raw, Sub rsub, St gpuRaw, St calm, Sub calmSub, double now)
    {
        bool rawCalm = !IsAttention(raw);
        Shown = rawCalm ? raw : calm; ShownSub = rawCalm ? rsub : calmSub; _shownGpu = gpuRaw; _holdingSeed = false;
        ShownSince = now; _differSince = rawCalm ? -1 : now;
        if (!rawCalm) { _candSince = now; _candTicks = 1; }
    }

    /// <summary>Window closed for more than 10 s: nothing shown is current any more, and the next push seeds a calm state.</summary>
    public void Reset()
    {
        Shown = St.UNKNOWN; ShownSub = Sub.None; _shownGpu = St.UNKNOWN; _cand = null; _candTicks = 0; _differSince = -1;
        AcDcPending = false; _seed = true; _holdingSeed = false;
    }
}

/// <summary>
/// CPU row hold (BladeCtl): a new row code is shown after 2 consecutive Live ticks, so the "CPU: …" action and the
/// "Full power until I plug in" button do not flicker when the load hovers at a threshold. Same code: shown at once.
/// </summary>
public sealed class CpuRowHold
{
    private CpuRowOut? _shown;
    private CpuCode? _cand;
    private int _n;

    public CpuRowOut Push(CpuRowOut raw)
    {
        if (_shown == null || raw.Code == _shown.Code) { _shown = raw; _cand = null; _n = 0; return raw; }
        if (_cand != raw.Code) { _cand = raw.Code; _n = 0; }
        if (++_n >= 2) { _shown = raw; _cand = null; _n = 0; }
        return _shown;
    }

    public void Reset() { _shown = null; _cand = null; _n = 0; }
}

/// <summary>
/// Supply row hysteresis (spec 7.1): a new state must hold 30 s to be shown (counted from when its own condition
/// started, so S3/S7's built-in timers are not doubled), BATTERY and IDENTIFYING are immediate, and any AC/DC or
/// charger-class change resets it, as does a gap in Live samples (the window was closed).
/// </summary>
public sealed class SupplyHysteresis
{
    public SupplyState Shown { get; private set; } = SupplyState.PLUGGED_IN;
    private SupplyState? _cand;
    private double _candSince;
    private bool _init;

    public void Reset() { _init = false; _cand = null; }

    public SupplyState Push(SupplyState raw, double now, double heldSince = -1)
    {
        if (!_init || raw is SupplyState.BATTERY or SupplyState.IDENTIFYING || Shown is SupplyState.BATTERY or SupplyState.IDENTIFYING)
        { _init = true; Shown = raw; _cand = null; return Shown; }
        if (raw == Shown) { _cand = null; return Shown; }
        if (_cand != raw) { _cand = raw; _candSince = now; }
        double since = heldSince >= 0 && heldSince < _candSince ? heldSince : _candSince;
        if (now - since >= 30 - 0.01) { Shown = raw; _cand = null; }
        return Shown;
    }
}
