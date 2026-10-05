using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace BladeCtl.Tray.Power;

/// <summary>
/// The flow diagram (spec 11.3): charger -> laptop -> battery. Edge thickness is watts, static chevrons show direction
/// (never colour alone), labels give W and %/h, the battery node fills to its charge and carries the ETA, and a small
/// stacked bar under the laptop splits the use into CPU / GPU / rest. No animation; an equal model causes no redraw.
/// </summary>
public sealed class PowerFlow : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(FlowModel), typeof(PowerFlow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((PowerFlow)d).OnModel()));
    public static readonly DependencyProperty SourceGlyphProperty = DependencyProperty.Register(
        nameof(SourceGlyph), typeof(Geometry), typeof(PowerFlow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FlowModel? Model { get => (FlowModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public Geometry? SourceGlyph { get => (Geometry?)GetValue(SourceGlyphProperty); set => SetValue(SourceGlyphProperty, value); }

    private const double H = 92, NodeY = 32, Node = 40;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");

    private Brush B(string key) => (Brush)FindResource(key);
    private Geometry G(string key) => (Geometry)FindResource(key);

    private void OnModel()
    {
        var m = Model;
        AutomationProperties.SetName(this, m?.Summary ?? "Power flow");
        ToolTip = m?.Summary;
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 372 : available.Width, H);

    private static double Thick(double w) => Math.Clamp(1.5 + Math.Abs(w) / 9.0, 1.5, 14);

    private FormattedText Text(string s, double size, Brush b, FontFamily f, FontWeight? wt = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(f, FontStyles.Normal, wt ?? FontWeights.Normal, FontStretches.Normal), size, b,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(240, ActualWidth);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, H));
        var m = Model;
        if (m == null)
        {
            var t = Text("reading…", 11, B("Text3Brush"), Body);
            dc.DrawText(t, new Point(w / 2 - t.Width / 2, NodeY - t.Height / 2));
            return;
        }
        double srcX = 20, lapX = w / 2, batX = w - 24;
        var nodeRectSrc = new Rect(srcX - Node / 2, NodeY - Node / 2, Node, Node);
        var nodeRectLap = new Rect(lapX - Node / 2, NodeY - Node / 2, Node, Node);
        var nodeRectBat = new Rect(batX - Node / 2, NodeY - Node / 2, Node, Node);

        // ---- edge 1: source -> laptop ----
        double e1a = nodeRectSrc.Right + 2, e1b = nodeRectLap.Left - 2;
        if (m.OnAC)
        {
            double t = Thick(m.SupplyW);
            Brush eb = B(m.SourceWarn ? "WarnBrush" : "AccentBrush");
            Edge(dc, e1a, e1b, t, eb, toRight: true, estimated: m.SupplyEstimated);
            Label(dc, (e1a + e1b) / 2, t, m.SupplyLabel, m.SupplySub);
        }
        else
        {
            dc.DrawLine(new Pen(B("Text3Brush"), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) }, new Point(e1a, NodeY), new Point(e1b, NodeY));
            var off = Text("unplugged", 10, B("Text3Brush"), Body);
            dc.DrawText(off, new Point((e1a + e1b) / 2 - off.Width / 2, NodeY + 5));
        }

        // ---- edge 2: laptop <-> battery ----
        double e2a = nodeRectLap.Right + 2, e2b = nodeRectBat.Left - 2;
        if (Math.Abs(m.BatW) < 0.5)
        {
            dc.DrawLine(new Pen(B("Text3Brush"), 1), new Point(e2a, NodeY), new Point(e2b, NodeY));
            Label(dc, (e2a + e2b) / 2, 1, m.BatLabel, "");
        }
        else
        {
            double t = Thick(m.BatW);
            Brush eb = m.Charging ? B("AccentBrush") : m.BatWarn ? B("WarnBrush") : B("Text2Brush");
            Edge(dc, e2a, e2b, t, eb, toRight: m.Charging, estimated: false);
            Label(dc, (e2a + e2b) / 2, t, m.BatLabel, m.BatSub);
        }

        // ---- nodes ----
        var nodeFill = B("Panel2Brush");
        var nodePen = new Pen(B("LineStrongBrush"), 1);
        if (m.OnAC) dc.DrawRoundedRectangle(nodeFill, nodePen, nodeRectSrc, 10, 10);
        else dc.DrawRoundedRectangle(null, new Pen(B("Text3Brush"), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) }, nodeRectSrc, 10, 10);
        Glyph(dc, SourceGlyph ?? G(m.SourceGlyph == "usbc" ? "GlyphUsbC" : m.SourceGlyph == "battery" ? "GlyphBattery" : m.SourceGlyph == "unknown" ? "GlyphUnknown" : "GlyphPlug"),
              nodeRectSrc, B(m.OnAC ? (m.SourceWarn ? "WarnBrush" : "TextBrush") : "Text3Brush"));
        dc.DrawRoundedRectangle(nodeFill, nodePen, nodeRectLap, 10, 10);
        Glyph(dc, G("GlyphLaptop"), nodeRectLap, B("TextBrush"));
        BatteryNode(dc, nodeRectBat, m);

        // ---- stacked use bar under the laptop ----
        double bw = Math.Min(120, w / 3), bx = lapX - bw / 2, by = NodeY + Node / 2 + 8;
        double total = Math.Max(0, m.CpuW) + Math.Max(0, m.GpuW) + Math.Max(0, m.RestW);
        dc.DrawRoundedRectangle(B("LineStrongBrush"), null, new Rect(bx, by, bw, 6), 3, 3);
        if (total > 0.5)
        {
            double x = bx;
            void Seg(double v, string brush, bool hatched)
            {
                if (v <= 0) return;
                double sw = bw * v / total;
                var r = new Rect(x, by, Math.Max(1, sw), 6);
                dc.DrawRectangle(B(brush), null, r);
                if (hatched)
                {
                    dc.PushClip(new RectangleGeometry(r));
                    var hp = new Pen(B("Text3Brush"), 1);
                    for (double hx = r.Left - 6; hx < r.Right; hx += 3) dc.DrawLine(hp, new Point(hx, r.Bottom), new Point(hx + 6, r.Top));
                    dc.Pop();
                }
                x += sw;
            }
            Seg(m.CpuW, "AccentBrush", false);
            Seg(m.GpuW, "GpuHueBrush", false);
            Seg(m.RestW, "RestHueBrush", true);
        }
        var st = Text(m.StackText, 10, B("Text2Brush"), Mono);
        dc.DrawText(st, new Point(lapX - st.Width / 2, by + 9));
    }

    private void Edge(DrawingContext dc, double a, double b, double t, Brush brush, bool toRight, bool estimated)
    {
        if (b - a < 4) return;
        var pen = new Pen(brush, t) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        if (estimated) dc.PushOpacity(0.75);
        dc.DrawLine(pen, new Point(a, NodeY), new Point(b, NodeY));
        if (estimated) dc.Pop();
        // static chevrons every 16 px: cut out of a thick edge, drawn in the edge colour on a thin one
        bool cut = t >= 6;
        double ch = cut ? Math.Max(2, t / 2 - 1) : 4;
        var cp = new Pen(cut ? B("BgBrush") : brush, cut ? 1.6 : 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        for (double x = a + 10; x < b - 6; x += 16)
        {
            double d = toRight ? 1 : -1;
            dc.DrawLine(cp, new Point(x - d * ch * 0.7, NodeY - ch), new Point(x + d * ch * 0.7, NodeY));
            dc.DrawLine(cp, new Point(x + d * ch * 0.7, NodeY), new Point(x - d * ch * 0.7, NodeY + ch));
        }
    }

    private void Label(DrawingContext dc, double cx, double t, string top, string sub)
    {
        if (top.Length > 0)
        {
            var ft = Text(top, 12, B("TextBrush"), Mono);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, NodeY - t / 2 - ft.Height - 1));
        }
        if (sub.Length > 0)
        {
            var fs = Text(sub, 10, B("Text3Brush"), Body);
            dc.DrawText(fs, new Point(cx - fs.Width / 2, NodeY + t / 2 + 1));
        }
    }

    private void Glyph(DrawingContext dc, Geometry g, Rect node, Brush stroke)
    {
        // glyph geometries are drawn on a 24-unit grid
        dc.PushTransform(new TranslateTransform(node.X + (node.Width - 24) / 2, node.Y + (node.Height - 24) / 2));
        dc.DrawGeometry(null, new Pen(stroke, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
        dc.Pop();
    }

    private void BatteryNode(DrawingContext dc, Rect node, FlowModel m)
    {
        var body = new Rect(node.X + 2, node.Y + 8, node.Width - 9, node.Height - 16);
        var tip = new Rect(body.Right, body.Y + body.Height / 2 - 4, 4, 8);
        bool low = !m.OnAC && m.Soc is >= 0 and <= 15;
        string fillKey = low ? "WarnBrush" : m.Charging ? "AccentBrush" : !m.OnAC ? "Text2Brush" : "AccentDimBrush";
        if (m.Soc >= 0)
        {
            double fw = (body.Width - 4) * Math.Clamp(m.Soc / 100.0, 0, 1);
            dc.DrawRoundedRectangle(B(fillKey), null, new Rect(body.X + 2, body.Y + 2, Math.Max(0, fw), body.Height - 4), 2, 2);
        }
        var pen = new Pen(B(m.BatWarn || low ? "WarnBrush" : "TextBrush"), 1.4);
        dc.DrawRoundedRectangle(null, pen, body, 4, 4);
        dc.DrawRoundedRectangle(B(m.BatWarn || low ? "WarnBrush" : "TextBrush"), null, tip, 1.5, 1.5);
        if (m.BatWarn)
        {
            var badge = new Rect(node.Right - 12, node.Y - 4, 14, 14);
            dc.DrawEllipse(B("WarnBrush"), null, new Point(badge.X + 7, badge.Y + 7), 7, 7);
            var ex = Text("!", 11, B("BgBrush"), Body, FontWeights.Bold);
            dc.DrawText(ex, new Point(badge.X + 7 - ex.Width / 2, badge.Y + 7 - ex.Height / 2));
        }
        string pct = m.Soc >= 0 ? m.Soc + "%" : "?";
        var ft = Text(pct, 12, B("TextBrush"), Mono);
        double cx = node.X + node.Width / 2;
        double y = node.Bottom + 2;
        dc.DrawText(ft, new Point(Math.Min(cx - ft.Width / 2, node.Right + 2 - ft.Width), y));
        if (m.Eta.Length > 0)
        {
            var et = Text(m.Eta, 10, B("Text3Brush"), Body);
            dc.DrawText(et, new Point(Math.Min(cx - et.Width / 2, node.Right + 2 - et.Width), y + ft.Height));
        }
    }
}
