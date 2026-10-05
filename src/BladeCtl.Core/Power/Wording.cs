using System.Globalization;

namespace BladeCtl.Core.Power;

/// <summary>Everything the wording needs about one Live tick. No process names exist anywhere in here.</summary>
public sealed record WordCtx
{
    public St St { get; init; }
    public Sub Sub { get; init; }
    public Derived D { get; init; } = new();
    public GpuRefs Lv { get; init; } = new();
    public bool OnAC { get; init; } = true;
    public SupplyClass Class { get; init; }
    public int AdapterW { get; init; }
    public int RecW { get; init; } = 230;
    public SupplyState Supply { get; init; } = SupplyState.PLUGGED_IN;
    public string LiveMode { get; init; } = "?";
    public int? LiveGpuBoost { get; init; }
    public bool ProfileEnabled { get; init; } = true;
    public bool ProfileEngaged { get; init; }
    public string BatteryCpuBoostName { get; init; } = "Low";
    public string BatteryGpuBoostName { get; init; } = "Low";
    public CpuRowOut? Cpu { get; init; }
    public int DcMaxPct { get; init; } = 100;
    public bool DcBoostOff { get; init; }
    public int? FanRpm { get; init; }
    public int NvSvc { get; init; } = -1;
    public int DMemHolders { get; init; } = -1;
    public bool Elevated { get; init; } = true;
    /// <summary>NVDisplay.ContainerLocalSystem is in the BatteryStopServices setting (default yes).</summary>
    public bool StopsService { get; init; } = true;
    public double UseW { get; init; } = -1;
    public int ProblemCode { get; init; }
    /// <summary>Test B1 has confirmed the dock-only reading is USB-C; until then sentences say "USB-C?".</summary>
    public bool UsbcConfirmed { get; init; } = true;
    /// <summary>0 none, 1 "Unplugged: reading the new limit…", 2 "Plugged in: …" (first 4 s after AC/DC).</summary>
    public int AcDcBanner { get; init; }
}

