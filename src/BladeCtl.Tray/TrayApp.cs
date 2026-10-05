using System.Drawing;
using System.Windows.Forms;
using BladeCtl.Core;

namespace BladeCtl.Tray;

/// <summary>
/// Tray adapter only. Renders the LAST SNAPSHOT — it performs no device I/O and never blocks,
/// so opening the menu can't stall on HID or nvidia-smi. All logic lives in BladeCtlContext.
/// (WinForms NotifyIcon: WPF has no tray icon of its own.)
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly CommandRunner _runner;
    private readonly Action _onShowWindow;
    private readonly Action _onExit;
    private readonly Font _boldFont;

    private DeviceSnapshot _snap;

    public TrayApp(DeviceMonitor monitor, CommandRunner runner, Icon icon, Action onShowWindow, Action onExit)
    {
        _runner = runner;
        _onShowWindow = onShowWindow;
        _onExit = onExit;
        _snap = monitor.Snapshot;

        _tray = new NotifyIcon
        {
            Icon = icon,
            Text = "BladeCtl — starting…",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _boldFont = new Font(_tray.ContextMenuStrip.Font, FontStyle.Bold);

        _tray.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _onShowWindow(); };
        _tray.DoubleClick += (_, _) => _onShowWindow();

        RebuildMenu();
    }

    public void UpdateFromSnapshot(DeviceSnapshot s)
    {
        _snap = s;
        string text;
        if (!s.Connected) text = "BladeCtl — DEVICE NOT FOUND";
        else
        {
            var mode = s.Mode?.ToString() ?? "?";
            var fan = s.ManualFan == true ? $"MANUAL {s.Rpm1?.ToString() ?? "?"} RPM" : "fan auto";
            var t = s.MaxTempC.HasValue ? $" · {s.MaxTempC:F0}°C" : "";
            var syn = s.Synapse.SynapseRunning ? " · Synapse" : "";
            text = $"BladeCtl — {mode} · {fan}{t}{syn}";
        }
        if (text.Length > 63) text = text[..63];
        try { _tray.Text = text; } catch { }
    }

    public void Notify(string text, bool ok)
    {
        try
        {
            _tray.BalloonTipTitle = ok ? "BladeCtl" : "BladeCtl — attention";
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = ok ? ToolTipIcon.Info : ToolTipIcon.Warning;
            _tray.ShowBalloonTip(ok ? 3000 : 6000);
        }
        catch { }
    }

    private void RebuildMenu()
    {
        var m = _tray.ContextMenuStrip!;
        m.Items.Clear();
        var s = _snap;

        m.Items.Add(new ToolStripMenuItem(s.Connected
            ? $"Connected · firmware {s.Firmware ?? "?"}"
            : "DEVICE NOT FOUND") { Enabled = false });

        if (s.Connected)
        {
            var fan = s.ManualFan == true ? $"MANUAL {s.Rpm1?.ToString() ?? "?"} RPM" : "auto";
            var temp = s.MaxTempC.HasValue ? $" · {s.MaxTempC:F0} °C" : "";
            m.Items.Add(new ToolStripMenuItem($"{s.Mode?.ToString() ?? "?"} · fan {fan}{temp}") { Enabled = false });
        }

        if (s.Synapse.SynapseRunning)
            m.Items.Add(new ToolStripMenuItem($"Synapse running ({s.Synapse.SynapseProcessCount}) — may override") { Enabled = false });

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("&Open BladeCtl", null, (_, _) => _onShowWindow()) { Font = _boldFont });
        m.Items.Add(new ToolStripMenuItem("&RESTORE AUTO FAN  (Ctrl+Alt+F)", null,
            async (_, _) => { var r = await _runner.RestoreAutoAsync(); Notify(r.Headline, r.Ok); })
        { Font = _boldFont, Enabled = s.Connected });
        m.Items.Add(new ToolStripSeparator());

        var perf = new ToolStripMenuItem("&Performance") { Enabled = s.Connected };
        foreach (var mode in new[] { PerfMode.Balanced, PerfMode.Gaming })
        {
            var pm = mode;
            perf.DropDownItems.Add(new ToolStripMenuItem(pm.ToString(), null,
                async (_, _) => { var r = await _runner.SetPowerModeAsync(pm); Notify(r.Headline, r.Ok); })
            { Checked = s.Mode == pm });
        }
        m.Items.Add(perf);

        var fanMenu = new ToolStripMenuItem("&Fan") { Enabled = s.Connected };
        fanMenu.DropDownItems.Add(new ToolStripMenuItem("Automatic (firmware)", null,
            async (_, _) => { var r = await _runner.RestoreAutoAsync(); Notify(r.Headline, r.Ok); })
        { Checked = s.ManualFan == false });
        fanMenu.DropDownItems.Add(new ToolStripSeparator());
        fanMenu.DropDownItems.Add(new ToolStripMenuItem("Set a manual floor…", null, (_, _) => _onShowWindow()));
        m.Items.Add(fanMenu);

        m.Items.Add(new ToolStripMenuItem("&Lighting…", null, (_, _) => _onShowWindow()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Open &log", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true }); }
            catch { }
        }));
        m.Items.Add(new ToolStripMenuItem("E&xit", null, (_, _) => _onExit()));
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _boldFont.Dispose();
    }
}
