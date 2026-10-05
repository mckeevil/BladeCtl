using BladeCtl.Core;

namespace BladeCtl.Tray;

public enum Outcome
{
    Confirmed,          // write acked AND device read-back agrees
    AcceptedUnverified, // write acked; this command family has no read-back (lighting effects)
    AckedNotApplied,    // write said SUCCESS but the device disagrees  <- the Synapse fingerprint
    Partial,            // per-zone mixed result
    Rejected,           // write returned non-SUCCESS
    Disconnected,       // no device
}

public sealed record CommandResult(Outcome Outcome, string Message, string? Detail = null)
{
    /// <summary>Good enough to persist. AcceptedUnverified is honest about what it is, but not a failure.</summary>
    public bool Ok => Outcome is Outcome.Confirmed or Outcome.AcceptedUnverified;

    public string Headline => Outcome switch
    {
        Outcome.Confirmed => Message,
        Outcome.AcceptedUnverified => Message,
        Outcome.AckedNotApplied => "Device did not accept: " + Message,
        Outcome.Partial => "Partly applied: " + Message,
        Outcome.Rejected => "Failed: " + Message,
        _ => "No device: " + Message,
    };
}

/// <summary>
/// Runs a device command off the UI thread, then re-reads the device to find out what actually
/// happened. A SUCCESS status from the EC is NOT treated as proof — this hardware acks commands
/// it does not apply (proven: the battery-limit command acks and does nothing), and Synapse can
/// overwrite a setting immediately after we set it.
///
/// Settings are persisted only on Confirmed / AcceptedUnverified.
/// </summary>
public sealed class CommandRunner
{
    private readonly DeviceMonitor _monitor;
    private readonly Settings _settings;

    public CommandRunner(DeviceMonitor monitor, Settings settings)
    {
        _monitor = monitor;
        _settings = settings;
    }

    /// <summary>Fired after every command with the user-facing result (pool thread).</summary>
    public event Action<CommandResult>? Completed;

    /// <summary>The fan-target loop's currently engaged floor, so a power-mode change keeps it.</summary>
    public Func<int?>? ActiveLoopRpm { get; set; }

    private const int SettleMs = 250;

    private byte Cpu => (byte)Math.Clamp(_settings.CpuBoost, 0, 3);
    private byte Gpu => (byte)Math.Clamp(_settings.GpuBoost, 0, 2);

    public static string BoostName(int? level, bool cpu) => level switch
    {
        0 => "Low", 1 => "Medium", 2 => "High", 3 when cpu => "Boost", null => "?", _ => level.ToString()!,
    };

    /// <summary>Balanced, Gaming, Creator or Custom (Custom also writes the saved CPU/GPU boost levels, laptop-control style).</summary>
    public Task<CommandResult> SetPowerModeAsync(PerfMode mode) => RunAsync($"Power mode {mode}", ctl =>
    {
        int? loopRpm = ActiveLoopRpm?.Invoke();
        byte? cpu = mode == PerfMode.Custom ? Cpu : null, gpu = mode == PerfMode.Custom ? Gpu : null;
        var res = loopRpm is int lr
            ? ctl.SetManualFanVerbose(mode, lr, cpu, gpu)
            : _settings.ManualFanRequested
                ? ctl.SetManualFanVerbose(mode, _settings.ManualRpm, cpu, gpu)
                : ctl.SetModeVerbose(mode, cpu, gpu);
        Thread.Sleep(SettleMs);
        var state = ctl.GetPowerState(1);

        if (state?.Mode == mode)
        {
            _settings.PowerMode = Settings.PowerModeName(mode);
            string extra = "";
            if (mode == PerfMode.Custom)
            {
                var rc = ctl.GetCpuBoost(); var rg = ctl.GetGpuBoost();
                extra = $" · CPU {BoostName(rc, true)}, GPU {BoostName(rg, false)}" +
                        (rc == cpu && rg == gpu ? "" : " (boost levels did not read back as saved)");
            }
            return Persist(new CommandResult(Outcome.Confirmed, $"Power mode {mode} — confirmed by device{extra}"));
        }
        return Classify(res, state == null
            ? "could not read power mode back"
            : $"device still reports {state.Value.Mode}");
    });

