using System.Diagnostics;

namespace BladeCtl.Tray;

/// <summary>
/// Razer's kernel driver for the Huntsman V2 Analog (RzDev_0266) exposes a permanent virtual Xbox 360
/// controller as a child of the keyboard (USB\VID_045E&amp;PID_028E&amp;REV_0110 under VID_1532&amp;PID_0266&amp;MI_01).
/// Without Synapse it never moves, but games still see it and it holds XInput slot 1, so BladeCtl's own
/// controller would land in slot 2. Disabling that device node is reversible (Enable) and needs elevation,
/// which the logon task provides. It goes away for good with the Razer uninstall.
/// </summary>
public static class PhantomPad
{
    private const string Filter = "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\\VID_045E&PID_028E&REV_0110*' } | Where-Object { (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data -like '*VID_1532*' }";

    /// <summary>Returns the phantom pad's instance ids and status, empty when none is present.</summary>
    public static string Query() => Run(Filter + " | ForEach-Object { $_.InstanceId + ' ' + $_.Status }");

    public static string Disable() => Run(Filter + " | ForEach-Object { $id = $_.InstanceId; Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop; 'disabled ' + $id }");

    public static string Enable() => Run("Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\\VID_045E&PID_028E&REV_0110*' -and $_.Status -ne 'OK' } | ForEach-Object { $id = $_.InstanceId; Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop; 'enabled ' + $id }");

    private static string Run(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return "powershell did not start";
            string outp = p.StandardOutput.ReadToEnd().Trim();
            string err = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit(20000);
            if (err.Length > 0) return (outp.Length > 0 ? outp + " · " : "") + "error: " + err.Split('\n')[0].Trim();
            return outp.Length == 0 ? "no Razer phantom controller present" : outp.Replace("\r\n", "; ");
        }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }
}
