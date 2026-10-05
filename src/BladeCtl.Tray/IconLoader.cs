using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BladeCtl.Tray;

public static class IconLoader
{
    /// <summary>
    /// Load the app icon for the tray. Never returns a shared SystemIcons instance that a caller
    /// might dispose (the very first build disposed SystemIcons.Application, corrupting it process-wide).
    /// </summary>
    public static Icon Load()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }

    /// <summary>The same icon as a WPF ImageSource for the window.</summary>
    public static ImageSource? ToImageSource(Icon icon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
