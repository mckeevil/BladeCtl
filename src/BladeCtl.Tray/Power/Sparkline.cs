using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace BladeCtl.Tray.Power;

/// <summary>
/// Signed battery watts over the last 10 minutes (spec 11.3): charging in the accent colour over a dim accent area,
/// discharging on battery in grey with no area, draining while plugged in in amber over a filled area (so the two never
/// differ by colour alone). Dashed zero line; a vertical line and a plug / battery glyph at each AC/DC flip; the line
/// breaks at gaps over 45 s; closed-window stretches (30 s samples) are drawn dotted.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(SparkModel), typeof(Sparkline),
        new FrameworkPropertyMetadata(SparkModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Sparkline)d).OnModel()));
    public SparkModel Model { get => (SparkModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    private const double H = 40, Span = 600;
    private Brush B(string key) => (Brush)FindResource(key);

    private void OnModel() { AutomationProperties.SetName(this, Model?.Tip ?? ""); ToolTip = Model?.Tip; }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 300 : available.Width, H);

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(40, ActualWidth);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, H));
        var m = Model ?? SparkModel.Empty;
        double range = m.Range > 0 ? m.Range : 10;
        double Y(double watts) => H / 2 - Math.Clamp(watts / range, -1, 1) * (H / 2 - 2);
        double X(double ago) => w * (1 - Math.Clamp(ago / Span, 0, 1));
        dc.DrawLine(new Pen(B("Text3Brush"), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) }, new Point(0, H / 2), new Point(w, H / 2));
        var pts = m.Pts;
        if (pts.Count == 0) return;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p.Flip)
            {
                // AC/DC flip: a vertical line plus an 8 px plug / battery glyph, so the direction never relies on colour
                double fx = X(p.Ago);
                dc.DrawLine(new Pen(B("LineStrongBrush"), 1), new Point(fx, 0), new Point(fx, H));
                if (TryFindResource(p.Kind == 1 ? "GlyphBattery" : "GlyphPlug") is Geometry glyph && !glyph.Bounds.IsEmpty)
                {
                    var bnd = glyph.Bounds; double sc = 8 / Math.Max(1e-3, Math.Max(bnd.Width, bnd.Height));
                    double gx = fx + 8 + 2 <= w ? fx + 2 : fx - 10;
                    dc.PushTransform(new TranslateTransform(gx, 1));
                    dc.PushTransform(new ScaleTransform(sc, sc));
                    dc.PushTransform(new TranslateTransform(-bnd.X, -bnd.Y));
                    dc.DrawGeometry(null, new Pen(B("Text2Brush"), 1.2 / sc), glyph);
                    dc.Pop(); dc.Pop(); dc.Pop();
                }
            }
            if (i == 0) continue;
            var a = pts[i - 1];
            if (a.Ago - p.Ago > 45) continue;   // gap: break the line
            string line = p.Kind == 0 ? "AccentBrush" : p.Kind == 2 ? "WarnBrush" : "Text2Brush";
            string area = p.Kind == 0 ? "AccentDimBrush" : p.Kind == 2 ? "WarnDimBrush" : "";
            var pa = new Point(X(a.Ago), Y(a.W)); var pb = new Point(X(p.Ago), Y(p.W));
            if (area.Length > 0)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(pa.X, H / 2), true, true);
                    c.LineTo(pa, false, false); c.LineTo(pb, false, false); c.LineTo(new Point(pb.X, H / 2), false, false);
                }
                g.Freeze();
                dc.DrawGeometry(B(area), null, g);
            }
            var pen = new Pen(B(line), p.Closed || a.Closed ? 1 : 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (p.Closed || a.Closed) pen.DashStyle = new DashStyle(new double[] { 1, 2 }, 0);
            dc.DrawLine(pen, pa, pb);
        }
        var last = pts[^1];
        dc.DrawEllipse(B(last.Kind == 0 ? "AccentBrush" : last.Kind == 2 ? "WarnBrush" : "Text2Brush"), null, new Point(X(last.Ago), Y(last.W)), 2.2, 2.2);
    }
}
