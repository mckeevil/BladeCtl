namespace BladeCtl.Core.Power;

/// <summary>
/// The learning rules of spec 9 (L1-L5), applied to one sample. Pure apart from writing into <see cref="PowerLearn"/>,
/// so the tests can count writes. Never learns within 10 s of an AC/DC flip, a charger-class change or a resume, never
/// from a private foreground app (L2, L3), and battery samples only ever land in DC keys.
/// </summary>
public sealed class PowerLearner
{
    public PowerLearn Store { get; }
    public int Writes { get; private set; }
    /// <summary>CPU-side writes only (L2 clock + L3 CPU): a private foreground app must leave this at 0.</summary>
    public int CpuWrites { get; private set; }
    private double _resumeAt = -1e9;

    public PowerLearner(PowerLearn store) { Store = store; }

    public void MarkResume(double now) => _resumeAt = now;

    /// <summary>Single-sample CPU flags for a closed sample (no window).</summary>
    public static (bool Busy1T, bool BusyMT) CpuFlags(PowerSample s)
    {
        bool fgOk = s.FgKnown && !s.FgPrivate;
        bool b1 = s.FgCores >= 0.85 && fgOk && !s.FgSelf && !s.FgShell;
        bool bm = s.AllCorePct >= 70 && fgOk && s.FgCores >= 0.5 * (s.AllCorePct / 100.0 * Math.Max(1, s.LogicalCpus));
        return (b1, bm);
    }

    /// <param name="d">window-derived values for a live sample, or null for a closed sample</param>
    /// <param name="shown">displayed GPU state for a live sample (null when closed)</param>
    /// <param name="dt">seconds this sample stands for (2 s live, ~30 s closed)</param>
    public void Learn(PowerSample s, Derived? d, St? shown, double dt)
    {
        double now = s.T;
        if (s.SinceFlip < 10 || now - _resumeAt < 10) return;
        string src = RazerAdapter.SrcKey(s.OnAC ? s.Class : SupplyClass.Battery, s.AdapterW);
        if (s.OnAC && s.Class is SupplyClass.Settling) return;
        string mode = s.RazerMode;
        string bst = PowerKeys.Bst(mode, s.CpuBoost, s.GpuBoost);
        string prof = PowerKeys.Prof(s.ProfileEngaged);
        bool live = d != null;

        // L1: GPU enforced limit
        if (s.NvOk && s.FOk[Nv.F_LENF] && shown is not St.UNKNOWN)
        {
            double u = live ? d!.U : (s.UtilOk ? s.Util : -1);
            bool thermal = live ? shown == St.THERMAL
                                : (s.ReasonsOk && (s.Reasons & Nv.RB_HWTHERM) != 0) || (s.TempC >= 0 && s.TempC >= (s.TslowC > 0 ? s.TslowC : 98) - 5);
            bool brake = live ? (d!.Duty271 > 0.01 || d.FracBrake > 0) : (s.ReasonsOk && (s.Reasons & Nv.RB_BRAKE) != 0);
            if (u >= 80 && !thermal && !brake && !(s.OnAC && s.DrainingOnAC))
            {
                int weight = live ? 1 : (int)Math.Clamp(Math.Round(dt / 2.0), 1, 15);
                Store.AddGpuLimit(PowerKeys.Gpu(s.Driver, src, mode, bst, prof), s.FVal[Nv.F_LENF] / 1000.0, weight); Writes++;
            }
        }

        // L2: graphics clock (live only: needs the displayed state)
        if (live && shown is St.WORKING or St.FULL_POWER && d!.U >= 50 && d.Clk > 0 && !s.FgPrivate)
        { Store.LearnGClk(PowerKeys.GClk(s.Driver, src, mode, bst, prof), d.Clk, dt); Writes++; CpuWrites++; }

        // L3: CPU package watts + MHz per load shape
        {
            bool b1, bm; double pkg, mhz;
            if (live) { b1 = d!.Busy1T; bm = d.BusyMT; pkg = d.PkgW; mhz = d.Freq; }
            else { (b1, bm) = CpuFlags(s); pkg = s.PkgW; mhz = s.FreqMHz; }
            if ((b1 || bm) && !s.FgPrivate && s.CpuOk && pkg >= 0)
            { Store.LearnCpu(PowerKeys.Cpu(src, mode, bst, prof, b1), pkg, mhz, dt); Writes++; CpuWrites++; }
        }

        // L4: charge power by SOC band
        int soc = s.Soc;
        if (s.OnAC && s.RateKnown && s.RateMw >= 1000 && s.SinceFlip >= 30 && soc >= 0 && soc < 100 && s.FullMwh > 0)
        { Store.LearnChg(src, Math.Min(BatteryEta.Bins - 1, (int)(s.SocFrac * BatteryEta.Bins)), s.RateMw); Writes++; }

        // L5: rest of the laptop (battery, GPU off)
        if (s.SinceFlip >= 30)
        {
            double rest = PowerMath.RestOnBattery(s);
            if (rest >= 0) { Store.LearnRest(PowerKeys.Rest(s.PanelOn, s.PanelHz), rest, dt); Writes++; }
        }
    }
}

/// <summary>The one button the strip may show (spec 11.6). Buttons act only when clicked.</summary>
public static class PowerButtons
{
    public const string Fans = "fans5000", Gaming = "gaming", FullPower = "fullpower";

    public static string? Decide(St st, Sub sub, bool onAC, int? fanRpmReal, bool profileEngaged, CpuCode? cpu, string liveMode,
                                 double cappedSec, double gamingLacW, double balancedLacW)
    {
        if (st == St.THERMAL && onAC && !(fanRpmReal >= 4900)) return Fans;
        if (st == St.LIMITED_ON_AC && sub == Sub.ModeFw && onAC && cappedSec >= 30 && liveMode == "Balanced" &&
            gamingLacW > 0 && balancedLacW > 0 && gamingLacW >= balancedLacW + 5) return Gaming;
        if (!onAC && profileEngaged &&
            ((st == St.BATTERY_LIMITED && sub is Sub.Power or Sub.Clock) || cpu == CpuCode.C6_Profile)) return FullPower;
        return null;
    }

    public static string Label(string? id) => id switch
    {
        Fans => "Fans to 5000",
        Gaming => "Switch to Gaming",
        FullPower => "Full power until I plug in",
        _ => "",
    };
}