    /// <summary>CPU 0-3, GPU 0-2. Takes effect in Custom mode; the registers hold the value in any mode.</summary>
    public Task<CommandResult> SetBoostAsync(int cpuLevel, int gpuLevel) => RunAsync($"Boost CPU {cpuLevel} GPU {gpuLevel}", ctl =>
    {
        byte cpu = (byte)Math.Clamp(cpuLevel, 0, 3), gpu = (byte)Math.Clamp(gpuLevel, 0, 2);
        bool a = ctl.SetCpuBoost(cpu), b = ctl.SetGpuBoost(gpu);
        Thread.Sleep(SettleMs);
        var rc = ctl.GetCpuBoost(); var rg = ctl.GetGpuBoost();
        if (rc == cpu && rg == gpu)
        {
            _settings.CpuBoost = cpu; _settings.GpuBoost = gpu;
            var mode = ctl.GetPowerState(1)?.Mode;
            string note = mode == PerfMode.Custom ? "" : $" (active once the power mode is Custom; device is in {mode?.ToString() ?? "?"})";
            return Persist(new CommandResult(Outcome.Confirmed, $"Boost CPU {BoostName(cpu, true)}, GPU {BoostName(gpu, false)} — confirmed by device{note}"));
        }
        return new CommandResult(a && b ? Outcome.AckedNotApplied : Outcome.Rejected,
            $"device reports CPU {BoostName(rc, true)}, GPU {BoostName(rg, false)}");
    });

    /// <summary>
    /// Battery profile (2.6.0): Custom mode at the given boost levels WITHOUT touching the saved power mode or
    /// boost levels, so plugging back in can re-apply exactly what the user chose. Fan handling mirrors
    /// SetPowerModeAsync: an engaged software floor is carried over, otherwise the firmware owns the fans.
    /// </summary>
    public Task<CommandResult> ApplyTransientCustomAsync(int cpuLevel, int gpuLevel) => RunAsync($"Battery profile (Custom CPU {cpuLevel} GPU {gpuLevel})", ctl =>
    {
        byte cpu = (byte)Math.Clamp(cpuLevel, 0, 3), gpu = (byte)Math.Clamp(gpuLevel, 0, 2);
        int? loopRpm = ActiveLoopRpm?.Invoke();
        var res = loopRpm is int lr
            ? ctl.SetManualFanVerbose(PerfMode.Custom, lr, cpu, gpu)
            : _settings.ManualFanRequested && _settings.KeepManualFanOnBattery
                ? ctl.SetManualFanVerbose(PerfMode.Custom, _settings.ManualRpm, cpu, gpu)
                : ctl.SetModeVerbose(PerfMode.Custom, cpu, gpu);
        Thread.Sleep(SettleMs);
        var state = ctl.GetPowerState(1);
        var rc = ctl.GetCpuBoost(); var rg = ctl.GetGpuBoost();
        if (state?.Mode == PerfMode.Custom && rc == cpu && rg == gpu)
            return new CommandResult(Outcome.Confirmed, $"Battery profile — Custom · CPU {BoostName(cpu, true)}, GPU {BoostName(gpu, false)} — confirmed by device");
        return Classify(res, state == null
            ? "could not read power mode back"
            : $"device reports {state.Value.Mode}, CPU {BoostName(rc, true)}, GPU {BoostName(rg, false)}");
    });

