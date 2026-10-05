namespace BladeCtl.Core.Power;

// BladeCtl 2.7.0 Power card: shared types. Pure data, no Win32, so the verdict code can be replayed in tests.
// St / Sub use the SAME names and numbering as GPU Glance src\common.h, so a state number means the same thing in both apps.

public enum St
{
    UNKNOWN, GPU_MISSING, ASLEEP, AWAKE_IDLE, KEPT_AWAKE, LIGHT, WORKING, FULL_POWER,
    BATTERY_LIMITED, CPU_LIMITED_BATTERY, LIMITED_ON_AC, CHARGER_LIMITED, THERMAL,
}

public enum Sub
{
    None, Power, Clock, Supply,            // BATTERY_LIMITED
    OnBattery,                             // FULL_POWER (battery)
    Hot, ModeFw,                           // LIMITED_ON_AC
    Undersized, Source1, Draining, Brake,  // CHARGER_LIMITED
    WakeLoop, Service,                     // KEPT_AWAKE flavours
    SamplerDead, NotReady, NvError, NotOptimus,   // UNKNOWN flavours (NotOptimus: GPU Glance only, kept for the numbering)
    CpuPkg, CpuFreq,                       // CPU_LIMITED_BATTERY: which comparison fired
}

public enum NvState { UNLOADED = 0, LOADED, READY, BACKOFF, FAILED }

/// <summary>Where the power comes from, as the Razer EC reports it (Windows calls both chargers "AC").</summary>
public enum SupplyClass { Battery, Settling, AcUnknown, Barrel, UsbC }

/// <summary>Supply row states S1-S8 (spec 7.1). Informational, never a verdict state.</summary>
public enum SupplyState { BATTERY, IDENTIFYING, CANT_KEEP_UP, AT_ITS_LIMIT, CHARGING, FULL, NOT_CHARGING, PLUGGED_IN }

/// <summary>CPU row rules C0-C9 (spec 7.3).</summary>
public enum CpuCode { C0_NotRead, C1_Private, C2_Idle, C3_Hot, C4_Charger, C5_Windows, C6_Profile, C7_Battery, C8_Learning, C9_Free }

public static class Nv
{
    // NVML field ids in batch order (spec 4.5). GPU Glance also reads 79; BladeCtl's spec lists these eleven.
    public const int F_ENERGY = 0, F_PAVG = 1, F_LMAX = 2, F_LDEF = 3, F_LENF = 4, F_CAP74 = 5, F_BOARD77 = 6, F_LOWUTIL78 = 7,
                     F_SWTH269 = 8, F_HWTH270 = 9, F_BRAKE271 = 10, F_COUNT = 11;
    public static readonly uint[] FieldIds = { 83, 185, 188, 189, 190, 74, 77, 78, 269, 270, 271 };

    // clock-event reason bits
    public const ulong RB_IDLE = 0x1, RB_APPCLK = 0x2, RB_SWPOWER = 0x4, RB_HWSLOW = 0x8, RB_SYNC = 0x10,
                       RB_SWTHERM = 0x20, RB_HWTHERM = 0x40, RB_BRAKE = 0x80;

    public static string ErrName(int r) => r switch
    {
        1 => "NVML not initialised", 2 => "invalid argument", 3 => "not supported", 4 => "no permission", 6 => "not found",
        7 => "buffer too small", 9 => "driver not loaded", 10 => "timeout", 15 => "GPU is lost", 16 => "GPU reset required",
        18 => "driver and nvml.dll versions differ", 999 => "unknown error", _ => "error",
    };
}

