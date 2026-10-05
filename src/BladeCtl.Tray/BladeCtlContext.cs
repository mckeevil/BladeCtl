using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using BladeCtl.Core;
using WinForms = System.Windows.Forms;

namespace BladeCtl.Tray;

/// <summary>
/// Owns the whole app: tray icon, monitor, command runner, main window, hotkey, Synapse guard,
/// exit flow. The WPF Application is the message pump; nothing here is a Form.
/// </summary>
public sealed class BladeCtlContext
{
    private readonly Application _app;
    private readonly Settings _settings;
    private readonly DeviceMonitor _monitor;
    private readonly CommandRunner _runner;
    private readonly TrayApp _tray;
    private readonly HiddenOwner _owner;
    private readonly System.Drawing.Icon _icon;
    private readonly MainViewModel _vm;
    private readonly AccessoryService _accessories;
    private readonly FanTargetController _fanTarget;
    private readonly LockWatcher _lock;
    private readonly AnalogEngine _analog;
    public AnalogEngine Analog => _analog;
    private readonly Power.PowerSampler _power;
    private bool _sessionLocked;

    private MainWindow? _window;
    private bool _exiting;

    public Settings Settings => _settings;
    public DeviceMonitor Monitor => _monitor;

    public BladeCtlContext(Application app, string[] args, bool startHidden)
    {
        _app = app;
        _settings = Settings.Load();
        Log.Verbose = _settings.VerboseLog;

        bool elevated = AutoStart.IsElevated;
        var syn = SynapseDetector.Detect();
        var runKey = RazerGuard.PresentValues();
        Log.Session(Program.Version, args, elevated, new[]
        {
            "settings: " + _settings.Summary + (_settings.WasMissingOnLoad ? " (NEW FILE - first run)" : ""),
            "autostart: " + (AutoStart.IsEnabled ? "logon task -> " + (AutoStart.RegisteredTarget() ?? "?") : "NOT registered"),
            "synapse: " + syn.Summary + (syn.Names.Length > 0 ? " [" + string.Join(", ", syn.Names) + "]" : ""),
            "razer run-key: " + (runKey.Length > 0 ? string.Join(", ", runKey) : "none"),
            "last startup apply: " + (_settings.LastStartupApplyUtc ?? "never"),
            "legacy v1 instance still running: " + (SingleInstance.LegacyV1Running() ? "YES (gone after next sign-out)" : "no"),
        });

        // Synapse guard runs BEFORE Explorer enumerates the Run key (~50 s after logon; we start at +5 s).
        if (_settings.BlockSynapseAutostart)
        {
            RazerGuard.RemoveAutostart();
            RazerGuard.StartWatching(TimeSpan.FromSeconds(30));
        }
        if (_settings.CloseSynapseAtStartup && syn.SynapseRunning)
            RazerGuard.CloseSynapse("close-at-startup is on");

        _icon = IconLoader.Load();
        _monitor = new DeviceMonitor(_settings);
        _power = new Power.PowerSampler(_settings) { FullPowerFlag = () => _fullPowerUntilAc, ControllerSource = () => _monitor.Controller };
        _monitor.Power = _power;
        _runner = new CommandRunner(_monitor, _settings);
        _accessories = new AccessoryService(_settings);
        _analog = new AnalogEngine();
        _analog.Configure(_settings.Analog);
        _accessories.Changed += () => SyncAnalog("device change");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _analog.Stop("process exit");
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _analog.Stop("unhandled exception");
        _fanTarget = new FanTargetController(_settings, _monitor, _runner, (m, ok) => _tray?.Notify(m, ok));
        _runner.ActiveLoopRpm = () => _fanTarget.CommandedRpm;
        _fanTarget.Paused = !_settings.KeepManualFanOnBattery && WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Offline;
        _vm = new MainViewModel(_settings, _monitor, _runner, this, _accessories, _fanTarget, _power);
        _vm.ViewChanged += UpdatePowerLive;
        _lock = new LockWatcher(_settings, LightsOffAsync, LightsRestoreAsync);

        _owner = new HiddenOwner(() => _ = RestoreAutoFromHotkeyAsync());
        _owner.RegisterRestoreHotkey();
        _owner.DeviceChanged += () => { _accessories.DeviceChangeNoticed(); _power.OnDeviceChanged(); };
        _ = _accessories.RefreshAsync("startup");

        _tray = new TrayApp(_monitor, _runner, _icon,
            onShowWindow: () => Dispatch(ShowWindow),
            onExit: () => Dispatch(() => _ = ExitAppAsync(prompt: true, reason: "tray Exit")));

        _monitor.Updated += s => _tray.UpdateFromSnapshot(s);
        _monitor.ThermalAutoRevert += OnThermalAutoRevert;
        _monitor.MaxFanCeiling += OnMaxFanCeiling;
        _monitor.CommandedFloorRpm = () => _fanTarget.CommandedRpm ?? (_settings.ManualFanRequested ? _settings.ManualRpm : (int?)null);
        _runner.Completed += OnCommandCompleted;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _panelActive = PanelRefresh.FindInternal() != null;

        bool firstRun = !_settings.FirstRunComplete;

        // Never touch hardware on a genuine first run before the user has seen the app.
        if (firstRun)
        {
            Log.Write("first run - not applying any hardware settings; seeding UI from device");
            ShowWindow();
        }
        else
        {
            if (_settings.ApplyOnStartup) ScheduleStartupApply();
            else Defer(2000, () => _ = SyncBatteryProfileAsync("startup", panel: true));
            if (!startHidden || _settings.ShowWindowAtStartup) ShowWindow();
        }

        StartDumpWatch();

        Defer(firstRun ? 500 : 1800, () =>
        {
            if (firstRun)
            {
                _ = ShowFirstRunNoticeAsync();
                _settings.FirstRunComplete = true;
                _settings.Save();
            }
            else AnnounceLaunch();
            HealAutostart();
            EnsureBootTask();
        });
    }

    /// <summary>The boot-lighting SYSTEM task is created/repointed from the elevated logon instance, never with a UAC prompt.</summary>
    private void EnsureBootTask()
    {
        if (!_settings.BootLightingTask) return;
        if (!AutoStart.IsElevated) { Log.Write("boot lighting task wanted but this instance is not elevated; it will be registered by the logon task's instance"); return; }
        var self = Environment.ProcessPath ?? "";
        var target = AutoStart.BootLightingTarget();
        if (target != null && string.Equals(target, self, StringComparison.OrdinalIgnoreCase)) return;
        Log.Write(target == null ? "registering the boot lighting task" : $"boot lighting task points at '{target}', repointing to '{self}'");
        if (!AutoStart.SetBootLightingEnabled(true, Settings.FilePath, Log.Dir, out var err))
            Log.Warn("boot lighting task could not be registered: " + err);
    }

