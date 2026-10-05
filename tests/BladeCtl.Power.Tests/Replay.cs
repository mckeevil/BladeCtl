using System.Globalization;
using BladeCtl.Core.Power;

namespace BladeCtl.Power.Tests;

/// <summary>A fixture CSV (GPU Glance SPEC 6.4 format, plus BladeCtl columns): '#' lines are title, #learned and #expect-*.</summary>
public sealed class Fixture
{
    public string Name = "", Title = "";
    public List<Dictionary<string, string>> Rows = new();
    public Dictionary<string, string> Learned = new();
    public List<(string K, string V)> Expects = new();

    public static Fixture Parse(string name, string text)
    {
        var f = new Fixture { Name = name };
        List<string>? header = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                var body = line[1..].Trim();
                int sp = body.IndexOf(' ');
                string k = sp < 0 ? body : body[..sp], v = sp < 0 ? "" : body[(sp + 1)..].Trim();
                if (k == "learned") { foreach (var kv in v.Split(' ', StringSplitOptions.RemoveEmptyEntries)) { int eq = kv.IndexOf('='); if (eq > 0) f.Learned[kv[..eq]] = kv[(eq + 1)..]; } }
                else if (k.StartsWith("expect", StringComparison.OrdinalIgnoreCase)) f.Expects.Add((k, v));
                else if (f.Title.Length == 0) f.Title = body;
                continue;
            }
            var cells = line.Split(',');
            if (header == null) { header = cells.Select(c => c.Trim().ToLowerInvariant()).ToList(); continue; }
            var r = new Dictionary<string, string>();
            for (int i = 0; i < header.Count && i < cells.Length; i++) r[header[i]] = cells[i].Trim();
            f.Rows.Add(r);
        }
        return f;
    }

    /// <summary>Copies of GPU Glance's own fixtures (tests\fixtures\gg), replayed through BladeCtl's engine so the two cannot drift.</summary>
    public static string GgDir() => Path.Combine(Dir(), "gg");

    /// <summary>GPU Glance's own fixture folder, when it is checked out under %USERPROFILE%\Tools (the drift check compares the copies against it).</summary>
    public static readonly string GgLiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Tools", "GpuGlance", "tests", "fixtures");

    public static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures");
            if (Directory.Exists(p)) return p;
            p = Path.Combine(d.FullName, "fixtures");
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "F1.csv"))) return p;
        }
        throw new DirectoryNotFoundException("tests\\fixtures not found above " + AppContext.BaseDirectory);
    }
}

public sealed record Tick(double T, St Raw, St Shown, Sub Sub, St GpuShown, string Status, string Action, CpuRowOut Cpu,
                          SupplyState Supply, string Battery, string Source, string? Button, double UseW);

public sealed class ReplayRun
{
    public List<Tick> Ticks = new();
    public long NvmlCalls;
    public List<string> Anomalies = new();
    public PowerLearner Learner = new(new PowerLearn());
}

