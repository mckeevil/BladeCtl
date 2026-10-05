using BladeCtl.Core;

namespace BladeCtl.Tray;

/// <summary>
/// Closed-loop fan control: hold a chosen sensor (CPU, GPU or the hotter of the two) at or below a
/// target temperature by moving the manual floor between 3500 and 5000 RPM.
///
/// Behaviour:
///  - over target by more than 1 °C: raise the floor, 200-600 RPM per step depending on how far over,
///    no more than one step every 8 s (thermal inertia; avoids hunting);
///  - under target by more than 3 °C: lower the floor 200 RPM every 15 s; once at 3500 and still
///    comfortably under for 60 s, hand the fans back to the firmware curve entirely;
///  - inside the band: hold;
///  - sensor unreadable for 30 s while engaged: restore firmware control and say so;
///  - the DeviceMonitor's 90 °C auto-revert and the battery revert still apply on top.
/// Every RPM write goes through CommandRunner, so it is verified by read-back and logged.
/// </summary>
public sealed class FanTargetController : IDisposable
{
    public const int MinTargetC = 55, MaxTargetC = 90;

    private readonly Settings _settings;
    private readonly DeviceMonitor _monitor;
    private readonly CommandRunner _runner;
    private readonly Action<string, bool> _notify;

    private int? _commandedRpm;
    private DateTime _lastChange = DateTime.MinValue;
    private DateTime _belowSince = DateTime.MinValue;
    private DateTime _sensorLostSince = DateTime.MinValue;
    private int _busy;

    public FanTargetController(Settings settings, DeviceMonitor monitor, CommandRunner runner, Action<string, bool> notify)
    {
        _settings = settings; _monitor = monitor; _runner = runner; _notify = notify;
        _monitor.Updated += OnSnapshot;
    }

    /// <summary>True when the saved fan mode is Target (the loop is armed, engaged or not).</summary>
    public bool Active => _settings.TargetFanRequested;

    /// <summary>The floor the loop currently has engaged, or null while the firmware curve is in charge.</summary>
    public int? CommandedRpm => _commandedRpm;

    /// <summary>Set by the context while on battery (unless the user keeps manual fan on battery).</summary>
    public bool Paused { get; set; }

    public string StatusLine { get; private set; } = "";

    /// <summary>Raised on a pool thread after every step.</summary>
    public event Action? Changed;

    public string SensorName => _settings.TargetSensor switch { "CPU" => "CPU", "GPU" => "GPU", _ => "hottest" };

    private double? SensorTemp(DeviceSnapshot s) => _settings.TargetSensor switch
    {
        "CPU" => s.CpuC,
        "GPU" => s.GpuC,
        _ => s.MaxTempC,
    };

    /// <summary>Arm the loop (persisted). It engages the fans only when the sensor goes over target.</summary>
    public void Start(int targetC, string sensor)
    {
        _settings.FanMode = "Target";
        _settings.TargetTempC = Math.Clamp(targetC, MinTargetC, MaxTargetC);
        _settings.TargetSensor = sensor;
        _settings.Save();
        _commandedRpm = null; _lastChange = DateTime.MinValue; _belowSince = DateTime.MinValue; _sensorLostSince = DateTime.MinValue;
        Log.Ok($"fan target armed: hold {SensorName} at {_settings.TargetTempC} °C");
        StatusLine = $"armed · waiting for {SensorName} to exceed {_settings.TargetTempC} °C";
        try { Changed?.Invoke(); } catch { }
        _monitor.PollNow();
    }

