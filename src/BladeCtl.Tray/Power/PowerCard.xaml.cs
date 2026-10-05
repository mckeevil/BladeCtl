using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace BladeCtl.Tray.Power;

/// <summary>The Power card (spec 11): first child of the Blade view. Renders PowerVm; all logic lives in the sampler.</summary>
public partial class PowerCard : UserControl
{
    public PowerCard()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PowerVm o) o.PropertyChanged -= OnVm;
            if (e.NewValue is PowerVm n) n.PropertyChanged += OnVm;
        };
    }

    private string? _announced;

    /// <summary>
    /// LiveSetting="Polite" alone does nothing in WPF: a screen reader hears a verdict change only when LiveRegionChanged is
    /// raised on the element's peer (spec 11.3 accessibility).
    /// </summary>
    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PowerVm.Strip) || sender is not PowerVm vm) return;
        string status = vm.Strip.Status;
        if (status == _announced) return;
        _announced = status;
        try
        {
            var peer = UIElementAutomationPeer.FromElement(StripStatus) ?? UIElementAutomationPeer.CreatePeerForElement(StripStatus);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch { }
    }
}

/// <summary>"plug" / "usbc" / "battery" / "laptop" / "cap" / "heat" / "check" / "sleep" / "unknown" -> the Glyph* geometry in Theme.xaml.</summary>
public sealed class GlyphConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        string key = (value as string) switch
        {
            "plug" => "GlyphPlug", "usbc" => "GlyphUsbC", "battery" => "GlyphBattery", "laptop" => "GlyphLaptop", "cap" => "GlyphCap",
            "heat" => "GlyphHeat", "check" => "GlyphCheck", "sleep" => "GlyphSleep", _ => "GlyphUnknown",
        };
        return Application.Current?.TryFindResource(key) as Geometry;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
