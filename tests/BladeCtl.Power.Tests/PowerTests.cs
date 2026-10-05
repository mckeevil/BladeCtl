using System.Text.RegularExpressions;
using BladeCtl.Core;
using BladeCtl.Core.Power;
using Xunit;

namespace BladeCtl.Power.Tests;

internal static class TestClock
{
    /// <summary>"learned Oct 3" labels must not turn into "(older data)" when the tests run months later.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Pin() => PowerLearn.Clock = () => new DateTime(2026, 10, 5, 12, 0, 0);
}

/// <summary>A0-1 and A0-2: every fixture (GPU Glance F1-F12 ported, BladeCtl F13-F24) replays to its expected states.</summary>
public class FixtureTests
{
    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Fixture.Dir(), "F*.csv").Select(p => new object[] { Path.GetFileName(p) }).OrderBy(x => (string)x[0]);

    [Theory, MemberData(nameof(Files))]
    public void Fixture_replays_to_expected_states(string file)
    {
        var f = Fixture.Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(Path.Combine(Fixture.Dir(), file)));
        Assert.NotEmpty(f.Expects);
        var R = Replay.Run(f);
        Assert.NotEmpty(R.Ticks);
        var fails = f.Expects.Select(e => (e, why: Replay.Check(R, e.K, e.V))).Where(x => x.why != null).Select(x => $"{f.Name} {x.e.K} {x.e.V}: {x.why}").ToList();
        Assert.True(fails.Count == 0, string.Join(Environment.NewLine, fails));
    }

    public static IEnumerable<object[]> GgFiles() =>
        Directory.GetFiles(Fixture.GgDir(), "F*.csv").Select(p => new object[] { Path.GetFileName(p) }).OrderBy(x => (string)x[0]);

    /// <summary>GPU Glance's own fixtures (its tests\fixtures, as of 2026-10-05) through BladeCtl's engine: the two apps agree.</summary>
    [Theory, MemberData(nameof(GgFiles))]
    public void GPU_Glance_fixture_replays_the_same_in_BladeCtl(string file)
    {
        var f = Fixture.Parse("GG " + Path.GetFileNameWithoutExtension(file), File.ReadAllText(Path.Combine(Fixture.GgDir(), file)));
        Assert.NotEmpty(f.Expects);
        var R = Replay.Run(f);
        var fails = f.Expects.Select(e => (e, why: Replay.Check(R, e.K, e.V))).Where(x => x.why != null).Select(x => $"{f.Name} {x.e.K} {x.e.V}: {x.why}").ToList();
        Assert.True(fails.Count == 0, string.Join(Environment.NewLine, fails));
    }

    /// <summary>
    /// Drift guard: the ported GPU Glance fixtures (F1-F12 here, gg\*) must equal GPU Glance's current files. When GPU Glance
    /// changes a fixture, this fails until BladeCtl's copy (and its engine) is re-synced.
    /// </summary>
    [Fact]
    public void Ported_fixtures_match_GPU_Glance_current_files()
    {
        if (!Directory.Exists(Fixture.GgLiveDir)) return;   // GPU Glance is not checked out on this machine
        static string Norm(string t) => t.Replace("\r\n", "\n").TrimEnd();
        var pairs = new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F10d", "F11", "F12" }.Select(n => (Path.Combine(Fixture.Dir(), n + ".csv"), n))
            .Concat(Directory.GetFiles(Fixture.GgDir(), "F*.csv").Select(p => (p, Path.GetFileNameWithoutExtension(p))));
        foreach (var (ours, name) in pairs)
        {
            string theirs = Path.Combine(Fixture.GgLiveDir, name + ".csv");
            Assert.True(File.Exists(theirs), "GPU Glance has no " + name);
            Assert.True(Norm(File.ReadAllText(ours)) == Norm(File.ReadAllText(theirs)), $"{name} differs from GPU Glance's current fixture: re-sync it");
        }
    }

    [Fact]
    public void GPU_Glance_F21_names_the_display_service()
    {
        var R = Replay.Run(Fixture.Parse("F21", File.ReadAllText(Path.Combine(Fixture.GgDir(), "F21.csv"))));
        Assert.Equal(St.KEPT_AWAKE, R.Ticks[^1].Shown);
        Assert.Equal("NVIDIA GPU keeps waking up on battery.", R.Ticks[^1].Status);
        Assert.Contains("the NVIDIA display service is running on battery", R.Ticks[^1].Action);
    }

    [Fact]
    public void All_24_fixtures_are_present()
    {
        var names = Directory.GetFiles(Fixture.Dir(), "F*.csv").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        foreach (var n in Enumerable.Range(1, 22).Append(24).Select(i => "F" + i)) Assert.Contains(n, names);
    }

    [Fact]
    public void F9_makes_no_NVML_calls()
    {
        var R = Replay.Run(Fixture.Parse("F9", File.ReadAllText(Path.Combine(Fixture.Dir(), "F9.csv"))));
        Assert.Equal(0, R.NvmlCalls);
    }

    /// <summary>F23: window closed, dGPU in D3 for 30 min -> 0 NVML calls and one closed sample per 30 s.</summary>
    [Theory]
    [InlineData(30.0)]   // default monitor cadence
    [InlineData(5.0)]    // fan target loop cadence
    [InlineData(10.0)]   // manual fan floor cadence
    public void F23_closed_window_D3_never_opens_the_gate(double tickEvery)
    {
        var cad = new SampleCadence();
        int closed = 0, gates = 0;
        for (double t = 0; t < 1800 - 1e-6; t += tickEvery)
        {
            var k = cad.Decide(live: false, t);
            if (k != SampleCadence.Kind.Closed) continue;
            closed++;
            cad.NoteClosedD(4);
            bool onBatteryGate = cad.ConsecutiveClosedD0 >= 2 && PowerMath.GateWanted(4, false, 0, false, false, false);
            bool onAcGate = PowerMath.GateWanted(4, false, 0, false, true, false);
            if (onBatteryGate || onAcGate) gates++;
        }
        Assert.Equal(0, gates);
        Assert.Equal(60, closed);
    }
}

/// <summary>A0-3: battery ETA, ported from GPU Glance SPEC 22.1.</summary>
public class BatteryEtaTests
{
    private static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol * Math.Max(1, Math.Abs(b));

    [Fact]
    public void Spec_example_66_percent_at_37_5_W_is_1_h_05()
    {
        const double full = 69449;
        double eta = BatteryEta.ChargeEtaSec(0.66 * full, full, 37500, null);
        double knee = 0.14 * full / 37500 * 60, taper = full * 0.2 / 37500 * (-Math.Log(0.15) / 0.85) * 60;
        Assert.True(Near(eta / 60, knee + taper, 0.005), $"{eta / 60:F1} min vs {knee + taper:F1}");
        Assert.InRange(knee, 15.5, 15.7);
        Assert.InRange(taper, 49.5, 49.7);
        Assert.Equal("~1 h 05 min", BatteryEta.FmtDuration(eta));
    }