/// <summary>
/// Every sentence the Power card prints (spec 11.4), kept here so tests can assert exact text. Status sentences are GPU
/// Glance's (SPEC 10.1) with app names removed; numbers are formatted the GPU Glance way (W without decimals, MHz with a
/// thousands separator, GHz with one decimal).
/// </summary>
public static class Wording
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string W0(double w) => Math.Max(0, w).ToString("F0", Inv);
    public static string Watts(double w) => W0(w) + " W";
    public static string MHz(double m) => Math.Round(m).ToString("N0", Inv) + " MHz";
    public static string GHz(double mhz) => (mhz / 1000.0).ToString("F1", Inv) + " GHz";
    public static string Pct(double f) => (f * 100).ToString("F0", Inv);

    public static bool IsCalm(St s) => s is St.LIGHT or St.WORKING or St.FULL_POWER or St.ASLEEP or St.AWAKE_IDLE;
    public static bool CpuHeld(CpuCode c) => c is CpuCode.C3_Hot or CpuCode.C4_Charger or CpuCode.C5_Windows or CpuCode.C6_Profile or CpuCode.C7_Battery;

    /// <summary>No process holds dGPU memory (NVIDIA's service and BladeCtl excluded by the sampler), the NVIDIA display
    /// service is running, on battery: the service is what keeps the GPU awake (GPU Glance ServiceCase).</summary>
    private static bool ServiceCase(WordCtx c) => c.DMemHolders == 0 && c.NvSvc == 1 && !c.OnAC;

    /// <summary>Why BladeCtl did not stop the NVIDIA display service (the 2.6.1 battery fix).</summary>
    private static string ServiceClause(WordCtx c) =>
        !c.Elevated ? "BladeCtl isn't running as administrator, so it can't stop that service."
        : !c.ProfileEnabled ? "The battery profile is off, so BladeCtl leaves that service running."
        : !c.StopsService ? "It isn't in BladeCtl's BatteryStopServices setting, so BladeCtl leaves it running."
        : "BladeCtl tried to stop it on battery; the log has the result.";

    /// <summary>"USB-C" once test B1 confirmed it, "USB-C?" until then (spec 8.1).</summary>
    public static string UsbC(bool confirmed) => confirmed ? "USB-C" : "USB-C?";

    /// <summary>GPU Glance DrainText as a sentence (no full stop): the drain figure is the 6 s mean; under 1 W no number.</summary>
    public static string DrainSentence(double w) =>
        w >= 1.0 ? $"The battery is draining {Watts(w)} while plugged in" : "The battery is discharging while plugged in";

    // ---------- 11.4.1 status ----------

    public static string Status(WordCtx c)
    {
        if (c.AcDcBanner == 1) return "Unplugged: reading the new limit…";
        if (c.AcDcBanner == 2) return "Plugged in: reading the new limit…";
        var d = c.D;
        switch (c.St)
        {
            case St.BATTERY_LIMITED:
                if (c.Sub == Sub.Clock) return "Plug in for full GPU speed (probably).";
                if (c.Sub == Sub.Supply) return "The battery can't supply the GPU fast enough.";
                return "Plug in for full GPU power.";
            case St.CPU_LIMITED_BATTERY: return "Plug in: the app in front is waiting on the CPU, which is slowed on battery.";
            case St.FULL_POWER:
                if (c.Sub == Sub.OnBattery) return $"GPU at its battery limit: {W0(d.P)} of {Watts(d.L)}.";
                return $"Full power: {W0(d.P)} of {Watts(d.L)}.";
            case St.WORKING: return $"GPU working: {W0(d.P)} W, {d.U.ToString("F0", Inv)}% busy, not limited.";
            case St.LIGHT: return $"GPU lightly used: {d.U.ToString("F0", Inv)}% busy, {W0(d.P)} W.";
            case St.AWAKE_IDLE: return "NVIDIA GPU is awake but idle.";
            case St.KEPT_AWAKE:
                if (c.Sub == Sub.WakeLoop) return "NVIDIA GPU keeps waking up on battery.";
                if (ServiceCase(c)) return "NVIDIA's display service is keeping the GPU awake (no app is using it).";
                return $"NVIDIA GPU kept awake on battery for {Math.Max(1, Math.Floor(d.LowUseSec / 60)).ToString("F0", Inv)} min with nothing to do.";
            case St.ASLEEP: return "NVIDIA GPU is asleep (0 W).";
            case St.LIMITED_ON_AC:
                if (c.Sub == Sub.Hot) return $"Limited while plugged in: GPU capped at {Watts(d.L)}.";
                return $"Limited while plugged in: GPU capped at {Watts(d.L)} (normally {W0(d.Ldef > 0 ? d.Ldef : 80)}–{Watts(d.Lmax > 0 ? d.Lmax : 105)}).";
            case St.CHARGER_LIMITED: return c.Sub is Sub.Undersized or Sub.Source1 ? "Charger too weak." : "Charger can't keep up.";
            case St.THERMAL: return d.T >= 0 ? $"Too hot: GPU at {d.T} °C is slowing itself down." : "Too hot: the GPU is slowing itself down.";
            case St.UNKNOWN: return "GPU status unknown.";
            case St.GPU_MISSING: return "NVIDIA GPU not available.";
            default: return "";
        }
    }

    // ---------- 11.4.1 action (base) ----------

    public static string ActionBase(WordCtx c)
    {
        if (c.AcDcBanner != 0) return "";
        var d = c.D; var lv = c.Lv;
        switch (c.St)
        {
            case St.BATTERY_LIMITED:
                if (c.Sub == Sub.Clock)
                    return $"On battery it's running at {MHz(d.Clk)}; plugged in it ran at about {MHz(lv.ClkRefMHz)}. The power limit isn't the cause, so this looks like a battery speed cap.";
                if (c.Sub == Sub.Supply) return $"The power brake engaged {Pct(Math.Max(d.Duty271, d.FracBrake))}% of the last 6 s. Plug in.";
                {
                    string t = $"On battery the GPU is capped at {Watts(d.L)} and is using all of it.";
                    if (d.Lref > 0) t += $" Plugged in it gets up to {Watts(d.Lref)}{(d.LrefLearned ? " (learned)" : "")}.";
                    return t;
                }
            case St.CPU_LIMITED_BATTERY:
                return $"CPU at {Watts(d.PkgW)}, {GHz(d.Freq)} vs about {Watts(lv.CpuRefW)}, {GHz(lv.CpuRefMHz)} plugged in ({PowerLearn.LearnedLabel(lv.CpuRefDate)}).";
            case St.FULL_POWER:
                if (c.Sub == Sub.OnBattery) return d.Lmax > 0 ? $"Plugged in it can go up to {Watts(d.Lmax)} with Dynamic Boost." : "Plugging in raises the limit.";
                return $"The limit moves between {W0(d.Ldef > 0 ? d.Ldef : 80)} and {Watts(d.Lmax > 0 ? d.Lmax : 105)} as the CPU and GPU share power. That's normal.";
            case St.WORKING: return $"It isn't at its {Watts(d.L)} limit, so more power wouldn't help right now.";
            case St.LIGHT: return c.OnAC ? "Plenty of headroom." : "Plugging in won't make the GPU faster.";
            case St.AWAKE_IDLE: return "Watts aren't read while it's idle, so BladeCtl never keeps it awake. GPU Glance shows which app is holding it.";
            case St.KEPT_AWAKE:
                if (c.Sub == Sub.WakeLoop)
                {
                    // GPU Glance checks the service before the wake count: the proven cause gets named
                    string woke = $"It woke {d.WakeEdges} times in the last 2 min";
                    if (ServiceCase(c)) return $"{woke}. No app is using it, and the NVIDIA display service is running on battery. {ServiceClause(c)}";
                    return woke + " with nothing to do.";
                }
                if (ServiceCase(c)) return ServiceClause(c);
                return "GPU Glance shows which app is holding it.";
            case St.ASLEEP:
                if (c.OnAC) return "It wakes when an app needs it.";
                if (lv.LbatW > 0) return $"If something needs the NVIDIA GPU on battery, it gets about {Watts(lv.LbatW)} ({PowerLearn.LearnedLabel(lv.LbatDate)}).";
                return "Nothing is using it, so it saves power.";
            case St.LIMITED_ON_AC:
                if (c.Sub == Sub.Hot) return $"The GPU is at {d.T} °C, its temperature target, so the laptop is holding power back. Check the vents.";
                return "Use the 230 W barrel charger and check the Razer power mode.";
            case St.CHARGER_LIMITED:
                if (c.Sub == Sub.Undersized) return "NVIDIA reports an undersized power supply. Use the 230 W barrel charger.";
                if (c.Sub == Sub.Source1) return "NVIDIA reports battery power while Windows says plugged in. Use the 230 W barrel charger.";
                if (c.Sub == Sub.Brake) return $"The GPU's power brake engaged {Pct(Math.Max(d.Duty271, d.FracBrake))}% of the last 6 s.";
                return DrainSentence(d.DrainW) + ". Use the 230 W barrel charger.";
            case St.THERMAL: return "Check the vents and what the laptop is sitting on.";
            case St.UNKNOWN:
                if (c.Sub == Sub.NotOptimus) return "This NVIDIA GPU isn't switchable (Optimus) graphics, so its power state can't be read as asleep.";
                if (d.NvErr != 0) return $"The NVIDIA driver returned error {d.NvErr} ({Nv.ErrName(d.NvErr)}). " + (d.NvRetryIn > 0 ? $"Retrying in {Math.Ceiling(d.NvRetryIn).ToString("F0", Inv)} s." : "Retrying.");
                if (d.NvState == NvState.FAILED) return "nvml.dll could not be loaded. It is retried when the GPU is re-added.";
                return "Waiting for the NVIDIA readout to start.";
            case St.GPU_MISSING: return c.ProblemCode > 0 ? $"Device Manager reports problem code {c.ProblemCode}." : "Windows doesn't list the NVIDIA GPU as working right now.";
            default: return "";
        }
    }

    // ---------- 11.4.2 BladeCtl clauses (never change the state) ----------

    public static string Action(WordCtx c)
    {
        string t = ActionBase(c);
        if (c.AcDcBanner != 0) return t;
        var d = c.D; var lv = c.Lv;
        const string useBarrel = "Use the 230 W barrel charger.";
        if (c.St == St.CHARGER_LIMITED && c.Sub == Sub.Draining && c.Class == SupplyClass.Barrel && c.AdapterW >= c.RecW)
            t = t.Replace(useBarrel, "Even the 230 W charger can't keep up with this load.");
        if (c.St == St.LIMITED_ON_AC && c.Sub == Sub.ModeFw && c.Class == SupplyClass.Barrel)
            t = $"Check the Razer power mode: it's {c.LiveMode}{(c.LiveMode == "Custom" && c.LiveGpuBoost is int gb ? $" with GPU {BoostName(gb, false)}" : "")}.";
        if ((c.St == St.CHARGER_LIMITED || (c.St == St.LIMITED_ON_AC && c.Sub == Sub.ModeFw)) && c.Class == SupplyClass.UsbC)
            t += $" Razer EC reports a {c.AdapterW} W supply (recommended {c.RecW} W).";
        if (c.St == St.BATTERY_LIMITED && c.Sub is Sub.Power or Sub.Clock && c.ProfileEngaged)
            t += $" Your battery profile holds it at GPU {c.BatteryGpuBoostName}.";
        if (c.St == St.CPU_LIMITED_BATTERY && c.Cpu?.Code == CpuCode.C5_Windows)
            t += $" Windows caps the CPU at {c.DcMaxPct}% on battery{(c.DcBoostOff ? " with boost off" : "")}.";
        if (c.St == St.CPU_LIMITED_BATTERY && c.Cpu?.Code == CpuCode.C6_Profile)
            t += $" Your battery profile holds it at CPU {c.BatteryCpuBoostName}.";
        if (c.St == St.FULL_POWER && c.Sub != Sub.OnBattery && c.Class == SupplyClass.UsbC && lv.BarrelLacW > 0 && lv.BarrelLacW - d.L >= 5)
            t += $" On {UsbC(c.UsbcConfirmed)} the limit is {Watts(d.L)} vs ~{Watts(lv.BarrelLacW)} on the 230 W charger (learned).";
        if (IsCalm(c.St) && c.Class == SupplyClass.UsbC && c.OnAC && !SupplyTable.IsAmber(c.Supply) && c.UseW > 0)
            t += $" On {UsbC(c.UsbcConfirmed)} the whole laptop shares {c.AdapterW} W (Razer EC); ~{Watts(c.UseW)} in use now.";
        if (c.St == St.THERMAL && c.FanRpm is int rpm && rpm < 4900)
            t += $" Fans {rpm.ToString("N0", Inv)} of 5,000 RPM.";
        if (IsCalm(c.St) && c.Cpu is { } cpu && CpuHeld(cpu.Code) && cpu.Reason.Length > 0)
            t = $"CPU: {cpu.Reason}.";
        return t.Trim();
    }

    /// <summary>A "probably" verdict gets a dotted outline.</summary>
    public static bool Probably(WordCtx c) =>
        (c.St == St.BATTERY_LIMITED && c.Sub == Sub.Clock) || (IsCalm(c.St) && c.Cpu is { Probably: true } cpu && CpuHeld(cpu.Code));

    /// <summary>Strip colour (spec 7.4): warn / ok / accent / grey.</summary>
    public static string StripKind(St s, Sub sub)
    {
        if (Hysteresis.IsAttention(s)) return "warn";
        if (s == St.FULL_POWER) return sub == Sub.OnBattery ? "accent" : "ok";
        if (s is St.LIGHT or St.WORKING) return "ok";
        return "grey";
    }

    public static string StripGlyph(St s) => s switch
    {
        St.THERMAL => "heat",
        St.LIMITED_ON_AC or St.BATTERY_LIMITED or St.CHARGER_LIMITED or St.CPU_LIMITED_BATTERY => "cap",
        St.KEPT_AWAKE => "unknown",
        St.ASLEEP => "sleep",
        St.UNKNOWN or St.GPU_MISSING => "unknown",
        _ => "check",
    };

    // ---------- 11.4.3 rows ----------

    public sealed record RowWord(string Word, string Kind, string Glyph, string Reason, string Numbers, bool LimitLearned);

    /// <summary>GPU row from the GPU-only state.</summary>
    public static RowWord GpuRow(St s, Sub sub, Derived d, GpuRefs lv, bool onAC, SupplyClass cls, int adapterW, bool nvData, bool usbcConfirmed = true)
    {
        (string word, string kind, string glyph) = s switch
        {
            St.ASLEEP => ("ASLEEP", "info", "sleep"),
            St.AWAKE_IDLE or St.LIGHT or St.WORKING => ("FREE", "ok", "check"),
            St.FULL_POWER => ("AT LIMIT", "accent", "cap"),
            St.BATTERY_LIMITED or St.CHARGER_LIMITED => ("HELD BACK", "warn", "cap"),
            St.LIMITED_ON_AC => sub == Sub.Hot ? ("HOT", "warn", "heat") : ("HELD BACK", "warn", "cap"),
            St.THERMAL => ("HOT", "warn", "heat"),
            St.KEPT_AWAKE => ("AWAKE", "warn", "unknown"),
            St.GPU_MISSING => ("MISSING", "info", "unknown"),
            _ => ("NOT READ", "info", "unknown"),
        };
        string nums = s == St.ASLEEP ? "asleep" : s == St.GPU_MISSING ? "--" : nvData && d.HaveP && d.L > 0 ? $"{W0(d.P)} / {Watts(d.L)}"
                    : s == St.AWAKE_IDLE || s == St.KEPT_AWAKE ? "awake" : "reading…";
        string reason;
        double ldef = d.Ldef > 0 ? d.Ldef : 80, lmax = d.Lmax > 0 ? d.Lmax : 105;
        if (onAC)
        {
            string src = cls == SupplyClass.UsbC ? $"On {UsbC(usbcConfirmed)} ({adapterW} W)" : cls == SupplyClass.Barrel ? $"Plugged in ({adapterW} W)" : "Plugged in";
            if (lv.LrefLearned && lv.LrefW > 0)
                reason = cls == SupplyClass.UsbC && lv.BarrelLacW > 0
                    ? $"{src}: limit {(d.L > 0 ? Watts(d.L) : "?")} · up to {Watts(lv.BarrelLacW)} on the 230 W charger (learned)"
                    : $"{src}: up to {Watts(lv.LrefW)} · {PowerLearn.LearnedLabel(lv.LrefDate)}";
            else reason = $"{W0(ldef)} W plugged in, up to {Watts(lmax)} with Dynamic Boost";
        }
        else
        {
            string cap = d.L > 0 ? $"{Watts(d.L)} cap" : lv.LbatW > 0 ? $"about {Watts(lv.LbatW)} cap ({PowerLearn.LearnedLabel(lv.LbatDate)})" : "cap not read yet";
            reason = lv.LrefLearned && lv.LrefW > 0 ? $"On battery: {cap} · ~{Watts(lv.LrefW)} plugged in (learned)"
                   : $"On battery: {cap} · {W0(ldef)} W plugged in, up to {Watts(lmax)} with Dynamic Boost";
        }
        return new(word, kind, glyph, reason, nums, lv.LrefLearned);
    }

    public static string BoostName(int? level, bool cpu) => level switch
    {
        0 => "Low", 1 => "Medium", 2 => "High", 3 when cpu => "Boost", null => "?", _ => level.Value.ToString(Inv),
    };

    // ---------- header ----------

    /// <summary>Header chip text for the power source (spec 8.1). <paramref name="usbcConfirmed"/> = test B1 has confirmed the class.</summary>
    public static string SourceText(bool onAC, SupplyClass cls, int adapterW, bool usbcConfirmed) => !onAC ? "Battery" : cls switch
    {
        SupplyClass.Settling => "Plugged in · identifying charger…",
        SupplyClass.AcUnknown => "Plugged in · charger not identified",
        SupplyClass.Barrel => $"{adapterW} W charger",
        SupplyClass.UsbC => usbcConfirmed ? $"USB-C · {adapterW} W" : $"USB-C? · {adapterW} W",
        _ => "Plugged in",
    };

    public static string FlowSummary(bool onAC, SupplyClass cls, int adapterW, double supplyEstW, double batW, int soc, string eta)
    {
        string src = !onAC ? "Running on battery" : cls == SupplyClass.Barrel ? $"{adapterW} W charger" : cls == SupplyClass.UsbC ? $"USB-C {adapterW} W supply" : "Plugged in";
        string sup = onAC && supplyEstW > 0 ? $" supplying about {W0(supplyEstW)} W" : "";
        string bat = batW >= 0.5 ? $"battery charging at {W0(batW)} W" : batW <= -0.5 ? $"battery {(onAC ? "draining" : "discharging")} at {W0(-batW)} W" : "battery idle";
        return $"{src}{sup}; {bat}{(soc >= 0 ? $", {soc}%" : "")}{(eta.Length > 0 ? ", " + eta.Replace("~", "about ").Replace(" h ", " hour ").Replace(" min", " minutes") : "")}";
    }
}
