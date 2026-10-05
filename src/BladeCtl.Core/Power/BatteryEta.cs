using System.Globalization;

namespace BladeCtl.Core.Power;

public struct ChargeBin { public double Mw; public int N; }

/// <summary>
/// Battery line (spec 10): a port of GPU Glance's battery model as built 2026-10-05 (SPEC 22.1, src\learn.cpp
/// ChargeEtaSec / FmtDuration / BatteryModel) so both apps print the same ETA from the same inputs: settling reads never
/// seed the charge EMA, a charge power under 1.5 W gets no ETA ("topping off" / "charging"), an ETA over 24 h is unknown,
/// and CHARGING with no charge power drops the old EMA. BladeCtl adds "(laptop using ~X W)" to GPU Glance's
/// "plugged in, not charging" when it knows the whole laptop's use.
/// </summary>
public static class BatteryEta
{
    public const int Bins = 20;   // 5% SOC each

    /// <summary>
    /// Seconds to full. Learned bins (n &gt;= 3) are used where present; the current bin uses the live smoothed power;
    /// unlearned bins use a knee model (live power below the knee, then falling linearly to endFrac of the knee power at
    /// 100%). When live power below the knee is more than 20% under the learned bin, learned bins are scaled by live/learned.
    /// </summary>
    public static double ChargeEtaSec(double capMwh, double fullMwh, double liveMw, ChargeBin[]? curve, double knee = 0.8, double endFrac = 0.15)
    {
        if (fullMwh <= 0 || capMwh < 0 || liveMw <= 0) return -1;
        double soc = capMwh / fullMwh;
        if (soc >= 0.999) return 0;
        soc = Math.Max(0.0, soc);
        int cur = Math.Min(Bins - 1, (int)(soc * Bins));
        bool Learned(int b) => curve != null && b < curve.Length && curve[b].N >= 3 && curve[b].Mw > 0;
        double scale = 1.0;
        if (soc < knee && Learned(cur) && liveMw < 0.8 * curve![cur].Mw) scale = liveMw / curve[cur].Mw;
        double pk = liveMw;
        if (soc >= knee && knee < 1.0) { double f = 1.0 - (1.0 - endFrac) * (soc - knee) / (1.0 - knee); pk = liveMw / Math.Max(f, endFrac); }
        double Model(double s) => (s < knee || knee >= 1.0) ? pk : pk * (1.0 - (1.0 - endFrac) * (s - knee) / (1.0 - knee));
        const int steps = 2000;
        double ds = (1.0 - soc) / steps, hours = 0;
        for (int i = 0; i < steps; i++)
        {
            double s = soc + (i + 0.5) * ds;
            int b = Math.Min(Bins - 1, (int)(s * Bins + 1e-9));
            double p;
            if (b == cur) p = s < knee ? liveMw : Model(s);
            else if (Learned(b)) p = curve![b].Mw * scale;
            else p = Model(s);
            p = Math.Max(p, 1.0);
            hours += ds * fullMwh / p;
        }
        return hours * 3600.0;
    }

    /// <summary>"~8 min", "~1 h 05 min": under 10 min to the minute, otherwise to 5 min.</summary>
    public static string FmtDuration(double sec)
    {
        if (sec < 0) return "";
        double m = sec / 60.0;
        long n = m < 10 ? Math.Max(1L, (long)Math.Round(m, MidpointRounding.AwayFromZero)) : (long)Math.Round(m / 5.0, MidpointRounding.AwayFromZero) * 5;
        if (n < 60) return $"~{n} min";
        return $"~{n / 60} h {n % 60:00} min";
    }
}

/// <summary>Smoothed charge / discharge power and the battery line. Call Update on every sample (live or closed).</summary>
public sealed class BatteryModel
{
    private double _emaC = -1, _emaD = -1, _lastT = -1, _plugAt = -1e9, _lastFull = -1;
    private bool _lastAC = true, _haveAC;
    private double _emaW;   // signed EMA (tau 60 s) for %/h
    private bool _haveEma;

    public ChargeBin[]? Curve { get; set; }
    public string CurveSource { get; set; } = "model";
    public double PlugAt => _plugAt;

    public void OnPlugEvent(double now) { _plugAt = now; _emaC = -1; _emaD = -1; _haveEma = false; }

    public void Update(double now, PowerSample s)
    {
        if (_haveAC && s.OnAC != _lastAC) OnPlugEvent(now);
        _lastAC = s.OnAC; _haveAC = true;
        if (!s.BatOk) return;
        double dt = _lastT < 0 ? 0 : now - _lastT;
        _lastT = now;
        double a = dt <= 0 ? 1.0 : 1.0 - Math.Exp(-dt / 60.0);
        // a full-charge capacity that jumped by more than 10% (battery re-read) makes this read unreliable: skip it
        bool fullJump = _lastFull > 0 && s.FullMwh > 0 && Math.Abs(s.FullMwh - _lastFull) > 0.1 * _lastFull;
        if (s.FullMwh > 0) _lastFull = s.FullMwh;
        if (fullJump) return;
        bool charging = s.OnAC && ((s.BatState & 0x4) != 0 || (s.RateKnown && s.RateMw > 0));
        bool settling = Settling(now);   // the charge rate settles for 30 s after a plug event: those reads never seed the EMA
        if (charging && s.RateKnown && s.RateMw > 0)
        {
            double x = s.RateMw;
            if (!settling) _emaC = _emaC < 0 ? x : _emaC + a * (x - _emaC);
        }
        else if (!charging || (s.RateKnown && s.RateMw <= 0)) _emaC = -1;   // not charging, or CHARGING with no charge power (top-off)
        if (!s.OnAC && s.RateKnown && s.RateMw < 0)
        {
            double x = -s.RateMw;
            _emaD = _emaD < 0 ? x : _emaD + a * (x - _emaD);
        }
        else if (s.OnAC) _emaD = -1;
        if (s.RateKnown)
        {
            _emaW = !_haveEma ? s.RateMw : _emaW + a * (s.RateMw - _emaW);
            _haveEma = true;
        }
    }