    [Fact]
    public void Ported_GPU_Glance_ETA_checks()
    {
        const double full = 69400;
        Assert.True(Near(BatteryEta.ChargeEtaSec(34700, full, 34700, null, 1.0, 0.15), 3600, 0.001));
        double expTaper = (full * 0.2 / 30000.0) * (-Math.Log(0.15) / 0.85) * 3600.0;
        Assert.True(Near(BatteryEta.ChargeEtaSec(full * 0.8, full, 30000, null), expTaper, 0.005));
        double live90 = 30000 * (1 - 0.85 * 0.5);
        double exp90 = (full * 0.2 / 30000.0) * (1 / 0.85) * (Math.Log(1 - 0.85 * 0.5) - Math.Log(0.15)) * 3600.0;
        Assert.True(Near(BatteryEta.ChargeEtaSec(full * 0.9, full, live90, null), exp90, 0.005));
        var curve = new ChargeBin[20];
        double[] mw = { 40000, 40000, 38000, 36000, 34000, 30000, 20000, 12000, 8000, 5000 };
        for (int b = 10; b < 20; b++) curve[b] = new ChargeBin { Mw = mw[b - 10], N = 5 };
        double expL = 0.05 * full / 40000.0; for (int b = 11; b < 20; b++) expL += 0.05 * full / curve[b].Mw; expL *= 3600.0;
        Assert.True(Near(BatteryEta.ChargeEtaSec(full * 0.5, full, 40000, curve), expL, 0.003));
        // weak charger: live more than 20% under the learned bin scales the remaining bins by live/learned
        double expW = 0.05 * full / 20000.0; for (int b = 11; b < 20; b++) expW += 0.05 * full / (curve[b].Mw * 0.5); expW *= 3600.0;
        Assert.True(Near(BatteryEta.ChargeEtaSec(full * 0.5, full, 20000, curve), expW, 0.003));
        // within 20%: not scaled
        double expNs = 0.05 * full / 35000.0; for (int b = 11; b < 20; b++) expNs += 0.05 * full / curve[b].Mw; expNs *= 3600.0;
        Assert.True(Near(BatteryEta.ChargeEtaSec(full * 0.5, full, 35000, curve), expNs, 0.003));
        // bins with fewer than 3 samples do not count
        var c2 = (ChargeBin[])curve.Clone(); c2[12].N = 2;
        double expU = 0.05 * full / 40000.0; for (int b = 11; b < 20; b++) expU += 0.05 * full / (b == 12 ? 40000.0 : curve[b].Mw); expU *= 3600.0;
        Assert.True(Near(BatteryEta.ChargeEtaSec(full * 0.5, full, 40000, c2), expU, 0.003));
    }

    [Theory]
    [InlineData(9 * 60 + 20, "~9 min")]
    [InlineData(10 * 60, "~10 min")]
    [InlineData(23 * 60, "~25 min")]
    [InlineData(63 * 60, "~1 h 05 min")]
    [InlineData(230 * 60, "~3 h 50 min")]
    public void Rounding_under_10_min_to_the_minute_else_to_5(double sec, string text) => Assert.Equal(text, BatteryEta.FmtDuration(sec));

    private static PowerSample Bat(double t, bool ac, int rate, int soc = 66) => new()
    {
        T = t, OnAC = ac, BatOk = true, RateKnown = true, RateMw = rate, SocPct = soc, FullMwh = 69449, DesignMwh = 80000,
        CapMwh = (uint)(69449 * soc / 100), BatState = ac ? (rate > 0 ? 0x5u : 0x1u) : 0x2u,
    };

    [Fact]
    public void ETA_is_hidden_for_30_s_after_a_plug_event()
    {
        var m = new BatteryModel();
        m.Update(0, Bat(0, false, -18000));
        var s2 = Bat(2, true, 37500); m.Update(2, s2);
        Assert.Equal("Battery 66% \u00B7 charging, estimating time to full", m.Line(2, s2, -1));
        var s40 = Bat(40, true, 37500); m.Update(40, s40);
        Assert.StartsWith("Battery 66% \u00B7 full in ~1 h 05 min (charging 38 W)", m.Line(40, s40, -1));
    }

    [Fact]
    public void Battery_lines_match_spec_10()
    {
        var m = new BatteryModel();
        var s = Bat(0, false, -18000, 54); s.CapMwh = (uint)(69449 * 0.54);
        m.Update(0, s);
        Assert.Equal("Battery 54% \u00B7 ~2 h 05 min left at 18 W", m.Line(0, s, -1));
        // not draining (charge paused, rate -0.2 W): never blamed on the charger (GPU Glance wording)
        var nc = Bat(100, true, -200, 54) with { NotChargingOnAC = true };
        Assert.Equal("Battery 54% \u00B7 plugged in, not charging (laptop using ~63 W)", m.Line(100, nc, 63));
        Assert.Equal("Battery 54% \u00B7 plugged in, not charging", m.Line(100, nc, -1));
        Assert.Equal("Battery 60% \u00B7 plugged in, not charging (laptop using ~45 W)", m.Line(100, Bat(100, true, 0, 60) with { NotChargingOnAC = true, BatState = 0x1 }, 45));
        // draining: GPU Glance's exact line
        var dr = Bat(100, true, -9000, 54) with { NotChargingOnAC = true, BatState = 0x3 };
        Assert.Equal("Battery 54% \u00B7 plugged in but not charging (draining 9 W, charger can't keep up)", m.Line(100, dr, 63));
        Assert.Equal("Battery 100% \u00B7 full", m.Line(100, Bat(100, true, 0, 100), -1));
        Assert.Equal("Battery 97% \u00B7 topping off", m.Line(100, Bat(100, true, 0, 97), -1));
        Assert.Equal("Full charge 69.4 of 80.0 Wh (87%)", BatteryModel.HealthLine(Bat(0, true, 0)));
        Assert.Equal("+54 %/h charging", BatteryModel.PctPerHour(37.5, 69449));
        Assert.Equal("", m.Line(100, Bat(100, false, -18000) with { CapKnown = false, SocPct = -1 }, -1));   // unknown capacity: no line, never "0%"
        Assert.Equal("\u221226 %/h", BatteryModel.PctPerHour(-18, 69449));
    }
}

/// <summary>The battery model guards GPU Glance's learn.cpp has (2026-10-05).</summary>
public class BatteryModelGuardTests
{
    private static PowerSample Chg(double t, int rate, int soc = 60, uint state = 0x5) => new()
    {
        T = t, OnAC = true, BatOk = true, RateKnown = true, RateMw = rate, SocPct = soc, FullMwh = 69400, DesignMwh = 80000,
        CapMwh = (uint)(69400 * soc / 100), BatState = state,
    };

    [Fact]
    public void Settling_reads_never_seed_the_charge_EMA()
    {
        var m = new BatteryModel();
        m.Update(0, Chg(0, -15000) with { OnAC = false, BatState = 0x2 });
        m.Update(2, Chg(2, 60000));                         // plug-in transient, inside the 30 s settling window
        for (double t = 4; t <= 30; t += 2) m.Update(t, Chg(t, 60000));
        for (double t = 32; t <= 34; t += 2) m.Update(t, Chg(t, 30000));
        Assert.Equal(30.0, m.ChargeW, 1);                   // GPU Glance: 30.0 W, not a 47.6 W blend
    }

    [Fact]
    public void Trickle_charge_gets_no_ETA_and_ETAs_over_24_h_are_unknown()
    {
        var m = new BatteryModel();
        m.Update(0, Chg(0, -15000) with { OnAC = false, BatState = 0x2 });
        m.Update(2, Chg(2, 300));                           // plug event at t=2; settled from t=32
        for (double t = 40; t <= 44; t += 2) m.Update(t, Chg(t, 300));
        Assert.Equal("Battery 60% \u00B7 charging", m.Line(44, Chg(44, 300), -1));
        Assert.Equal(-1, m.EtaSec(44, Chg(44, 300)));
        var top = Chg(44, 300, 97);
        Assert.Equal("Battery 97% \u00B7 topping off", m.Line(44, top, -1));
        var slow = new BatteryModel();
        slow.Update(0, Chg(0, -15000) with { OnAC = false, BatState = 0x2 });
        slow.Update(2, Chg(2, 1600, 5));
        for (double t = 40; t <= 44; t += 2) slow.Update(t, Chg(t, 1600, 5));   // 1.6 W from 5%: about 40 h
        Assert.Equal(-1, slow.EtaSec(44, Chg(44, 1600, 5)));
        Assert.Equal("Battery 5% \u00B7 charging 2 W", slow.Line(44, Chg(44, 1600, 5), -1));
    }

    [Fact]
    public void Charging_bit_with_no_charge_power_drops_the_old_EMA()
    {
        var m = new BatteryModel();
        m.Update(0, Chg(0, -15000) with { OnAC = false, BatState = 0x2 });
        m.Update(2, Chg(2, 38000));
        for (double t = 40; t <= 50; t += 2) m.Update(t, Chg(t, 38000));
        Assert.True(m.ChargeW > 30);
        m.Update(52, Chg(52, 0, 97));
        Assert.True(m.ChargeW < 0);
        Assert.Equal("Battery 97% \u00B7 topping off", m.Line(52, Chg(52, 0, 97), -1));
    }

