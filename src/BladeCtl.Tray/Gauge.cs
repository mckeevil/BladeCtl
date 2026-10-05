using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BladeCtl.Tray;

/// <summary>
/// Fan gauge: a 220° arc, the current RPM as a big numeral, an optional floor tick.
/// Drawn directly so it costs nothing and needs no template.
/// </summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Gauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ManualProperty = DependencyProperty.Register(
        nameof(Manual), typeof(bool), typeof(Gauge), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FloorProperty = DependencyProperty.Register(
        nameof(Floor), typeof(double), typeof(Gauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ConnectedProperty = DependencyProperty.Register(
        nameof(Connected), typeof(bool), typeof(Gauge), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Manual { get => (bool)GetValue(ManualProperty); set => SetValue(ManualProperty, value); }
    public double Floor { get => (double)GetValue(FloorProperty); set => SetValue(FloorProperty, value); }
    public bool Connected { get => (bool)GetValue(ConnectedProperty); set => SetValue(ConnectedProperty, value); }

    private const double MaxRpm = 5000;
    private const double StartDeg = 200, SweepDeg = 220;

    private Brush B(string key) => (Brush)FindResource(key);

    protected override Size MeasureOverride(Size availableSize) => new(200, 132);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : 200;
        var c = new Point(w / 2, 100);
        const double r = 74;

        var track = new Pen(B("LineStrongBrush"), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(c, r, StartDeg, StartDeg - SweepDeg));

        double frac = Math.Clamp(Value / MaxRpm, 0, 1);
        if (Connected && frac > 0.005)
        {
            var val = new Pen(B(Manual ? "AccentBrush" : "Text2Brush"), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, val, Arc(c, r, StartDeg, StartDeg - SweepDeg * frac));
        }

        if (Floor > 0)
        {
            double a = StartDeg - SweepDeg * Math.Clamp(Floor / MaxRpm, 0, 1);
            dc.DrawLine(new Pen(B("WarnBrush"), 2), P(c, 62, a), P(c, 86, a));
        }

        var mono = new FontFamily("Cascadia Mono, Consolas");
        var head = new FontFamily("Bahnschrift, Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        string big = Connected ? ((int)Value).ToString(CultureInfo.InvariantCulture) : "--";
        var ft = new FormattedText(big, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(mono, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 30, B(Connected ? "TextBrush" : "Text3Brush"), dpi);
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, 62));

        string small = !Connected ? "NO DEVICE" : Manual ? "MANUAL FLOOR" : "RPM · AUTO";
        var fs = new FormattedText(small, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(head, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11, B(Manual ? "AccentBrush" : "Text3Brush"), dpi);
        dc.DrawText(fs, new Point(c.X - fs.Width / 2, 100));
    }

    private static Point P(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a));
    }

    private static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(c, r, fromDeg), false, false);
            ctx.ArcTo(P(c, r, toDeg), new Size(r, r), 0, fromDeg - toDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
