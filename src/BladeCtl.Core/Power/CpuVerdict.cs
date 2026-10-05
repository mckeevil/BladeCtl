namespace BladeCtl.Core.Power;

/// <summary>Inputs for the CPU row (spec 7.3). References are learned CPU data for the same load shape (1T or MT).</summary>
public sealed record CpuInputs
{
    public bool CpuOk { get; init; }
    public bool FgPrivate { get; init; }
    public Derived D { get; init; } = new();
    public bool OnAC { get; init; } = true;
    public SupplyClass Class { get; init; }
    public int AdapterW { get; init; }
    public bool DrainingOnAC { get; init; }
    public bool ProfileEngaged { get; init; }
    public string BatteryCpuBoostName { get; init; } = "Low";
    /// <summary>Barrel, saved AC mode, profile off.</summary>
    public CpuEntry? RefAC { get; init; }
    /// <summary>Current class, live mode.</summary>
    public CpuEntry? RefSrc { get; init; }
    /// <summary>Windows ceiling for the current source in MHz, or &lt;= 0 when Windows does not cap.</summary>
    public double CeilingMHz { get; init; } = -1;
    public int CapPct { get; init; } = 100;
    public bool BoostOff { get; init; }
    public double? CpuC { get; init; }
    public int? FanRpm { get; init; }
    /// <summary>Test B1 has confirmed the dock-only reading is USB-C; until then the reason says "USB-C?".</summary>
    public bool UsbcConfirmed { get; init; } = true;
}

public sealed record CpuRowOut(CpuCode Code, string Word, string Kind, string Reason, bool Probably, string Numbers);

/// <summary>
/// CPU row, first match wins (spec 7.3). It feeds the main verdict only through R10; temperature alone never counts.
/// The references are per load shape (every single-thread app pooled), not per app as in GPU Glance, so a package-watts
/// shortfall only counts with frequency evidence too (clock under 90% of the reference): a lighter app at full turbo
/// draws fewer watts without being slowed.
/// </summary>
public static class CpuVerdict
{
    public static CpuRowOut Evaluate(CpuInputs x)
    {
        var d = x.D;
        string nums = d.PkgW >= 0 ? Wording.Watts(d.PkgW) : "--";
        if (!x.CpuOk || d.PkgW < 0)
            return new(CpuCode.C0_NotRead, "NOT READ", "info", "CPU counters unavailable", false, "--");
        if (x.FgPrivate)   // C1: numbers only, no chip, no reason
            return new(CpuCode.C1_Private, "", "none", "", false, d.Freq > 0 ? $"{Wording.Watts(d.PkgW)} · {Wording.GHz(d.Freq)}" : nums);
        if (!d.CpuBusy)
            return new(CpuCode.C2_Idle, "IDLE", "info", "", false, nums);

        // C3: hot (probably)
        bool srcSlow = x.RefSrc is { MHz: > 0 } rs && d.Freq > 0 && d.Freq < 0.9 * rs.MHz;
        if (d.TcpuHotFrac >= 0.5 && (srcSlow || d.PkgFell))
        {
            string fans = x.FanRpm is int f ? $"; fans {f.ToString("N0", Wording.Inv)} of 5,000 RPM (real speed)" : "";
            string t = x.CpuC is double c ? $"CPU at {c:F0} °C" : "CPU at its temperature limit";
            return new(CpuCode.C3_Hot, "HOT", "warn", t + fans, true, nums);
        }

        var refAC = x.RefAC;
        bool refOk = refAC is { W: > 0 };
        bool slowVsRef = refOk && d.Freq > 0 && refAC!.MHz > 0 && d.Freq < 0.9 * refAC.MHz;
        bool pkgShort = refOk && d.PkgW <= refAC!.W - 5 && slowVsRef;
        string usbc = Wording.UsbC(x.UsbcConfirmed);
        // C4: held back by a USB-C charger
        if (x.OnAC && x.Class == SupplyClass.UsbC && ((d.Plateau && pkgShort) || x.DrainingOnAC))
        {
            if (pkgShort)
                return new(CpuCode.C4_Charger, "HELD BACK", "warn",
                    $"On {usbc} ({x.AdapterW} W, Razer EC): {Wording.Watts(d.PkgW)} vs ~{Wording.Watts(refAC!.W)} on the 230 W charger ({PowerLearn.LearnedLabel(refAC.Updated)})", false, nums);
            return new(CpuCode.C4_Charger, "HELD BACK", "warn", $"Probably held back by the {usbc} charger: the battery is draining while plugged in", true, nums);
        }
        // C5: the user's Windows power setting
        if (x.CeilingMHz > 0 && d.Freq > 0 && d.Freq >= 0.95 * x.CeilingMHz)
            return new(CpuCode.C5_Windows, "HELD BACK", "warn", WindowsCapText(x.CapPct, x.BoostOff, x.CeilingMHz, !x.OnAC), false, nums);
        // C6: the battery profile
        if (!x.OnAC && x.ProfileEngaged && pkgShort)
            return new(CpuCode.C6_Profile, "HELD BACK", "warn",
                $"Battery profile: Razer Custom, CPU {x.BatteryCpuBoostName}. Plugged in: ~{Wording.Watts(refAC!.W)} ({PowerLearn.LearnedLabel(refAC.Updated)})", false, nums);
        // C7: the battery itself
        if (!x.OnAC && !x.ProfileEngaged && refOk && (pkgShort || (d.Freq > 0 && refAC!.MHz > 0 && d.Freq < 0.8 * refAC.MHz)))
            return new(CpuCode.C7_Battery, "HELD BACK", "warn",
                $"On battery: {Wording.Watts(d.PkgW)}, {Wording.GHz(d.Freq)} vs ~{Wording.Watts(refAC!.W)}, {Wording.GHz(refAC.MHz)} plugged in ({PowerLearn.LearnedLabel(refAC.Updated)})", false, nums);
        // C8: nothing to compare against yet
        if ((!x.OnAC || x.Class == SupplyClass.UsbC) && !refOk)
            return new(CpuCode.C8_Learning, "LEARNING", "info", "No plugged-in reference for this load yet", false, nums);
        return new(CpuCode.C9_Free, "FREE", "ok", d.Freq > 0 ? $"{Wording.Watts(d.PkgW)} at {Wording.GHz(d.Freq)}" : Wording.Watts(d.PkgW), false, nums);
    }

    /// <summary>"Windows caps the CPU at 80% on battery (about 1.8 GHz, boost off)".</summary>
    public static string WindowsCapText(int capPct, bool boostOff, double ceilingMHz, bool onBattery)
    {
        string where = onBattery ? "on battery" : "plugged in";
        string about = ceilingMHz > 0 ? $"about {Wording.GHz(ceilingMHz)}" : "";
        if (capPct < 100)
            return $"Windows caps the CPU at {capPct}% {where} ({about}{(boostOff ? ", boost off" : "")})";
        return $"Windows turns CPU boost off {where} ({about})";
    }

    /// <summary>Windows ceiling in MHz (spec 4.4): nominal x max state when the state is under 100% or boost is off; else none.</summary>
    public static double Ceiling(double nominalMHz, int maxStatePct, int boostMode)
    {
        if (nominalMHz <= 0) return -1;
        if (maxStatePct < 100 || boostMode == 0) return nominalMHz * Math.Clamp(maxStatePct, 1, 100) / 100.0;
        return -1;
    }
}