    [Fact]
    public void Unknown_capacity_is_not_zero_percent()
    {
        var s = new PowerSample { OnAC = true, BatOk = true, CapKnown = false, CapMwh = 0, FullMwh = 69400 };
        Assert.Equal(-1, s.Soc);
        Assert.Equal(-1, s.SocFrac);
        Assert.Equal(-1, new BatteryModel().EtaSec(0, s with { RateKnown = true, RateMw = 30000, BatState = 0x5 }));
    }
}

/// <summary>A0-4: hysteresis timings (GPU Glance SPEC 6.3).</summary>
public class HysteresisTests
{
    [Fact]
    public void Calm_states_need_two_ticks_and_immediate_states_switch_at_once()
    {
        var h = new Hysteresis();
        h.Push(St.LIGHT, Sub.None, 0); Assert.Equal(St.LIGHT, h.Shown);       // from UNKNOWN: immediate
        h.Push(St.WORKING, Sub.None, 2); Assert.Equal(St.LIGHT, h.Shown);
        h.Push(St.WORKING, Sub.None, 4); Assert.Equal(St.WORKING, h.Shown);
        h.Push(St.ASLEEP, Sub.None, 6); Assert.Equal(St.ASLEEP, h.Shown);
    }

    [Fact]
    public void Attention_enters_after_6_s_thermal_after_4_s_and_leaves_after_12_s()
    {
        var h = new Hysteresis();
        h.Push(St.FULL_POWER, Sub.None, 0); h.Push(St.FULL_POWER, Sub.None, 2);
        for (double t = 4; t < 10; t += 2) { h.Push(St.LIMITED_ON_AC, Sub.ModeFw, t); Assert.Equal(St.FULL_POWER, h.Shown); }
        h.Push(St.LIMITED_ON_AC, Sub.ModeFw, 10); Assert.Equal(St.LIMITED_ON_AC, h.Shown);
        for (double t = 12; t < 24; t += 2) { h.Push(St.FULL_POWER, Sub.None, t); Assert.Equal(St.LIMITED_ON_AC, h.Shown); }
        h.Push(St.FULL_POWER, Sub.None, 24); Assert.Equal(St.FULL_POWER, h.Shown);

        var g = new Hysteresis();
        g.Push(St.WORKING, Sub.None, 0); g.Push(St.WORKING, Sub.None, 2);
        g.Push(St.THERMAL, Sub.None, 4); g.Push(St.THERMAL, Sub.None, 6); Assert.Equal(St.WORKING, g.Shown);
        g.Push(St.THERMAL, Sub.None, 8); Assert.Equal(St.THERMAL, g.Shown);
    }

    [Fact]
    public void More_severe_after_4_s_less_severe_after_6_s_and_at_once_after_AC_DC()
    {
        var h = new Hysteresis();
        h.Push(St.ASLEEP, Sub.None, 0);
        h.Push(St.BATTERY_LIMITED, Sub.Power, 2); Assert.Equal(St.BATTERY_LIMITED, h.Shown);   // from ASLEEP: immediate
        h.Push(St.CHARGER_LIMITED, Sub.Draining, 4); h.Push(St.CHARGER_LIMITED, Sub.Draining, 6); Assert.Equal(St.BATTERY_LIMITED, h.Shown);
        h.Push(St.CHARGER_LIMITED, Sub.Draining, 8); Assert.Equal(St.CHARGER_LIMITED, h.Shown);
        h.Push(St.KEPT_AWAKE, Sub.None, 10); h.Push(St.KEPT_AWAKE, Sub.None, 14); Assert.Equal(St.CHARGER_LIMITED, h.Shown);
        h.Push(St.KEPT_AWAKE, Sub.None, 16); Assert.Equal(St.KEPT_AWAKE, h.Shown);
        h.AcDcPending = true;
        h.Push(St.LIGHT, Sub.None, 18); Assert.Equal(St.LIGHT, h.Shown);
    }

    [Fact]
    public void AC_DC_event_never_goes_straight_into_another_attention_state_and_distrusts_a_low_AC_limit_for_4_s()
    {
        var h = new Hysteresis();
        h.Push(St.BATTERY_LIMITED, Sub.Power, St.BATTERY_LIMITED, St.FULL_POWER, Sub.OnBattery, 0);   // seed: calm first
        for (double t = 2; t <= 8; t += 2) h.Push(St.BATTERY_LIMITED, Sub.Power, St.BATTERY_LIMITED, St.FULL_POWER, Sub.OnBattery, t);
        Assert.Equal(St.BATTERY_LIMITED, h.Shown);
        h.AcDcPending = true; h.AcDcAt = 10;
        h.Push(St.LIMITED_ON_AC, Sub.ModeFw, St.LIMITED_ON_AC, St.FULL_POWER, Sub.None, 10);
        Assert.Equal(St.FULL_POWER, h.Shown);                                       // calm, not LIMITED_ON_AC
        for (double t = 12; t <= 18; t += 2) { h.Push(St.LIMITED_ON_AC, Sub.ModeFw, St.LIMITED_ON_AC, St.FULL_POWER, Sub.None, t); Assert.Equal(St.FULL_POWER, h.Shown); }
        h.Push(St.LIMITED_ON_AC, Sub.ModeFw, St.LIMITED_ON_AC, St.FULL_POWER, Sub.None, 20);   // 6 s counted from acdcAt + 4
        Assert.Equal(St.LIMITED_ON_AC, h.Shown);
        Assert.False(h.AcDcPending);                                                // consumed by the first push
    }

    [Fact]
    public void CPU_overlay_on_an_asleep_GPU_uses_the_attention_holds()
    {
        var h = new Hysteresis();
        h.Push(St.ASLEEP, Sub.None, St.ASLEEP, St.ASLEEP, Sub.None, 0);
        int changes = 0;
        for (double t = 2; t <= 50; t += 2)
        {
            bool busy = ((int)(t / 2)) % 4 != 0;   // hovering: three ticks over the threshold, one under
            changes += h.Push(busy ? St.CPU_LIMITED_BATTERY : St.ASLEEP, busy ? Sub.CpuFreq : Sub.None, St.ASLEEP, St.ASLEEP, Sub.None, t) ? 1 : 0;
        }
        Assert.Equal(0, changes);
        Assert.Equal(St.ASLEEP, h.Shown);
    }

    [Fact]
    public void After_a_reset_one_attention_sample_is_not_shown()
    {
        var h = new Hysteresis();
        h.Push(St.WORKING, Sub.None, 0); h.Push(St.WORKING, Sub.None, 2);
        h.Reset();
        h.Push(St.THERMAL, Sub.None, St.THERMAL, St.WORKING, Sub.None, 200);
        Assert.Equal(St.UNKNOWN, h.Shown);                 // "reading", not THERMAL from one sample
        Assert.True(h.HoldingSeed);
        h.Push(St.THERMAL, Sub.None, St.THERMAL, St.WORKING, Sub.None, 202);
        Assert.Equal(St.UNKNOWN, h.Shown);
        h.Push(St.THERMAL, Sub.None, St.THERMAL, St.WORKING, Sub.None, 204);
        Assert.Equal(St.THERMAL, h.Shown);                 // its normal 4 s entry hold
        var g = new Hysteresis();
        g.Push(St.THERMAL, Sub.None, St.THERMAL, St.WORKING, Sub.None, 0);
        g.Push(St.WORKING, Sub.None, St.WORKING, St.WORKING, Sub.None, 2);
        Assert.Equal(St.WORKING, g.Shown);                 // a calm reading replaces it at once
    }

    [Fact]
    public void CPU_row_code_needs_two_ticks()
    {
        var hold = new CpuRowHold();
        CpuRowOut R(CpuCode c) => new(c, "", "", "", false, "");
        Assert.Equal(CpuCode.C2_Idle, hold.Push(R(CpuCode.C2_Idle)).Code);
        Assert.Equal(CpuCode.C2_Idle, hold.Push(R(CpuCode.C6_Profile)).Code);
        Assert.Equal(CpuCode.C2_Idle, hold.Push(R(CpuCode.C2_Idle)).Code);
        Assert.Equal(CpuCode.C2_Idle, hold.Push(R(CpuCode.C6_Profile)).Code);
        Assert.Equal(CpuCode.C6_Profile, hold.Push(R(CpuCode.C6_Profile)).Code);
    }
}

