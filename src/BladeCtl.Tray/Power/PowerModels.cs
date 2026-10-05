namespace BladeCtl.Tray.Power;

// Immutable render models for the Power card. Custom elements get these through a dependency property, so an equal
// model (record value equality) causes no redraw. Nothing in here ever holds a process name.

public sealed record HeaderModel(string Glyph, string Text, string Tip, bool Dotted, string Provenance);

public sealed record StripModel(string Kind, string Glyph, string Status, string Action, bool Probably, string? ButtonId, string ButtonText);

public sealed record FlowModel(
    bool OnAC, string SourceGlyph, double SupplyW, bool SupplyEstimated, string SupplyLabel, string SupplySub, bool SourceWarn,
    double BatW, string BatLabel, string BatSub, bool BatWarn, bool Charging, int Soc, string Eta,
    double CpuW, double GpuW, double RestW, bool RestEstimated, string StackText, string Summary);

// ShowMissing: the row is held back by power right now (GPU HELD BACK, supply amber); only then is the gap between the
// limit and the 230 W reference drawn as "watts you're missing".
public sealed record BarModel(double Scale, double Now, bool NowEstimated, double Limit, bool LimitLearned, string LimitLabel,
                              double Ref, bool Asleep, bool Unknown, string Hue, string Tip, bool ShowMissing = false)
{
    public static readonly BarModel Empty = new(100, -1, false, -1, false, "", -1, false, true, "cpu", "");
    public bool MissingZone => ShowMissing && !Asleep && Ref > 0 && Limit > 0 && Ref - Limit >= 5;
}

public sealed record RowModel(bool Visible, string Name, string Numbers, string Word, string Kind, string Glyph, string Reason, BarModel Bar)
{
    public static readonly RowModel Hidden = new(false, "", "", "", "none", "", "", BarModel.Empty);
    public bool HasChip => Word.Length > 0;
    public bool HasReason => Reason.Length > 0;
    /// <summary>The reason line is shown under HELD BACK / HOT / AT LIMIT rows only; every other row keeps it in its tooltip.</summary>
    public bool ShowReason => Reason.Length > 0 && Kind is "warn" or "accent";
    public string? ReasonTip => Reason.Length > 0 ? Reason : null;
}

public sealed record ChipModel(string Text, bool Cause);

/// <summary>One sparkline point: seconds ago, signed battery watts (+ charging), kind 0 charging / 1 discharging on battery / 2 draining on AC.</summary>
public readonly record struct SparkPt(double Ago, double W, int Kind, bool Closed, bool Flip);

public sealed class SparkModel : IEquatable<SparkModel>
{
    public SparkModel(IReadOnlyList<SparkPt> pts, double range, string rangeText, string tip) { Pts = pts; Range = range; RangeText = rangeText; Tip = tip; }
    public IReadOnlyList<SparkPt> Pts { get; }
    public double Range { get; }
    public string RangeText { get; }
    public string Tip { get; }
    public static readonly SparkModel Empty = new(Array.Empty<SparkPt>(), 10, "", "battery rate, last 10 minutes");
    public bool Equals(SparkModel? o) => o != null && Range == o.Range && RangeText == o.RangeText && Tip == o.Tip && Pts.SequenceEqual(o.Pts);
    public override bool Equals(object? obj) => Equals(obj as SparkModel);
    public override int GetHashCode() => HashCode.Combine(Range, RangeText, Pts.Count);
}

public sealed class PowerView
{
    public HeaderModel Header { get; init; } = new("unknown", "Power", "", false, "");
    public StripModel Strip { get; init; } = new("grey", "unknown", "Reading the power state…", "", false, null, "");
    public FlowModel? Flow { get; init; }
    public RowModel Cpu { get; init; } = RowModel.Hidden;
    public RowModel Gpu { get; init; } = RowModel.Hidden;
    public RowModel Supply { get; init; } = RowModel.Hidden;
    public IReadOnlyList<ChipModel> Chips { get; init; } = Array.Empty<ChipModel>();
    public SparkModel Spark { get; init; } = SparkModel.Empty;
    public string BatteryLine { get; init; } = "";
    public string BatteryTip { get; init; } = "";
    public string HealthLine { get; init; } = "";
    public string Footer { get; init; } = "Battery: Windows · CPU: Intel RAPL · GPU: NVIDIA, only while awake · charger: Razer EC";
}