    /// <summary>Signed smoothed battery power in W (+ charging), or null.</summary>
    public double? EmaW => _haveEma ? _emaW / 1000.0 : null;
    public double DischargeW => _emaD > 0 ? _emaD / 1000.0 : -1;
    public double ChargeW => _emaC > 0 ? _emaC / 1000.0 : -1;
    public bool Settling(double now) => now - _plugAt < 30;

    /// <summary>Signed %/h text: "+54 %/h charging", "−26 %/h".</summary>
    public static string PctPerHour(double w, uint fullMwh)
    {
        if (fullMwh == 0) return "";
        double p = 100.0 * w / (fullMwh / 1000.0);
        string n = Math.Abs(p).ToString("F0", CultureInfo.InvariantCulture);
        return p >= 0.5 ? $"+{n} %/h charging" : p <= -0.5 ? $"−{n} %/h" : "0 %/h";
    }

    /// <summary>Charge power under this gets no ETA or wattage (GPU Glance: trickle / top-off).</summary>
    public const double TrickleMw = 1500;

    private double ChargeEta(PowerSample s, double live)
    {
        if (live < TrickleMw || !s.CapKnown) return -1;
        double eta = BatteryEta.ChargeEtaSec(s.CapMwh, s.FullMwh, live, Curve);
        return eta > 24 * 3600.0 ? -1 : eta;
    }

    /// <summary>ETA in seconds, or -1. Charging: to full; on battery: until empty.</summary>
    public double EtaSec(double now, PowerSample s)
    {
        if (s.OnAC)
        {
            bool charging = (s.BatState & 0x4) != 0 || (s.RateKnown && s.RateMw > 0);
            if (!charging || Settling(now)) return -1;
            double live = _emaC > 0 ? _emaC : (s.RateKnown && s.RateMw > 0 ? s.RateMw : -1);
            return ChargeEta(s, live);
        }
        double dis = _emaD > 0 ? _emaD : (s.RateKnown && s.RateMw < 0 ? -s.RateMw : -1);
        return dis > 0 && s.CapKnown && s.CapMwh > 0 ? s.CapMwh / dis * 3600.0 : -1;
    }

    /// <summary>The battery line, exact spec 10 text. <paramref name="useW"/> = whole-laptop watts (estimate) or &lt;= 0.</summary>
    public string Line(double now, PowerSample s, double useW)
    {
        int pct = s.Soc;
        if (pct < 0) return "";
        const string dot = " · ";
        if (s.OnAC)
        {
            bool charging = (s.BatState & 0x4) != 0 || (s.RateKnown && s.RateMw > 0);
            if (charging)
            {
                double live = _emaC > 0 ? _emaC : (s.RateKnown && s.RateMw > 0 ? s.RateMw : -1);
                if (Settling(now)) return $"Battery {pct}%{dot}charging, estimating time to full";
                // trickle / top-off: no honest ETA or wattage from a charge power this small
                if (live < TrickleMw) return pct >= 95 ? $"Battery {pct}%{dot}topping off" : $"Battery {pct}%{dot}charging";
                double eta = ChargeEta(s, live);
                if (eta > 0) return $"Battery {pct}%{dot}full in {BatteryEta.FmtDuration(eta)} (charging {W(live / 1000.0)} W)";
                return $"Battery {pct}%{dot}charging {W(live / 1000.0)} W";
            }
            if (s.NotChargingOnAC)
            {
                // "charger can't keep up" only when the battery is actually draining (GPU Glance); a paused charge is not blamed on it
                if (s.RateKnown && s.RateMw < -500)
                    return $"Battery {pct}%{dot}plugged in but not charging (draining {W(-s.RateMw / 1000.0)} W, charger can't keep up)";
                return useW > 0 ? $"Battery {pct}%{dot}plugged in, not charging (laptop using ~{W(useW)} W)" : $"Battery {pct}%{dot}plugged in, not charging";
            }
            if (pct >= 100) return $"Battery 100%{dot}full";
            if (pct >= 95) return $"Battery {pct}%{dot}topping off";
            return $"Battery {pct}%{dot}plugged in";
        }
        double dis = _emaD > 0 ? _emaD : (s.RateKnown && s.RateMw < 0 ? -s.RateMw : -1);
        if (dis > 0 && s.CapKnown && s.CapMwh > 0)
            return $"Battery {pct}%{dot}{BatteryEta.FmtDuration(s.CapMwh / dis * 3600.0)} left at {W(dis / 1000.0)} W";
        return $"Battery {pct}%{dot}on battery";
    }

    public static string HealthLine(PowerSample s) =>
        s.FullMwh == 0 || s.DesignMwh == 0 ? "" :
        string.Format(CultureInfo.InvariantCulture, "Full charge {0:F1} of {1:F1} Wh ({2:F0}%)", s.FullMwh / 1000.0, s.DesignMwh / 1000.0, 100.0 * s.FullMwh / s.DesignMwh);

    public static string WindowsEstimate(PowerSample s)
    {
        if (s.OnAC || s.WinEstSec <= 0) return "";
        long m = s.WinEstSec / 60;
        return m >= 60 ? $"Windows estimates {m / 60} h {m % 60:00} min" : $"Windows estimates {m} min";
    }

    private static string W(double w) => Math.Max(0, w).ToString("F0", CultureInfo.InvariantCulture);
}