/// <summary>GpuVerdict deltas: D1 with bit 0x20, idle-only wake episodes, the reopen gap.</summary>
public class GpuVerdictTests
{
    private static PowerSample NvS(double t, bool ac, ulong reasons, double c74, double c269, double energy, int dstate = 1) => new()
    {
        T = t, OnAC = ac, DState = dstate, Gate = true, GateWanted = true, NvOk = true, NvState = NvState.READY, DBusy = 96,
        FOk = Enumerable.Repeat(true, Nv.F_COUNT).ToArray(),
        FVal = new double[] { energy, 90000, 105000, 80000, 91000, c74, 0, 0, c269, 0, 0 },
        ReasonsOk = true, Reasons = reasons, UtilOk = true, Util = 98, TempC = 70, TslowC = 98, TtargetC = 80, PowerSource = ac ? 0 : 1,
    };

    [Fact]
    public void Bit_0x20_next_to_0x4_is_not_thermal_while_field_269_mirrors_74()
    {
        var v = new GpuVerdict(); VerdictOut o = new();
        double e = 0, c = 0;
        for (double t = 0; t <= 20; t += 2) { e += 90 * 2000; c += 0.6 * 2e9; o = v.Push(NvS(t, true, 0x24, c, c, e), new GpuRefs()); }
        Assert.True(o.D.Alias269);
        Assert.Equal(0, o.D.FracThermal);
        Assert.Equal(St.FULL_POWER, o.Shown);
        // 0x20 on its own still counts
        var v2 = new GpuVerdict();
        e = 0; c = 0;
        for (double t = 0; t <= 20; t += 2) { e += 90 * 2000; c += 0.6 * 2e9; o = v2.Push(NvS(t, true, 0x20, c, c, e), new GpuRefs()); }
        Assert.Equal(St.THERMAL, o.Shown);
    }

    private static PowerSample Idle(double t, bool ac, int d, int holders = 0) => new()
    {
        T = t, OnAC = ac, DState = d, DBusy = 0, DMemHolders = d == 1 ? holders : -1, NvSvc = ac ? -1 : 1,
    };

    [Fact]
    public void Wakes_seen_on_AC_do_not_count_after_unplugging()
    {
        var v = new GpuVerdict(); VerdictOut o = new();
        double t = 0;
        for (int i = 0; i < 20; i++, t += 2) o = v.Push(Idle(t, true, i % 2 == 0 ? 1 : 4), new GpuRefs());
        for (int i = 0; i < 30; i++, t += 2) { o = v.Push(Idle(t, false, 4), new GpuRefs()); Assert.NotEqual(St.KEPT_AWAKE, o.Shown); }
        Assert.Equal(0, o.D.WakeEdges);
    }

    [Fact]
    public void Only_idle_wake_episodes_count()
    {
        var busy = new GpuVerdict(); var idle = new GpuVerdict(); VerdictOut ob = new(), oi = new();
        double t = 0;
        for (int i = 0; i < 40; i++, t += 2)
        {
            int d = i % 2 == 0 ? 1 : 4;
            ob = busy.Push(Idle(t, false, d, holders: 1), new GpuRefs());
            oi = idle.Push(Idle(t, false, d), new GpuRefs());
        }
        Assert.Equal(0, ob.D.WakeEdges);
        Assert.True(oi.D.WakeEdges >= 5);
        Assert.Equal(St.KEPT_AWAKE, oi.Shown);
        Assert.Equal(Sub.WakeLoop, oi.ShownSub);
    }

    [Fact]
    public void An_unplug_while_the_window_was_closed_is_not_a_fresh_event_at_reopen()
    {
        var v = new GpuVerdict();
        v.Push(new PowerSample { T = 0, OnAC = true, DState = 4 }, new GpuRefs());
        var o = v.Push(new PowerSample { T = 600, OnAC = false, DState = 4, AcDcAt = 30 }, new GpuRefs());
        Assert.Equal(30, o.AcDcAt);           // from the trackers, not "now": no 4 s "Unplugged: reading the new limit" banner
        Assert.False(v.Main.AcDcPending);
        Assert.Equal(St.ASLEEP, o.Shown);
    }
}

/// <summary>A0-5: exact wording for every row of spec 11.4.1 and 11.4.2.</summary>
public class WordingTests
{
    private static Derived D(double P = 0, double L = 0, double U = 0, double Ldef = 80, double Lmax = 105, int T = -1, double clk = 0, double lref = -1, bool learned = false) =>
        new() { P = P, L = L, U = U, Ldef = Ldef, Lmax = Lmax, T = T, Clk = clk, Lref = lref, LrefLearned = learned, HaveP = true, Nv = true };

