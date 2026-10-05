using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BladeCtl.Core;
using BladeCtl.Tray.Power;

namespace BladeCtl.Tray;

public enum DialogKind { Info, Warn, Bad }
public enum DialogBullet { Ok, Info, Warn }
public enum DialogButton { Primary, Quiet, Ghost, Danger }

public sealed record DialogSpec(
    string Title,
    string Body,
    IReadOnlyList<(DialogBullet Kind, string Text)> Bullets,
    IReadOnlyList<(string Text, DialogButton Kind)> Buttons,
    DialogKind Kind = DialogKind.Warn);

public sealed record ActivityItem(string Time, string Message, string Kind);
public sealed record BulletItem(string Text, string Kind);
public sealed record SwatchItem(string Hex, Brush Brush, ICommand Pick);

public sealed class ButtonItem
{
    public ButtonItem(string text, string kind, ICommand command) { Text = text; Kind = kind; Command = command; }
    public string Text { get; }
    public string Kind { get; }
    public ICommand Command { get; }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _run(p);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

/// <summary>
/// Everything the window shows. The top half renders from DeviceSnapshot (what the hardware
/// reports); saved intent sits beside it so drift is visible instead of invisible.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Settings _settings;
    private readonly DeviceMonitor _monitor;
    private readonly CommandRunner _runner;
    private readonly BladeCtlContext _ctx;
    private readonly AccessoryService _accessories;
    private readonly FanTargetController _fanTarget;
    private readonly Dispatcher _disp;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(n); return true;
    }

    public MainViewModel(Settings settings, DeviceMonitor monitor, CommandRunner runner, BladeCtlContext ctx, AccessoryService accessories, FanTargetController fanTarget, PowerSampler power)
    {
        _settings = settings; _monitor = monitor; _runner = runner; _ctx = ctx; _accessories = accessories; _fanTarget = fanTarget;
        _disp = Dispatcher.CurrentDispatcher;
        _powerSampler = power;

        _targetTempC = Math.Clamp(_settings.TargetTempC, FanTargetController.MinTargetC, FanTargetController.MaxTargetC);
        _targetSensor = _settings.TargetSensor;
        FanTargetCommand = new RelayCommand(() => { _targetPending = true; _fanPending = false; RaiseFanSelection(); });
        SetTargetSensorCommand = new RelayCommand(p => { if (p is string s) { TargetSensor = s; if (_fanTarget.Active) _fanTarget.Start((int)Math.Round(TargetTempC), s); } });
        EngageTargetCommand = new RelayCommand(async () => await EngageTargetAsync());
        DisarmTargetCommand = new RelayCommand(async () => { _targetPending = false; SetStatus("Disarming fan target…", "info"); await _fanTarget.StopAsync("user"); RaiseFanSelection(); });
        _fanTarget.Changed += () => _disp.BeginInvoke(() => { TargetStatus = _fanTarget.StatusLine; Raise(nameof(TargetArmed)); Raise(nameof(GaugeFloor)); RaiseFanSelection(); });

        ShowBladeViewCommand = new RelayCommand(() => ViewIndex = 0);
        ShowDevicesViewCommand = new RelayCommand(() => ViewIndex = 1);
        RefreshDevicesCommand = new RelayCommand(async () => { SetStatus("Re-scanning Razer devices…", "info"); await _accessories.RefreshAsync("manual refresh"); });
        _accessories.Changed += () => _disp.BeginInvoke(RebuildDeviceCards);
        _ctx.Analog.Changed += () => _disp.BeginInvoke(() => { foreach (var c in Devices) c.RefreshAnalog(); });
        _analogTick = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { if (IsDevicesView && _ctx.Analog.Running) foreach (var c in Devices) c.RefreshAnalog(); }, _disp);
        _analogTick.Start();
        _accessories.Applied += (info, o) => _disp.BeginInvoke(() =>
        {
            SetStatus(o.Message, o.Kind);
            var card = Devices.FirstOrDefault(c => c.Pid == info.Pid);
            if (card != null) { card.LastResult = o.Message; card.LastKind = o.Kind; }
        });

        RestoreAutoCommand = new RelayCommand(async () => { SetStatus("Restoring firmware fan control…", "info"); await _runner.RestoreAutoAsync(); });
        SetBalancedCommand = new RelayCommand(async () => await ApplyPowerAsync(PerfMode.Balanced));
        SetGamingCommand = new RelayCommand(async () => await ApplyPowerAsync(PerfMode.Gaming));
        SetCreatorCommand = new RelayCommand(async () => await ApplyPowerAsync(PerfMode.Creator));
        SetCustomCommand = new RelayCommand(async () => await ApplyPowerAsync(PerfMode.Custom));
        SetCpuBoostCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var v)) { SetStatus($"Setting CPU boost {CommandRunner.BoostName(v, true)}…", "info"); await _runner.SetBoostAsync(v, _settings.GpuBoost); RaiseBoost(); } });
        SetGpuBoostCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var v)) { SetStatus($"Setting GPU boost {CommandRunner.BoostName(v, false)}…", "info"); await _runner.SetBoostAsync(_settings.CpuBoost, v); RaiseBoost(); } });
        SetLogoCommand = new RelayCommand(async p => { if (p is string m) { SetStatus($"Setting lid logo {m}…", "info"); await _runner.SetLogoAsync(m); RaiseLogo(); } });
        SetRgbDirectionCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var d)) { _settings.RgbDirection = d; _settings.Save(); RaiseRgb(); if (_settings.Rgb == "Wave") await ApplyLightingAsync("Wave"); } });
        SetRgbSpeedCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var v)) { _settings.RgbSpeed = v; _settings.Save(); RaiseRgb(); if (_settings.Rgb is "Reactive" or "Starlight") await ApplyLightingAsync(_settings.Rgb); } });
        MatchBladeCommand = new RelayCommand(async () =>
        {
            SetStatus($"Sending the Blade's {_settings.Rgb} {ColorHex} at brightness {_settings.Brightness} to every accessory…", "info");
            var outs = await _accessories.ApplyToAllAsync(_settings.Rgb is "Off" or "Static" or "Spectrum" or "Breathing" or "Wave" or "Reactive" or "Starlight" ? _settings.Rgb : "Static",
                _settings.ColorR, _settings.ColorG, _settings.ColorB, _settings.Brightness);
            foreach (var card in Devices.Where(c => c.Supported)) card.Adopt(_settings.AccessoryFor(card.Pid));
            int okc = outs.Count(o => o.Kind == "ok"), bad = outs.Count(o => o.Kind == "bad");
            SetStatus($"Matched {outs.Count} accessories to the Blade: {okc} confirmed, {outs.Count - okc - bad} accepted, {bad} failed.", bad > 0 ? "warn" : "ok");
        });
        SetIdleOffCommand = new RelayCommand(p => { if (p is string s && int.TryParse(s, out var m)) IdleOffMinutes = m; });
        FanAutoCommand = new RelayCommand(async () =>
        {
            _fanPending = false; _targetPending = false; FanManualSelected = false;
            SetStatus("Restoring firmware fan control…", "info");
            if (_fanTarget.Active) await _fanTarget.StopAsync("user chose Automatic");
            await _runner.RestoreAutoAsync();
            RaiseFanSelection();
        });
        FanManualCommand = new RelayCommand(async () =>
        {
            _targetPending = false;
            if (_fanTarget.Active) await _fanTarget.StopAsync("user chose Manual floor");
            _fanPending = true; FanManualSelected = true;
        });
        ApplyFloorCommand = new RelayCommand(async () => await ApplyFloorAsync((int)Math.Round(FloorRpm / 100) * 100));
        PresetCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var rpm)) { _fanPending = true; FanManualSelected = true; FloorRpm = rpm; await ApplyFloorAsync(rpm); } });
        SetRgbCommand = new RelayCommand(async p => { if (p is string eff) await ApplyLightingAsync(eff); });
        PickColorCommand = new RelayCommand(() => ShowColorPicker("Keyboard colour", ApplyColorAsync));
        ReapplyCommand = new RelayCommand(async () => { SetStatus("Re-applying saved settings…", "info"); await _ctx.ApplySavedAsync("re-apply"); });
        RetryCommand = new RelayCommand(() => { SetStatus("Re-probing device…", "info"); _monitor.PollNow(); });
        OpenLogCommand = new RelayCommand(() => OpenPath(Log.FilePath));
        OpenFolderCommand = new RelayCommand(() => OpenPath(Log.AppDataDir));
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        OpenDynamicLightingCommand = new RelayCommand(SynapseDetector.OpenDynamicLightingSettings);
        RestartElevatedCommand = new RelayCommand(async () => await OfferElevatedRestartAsync("Restart BladeCtl as administrator?"));
        CloseSynapseNowCommand = new RelayCommand(() => { int n = RazerGuard.CloseSynapse("user clicked Close Synapse"); SetStatus(n > 0 ? $"Closed Razer Synapse ({n} processes)." : "Synapse was not running.", n > 0 ? "ok" : "info"); _monitor.PollNow(); });
        Power = new PowerVm(power, _disp, RunPowerButtonAsync, ResetLearnedPowerAsync);

        _floorRpm = Math.Clamp(_settings.ManualRpm, BladeController.FanMinRpm, BladeController.FanMaxRpm);
        _brightness = Math.Clamp(_settings.Brightness, 0, 255);
        _rgbEffect = _settings.Rgb;
        RefreshColor();
        RaiseRgb();

        foreach (var e in Log.Recent.TakeLast(ActivityRows).Reverse()) Activity.Add(ToActivity(e));
        Log.EntryAdded += e => _disp.BeginInvoke(() => AddActivity(e));
        _monitor.Updated += s => _disp.BeginInvoke(() => Render(s));
        _runner.Completed += r => _disp.BeginInvoke(() => OnCommand(r));

        Render(_monitor.Snapshot);
    }

    public const int ActivityRows = 6;
    private readonly DispatcherTimer _analogTick;

    // ---------- views ----------

    private int _viewIndex; public int ViewIndex { get => _viewIndex; set { if (Set(ref _viewIndex, value)) { Raise(nameof(IsBladeView)); Raise(nameof(IsDevicesView)); ViewChanged?.Invoke(); } } }
    /// <summary>2.7.0: raised after the view switches, so the Power card knows whether it is on screen.</summary>
    public event Action? ViewChanged;

    // ---------- 2.7.0 Power card ----------

    private readonly PowerSampler _powerSampler;
    public PowerVm Power { get; }

    /// <summary>The strip's one button (spec 11.6). Each goes through an existing, already-reviewed path and acts only when clicked.</summary>
    private async Task RunPowerButtonAsync(string? id)
    {
        switch (id)
        {
            case BladeCtl.Core.Power.PowerButtons.Fans: PresetCommand.Execute("5000"); break;
            case BladeCtl.Core.Power.PowerButtons.Gaming: SetGamingCommand.Execute(null); break;
            case BladeCtl.Core.Power.PowerButtons.FullPower:
                SetStatus("Full power until you plug in: restoring the saved Razer mode…", "info");
                await _ctx.FullPowerUntilPluggedInAsync();
                break;
        }
    }

    private async Task ResetLearnedPowerAsync()
    {
        int choice = await ShowDialogAsync(new DialogSpec("Reset learned power data?",
            "BladeCtl forgets the GPU limits, CPU references, charge curve and rest-of-laptop watts it has learned. Comparisons say \"learning\" again until it has new data. Settings are not touched.",
            Array.Empty<(DialogBullet, string)>(),
            new[] { ("Cancel", DialogButton.Ghost), ("Reset", DialogButton.Primary) }, DialogKind.Info));
        if (choice != 1) return;
        _powerSampler.ResetLearned();
        SetStatus("Learned power data reset.", "ok");
    }
    public bool IsBladeView => _viewIndex == 0;
    public bool IsDevicesView => _viewIndex == 1;
    public ICommand ShowBladeViewCommand { get; }
    public ICommand ShowDevicesViewCommand { get; }

    // ---------- accessories ----------

    public ObservableCollection<DeviceCardVm> Devices { get; } = new();
    public ICommand RefreshDevicesCommand { get; }
    private string _devicesSummary = "Scanning…"; public string DevicesSummary { get => _devicesSummary; private set => Set(ref _devicesSummary, value); }

    private void RebuildDeviceCards()
    {
        var list = _accessories.Devices;
        foreach (var d in list)
        {
            var existing = Devices.FirstOrDefault(c => c.Pid == d.Pid);
            if (existing != null) existing.Update(d);
            else Devices.Add(new DeviceCardVm(d, _settings.AccessoryFor(d.Pid), ApplyDeviceAsync, PickDeviceColor,
                async (card, on) => { SetStatus($"{(on ? "Enabling" : "Disabling")} game mode on {card.Name}…", "info"); await _accessories.SetGameModeAsync(card.Pid, on); },
                async (card, hz) => { SetStatus($"Setting {card.Name} polling to {hz} Hz…", "info"); await _accessories.SetPollingAsync(card.Pid, hz); },
                d.Pid == 0x0266 ? _settings.Analog : null,
                () => _ctx.OnAnalogChanged(),
                () => _ctx.AnalogStatusLine));
        }
        foreach (var stale in Devices.Where(c => list.All(d => d.Pid != c.Pid)).ToList()) Devices.Remove(stale);
        int ctrl = list.Count(d => !d.IsBlade && d.SpeaksProtocol);
        DevicesSummary = $"{list.Count} Razer products · {ctrl} accessories controllable · scanned {_accessories.LastRefresh:HH:mm:ss}";
    }

    private async Task ApplyDeviceAsync(DeviceCardVm card, AccessoryLighting l)
    {
        SetStatus($"Sending {l.Effect} to {card.Name}…", "info");
        await _accessories.ApplyAsync(card.Pid, l);
    }

    private void PickDeviceColor(DeviceCardVm card) =>
        ShowColorPicker($"{card.Name} colour", async (r, g, b) => { card.SetColor(r, g, b); await card.Send(); });

    // ---------- commands ----------

    public ICommand RestoreAutoCommand { get; }
    public ICommand SetBalancedCommand { get; }
    public ICommand SetGamingCommand { get; }
    public ICommand SetCreatorCommand { get; }
    public ICommand SetCustomCommand { get; }
    public ICommand SetCpuBoostCommand { get; }
    public ICommand SetGpuBoostCommand { get; }
    public ICommand SetLogoCommand { get; }
    public ICommand SetRgbDirectionCommand { get; }
    public ICommand SetRgbSpeedCommand { get; }
    public ICommand MatchBladeCommand { get; }
    public ICommand SetIdleOffCommand { get; }

    // ---------- power: creator / custom / boost ----------

    private bool _isCreator; public bool IsCreator { get => _isCreator; private set { _isCreator = value; Raise(); } }
    private bool _isCustom; public bool IsCustom { get => _isCustom; private set { _isCustom = value; Raise(); Raise(nameof(ShowBoost)); } }
    public bool ShowBoost => IsCustom || _settings.PowerMode == "Custom";
    public bool IsCpu0 => _settings.CpuBoost == 0; public bool IsCpu1 => _settings.CpuBoost == 1; public bool IsCpu2 => _settings.CpuBoost == 2; public bool IsCpu3 => _settings.CpuBoost == 3;
    public bool IsGpu0 => _settings.GpuBoost == 0; public bool IsGpu1 => _settings.GpuBoost == 1; public bool IsGpu2 => _settings.GpuBoost == 2;
    private string _boostDeviceText = ""; public string BoostDeviceText { get => _boostDeviceText; private set => Set(ref _boostDeviceText, value); }
    private void RaiseBoost() { foreach (var n in new[] { nameof(IsCpu0), nameof(IsCpu1), nameof(IsCpu2), nameof(IsCpu3), nameof(IsGpu0), nameof(IsGpu1), nameof(IsGpu2), nameof(ShowBoost) }) Raise(n); }
    public string BoostNote => "Custom mode is Razer's fourth performance mode: the EC takes the CPU and GPU power ceilings from these two levels instead of a preset. Both read back from the device.";

    // ---------- lid logo ----------

    public bool IsLogoOff => _settings.Logo == "Off";
    public bool IsLogoOn => _settings.Logo == "On";
    public bool IsLogoBlink => _settings.Logo == "Blink";
    private string _logoDeviceText = ""; public string LogoDeviceText { get => _logoDeviceText; private set => Set(ref _logoDeviceText, value); }
    private void RaiseLogo() { Raise(nameof(IsLogoOff)); Raise(nameof(IsLogoOn)); Raise(nameof(IsLogoBlink)); }
    public ICommand FanAutoCommand { get; }
    public ICommand FanManualCommand { get; }
    public ICommand ApplyFloorCommand { get; }
    public ICommand PresetCommand { get; }
    public ICommand SetRgbCommand { get; }
    public ICommand PickColorCommand { get; }
    public ICommand ReapplyCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }
    public ICommand OpenDynamicLightingCommand { get; }
    public ICommand RestartElevatedCommand { get; }
    public ICommand CloseSynapseNowCommand { get; }

    // ---------- header / readouts ----------

    private bool _connected; public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    private string _deviceLine = "Looking for device…"; public string DeviceLine { get => _deviceLine; private set => Set(ref _deviceLine, value); }
    private string _notFoundReason = ""; public string NotFoundReason { get => _notFoundReason; private set => Set(ref _notFoundReason, value); }
    public bool Elevated { get; } = AutoStart.IsElevated;
    public string ElevatedText => Elevated ? "Elevated" : "Not elevated";
    public string ElevatedTip => Elevated
        ? "Running as administrator. Temperatures come from the laptop's own controller, which needs no special rights."
        : "Not running as administrator. Temperatures still work — they come from the laptop's controller, not from Windows. Click to restart elevated for fan and power control.";

    private string _cpuText = "--"; public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    private string _cpuNote = ""; public string CpuNote { get => _cpuNote; private set => Set(ref _cpuNote, value); }
    private string _gpuText = "--"; public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    private string _gpuNote = ""; public string GpuNote { get => _gpuNote; private set => Set(ref _gpuNote, value); }

    private string _modeText = "?"; public string ModeText { get => _modeText; private set => Set(ref _modeText, value); }
    private bool _isBalanced; public bool IsBalanced { get => _isBalanced; private set { _isBalanced = value; Raise(); } }
    private bool _isGaming; public bool IsGaming { get => _isGaming; private set { _isGaming = value; Raise(); } }
    public string ModeDeviceText => Connected ? $"device reports {ModeText}" : "";

    private bool _manualFan; public bool ManualFan { get => _manualFan; private set => Set(ref _manualFan, value); }
    private double _gaugeRpm; public double GaugeRpm { get => _gaugeRpm; private set => Set(ref _gaugeRpm, value); }
    private double _gaugeFloor; public double GaugeFloor { get => _gaugeFloor; private set => Set(ref _gaugeFloor, value); }
    private string _rpm1Text = "--"; public string Rpm1Text { get => _rpm1Text; private set => Set(ref _rpm1Text, value); }
    private string _rpm2Text = "--"; public string Rpm2Text { get => _rpm2Text; private set => Set(ref _rpm2Text, value); }
    private string _peakText = "--"; public string PeakText { get => _peakText; private set => Set(ref _peakText, value); }
    private string _fanSubText = ""; public string FanSubText { get => _fanSubText; private set => Set(ref _fanSubText, value); }
    private string _restoreHint = "Ctrl+Alt+F works anywhere"; public string RestoreHint { get => _restoreHint; private set => Set(ref _restoreHint, value); }

    // ---------- fan controls ----------

    private bool _fanPending;           // user picked "Manual floor" but has not applied yet
    private bool _targetPending;        // user picked "Hold temperature" but has not engaged yet
    private bool _fanManualSelected; public bool FanManualSelected { get => _fanManualSelected; private set { _fanManualSelected = value; RaiseFanSelection(); } }
    public bool FanTargetSelected => _settings.TargetFanRequested || _targetPending;
    public bool FanAutoSelected => !FanManualSelected && !FanTargetSelected;
    public bool FloorEnabled => FanManualSelected && !FanTargetSelected && Connected;
    public bool ManualBlockVisible => !FanTargetSelected;
    private void RaiseFanSelection()
    {
        foreach (var n in new[] { nameof(FanManualSelected), nameof(FanAutoSelected), nameof(FanTargetSelected), nameof(FloorEnabled), nameof(ManualBlockVisible), nameof(TargetArmed), nameof(TargetEnabled) })
            Raise(n);
    }

    // ---------- fan target loop ----------

    public ICommand FanTargetCommand { get; }
    public ICommand SetTargetSensorCommand { get; }
    public ICommand EngageTargetCommand { get; }
    public ICommand DisarmTargetCommand { get; }

    private double _targetTempC; public double TargetTempC { get => _targetTempC; set { if (Set(ref _targetTempC, value)) Raise(nameof(TargetTempText)); } }
    public string TargetTempText => $"{(int)Math.Round(TargetTempC)} °C";
    private string _targetSensor; public string TargetSensor { get => _targetSensor; set { _targetSensor = value; Raise(); Raise(nameof(IsSensorHottest)); Raise(nameof(IsSensorCpu)); Raise(nameof(IsSensorGpu)); } }
    public bool IsSensorHottest => _targetSensor == "Hottest";
    public bool IsSensorCpu => _targetSensor == "CPU";
    public bool IsSensorGpu => _targetSensor == "GPU";
    public bool TargetArmed => _fanTarget.Active;
    public bool TargetEnabled => FanTargetSelected && Connected;
    private string _targetStatus = ""; public string TargetStatus { get => _targetStatus; private set => Set(ref _targetStatus, value); }
    public string TargetNote => "Raises the floor 200–600 RPM every 8 s while the sensor is over target, eases it back every 15 s once it is 3 °C under, and hands the fans back to the firmware after a minute at 3500. Stops itself at 90 °C, on battery, or if the sensor stops reading.";

    private async Task EngageTargetAsync()
    {
        if (!Connected) return;
        int target = (int)Math.Round(TargetTempC);
        var snap = _monitor.Snapshot;
        bool sensorOk = TargetSensor switch { "CPU" => snap.CpuC.HasValue, "GPU" => snap.GpuC.HasValue, _ => snap.HasAnySensor };
        if (!sensorOk)
        {
            await ShowDialogAsync(new DialogSpec("That sensor is not readable",
                $"The {TargetSensor} sensor is not reading right now, so there is nothing to hold.",
                Array.Empty<(DialogBullet, string)>(), new[] { ("OK", DialogButton.Primary) }, DialogKind.Warn));
            return;
        }
        _targetPending = false;
        _fanTarget.Start(target, TargetSensor);
        SetStatus($"Fan target armed: holding {_fanTarget.SensorName} at {target} °C.", "ok");
        RaiseFanSelection();
    }

    private double _floorRpm; public double FloorRpm { get => _floorRpm; set { if (Set(ref _floorRpm, value)) Raise(nameof(FloorText)); } }
    public string FloorText => ((int)Math.Round(FloorRpm / 100) * 100).ToString();
    public bool FloorDragging { get; set; }
    public string FanNote => $"{BladeController.FanMinRpm}–{BladeController.FanMaxRpm} is Razer's own manual range. No fan-off command exists. " +
                             $"Auto-reverts to firmware at CPU {DeviceMonitor.AutoRevertCpuC:F0} °C or GPU {DeviceMonitor.AutoRevertC:F0} °C" +
                             $" (watching {_monitor.SensorSummary()}). This CPU idles near 90 °C, which is why its limit is higher.";

    // ---------- lighting ----------

    private string _rgbEffect; public string RgbEffect { get => _rgbEffect; private set { _rgbEffect = value; RaiseRgb(); } }
    public bool IsRgbOff => _rgbEffect == "Off";
    public bool IsRgbStatic => _rgbEffect == "Static";
    public bool IsRgbSpectrum => _rgbEffect == "Spectrum";
    public bool IsRgbBreathing => _rgbEffect == "Breathing";
    public bool IsRgbWave => _rgbEffect == "Wave";
    public bool IsRgbReactive => _rgbEffect == "Reactive";
    public bool IsRgbStarlight => _rgbEffect == "Starlight";
    public bool ShowRgbDirection => _rgbEffect == "Wave";
    public bool ShowRgbSpeed => _rgbEffect is "Reactive" or "Starlight";
    public bool ShowRgbSpeed4 => _rgbEffect == "Reactive";
    public bool IsDirLtr => _settings.RgbDirection != 2;
    public bool IsDirRtl => _settings.RgbDirection == 2;
    public bool IsSpeed1 => _settings.RgbSpeed <= 1; public bool IsSpeed2 => _settings.RgbSpeed == 2; public bool IsSpeed3 => _settings.RgbSpeed == 3; public bool IsSpeed4 => _settings.RgbSpeed >= 4;
    private void RaiseRgb()
    {
        foreach (var n in new[] { nameof(RgbEffect), nameof(IsRgbOff), nameof(IsRgbStatic), nameof(IsRgbSpectrum), nameof(IsRgbBreathing), nameof(IsRgbWave), nameof(IsRgbReactive), nameof(IsRgbStarlight),
                                  nameof(ShowRgbDirection), nameof(ShowRgbSpeed), nameof(ShowRgbSpeed4), nameof(IsDirLtr), nameof(IsDirRtl), nameof(IsSpeed1), nameof(IsSpeed2), nameof(IsSpeed3), nameof(IsSpeed4) })
            Raise(n);
    }

    // ---------- lights off on lock / idle ----------

    public bool LightsOffWhenLocked { get => _settings.LightsOffWhenLocked; set { _settings.LightsOffWhenLocked = value; _settings.Save(); Log.Write($"setting LightsOffWhenLocked={value}"); Raise(); } }
    public int IdleOffMinutes { get => _settings.IdleOffMinutes; set { _settings.IdleOffMinutes = value; _settings.Save(); Log.Write($"setting IdleOffMinutes={value}"); Raise(); Raise(nameof(IsIdle0)); Raise(nameof(IsIdle5)); Raise(nameof(IsIdle15)); Raise(nameof(IsIdle30)); } }
    public bool IsIdle0 => _settings.IdleOffMinutes <= 0; public bool IsIdle5 => _settings.IdleOffMinutes == 5; public bool IsIdle15 => _settings.IdleOffMinutes == 15; public bool IsIdle30 => _settings.IdleOffMinutes >= 30;

    private Brush _colorBrush = Brushes.White; public Brush ColorBrush { get => _colorBrush; private set => Set(ref _colorBrush, value); }
    private string _colorHex = ""; public string ColorHex { get => _colorHex; private set => Set(ref _colorHex, value); }
    private void RefreshColor()
    {
        var c = Color.FromRgb((byte)_settings.ColorR, (byte)_settings.ColorG, (byte)_settings.ColorB);
        var b = new SolidColorBrush(c); b.Freeze();
        ColorBrush = b;
        ColorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private double _brightness; public double Brightness { get => _brightness; set { if (Set(ref _brightness, value)) Raise(nameof(BrightnessText)); } }
    public string BrightnessText => $"{(int)Brightness} · {(int)Brightness * 100 / 255}%";
    public bool BrightnessDragging { get; set; }
    public string LightingNote => "Effects are write-only on this firmware: the device accepts them but cannot report them back. Static and Spectrum are confirmed by eye with Synapse closed; Synapse running will fight them.";

    // ---------- startup toggles ----------

    public bool StartWithWindows
    {
        get => AutoStart.IsEnabled;
        set
        {
            if (value == AutoStart.IsEnabled) return;
            if (value && !AutoStart.IsElevated)
            {
                _ = OfferElevatedRestartAsync("Registering the startup task needs administrator rights.").ContinueWith(_ => _disp.BeginInvoke(() => Raise(nameof(StartWithWindows))));
                return;
            }
            if (AutoStart.SetEnabled(value, out var err))
                SetStatus(value ? "Autostart enabled: elevated logon task, no UAC prompt at logon." : "Autostart removed.", "ok");
            else
                SetStatus("Autostart change failed: " + err, "bad");
            Raise();
        }
    }
    public bool ApplyOnStartup { get => _settings.ApplyOnStartup; set { _settings.ApplyOnStartup = value; _settings.Save(); Log.Write($"setting ApplyOnStartup={value}"); Raise(); } }
    public bool ShowWindowAtStartup { get => _settings.ShowWindowAtStartup; set { _settings.ShowWindowAtStartup = value; _settings.Save(); Log.Write($"setting ShowWindowAtStartup={value}"); Raise(); } }
    public bool VerboseLog { get => _settings.VerboseLog; set { _settings.VerboseLog = value; Log.Verbose = value; _settings.Save(); Log.Write($"setting VerboseLog={value}"); Raise(); } }
    public bool BlockSynapseAutostart
    {
        get => _settings.BlockSynapseAutostart;
        set
        {
            _settings.BlockSynapseAutostart = value; _settings.Save();
            Log.Write($"setting BlockSynapseAutostart={value}");
            if (value) { RazerGuard.RemoveAutostart(); RazerGuard.StartWatching(TimeSpan.FromSeconds(30)); }
            else RazerGuard.StopWatching();
            Raise();
        }
    }
    public bool CloseSynapseAtStartup { get => _settings.CloseSynapseAtStartup; set { _settings.CloseSynapseAtStartup = value; _settings.Save(); Log.Write($"setting CloseSynapseAtStartup={value}"); Raise(); } }

    /// <summary>SYSTEM task at boot: lighting goes to the saved state while the sign-in screen is still up.</summary>
    public bool BootLightingTask
    {
        get => _settings.BootLightingTask && AutoStart.IsBootLightingEnabled;
        set
        {
            if (value && !AutoStart.IsElevated)
            {
                _ = OfferElevatedRestartAsync("Registering the boot lighting task needs administrator rights.").ContinueWith(_ => _disp.BeginInvoke(() => Raise(nameof(BootLightingTask))));
                return;
            }
            _settings.BootLightingTask = value; _settings.Save();
            if (AutoStart.SetBootLightingEnabled(value, Settings.FilePath, Log.Dir, out var err))
                SetStatus(value ? "Boot lighting task registered: lighting is applied before sign-in from now on." : "Boot lighting task removed.", "ok");
            else SetStatus("Boot lighting task change failed: " + err, "bad");
            Raise();
        }
    }

    /// <summary>Experiment: switch the Blade controller to normal mode before lighting writes.</summary>
    public bool BladeNormalMode { get => _settings.BladeNormalMode; set { _settings.BladeNormalMode = value; _settings.Save(); Log.Write($"setting BladeNormalMode={value}"); Raise(); } }

    // ---------- status chips ----------

    private string _synapseText = ""; public string SynapseText { get => _synapseText; private set => Set(ref _synapseText, value); }
    private string _synapseKind = "ok"; public string SynapseKind { get => _synapseKind; private set => Set(ref _synapseKind, value); }
    private bool _synapseRunning; public bool SynapseRunning { get => _synapseRunning; private set => Set(ref _synapseRunning, value); }
    public string LoggingText => Log.LastError == null
        ? $"Logging to {Log.Dir}"
        : $"LOG WRITE FAILING: {Log.LastError}";
    public string LoggingKind => Log.LastError == null ? "info" : "bad";

    private string? _driftText; public string? DriftText { get => _driftText; private set { if (Set(ref _driftText, value)) Raise(nameof(DriftVisible)); } }
    public bool DriftVisible => DriftText != null;

    private string _statusText = "Ready."; public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    private string _statusKind = "info"; public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public void SetStatus(string text, string kind) { StatusText = $"{text}   {DateTime.Now:HH:mm:ss}"; StatusKind = kind; }

    public ObservableCollection<ActivityItem> Activity { get; } = new();
    private static ActivityItem ToActivity(LogEntry e) => new(e.Time.ToString("HH:mm:ss"), e.Message, e.Level switch
    {
        LogLevel.Ok => "ok", LogLevel.Warn => "warn", LogLevel.Error => "bad", _ => "info",
    });
    private void AddActivity(LogEntry e)
    {
        Activity.Insert(0, ToActivity(e));
        while (Activity.Count > ActivityRows) Activity.RemoveAt(Activity.Count - 1);
        Raise(nameof(LoggingText)); Raise(nameof(LoggingKind));
    }

    // ---------- overlay (dialogs) ----------

    private bool _overlayVisible; public bool OverlayVisible { get => _overlayVisible; private set => Set(ref _overlayVisible, value); }
    private string _overlayTitle = ""; public string OverlayTitle { get => _overlayTitle; private set => Set(ref _overlayTitle, value); }
    private string _overlayBody = ""; public string OverlayBody { get => _overlayBody; private set => Set(ref _overlayBody, value); }
    private string _overlayKind = "warn"; public string OverlayKind { get => _overlayKind; private set => Set(ref _overlayKind, value); }
    private bool _overlayIsColor; public bool OverlayIsColor { get => _overlayIsColor; private set => Set(ref _overlayIsColor, value); }
    public ObservableCollection<BulletItem> OverlayBullets { get; } = new();
    public ObservableCollection<ButtonItem> OverlayButtons { get; } = new();
    public ObservableCollection<SwatchItem> OverlaySwatches { get; } = new();
    public bool OverlayHasBullets => OverlayBullets.Count > 0;

    private TaskCompletionSource<int>? _dialogTcs;

    /// <summary>Show an in-window dialog. Returns the index of the pressed button.</summary>
    public Task<int> ShowDialogAsync(DialogSpec spec)
    {
        _dialogTcs?.TrySetResult(-1);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogTcs = tcs;

        OverlayTitle = spec.Title;
        OverlayBody = spec.Body;
        OverlayKind = spec.Kind.ToString().ToLowerInvariant();
        OverlayIsColor = false;
        OverlayBullets.Clear();
        foreach (var (k, t) in spec.Bullets) OverlayBullets.Add(new BulletItem(t, k.ToString().ToLowerInvariant()));
        Raise(nameof(OverlayHasBullets));
        OverlayButtons.Clear();
        for (int i = 0; i < spec.Buttons.Count; i++)
        {
            int idx = i;
            OverlayButtons.Add(new ButtonItem(spec.Buttons[i].Text, spec.Buttons[i].Kind.ToString().ToLowerInvariant(),
                new RelayCommand(() => CloseDialog(idx))));
        }
        OverlayVisible = true;
        return tcs.Task;
    }

    public void CloseDialog(int result)
    {
        OverlayVisible = false;
        var tcs = _dialogTcs; _dialogTcs = null;
        tcs?.TrySetResult(result);
    }

    private static readonly string[] SwatchHexes =
    {
        "#5CC8D6", "#5FC98A", "#E3A94A", "#E5605C", "#B57BEE", "#4F8DFF", "#FFFFFF", "#FF7A18", "#F5D90A", "#00FF66",
    };

    private void ShowColorPicker(string title, Func<byte, byte, byte, Task> onPick)
    {
        _dialogTcs?.TrySetResult(-1); _dialogTcs = null;
        OverlayTitle = title;
        OverlayBody = "Pick a colour for Static and Breathing. Devices cannot report their colour back, so the swatch shows what was sent.";
        OverlayKind = "info";
        OverlayBullets.Clear(); Raise(nameof(OverlayHasBullets));
        OverlaySwatches.Clear();
        foreach (var hex in SwatchHexes)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c); b.Freeze();
            OverlaySwatches.Add(new SwatchItem(hex, b, new RelayCommand(async () => { OverlayVisible = false; await onPick(c.R, c.G, c.B); })));
        }
        OverlayButtons.Clear();
        OverlayButtons.Add(new ButtonItem("Cancel", "ghost", new RelayCommand(() => OverlayVisible = false)));
        OverlayButtons.Add(new ButtonItem("More colours…", "quiet", new RelayCommand(async () =>
        {
            OverlayVisible = false;
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(_settings.ColorR, _settings.ColorG, _settings.ColorB) };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await onPick(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        })));
        OverlayIsColor = true;
        OverlayVisible = true;
    }

    // ---------- rendering ----------

    public void Render(DeviceSnapshot s)
    {
        Connected = s.Connected;
        if (s.Connected)
        {
            DeviceLine = $"Blade 15 Advanced · fw {s.Firmware ?? "?"}";
            NotFoundReason = "";
        }
        else
        {
            DeviceLine = "No device";
            NotFoundReason = s.NotFoundReason ?? "Device not found.";
        }

        var age = (DateTime.Now - s.TakenLocal).TotalSeconds;
        string ago = age < 15 ? $"{age:F0} s ago" : $"STALE · {age:F0} s ago";
        CpuText = s.CpuC.HasValue ? s.CpuC.Value.ToString("F0") : "--";
        CpuNote = s.CpuC.HasValue ? $"{s.CpuNote ?? "sensor"} · {ago}" : (s.CpuNote ?? "not watched");
        GpuText = s.GpuC.HasValue ? s.GpuC.Value.ToString("F0") : "--";
        GpuNote = s.GpuC.HasValue ? $"{s.GpuNote ?? "nvidia-smi"} · {ago}" : (s.GpuNote ?? "not watched");

        ModeText = s.Mode?.ToString() ?? "?";
        IsBalanced = s.Mode == PerfMode.Balanced;
        IsGaming = s.Mode == PerfMode.Gaming;
        IsCreator = s.Mode == PerfMode.Creator;
        IsCustom = s.Mode == PerfMode.Custom;
        Raise(nameof(ModeDeviceText));
        BoostDeviceText = s.Connected ? $"device reports CPU {CommandRunner.BoostName(s.CpuBoost, true)}, GPU {CommandRunner.BoostName(s.GpuBoost, false)}" : "";
        LogoDeviceText = s.Connected ? $"device reports {s.LogoText.ToLowerInvariant()}" : "";

        ManualFan = s.ManualFan == true;
        GaugeRpm = s.Rpm1 ?? 0;
        GaugeFloor = _fanTarget.CommandedRpm ?? (ManualFan ? (s.Rpm1 ?? 0) : 0);
        Rpm1Text = s.Rpm1?.ToString() ?? "--";
        Rpm2Text = s.Rpm2?.ToString() ?? "--";
        PeakText = _monitor.PeakSinceManual is double pk ? $"{pk:F0}°" : "--";
        FanSubText = !s.Connected ? "" : _fanTarget.Active ? _fanTarget.StatusLine : ManualFan ? $"floor {s.Rpm1?.ToString() ?? "?"} RPM on both zones" : "firmware curve";
        RestoreHint = ManualFan ? $"Ctrl+Alt+F works anywhere · auto-revert at {DeviceMonitor.AutoRevertC:F0} °C" : "Ctrl+Alt+F works anywhere";

        if (ManualFan && !_fanTarget.Active) _fanPending = false;
        if (!_fanPending) FanManualSelected = ManualFan && !_fanTarget.Active;
        if (ManualFan && !FloorDragging && s.Rpm1 is int r1 && r1 >= BladeController.FanMinRpm && r1 <= BladeController.FanMaxRpm)
            FloorRpm = r1;
        Raise(nameof(FloorEnabled));
        Raise(nameof(FanNote));

        if (s.Brightness.HasValue && !BrightnessDragging) Brightness = Math.Clamp(s.Brightness.Value, 0, 255);

        var syn = s.Synapse;
        SynapseRunning = syn.SynapseRunning;
        if (syn.SynapseRunning) { SynapseText = $"Synapse running · {syn.SynapseProcessCount} processes · may override"; SynapseKind = "warn"; }
        else if (syn.BackgroundServices) { SynapseText = "Synapse closed · Razer services idle"; SynapseKind = "info"; }
        else { SynapseText = "Synapse not running"; SynapseKind = "ok"; }
        Raise(nameof(LoggingText)); Raise(nameof(LoggingKind));

        DriftText = _monitor.CurrentDrift(s);
    }

    private void OnCommand(CommandResult r)
    {
        string kind = r.Outcome switch
        {
            Outcome.Confirmed => "ok",
            Outcome.AcceptedUnverified => "warn",
            _ => "bad",
        };
        SetStatus(r.Headline + (r.Detail is null ? "" : "  —  " + r.Detail), kind);
        if (r.Outcome != Outcome.Disconnected) _fanPending = false;
    }

    // ---------- actions ----------

    private async Task ApplyPowerAsync(PerfMode m)
    {
        SetStatus($"Setting power mode {m}…", "info");
        await _runner.SetPowerModeAsync(m);
    }

    private bool _warnedManual;
    private async Task ApplyFloorAsync(int rpm)
    {
        rpm = BladeController.ClampRpm(rpm);
        if (!_warnedManual)
        {
            var snap = _monitor.Snapshot;
            bool sensor = snap.HasAnySensor;
            var bullets = new List<(DialogBullet, string)>();
            if (sensor)
            {
                bullets.Add((DialogBullet.Ok, $"Watching {_monitor.SensorSummary()} · auto-revert at CPU {DeviceMonitor.AutoRevertCpuC:F0} °C or GPU {DeviceMonitor.AutoRevertC:F0} °C"));
                if (!snap.CpuC.HasValue) bullets.Add((DialogBullet.Warn, "CPU temperature is NOT reading right now, so a CPU-heavy load could run hot without tripping the auto-revert."));
            }
            else bullets.Add((DialogBullet.Warn, "No temperature sensor is readable right now, so BladeCtl CANNOT auto-revert on heat. Only the firmware failsafe protects you."));
            bullets.Add((DialogBullet.Ok, "RESTORE AUTO FAN or Ctrl+Alt+F reverts instantly"));
            bullets.Add((DialogBullet.Info, _settings.KeepManualFanOnBattery ? "Stays engaged on battery" : "Reverts to auto when you unplug"));

            int choice = await ShowDialogAsync(new DialogSpec($"Set a manual floor of {rpm} RPM?",
                $"The fans will not drop below {rpm} RPM until you restore automatic control. The firmware failsafe stays active and can still spin faster.",
                bullets,
                new[] { ("Cancel", DialogButton.Ghost), ($"Engage {rpm} RPM", DialogButton.Primary) },
                sensor ? DialogKind.Warn : DialogKind.Bad));
            if (choice != 1) { _fanPending = false; FanManualSelected = ManualFan; return; }
            _warnedManual = true;
        }
        SetStatus($"Applying manual fan floor {rpm} RPM…", "info");
        await _runner.SetManualFanAsync(rpm);
    }

    private async Task ApplyLightingAsync(string effect)
    {
        RgbEffect = effect;
        SetStatus($"Setting lighting {effect}…", "info");
        await _runner.SetLightingAsync(effect, (byte)_settings.ColorR, (byte)_settings.ColorG, (byte)_settings.ColorB);
        RgbEffect = _settings.Rgb;
    }

    private async Task ApplyColorAsync(byte r, byte g, byte b)
    {
        _settings.ColorR = r; _settings.ColorG = g; _settings.ColorB = b;
        RefreshColor();
        string eff = _settings.Rgb is "Static" or "Breathing" or "Reactive" or "Starlight" ? _settings.Rgb : "Static";
        await ApplyLightingAsync(eff);
    }

    public async Task CommitBrightnessAsync()
    {
        int v = (int)Math.Round(Brightness);
        if (Math.Abs(v - _settings.Brightness) <= 1 && _monitor.Snapshot.Brightness is int d && Math.Abs(d - v) <= 2) return;
        SetStatus($"Setting brightness {v}…", "info");
        await _runner.SetBrightnessAsync(v);
    }

    private async Task<bool> OfferElevatedRestartAsync(string why)
    {
        int choice = await ShowDialogAsync(new DialogSpec("Restart as administrator",
            why + " Running elevated also lets BladeCtl read CPU temperature, so the manual-fan thermal auto-revert watches the CPU as well as the GPU. The logon task already does this automatically.",
            Array.Empty<(DialogBullet, string)>(),
            new[] { ("Not now", DialogButton.Ghost), ("Restart elevated", DialogButton.Primary) }, DialogKind.Info));
        if (choice != 1) return false;
        _ctx.RestartElevated();
        return true;
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"could not open {path}: {ex.Message}"); }
    }

    private void CopyDiagnostics()
    {
        var s = _monitor.Snapshot;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"BladeCtl {Program.Version}  exe: {Environment.ProcessPath}  elevated: {Elevated}  uptime: {Log.UptimeSpan:hh\\:mm\\:ss}");
        sb.AppendLine($"connected: {s.Connected}  path: {s.DevicePath}  firmware: {s.Firmware}");
        if (!s.Connected) sb.AppendLine($"not-found reason: {s.NotFoundReason}");
        sb.AppendLine($"mode: {s.Mode}  manualFan: {s.ManualFan}  rpm1: {s.Rpm1}  rpm2: {s.Rpm2}  brightness: {s.Brightness}  boost: {s.CpuBoost}/{s.GpuBoost}  logo: {s.LogoText}");
        sb.AppendLine($"accessories: {string.Join(" | ", _accessories.Devices.Select(AccessoryService.Describe))}");
        sb.AppendLine($"cpu: {s.CpuC?.ToString("F0") ?? s.CpuNote}  gpu: {s.GpuC?.ToString("F0") ?? s.GpuNote}");
        sb.AppendLine($"synapse: {s.Synapse.Summary} [{string.Join(", ", s.Synapse.Names)}]  run-key: {string.Join(",", RazerGuard.PresentValues())}");
        sb.AppendLine($"saved: {_settings.Summary}");
        sb.AppendLine($"autostart: {(AutoStart.IsEnabled ? AutoStart.RegisteredTarget() : "not registered")}");
        sb.AppendLine($"log: {Log.FilePath}{(Log.DirNote is null ? "" : " FALLBACK " + Log.DirNote)}{(Log.LastError is null ? "" : "  LAST ERROR " + Log.LastError)}");
        foreach (var pl in _powerSampler.DumpLines()) sb.AppendLine(pl);
        sb.AppendLine("--- last 40 log lines ---");
        foreach (var l in Log.Tail(40)) sb.AppendLine(l);
        try { Clipboard.SetText(sb.ToString()); SetStatus("Diagnostics copied to clipboard.", "ok"); }
        catch (Exception ex) { SetStatus("Could not copy diagnostics: " + ex.Message, "bad"); }
    }
}
