using System.Runtime.InteropServices;
using System.Windows;
using BladeCtl.Core;

namespace BladeCtl.Tray;

internal static class Program
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr h);
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;

    public static string Version { get; } = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    [STAThread]
    private static int Main(string[] args)
    {
        // Stable notification/taskbar identity, before any UI exists.
        try { SetCurrentProcessExplicitAppUserModelID("mckeevil.BladeCtl"); } catch { }

        bool has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
        string? after(string a)
        {
            int i = Array.FindIndex(args, x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        }

        if (has("--help") || has("-h") || has("/?")) return WithConsole(PrintHelp);
        if (has("--version")) return WithConsole(() => { Console.WriteLine($"BladeCtl {Version}"); return 0; });
        if (has("--verbose")) Log.Verbose = true;
        if (after("--logdir") is string ld) Log.OverrideDir(ld);
        if (after("--settings") is string sp) Settings.OverridePath(sp);

        if (has("--apply-boot-lighting")) return WithConsole(ApplyBootLighting);
        if (has("--guard-analog")) return int.TryParse(after("--guard-analog"), out var ppid) ? AnalogEngine.RunGuardian(ppid) : 2;
        if (has("--devices")) return WithConsole(PrintDevices);
        if (has("--accessory")) return WithConsole(() => AccessoryCli(args));
        if (has("--keyboard")) return WithConsole(() => KeyboardCli(args));
        if (has("--private-hash")) return WithConsole(() =>
        {
            if (after("--private-hash") is not string n || string.IsNullOrWhiteSpace(n)) { Console.Error.WriteLine("usage: BladeCtl.exe --private-hash <name>"); return 2; }
            Console.WriteLine(Core.Power.PrivateApps.Line(n));
            return 0;
        });

        // 2.7.0 Power card, read-only: no mutex, no tray, no window, no settings applied or saved.
        if (has("--power-probe")) return WithConsole(() => Power.PowerCli.Probe(after("--power-probe")));
        if (has("--power-preview"))
        {
            int i = Array.FindIndex(args, x => x.Equals("--power-preview", StringComparison.OrdinalIgnoreCase));
            var rest = args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).ToArray();
            return WithConsole(() => Power.PowerCli.Preview(rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1), rest.ElementAtOrDefault(2)));
        }

        if (has("--install-autostart") || has("--uninstall-autostart"))
        {
            bool on = has("--install-autostart");
            return WithConsole(() =>
            {
                if (!AutoStart.IsElevated) { Console.WriteLine("Needs to run elevated."); return 2; }
                bool ok = AutoStart.SetEnabled(on, out var err);
                Console.WriteLine(ok
                    ? (on ? $"Autostart installed: elevated logon task '{AutoStart.TaskName}' -> {Environment.ProcessPath} --tray" : "Autostart removed.")
                    : $"FAILED: {err}");
                return ok ? 0 : 2;
            });
        }

        // Read-only queries: work whether or not the GUI is running, never pop a dialog.
        if (has("--status")) return WithConsole(PrintStatus);
        if (has("--log")) return WithConsole(() => PrintLog(after("--log")));
        if (has("--selftest")) return WithConsole(SelfTest);

        // Commands that should reach an ALREADY-RUNNING instance rather than starting a second one.
        if (!SingleInstance.TryAcquire())
        {
            if (has("--restore-auto"))
            {
                bool sent = SingleInstance.Signal(SingleInstance.Sig.RestoreAuto);
                return WithConsole(() => { Console.WriteLine(sent ? "Asked the running BladeCtl to restore auto fan." : "Could not reach the running BladeCtl."); return sent ? 0 : 2; });
            }
            if (has("--exit")) return WithConsole(ExitRunning);
            if (has("--capture")) return WithConsole(() => CaptureRunning(after("--capture")));
            if (has("--cmd")) return WithConsole(() => QueueCommand(args));
            if (has("--analog")) return WithConsole(() => QueueCommand(new[] { "--cmd", "analog", after("--analog") ?? "off" }));

            // A second launch NEVER exits silently.
            if (!SingleInstance.Signal(SingleInstance.Sig.Show))
            {
                MessageBox.Show(
                    "BladeCtl is already running, but this copy could not reach it.\n\n" +
                    "Its icon is in the notification area — click the ^ arrow next to the clock.",
                    "BladeCtl", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return 0;
        }

        // Standalone CLI actions (no instance was running to forward to).
        if (has("--restore-auto")) return WithConsole(RestoreAutoCli);
        if (has("--exit") || has("--capture") || has("--cmd")) return WithConsole(() => { Console.WriteLine("BladeCtl is not running."); return 1; });
        if (has("--analog")) return WithConsole(() =>
        {
            // Emergency path: no instance running, so the only sensible request is "give me the firmware back".
            string m = after("--analog") ?? "off";
            if (m.Equals("off", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(AnalogEngine.ForceNormalMode()); return 0; }
            Console.WriteLine("BladeCtl is not running; start it and switch the analog engine on there (or: BladeCtl.exe --analog on while it runs).");
            return 1;
        });

        return RunGui(args, has("--tray"));
    }

    private static int RunGui(string[] args, bool startHidden)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/BladeCtl;component/Theme.xaml", UriKind.Relative),
        });
        app.DispatcherUnhandledException += (_, e) => { Crash("UI thread", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("background", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task exception: " + e.Exception.GetBaseException()); e.SetObserved(); };

        BladeCtlContext? ctx = null;
        try
        {
            // Listen first: the constructor can take several seconds at logon (Shell_NotifyIcon waits for
            // the taskbar), and --exit / --capture must be reachable from the moment the mutex exists.
            SingleInstance.Listen(sig => ctx?.OnSignal(sig));
            ctx = new BladeCtlContext(app, args, startHidden);
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Crash("startup", ex);
            return 1;
        }
        finally { SingleInstance.Release(); }
    }

    /// <summary>A crash is written to the log AND the user is told where to look.</summary>
    private static void Crash(string where, Exception? ex)
    {
        try
        {
            Log.Error($"UNHANDLED EXCEPTION ({where}): {ex}");
            MessageBox.Show(
                $"BladeCtl hit an unexpected error ({where}):\n\n{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"Details were written to:\n{Log.FilePath}",
                "BladeCtl — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    // ---------- CLI ----------

    private static int WithConsole(Func<int> body)
    {
        try
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            uint type = (h == IntPtr.Zero || h == new IntPtr(-1)) ? 0u : GetFileType(h);
            // FILE_TYPE_DISK (1) / FILE_TYPE_PIPE (3): the parent redirected stdout (`cmd > file`, a
            // pipeline). AttachConsole would re-point stdout at the console screen buffer and the
            // redirect would receive nothing — the v1 bug that made `--status > out.txt` come back empty.
            if (type != 1 && type != 3) AttachConsole(ATTACH_PARENT_PROCESS);
        }
        catch { }
        try { return body(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static int PrintHelp()
    {
        Console.WriteLine($"""
            BladeCtl {Version} — Razer Blade 15 Advanced (Mid 2021) control

              BladeCtl.exe                 open the control panel
              BladeCtl.exe --tray          start hidden in the notification area
              BladeCtl.exe --status        print device + app state and exit
              BladeCtl.exe --log [N]       print the last N log lines (default 60)
              BladeCtl.exe --selftest      round-trip power mode and brightness against the device, then restore
              BladeCtl.exe --restore-auto  restore firmware fan control and exit
              BladeCtl.exe --exit          ask the running BladeCtl to quit
              BladeCtl.exe --capture [png] ask the running BladeCtl to save a screenshot of its window
              BladeCtl.exe --cmd <line>    queue a command for the running BladeCtl: power balanced|gaming|creator|custom [cpu gpu],
                                           boost <cpu 0-3> <gpu 0-2>, logo off|on|blink, lighting <effect> [r g b], lights off|on,
                                           match-blade, fan-target <55-90> [Hottest|CPU|GPU], fan-target off, restore-auto,
                                           battery-profile fullpower, power-learn reset
              BladeCtl.exe --devices       every Razer product and whether it can be controlled
              BladeCtl.exe --accessory <pid> <effect> [r g b] [brightness] [--logon]
              BladeCtl.exe --keyboard <pid> gamemode on|off  |  polling 125|500|1000
              BladeCtl.exe --analog on|off   Huntsman analog engine; "off" also works with no instance running
                                             (emergency: puts the keyboard back in normal mode)
              BladeCtl.exe --power-probe <s>   read-only Power card sampling for <s> seconds (no instance needed, EC GETs only,
                                               NVML only while the NVIDIA GPU is already awake); writes power-probe-*.txt
              BladeCtl.exe --power-preview <png> [barrel|usbc|battery] [width]   render the Power card from sample data
              BladeCtl.exe --private-hash <name>   print the private-apps.txt line for an app name (see README)
              BladeCtl.exe --verbose       log every HID transaction
              BladeCtl.exe --version
              BladeCtl.exe --help

              BladeCtl.exe --install-autostart     register the elevated logon task
              BladeCtl.exe --uninstall-autostart   remove it

            If BladeCtl is already running, --restore-auto, --exit, --capture and a plain launch are
            forwarded to it instead of starting a second copy.

            Exit codes: 0 ok · 1 no device / not running · 2 command failed
            """);
        return 0;
    }

    private static int PrintStatus()
    {
        Console.WriteLine($"BladeCtl {Version}  exe={Environment.ProcessPath}");
        Console.WriteLine($"instance   : {(SingleInstance.IsRunning() ? "running" : "not running")}   this shell elevated: {AutoStart.IsElevated}");
        Console.WriteLine($"log        : {Log.FilePath}{(Log.DirNote is null ? "" : "  (FALLBACK: " + Log.DirNote + ")")}");
        Console.WriteLine($"autostart  : {(AutoStart.IsEnabled ? "logon task registered -> " + (AutoStart.RegisteredTarget() ?? "?") : "NOT registered")}");
        Console.WriteLine($"synapse    : {SynapseDetector.Detect().Summary}");
        var rk = RazerGuard.PresentValues();
        Console.WriteLine($"razer run  : {(rk.Length == 0 ? "no Synapse autostart value in HKCU Run" : string.Join(", ", rk))}");

        using var dev = BladeDevice.Find(Log.CoreLog);
        if (dev == null) { Console.WriteLine("device     : NO Razer Blade control device answered (VID 1532 / PID 0276)."); return 1; }
        using var ctl = new BladeController(dev, Log.CoreLog);
        var p1 = ctl.GetPowerState(1);
        var p2 = ctl.GetPowerState(2);
        Console.WriteLine($"device     : {dev.DevicePath}");
        Console.WriteLine($"firmware   : {ctl.GetFirmwareVersion() ?? "?"}");
        Console.WriteLine($"power mode : z1={p1?.Mode.ToString() ?? "?"} z2={p2?.Mode.ToString() ?? "?"}");
        Console.WriteLine($"fan        : {(p1?.ManualFan == true ? "MANUAL" : "auto")}  z1={ctl.GetFanRpm(1)?.ToString() ?? "?"}  z2={ctl.GetFanRpm(2)?.ToString() ?? "?"} rpm");
        Console.WriteLine($"brightness : {ctl.GetBrightness()?.ToString() ?? "?"}");
        var bho = ctl.GetBho();
        Console.WriteLine($"batt limit : {(bho.HasValue ? $"enabled={bho.Value.Enabled} threshold={bho.Value.ThresholdPercent}% (no-op on this model)" : "not supported / no answer")}");
        Console.WriteLine("--- last log lines ---");
        foreach (var l in Log.Tail(5)) Console.WriteLine(l);
        return 0;
    }

    private static int PrintDevices()
    {
        var list = BladeCtl.Core.RazerEnumerator.Enumerate(Log.CoreLog);
        Console.WriteLine($"{list.Count} Razer products");
        foreach (var d in list)
        {
            Console.WriteLine($"  {d.Pid:X4}  {d.Name}  [{d.Kind}]");
            Console.WriteLine($"        {(d.SpeaksProtocol ? $"control ok · tid 0x{d.Tid:X2} · report id 0x{d.ReportId:X2} · fw {d.Firmware ?? "?"} · serial {d.Serial ?? "?"} · mode {d.DeviceMode?.ToString() ?? "?"} · brightness {d.Brightness?.ToString() ?? "?"}" : "no control protocol · " + d.Detail)}");
            if (d.Model != null) Console.WriteLine($"        {d.Model.Notes}");
        }
        return 0;
    }

    /// <summary>--accessory &lt;pid&gt; &lt;Off|Static|Spectrum|Breathing|Wave&gt; [r g b] [brightness] [--logon]</summary>
    private static int AccessoryCli(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--accessory", StringComparison.OrdinalIgnoreCase));
        var rest = args.Skip(i + 1).Where(a => !a.StartsWith("--")).ToArray();
        if (rest.Length < 2 || !ushort.TryParse(rest[0], System.Globalization.NumberStyles.HexNumber, null, out var pid))
        {
            Console.WriteLine("usage: --accessory <pid hex> <Off|Static|Spectrum|Breathing|Wave|Reactive|Starlight> [r g b] [brightness] [--logon]");
            return 2;
        }
        var settings = Settings.Load();
        var saved = settings.AccessoryFor(pid);
        var l = new AccessoryLighting { Effect = rest[1], R = saved.R, G = saved.G, B = saved.B, Brightness = saved.Brightness, ApplyAtLogon = saved.ApplyAtLogon || args.Contains("--logon"),
                                        Direction = saved.Direction, Speed = saved.Speed, GameMode = saved.GameMode, PollingHz = saved.PollingHz };
        if (rest.Length >= 5) { l.R = int.Parse(rest[2]); l.G = int.Parse(rest[3]); l.B = int.Parse(rest[4]); }
        if (rest.Length >= 6) l.Brightness = int.Parse(rest[5]);
        else if (rest.Length == 3) l.Brightness = int.Parse(rest[2]);

        var svc = new AccessoryService(settings);
        svc.Refresh("cli");
        foreach (var d in svc.Devices) Console.WriteLine("  " + AccessoryService.Describe(d));
        var o = svc.ApplyAsync(pid, l).GetAwaiter().GetResult();
        Console.WriteLine($"[{o.Kind.ToUpperInvariant()}] {o.Message}");
        return o.Kind == "bad" ? 2 : 0;
    }

    /// <summary>--keyboard &lt;pid&gt; gamemode on|off  |  --keyboard &lt;pid&gt; polling 125|500|1000</summary>
    private static int KeyboardCli(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--keyboard", StringComparison.OrdinalIgnoreCase));
        var rest = args.Skip(i + 1).Where(a => !a.StartsWith("--")).ToArray();
        if (rest.Length < 3 || !ushort.TryParse(rest[0], System.Globalization.NumberStyles.HexNumber, null, out var pid))
        {
            Console.WriteLine("usage: --keyboard <pid hex> gamemode on|off   |   --keyboard <pid hex> polling 125|500|1000");
            return 2;
        }
        var settings = Settings.Load();
        var svc = new AccessoryService(settings);
        svc.Refresh("cli");
        AccessoryService.ApplyOutcome o = rest[1].ToLowerInvariant() switch
        {
            "gamemode" => svc.SetGameModeAsync(pid, rest[2].Equals("on", StringComparison.OrdinalIgnoreCase)).GetAwaiter().GetResult(),
            "polling" => svc.SetPollingAsync(pid, int.TryParse(rest[2], out var hz) ? hz : 1000).GetAwaiter().GetResult(),
            _ => new AccessoryService.ApplyOutcome(false, "unknown keyboard setting: " + rest[1], "bad"),
        };
        Console.WriteLine($"[{o.Kind.ToUpperInvariant()}] {o.Message}");
        return o.Kind == "bad" ? 2 : 0;
    }

    /// <summary>--cmd &lt;line&gt;: queue one command-file line for the running instance (consumed within 5 s).</summary>
    private static int QueueCommand(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--cmd", StringComparison.OrdinalIgnoreCase));
        string line = string.Join(" ", args.Skip(i + 1));
        if (string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine("usage: --cmd power balanced|gaming|creator|custom [cpu gpu] | boost <cpu> <gpu> | logo off|on|blink | lighting <effect> [r g b] | lights off|on | match-blade | fan-target <55-90> [Hottest|CPU|GPU] | fan-target off | restore-auto");
            return 2;
        }
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "command-request.txt"), line + Environment.NewLine);
            Console.WriteLine($"Queued for the running BladeCtl: {line}  (applied within 5 s; see --log)");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Could not queue: " + ex.Message); return 2; }
    }

    /// <summary>
    /// Runs as SYSTEM from the boot task: apply the user's saved Blade lighting and any accessory
    /// lighting flagged for logon, then exit. No window, no tray, no mutex.
    /// </summary>
    private static int ApplyBootLighting()
    {
        var settings = Settings.Load();
        Log.Write($"BOOT LIGHTING: start (user={Environment.UserName} settings={Settings.FilePath} saved rgb={settings.Rgb} #{settings.ColorR:X2}{settings.ColorG:X2}{settings.ColorB:X2} bright={settings.Brightness} normalMode={settings.BladeNormalMode})");
        int rc = 0;

        BladeDevice? dev = null;
        for (int i = 0; i < 20 && dev == null; i++) { dev = BladeDevice.Find(Log.CoreLog); if (dev == null) Thread.Sleep(1000); }
        if (dev == null) { Log.Warn("BOOT LIGHTING: Blade control device did not appear within 20 s"); rc = 1; }
        else
        {
            using (dev)
            using (var ctl = new BladeController(dev, Log.CoreLog))
            {
                if (settings.BladeNormalMode)
                {
                    bool mok = ctl.SetDeviceMode(0x00);
                    Log.Write($"BOOT LIGHTING: device mode 0 -> {(mok ? "accepted" : "rejected")}");
                }
                bool b = ctl.SetBrightness((byte)Math.Clamp(settings.Brightness, 0, 255));
                byte cr = (byte)settings.ColorR, cg = (byte)settings.ColorG, cb = (byte)settings.ColorB;
                byte sp = (byte)Math.Clamp(settings.RgbSpeed, 1, 4), dr = (byte)Math.Clamp(settings.RgbDirection, 1, 2);
                bool e = settings.Rgb switch
                {
                    "Spectrum" => ctl.SetSpectrum(),
                    "Breathing" => ctl.SetBreathing(cr, cg, cb),
                    "Wave" => ctl.SetWave(dr),
                    "Reactive" => ctl.SetReactive(sp, cr, cg, cb),
                    "Starlight" => ctl.SetStarlight((byte)Math.Min(sp, (byte)3), cr, cg, cb),
                    "Off" => ctl.SetLightsOff(),
                    _ => ctl.SetStaticColor(cr, cg, cb),
                };
                bool lg = ctl.SetLogo(settings.Logo switch { "On" => 1, "Blink" => 2, _ => 0 });
                Thread.Sleep(200);
                var rb = ctl.GetBrightness();
                var lo = ctl.GetLogoLed();
                Log.Write($"BOOT LIGHTING: Blade {settings.Rgb} {(e ? "accepted" : "REJECTED")}, brightness {settings.Brightness} {(b ? "accepted" : "REJECTED")} read back {rb?.ToString() ?? "?"}, logo {settings.Logo} {(lg ? "accepted" : "REJECTED")} read back {(lo == null ? "?" : lo == true ? "on" : "off")}");
                if (!e || !b) rc = 2;
            }
        }

        try
        {
            var svc = new AccessoryService(settings);
            svc.Refresh("boot");
            int n = svc.ApplySavedAtLogon("BOOT LIGHTING");
            Log.Write($"BOOT LIGHTING: {n} accessories re-applied");
        }
        catch (Exception ex) { Log.Error("BOOT LIGHTING: accessories failed: " + ex.Message); }

        Log.Write($"BOOT LIGHTING: done rc={rc}");
        return rc;
    }

    private static int PrintLog(string? n)
    {
        int lines = int.TryParse(n, out var v) ? v : 60;
        Console.WriteLine($"# {Log.FilePath}");
        foreach (var l in Log.Tail(lines)) Console.WriteLine(l);
        return 0;
    }

    /// <summary>
    /// Proves the write path against real hardware: change power mode and brightness, read them
    /// back, restore. Lighting is deliberately not exercised (write-only, and it would repaint the
    /// keyboard). A manual fan floor, if engaged, is left alone.
    /// </summary>
    private static int SelfTest()
    {
        int pass = 0, fail = 0, skip = 0;
        void Report(string step, bool? ok, string detail)
        {
            string tag = ok switch { true => "PASS", false => "FAIL", null => "SKIP" };
            if (ok == true) pass++; else if (ok == false) fail++; else skip++;
            Console.WriteLine($"[{tag}] {step}: {detail}");
            if (ok == true) Log.Ok($"SELFTEST {step}: {detail}");
            else if (ok == false) Log.Error($"SELFTEST {step}: {detail}");
            else Log.Write($"SELFTEST {step} skipped: {detail}");
        }

        Log.Write("SELFTEST started" + (SingleInstance.IsRunning() ? " (a BladeCtl instance is running and polling concurrently)" : ""));
        using var dev = BladeDevice.Find(Log.CoreLog);
        if (dev == null) { Report("find device", false, "no HID collection answered"); return 1; }
        using var ctl = new BladeController(dev, Log.CoreLog);

        var fw = ctl.GetFirmwareVersion();
        var p1 = ctl.GetPowerState(1);
        var p2 = ctl.GetPowerState(2);
        var r1 = ctl.GetFanRpm(1);
        var r2 = ctl.GetFanRpm(2);
        var br = ctl.GetBrightness();
        bool readOk = fw != null && p1.HasValue && p2.HasValue && r1.HasValue && r2.HasValue && br.HasValue;
        Report("read state", readOk, $"fw={fw ?? "?"} z1={p1?.Mode}/{(p1?.ManualFan == true ? "manual" : "auto")} z2={p2?.Mode}/{(p2?.ManualFan == true ? "manual" : "auto")} rpm={r1?.ToString() ?? "?"}/{r2?.ToString() ?? "?"} bright={br?.ToString() ?? "?"}");
        if (!readOk) return 2;

        // Power mode round trip.
        if (p1!.Value.ManualFan)
        {
            Report("power mode round-trip", null, "a manual fan floor is engaged; toggling mode would clear it");
        }
        else
        {
            var original = p1.Value.Mode is PerfMode.Gaming ? PerfMode.Gaming : PerfMode.Balanced;
            var target = original == PerfMode.Gaming ? PerfMode.Balanced : PerfMode.Gaming;
            ctl.SetModeVerbose(target); Thread.Sleep(300);
            var mid = ctl.GetPowerState(1)?.Mode;
            ctl.SetModeVerbose(original); Thread.Sleep(300);
            var back = ctl.GetPowerState(1)?.Mode;
            Report("power mode round-trip", mid == target && back == original,
                $"{original} -> {target} (read {mid?.ToString() ?? "?"}) -> {original} (read {back?.ToString() ?? "?"})");
        }

        // Brightness round trip.
        {
            int b0 = br!.Value;
            byte target = (byte)(b0 >= 128 ? b0 - 60 : b0 + 60);
            ctl.SetBrightness(target); Thread.Sleep(300);
            var mid = ctl.GetBrightness();
            ctl.SetBrightness((byte)b0); Thread.Sleep(300);
            var back = ctl.GetBrightness();
            Report("brightness round-trip", mid.HasValue && Math.Abs(mid.Value - target) <= 2 && back.HasValue && Math.Abs(back.Value - b0) <= 2,
                $"{b0} -> {target} (read {mid?.ToString() ?? "?"}) -> {b0} (read {back?.ToString() ?? "?"})");
        }

        Report("lighting", null, "effects are write-only on this firmware; not exercised");
        var bho = ctl.GetBho();
        Report("battery limit read", bho.HasValue, bho.HasValue ? $"enabled={bho.Value.Enabled} threshold={bho.Value.ThresholdPercent}% (known no-op on PID 0276)" : "no answer");

        Console.WriteLine($"{pass} passed, {fail} failed, {skip} skipped");
        Log.Write($"SELFTEST finished: {pass} passed, {fail} failed, {skip} skipped");
        return fail == 0 ? 0 : 2;
    }

    private static int RestoreAutoCli()
    {
        using var dev = BladeDevice.Find(Log.CoreLog);
        if (dev == null) { Console.WriteLine("No device found."); return 1; }
        using var ctl = new BladeController(dev, Log.CoreLog);

        var res = ctl.RestoreAutoFanVerbose(PerfMode.Balanced);
        Thread.Sleep(250);
        var ps = ctl.GetPowerState(1);
        bool ok = ps?.ManualFan == false;
        Console.WriteLine(ok
            ? "Firmware fan control restored (confirmed by device)."
            : $"Restore did NOT confirm — device reports manual={ps?.ManualFan.ToString() ?? "?"}. {res.Describe()}");
        Log.Write($"--restore-auto (standalone) -> {(ok ? "confirmed" : "NOT confirmed")}");
        return ok ? 0 : 2;
    }

    private static int ExitRunning()
    {
        if (!SingleInstance.Signal(SingleInstance.Sig.Exit)) { Console.WriteLine("Could not reach the running BladeCtl."); return 2; }
        // TryAcquire() above left THIS process holding a handle on the mutex, which keeps the kernel object
        // alive after the real instance disposes its own. Drop it before polling or IsRunning() lies.
        SingleInstance.Release();
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(200);
            if (!SingleInstance.IsRunning()) { Console.WriteLine("BladeCtl exited."); return 0; }
        }
        Console.WriteLine("BladeCtl was signalled but is still running after 10 s.");
        return 2;
    }

    private static int CaptureRunning(string? path)
    {
        path ??= Path.Combine(Log.AppDataDir, "capture.png");
        path = Path.GetFullPath(path);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        string view = Environment.GetCommandLineArgs().SkipWhile(a => !a.Equals("--view", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "";
        File.WriteAllText(SingleInstance.CaptureRequestFile, path + Environment.NewLine + view);
        if (!SingleInstance.Signal(SingleInstance.Sig.Capture)) { Console.WriteLine("Could not reach the running BladeCtl."); return 2; }
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(250);
            if (File.Exists(path)) { Console.WriteLine(path); return 0; }
        }
        Console.WriteLine("No capture appeared within 15 s.");
        return 2;
    }
}
