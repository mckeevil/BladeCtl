namespace BladeCtl.Core.Power;

/// <summary>
/// Razer EC "Get Adapter Wattage Level" (class 0x07, id 0x8C, data size 2): args[0] = connected adapter level,
/// args[1] = recommended level. Level -> watts from Synapse's module for this laptop (spec 8.1). The table is NOT
/// monotonic (15 = 310 W, 17 = 280 W), so everything compares watts, never levels.
/// Every class-0x07 GET answers SUCCESS on this EC (128 of 128 ids), so SUCCESS proves nothing: validate by value.
/// </summary>
public static class RazerAdapter
{
    private static readonly Dictionary<int, int> LevelWatts = new()
    {
        [0] = 0, [1] = 40, [2] = 45, [3] = 60, [4] = 65, [5] = 80, [6] = 90, [7] = 100, [8] = 120, [9] = 130, [10] = 150,
        [11] = 180, [12] = 200, [13] = 230, [14] = 250, [15] = 310, [17] = 280, [18] = 330, [19] = 400,
    };

    /// <summary>Watts for a level byte; null = unknown (16, 255 and anything not in the table).</summary>
    public static int? Watts(byte level) => LevelWatts.TryGetValue(level, out var w) ? w : null;

    public sealed record Decoded(SupplyClass Class, int AdapterW, int RecW, string? Anomaly);

    /// <summary>
    /// Classify one EC read against what Windows says (spec 8.1). <paramref name="answered"/> = a SUCCESS reply was
    /// received; <paramref name="inBurst"/> = still inside the post-plug read burst (0 W then means "settling").
    /// </summary>
    public static Decoded Classify(bool windowsOnAC, bool answered, byte level, byte rec, bool inBurst)
    {
        int? recW0 = Watts(rec);
        string? anomaly = null;
        int recW = recW0 is int r && r > 0 ? r : 230;
        if (recW0 is not int rr || rr <= 0) anomaly = $"recommended assumed 230 (raw {rec:X2})";

        if (!windowsOnAC)
        {
            int? w = answered ? Watts(level) : null;
            if (answered && (w is null || w > 0))
                anomaly = $"EC reports adapter {(w is int ww ? ww + " W" : "level " + level.ToString("X2"))} while Windows says battery";
            return new(SupplyClass.Battery, 0, recW, anomaly);
        }
        if (!answered) return new(inBurst ? SupplyClass.Settling : SupplyClass.AcUnknown, 0, recW, anomaly);
        int? aw = Watts(level);
        if (aw is null) return new(inBurst ? SupplyClass.Settling : SupplyClass.AcUnknown, 0, recW, $"adapter level {level:X2} not in the table" + (anomaly is null ? "" : "; " + anomaly));
        if (aw == 0) return new(inBurst ? SupplyClass.Settling : SupplyClass.AcUnknown, 0, recW, anomaly);
        if (aw >= recW) return new(SupplyClass.Barrel, aw.Value, recW, anomaly);
        return new(SupplyClass.UsbC, aw.Value, recW, anomaly);
    }

    /// <summary>Learning-key part for the power source: B230 / C65 / ACU / DC (spec 9).</summary>
    public static string SrcKey(SupplyClass c, int adapterW) => c switch
    {
        SupplyClass.Battery => "DC",
        SupplyClass.Barrel => "B" + adapterW,
        SupplyClass.UsbC => "C" + adapterW,
        _ => "ACU",
    };
}