    /// <summary>Lid logo: Off, On or Blink. State and effect both read back.</summary>
    public Task<CommandResult> SetLogoAsync(string mode) => RunAsync($"Logo {mode}", ctl =>
    {
        byte m = mode switch { "On" => 1, "Blink" => 2, _ => 0 };
        bool ack = ctl.SetLogo(m);
        Thread.Sleep(SettleMs);
        var on = ctl.GetLogoLed(); var eff = ctl.GetLogoEffect();
        bool good = on == (m != 0) && (m != 2 || eff == 2) && (m != 1 || eff != 2);
        if (good)
        {
            _settings.Logo = m switch { 1 => "On", 2 => "Blink", _ => "Off" };
            return Persist(new CommandResult(Outcome.Confirmed, $"Lid logo {_settings.Logo} — confirmed by device"));
        }
        return new CommandResult(ack ? Outcome.AckedNotApplied : Outcome.Rejected,
            $"device reports logo {(on == true ? "on" : on == false ? "off" : "?")}, effect {eff?.ToString() ?? "?"}");
    });

    public Task<CommandResult> SetManualFanAsync(int rpm, string saveFanMode = "Manual") => RunAsync($"{(saveFanMode == "Target" ? "Fan target floor" : "Manual fan")} {rpm} RPM", ctl =>
    {
        rpm = BladeController.ClampRpm(rpm);
        var res = ctl.SetManualFanVerbose(_settings.PerfModeValue, rpm, _settings.PerfModeValue == PerfMode.Custom ? Cpu : null, _settings.PerfModeValue == PerfMode.Custom ? Gpu : null);
        Thread.Sleep(SettleMs);
        var state = ctl.GetPowerState(1);
        var r1 = ctl.GetFanRpm(1);
        var r2 = ctl.GetFanRpm(2);

        if (state?.ManualFan == true && r1 == rpm && r2 == rpm)
        {
            _settings.FanMode = saveFanMode;
            if (saveFanMode == "Manual") _settings.ManualRpm = rpm;
            return Persist(new CommandResult(Outcome.Confirmed,
                $"Manual fan floor {rpm} RPM active on both zones — confirmed"));
        }

        string detail = $"device reports manual={state?.ManualFan.ToString() ?? "?"} z1={r1?.ToString() ?? "?"} z2={r2?.ToString() ?? "?"}";
        return Classify(res, detail);
    });

    public Task<CommandResult> RestoreAutoAsync(bool keepTargetMode = false) => RunAsync("RESTORE AUTO FAN", ctl =>
    {
        string modeAfter = keepTargetMode && _settings.FanMode == "Target" ? "Target" : "Auto";
        var res = ctl.RestoreAutoFanVerbose(_settings.PerfModeValue);
        Thread.Sleep(SettleMs);
        var state = ctl.GetPowerState(1);

        if (state?.ManualFan == false)
        {
            _settings.FanMode = modeAfter;
            _settings.ManualExpiresUtc = null;
            return Persist(new CommandResult(Outcome.Confirmed,
                "Firmware fan control restored — device confirms auto"));
        }

        // Idempotent packets — one automatic retry before escalating.
        ctl.RestoreAutoFanVerbose(_settings.PerfModeValue);
        Thread.Sleep(SettleMs);
        state = ctl.GetPowerState(1);
        if (state?.ManualFan == false)
        {
            _settings.FanMode = modeAfter;
            _settings.ManualExpiresUtc = null;
            return Persist(new CommandResult(Outcome.Confirmed,
                "Firmware fan control restored on retry — device confirms auto"));
        }

        // FanMode deliberately left at Manual — do not record a restore that did not happen.
        return new CommandResult(Outcome.AckedNotApplied,
            "device still reports MANUAL fan after two restore attempts",
            res.Describe());
    });

    public Task<CommandResult> SetBrightnessAsync(int v) => RunAsync($"Brightness {v}", ctl =>
    {
        byte b = (byte)Math.Clamp(v, 0, 255);
        bool ack = ctl.SetBrightness(b);
        Thread.Sleep(SettleMs);
        var read = ctl.GetBrightness();

        if (read.HasValue && Math.Abs(read.Value - b) <= 2)
        {
            _settings.Brightness = b;
            return Persist(new CommandResult(Outcome.Confirmed, $"Brightness {b} ({b * 100 / 255}%) — confirmed by device"));
        }
        return new CommandResult(ack ? Outcome.AckedNotApplied : Outcome.Rejected,
            read.HasValue ? $"device reports brightness {read}" : "could not read brightness back");
    });