/// <summary>
/// One tick's worth of readings. Built by the sampler (or a test fixture) and not changed after it is pushed into
/// the verdict, the trackers and learning. Defaults mean "not read".
/// </summary>
public sealed record PowerSample
{
    public double T { get; set; }                 // monotonic seconds
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public bool Live { get; set; }                // window open: verdicts are computed only for live samples
    // dGPU devnode (wake-free)
    public bool Present { get; set; } = true;
    public bool Started { get; set; } = true;
    public bool Problem { get; set; }
    public int ProblemCode { get; set; }
    public int DState { get; set; }              // 1 = D0 ... 4 = D3; 0 = unreadable (counts as asleep)
    public bool DisplayKnown { get; set; }
    public bool DisplayOnD { get; set; }
    // PDH, GPU (only while D0)
    public bool GpuPdh { get; set; }
    public double DBusy { get; set; }
    public int DMemHolders { get; set; } = -1;
    // PDH, CPU
    public bool CpuOk { get; set; }
    public double PkgW { get; set; } = -1;
    public double FreqMHz { get; set; } = -1;
    public double AllCorePct { get; set; } = -1;
    public double UtilityPct { get; set; } = -1;
    public double PerfLimitPct { get; set; } = -1;
    public double PerfLimitFlags { get; set; } = -1;
    public double NominalMHz { get; set; } = -1;
    public double Pp0W { get; set; } = -1;
    public double Pp1W { get; set; } = -1;
    public int LogicalCpus { get; set; } = 16;
    // foreground app (no names, ever)
    public bool FgKnown { get; set; }
    public bool FgPrivate { get; set; }
    public bool FgSelf { get; set; }
    public bool FgShell { get; set; }
    public int FgPid { get; set; }
    public double FgCores { get; set; }
    // Windows power
    public bool OnAC { get; set; } = true;
    public bool BatterySaver { get; set; }
    // battery IOCTL
    public bool BatOk { get; set; }
    public uint BatState { get; set; }
    public bool RateKnown { get; set; }
    public int RateMw { get; set; }
    public uint CapMwh { get; set; }
    /// <summary>False when the battery did not report its remaining capacity (IOCTL failure, 0xFFFFFFFF): SOC is then unknown, never 0%.</summary>
    public bool CapKnown { get; set; } = true;
    public uint FullMwh { get; set; }
    public uint DesignMwh { get; set; }
    public int SocPct { get; set; } = -1;
    public int WinEstSec { get; set; } = -1;
    // NVML
    public NvState NvState { get; set; }
    public bool GateWanted { get; set; }
    public bool Gate { get; set; }
    public bool NvRan { get; set; }
    public bool NvOk { get; set; }
    public int NvErr { get; set; }
    public uint NvInitSeq { get; set; }
    public double NvRetryIn { get; set; }
    public bool[] FOk { get; set; } = new bool[Nv.F_COUNT];
    public double[] FVal { get; set; } = new double[Nv.F_COUNT];
    public bool ReasonsOk { get; set; }
    public ulong Reasons { get; set; }
    public bool UtilOk { get; set; }
    public uint Util { get; set; }
    public bool ClkOk { get; set; }
    public uint GfxClk { get; set; }
    public int TempC { get; set; } = -1;
    public int PState { get; set; } = -1;
    public int PowerSource { get; set; } = -1;
    public int TslowC { get; set; } = -1;
    public int TtargetC { get; set; } = -1;
    public uint LmaxMw { get; set; }
    public string Driver { get; set; } = "";
    // Razer EC
    public double? EcCpuC { get; set; }
    public double? EcGpuC { get; set; }
    public int? FanRpm1 { get; set; }             // real speed (0x0D/0x88), not the 0x81 setpoint
    public int? FanRpm2 { get; set; }
    public string RazerMode { get; set; } = "?";
    public int? CpuBoost { get; set; }
    public int? GpuBoost { get; set; }
    public SupplyClass Class { get; set; } = SupplyClass.AcUnknown;
    public int AdapterW { get; set; }
    public int RecW { get; set; } = 230;
    // BladeCtl state
    public bool ProfileEngaged { get; set; }
    public bool FullPowerUntilAc { get; set; }
    public int NvSvc { get; set; } = -1;          // -1 unknown, 0 stopped, 1 running, 2 other
    public bool PanelOn { get; set; }
    public int PanelHz { get; set; }
    // trackers (filled by SupplyTrackers before the sample is pushed anywhere)
    public bool DrainingOnAC { get; set; }
    public double DrainW { get; set; }
    public bool NotChargingOnAC { get; set; }
    public double NotChargingSince { get; set; } = -1;
    public double DrainSince { get; set; } = -1;
    public double SinceFlip { get; set; } = 1e9;   // seconds since an AC/DC or charger-class change
    public double SinceAcDc { get; set; } = 1e9;
    public double FlipAt { get; set; } = -1e9;     // monotonic time of the last flip (AC/DC or class)
    public double AcDcAt { get; set; } = -1e9;

