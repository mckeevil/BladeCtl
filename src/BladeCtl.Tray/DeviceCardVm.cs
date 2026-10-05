using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using BladeCtl.Core;

namespace BladeCtl.Tray;

/// <summary>One accessory card in the Devices view. Renders device facts plus the saved lighting.</summary>
public sealed class DeviceCardVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly AccessoryLighting _saved;
    private readonly Func<DeviceCardVm, AccessoryLighting, Task> _apply;
    private readonly Action<DeviceCardVm> _pickColor;
    private readonly Func<DeviceCardVm, bool, Task>? _gameMode;
    private readonly Func<DeviceCardVm, int, Task>? _polling;
    private readonly AnalogSettings? _analog;
    private readonly Action? _analogChanged;
    private readonly Func<string>? _analogStatus;

    public RazerDeviceInfo Info { get; private set; }

    public DeviceCardVm(RazerDeviceInfo info, AccessoryLighting saved,
        Func<DeviceCardVm, AccessoryLighting, Task> apply, Action<DeviceCardVm> pickColor,
        Func<DeviceCardVm, bool, Task>? gameMode = null, Func<DeviceCardVm, int, Task>? polling = null,
        AnalogSettings? analog = null, Action? analogChanged = null, Func<string>? analogStatus = null)
    {
        Info = info; _saved = saved; _apply = apply; _pickColor = pickColor; _gameMode = gameMode; _polling = polling;
        _analog = analog; _analogChanged = analogChanged; _analogStatus = analogStatus;
        _effect = saved.Effect; _brightness = saved.Brightness; _applyAtLogon = saved.ApplyAtLogon;
        RefreshColor();
        SetEffectCommand = new RelayCommand(async p => { if (p is string e) { Effect = e; await Send(); } });
        PickColorCommand = new RelayCommand(() => _pickColor(this));
        ApplyCommand = new RelayCommand(async () => await Send());
        BlindCommand = new RelayCommand(async p => { if (p is string e) { Effect = e; await Send(); } });
        SetDirectionCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var d)) { Direction = d; if (Effect == "Wave") await Send(); } });
        SetSpeedCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var v)) { Speed = v; if (Effect is "Reactive" or "Starlight") await Send(); } });
        SetPollingCommand = new RelayCommand(async p => { if (p is string s && int.TryParse(s, out var hz) && _polling != null) await _polling(this, hz); });
    }

    public void Update(RazerDeviceInfo info) { Info = info; Raise(nameof(Info)); RaiseFacts(); }

    // ---------- keyboard extras ----------
    public bool IsKeyboard => Info.IsKeyboard && Supported;
    public bool GameMode
    {
        get => Info.GameMode == true;
        set { if (_gameMode != null && value != (Info.GameMode == true)) _ = _gameMode(this, value); Raise(); }
    }
    public string GameModeNote => Info.GameMode == null ? "not readable on this device" : Info.GameMode == true ? "Windows key disabled" : "Windows key active";
    public int? PollingHz => Info.PollingHz;
    public bool IsPoll125 => Info.PollingHz == 125;
    public bool IsPoll500 => Info.PollingHz == 500;
    public bool IsPoll1000 => Info.PollingHz == 1000;
    public string PollingNote => Info.PollingHz is int hz ? $"device reports {hz} Hz" : "polling not readable";
    public ICommand SetPollingCommand { get; }

    // ---------- analog engine (Huntsman V2 Analog) ----------
    public bool HasAnalog => _analog != null && Supported && Info.Pid == 0x0266;
    public bool AnalogEnabled { get => _analog?.Enabled == true; set { if (_analog == null || _analog.Enabled == value) return; _analog.Enabled = value; Raise(); _analogChanged?.Invoke(); } }
    public double MakeMm
    {
        get => _analog?.MakeMm ?? 1.5;
        set
        {
            if (_analog == null) return;
            _analog.MakeMm = Math.Round(Math.Clamp(value, 0.3, 3.8), 1);
            if (_analog.BreakMm > _analog.MakeMm - 0.1) _analog.BreakMm = Math.Round(_analog.MakeMm - 0.1, 1);
            Raise(); Raise(nameof(MakeText)); Raise(nameof(BreakMm)); Raise(nameof(BreakText)); _analogChanged?.Invoke();
        }
    }
    public string MakeText => $"{MakeMm:F1} mm";
    public double BreakMm
    {
        get => _analog?.BreakMm ?? 1.2;
        set
        {
            if (_analog == null) return;
            _analog.BreakMm = Math.Round(Math.Clamp(Math.Min(value, _analog.MakeMm - 0.1), 0.2, 3.7), 1);
            Raise(); Raise(nameof(BreakText)); _analogChanged?.Invoke();
        }
    }
    public string BreakText => $"{BreakMm:F1} mm";
    public bool RapidTrigger { get => _analog?.RapidTrigger == true; set { if (_analog == null) return; _analog.RapidTrigger = value; Raise(); _analogChanged?.Invoke(); } }
    public double RapidTriggerMm { get => _analog?.RapidTriggerMm ?? 0.3; set { if (_analog == null) return; _analog.RapidTriggerMm = Math.Round(Math.Clamp(value, 0.1, 1.5), 1); Raise(); Raise(nameof(RapidTriggerText)); _analogChanged?.Invoke(); } }
    public string RapidTriggerText => $"{RapidTriggerMm:F1} mm";
    public bool BlockWindowsKey { get => _analog?.BlockWindowsKey == true; set { if (_analog == null) return; _analog.BlockWindowsKey = value; Raise(); _analogChanged?.Invoke(); } }
    public string Overrides { get => _analog?.Overrides ?? ""; set { if (_analog == null) return; _analog.Overrides = value ?? ""; Raise(); _analogChanged?.Invoke(); } }
    public string AnalogStatus => _analogStatus?.Invoke() ?? "";

    // controller mode
    public bool Joystick { get => _analog?.Joystick == true; set { if (_analog == null) return; _analog.Joystick = value; Raise(); _analogChanged?.Invoke(); } }
    public string JoystickKeys { get => _analog?.JoystickKeys ?? "W A S D"; set { if (_analog == null) return; _analog.JoystickKeys = value ?? ""; Raise(); _analogChanged?.Invoke(); } }
    public double JoystickDeadzoneMm { get => _analog?.JoystickDeadzoneMm ?? 0.3; set { if (_analog == null) return; _analog.JoystickDeadzoneMm = Math.Round(Math.Clamp(value, 0.1, 1.5), 1); Raise(); Raise(nameof(JoystickDeadzoneText)); _analogChanged?.Invoke(); } }
    public string JoystickDeadzoneText => $"{JoystickDeadzoneMm:F1} mm";
    public double JoystickFullMm { get => _analog?.JoystickFullMm ?? 3.0; set { if (_analog == null) return; _analog.JoystickFullMm = Math.Round(Math.Clamp(value, 1.0, 3.9), 1); Raise(); Raise(nameof(JoystickFullText)); _analogChanged?.Invoke(); } }
    public string JoystickFullText => $"{JoystickFullMm:F1} mm";
    public bool JoystickKeysAlsoType { get => _analog?.JoystickKeysAlsoType == true; set { if (_analog == null) return; _analog.JoystickKeysAlsoType = value; Raise(); _analogChanged?.Invoke(); } }
    public string JoystickButtons { get => _analog?.JoystickButtons ?? ""; set { if (_analog == null) return; _analog.JoystickButtons = value ?? ""; Raise(); _analogChanged?.Invoke(); } }
    public string JoystickNote => "The four keys drive the left stick of a virtual Xbox 360 controller (deeper = further), and stop typing letters unless \"keys also type\" is on. The controller exists only while this is on. Buttons are optional: Space=A, Shift=LS, Ctrl=B, Q=LB, E=RB, R=X, F=Y, G=LT, V=RT, Tab=Back, Esc=Start.";

    // typo guard
    public bool Adaptive { get => _analog?.Adaptive == true; set { if (_analog == null) return; _analog.Adaptive = value; Raise(); _analogChanged?.Invoke(); } }
    public double AdaptivePercent { get => _analog?.AdaptivePercent ?? 30; set { if (_analog == null) return; _analog.AdaptivePercent = (int)Math.Clamp(Math.Round(value / 5) * 5, 5, 90); Raise(); Raise(nameof(AdaptiveText)); _analogChanged?.Invoke(); } }
    public string AdaptiveText => $"{(int)AdaptivePercent}%";
    public string AdaptiveNote => "Learns how deep you usually press each key (from presses that counted) and ignores presses that stay under this share of that depth: a brushed key at 5% of your normal travel does nothing. It only ever raises the actuation point above the slider, never lowers it.";
    public string AnalogNote => "Off: the keyboard's firmware types at its fixed ~1.6 mm. On: the keyboard goes into driver mode and streams every key's depth; BladeCtl decides the press from these depths and injects the keystroke, like Synapse did. The volume dial is translated too. A key the table does not know shows up in the status line as an id; a line like \"70 = MediaNext\" in the overrides box wires it (MediaPrev, MediaPlay, Mute, VolumeUp, VolumeDown also work). While it runs, typing on this keyboard depends on BladeCtl staying open; on the lock screen and UAC prompts it hands back to the firmware automatically.";
    public void RefreshAnalog() { Raise(nameof(AnalogStatus)); Raise(nameof(AnalogEnabled)); Raise(nameof(HasAnalog)); }

    // ---------- effect options ----------
    public int Direction { get => _saved.Direction; set { _saved.Direction = value; Raise(); Raise(nameof(IsDirLtr)); Raise(nameof(IsDirRtl)); } }
    public int Speed { get => _saved.Speed; set { _saved.Speed = value; Raise(); RaiseSpeed(); } }
    public bool IsDirLtr => _saved.Direction != 2;
    public bool IsDirRtl => _saved.Direction == 2;
    public bool IsSpeed1 => _saved.Speed <= 1;
    public bool IsSpeed2 => _saved.Speed == 2;
    public bool IsSpeed3 => _saved.Speed == 3;
    public bool IsSpeed4 => _saved.Speed >= 4;
    private void RaiseSpeed() { foreach (var n in new[] { nameof(IsSpeed1), nameof(IsSpeed2), nameof(IsSpeed3), nameof(IsSpeed4) }) Raise(n); }
    public bool ShowDirection => Effect == "Wave";
    public bool ShowSpeed => Effect is "Reactive" or "Starlight";
    public bool ShowSpeed4 => Effect == "Reactive";
    public ICommand SetDirectionCommand { get; }
    public ICommand SetSpeedCommand { get; }
    public string DeviceEffectText => Info.Model?.EffectReadback == false ? "" : Info.EffectId is byte id ? $"device reports {AccessoryService.EffectName(id)}" : "";

    // ---------- facts ----------
    public string Name => Info.Name;
    public string Kind => Info.Kind;
    public ushort Pid => Info.Pid;
    public string PidText => $"1532:{Info.Pid:X4}";
    public bool IsBlade => Info.IsBlade;
    public bool Supported => !Info.IsBlade && Info.Family == EffectFamily.ExtendedMatrix && Info.SpeaksProtocol;
    public bool Experimental => !Info.IsBlade && !Info.SpeaksProtocol && Info.CandidatePath != null;
    public bool InventoryOnly => !Supported && !Experimental && !Info.IsBlade;
    public bool ShowColour => Effect is "Static" or "Breathing" or "Reactive" or "Starlight";
    public string FactsLine
    {
        get
        {
            var bits = new List<string>();
            if (Info.Firmware != null) bits.Add("fw " + Info.Firmware);
            if (Info.Serial != null) bits.Add("serial " + Info.Serial);
            if (Info.DeviceMode is byte m) bits.Add(m == 3 ? "driver mode" : m == 0 ? "normal mode" : $"mode {m}");
            if (Info.Brightness is int b) bits.Add($"brightness {b}");
            if (Info.Model?.EffectReadback != false && Info.EffectId is byte e) bits.Add(AccessoryService.EffectName(e).ToLowerInvariant());
            if (Info.PollingHz is int hz) bits.Add($"{hz} Hz");
            if (Info.GameMode == true) bits.Add("game mode");
            if (bits.Count == 0) bits.Add(Info.Detail.Length > 0 ? Info.Detail : $"{Info.Collections} HID interfaces");
            return string.Join(" · ", bits);
        }
    }
    public string Notes => Info.Model?.Notes ?? "Not in the catalogue; enumerated generically.";
    public string StatusText => Info.IsBlade ? "Blade view" : Supported ? "Control ok" : Experimental ? "Silent device" : "Inventory only";
    public string StatusKind => Info.IsBlade ? "info" : Supported ? "ok" : Experimental ? "warn" : "info";
    public string MemoryNote => Info.Model?.OnboardMemory == true
        ? "Written to the device's own memory, so it stays that way with no software running."
        : "This device does not keep settings; re-apply at sign-in is on.";
    private void RaiseFacts()
    {
        foreach (var n in new[] { nameof(Name), nameof(FactsLine), nameof(StatusText), nameof(StatusKind), nameof(Supported), nameof(Experimental), nameof(InventoryOnly), nameof(Notes),
                                  nameof(IsKeyboard), nameof(GameMode), nameof(GameModeNote), nameof(PollingHz), nameof(IsPoll125), nameof(IsPoll500), nameof(IsPoll1000), nameof(PollingNote), nameof(DeviceEffectText) })
            Raise(n);
    }

    // ---------- lighting state ----------
    private string _effect; public string Effect { get => _effect; set { _effect = value; Raise(); foreach (var n in new[] { nameof(IsOff), nameof(IsStatic), nameof(IsSpectrum), nameof(IsBreathing), nameof(IsWave), nameof(IsReactive), nameof(IsStarlight), nameof(ShowColour), nameof(ShowDirection), nameof(ShowSpeed), nameof(ShowSpeed4) }) Raise(n); } }
    public bool IsOff => _effect == "Off";
    public bool IsStatic => _effect == "Static";
    public bool IsSpectrum => _effect == "Spectrum";
    public bool IsBreathing => _effect == "Breathing";
    public bool IsWave => _effect == "Wave";
    public bool IsReactive => _effect == "Reactive";
    public bool IsStarlight => _effect == "Starlight";

    private double _brightness; public double Brightness { get => _brightness; set { _brightness = value; Raise(); Raise(nameof(BrightnessText)); } }
    public string BrightnessText => $"{(int)Brightness} · {(int)Brightness * 100 / 255}%";
    public bool BrightnessDragging { get; set; }

    private bool _applyAtLogon; public bool ApplyAtLogon { get => _applyAtLogon; set { _applyAtLogon = value; _saved.ApplyAtLogon = value; Raise(); } }

    private Brush _colorBrush = Brushes.White; public Brush ColorBrush { get => _colorBrush; private set { _colorBrush = value; Raise(); } }
    public string ColorHex => _saved.Hex;
    public void SetColor(byte r, byte g, byte b) { _saved.R = r; _saved.G = g; _saved.B = b; RefreshColor(); if (!ShowColour) Effect = "Static"; }
    private void RefreshColor()
    {
        var br = new SolidColorBrush(Color.FromRgb((byte)_saved.R, (byte)_saved.G, (byte)_saved.B)); br.Freeze();
        ColorBrush = br; Raise(nameof(ColorHex));
    }

    private string _lastResult = ""; public string LastResult { get => _lastResult; set { _lastResult = value; Raise(); } }
    private string _lastKind = "info"; public string LastKind { get => _lastKind; set { _lastKind = value; Raise(); } }

    public ICommand SetEffectCommand { get; }
    public ICommand PickColorCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand BlindCommand { get; }

    public AccessoryLighting Snapshot() => new()
    {
        Effect = Effect, R = _saved.R, G = _saved.G, B = _saved.B,
        Brightness = (int)Math.Round(Brightness), ApplyAtLogon = ApplyAtLogon,
        Direction = _saved.Direction, Speed = _saved.Speed, GameMode = _saved.GameMode, PollingHz = _saved.PollingHz,
    };

    /// <summary>Called after "Match the Blade" so the card shows what was just sent.</summary>
    public void Adopt(AccessoryLighting l)
    {
        _saved.R = l.R; _saved.G = l.G; _saved.B = l.B; RefreshColor();
        Effect = l.Effect; Brightness = l.Brightness;
    }

    public Task Send() => _apply(this, Snapshot());
}
