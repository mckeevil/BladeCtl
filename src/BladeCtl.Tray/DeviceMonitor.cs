using System.Diagnostics;
using System.Management;
using BladeCtl.Core;

namespace BladeCtl.Tray;

/// <summary>What the DEVICE actually reports, plus sensors and contention. Never user intent.</summary>
public sealed record DeviceSnapshot(
    bool Connected,
    string? DevicePath,
    string? Firmware,
    string? NotFoundReason,
    PerfMode? Mode,
    bool? ManualFan,
    int? Rpm1,
    int? Rpm2,
    int? Brightness,
    double? CpuC,
    double? GpuC,
    string? CpuNote,
    string? GpuNote,
    SynapseDetector.Status Synapse,
    DateTime TakenLocal,
    int? CpuBoost = null,
    int? GpuBoost = null,
    bool? LogoOn = null,
    byte? LogoEffect = null)
{
    public string LogoText => LogoOn == true ? (LogoEffect == 2 ? "Blink" : "On") : LogoOn == false ? "Off" : "?";
    public double? MaxTempC =>
        CpuC.HasValue && GpuC.HasValue ? Math.Max(CpuC.Value, GpuC.Value) : (CpuC ?? GpuC);

    public bool HasAnySensor => CpuC.HasValue || GpuC.HasValue;

    public static DeviceSnapshot Disconnected(string reason) => new(
        false, null, null, reason, null, null, null, null, null, null, null, null, null,
        SynapseDetector.Detect(), DateTime.Now);
}