    [Fact]
    public void Status_and_action_sentences_11_4_1()
    {
        void Check(WordCtx c, string status, string action) { Assert.Equal(status, Wording.Status(c)); Assert.Equal(action, Wording.Action(c)); }
        Check(new WordCtx { St = St.BATTERY_LIMITED, Sub = Sub.Power, OnAC = false, Class = SupplyClass.Battery, D = D(34.6, 35, 97, lref: 93, learned: true) },
              "Plug in for full GPU power.", "On battery the GPU is capped at 35 W and is using all of it. Plugged in it gets up to 93 W (learned).");
        Check(new WordCtx { St = St.BATTERY_LIMITED, Sub = Sub.Clock, OnAC = false, Class = SupplyClass.Battery, D = D(clk: 1010), Lv = new GpuRefs { ClkRefMHz = 1450 } },
              "Plug in for full GPU speed (probably).", "On battery it's running at 1,010 MHz; plugged in it ran at about 1,450 MHz. The power limit isn't the cause, so this looks like a battery speed cap.");
        Check(new WordCtx { St = St.BATTERY_LIMITED, Sub = Sub.Supply, OnAC = false, Class = SupplyClass.Battery, D = new Derived { Duty271 = 0.4 } },
              "The battery can't supply the GPU fast enough.", "The power brake engaged 40% of the last 6 s. Plug in.");
        Check(new WordCtx { St = St.CPU_LIMITED_BATTERY, Sub = Sub.CpuPkg, OnAC = false, Class = SupplyClass.Battery, D = new Derived { PkgW = 18, Freq = 2100 }, Lv = new GpuRefs { CpuRefValid = true, CpuRefW = 45, CpuRefMHz = 4200, CpuRefDate = "2026-10-02" } },
              "Plug in: the app in front is waiting on the CPU, which is slowed on battery.", "CPU at 18 W, 2.1 GHz vs about 45 W, 4.2 GHz plugged in (learned Oct 2).");
        Check(new WordCtx { St = St.FULL_POWER, Class = SupplyClass.Barrel, AdapterW = 230, D = D(79, 80, 98) },
              "Full power: 79 of 80 W.", "The limit moves between 80 and 105 W as the CPU and GPU share power. That's normal.");
        Check(new WordCtx { St = St.FULL_POWER, Sub = Sub.OnBattery, OnAC = false, Class = SupplyClass.Battery, D = D(79, 80, 98) },
              "GPU at its battery limit: 79 of 80 W.", "Plugged in it can go up to 105 W with Dynamic Boost.");
        Check(new WordCtx { St = St.WORKING, Class = SupplyClass.Barrel, D = D(55, 80, 70) },
              "GPU working: 55 W, 70% busy, not limited.", "It isn't at its 80 W limit, so more power wouldn't help right now.");
        Check(new WordCtx { St = St.LIGHT, Class = SupplyClass.Barrel, D = D(18, 91, 12) }, "GPU lightly used: 12% busy, 18 W.", "Plenty of headroom.");
        Check(new WordCtx { St = St.LIGHT, OnAC = false, Class = SupplyClass.Battery, D = D(18, 35, 12) }, "GPU lightly used: 12% busy, 18 W.", "Plugging in won't make the GPU faster.");
        Check(new WordCtx { St = St.AWAKE_IDLE }, "NVIDIA GPU is awake but idle.", "Watts aren't read while it's idle, so BladeCtl never keeps it awake. GPU Glance shows which app is holding it.");
        Check(new WordCtx { St = St.KEPT_AWAKE, OnAC = false, D = new Derived { LowUseSec = 185 }, DMemHolders = 1 },
              "NVIDIA GPU kept awake on battery for 3 min with nothing to do.", "GPU Glance shows which app is holding it.");
        Check(new WordCtx { St = St.KEPT_AWAKE, OnAC = false, DMemHolders = 0, NvSvc = 1, Elevated = false },
              "NVIDIA's display service is keeping the GPU awake (no app is using it).", "BladeCtl isn't running as administrator, so it can't stop that service.");
        Check(new WordCtx { St = St.KEPT_AWAKE, OnAC = false, DMemHolders = 0, NvSvc = 1, ProfileEnabled = false },
              "NVIDIA's display service is keeping the GPU awake (no app is using it).", "The battery profile is off, so BladeCtl leaves that service running.");
        Check(new WordCtx { St = St.KEPT_AWAKE, OnAC = false, DMemHolders = 0, NvSvc = 1 },
              "NVIDIA's display service is keeping the GPU awake (no app is using it).", "BladeCtl tried to stop it on battery; the log has the result.");
        Check(new WordCtx { St = St.KEPT_AWAKE, Sub = Sub.WakeLoop, OnAC = false, D = new Derived { WakeEdges = 6 } },
              "NVIDIA GPU keeps waking up on battery.", "It woke 6 times in the last 2 min with nothing to do.");
        Check(new WordCtx { St = St.KEPT_AWAKE, Sub = Sub.WakeLoop, OnAC = false, D = new Derived { WakeEdges = 6 }, DMemHolders = 0, NvSvc = 1, Elevated = false },
              "NVIDIA GPU keeps waking up on battery.",
              "It woke 6 times in the last 2 min. No app is using it, and the NVIDIA display service is running on battery. BladeCtl isn't running as administrator, so it can't stop that service.");
        Check(new WordCtx { St = St.KEPT_AWAKE, OnAC = false, DMemHolders = 0, NvSvc = 1, StopsService = false },
              "NVIDIA's display service is keeping the GPU awake (no app is using it).", "It isn't in BladeCtl's BatteryStopServices setting, so BladeCtl leaves it running.");
        Check(new WordCtx { St = St.ASLEEP, OnAC = false, Lv = new GpuRefs { LbatW = 35, LbatDate = "2026-10-03" } },
              "NVIDIA GPU is asleep (0 W).", "If something needs the NVIDIA GPU on battery, it gets about 35 W (learned Oct 3).");
        Check(new WordCtx { St = St.ASLEEP }, "NVIDIA GPU is asleep (0 W).", "It wakes when an app needs it.");
        Check(new WordCtx { St = St.LIMITED_ON_AC, Sub = Sub.Hot, Class = SupplyClass.AcUnknown, D = D(70, 72, 99, T: 79) },
              "Limited while plugged in: GPU capped at 72 W.", "The GPU is at 79 \u00B0C, its temperature target, so the laptop is holding power back. Check the vents.");
        Check(new WordCtx { St = St.LIMITED_ON_AC, Sub = Sub.ModeFw, Class = SupplyClass.AcUnknown, D = D(34, 35, 99) },
              "Limited while plugged in: GPU capped at 35 W (normally 80\u2013105 W).", "Use the 230 W barrel charger and check the Razer power mode.");
        Check(new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Undersized, Class = SupplyClass.AcUnknown },
              "Charger too weak.", "NVIDIA reports an undersized power supply. Use the 230 W barrel charger.");
        Check(new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Draining, Class = SupplyClass.AcUnknown, D = new Derived { DrainW = 9 } },
              "Charger can't keep up.", "The battery is draining 9 W while plugged in. Use the 230 W barrel charger.");
        Check(new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Draining, Class = SupplyClass.AcUnknown, D = new Derived { DrainW = 0.4 } },
              "Charger can't keep up.", "The battery is discharging while plugged in. Use the 230 W barrel charger.");
        Check(new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Brake, Class = SupplyClass.AcUnknown, D = new Derived { FracBrake = 0.5 } },
              "Charger can't keep up.", "The GPU's power brake engaged 50% of the last 6 s.");
        Check(new WordCtx { St = St.THERMAL, Class = SupplyClass.Barrel, D = D(T: 93) }, "Too hot: GPU at 93 \u00B0C is slowing itself down.", "Check the vents and what the laptop is sitting on.");
        Check(new WordCtx { St = St.UNKNOWN, D = new Derived { NvErr = 15, NvRetryIn = 29.2 } }, "GPU status unknown.", "The NVIDIA driver returned error 15 (GPU is lost). Retrying in 30 s.");
        Check(new WordCtx { St = St.GPU_MISSING, ProblemCode = 43 }, "NVIDIA GPU not available.", "Device Manager reports problem code 43.");
        Check(new WordCtx { St = St.UNKNOWN, Sub = Sub.NotOptimus }, "GPU status unknown.", "This NVIDIA GPU isn't switchable (Optimus) graphics, so its power state can't be read as asleep.");
        Check(new WordCtx { St = St.LIGHT, AcDcBanner = 1 }, "Unplugged: reading the new limit\u2026", "");
        Check(new WordCtx { St = St.LIGHT, AcDcBanner = 2 }, "Plugged in: reading the new limit\u2026", "");
    }

    [Fact]
    public void BladeCtl_clauses_11_4_2()
    {
        string A(WordCtx c) => Wording.Action(c);
        Assert.Equal("The battery is draining 9 W while plugged in. Use the 230 W barrel charger. Razer EC reports a 65 W supply (recommended 230 W).",
            A(new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Draining, Class = SupplyClass.UsbC, AdapterW = 65, D = new Derived { DrainW = 9 } }));
        Assert.Equal("The battery is draining 9 W while plugged in. Even the 230 W charger can't keep up with this load.",
            A(new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Draining, Class = SupplyClass.Barrel, AdapterW = 230, D = new Derived { DrainW = 9 } }));
        Assert.Equal("Check the Razer power mode: it's Balanced.",
            A(new WordCtx { St = St.LIMITED_ON_AC, Sub = Sub.ModeFw, Class = SupplyClass.Barrel, AdapterW = 230, LiveMode = "Balanced", D = D(34, 35, 99) }));
        Assert.Equal("Check the Razer power mode: it's Custom with GPU Low.",
            A(new WordCtx { St = St.LIMITED_ON_AC, Sub = Sub.ModeFw, Class = SupplyClass.Barrel, AdapterW = 230, LiveMode = "Custom", LiveGpuBoost = 0, D = D(34, 35, 99) }));
        Assert.Equal("Use the 230 W barrel charger and check the Razer power mode. Razer EC reports a 65 W supply (recommended 230 W).",
            A(new WordCtx { St = St.LIMITED_ON_AC, Sub = Sub.ModeFw, Class = SupplyClass.UsbC, AdapterW = 65, D = D(34, 35, 99) }));
        Assert.EndsWith(" Your battery profile holds it at GPU Low.",
            A(new WordCtx { St = St.BATTERY_LIMITED, Sub = Sub.Power, OnAC = false, ProfileEngaged = true, D = D(34.6, 35, 97, lref: 93, learned: true) }));
        var cpuLimited = new WordCtx
        {
            St = St.CPU_LIMITED_BATTERY, OnAC = false, D = new Derived { PkgW = 18, Freq = 1840 },
            Lv = new GpuRefs { CpuRefValid = true, CpuRefW = 45, CpuRefMHz = 4200, CpuRefDate = "2026-10-02" }, DcMaxPct = 80, DcBoostOff = true,
        };
        Assert.EndsWith(" Windows caps the CPU at 80% on battery with boost off.", A(cpuLimited with { Cpu = new CpuRowOut(CpuCode.C5_Windows, "HELD BACK", "warn", "x", false, "") }));
        Assert.EndsWith(" Your battery profile holds it at CPU Low.", A(cpuLimited with { Cpu = new CpuRowOut(CpuCode.C6_Profile, "HELD BACK", "warn", "x", false, "") }));
        Assert.Equal("The limit moves between 80 and 105 W as the CPU and GPU share power. That's normal. On USB-C the limit is 85 W vs ~93 W on the 230 W charger (learned).",
            A(new WordCtx { St = St.FULL_POWER, Class = SupplyClass.UsbC, AdapterW = 65, D = D(84, 85, 98), Lv = new GpuRefs { BarrelLacW = 93 } }));
        Assert.Equal("Plenty of headroom. On USB-C the whole laptop shares 65 W (Razer EC); ~48 W in use now.",
            A(new WordCtx { St = St.LIGHT, Class = SupplyClass.UsbC, AdapterW = 65, D = D(18, 91, 12), UseW = 48 }));
        Assert.Equal("Plenty of headroom. On USB-C? the whole laptop shares 65 W (Razer EC); ~48 W in use now.",
            A(new WordCtx { St = St.LIGHT, Class = SupplyClass.UsbC, AdapterW = 65, D = D(18, 91, 12), UseW = 48, UsbcConfirmed = false }));
        Assert.Equal("CPU: Windows caps the CPU at 80% on battery (about 1.8 GHz, boost off).",
            A(new WordCtx { St = St.LIGHT, OnAC = false, D = D(5, 35, 5), Cpu = new CpuRowOut(CpuCode.C5_Windows, "HELD BACK", "warn", CpuVerdict.WindowsCapText(80, true, 1843.2, true), false, "") }));
        Assert.Equal("Check the vents and what the laptop is sitting on. Fans 3,300 of 5,000 RPM.",
            A(new WordCtx { St = St.THERMAL, Class = SupplyClass.Barrel, D = D(T: 93), FanRpm = 3300 }));
    }

    [Fact]
    public void Rows_and_source_text_11_4_3()
    {
        var acRow = Wording.GpuRow(St.LIGHT, Sub.None, D(18, 91.4, 12), new GpuRefs { LrefW = 93, LrefLearned = true, LrefDate = "2026-10-03" }, true, SupplyClass.Barrel, 230, true);
        Assert.Equal("Plugged in (230 W): up to 93 W \u00B7 learned Oct 3", acRow.Reason);
        Assert.Equal("18 / 91 W", acRow.Numbers);
        Assert.Equal("FREE", acRow.Word);
        var batRow = Wording.GpuRow(St.BATTERY_LIMITED, Sub.Power, D(34.6, 35, 97), new GpuRefs { LrefW = 93, LrefLearned = true }, false, SupplyClass.Battery, 0, true);
        Assert.Equal("On battery: 35 W cap \u00B7 ~93 W plugged in (learned)", batRow.Reason);
        Assert.Equal("HELD BACK", batRow.Word);
        var fresh = Wording.GpuRow(St.FULL_POWER, Sub.None, D(79, 80, 98), new GpuRefs(), true, SupplyClass.Barrel, 230, true);
        Assert.Equal("80 W plugged in, up to 105 W with Dynamic Boost", fresh.Reason);
        Assert.Equal("AT LIMIT", fresh.Word);
        Assert.Equal("asleep", Wording.GpuRow(St.ASLEEP, Sub.None, new Derived(), new GpuRefs(), true, SupplyClass.Barrel, 230, false).Numbers);
        Assert.Equal("230 W charger", Wording.SourceText(true, SupplyClass.Barrel, 230, false));
        Assert.Equal("USB-C? \u00B7 65 W", Wording.SourceText(true, SupplyClass.UsbC, 65, false));
        Assert.Equal("USB-C \u00B7 65 W", Wording.SourceText(true, SupplyClass.UsbC, 65, true));
        Assert.Equal("Battery", Wording.SourceText(false, SupplyClass.Barrel, 230, false));
        Assert.Equal("Plugged in \u00B7 identifying charger\u2026", Wording.SourceText(true, SupplyClass.Settling, 0, false));
    }
}

