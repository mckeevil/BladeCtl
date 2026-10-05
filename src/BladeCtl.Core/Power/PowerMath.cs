namespace BladeCtl.Core.Power;

/// <summary>Small pure rules shared by the sampler and the tests.</summary>
public static class PowerMath
{
    /// <summary>
    /// NVML gate (spec 4.5, identical to GPU Glance SPEC 5.2 with the flyout replaced by Live): only with the dGPU in D0,
    /// not paused, and only when something is already using it, a display hangs off it, or the window is open on AC.
    /// </summary>
    public static bool GateWanted(int dstate, bool paused, double dBusyPdh, bool displayOnD, bool onAC, bool live) =>
        dstate == 1 && !paused && (dBusyPdh >= 2.0 || displayOnD || (onAC && live));

    /// <summary>GPU watts for the stacked bar: P when read, 0 when the GPU is not in D0, otherwise unknown (-1).</summary>
    public static double GpuW(PowerSample s, Derived? d) =>
        s.DState != 1 ? 0 : (d is { Nv: true, HaveP: true } ? d.P : -1);

    /// <summary>
    /// Whole-laptop watts (spec 6). On battery it is exact (-Rate). On AC it is an estimate (pkg + GPU + learned rest),
    /// only when the GPU watts are known, the built-in panel is on (lid closed hides it, F24) and a rest value is learned.
    /// </summary>
    public static double UseW(PowerSample s, double gpuW, double restW)
    {
        if (!s.OnAC) return s.RateKnown && s.RateMw < 0 ? -s.RateMw / 1000.0 : -1;
        if (gpuW < 0 || restW <= 0 || !s.PanelOn || s.PkgW < 0) return -1;
        return s.PkgW + gpuW + restW;
    }

    /// <summary>"~supply" on AC: what the laptop uses plus what goes into the battery.</summary>
    public static double SupplyEst(PowerSample s, double useW) =>
        !s.OnAC || useW <= 0 ? -1 : useW + Math.Max(s.RateKnown ? s.RateMw / 1000.0 : 0, 0);

    /// <summary>Rest of the laptop on battery (L5 input): -Rate - pkg, only with the GPU off.</summary>
    public static double RestOnBattery(PowerSample s) =>
        !s.OnAC && s.DState != 1 && s.RateKnown && s.RateMw < 0 && s.PkgW >= 0 ? -s.RateMw / 1000.0 - s.PkgW : -1;
}

/// <summary>
/// Live / closed cadence (spec 5). Live: every monitor tick. Closed: inside whatever tick fires first once 30 s have
/// passed since the last closed sample. Nothing else is scheduled.
/// </summary>
public sealed class SampleCadence
{
    public const double ClosedEvery = 30.0;
    private double _lastClosed = -1e9;
    public enum Kind { None, Live, Closed }

    /// <summary>Would <see cref="Decide"/> sample now? No side effects (the monitor asks before reading the D-state on battery).</summary>
    public bool WouldSample(bool live, double now) => live || now - _lastClosed >= ClosedEvery - 0.05;

    public double LastClosed => _lastClosed;

    public Kind Decide(bool live, double now)
    {
        if (live) return Kind.Live;
        if (now - _lastClosed >= ClosedEvery - 0.05) { _lastClosed = now; return Kind.Closed; }
        return Kind.None;
    }

    /// <summary>Closed samples on battery need D0 on two consecutive closed samples before NVML is touched (spec 4.5).</summary>
    public int ConsecutiveClosedD0 { get; private set; }
    public void NoteClosedD(int dstate) => ConsecutiveClosedD0 = dstate == 1 ? ConsecutiveClosedD0 + 1 : 0;
}