    /// <summary>
    /// Lighting effects are WRITE-ONLY in this protocol — there is no read-back for the active
    /// effect or colour, so the honest outcome is AcceptedUnverified, never Confirmed.
    /// </summary>
    public Task<CommandResult> SetLightingAsync(string effect, byte r, byte g, byte b) =>
        RunAsync($"Lighting {effect}", ctl =>
        {
            byte speed = (byte)Math.Clamp(_settings.RgbSpeed, 1, 4), dir = (byte)Math.Clamp(_settings.RgbDirection, 1, 2);
            bool ack = effect switch
            {
                "Spectrum" => ctl.SetSpectrum(),
                "Breathing" => ctl.SetBreathing(r, g, b),
                "Wave" => ctl.SetWave(dir),
                "Reactive" => ctl.SetReactive(speed, r, g, b),
                "Starlight" => ctl.SetStarlight((byte)Math.Min(speed, (byte)3), r, g, b),
                "Off" => ctl.SetLightsOff(),
                _ => ctl.SetStaticColor(r, g, b),
            };
            if (!ack) return new CommandResult(Outcome.Rejected, $"device rejected {effect}");

            _settings.Rgb = effect;
            _settings.ColorR = r; _settings.ColorG = g; _settings.ColorB = b;
            string opt = effect switch
            {
                "Wave" => dir == 2 ? " right-to-left" : " left-to-right",
                "Reactive" => $" speed {speed}",
                "Starlight" => $" speed {Math.Min(speed, (byte)3)}",
                _ => "",
            };
            string note = "accepted — effects cannot be read back from this device";
            return Persist(new CommandResult(Outcome.AcceptedUnverified, $"Lighting {effect}{opt} — {note}"));
        });

    // ---------- plumbing ----------

    private CommandResult Persist(CommandResult r)
    {
        if (!_settings.Save(out var err))
            return r with { Detail = $"applied, but settings could not be saved ({err}) — this will not survive a restart" };
        return r;
    }

    private static CommandResult Classify(BladeController.ApplyResult res, string detail)
    {
        if (res.AllOk) return new CommandResult(Outcome.AckedNotApplied, detail, res.Describe());
        bool any = res.Zones.Any(z => z.PowerOk);
        return new CommandResult(any ? Outcome.Partial : Outcome.Rejected, detail, res.Describe());
    }

    private async Task<CommandResult> RunAsync(string label, Func<BladeController, CommandResult> body)
    {
        var result = await Task.Run(() =>
        {
            var ctl = _monitor.Controller;
            if (ctl == null)
                return new CommandResult(Outcome.Disconnected, "no Razer Blade control device is connected");
            try { return body(ctl); }
            catch (Exception ex)
            {
                Log.Error($"{label} threw: {ex}");
                return new CommandResult(Outcome.Rejected, $"{ex.GetType().Name}: {ex.Message}");
            }
        }).ConfigureAwait(false);

        var syn = _monitor.Snapshot.Synapse;
        if (result.Outcome is Outcome.AckedNotApplied or Outcome.Partial && syn.SynapseRunning)
            result = result with { Detail = (result.Detail is null ? "" : result.Detail + " · ") +
                $"Razer Synapse is running ({syn.SynapseProcessCount} processes) and may have overridden this." };

        string line = $"{label} -> {result.Outcome}: {result.Message}{(result.Detail is null ? "" : " [" + result.Detail + "]")}";
        switch (result.Outcome)
        {
            case Outcome.Confirmed: Log.Ok(line); break;
            case Outcome.AcceptedUnverified: Log.Warn(line); break;
            default: Log.Error(line); break;
        }
        _monitor.PollNow();
        try { Completed?.Invoke(result); } catch { }
        return result;
    }
}