/// <summary>A0-6: the Razer adapter table and classification.</summary>
public class RazerAdapterTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 40)] [InlineData(2, 45)] [InlineData(3, 60)] [InlineData(4, 65)] [InlineData(5, 80)] [InlineData(6, 90)]
    [InlineData(7, 100)] [InlineData(8, 120)] [InlineData(9, 130)] [InlineData(10, 150)] [InlineData(11, 180)] [InlineData(12, 200)]
    [InlineData(13, 230)] [InlineData(14, 250)] [InlineData(15, 310)] [InlineData(17, 280)] [InlineData(18, 330)] [InlineData(19, 400)]
    public void Every_table_entry(int level, int watts) => Assert.Equal(watts, RazerAdapter.Watts((byte)level));

    [Theory]
    [InlineData(16)] [InlineData(20)] [InlineData(254)] [InlineData(255)]
    public void Unknown_levels(int level) => Assert.Null(RazerAdapter.Watts((byte)level));

    [Fact]
    public void Classification_compares_watts_not_levels()
    {
        Assert.Equal(SupplyClass.UsbC, RazerAdapter.Classify(true, true, 17, 15, false).Class);    // 280 W < 310 W although 17 > 15
        Assert.Equal(SupplyClass.Barrel, RazerAdapter.Classify(true, true, 15, 17, false).Class);  // 310 W >= 280 W
        Assert.Equal(SupplyClass.Barrel, RazerAdapter.Classify(true, true, 0x0D, 0x0D, false).Class);
        var u = RazerAdapter.Classify(true, true, 0x04, 0x0D, false);
        Assert.Equal((SupplyClass.UsbC, 65, 230), (u.Class, u.AdapterW, u.RecW));
        Assert.Equal(SupplyClass.Settling, RazerAdapter.Classify(true, true, 0, 0x0D, true).Class);
        Assert.Equal(SupplyClass.AcUnknown, RazerAdapter.Classify(true, true, 0, 0x0D, false).Class);
        Assert.Equal(SupplyClass.AcUnknown, RazerAdapter.Classify(true, false, 0, 0, false).Class);
        var dc = RazerAdapter.Classify(false, true, 0x0D, 0x0D, false);
        Assert.Equal(SupplyClass.Battery, dc.Class);
        Assert.Contains("while Windows says battery", dc.Anomaly);
        Assert.Null(RazerAdapter.Classify(false, true, 0, 0x0D, false).Anomaly);
        var rec0 = RazerAdapter.Classify(true, true, 0x0D, 0x00, false);
        Assert.Equal(230, rec0.RecW);
        Assert.Contains("recommended assumed 230 (raw 00)", rec0.Anomaly);
    }
}

/// <summary>A0-7: every packet the 2.7.0 code builds is a GET (command id bit 0x80 set). No SET is added.</summary>
public class GetOnlyTests
{
    [Fact]
    public void New_packets_are_GETs()
    {
        var packets = new[] { BladeController.AdapterWattagePacket(), BladeController.FanCurrentRpmPacket(1), BladeController.FanCurrentRpmPacket(2), BladeController.ExternalPowerPacket() };
        foreach (var p in packets) Assert.True((p.CommandId & 0x80) != 0, $"class 0x{p.CommandClass:X2} id 0x{p.CommandId:X2} is not a GET");
        Assert.Equal((0x07, 0x8C, 2), (packets[0].CommandClass, packets[0].CommandId, packets[0].DataSize));
        Assert.Equal((0x0D, 0x88, 3), (packets[1].CommandClass, packets[1].CommandId, packets[1].DataSize));
        Assert.Equal(2, packets[2].Args[1]);
        Assert.Equal((0x00, 0xB7, 1), (packets[3].CommandClass, packets[3].CommandId, packets[3].DataSize));
    }

