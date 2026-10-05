using System.Diagnostics;
using Microsoft.Win32;

namespace BladeCtl.Tray;

/// <summary>
/// Keeps Razer Synapse from starting with Windows.
///
/// How Synapse actually relaunches (verified 2026-09-05 from Explorer's own startup log,
/// Microsoft-Windows-Shell-Core/Operational events 9705-9708): Explorer executes
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RazerAppEngine
/// about 50 s after logon. The value is NOT present while Synapse is running; Synapse re-plants
/// it before shutdown. Deleting it once (2026-07-21) therefore did nothing durable.
///
/// BladeCtl starts 5 s after logon (elevated logon task), i.e. BEFORE Explorer enumerates the
/// Run key, so deleting the value here is enough to stop the launch. The key is also watched
/// for the lifetime of the app so a re-plant is removed and logged.
///
/// Closing an already-running Synapse is a separate, opt-in action (see <see cref="CloseSynapse"/>):
/// killing its processes was observed to drop an external Razer keyboard for a moment.
/// </summary>
public static class RazerGuard
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Run-key value names Razer has used for the Synapse engine.</summary>
    private static readonly string[] ValueNames = { "RazerAppEngine", "Razer Synapse", "RazerSynapse", "Razer Central" };

    private static readonly string[] SynapseProcesses = { "RzEngineMon", "RazerAppEngine", "razerwdl", "RazerCentralService" };

    private static Timer? _watch;

    /// <summary>Names of Razer-looking values currently in the HKCU Run key.</summary>
    public static string[] PresentValues()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (k == null) return Array.Empty<string>();
            return k.GetValueNames()
                .Where(n => ValueNames.Contains(n, StringComparer.OrdinalIgnoreCase)
                            || (k.GetValue(n) as string ?? "").Contains("Razer", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Remove every Razer autostart value. Returns what was removed (value = command line, for the log).</summary>
    public static List<(string Name, string Command)> RemoveAutostart()
    {
        var removed = new List<(string, string)>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return removed;
            foreach (var name in PresentValues())
            {
                var cmd = k.GetValue(name) as string ?? "";
                k.DeleteValue(name, throwOnMissingValue: false);
                removed.Add((name, cmd));
                Log.Ok($"Synapse autostart removed: HKCU\\...\\Run\\{name} = {cmd}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"could not edit the Run key: {ex.GetType().Name}: {ex.Message}");
        }
        return removed;
    }

    /// <summary>Poll the Run key (cheap: one registry read) and strip a re-planted value.</summary>
    public static void StartWatching(TimeSpan interval)
    {
        _watch?.Dispose();
        _watch = new Timer(_ =>
        {
            try
            {
                if (PresentValues().Length > 0)
                {
                    Log.Warn("Synapse re-planted its Run entry while BladeCtl was running - removing it again");
                    RemoveAutostart();
                }
            }
            catch (Exception ex) { Log.Error("run-key watch: " + ex.Message); }
        }, null, interval, interval);
    }

    public static void StopWatching()
    {
        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>
    /// Close a running Synapse stack. Engine monitor first so it cannot respawn the engine.
    /// Returns the number of processes stopped.
    /// </summary>
    public static int CloseSynapse(string why)
    {
        int n = 0;
        foreach (var name in SynapseProcesses)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    n++;
                }
                catch (Exception ex) { Log.Warn($"could not stop {name} ({p.Id}): {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
        if (n > 0) Log.Ok($"closed Razer Synapse ({n} processes) - {why}");
        else Log.Write($"Synapse close requested ({why}) but nothing was running");
        return n;
    }
}
