using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BladeCtl.Tray;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public MainWindow(MainViewModel vm, ImageSource? icon)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        if (icon != null) Icon = icon;

        SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int on = 1; DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                int round = DWMWCP_ROUND; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }
        };
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _vm.OverlayVisible) { _vm.CloseDialog(0); e.Handled = true; } };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Floor_DragStarted(object sender, DragStartedEventArgs e) => _vm.FloorDragging = true;
    private void Floor_DragCompleted(object sender, DragCompletedEventArgs e) => _vm.FloorDragging = false;

    private void Bright_DragStarted(object sender, DragStartedEventArgs e) => _vm.BrightnessDragging = true;
    private async void Bright_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.BrightnessDragging = false;
        await _vm.CommitBrightnessAsync();
    }
    private async void Bright_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // A click on the track (not a thumb drag) moves the value without a DragCompleted.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        if (!_vm.BrightnessDragging) await _vm.CommitBrightnessAsync();
    }
    private async void Bright_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            await _vm.CommitBrightnessAsync();
    }

    // Accessory cards: the slider lives in a DataTemplate, so the card is found through DataContext.
    private static DeviceCardVm? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as DeviceCardVm;
    private void Dev_DragStarted(object sender, DragStartedEventArgs e) { if (CardOf(sender) is { } c) c.BrightnessDragging = true; }
    private async void Dev_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (CardOf(sender) is not { } c) return;
        c.BrightnessDragging = false;
        await c.Send();
    }
    private async void Dev_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (CardOf(sender) is not { } c) return;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        if (!c.BrightnessDragging) await c.Send();
    }

    /// <summary>Render the window content to a PNG (used by `--capture` for remote verification).</summary>
    public void CaptureToPng(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