    /// <summary>Source scan: every RazerPacket.Create in the 2.7.0 block of BladeController.cs and in the Power sources is a GET.</summary>
    [Fact]
    public void Source_scan_finds_no_SET_in_the_power_code()
    {
        string root = Path.GetFullPath(Path.Combine(Fixture.Dir(), "..", ".."));
        string ctl = File.ReadAllText(Path.Combine(root, "src", "BladeCtl.Core", "BladeController.cs"));
        int a = ctl.IndexOf("2.7.0 Power card", StringComparison.Ordinal);
        Assert.True(a > 0);
        int b = ctl.IndexOf("/// <summary>\n    /// Set performance mode", a, StringComparison.Ordinal);
        if (b < 0) b = ctl.IndexOf("public bool SetMode(", a, StringComparison.Ordinal);
        string block = ctl[a..b];
        var sources = new List<string> { block };
        foreach (var dir in new[] { Path.Combine(root, "src", "BladeCtl.Core", "Power"), Path.Combine(root, "src", "BladeCtl.Tray", "Power") })
            if (Directory.Exists(dir)) sources.AddRange(Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
        var rx = new Regex(@"RazerPacket\.Create\(\s*\w+\s*,\s*0x([0-9A-Fa-f]{2})\s*,\s*0x([0-9A-Fa-f]{2})");
        int n = 0;
        foreach (var src in sources)
            foreach (Match m in rx.Matches(src)) { n++; Assert.True((Convert.ToByte(m.Groups[2].Value, 16) & 0x80) != 0, "SET found: " + m.Value); }
        Assert.True(n >= 3);
        // every packet in those sources uses a literal command id the scan above could read (no named constant slips past it)
        int creates = sources.Sum(src => Regex.Matches(src, @"RazerPacket\.Create\(").Count);
        Assert.Equal(creates, n);
        // Allowlist: the Power code may call only these BladeController getters, and no BladeDevice member but Find.
        string[] allowed = { "GetAdapterWattage", "GetFanCurrentRpm", "GetExternalPowerStatus", "GetPowerState", "GetCpuBoost", "GetGpuBoost",
                             "GetThermalReading", "GetFanRpm", "GetFirmwareVersion" };
        var ctlMembers = typeof(BladeController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => !n.StartsWith("get_") && !n.StartsWith("set_")).Distinct().ToList();
        var devMembers = typeof(BladeDevice).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => n != "Find" && !n.StartsWith("get_") && !n.StartsWith("set_") && n != ".ctor").Distinct().ToList();
        string tray = Path.Combine(root, "src", "BladeCtl.Tray", "Power");
        Assert.True(Directory.Exists(tray));
        foreach (var f in Directory.GetFiles(tray, "*.cs"))
        {
            string t = File.ReadAllText(f);
            foreach (var m in ctlMembers.Where(m => !allowed.Contains(m) && m != "Dispose"))
                Assert.False(Regex.IsMatch(t, @"\." + Regex.Escape(m) + @"\s*\("), $"{Path.GetFileName(f)} calls BladeController.{m}");
            foreach (var m in devMembers.Where(m => m != "Dispose" && m != "DevicePath"))
                Assert.False(Regex.IsMatch(t, @"\." + Regex.Escape(m) + @"\b"), $"{Path.GetFileName(f)} uses BladeDevice.{m}");
            Assert.False(t.Contains("RazerPacket.Create"), $"{Path.GetFileName(f)} builds its own EC packet");
        }
    }
}

/// <summary>The optional private-apps list: hashing, matching, missing file, unreadable file.</summary>
public class PrivateAppsTests
{
    [Fact]
    public void Hash_is_fnv1a_64_over_utf16le()
    {
        Assert.Equal(0xcbf29ce484222325UL, PrivateApps.Hash(""));
        Assert.Equal(PrivateApps.Hash("exampleapp").ToString("x16"), PrivateApps.Line("  ExampleApp "));
    }

    [Fact]
    public void Matches_tokens_segments_and_runs()
    {
        string f = Path.Combine(Path.GetTempPath(), "bladectl-private-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "# comment\n0x" + PrivateApps.Line("exampleapp") + "\n\nnot-hex\n");
        try
        {
            var l = PrivateApps.Load(f);
            Assert.Equal(1, l.Count);
            Assert.False(l.FailedClosed);
            Assert.True(l.Is("ExampleApp"));
            Assert.True(l.Is(@"C:\Program Files\Vendor\ExampleApp.exe"));
            Assert.True(l.Is("", @"C:\Tools\host.exe --target=exampleapp;x"));
            Assert.False(l.Is("notepad", @"C:\Windows\notepad.exe", "Notepad"));
            Assert.False(l.Is("exampleapp2"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void No_file_means_nothing_is_private()
    {
        var l = PrivateApps.Load(Path.Combine(Path.GetTempPath(), "bladectl-none-" + Guid.NewGuid().ToString("N") + ".txt"));
        Assert.Equal(0, l.Count);
        Assert.False(l.Is("anything", @"C:\any\thing.exe"));
    }

    [Fact]
    public void Unreadable_file_fails_closed()
    {
        string f = Path.Combine(Path.GetTempPath(), "bladectl-locked-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, PrivateApps.Line("exampleapp"));
        try
        {
            using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var l = PrivateApps.Load(f);
                Assert.True(l.FailedClosed);
                Assert.True(l.Is("anything"));
            }
        }
        finally { File.Delete(f); }
    }
}

public class LearnTests
{
    [Fact]
    public void Limit_histogram_validity_and_percentiles()
    {
        var l = new PowerLearn();
        string ac = PowerKeys.Gpu("616.64", "B230", "Balanced", "x", "P0"), dc = PowerKeys.Gpu("616.64", "DC", "Custom", "c0g0", "P1");
        for (int i = 0; i < 59; i++) l.AddGpuLimit(ac, i < 40 ? 93 : 94, 1);
        Assert.Null(l.GpuLimit(ac));
        l.AddGpuLimit(ac, 94, 1);
        Assert.Equal(94, l.GpuLimit(ac)!.Value.W);
        l.AddGpuLimit(dc, 35, 15);
        Assert.Equal(35, l.GpuLimit(dc)!.Value.W);
        Assert.Equal(("616.64", "B230", "Balanced", "x", "P0"), PowerKeys.ParseGpu(ac));
    }

    [Fact]
    public void Save_load_roundtrip_and_quarantine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bladectl-learn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "power-learned.json");
            var l = new PowerLearn();
            l.LearnCpu(PowerKeys.Cpu("B230", "Balanced", "x", "P0", true), 44.8, 4210, 300);
            l.LearnChg("B230", 14, 38400); l.NoteEc(DateTime.UtcNow, 13, 13, true);
            Assert.True(l.Save(path, force: true));
            var r = new PowerLearn(); r.Load(path);
            Assert.Equal("OK", r.FileState);
            Assert.NotNull(r.Cpu(PowerKeys.Cpu("B230", "Balanced", "x", "P0", true)));
            Assert.Single(r.EcHistory());
            File.WriteAllText(path, "{ not json");
            var q = new PowerLearn(); q.Load(path);
            Assert.StartsWith("quarantined", q.FileState);
            Assert.True(Directory.GetFiles(dir, "power-learned.json.bad-*").Length == 1);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Nothing_is_learned_within_10_s_of_a_flip()
    {
        var learner = new PowerLearner(new PowerLearn());
        var s = new PowerSample { T = 100, OnAC = true, Class = SupplyClass.Barrel, AdapterW = 230, SinceFlip = 5, RateKnown = true, RateMw = 38000, BatOk = true, SocPct = 60, FullMwh = 69449, CapMwh = 41669 };
        learner.Learn(s, null, null, 30);
        Assert.Equal(0, learner.Writes);
        learner.Learn(s with { SinceFlip = 40 }, null, null, 30);
        Assert.Equal(1, learner.Writes);
    }
}

public class CpuRowTests
{
    /// <summary>The reference pools every single-thread app: a lighter app at full turbo draws fewer watts without being slowed.</summary>
    [Fact]
    public void Fewer_package_watts_at_full_clock_is_not_held_back()
    {
        var light = Busy(15, 4200);
        Assert.Equal(CpuCode.C9_Free, CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, OnAC = false, Class = SupplyClass.Battery, D = light, RefAC = Ref }).Code);
        Assert.Equal(CpuCode.C9_Free, CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, Class = SupplyClass.UsbC, AdapterW = 65, D = light, RefAC = Ref }).Code);
        // R10: no CPU_LIMITED_BATTERY either
        var v = new GpuVerdict(); VerdictOut o = new();
        var refs = new GpuRefs { CpuRefValid = true, CpuRefW = 45, CpuRefMHz = 4200 };
        for (double t = 0; t <= 30; t += 2)
            o = v.Push(new PowerSample { T = t, OnAC = false, DState = 4, FgKnown = true, FgPid = 7, FgCores = 1, CpuOk = true, PkgW = 15, FreqMHz = 4200 }, refs);
        Assert.Equal(St.ASLEEP, o.Shown);
    }

    private static Derived Busy(double pkg, double freq, bool hot = false) => new()
    {
        PkgW = pkg, Freq = freq, FgCores = 0.97, Busy1T = true, CpuBusy = true, PkgRange = 1, Plateau = pkg >= 15, TcpuHotFrac = hot ? 1 : 0, PkgFell = hot,
    };
    private static readonly CpuEntry Ref = new() { W = 45, MHz = 4200, Secs = 600, Updated = "2026-10-03" };