/// <summary>
/// Owns the single background timer that polls the device, sensors and Synapse state.
/// All device I/O happens here, off the UI thread — the menu and window only render the
/// last snapshot, so opening the menu can never stall on HID or nvidia-smi.
///
/// Thermal supervision: while a manual fan floor is engaged, it watches temps and auto-reverts
/// to firmware control at <see cref="AutoRevertC"/>.
///
/// Logging: a HEARTBEAT line every 15 minutes, plus every connect / disconnect / drift / Synapse
/// transition, so the log can answer "was it running and what did the device say" for any hour.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    /// <summary>GPU auto-revert point. Unchanged: the GPU sensor was always real.</summary>
    public const double AutoRevertC = 90.0;
    public const double ReArmC = 85.0;

    /// <summary>
    /// CPU auto-revert point, separate from the GPU's because the two chips run at very different
    /// temperatures. Measured on this machine 2026-09-07: the i7-11800H idles at 89-91 C and reaches 98 C
    /// under an all-core load, against a Tj max of 100 C. A shared 90 C limit - which is what the old code
    /// nominally applied - would trip permanently at idle now that the sensor reports the truth, so the CPU
    /// gets its own limit just under Tj max.
    /// </summary>
    public const double AutoRevertCpuC = 97.0;
    public const double ReArmCpuC = 93.0;
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(15);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly Settings _settings;

    private BladeController? _ctl;
    private int _tick;
    private int _gpuFailures;
    private bool _thermalTripped;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private bool? _lastSynapseRunning;

    public DeviceSnapshot Snapshot { get; private set; } = DeviceSnapshot.Disconnected("starting up");
    public double? PeakSinceManual { get; private set; }

    /// <summary>Raised on the timer thread whenever a new snapshot lands.</summary>
    public event Action<DeviceSnapshot>? Updated;

    /// <summary>Raised when a manual floor was auto-reverted for heat. Args: temperature seen.</summary>
    public event Action<double>? ThermalAutoRevert;

    /// <summary>Raised once when the limit is reached with the fans ALREADY at maximum, so nothing was reverted.</summary>
    public event Action<double>? MaxFanCeiling;

    /// <summary>
    /// The software fan floor currently commanded (temperature loop or manual), or null when the firmware owns
    /// the fans. Set by the composition root; used so the thermal auto-revert never spins the fans DOWN.
    /// </summary>
    public Func<int?>? CommandedFloorRpm { get; set; }

    private bool _maxFanWarned;

    /// <summary>Raised when the device reports something different from what the user saved.</summary>
    public event Action<string>? DriftDetected;

    /// <summary>Set by the window so polling is fast while it is on screen.</summary>
    public bool WindowVisible { get; set; }

    /// <summary>True while lights are deliberately off (locked / idle): brightness 0 is then not drift.</summary>
    public bool LightsOff { get; set; }

    /// <summary>2.7.0 Power card sampler, called once per tick on this thread (spec 12.2). Null = no Power card.</summary>
    public Power.PowerSampler? Power { get; set; }

    /// <summary>dGPU D-state read once per tick (wake-free): on AC as before, on battery only when the Power sampler samples. Null = not read.</summary>
    public int? DState { get; private set; }

    public DeviceMonitor(Settings settings)
    {
        _settings = settings;
        _timer = new System.Threading.Timer(_ => SafeTick(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public BladeController? Controller { get { lock (_gate) return _ctl; } }

    /// <summary>Force an immediate poll (after a command, or on user request).</summary>
    public void PollNow() => _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    private TimeSpan NextInterval()
    {
        if (WindowVisible) return TimeSpan.FromSeconds(2);
        if (_settings.TargetFanRequested) return TimeSpan.FromSeconds(5);   // control loop cadence
        if (_settings.ManualFanRequested) return TimeSpan.FromSeconds(10);
        return TimeSpan.FromSeconds(30);
    }

    private readonly object _tickGate = new();

    private void SafeTick()
    {
        // Timer callbacks can overlap when PollNow() is called repeatedly. Without this guard two
        // threads both see _ctl == null, both run Find(), and one HID handle leaks unreferenced.
        if (!Monitor.TryEnter(_tickGate)) return;
        try { Tick(); }
        catch (Exception ex) { Log.Error($"monitor tick error: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            Monitor.Exit(_tickGate);
            try { _timer.Change(NextInterval(), Timeout.InfiniteTimeSpan); } catch { }
        }
    }

    private void Tick()
    {
        _tick++;
        EnsureConnected();

        BladeController? ctl;
        lock (_gate) ctl = _ctl;

        DeviceSnapshot snap;
        if (ctl == null)
        {
            snap = DeviceSnapshot.Disconnected(_lastFindReason ?? "no Razer Blade control device answered");
            ReadDState();
            PowerTick(new Power.TickInput(null, DState, null, null, null, null, null));
        }
        else
        {
            var ps = ctl.GetPowerState(1);
            var rpm1 = ctl.GetFanRpm(1);
            var rpm2 = ctl.GetFanRpm(2);
            var bright = ctl.GetBrightness();
            var cpuBoost = ctl.GetCpuBoost();
            var gpuBoost = ctl.GetGpuBoost();
            var logoOn = ctl.GetLogoLed();
            var logoEff = ctl.GetLogoEffect();

            // Device went away mid-poll (undock / sleep): drop the controller so we re-Find next tick.
            if (ctl.ConsecutiveFailures >= 3)
            {
                Log.Warn($"device unresponsive ({ctl.ConsecutiveFailures} consecutive failures) - dropping controller to re-probe" +
                         (Snapshot.Synapse.SynapseRunning ? $" (Synapse running, {Snapshot.Synapse.SynapseProcessCount} processes - contention likely)" : ""));
                lock (_gate) { _ctl?.Dispose(); _ctl = null; }
                snap = DeviceSnapshot.Disconnected("device stopped responding (undocked, asleep, or claimed by another app)");
                Publish(snap);
                return;
            }

            var (cpu, cpuNote) = ReadCpuTemp(ctl);
            ReadDState();
            PowerTick(new Power.TickInput(ctl, DState, ps?.Mode, cpuBoost, gpuBoost, cpuNote == "Razer EC" ? cpu : null, Snapshot.Connected ? Snapshot.GpuC : null));
            var (gpu, gpuNote) = ReadGpuTemp(ctl);

            snap = new DeviceSnapshot(
                true, _devicePath, _firmware, null,
                ps?.Mode, ps?.ManualFan, rpm1, rpm2, bright,
                cpu, gpu, cpuNote, gpuNote,
                (_tick % 5 == 1) ? SynapseDetector.Detect() : Snapshot.Synapse,
                DateTime.Now, cpuBoost, gpuBoost, logoOn, logoEff);
        }

        Publish(snap);
        CheckSynapseTransition(snap);
        CheckThermal(snap);
        CheckDrift(snap);
        Heartbeat(snap);
    }

    /// <summary>One wake-free D-state read per tick, shared by ReadGpuTemp and the Power sampler (2.7.0).</summary>
    private void ReadDState()
    {
        bool onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
        DState = !onBattery || Power?.WantsDState == true ? GpuPower.NvidiaDState() : null;
    }

    private void PowerTick(Power.TickInput x)
    {
        if (Power == null) return;
        try { Power.OnTick(x); }
        catch (Exception ex) { Log.Error($"power sampler tick: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Publish(DeviceSnapshot snap)
    {
        Snapshot = snap;
        if (snap.Connected && snap.ManualFan == true && snap.MaxTempC is double t)
            PeakSinceManual = PeakSinceManual.HasValue ? Math.Max(PeakSinceManual.Value, t) : t;
        else if (snap.ManualFan != true)
            PeakSinceManual = null;

        try { Updated?.Invoke(snap); } catch (Exception ex) { Log.Error("Updated handler threw: " + ex.Message); }
    }

    private void Heartbeat(DeviceSnapshot s)
    {
        if (DateTime.Now - _lastHeartbeat < HeartbeatEvery) return;
        _lastHeartbeat = DateTime.Now;
        Log.Write(HeartbeatLine(s));
    }

    public string HeartbeatLine(DeviceSnapshot s) =>
        s.Connected
            ? $"HEARTBEAT mode={s.Mode?.ToString() ?? "?"} fan={(s.ManualFan == true ? "MANUAL" : "auto")} z1={s.Rpm1?.ToString() ?? "?"} z2={s.Rpm2?.ToString() ?? "?"} " +
              $"bright={s.Brightness?.ToString() ?? "?"} boost={s.CpuBoost?.ToString() ?? "?"}/{s.GpuBoost?.ToString() ?? "?"} logo={s.LogoText} cpu={(s.CpuC.HasValue ? s.CpuC.Value.ToString("F0") : "n/a")} gpu={(s.GpuC.HasValue ? s.GpuC.Value.ToString("F0") : "n/a")} " +
              $"synapse={(s.Synapse.SynapseRunning ? s.Synapse.SynapseProcessCount.ToString() : "no")} window={(WindowVisible ? "shown" : "hidden")} uptime={Log.UptimeSpan:hh\\:mm\\:ss}" + (Power?.HeartbeatSuffix ?? "")
            : $"HEARTBEAT DISCONNECTED ({s.NotFoundReason}) synapse={(s.Synapse.SynapseRunning ? s.Synapse.SynapseProcessCount.ToString() : "no")} uptime={Log.UptimeSpan:hh\\:mm\\:ss}" + (Power?.HeartbeatSuffix ?? "");

    private void CheckSynapseTransition(DeviceSnapshot s)
    {
        bool now = s.Synapse.SynapseRunning;
        if (_lastSynapseRunning == now) return;
        if (_lastSynapseRunning.HasValue)
        {
            if (now) Log.Warn($"Razer Synapse appeared ({s.Synapse.SynapseProcessCount} processes: {string.Join(", ", s.Synapse.Names)}) - it shares the control interface");
            else Log.Write("Razer Synapse is no longer running");
        }
        _lastSynapseRunning = now;
    }

    /// <summary>
    /// THERMAL SAFETY: the firmware failsafe is always active, but a low manual floor plus a heavy
    /// load can still let temps climb. If we can actually read a sensor, revert to firmware control.
    /// </summary>
    private void CheckThermal(DeviceSnapshot snap)
    {
        if (!snap.Connected || snap.ManualFan != true) { _thermalTripped = false; return; }
        if (!snap.HasAnySensor) return; // no sensor -> user was warned at engage time

        // Per-sensor limits: a single threshold against max(cpu, gpu) is wrong now that the CPU reports its
        // real 89-91 C idle, because the CPU would hold the trip permanently while a genuinely hot GPU at
        // 89 C would not trip at all.
        bool cpuOver = snap.CpuC is double c && c >= AutoRevertCpuC;
        bool gpuOver = snap.GpuC is double g && g >= AutoRevertC;

        if (!_thermalTripped && (cpuOver || gpuOver))
        {
            double t = cpuOver ? snap.CpuC!.Value : snap.GpuC!.Value;

            // Never revert away from a floor that is already at maximum. The auto-revert exists for the case
            // where the user's floor is LOWER than the firmware would run, so handing control back means more
            // cooling. At full speed the opposite is true: measured on this machine 2026-09-07, reverting at
            // 97 C dropped the fans from 4800 to 2900 RPM, i.e. the safety feature made the machine hotter.
            // If we are at max and still climbing, the fans are no longer the lever - power is.
            int? floor = CommandedFloorRpm?.Invoke();
            if (floor.HasValue && floor.Value >= BladeController.FanMaxRpm - 100)
            {
                if (!_maxFanWarned)
                {
                    _maxFanWarned = true;
                    Log.Warn($"{(cpuOver ? "CPU" : "GPU")} {t:F0}C with the fans already at {floor.Value} RPM - HOLDING max fans " +
                             "(reverting to firmware here would spin them down). Reduce the power ceiling to go lower.");
                    try { MaxFanCeiling?.Invoke(t); } catch { }
                }
                return;
            }
            _maxFanWarned = false;

            _thermalTripped = true;
            Log.Warn($"THERMAL AUTO-REVERT @ {(cpuOver ? "CPU" : "GPU")} {t:F0}C - restoring firmware fan control");
            var ctl = Controller;
            if (ctl != null)
            {
                var res = ctl.RestoreAutoFanVerbose(_settings.PerfModeValue);
                Log.Write($"thermal auto-revert result: {res.Describe()}");
            }
            _settings.FanMode = "Auto";
            _settings.Save();
            try { ThermalAutoRevert?.Invoke(t); } catch { }
        }
        else if (_thermalTripped
                 && (snap.CpuC is not double rc || rc < ReArmCpuC)
                 && (snap.GpuC is not double rg || rg < ReArmC))
        {
            _thermalTripped = false;
        }
    }

    private string? _lastDriftKey;
    private void CheckDrift(DeviceSnapshot s)
    {
        if (!s.Connected) { _lastDriftKey = null; return; }

        var problems = new List<string>();
        bool wantManual = _settings.ManualFanRequested;
        // In Target mode the loop moves the fans between auto and manual on purpose; no drift there.
        if (!_settings.TargetFanRequested && s.ManualFan.HasValue && s.ManualFan.Value != wantManual)
            problems.Add(wantManual
                ? $"device fan is on AUTO but you saved Manual {_settings.ManualRpm} RPM"
                : "device fan is on MANUAL but you saved Auto");

        if (s.Mode.HasValue && s.Mode.Value != _settings.ExpectedPerfMode)
            problems.Add($"device power mode is {s.Mode} but you saved {_settings.ExpectedPowerModeName}");

        if (!LightsOff && s.Brightness.HasValue && Math.Abs(s.Brightness.Value - _settings.Brightness) > 2)
            problems.Add($"device brightness is {s.Brightness} but you saved {_settings.Brightness}");

        bool hadUsableRead = s.ManualFan.HasValue || s.Mode.HasValue || s.Brightness.HasValue;
        if (problems.Count == 0)
        {
            if (hadUsableRead && _lastDriftKey != null) { Log.Write("drift resolved - device matches saved settings again"); }
            if (hadUsableRead) _lastDriftKey = null;
            return;
        }

        var key = string.Join("|", problems);
        if (key == _lastDriftKey) return; // don't re-fire the same drift every tick
        _lastDriftKey = key;

        var msg = string.Join("; ", problems);
        Log.Warn("DRIFT: " + msg + (s.Synapse.SynapseRunning ? $" (Razer Synapse running, {s.Synapse.SynapseProcessCount} processes)" : ""));
        try { DriftDetected?.Invoke(msg); } catch { }
    }

    /// <summary>Current drift description for the UI, or null when the device matches what was saved.</summary>
    public string? CurrentDrift(DeviceSnapshot s)
    {
        if (!s.Connected) return null;
        var problems = new List<string>();
        if (!_settings.TargetFanRequested && s.ManualFan.HasValue && s.ManualFan.Value != _settings.ManualFanRequested)
            problems.Add(_settings.ManualFanRequested
                ? $"Device fan is on AUTO — you saved Manual {_settings.ManualRpm} RPM"
                : "Device fan is on MANUAL — you saved Auto");
        if (s.Mode.HasValue && s.Mode.Value != _settings.ExpectedPerfMode)
            problems.Add($"Device power mode is {s.Mode} — you saved {_settings.ExpectedPowerModeName}");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // ---------- connection ----------

    private string? _devicePath;
    private string? _firmware;
    private string? _lastFindReason;

    private void EnsureConnected()
    {
        lock (_gate) { if (_ctl != null) return; }

        try
        {
            var dev = BladeDevice.Find(Log.CoreLog);
            if (dev == null)
            {
                var reason = BuildNotFoundReason();
                if (reason != _lastFindReason) Log.Warn("device not found: " + reason);
                _lastFindReason = reason;
                return;
            }
            var ctl = new BladeController(dev, Log.CoreLog);
            _devicePath = dev.DevicePath;
            _firmware = ctl.GetFirmwareVersion();

            bool kept;
            lock (_gate)
            {
                kept = _ctl == null;      // never overwrite (and orphan) an existing handle
                if (kept) _ctl = ctl;
            }
            if (!kept) { ctl.Dispose(); return; }

            _lastFindReason = null;
            Log.Ok($"connected: {_devicePath} firmware {_firmware ?? "unknown"}");
        }
        catch (Exception ex)
        {
            _lastFindReason = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("connect failed: " + _lastFindReason);
        }
    }

    private static string BuildNotFoundReason()
    {
        var syn = SynapseDetector.Detect();
        if (syn.SynapseRunning)
            return "No control collection answered. Razer Synapse is running and may be holding the interface — " +
                   "close Synapse, then Retry.";
        return "No HID collection for VID 1532 / PID 0276 answered the control probe. This build supports only " +
               "the Razer Blade 15 Advanced (Mid 2021). If that is your machine, try sleeping/waking or rebooting, then Retry.";
    }

    // ---------- sensors ----------

    /// <summary>
    /// CPU temperature from the Razer EC. Until 2026-09-07 this read the first ACPI thermal zone instead, and
    /// on this chassis there is exactly one zone (\_TZ.TZ00) which is a skin sensor pinned near 28 C - so the
    /// app reported a constant 28 in all 108 logged heartbeats and the thermal auto-revert could never fire on
    /// CPU. The EC's own sensor is the real die temperature and needs no administrator rights.
    /// </summary>
    private (double?, string?) ReadCpuTemp(BladeController? ctl)
    {
        var t = ctl?.GetThermalReading();
        if (t.HasValue) return (t.Value.Cpu, "Razer EC");

        // Fallback only, and labelled honestly: this is a chassis zone, not the CPU die.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double hottest = 0;
            foreach (var o in searcher.Get())
            {
                var raw = Convert.ToDouble(o["CurrentTemperature"]);
                o.Dispose();
                double c = (raw - 2732) / 10.0;
                if (c is > 0 and < 125 && c > hottest) hottest = c;   // take the hottest zone, not the first
            }
            if (hottest > 0) return (hottest, "ACPI chassis zone, not the CPU die");
            return (null, "no usable ACPI zone");
        }
        catch (ManagementException ex) when (ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "EC did not answer; ACPI needs admin");
        }
        catch (Exception ex)
        {
            return (null, "unavailable (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>Plain-language list of which sensors the thermal auto-revert can actually see.</summary>
    public string SensorSummary()
    {
        var s = Snapshot;
        var live = new List<string>();
        if (s.CpuC.HasValue) live.Add("CPU");
        if (s.GpuC.HasValue) live.Add("GPU");
        return live.Count == 0 ? "none" : string.Join(" and ", live);
    }

    private DateTime _gpuRetryAt = DateTime.MinValue;

    private (double?, string?) ReadGpuTemp(BladeController? ctl)
    {
        // 2.6.0: nvidia-smi wakes a sleeping Optimus dGPU (D3cold -> D0), which on battery cost watts every
        // 30 s just to read a temperature. The EC reports the GPU temperature too (within 1 C of nvidia-smi,
        // verified 2026-09-07), so use it on battery and whenever the dGPU is not already awake.
        bool onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
        // 2.7.0: the D-state was read once for this tick (ReadDState), not a second time here.
        if (onBattery || DState != 1)
            return EcGpu(ctl) ?? (null, onBattery ? "GPU asleep (battery: EC only, nvidia-smi not used)" : "GPU asleep");

        // 2.7.0: the Power card's NVML batch already read the temperature this tick, so no nvidia-smi launch.
        if (Power?.FreshNvmlTempC is double nvt) return (nvt, "NVML");

        // Back off after repeated failures, but never for good: nvidia-smi hiccups transiently
        // (one odd line was seen on 2026-09-05) and the GPU sensor must come back on its own.
        if (_gpuFailures >= 2)
        {
            if (DateTime.Now < _gpuRetryAt) return EcGpu(ctl) ?? (null, "nvidia-smi unavailable, retrying soon");
            _gpuFailures = 0;
        }
        // 2.7.0: the Power tick between ReadDState and here can take seconds (NVML init, EC retries, D-state re-reads while
        // the GPU cycles), so re-read the D-state (wake-free, cheap) right before launching nvidia-smi, which would wake it.
        if (GpuPower.NvidiaDState() != 1) return EcGpu(ctl) ?? (null, "GPU asleep");
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p = Process.Start(psi);
            if (p == null) { GpuFailed(); return EcGpu(ctl) ?? (null, "nvidia-smi did not start"); }

            if (!p.WaitForExit(1500))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                GpuFailed();
                return EcGpu(ctl) ?? (null, "nvidia-smi timed out");
            }
            var outp = p.StandardOutput.ReadToEnd().Trim();
            if (int.TryParse(outp, out var t)) { _gpuFailures = 0; return (t, "nvidia-smi"); }
            GpuFailed();
            return EcGpu(ctl) ?? (null, "unexpected nvidia-smi output");
        }
        catch (Exception ex)
        {
            GpuFailed();
            return EcGpu(ctl) ?? (null, ex.GetType().Name);
        }
        finally { p?.Dispose(); }
    }

    /// <summary>The EC reports the GPU too, so a missing nvidia-smi no longer means an unwatched GPU.</summary>
    private static (double?, string?)? EcGpu(BladeController? ctl)
    {
        var t = ctl?.GetThermalReading();
        if (!t.HasValue) return null;
        return t.Value.Gpu.HasValue ? (t.Value.Gpu, "Razer EC") : (null, "GPU off (EC reports no reading)");
    }

    private void GpuFailed()
    {
        _gpuFailures++;
        if (_gpuFailures >= 2)
        {
            _gpuRetryAt = DateTime.Now.AddMinutes(2);
            Log.Warn("GPU temperature unavailable twice in a row; retrying in 2 minutes");
        }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { }
        lock (_gate) { _ctl?.Dispose(); _ctl = null; }
    }
}