/// <summary>
/// Replays a fixture through exactly the Core code the app runs: SupplyTrackers -> GpuVerdict -> CpuVerdict ->
/// SupplyTable -> Wording -> PowerButtons -> PowerLearner, at a 2 s tick, emulating the NVML gate like GPU Glance's replay.
/// </summary>
public static class Replay
{
    private static double Num(Dictionary<string, string> r, string k, double def)
    {
        if (!r.TryGetValue(k, out var v) || v.Length == 0) return def;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ulong.Parse(v[2..], NumberStyles.HexNumber);
        return double.Parse(v, CultureInfo.InvariantCulture);
    }
    private static string Str(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";

    public static ReplayRun Run(Fixture f)
    {
        var R = new ReplayRun();
        var ve = new GpuVerdict(); var tr = new SupplyTrackers(); var bat = new BatteryModel(); var sh = new SupplyHysteresis(); var cpuHold = new CpuRowHold();
        const double tick = 2.0;
        double t = 1000.0;
        double energy = 5e8, c74 = 0, c77 = 0, c78 = 0, c269 = 0, c270 = 0, c271 = 0;
        var nv = NvState.UNLOADED; long calls = 0;

        double L(string k) => f.Learned.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : -1;
        double lac = L("lac"), barrel = L("barrel"), lbat = L("lbat"), band = L("band"), rest = L("rest");
        (double W, double MHz)? cpuRef = null; double clkRef = -1;
        if (f.Learned.TryGetValue("cpu", out var cr)) { var p = cr.Split(':'); if (p.Length == 3) cpuRef = (double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture)); }
        if (f.Learned.TryGetValue("gpu", out var gr)) { var p = gr.Split(':'); if (p.Length == 3) clkRef = double.Parse(p[2], CultureInfo.InvariantCulture); }
        var lv = new GpuRefs
        {
            LrefW = lac, LrefLearned = lac > 0, LrefDate = "2026-10-03", BarrelLacW = barrel > 0 ? barrel : lac,
            LbatW = lbat, LbatDate = "2026-10-03", ClkRefMHz = clkRef,
            CpuRefValid = cpuRef != null, CpuRefW = cpuRef?.W ?? 0, CpuRefMHz = cpuRef?.MHz ?? 0, CpuRefDate = "2026-10-02",
        };
        CpuEntry? refAC = cpuRef is { } c ? new CpuEntry { W = c.W, MHz = c.MHz, Secs = 600, Updated = "2026-10-02" } : null;

        foreach (var r in f.Rows)
        {
            double dur = Num(r, "dur", 2);
            for (double e = 0; e < dur - 1e-6; e += tick, t += tick)
            {
                var s = new PowerSample { T = t, Live = Num(r, "live", 0) != 0 };
                s.Present = Num(r, "missing", 0) == 0; s.Started = s.Present;
                s.DState = (int)Num(r, "d", 0) + 1;
                s.DisplayKnown = true; s.DisplayOnD = Num(r, "display", 0) != 0;
                s.OnAC = Num(r, "ac", 1) != 0;
                s.GpuPdh = true; s.DBusy = Num(r, "pdh", 0);
                s.DMemHolders = Str(r, "holders").Split('|', StringSplitOptions.RemoveEmptyEntries).Count(h => !h.StartsWith("Windows"));
                string fg = Str(r, "fg");
                if (fg.Length > 0) { s.FgKnown = true; s.FgPid = 4242; s.FgCores = Num(r, "fgcores", 0); s.FgPrivate = Num(r, "private", 0) != 0; }
                s.PkgW = Num(r, "pkg", -1); s.FreqMHz = Num(r, "freq", -1); s.AllCorePct = Num(r, "allcore", -1); s.CpuOk = s.PkgW >= 0 || s.FreqMHz >= 0;
                s.NominalMHz = Num(r, "nominal", -1);
                s.BatOk = true; s.SocPct = (int)Num(r, "bat", 60); s.FullMwh = (uint)Num(r, "full", 69400); s.DesignMwh = 80000;
                s.CapMwh = (uint)Math.Round(s.FullMwh * s.SocPct / 100.0);
                s.BatState = (uint)Num(r, "batstate", s.OnAC ? 0x1 : 0x2);
                if (Str(r, "rate").Length > 0) { s.RateKnown = true; s.RateMw = (int)Num(r, "rate", 0); }
                s.NvSvc = (int)Num(r, "svc", -1);
                s.PanelOn = Num(r, "panel", 1) != 0; s.PanelHz = (int)Num(r, "hz", 60);
                s.ProfileEngaged = Num(r, "profile", 0) != 0;
                s.RazerMode = Str(r, "mode") is { Length: > 0 } m ? m : s.ProfileEngaged ? "Custom" : "Balanced";
                if (s.ProfileEngaged) { s.CpuBoost = 0; s.GpuBoost = 0; }
                s.EcCpuC = Str(r, "cpuc").Length > 0 ? Num(r, "cpuc", 0) : null;
                if (Str(r, "fanrpm").Length > 0) s.FanRpm1 = s.FanRpm2 = (int)Num(r, "fanrpm", 0);
                // charger class: explicit column, an EC reply, or the default (barrel on AC)
                string cls = Str(r, "class").ToLowerInvariant();
                if (cls.Length > 0)
                {
                    s.Class = cls switch { "usbc" => SupplyClass.UsbC, "acu" => SupplyClass.AcUnknown, "settling" => SupplyClass.Settling, "battery" => SupplyClass.Battery, _ => SupplyClass.Barrel };
                    s.AdapterW = (int)Num(r, "adapterw", 230);
                    if (!s.OnAC) s.Class = SupplyClass.Battery;
                }
                else if (Str(r, "ec") is { Length: 5 } ec)
                {
                    byte lvl = Convert.ToByte(ec[..2], 16), rec = Convert.ToByte(ec[3..], 16);
                    var dec = RazerAdapter.Classify(s.OnAC, true, lvl, rec, Num(r, "inburst", 0) != 0);
                    s.Class = dec.Class; s.AdapterW = dec.AdapterW; s.RecW = dec.RecW;
                    if (dec.Anomaly != null) R.Anomalies.Add(dec.Anomaly);
                }
                else { s.Class = s.OnAC ? SupplyClass.Barrel : SupplyClass.Battery; s.AdapterW = s.OnAC ? 230 : 0; }

                // hardware counters keep running whether or not NVML reads them
                double P = Num(r, "p", 0), Lw = Num(r, "l", 80);
                energy += P * 1000.0 * tick;
                c74 += Num(r, "cap74", 0) * tick * 1e9; c77 += Num(r, "board77", 0) * tick * 1e9; c78 += Num(r, "low78", 0) * tick * 1e9;
                c269 += Num(r, "sw269", 0) * tick * 1e9; c270 += Num(r, "hw270", 0) * tick * 1e9; c271 += Num(r, "brake271", 0) * tick * 1e9;
                bool wanted = PowerMath.GateWanted(s.DState, false, s.DBusy, s.DisplayOnD, s.OnAC, s.Live);
                s.GateWanted = wanted; s.Gate = wanted;
                if (wanted)
                {
                    nv = NvState.READY; calls += 10;
                    s.NvRan = true; s.NvOk = true;
                    double[] vals = { energy, P * 1000, Num(r, "lmax", 105) * 1000, Num(r, "ldef", 80) * 1000, Lw * 1000, c74, c77, c78, c269, c270, c271 };
                    for (int i = 0; i < Nv.F_COUNT; i++) { s.FOk[i] = true; s.FVal[i] = vals[i]; }
                    s.ReasonsOk = true; s.Reasons = (ulong)Num(r, "reasons", 0);
                    s.UtilOk = true; s.Util = (uint)Num(r, "util", 0);
                    s.ClkOk = Num(r, "clk", 0) > 0; s.GfxClk = (uint)Num(r, "clk", 0);
                    s.TempC = (int)Num(r, "temp", 60); s.PState = 0; s.PowerSource = (int)Num(r, "psrc", s.OnAC ? 0 : 1);
                    s.TslowC = 98; s.TtargetC = 80; s.LmaxMw = 105000; s.Driver = "616.64";
                }
                else if (nv == NvState.READY) nv = NvState.LOADED;
                s.NvState = nv;

                tr.Note(s);
                if (tr.ClassChangedNow) { ve.OnClassChange(t); bat.OnPlugEvent(t); }   // the sampler's order
                bat.Update(t, s);
                var vo = ve.Push(s, lv);

                int dcmax = (int)Num(r, "dcmax", 100), dcboost = (int)Num(r, "dcboost", 3);
                var cpu = cpuHold.Push(CpuVerdict.Evaluate(new CpuInputs
                {
                    CpuOk = s.CpuOk, FgPrivate = s.FgPrivate, D = vo.D, OnAC = s.OnAC, Class = s.Class, AdapterW = s.AdapterW,
                    DrainingOnAC = s.DrainingOnAC, ProfileEngaged = s.ProfileEngaged, RefAC = refAC,
                    CeilingMHz = s.OnAC ? -1 : CpuVerdict.Ceiling(s.NominalMHz, dcmax, dcboost), CapPct = dcmax, BoostOff = dcboost == 0,
                    CpuC = s.EcCpuC, FanRpm = s.MaxFanRpm,
                }));
                var rawSup = SupplyTable.Raw(s, band, out var held);
                if (tr.FlipAt == t) sh.Reset();
                var sup = sh.Push(rawSup, t, held);
                double useW = PowerMath.UseW(s, PowerMath.GpuW(s, vo.D), rest);
                int banner = t - vo.AcDcAt < 4.0 && t >= vo.AcDcAt ? (s.OnAC ? 2 : 1) : 0;
                var ctx = new WordCtx
                {
                    St = vo.Shown, Sub = vo.ShownSub, D = vo.D, Lv = lv, OnAC = s.OnAC, Class = s.Class, AdapterW = s.AdapterW, RecW = s.RecW,
                    Supply = sup, LiveMode = s.RazerMode, LiveGpuBoost = s.GpuBoost, ProfileEngaged = s.ProfileEngaged, Cpu = cpu,
                    DcMaxPct = dcmax, DcBoostOff = dcboost == 0, FanRpm = s.MaxFanRpm, NvSvc = s.NvSvc, DMemHolders = s.DMemHolders,
                    UseW = useW, AcDcBanner = banner,
                };
                string? button = PowerButtons.Decide(vo.Shown, vo.ShownSub, s.OnAC, s.MaxFanRpm, s.ProfileEngaged, cpu.Code, s.RazerMode, vo.D.CappedSec, -1, -1);
                R.Learner.Learn(s, vo.D, vo.Shown, tick);
                R.Ticks.Add(new Tick(t - 1000.0, vo.Raw, vo.Shown, vo.ShownSub, vo.GpuShown, Wording.Status(ctx), Wording.Action(ctx), cpu, sup,
                                     bat.Line(t, s, useW), Wording.SourceText(s.OnAC, s.Class, s.AdapterW, false), button, useW));
            }
        }
        R.NvmlCalls = calls;
        return R;
    }

