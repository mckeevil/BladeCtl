using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace BladeCtl.Tray;

/// <summary>
/// Autostart via a Scheduled Task rather than a Startup-folder shortcut.
///
/// Why: BladeCtl runs elevated (so it can read the CPU thermal zone, which Windows exposes only to
/// administrators). A Startup shortcut to an elevated app is blocked by UAC at logon and simply never
/// runs. A scheduled task with RunLevel=HighestAvailable starts it elevated at logon with no prompt.
///
/// Implemented with schtasks.exe + XML so there is no extra NuGet dependency.
/// </summary>
public static class AutoStart
{
    public const string TaskName = "BladeCtl";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static bool IsEnabled
    {
        get
        {
            var (code, _, _) = Run($"/Query /TN \"{TaskName}\"");
            return code == 0;
        }
    }

    /// <summary>Target exe the registered task actually points at, or null if not registered.</summary>
    public static string? RegisteredTarget()
    {
        var (code, stdout, _) = Run($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(stdout, @"<Command>(.*?)</Command>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (!enabled)
            {
                var (c, _, e) = Run($"/Delete /TN \"{TaskName}\" /F");
                if (c != 0 && !e.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
                {
                    error = e.Trim();
                    Log.Error("autostart delete failed: " + error);
                    return false;
                }
                Log.Write("autostart scheduled task removed");
                return true;
            }

            if (!IsElevated)
            {
                error = "creating the logon task needs administrator rights";
                return false;
            }

            var exe = Environment.ProcessPath ?? "";
            var xmlPath = Path.Combine(Path.GetTempPath(), "bladectl-task.xml");
            File.WriteAllText(xmlPath, BuildXml(exe), new UnicodeEncoding(false, true));

            var (code, _, err) = Run($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            try { File.Delete(xmlPath); } catch { }

            if (code != 0)
            {
                error = string.IsNullOrWhiteSpace(err) ? $"schtasks exited {code}" : err.Trim();
                Log.Error("autostart create failed: " + error);
                return false;
            }

            // Verify rather than assume.
            if (!IsEnabled)
            {
                error = "task was created but does not query back";
                Log.Error("autostart verification failed");
                return false;
            }
            Log.Ok($"autostart scheduled task created -> {exe} --tray (elevated, at logon)");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("autostart error: " + error);
            return false;
        }
    }

    // ---------- boot lighting: SYSTEM task at startup, before anyone signs in ----------

    public const string BootTaskName = "BladeCtl Boot Lighting";

    public static bool IsBootLightingEnabled => Run($"/Query /TN \"{BootTaskName}\"").Code == 0;

    public static string? BootLightingTarget()
    {
        var (code, stdout, _) = Run($"/Query /TN \"{BootTaskName}\" /XML");
        if (code != 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(stdout, @"<Command>(.*?)</Command>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    /// <summary>
    /// Register/remove the boot task. Runs as SYSTEM (S-1-5-18) on the BootTrigger, so the keyboard
    /// goes to the saved lighting while the sign-in screen is still up. Needs elevation (the logon
    /// task provides it). The user's settings and log paths are passed explicitly because SYSTEM's
    /// own profile is not the user's.
    /// </summary>
    public static bool SetBootLightingEnabled(bool enabled, string settingsPath, string logDir, out string? error)
    {
        error = null;
        try
        {
            if (!enabled)
            {
                var (c, _, e) = Run($"/Delete /TN \"{BootTaskName}\" /F");
                if (c != 0 && !e.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
                {
                    error = e.Trim(); Log.Error("boot lighting task delete failed: " + error); return false;
                }
                Log.Write("boot lighting task removed");
                return true;
            }
            if (!IsElevated) { error = "creating the boot task needs administrator rights"; return false; }

            var exe = Environment.ProcessPath ?? "";
            string esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
            string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>BladeCtl - apply saved keyboard lighting at boot, before sign-in</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT0S</Delay>
                </BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{esc(exe)}"</Command>
                  <Arguments>--apply-boot-lighting --settings "{esc(settingsPath)}" --logdir "{esc(logDir)}"</Arguments>
                  <WorkingDirectory>{esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
            var xmlPath = Path.Combine(Path.GetTempPath(), "bladectl-boot-task.xml");
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(false, true));
            var (code, _, err) = Run($"/Create /TN \"{BootTaskName}\" /XML \"{xmlPath}\" /F");
            try { File.Delete(xmlPath); } catch { }
            if (code != 0)
            {
                error = string.IsNullOrWhiteSpace(err) ? $"schtasks exited {code}" : err.Trim();
                Log.Error("boot lighting task create failed: " + error);
                return false;
            }
            if (!IsBootLightingEnabled) { error = "task was created but does not query back"; return false; }
            Log.Ok($"boot lighting task registered -> {exe} --apply-boot-lighting (SYSTEM, at boot)");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("boot lighting task error: " + error);
            return false;
        }
    }

    private static string BuildXml(string exe)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>BladeCtl - Razer Blade fan / power / lighting control</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
              <Delay>PT5S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{System.Security.SecurityElement.Escape(exe)}"</Command>
              <Arguments>--tray</Arguments>
              <WorkingDirectory>{System.Security.SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static (int Code, string Out, string Err) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "could not start schtasks.exe");
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, o, e);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }
}