    // ---------- helpers ----------

    private void Dispatch(Action a) => _app.Dispatcher.BeginInvoke(a);

    private void Defer(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { a(); } catch (Exception ex) { Log.Error("deferred step failed: " + ex.Message); }
        };
        t.Start();
    }

    public void Notify(string text, bool ok) => _tray.Notify(text, ok);

    /// <summary>
    /// Autostart must survive the exe being moved. Moving the install folder broke it
    /// once (a Startup shortcut kept pointing at the dead path). Self-heal instead of just warning.
    /// </summary>
    private void HealAutostart()
    {
        if (StartupShortcut.IsEnabled)
        {
            var legacy = StartupShortcut.StaleTarget();
            StartupShortcut.SetEnabled(false, out _);
            Log.Write($"removed legacy Startup-folder shortcut (target was {legacy ?? "current exe"}); the logon task supersedes it");
        }

        if (!AutoStart.IsEnabled) { Log.Warn("autostart task is NOT registered - BladeCtl will not start at logon"); return; }

        var self = Environment.ProcessPath;
        var registered = AutoStart.RegisteredTarget();
        if (self == null || registered == null) return;
        if (string.Equals(registered, self, StringComparison.OrdinalIgnoreCase)) return;

        Log.Warn($"autostart task points at '{registered}' but running exe is '{self}'");
        if (!AutoStart.IsElevated) { Log.Warn("not elevated, so the task cannot be repointed from here"); return; }
        if (AutoStart.SetEnabled(true, out var err))
            _tray.Notify("BladeCtl moved, so its startup entry was repointed to the new location.", true);
        else
            _tray.Notify($"BladeCtl has moved and its startup entry is stale ({registered}). Re-tick 'Start with Windows'. ({err})", false);
    }

    // ---------- startup ----------

    private void ScheduleStartupApply()
    {
        var syn = SynapseDetector.Detect();
        int delayMs = syn.SynapseRunning ? 25_000 : 2_000;
        Log.Write($"startup apply scheduled in {delayMs} ms (synapse running: {syn.SynapseRunning})");
        Defer(delayMs, () => _ = StartupApplyAsync());
    }

    private async Task StartupApplyAsync()
    {
        await ApplySavedAsync("startup");
        await SyncBatteryProfileAsync("startup", panel: true);
    }

    public async Task ApplySavedAsync(string why)
    {
        Log.Write($"applying saved settings ({why}): {_settings.Summary}");

        // The monitor connects on a background tick; give it a moment rather than failing instantly.
        for (int i = 0; i < 40 && _monitor.Controller == null; i++) await Task.Delay(250);
        if (_monitor.Controller == null)
        {
            Log.Warn($"{why} apply aborted: no device after 10 s ({_monitor.Snapshot.NotFoundReason})");
            _tray.Notify("BladeCtl could not find the Blade control device, so saved settings were not applied.", false);
            return;
        }

        var results = new List<CommandResult>();
        if (_settings.ManualFanRequested)
        {
            results.Add(await _runner.SetManualFanAsync(_settings.ManualRpm));
            _tray.Notify($"Manual fan floor {_settings.ManualRpm} RPM re-applied at startup. RESTORE AUTO FAN or Ctrl+Alt+F reverts it.", true);
        }
        else if (_settings.TargetFanRequested)
        {
            results.Add(await _runner.SetPowerModeAsync(_settings.PerfModeValue));
            _fanTarget.Start(_settings.TargetTempC, _settings.TargetSensor);
            _tray.Notify($"Fan target re-armed: holding {_fanTarget.SensorName} at {_settings.TargetTempC} °C. RESTORE AUTO FAN disarms it.", true);
        }
        else
        {
            results.Add(await _runner.SetPowerModeAsync(_settings.PerfModeValue));
        }
        // The saved mode is now on the device, so the battery overlay no longer is; the caller (or the next
        // power event) re-engages it when unplugged.
        if (_settings.BatteryProfileEngaged) { _settings.BatteryProfileEngaged = false; _settings.Save(); }
        results.Add(await _runner.SetBrightnessAsync(_settings.Brightness));
        results.Add(await _runner.SetLightingAsync(_settings.Rgb, (byte)_settings.ColorR, (byte)_settings.ColorG, (byte)_settings.ColorB));
        results.Add(await _runner.SetLogoAsync(_settings.Logo));

        int ok = results.Count(r => r.Ok);
        try
        {
            if (_accessories.Devices.Count == 0) _accessories.Refresh(why);
            int n = _accessories.ApplySavedAtLogon(why);
            if (n > 0) Log.Write($"{why} apply: {n} accessories re-sent");
        }
        catch (Exception ex) { Log.Error($"{why} apply: accessories failed: {ex.Message}"); }

        if (ok == results.Count)
        {
            Log.Ok($"{why} apply complete: {ok}/{results.Count} settings applied");
            _settings.LastStartupApplyUtc = DateTime.UtcNow.ToString("o");
            _settings.Save();
        }
        else
        {
            Log.Warn($"{why} apply: {results.Count - ok} of {results.Count} settings did not confirm");
            _tray.Notify($"Startup apply: {results.Count - ok} of {results.Count} settings did not confirm. Open BladeCtl for detail.", false);
        }
        // A manual re-apply on battery puts the cool mode back right away (startup does this itself, with the panel).
        if (why != "startup") await SyncBatteryProfileAsync(why);
    }

    // ---------- Huntsman analog engine ----------

    public string AnalogStatusLine => _analog.Running
        ? $"{_analog.Status} · {_analog.ConfigSummary} · {_analog.Packets} packets, {_analog.KeyEvents} key events" +
          (_analog.PadStatus.Length > 0 ? " · " + _analog.PadStatus : "") + (_analog.DialStatus.Length > 0 ? " · " + _analog.DialStatus : "") + (_settings.Analog.Adaptive ? " · " + _analog.LearnedSummary : "") +
          (DateTime.Now - _analog.LastUnknownAt < TimeSpan.FromMinutes(30) ? $" · UNMAPPED KEY id {_analog.LastUnknownId} pressed at {_analog.LastUnknownAt:HH:mm:ss}: add \"{_analog.LastUnknownId} = MediaNext\" (or MediaPrev, MediaPlay, Mute) to the overrides box" : "")
        : _settings.Analog.Enabled ? _analog.Status : "off — firmware actuation, fixed ~1.6 mm on every key";

    private System.Threading.Timer? _analogSave;
    private readonly object _analogGate = new();
    private DateTime _lastHealCheck = DateTime.MinValue;

    /// <summary>Any slider/switch change: thresholds apply on the next packet, the file is saved a moment later.</summary>
    public void OnAnalogChanged()
    {
        _analog.Configure(_settings.Analog);
        _analogSave?.Dispose();
        _analogSave = new System.Threading.Timer(_ =>
        {
            _analog.ExportLearned(_settings.Analog);
            _settings.Save();
            Log.Write($"analog settings: {(_settings.Analog.Enabled ? "on" : "off")} · {_analog.ConfigSummary}");
        }, null, 700, Timeout.Infinite);
        _ = Task.Run(() => SyncAnalog("settings"));
    }

    private void SyncAnalog(string why)
    {
        lock (_analogGate)
        {
            try
            {
                var kb = _accessories.Devices.FirstOrDefault(d => d.Pid == 0x0266 && d.SpeaksProtocol);
                if (_settings.Analog.Enabled)
                {
                    if (kb == null) { _analog.Stop("keyboard gone"); return; }
                    if (!_analog.Running && !_analog.Start(kb, out var err)) _tray?.Notify("Analog engine could not start: " + err, false);
                }
                else
                {
                    // Engine wanted OFF. Stop UNCONDITIONALLY: after a failed start Running is false while the
                    // keyboard may still be in driver mode, and that is precisely when the user reaches for the
                    // switch. Stop() is a no-op when there is nothing to reclaim.
                    _analog.Stop(why);
                    _analog.ExportLearned(_settings.Analog);
                    _settings.Save();

                    // 2.4.1 gated this heal on `why != "settings"`, but the UI switch and `--cmd analog off`
                    // both pass "settings" - so the documented recovery could never run in the one state that
                    // needed it. Neither the cached DeviceMode nor "did WE apply it" is a safe gate either:
                    // the keyboard can be stranded by a previous instance or a killed process, and the cached
                    // snapshot still says 0. So ask the DEVICE, every time the engine is meant to be off -
                    // rate-limited only so that dragging a slider cannot spam the HID interface.
                    if (kb != null && (_analog.DriverModeOutstanding || DateTime.UtcNow - _lastHealCheck > TimeSpan.FromSeconds(5)))
                    {
                        _lastHealCheck = DateTime.UtcNow;
                        HealDriverMode(kb, why);
                    }
                }
            }
            catch (Exception ex) { Log.Error("analog sync: " + ex.Message); }
        }
    }

    /// <summary>
    /// A keyboard in driver mode with no engine reading it cannot type at all, so this asks the DEVICE for its
    /// mode rather than trusting the cached enumeration, and restores normal mode unless it is confirmed fine.
    /// </summary>
    private static void HealDriverMode(RazerDeviceInfo kb, string why)
    {
        byte? mode;
        using (var ctl = RazerEnumerator.Open(kb, Log.CoreLog)) mode = ctl?.GetDeviceMode();
        if (mode == 0) return;
        Log.Warn($"Huntsman reports mode {mode?.ToString() ?? "unreadable"} with the analog engine off ({why}) - restoring normal mode");
        Log.Write("analog: " + AnalogEngine.ForceNormalMode());
    }

    // ---------- lights off on lock / idle ----------

    /// <summary>Blade brightness 0 plus every accessory dimmed. Effects and saved settings are untouched.</summary>
    public async Task LightsOffAsync(string why)
    {
        Log.Write($"lights off ({why})");
        _monitor.LightsOff = true;
        await Task.Run(() =>
        {
            var ctl = _monitor.Controller;
            if (ctl != null)
            {
                bool ok = ctl.SetBrightness(0);
                Log.Write($"{why}: Blade brightness 0 {(ok ? "accepted" : "REJECTED")}");
            }
            int n = _accessories.LightsOff(why);
            Log.Write($"{why}: {n} accessories dimmed");
        });
    }

    public async Task LightsRestoreAsync(string why)
    {
        Log.Write($"lights back ({why})");
        await Task.Run(() =>
        {
            var ctl = _monitor.Controller;
            if (ctl != null)
            {
                int b = Math.Clamp(_settings.Brightness, 0, 255);
                bool ok = ctl.SetBrightness((byte)b);
                Log.Write($"{why}: Blade brightness {b} {(ok ? "restored" : "REJECTED")}");
            }
            int n = _accessories.LightsRestore(why);
            Log.Write($"{why}: {n} accessories restored");
        });
        _monitor.LightsOff = false;
        _monitor.PollNow();
    }

    private void AnnounceLaunch()
    {
        var s = _monitor.Snapshot;
        if (s.Connected)
            _tray.Notify($"BladeCtl is running — Blade 15 Advanced connected (fw {s.Firmware ?? "?"}). Left-click the tray icon for controls.", true);
        else
            _tray.Notify("BladeCtl started, but no Razer Blade control device answered yet. Open BladeCtl for diagnostics.", false);
    }

    private async Task ShowFirstRunNoticeAsync()
    {
        var s = _monitor.Snapshot;
        string device = s.Connected
            ? $"Found your Razer Blade 15 Advanced (firmware {s.Firmware ?? "?"})."
            : "No Razer Blade control device answered yet — the window shows why and lets you retry.";
        await _vm.ShowDialogAsync(new DialogSpec("Welcome to BladeCtl",
            device + " Nothing on your laptop has been changed: BladeCtl does not touch fans or lighting on first run — you make the first change.",
            new[]
            {
                (DialogBullet.Info, "Its tray icon may be hidden behind the ^ arrow next to the clock. Pin it under Settings > Personalization > Taskbar."),
                (DialogBullet.Info, "Closing this window keeps BladeCtl running in the tray. Use Exit in the tray menu to quit."),
            },
            new[] { ("Got it", DialogButton.Primary) }));
    }

    // ---------- window ----------

    public void ShowWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow(_vm, IconLoader.ToImageSource(_icon));
            _window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                _window!.Hide();
                if (!_settings.MinimiseNoticeShown)
                {
                    _settings.MinimiseNoticeShown = true;
                    _settings.Save();
                    _tray.Notify("BladeCtl is still running in the notification area. Use Exit in its menu to quit.", true);
                }
            };
            _window.IsVisibleChanged += (_, _) =>
            {
                _monitor.WindowVisible = _window!.IsVisible;
                Log.Write(_window.IsVisible ? "window shown" : "window hidden");
                UpdatePowerLive();
                if (_window.IsVisible) _monitor.PollNow();
            };
            _window.StateChanged += (_, _) => UpdatePowerLive();
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _monitor.WindowVisible = true;
        UpdatePowerLive();
        _monitor.PollNow();
    }

    /// <summary>
    /// Power card Live (spec 5.1): window visible, not minimized, Blade view, session unlocked. Minimized counts as closed
    /// for the Power card even though the monitor keeps its 2 s cadence (existing behaviour, unchanged).
    /// </summary>
    private void UpdatePowerLive()
    {
        bool live = _window != null && _window.IsVisible && _window.WindowState != WindowState.Minimized && _vm.IsBladeView && !_sessionLocked;
        if (live != _power.Live) Log.Write($"power card {(live ? "live" : "closed")}");
        bool becameLive = live && !_power.Live;
        _power.Live = live;
        if (becameLive) _monitor.PollNow();   // first Live reading now, not at the next 2-30 s tick
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { _sessionLocked = true; _power.Pause(true); }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) { _sessionLocked = false; _power.Pause(false); }
        else return;
        Dispatch(UpdatePowerLive);
    }

    // ---------- events ----------

    private void OnCommandCompleted(CommandResult r)
    {
        if (r.Ok) return;
        _tray.Notify(r.Headline + (r.Detail is null ? "" : "\n" + r.Detail), false);
    }

    private void OnThermalAutoRevert(double temp)
    {
        _fanTarget.NoteExternalRevert();
        Dispatch(() =>
        {
            _tray.Notify($"THERMAL AUTO-REVERT at {temp:F0} °C — firmware fan control restored.", false);
            ShowWindow();
            _ = _vm.ShowDialogAsync(new DialogSpec("Thermal auto-revert",
                $"BladeCtl saw {temp:F0} °C while software fan control was engaged and has restored firmware fan control. " +
                "Your fan setting is back to Automatic and any temperature target is disarmed. The firmware curve alone could not keep this workload cooler either; consider a lower power mode.",
                Array.Empty<(DialogBullet, string)>(),
                new[] { ("OK", DialogButton.Primary) }, DialogKind.Bad));
        });
    }

    /// <summary>
    /// The limit was reached with the fans already flat out, so nothing was reverted (spinning them down would
    /// only make it hotter). Tell the user once, and point at the lever that is actually left: power.
    /// </summary>
    private void OnMaxFanCeiling(double temp)
    {
        Dispatch(() =>
        {
            _tray.Notify($"{temp:F0} °C with the fans already at maximum — holding max fans. Lower the power ceiling to go cooler.", false);
            _vm.SetStatus($"{temp:F0} °C at full fan speed. Fans are maxed; the remaining lever is the power ceiling — try Custom power mode with CPU boost Low.", "warn");
        });
    }

    private static bool OnBattery => WinForms.SystemInformation.PowerStatus.PowerLineStatus == WinForms.PowerLineStatus.Offline;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Write("system resumed - re-probing device");
            _power.OnResume();
            _monitor.PollNow();
            // Plugged or unplugged while asleep: StatusChange may not fire for that, so re-check on wake.
            // (SystemEvents raises this on its own thread, so no DispatcherTimer here.)
            _ = Task.Delay(3000).ContinueWith(_ => SyncBatteryProfileAsync("resume"));
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            Log.Write("system suspending");
            _power.OnSuspend();
        }
        else if (e.Mode == PowerModes.StatusChange)
        {
            // Also fires on every battery-percentage change; SyncBatteryProfileAsync only acts on a real AC/DC flip.
            _power.OnPowerStatusChange();
            _ = SyncBatteryProfileAsync("power source change");
        }
        if (e.Mode == PowerModes.StatusChange && !_settings.KeepManualFanOnBattery)
        {
            bool onBattery = OnBattery;
            if (_fanTarget.Paused != onBattery)
            {
                _fanTarget.Paused = onBattery;
                if (_settings.TargetFanRequested)
                {
                    Log.Write(onBattery ? "switched to battery - fan target loop paused" : "back on AC - fan target loop resumes");
                    _monitor.PollNow();
                }
            }
            if (onBattery && _settings.ManualFanRequested)
            {
                Log.Write("switched to battery - reverting manual fan floor to auto");
                _ = _runner.RestoreAutoAsync();
                _tray.Notify("Switched to battery — manual fan floor reverted to firmware control.", true);
            }
        }
    }

    // ---------- battery profile (2.6.0) ----------

    private readonly SemaphoreSlim _batteryGate = new(1, 1);
    private bool? _forcedBattery;          // command-file test override: true = behave as if unplugged
    private bool? _lastBattery;            // last power source seen, to act on real AC/DC flips only
    private bool _batteryFailNotified;
    private bool _panelActive;
    private DateTime _panelChangedAt = DateTime.MinValue;
    /// <summary>
    /// 2.7.0 "Full power until I plug in": session-only. Suspends the Razer part of the battery profile until the next AC;
    /// the panel refresh and the NVIDIA display service keep following the real battery state. Never persisted.
    /// </summary>
    private bool _fullPowerUntilAc;

    /// <summary>The Power card's button and `battery-profile fullpower`: the saved Razer mode until the next time AC is seen.</summary>
    public Task FullPowerUntilPluggedInAsync() { _fullPowerUntilAc = true; Log.Write("full power until plugged in: requested"); return SyncBatteryProfileAsync("full power until plugged in"); }

    /// <summary>
    /// Unplugged: Razer Custom mode at the battery boost levels (the saved mode is NOT overwritten) and the
    /// built-in panel at BatteryRefreshHz. Plugged in - main charger or USB-C, both report AC online - the saved
    /// mode and the previous refresh rate come back. Keyboard lighting is never touched. Idempotent: it runs on
    /// every power event, including the battery-percentage ticks, and only acts when something has to change.
    /// </summary>
    private async Task SyncBatteryProfileAsync(string why, bool panel = false)
    {
        if (_exiting || !_settings.FirstRunComplete) return;
        await _batteryGate.WaitAsync();
        try
        {
            bool battery = _forcedBattery ?? OnBattery;
            bool flipped = _lastBattery != battery;
            _lastBattery = battery;
            if (!battery) _fullPowerUntilAc = false;                           // 2.7.0: any AC clears it
            bool onBatteryProfile = _settings.BatteryProfile && battery;      // = the old 'want'
            bool want = onBatteryProfile && !_fullPowerUntilAc;               // 2.7.0: only the Razer mode honours the flag

            if (want && !_settings.BatteryProfileEngaged)
            {
                var r = await _runner.ApplyTransientCustomAsync(_settings.BatteryCpuBoost, _settings.BatteryGpuBoost);
                Log.Write($"battery profile engage ({why}): {r.Outcome} {r.Message}");
                if (r.Ok)
                {
                    _settings.BatteryProfileEngaged = true; _settings.Save(); _batteryFailNotified = false;
                    _tray.Notify($"On battery: cool mode on (Razer Custom, CPU {CommandRunner.BoostName(_settings.BatteryCpuBoost, true)} / GPU {CommandRunner.BoostName(_settings.BatteryGpuBoost, false)}). Plug in for full power.", true);
                }
                else if (!_batteryFailNotified) { _batteryFailNotified = true; _tray.Notify("On battery, but the Razer power mode could not be lowered: " + r.Headline, false); }
            }
            else if (!want && _settings.BatteryProfileEngaged)
            {
                var r = await _runner.SetPowerModeAsync(_settings.PerfModeValue);
                Log.Write($"battery profile release ({why}): {r.Outcome} {r.Message}");
                if (r.Ok)
                {
                    _settings.BatteryProfileEngaged = false; _settings.Save(); _batteryFailNotified = false;
                    if (why == "full power until plugged in")
                        _tray.Notify($"Full power until you plug in: Razer {_settings.PowerMode}." +
                                     (_settings.BatteryServicesStopped.Count > 0 ? " The NVIDIA display service stays stopped" : " The NVIDIA display service is left as it was") +
                                     $" and the screen stays at {(PanelRefresh.FindInternal() is string pd ? PanelRefresh.Current(pd)?.ToString() ?? "?" : _settings.BatteryRefreshHz.ToString())} Hz.", true);
                    else
                        _tray.Notify(battery ? $"Battery cool mode off: Razer {_settings.PowerMode}." : $"Plugged in: full power back (Razer {_settings.PowerMode}).", true);
                }
                else if (!_batteryFailNotified) { _batteryFailNotified = true; _tray.Notify("Plugged in, but the Razer power mode could not be restored: " + r.Headline, false); }
            }

            // Refresh only on a real flip (or startup/resume/panel turning on), so a rate the user picks by hand
            // on battery is not overridden by the next battery-percentage tick.
            // Panel refresh and the NVIDIA services key on the real battery state, never on the full-power flag: otherwise a
            // later panel:true call on battery would restore 360 Hz and restart NVDisplay.ContainerLocalSystem (18 -> 38-46 W).
            if (flipped || panel) SyncPanelRefresh(onBatteryProfile, why);
            if (flipped || panel) SyncBatteryServices(onBatteryProfile, why);
        }
        catch (Exception ex) { Log.Error($"battery profile ({why}) failed: {ex.Message}"); }
        finally { _batteryGate.Release(); }
    }

    private void SyncPanelRefresh(bool battery, string why)
    {
        var dev = PanelRefresh.FindInternal();
        _panelActive = dev != null;
        if (dev == null) return;   // lid shut / docked: nothing to change; PanelAcRefreshHz keeps what to restore later
        int? cur = PanelRefresh.Current(dev);
        if (cur is not int now) return;
        var rates = PanelRefresh.Rates(dev);

        if (battery && _settings.BatteryRefreshHz > 0)
        {
            int target = rates.Contains(_settings.BatteryRefreshHz) ? _settings.BatteryRefreshHz
                       : rates.Where(r => r >= _settings.BatteryRefreshHz).DefaultIfEmpty(0).Min();
            if (target <= 0 || target >= now) return;
            if (_settings.PanelAcRefreshHz == 0) _settings.PanelAcRefreshHz = now;
            _panelChangedAt = DateTime.Now;
            var err = PanelRefresh.Set(dev, target);
            if (err == null) { _settings.Save(); Log.Ok($"built-in panel {now} -> {target} Hz on battery ({why})"); }
            else Log.Warn($"built-in panel -> {target} Hz failed ({why}): {err}");
        }
        else if (!battery && _settings.PanelAcRefreshHz > 0)
        {
            int back = rates.Contains(_settings.PanelAcRefreshHz) ? _settings.PanelAcRefreshHz : (rates.Count > 0 ? rates.Max : now);
            if (back != now)
            {
                _panelChangedAt = DateTime.Now;
                var err = PanelRefresh.Set(dev, back);
                if (err == null) Log.Ok($"built-in panel {now} -> {back} Hz on AC ({why})");
                else { Log.Warn($"built-in panel -> {back} Hz failed ({why}): {err}"); return; }
            }
            _settings.PanelAcRefreshHz = 0; _settings.Save();
        }
    }

    /// <summary>
    /// Battery: stop the services in BatteryStopServices (NVIDIA's background container kept waking the sleeping
    /// RTX 3070 every second on battery - measured 43.7 W with it, 22.8 W without). AC: start everything this
    /// stopped, tracked in BatteryServicesStopped so a restart or crash on battery still gets them back. (2.6.1)
    /// </summary>
    private void SyncBatteryServices(bool battery, string why)
    {
        try
        {
            if (!battery || _exiting)
            {
                if (_settings.BatteryServicesStopped.Count == 0) return;
                foreach (var s in _settings.BatteryServicesStopped.ToList())
                {
                    var r = Sc("start", s);
                    Log.Write($"battery services ({why}): start {s} -> {r}");
                    if (r is "ok" or "already running" or "not installed") _settings.BatteryServicesStopped.Remove(s);   // else keep it: retried on the next AC event or start
                }
                _settings.Save();
                return;
            }
            if (_settings.BatteryStopServices.Count == 0) return;
            if (!AutoStart.IsElevated) { Log.Warn($"battery services ({why}): this BladeCtl is not elevated, so {string.Join(", ", _settings.BatteryStopServices)} keep running"); return; }
            foreach (var s in _settings.BatteryStopServices)
            {
                if (_settings.BatteryServicesStopped.Contains(s, StringComparer.OrdinalIgnoreCase)) continue;
                if (ServiceState(s) != "RUNNING") continue;   // not installed or already stopped: not ours to restart later
                var r = Sc("stop", s);
                Log.Write($"battery services ({why}): stop {s} -> {r}");
                if (r is "ok" or "not running") _settings.BatteryServicesStopped.Add(s);
            }
            _settings.Save();
        }
        catch (Exception ex) { Log.Error($"battery services ({why}) failed: {ex.Message}"); }
    }

    private static string ServiceState(string name)
    {
        var o = RunSc($"query \"{name}\"", out _);
        var m = System.Text.RegularExpressions.Regex.Match(o, @"STATE\s*:\s*\d+\s+(\w+)");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "MISSING";
    }

    /// <summary>sc.exe start/stop, with the common "already" exit codes turned into plain words.</summary>
    private static string Sc(string verb, string name)
    {
        var o = RunSc($"{verb} \"{name}\"", out var code);
        return code switch
        {
            0 => "ok",
            1056 => "already running",
            1062 => "not running",
            1060 => "not installed",
            5 => "access denied",
            _ => $"exit {code}: {o.Trim().Replace(Environment.NewLine, " ")}",
        };
    }

    private static string RunSc(string args, out int code)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } code = -1; return "timed out"; }
        code = p.ExitCode; return o;
    }

    /// <summary>Lid opened / undocked: the built-in panel just came on, so give it the right rate for the power source.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _power.OnDisplayChanged();
        if (DateTime.Now - _panelChangedAt < TimeSpan.FromSeconds(5)) return;   // our own change echoing back
        bool active = PanelRefresh.FindInternal() != null;
        bool appeared = active && !_panelActive;
        _panelActive = active;
        if (appeared) _ = SyncBatteryProfileAsync("built-in panel turned on", panel: true);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        Log.Write($"session ending ({e.Reason})");
        RestoreFanNow("session ending");
        try { _power.FlushLearned(); } catch { }
        if (_settings.BlockSynapseAutostart) RazerGuard.RemoveAutostart();
        Log.SessionEnd("session ending");
    }

    private async Task RestoreAutoFromHotkeyAsync()
    {
        var r = await _runner.RestoreAutoAsync();
        _tray.Notify(r.Headline, r.Ok);
    }

    /// <summary>Signals from a later launch (`BladeCtl.exe`, `--restore-auto`, `--exit`, `--capture`).</summary>
    public void OnSignal(SingleInstance.Sig sig)
    {
        Dispatch(() =>
        {
            switch (sig)
            {
                case SingleInstance.Sig.Show: ShowWindow(); break;
                case SingleInstance.Sig.RestoreAuto: _ = RestoreAutoFromHotkeyAsync(); break;
                case SingleInstance.Sig.Exit: _ = ExitAppAsync(prompt: false, reason: "--exit"); break;
                case SingleInstance.Sig.Capture: _ = CaptureAsync(); break;
            }
        });
    }

    private async Task CaptureAsync()
    {
        string path; string view = "";
        try
        {
            var lines = File.ReadAllLines(SingleInstance.CaptureRequestFile);
            path = lines.Length > 0 ? lines[0].Trim() : "";
            view = lines.Length > 1 ? lines[1].Trim().ToLowerInvariant() : "";
        }
        catch (Exception ex) { Log.Error("capture: no request file: " + ex.Message); return; }
        try { File.Delete(SingleInstance.CaptureRequestFile); } catch { }
        if (view == "devices") _vm.ViewIndex = 1; else if (view == "blade") _vm.ViewIndex = 0;
        await CaptureWindowAsync(path, keepShown: true);
    }

    /// <summary>Show (if needed), render to PNG, then restore the previous visibility.</summary>
    private async Task CaptureWindowAsync(string path, bool keepShown)
    {
        bool wasVisible = _window is { IsVisible: true };
        ShowWindow();
        await Task.Delay(900); // let the first poll land and the window paint
        try
        {
            _window!.CaptureToPng(path);
            Log.Ok($"window captured to {path}");
        }
        catch (Exception ex) { Log.Error("capture failed: " + ex.Message); }
        if (!wasVisible && !keepShown) _window!.Hide();
    }

    /// <summary>
    /// File-based diagnostics channel: a `dump-request.txt` next to the exe makes the instance write
    /// `bladectl-dump.txt` (paths, settings, last 300 log lines) and `bladectl-window.png` beside it.
    /// Exists because a sandboxed or non-elevated shell can neither open this instance's kernel objects nor read the
    /// real %APPDATA% files it writes; the exe directory is the one place both sides see.
    /// </summary>
    private void StartDumpWatch()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += async (_, _) =>
        {
            await RunCommandFileAsync();
            if (!File.Exists(SingleInstance.DumpRequestFile)) return;
            try { File.Delete(SingleInstance.DumpRequestFile); } catch { }
            Log.Write("dump requested via exe-directory file");
            string dir = AppContext.BaseDirectory;
            try
            {
                var s = _monitor.Snapshot;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"BladeCtl {Program.Version} pid={Environment.ProcessId} elevated={AutoStart.IsElevated} uptime={Log.UptimeSpan.ToString(@"hh\:mm\:ss")} now={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"exe={Environment.ProcessPath}");
                sb.AppendLine($"appdata={Log.AppDataDir}  logdir={Log.Dir}  logfile={Log.FilePath}  dirNote={Log.DirNote ?? "none"}  lastLogError={Log.LastError ?? "none"}");
                sb.AppendLine($"settings file={Settings.FilePath}");
                sb.AppendLine($"settings: {_settings.Summary}  firstRunComplete={_settings.FirstRunComplete}  lastStartupApply={_settings.LastStartupApplyUtc ?? "never"}");
                sb.AppendLine($"device: connected={s.Connected} fw={s.Firmware} mode={s.Mode} manual={s.ManualFan} rpm={s.Rpm1}/{s.Rpm2} bright={s.Brightness} cpu={s.CpuC?.ToString("F0") ?? s.CpuNote} gpu={s.GpuC?.ToString("F0") ?? s.GpuNote} gpuSrc={s.GpuNote} dgpuD={GpuPower.NvidiaDState()?.ToString() ?? "?"}");
                sb.AppendLine($"battery profile: enabled={_settings.BatteryProfile} engaged={_settings.BatteryProfileEngaged} onBattery={OnBattery} levels=CPU {_settings.BatteryCpuBoost}/GPU {_settings.BatteryGpuBoost} refresh={_settings.BatteryRefreshHz} Hz panelAc={_settings.PanelAcRefreshHz} panel={PanelRefresh.Describe()} " +
                              $"stopServices={string.Join(",", _settings.BatteryStopServices)} stoppedNow={string.Join(",", _settings.BatteryServicesStopped)} elevated={AutoStart.IsElevated}");
                sb.AppendLine($"synapse: {s.Synapse.Summary}  run-key: {string.Join(",", RazerGuard.PresentValues())}");
                sb.AppendLine($"autostart: {(AutoStart.IsEnabled ? AutoStart.RegisteredTarget() : "not registered")}  hotkey={_owner.HotkeyRegistered}  window={(_window is { IsVisible: true } ? "shown" : "hidden")}");
                sb.AppendLine($"boot task: {(AutoStart.IsBootLightingEnabled ? AutoStart.BootLightingTarget() : "not registered")}");
                sb.AppendLine($"fan target: {(_fanTarget.Active ? _fanTarget.StatusLine : "not armed")}  paused={_fanTarget.Paused}");
                foreach (var pl in _power.DumpLines()) sb.AppendLine(pl);
                sb.AppendLine($"power-flags: fullPowerUntilAc={(_fullPowerUntilAc ? 1 : 0)} sessionLocked={(_sessionLocked ? 1 : 0)}");
                foreach (var d in _accessories.Devices) sb.AppendLine("accessory: " + AccessoryService.Describe(d));
                sb.AppendLine("--- last 300 log lines ---");
                foreach (var l in Log.Tail(300)) sb.AppendLine(l);
                File.WriteAllText(Path.Combine(dir, "bladectl-dump.txt"), sb.ToString(), new System.Text.UTF8Encoding(true));
                await CaptureWindowAsync(Path.Combine(dir, "bladectl-window.png"), keepShown: false);
                Log.Ok("dump written next to the exe");
            }
            catch (Exception ex) { Log.Error("dump failed: " + ex.Message); }
        };
        t.Start();
    }

    /// <summary>
    /// `command-request.txt` next to the exe, one command per line, consumed within 5 s:
    ///   fan-target &lt;55-90&gt; [Hottest|CPU|GPU]   arm the temperature loop
    ///   fan-target off                          disarm it
    ///   restore-auto                            firmware fan control
    ///   power balanced|gaming
    /// Same rationale as the dump file: a sandboxed or non-elevated shell can reach the exe directory and nothing else.
    /// </summary>
    private async Task RunCommandFileAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "command-request.txt");
        if (!File.Exists(path)) return;
        string[] lines;
        try { lines = File.ReadAllLines(path); File.Delete(path); }
        catch (Exception ex) { Log.Error("command file: " + ex.Message); return; }
        foreach (var raw in lines)
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            Log.Write("command file: " + raw.Trim());
            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "fan-target":
                        if (parts.Length >= 2 && parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)) await _fanTarget.StopAsync("command file");
                        else if (parts.Length >= 2 && int.TryParse(parts[1], out var c)) _fanTarget.Start(c, parts.Length >= 3 ? parts[2] : _settings.TargetSensor);
                        break;
                    case "restore-auto": await _runner.RestoreAutoAsync(); break;
                    case "power":
                    {
                        string pm = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "balanced";
                        var mode = pm switch { "gaming" => PerfMode.Gaming, "creator" => PerfMode.Creator, "custom" => PerfMode.Custom, _ => PerfMode.Balanced };
                        if (mode == PerfMode.Custom && parts.Length >= 4 && int.TryParse(parts[2], out var cb) && int.TryParse(parts[3], out var gb))
                        { _settings.CpuBoost = Math.Clamp(cb, 0, 3); _settings.GpuBoost = Math.Clamp(gb, 0, 2); }
                        await _runner.SetPowerModeAsync(mode);
                        break;
                    }
                    case "boost":
                        if (parts.Length >= 3 && int.TryParse(parts[1], out var c1) && int.TryParse(parts[2], out var g1)) await _runner.SetBoostAsync(c1, g1);
                        break;
                    case "logo":
                        await _runner.SetLogoAsync(parts.Length >= 2 ? parts[1].ToLowerInvariant() switch { "on" => "On", "blink" => "Blink", _ => "Off" } : "Off");
                        break;
                    case "lighting":
                    {
                        string eff = parts.Length >= 2 ? parts[1] : "Static";
                        byte lr = (byte)_settings.ColorR, lg = (byte)_settings.ColorG, lb = (byte)_settings.ColorB;
                        if (parts.Length >= 5 && byte.TryParse(parts[2], out var pr) && byte.TryParse(parts[3], out var pg) && byte.TryParse(parts[4], out var pb)) { lr = pr; lg = pg; lb = pb; }
                        await _runner.SetLightingAsync(eff, lr, lg, lb);
                        break;
                    }
                    case "lights":
                        if (parts.Length >= 2 && parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)) await LightsOffAsync("command file");
                        else await LightsRestoreAsync("command file");
                        break;
                    case "match-blade":
                        await _accessories.ApplyToAllAsync(_settings.Rgb, _settings.ColorR, _settings.ColorG, _settings.ColorB, _settings.Brightness);
                        break;
                    case "analog":
                    {
                        // analog on|off · analog joystick on|off · analog guard on|off
                        string what = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "off";
                        bool on = parts.Length >= 3 ? parts[2].Equals("on", StringComparison.OrdinalIgnoreCase) : what == "on";
                        if (what == "joystick") _settings.Analog.Joystick = on;
                        else if (what == "guard") _settings.Analog.Adaptive = on;
                        else _settings.Analog.Enabled = on;
                        _settings.Save();
                        OnAnalogChanged();
                        break;
                    }
                    case "battery-profile":
                    {
                        // battery-profile on|off            enable/disable the automatic battery profile
                        // battery-profile test|release      act as if unplugged / back to the real power source (testing on AC)
                        // battery-profile levels <cpu 0-3> <gpu 0-2> · battery-profile refresh <hz, 0 = leave alone>
                        string what = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "status";
                        if (what is "on" or "off") { _settings.BatteryProfile = what == "on"; _settings.Save(); }
                        else if (what == "test") _forcedBattery = true;
                        else if (what == "release") _forcedBattery = null;
                        else if (what == "levels" && parts.Length >= 4 && int.TryParse(parts[2], out var bc) && int.TryParse(parts[3], out var bg))
                        { _settings.BatteryCpuBoost = Math.Clamp(bc, 0, 3); _settings.BatteryGpuBoost = Math.Clamp(bg, 0, 2); _settings.Save(); }
                        else if (what == "refresh" && parts.Length >= 3 && int.TryParse(parts[2], out var hz))
                        { _settings.BatteryRefreshHz = Math.Max(0, hz); _settings.Save(); }
                        if (what == "fullpower") await FullPowerUntilPluggedInAsync();
                        else if (what != "status") await SyncBatteryProfileAsync("command file " + what, panel: true);
                        Log.Write($"battery profile: enabled={_settings.BatteryProfile} engaged={_settings.BatteryProfileEngaged} forced={_forcedBattery?.ToString() ?? "no"} onBattery={OnBattery} " +
                                  $"levels CPU {_settings.BatteryCpuBoost}/GPU {_settings.BatteryGpuBoost} refresh {_settings.BatteryRefreshHz} Hz · panel {PanelRefresh.Describe()}");
                        break;
                    }
                    case "power-learn":
                        // power-learn reset : forget the Power card's learned limits, charge curve and references
                        if (parts.Length >= 2 && parts[1].Equals("reset", StringComparison.OrdinalIgnoreCase)) _power.ResetLearned();
                        else Log.Write("power-learn: " + string.Join(" | ", _power.DumpLines()));
                        break;
                    case "power-usbc":
                    {
                        // power-usbc confirmed|unconfirmed : record test B1 (the dock-only EC reading is USB-C); drops the "USB-C?" question mark
                        string what = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "status";
                        if (what is "confirmed" or "unconfirmed") _power.UsbcConfirmed = what == "confirmed";
                        Log.Write($"power-usbc: USB-C {(_power.UsbcConfirmed ? "confirmed (shown as USB-C)" : "not confirmed (shown as USB-C?)")}");
                        break;
                    }
                    case "failfast-test":
                        // Test hook only: die the way the runtime killed 2.4.0 (no exit handlers run) to prove the guardian.
                        Log.Warn("command file: FAILFAST TEST - terminating without cleanup on purpose");
                        Environment.FailFast("BladeCtl failfast test (command file)");
                        break;
                    case "razer-pad":
                    {
                        // razer-pad disable|enable|query : the phantom Xbox controller Razer's driver attaches to the Huntsman
                        string what = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "query";
                        string r = what == "disable" ? PhantomPad.Disable() : what == "enable" ? PhantomPad.Enable() : PhantomPad.Query();
                        Log.Write($"razer phantom controller {what}: {r}");
                        break;
                    }
                    default: Log.Warn("command file: unknown command " + parts[0]); break;
                }
            }
            catch (Exception ex) { Log.Error($"command file '{raw}': {ex.Message}"); }
        }
    }

    public void RestartElevated()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "BladeCtl.exe") { UseShellExecute = true, Verb = "runas" };
            Log.Write("relaunching elevated at user request");
            _ = ExitAppAsync(prompt: false, reason: "restart elevated").ContinueWith(_ => Process.Start(psi));
        }
        catch (Exception ex)
        {
            Log.Warn("elevated relaunch failed/declined: " + ex.Message);
        }
    }

    // ---------- exit ----------

    private void RestoreFanNow(string why)
    {
        bool engaged = _settings.ManualFanRequested || _fanTarget.CommandedRpm != null || _monitor.Snapshot.ManualFan == true;
        if (!engaged) return;
        var ctl = _monitor.Controller;
        if (ctl == null) return;
        var res = ctl.RestoreAutoFanVerbose(_settings.PerfModeValue);
        Log.Write($"{why}: restored auto fan -> {res.Describe()}");
        _fanTarget.NoteExternalRevert();
        // A temperature target stays armed for the next session; a fixed floor does not.
        if (_settings.ManualFanRequested) { _settings.FanMode = "Auto"; _settings.Save(); }
    }

    /// <summary>A manual fan floor persists in the EC after we exit — make that a decision, not an accident.</summary>
    public async Task ExitAppAsync(bool prompt, string reason)
    {
        if (_exiting) return;

        bool floorEngaged = _settings.ManualFanRequested || _fanTarget.CommandedRpm != null;
        if (floorEngaged && _monitor.Controller != null)
        {
            if (prompt)
            {
                int rpmNow = _fanTarget.CommandedRpm ?? _settings.ManualRpm;
                int choice = await _vm.ShowDialogAsync(new DialogSpec("Exiting with a fan floor engaged",
                    $"A floor of {rpmNow} RPM is active{(_fanTarget.CommandedRpm != null ? " (held by the temperature target)" : "")}. It stays engaged in the laptop's controller after BladeCtl exits, and nothing will be watching temperatures.",
                    Array.Empty<(DialogBullet, string)>(),
                    new[] { ("Cancel", DialogButton.Ghost), ("Leave floor engaged", DialogButton.Quiet), ("Restore auto and exit", DialogButton.Primary) },
                    DialogKind.Warn));
                if (choice == 0) return;
                if (choice == 1) Log.Warn("exit: user chose to LEAVE the manual fan floor engaged");
                else RestoreFanNow("exit");
            }
            else RestoreFanNow("exit");
        }

        _analog.Stop("exit");
        _monitor.Power = null;
        try { _power.Dispose(); } catch (Exception ex) { Log.Error("power card shutdown: " + ex.Message); }   // NVML shutdown + FreeLibrary, PDH closed, learned data flushed
        // Nobody else would restart services BladeCtl stopped for battery, so give them back before leaving.
        if (_settings.BatteryServicesStopped.Count > 0) SyncBatteryServices(false, "exit");
        _exiting = true;
        Log.SessionEnd(reason);

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        RazerGuard.StopWatching();

        try { _window?.Close(); } catch { }
        _tray.Dispose();
        _fanTarget.Dispose();
        _monitor.Dispose();
        _accessories.Dispose();
        _lock.Dispose();
        _analog.Dispose();
        _owner.Dispose();
        SingleInstance.Release();
        _app.Shutdown();
    }
}