    /// <summary>Checks one expectation. Returns null when it holds, else the reason.</summary>
    public static string? Check(ReplayRun R, string k, string v)
    {
        var last = R.Ticks[^1];
        List<St> States(string list) => list.Split(',').Select(x => Enum.TryParse<St>(x.Trim(), out var s) ? (St?)s : null).Where(x => x != null).Select(x => x!.Value).ToList();
        switch (k)
        {
            case "expect-final": return last.Shown.ToString() == v ? null : $"final {last.Shown}";
            case "expect-final-in": return States(v).Contains(last.Shown) ? null : $"final {last.Shown}";
            case "expect-always": { var bad = R.Ticks.FirstOrDefault(x => x.Shown.ToString() != v); return bad == null ? null : $"t={bad.T} shown {bad.Shown}"; }
            case "expect-never": { var ss = States(v); var bad = R.Ticks.FirstOrDefault(x => ss.Contains(x.Shown)); return bad == null ? null : $"t={bad.T} shown {bad.Shown}"; }
            case "expect-by":
            {
                var p = v.Split(' '); double by = p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : 1e9;
                var hit = R.Ticks.FirstOrDefault(x => x.Shown.ToString() == p[0]);
                return hit == null ? "never shown" : hit.T <= by + 1e-6 ? null : $"shown at t={hit.T} (limit {by})";
            }
            case "expect-at":   // "<STATE> <t>": shown state at tick t (GPU Glance)
            {
                var p = v.Split(' '); double at = p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : 0;
                var hit = R.Ticks.FirstOrDefault(x => Math.Abs(x.T - at) < 1e-6);
                return hit == null ? $"no tick at t={at}" : hit.Shown.ToString() == p[0] ? null : $"t={at} shown {hit.Shown}";
            }
            case "expect-max-transitions":   // shown-state changes after the first tick (GPU Glance)
            {
                int n = 0; for (int i = 1; i < R.Ticks.Count; i++) if (R.Ticks[i].Shown != R.Ticks[i - 1].Shown) n++;
                return n <= int.Parse(v) ? null : $"{n} transitions";
            }
            case "expect-nvml": return R.NvmlCalls == long.Parse(v) ? null : $"{R.NvmlCalls} NVML calls";
            case "expect-tooltip": return null;   // GPU Glance's tray tooltip wording; BladeCtl has no tooltip (R5)
            case "expect-status": return last.Status.Contains(v) ? null : $"status: {last.Status}";
            case "expect-action": return last.Action.Contains(v) ? null : $"action: {last.Action}";
            case "expect-cpu": return last.Cpu.Code.ToString() == v ? null : $"cpu row {last.Cpu.Code}";
            case "expect-supply-final": return last.Supply.ToString() == v ? null : $"supply {last.Supply}";
            case "expect-supply-by":
            {
                var p = v.Split(' '); double by = double.Parse(p[1], CultureInfo.InvariantCulture);
                var hit = R.Ticks.FirstOrDefault(x => x.Supply.ToString() == p[0]);
                return hit == null ? "never shown" : hit.T <= by + 1e-6 && hit.T >= by - 2 - 1e-6 ? null : $"shown at t={hit.T} (expected {by})";
            }
            case "expect-button": return PowerButtons.Label(last.Button) == v ? null : $"button '{PowerButtons.Label(last.Button)}'";
            case "expect-battery": return last.Battery.Contains(v) ? null : $"battery: {last.Battery}";
            case "expect-battery-not": return !last.Battery.Contains(v) ? null : $"battery: {last.Battery}";
            case "expect-use": return v == "hidden" ? (last.UseW < 0 ? null : $"use {last.UseW:F1} W") : null;
            case "expect-source-seen": return R.Ticks.Any(x => x.Source == v) ? null : "never: " + string.Join(" | ", R.Ticks.Select(x => x.Source).Distinct());
            case "expect-source-final": return last.Source == v ? null : $"final source '{last.Source}'";
            case "expect-source-never": { var bad = R.Ticks.FirstOrDefault(x => x.Source.Contains(v)); return bad == null ? null : $"t={bad.T} source '{bad.Source}'"; }
            case "expect-anomaly": return R.Anomalies.Any(a => a.Contains(v)) ? null : "anomalies: " + string.Join(" | ", R.Anomalies);
            case "expect-learn-cpu": return R.Learner.CpuWrites == int.Parse(v) ? null : $"{R.Learner.CpuWrites} CPU learning writes";
            default: return "unknown expectation " + k;
        }
    }
}