    [Fact]
    public void Rules_C0_to_C9()
    {
        Assert.Equal(CpuCode.C0_NotRead, CpuVerdict.Evaluate(new CpuInputs { CpuOk = false }).Code);
        Assert.Equal(CpuCode.C1_Private, CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, FgPrivate = true, D = Busy(20, 2000) }).Code);
        Assert.Equal(CpuCode.C2_Idle, CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, D = new Derived { PkgW = 5 } }).Code);
        var hot = CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, D = Busy(40, 3500, true), CpuC = 98, FanRpm = 3300 });
        Assert.Equal((CpuCode.C3_Hot, "CPU at 98 \u00B0C; fans 3,300 of 5,000 RPM (real speed)"), (hot.Code, hot.Reason));
        var usb = CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, D = Busy(38, 3500), Class = SupplyClass.UsbC, AdapterW = 65, RefAC = Ref });
        Assert.Equal((CpuCode.C4_Charger, "On USB-C (65 W, Razer EC): 38 W vs ~45 W on the 230 W charger (learned Oct 3)"), (usb.Code, usb.Reason));
        var win = CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, OnAC = false, Class = SupplyClass.Battery, D = Busy(18, 1840), CeilingMHz = 1843.2, CapPct = 80, BoostOff = true });
        Assert.Equal((CpuCode.C5_Windows, "Windows caps the CPU at 80% on battery (about 1.8 GHz, boost off)"), (win.Code, win.Reason));
        var prof = CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, OnAC = false, Class = SupplyClass.Battery, D = Busy(18, 3000), ProfileEngaged = true, RefAC = Ref });
        Assert.Equal((CpuCode.C6_Profile, "Battery profile: Razer Custom, CPU Low. Plugged in: ~45 W (learned Oct 3)"), (prof.Code, prof.Reason));
        var bat = CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, OnAC = false, Class = SupplyClass.Battery, D = Busy(18, 2100), RefAC = new CpuEntry { W = 45, MHz = 4200, Secs = 600, Updated = "2026-10-02" } });
        Assert.Equal((CpuCode.C7_Battery, "On battery: 18 W, 2.1 GHz vs ~45 W, 4.2 GHz plugged in (learned Oct 2)"), (bat.Code, bat.Reason));
        Assert.Equal(CpuCode.C8_Learning, CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, OnAC = false, Class = SupplyClass.Battery, D = Busy(18, 2100) }).Code);
        Assert.Equal(CpuCode.C9_Free, CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, Class = SupplyClass.Barrel, D = Busy(44, 4200), RefAC = Ref }).Code);
        Assert.Equal(1843.2, CpuVerdict.Ceiling(2304, 80, 0), 3);
        // unconfirmed USB-C carries the question mark
        Assert.StartsWith("On USB-C? (65 W", CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, D = Busy(38, 3500), Class = SupplyClass.UsbC, AdapterW = 65, RefAC = Ref, UsbcConfirmed = false }).Reason);
        Assert.Equal("Probably held back by the USB-C charger: the battery is draining while plugged in",
            CpuVerdict.Evaluate(new CpuInputs { CpuOk = true, D = Busy(44, 4200), Class = SupplyClass.UsbC, AdapterW = 65, RefAC = Ref, DrainingOnAC = true }).Reason);
        Assert.Equal(-1, CpuVerdict.Ceiling(2304, 100, 3));
    }
}

/// <summary>Spec 3 / R7: St and Sub carry GPU Glance's names AND numbering (Mode S decodes them as ints).</summary>
public class EnumParityTests
{
    private static readonly string CommonH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Tools", "GpuGlance", "src", "common.h");

    private static List<string> EnumNames(string text, string name)
    {
        var m = Regex.Match(text, @"enum\s+class\s+" + name + @"\s*:\s*int\s*\{(?<b>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(m.Success, "enum " + name + " not found in common.h");
        string body = Regex.Replace(m.Groups["b"].Value, @"//[^\n]*", "");
        return body.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0 && x != "COUNT").ToList();
    }

    [Fact]
    public void St_and_Sub_match_GPU_Glance_common_h()
    {
        if (!File.Exists(CommonH)) return;   // GPU Glance is not checked out on this machine
        string h = File.ReadAllText(CommonH);
        Assert.Equal(EnumNames(h, "St"), Enum.GetNames<St>().ToList());
        Assert.Equal(EnumNames(h, "Sub"), Enum.GetNames<Sub>().ToList());
    }
}

/// <summary>R4: the private-list result never reaches the dump, the log or the probe output.</summary>
public class DumpPrivacyTests
{
    [Fact]
    public void A_private_app_prints_exactly_like_an_unreadable_one()
    {
        var priv = new PowerSample { FgKnown = true, FgPrivate = true, FgPid = 42, FgCores = 0.97 };
        var unknown = new PowerSample { FgKnown = false, FgPid = 42, FgCores = 0 };
        var d = new Derived();
        Assert.Equal(PowerDump.FgFields(unknown, CpuCode.C2_Idle, d), PowerDump.FgFields(priv, CpuCode.C1_Private, d));
        Assert.DoesNotContain("Priv", PowerDump.FgFields(priv, CpuCode.C1_Private, d));
        Assert.Equal(0, PowerDump.FgCoresShown(priv));
    }

    /// <summary>Source scan: the Tray power code never formats FgPrivate into text.</summary>
    [Fact]
    public void Tray_power_code_never_prints_FgPrivate()
    {
        string root = Path.GetFullPath(Path.Combine(Fixture.Dir(), "..", ".."));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "BladeCtl.Tray", "Power"), "*.cs"))
            foreach (var line in File.ReadAllLines(f))
                Assert.False(line.Contains("FgPrivate") && (line.Contains("$\"") || line.Contains("Out(") || line.Contains("Log.")), $"{Path.GetFileName(f)}: {line.Trim()}");
    }
}

/// <summary>Supply trackers: class changes through a 0 W read, and the drain figure.</summary>
public class SupplyTrackerTests
{
    private static PowerSample Ac(double t, SupplyClass c, int w, int rate = 20000) => new()
    {
        T = t, OnAC = true, Class = c, AdapterW = w, BatOk = true, RateKnown = true, RateMw = rate, SocPct = 60, FullMwh = 69400, CapMwh = 41640,
    };

    [Fact]
    public void A_swap_seen_through_an_unidentified_read_is_a_class_change()
    {
        var tr = new SupplyTrackers();
        tr.Note(Ac(0, SupplyClass.UsbC, 65)); tr.Note(Ac(2, SupplyClass.UsbC, 65));
        tr.Note(Ac(4, SupplyClass.AcUnknown, 0)); Assert.False(tr.ClassChangedNow);
        tr.Note(Ac(6, SupplyClass.Barrel, 230)); Assert.True(tr.ClassChangedNow);
        Assert.Equal(6, tr.FlipAt);
    }

    [Fact]
    public void Plugging_in_after_battery_is_not_also_a_class_change()
    {
        var tr = new SupplyTrackers();
        tr.Note(Ac(0, SupplyClass.Barrel, 230));
        tr.Note(new PowerSample { T = 2, OnAC = false, Class = SupplyClass.Battery });
        tr.Note(Ac(4, SupplyClass.Settling, 0));
        tr.Note(Ac(6, SupplyClass.UsbC, 65)); Assert.False(tr.ClassChangedNow);
    }

    [Fact]
    public void Drain_is_the_6_s_mean_of_the_negative_reads()
    {
        var tr = new SupplyTrackers();
        var a = Ac(0, SupplyClass.Barrel, 230, -9000); tr.Note(a);
        var b = Ac(2, SupplyClass.Barrel, 230, 0) with { RateKnown = false }; tr.Note(b);
        var c = Ac(4, SupplyClass.Barrel, 230, -400); tr.Note(c);
        Assert.Equal(4.7, c.DrainW, 3);
        var e = Ac(12, SupplyClass.Barrel, 230, 1000); tr.Note(e);
        Assert.Equal(0, e.DrainW);
    }
}
