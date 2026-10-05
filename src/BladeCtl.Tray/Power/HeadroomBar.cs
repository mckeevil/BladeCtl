using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace BladeCtl.Tray.Power;

/// <summary>
/// One headroom bar (spec 11.3): fill = watts now (row hue; estimates at 55% with a dashed outline), a 2x14 tick at the
/// current limit (dashed when it is learned rather than live), a hollow triangle above the track at what the same load
/// gets on the 230 W charger, and, when the row is held back by power and that is 5 W or more above the limit, the "watts
/// you're missing" zone in hatched amber. Drawn directly like <see cref="Gauge"/>; an equal model causes no redraw.
/// </summary>
public sealed class HeadroomBar : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(BarModel), typeof(HeadroomBar),
        new FrameworkPropertyMetadata(BarModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((HeadroomBar)d).OnModel()));

    public BarModel Model { get => (BarModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    private const double TrackY = 11, TrackH = 6;

    private Brush B(string key) => (Brush)FindResource(key);

    private void OnModel()
    {
        var m = Model;
        ToolTip = string.IsNullOrEmpty(m?.Tip) ? null : m!.Tip;
        AutomationProperties.SetName(this, m?.Tip ?? "");
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 160 : available.Width, 22);

    protected override void OnRender(DrawingContext dc)
    {
        var m = Model ?? BarModel.Empty;
        double w = Math.Max(10, ActualWidth);
        double scale = m.Scale > 0 ? m.Scale : 100;
        double X(double v) => Math.Clamp(v / scale, 0, 1) * w;
        var track = new Rect(0, TrackY, w, TrackH);
        dc.DrawRectangle(Background(), null, new Rect(0, 0, w, 22));   // hit-test surface for the tooltip

        if (m.Unknown && !m.Asleep)
            dc.DrawRoundedRectangle(null, new Pen(B("LineStrongBrush"), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }, track, 3, 3);
        else
            dc.DrawRoundedRectangle(B("LineStrongBrush"), null, track, 3, 3);

        string hueKey = m.Hue == "gpu" ? "GpuHueBrush" : "AccentBrush";
        if (!m.Asleep && m.Now > 0)
        {
            var fill = new Rect(0, TrackY, Math.Max(2, X(m.Now)), TrackH);
            if (m.NowEstimated)
            {
                dc.PushOpacity(0.55);
                dc.DrawRoundedRectangle(B(hueKey), null, fill, 3, 3);
                dc.Pop();
                dc.DrawRoundedRectangle(null, new Pen(B(hueKey), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }, fill, 3, 3);
            }
            else dc.DrawRoundedRectangle(B(hueKey), null, fill, 3, 3);
        }

        // the watts you're missing: limit .. reference
        if (m.MissingZone)
        {
            var zone = new Rect(X(m.Limit), TrackY - 1, Math.Max(1, X(m.Ref) - X(m.Limit)), TrackH + 2);
            dc.DrawRectangle(B("WarnDimBrush"), null, zone);
            dc.PushClip(new RectangleGeometry(zone));
            dc.PushOpacity(0.3);
            var hatch = new Pen(B("WarnBrush"), 1);
            for (double x = zone.Left - zone.Height; x < zone.Right; x += 4) dc.DrawLine(hatch, new Point(x, zone.Bottom), new Point(x + zone.Height, zone.Top));
            dc.Pop(); dc.Pop();
        }

        // current limit tick (an asleep GPU has none: empty track, reference marker only)
        if (m.Limit > 0 && !m.Asleep)
        {
            double x = X(m.Limit);
            var pen = new Pen(B("TextBrush"), 2);
            if (m.LimitLearned) pen.DashStyle = new DashStyle(new double[] { 1.5, 1.5 }, 0);
            dc.DrawLine(pen, new Point(x, TrackY - 4), new Point(x, TrackY + TrackH + 4));
        }

        // barrel reference: hollow triangle above the track with a dotted drop line
        if (m.Ref > 0)
        {
            double x = X(m.Ref);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x - 4, 0.5), true, true);
                c.LineTo(new Point(x + 4, 0.5), true, false);
                c.LineTo(new Point(x, 6), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(B("Text2Brush"), 1.2), g);
            dc.DrawLine(new Pen(B("Text2Brush"), 1) { DashStyle = new DashStyle(new double[] { 1, 2 }, 0) }, new Point(x, 6), new Point(x, TrackY + TrackH));
        }
    }

    private static readonly Brush Clear = Freeze(new SolidColorBrush(Colors.Transparent));
    private static Brush Background() => Clear;
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
