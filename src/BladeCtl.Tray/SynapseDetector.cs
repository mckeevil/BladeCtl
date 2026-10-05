using System.Diagnostics;

namespace BladeCtl.Tray;

/// <summary>
/// Detects a running Razer Synapse stack. BladeCtl works with Synapse running (verified on hardware),
/// but both poll the same HID feature-report interface, so commands can be missed and Synapse can
/// silently re-assert its own profile over BladeCtl's settings. We surface that rather than hide it.
///
/// Deliberately does NOT try to detect whether Windows 11 Dynamic Lighting is actively driving the
/// keyboard: Windows exposes no readable state for it (verified — no Lighting registry key exists even
/// with the RazerDynamicLighting Appx installed). Guessing would mean the UI could lie, so instead the
/// Lighting section always shows an actionable hint and a button that opens the Windows setting.
/// </summary>
public static class SynapseDetector
{
    /// <summary>The Synapse UI/engine itself — this is what actively fights BladeCtl for the device.</summary>
    private static readonly string[] AppProcesses = { "RazerAppEngine", "RazerSynapse", "Razer Synapse 3", "RazerCentralService" };

    /// <summary>Background Razer bits. Present even when the Synapse window is closed; low impact.</summary>
    private static readonly string[] ServiceProcesses = { "razer_elevation_service", "RzActionSvc", "razerwdl" };

    public sealed record Status(bool SynapseRunning, int SynapseProcessCount, bool BackgroundServices, string[] Names)
    {
        public string Summary =>
            SynapseRunning ? $"Razer Synapse is running ({SynapseProcessCount} processes)"
            : BackgroundServices ? "Synapse closed; Razer background services still running"
            : "Razer Synapse not running";
    }

    public static Status Detect()
    {
        try
        {
            var procs = Process.GetProcesses();
            try
            {
                var names = procs.Select(p => p.ProcessName).ToArray();

                int appCount = names.Count(n => AppProcesses.Any(a => n.Equals(a, StringComparison.OrdinalIgnoreCase)));
                bool svc = names.Any(n => ServiceProcesses.Any(s => n.Equals(s, StringComparison.OrdinalIgnoreCase)));

                var razerNames = names
                    .Where(n => n.StartsWith("Razer", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Rz", StringComparison.OrdinalIgnoreCase))
                    .Distinct().OrderBy(n => n).ToArray();

                return new Status(appCount > 0, appCount, svc, razerNames);
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return new Status(false, 0, false, Array.Empty<string>());
        }
    }

    /// <summary>Opens Settings → Personalization → Dynamic Lighting.</summary>
    public static void OpenDynamicLightingSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true });
        }
        catch { }
    }
}