    public bool OnBattery => !OnAC;
    public double SocFrac => CapKnown && FullMwh > 0 ? (double)CapMwh / FullMwh : (SocPct >= 0 ? SocPct / 100.0 : -1);
    public int Soc => SocPct >= 0 ? SocPct : (CapKnown && FullMwh > 0 ? (int)Math.Round(100.0 * CapMwh / FullMwh) : -1);
    public int? MaxFanRpm => FanRpm1.HasValue || FanRpm2.HasValue ? Math.Max(FanRpm1 ?? 0, FanRpm2 ?? 0) : null;
}

/// <summary>Learned references handed to the verdict (spec 7.2 D3). Filled by the sampler from PowerLearn, or by a test.</summary>
public sealed record GpuRefs
{
    public double LrefW { get; init; } = -1;          // reference AC limit for the battery comparison
    public bool LrefLearned { get; init; }
    public string LrefDate { get; init; } = "";
    public double BarrelLacW { get; init; } = -1;     // learned barrel Lac for the saved AC mode (for USB-C wording)
    public double LbatW { get; init; } = -1;
    public string LbatDate { get; init; } = "";
    public double ClkRefMHz { get; init; } = -1;      // L2: barrel, saved mode, profile off
    public bool CpuRefValid { get; init; }            // L3 refAC for a single-thread load
    public double CpuRefW { get; init; }
    public double CpuRefMHz { get; init; }
    public string CpuRefDate { get; init; } = "";
    public bool CpuRefOld { get; init; }
}

/// <summary>Window-derived values (spec 6). A port of GPU Glance's Derived plus BladeCtl's CPU and D1/D2 fields.</summary>
public sealed class Derived
{
    public bool Nv; public int N; public double Span;
    public bool HaveP; public double P;
    public double L = -1, Ldef = -1, Lmax = -1;
    public bool HaveDuty;
    public double Duty74, Duty77, Duty78, Duty269, Duty270, Duty271;
    public bool Alias269; public double D269Ms = -1, D74Ms = -1;
    public int With04, With04And20;                     // D1: does 0x20 co-occur with 0x4?
    public double U, Clk; public int T = -1, PState = -1, Psrc = -1;
    public double FracThermal, FracBrake, FracIdle, FracPower; public ulong ReasonsOr;
    // CPU side
    public double FgCores, PkgW = -1, Freq = -1, AllCore = -1, PkgRange, PkgFirst = -1, PkgLast = -1, TcpuHotFrac;
    public bool Busy1T, BusyMT, CpuBusy, Plateau, PkgFell;
    // supply / power
    public bool OnBattery, Display, DrainingOnAC; public double DrainW;
    public double LowUseSec; public int WakeEdges;
    public bool Busy, Capped; public double CappedSec;
    public double Tslow = 98, Ttarget = -1, Lref = -1; public bool LrefLearned;
    public double DBusy; public int NvErr; public double NvRetryIn; public NvState NvState;
}

public sealed class VerdictOut
{
    public St Raw = St.UNKNOWN; public Sub RawSub;
    public St Shown = St.UNKNOWN; public Sub ShownSub; public double ShownSince;
    public St GpuShown = St.UNKNOWN; public Sub GpuShownSub;   // GPU-only (before R10), own hysteresis
    public bool Changed;
    /// <summary>The shown UNKNOWN is the first-tick "reading" hold of an attention state (window just opened).</summary>
    public bool Reading;
    public double AcDcAt = -1e9;
    public Derived D = new();
}
