namespace BladeCtl.Core.Power;

/// <summary>
/// Time-qualified supply flags shared by the verdict, the supply row, the CPU row and learning (spec 6):
/// drainingOnAC (30 s), notChargingOnAC (20 s, SOC &lt; 95), and seconds since the last AC/DC or charger-class flip.
/// Fed every sample, live or closed, so the timers keep running while the window is shut.
/// </summary>
public sealed class SupplyTrackers
{
    private bool? _prevAC;
    // the last IDENTIFIED class (barrel / USB-C): a 0 W or unanswered read in between (AcUnknown, Settling) must not hide a
    // charger swap, so only identified reads update it; an AC/DC flip clears it (the first identification after a plug-in
    // is part of that flip, not a second event)
    private SupplyClass _prevClass = SupplyClass.AcUnknown;
    private int _prevW;
    private double _drainSince = -1, _notChgSince = -1;
    // battery discharge over the last 6 s (GPU Glance verdict.cpp: the drain figure is the window mean of the negative reads)
    private readonly Queue<(double T, double W)> _rates = new();
    public double FlipAt { get; private set; } = -1e9;
    public double AcDcAt { get; private set; } = -1e9;
    /// <summary>True on the sample where the charger class changed without an AC/DC flip (barrel pulled, dock stays).</summary>
    public bool ClassChangedNow { get; private set; }
    public string? ClassChangeFrom { get; private set; }

    public static bool Identified(SupplyClass c) => c is SupplyClass.Barrel or SupplyClass.UsbC;

    /// <summary>Resume / explicit reset: the next sample counts as a flip for "never learn within 10 s".</summary>
    public void MarkFlip(double now) { FlipAt = now; _drainSince = _notChgSince = -1; _rates.Clear(); }

    public void Note(PowerSample s)
    {
        double now = s.T;
        ClassChangedNow = false; ClassChangeFrom = null;
        if (_prevAC.HasValue && _prevAC.Value != s.OnAC) { AcDcAt = now; FlipAt = now; _drainSince = _notChgSince = -1; _prevClass = SupplyClass.AcUnknown; _prevW = 0; _rates.Clear(); }
        else if (s.OnAC && Identified(_prevClass) && Identified(s.Class) && (_prevClass != s.Class || _prevW != s.AdapterW))
        {
            FlipAt = now; ClassChangedNow = true; ClassChangeFrom = _prevClass == SupplyClass.UsbC ? $"USB-C {_prevW} W" : $"{_prevW} W charger";
            _drainSince = _notChgSince = -1;
        }
        _prevAC = s.OnAC;
        if (Identified(s.Class)) { _prevClass = s.Class; _prevW = s.AdapterW; }

        bool drainNow = s.OnAC && s.BatOk && ((s.BatState & 0x2) != 0 || (s.RateKnown && s.RateMw <= -3000));
        if (drainNow) { if (_drainSince < 0) _drainSince = now; } else _drainSince = -1;
        s.DrainSince = _drainSince;
        s.DrainingOnAC = _drainSince >= 0 && now - _drainSince >= 30 - 0.01;
        _rates.Enqueue((now, s.BatOk && s.RateKnown && s.RateMw < 0 ? -s.RateMw / 1000.0 : double.NaN));
        while (_rates.Count > 0 && now - _rates.Peek().T >= 5.5) _rates.Dequeue();
        { double sum = 0; int n = 0; foreach (var r in _rates) if (!double.IsNaN(r.W)) { sum += r.W; n++; } s.DrainW = n > 0 ? sum / n : 0; }

        int soc = s.Soc;
        bool notChg = s.OnAC && s.BatOk && s.RateKnown && s.RateMw <= 0 && soc >= 0 && soc < 95;
        if (notChg) { if (_notChgSince < 0) _notChgSince = now; } else _notChgSince = -1;
        s.NotChargingSince = _notChgSince;
        s.NotChargingOnAC = _notChgSince >= 0 && now - _notChgSince >= 20 - 0.01;

        s.FlipAt = FlipAt; s.AcDcAt = AcDcAt;
        s.SinceFlip = now - FlipAt; s.SinceAcDc = now - AcDcAt;
    }
}

/// <summary>Supply row decision table S1-S8 (spec 7.1), first match wins.</summary>
public static class SupplyTable
{
    /// <param name="barrelBandMw">learned barrel charge power for the current 5% SOC band (L4), or &lt;= 0 when not learned</param>
    /// <param name="heldSince">out: when the raw condition itself started (for the 30 s hysteresis), or -1</param>
    public static SupplyState Raw(PowerSample s, double barrelBandMw, out double heldSince)
    {
        heldSince = -1;
        if (!s.OnAC) return SupplyState.BATTERY;
        if (s.Class == SupplyClass.Settling) return SupplyState.IDENTIFYING;
        if (s.DrainingOnAC) { heldSince = s.DrainSince; return SupplyState.CANT_KEEP_UP; }
        int soc = s.Soc;
        if (s.Class == SupplyClass.UsbC && soc >= 0 && soc < 80 && barrelBandMw > 0 && s.RateKnown && s.RateMw < 0.5 * barrelBandMw)
            return SupplyState.AT_ITS_LIMIT;
        if (s.RateKnown && s.RateMw >= 1000) return SupplyState.CHARGING;
        if (s.RateKnown && Math.Abs(s.RateMw) < 1000 && soc >= 95) return SupplyState.FULL;
        if (s.NotChargingOnAC) { heldSince = s.NotChargingSince; return SupplyState.NOT_CHARGING; }
        return SupplyState.PLUGGED_IN;
    }

    public static bool IsAmber(SupplyState st) => st is SupplyState.CANT_KEEP_UP or SupplyState.AT_ITS_LIMIT or SupplyState.NOT_CHARGING;

    /// <summary>The supply row is shown on AC when the charger is not the barrel, or the supply is in trouble.</summary>
    public static bool RowVisible(PowerSample s, SupplyState st) => s.OnAC && (s.Class != SupplyClass.Barrel || IsAmber(st));

    public static string Word(SupplyState st) => st switch
    {
        SupplyState.BATTERY => "BATTERY",
        SupplyState.IDENTIFYING => "IDENTIFYING",
        SupplyState.CANT_KEEP_UP => "CAN'T KEEP UP",
        SupplyState.AT_ITS_LIMIT => "AT ITS LIMIT",
        SupplyState.CHARGING => "CHARGING",
        SupplyState.FULL => "FULL",
        SupplyState.NOT_CHARGING => "NOT CHARGING",
        _ => "PLUGGED IN",
    };
}