    /// <summary>Disarm (persisted) and give the fans back to the firmware.</summary>
    public async Task StopAsync(string why)
    {
        await DisengageAsync(why);
        if (_settings.FanMode == "Target") { _settings.FanMode = "Auto"; _settings.Save(); }
        Log.Write($"fan target disarmed ({why})");
        StatusLine = "";
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Restore firmware control without disarming (battery, sensor loss, exit).</summary>
    public async Task DisengageAsync(string why)
    {
        if (_commandedRpm == null) return;
        var r = await _runner.RestoreAutoAsync(keepTargetMode: true);
        Log.Write($"fan target: floor released ({why}) -> {r.Headline}");
        _commandedRpm = null;
        _lastChange = DateTime.Now;
    }

    /// <summary>Called by the thermal auto-revert: the monitor already restored the firmware curve.</summary>
    public void NoteExternalRevert()
    {
        _commandedRpm = null;
        _lastChange = DateTime.Now;
    }

    private async void OnSnapshot(DeviceSnapshot s)
    {
        if (!Active) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { await StepAsync(s); }
        catch (Exception ex) { Log.Error("fan target step: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task StepAsync(DeviceSnapshot s)
    {
        var now = DateTime.Now;
        int target = _settings.TargetTempC;

        if (!s.Connected)
        {
            _commandedRpm = null;
            StatusLine = "device not connected";
            try { Changed?.Invoke(); } catch { }
            return;
        }

        if (Paused)
        {
            if (_commandedRpm != null) await DisengageAsync("on battery");
            StatusLine = $"paused on battery · target {target} °C";
            try { Changed?.Invoke(); } catch { }
            return;
        }

        // The device is the truth: if something else put the fans back on auto, forget our floor.
        if (s.ManualFan == false && _commandedRpm != null)
        {
            Log.Write("fan target: device reports auto fan; the loop will re-engage from scratch");
            _commandedRpm = null;
        }

        double? t = SensorTemp(s);
        if (t == null)
        {
            if (_sensorLostSince == DateTime.MinValue) _sensorLostSince = now;
            if (_commandedRpm != null && now - _sensorLostSince > TimeSpan.FromSeconds(30))
            {
                Log.Warn($"fan target: {SensorName} sensor unreadable for 30 s while a floor was engaged; restoring firmware control");
                await DisengageAsync("sensor lost");
                _notify($"Fan target: the {SensorName} sensor stopped reading, so the fans are back on the firmware curve.", false);
            }
            StatusLine = $"{SensorName} sensor unavailable · target {target} °C";
            try { Changed?.Invoke(); } catch { }
            return;
        }
        _sensorLostSince = DateTime.MinValue;

        double err = t.Value - target;
        int? next = _commandedRpm;

        if (err > 1.0)
        {
            _belowSince = DateTime.MinValue;
            if (_commandedRpm == null || now - _lastChange >= TimeSpan.FromSeconds(8))
            {
                int step = (int)Math.Clamp(Math.Round(err) * 100, 200, 600);
                int cur = _commandedRpm ?? (BladeController.FanMinRpm - 100);
                next = Math.Clamp(RoundTo100(cur + step), BladeController.FanMinRpm, BladeController.FanMaxRpm);
            }
        }
        else if (err < -3.0)
        {
            if (_belowSince == DateTime.MinValue) _belowSince = now;
            if (_commandedRpm != null && now - _lastChange >= TimeSpan.FromSeconds(15))
            {
                if (_commandedRpm <= BladeController.FanMinRpm && now - _belowSince >= TimeSpan.FromSeconds(60)) next = null;
                else next = Math.Max(BladeController.FanMinRpm, _commandedRpm.Value - 200);
            }
        }

        if (next != _commandedRpm)
        {
            if (next == null)
            {
                await DisengageAsync($"{SensorName} {t:F0} °C, comfortably under {target} °C");
            }
            else
            {
                var r = await _runner.SetManualFanAsync(next.Value, saveFanMode: "Target");
                if (r.Ok)
                {
                    bool engaging = _commandedRpm == null;
                    _commandedRpm = next; _lastChange = now;
                    Log.Write($"fan target: {SensorName} {t:F0} °C vs {target} °C -> floor {next} RPM");
                    if (engaging) _notify($"Fan target: {SensorName} reached {t:F0} °C, holding {target} °C with a {next} RPM floor.", true);
                }
                else Log.Warn($"fan target: could not set {next} RPM: {r.Headline}");
            }
        }

        StatusLine = _commandedRpm is int c
            ? $"holding {target} °C · {SensorName} {t:F0} °C · floor {c} RPM"
            : $"armed · {SensorName} {t:F0} °C · firmware curve until {target} °C";
        try { Changed?.Invoke(); } catch { }
    }

    private static int RoundTo100(int v) => (int)Math.Round(v / 100.0) * 100;

    public void Dispose() => _monitor.Updated -= OnSnapshot;
}
