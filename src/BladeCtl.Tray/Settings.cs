using System.Text.Json;

namespace BladeCtl.Tray;

/// <summary>Saved lighting for one accessory (keyed by product id, e.g. "0266").</summary>
public sealed class AccessoryLighting
{
    public string Effect { get; set; } = "Static";   // Off | Static | Spectrum | Breathing | Wave | Reactive | Starlight
    public int R { get; set; } = 255;
    public int G { get; set; } = 255;
    public int B { get; set; } = 255;
    public int Brightness { get; set; } = 255;
    /// <summary>Re-send at logon. Off by default for devices with onboard memory; they keep it themselves.</summary>
    public bool ApplyAtLogon { get; set; }
    public string? LastAppliedUtc { get; set; }
    /// <summary>Wave direction 1 left-to-right, 2 right-to-left; Reactive speed 1-4; Starlight speed 1-3.</summary>
    public int Direction { get; set; } = 1;
    public int Speed { get; set; } = 2;
    /// <summary>Keyboard extras (v5). Null = never set from BladeCtl; the device keeps whatever it has.</summary>
    public bool? GameMode { get; set; }
    public int? PollingHz { get; set; }
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Persisted user state at %APPDATA%\BladeCtl\settings.json (or the path given with --settings).
///
/// Schema v2 split the old single "Mode" field into orthogonal PowerMode + FanMode.
/// Schema v3 (2026-09-05): Synapse guard switches, ShowWindowAtStartup off.
/// Schema v4 (2026-09-05): accessory lighting, boot-lighting task, Blade device-mode experiment.
/// Schema v5 (2026-09-05): Creator/Custom power modes with CPU/GPU boost, lid logo, more effects,
///   lights-off on lock/idle, keyboard game mode + polling.
/// </summary>
public sealed class Settings
{
    public const int CurrentSchema = 6;

    public int SchemaVersion { get; set; } = CurrentSchema;

    // v1 leftover, read only for migration. Never written by v2+.
    public string? Mode { get; set; }

    public string PowerMode { get; set; } = "Balanced";   // Balanced | Gaming | Creator | Custom
    public string FanMode { get; set; } = "Auto";         // Auto | Manual | Target
    public int ManualRpm { get; set; } = 4000;
    public string? ManualExpiresUtc { get; set; }          // ISO-8601, or null = until changed

    public string Rgb { get; set; } = "Spectrum";         // Off | Static | Spectrum | Breathing | Wave | Reactive | Starlight
    public int ColorR { get; set; } = 92;
    public int ColorG { get; set; } = 200;
    public int ColorB { get; set; } = 214;
    public int Brightness { get; set; } = 200;

    // ---- v5 ----

    /// <summary>Custom power mode boost levels: CPU 0 Low, 1 Medium, 2 High, 3 Boost; GPU 0 Low, 1 Medium, 2 High.</summary>
    public int CpuBoost { get; set; } = 1;
    public int GpuBoost { get; set; } = 1;

    /// <summary>Lid logo: Off | On | Blink. Applied with the rest of the saved state.</summary>
    public string Logo { get; set; } = "Off";

    /// <summary>Wave direction (1 left-to-right, 2 right-to-left), Reactive speed 1-4, Starlight speed 1-3.</summary>
    public int RgbDirection { get; set; } = 1;
    public int RgbSpeed { get; set; } = 2;

    /// <summary>Turn every light off while the session is locked and bring it back on unlock.</summary>
    public bool LightsOffWhenLocked { get; set; } = true;

    /// <summary>Turn every light off after this many minutes without input; 0 = never.</summary>
    public int IdleOffMinutes { get; set; } = 0;

    // ---- v6: Huntsman analog engine ----
    public AnalogSettings Analog { get; set; } = new();

    public bool ApplyOnStartup { get; set; } = true;
    public bool ShowWindowAtStartup { get; set; } = false;
    public bool FirstRunComplete { get; set; }
    public bool CoexistenceNoticeShown { get; set; }
    public bool MinimiseNoticeShown { get; set; }
    public bool VerboseLog { get; set; }
    public bool KeepManualFanOnBattery { get; set; }

    /// <summary>Strip Synapse's Run-key entry at every start and keep it stripped. Default on.</summary>
    public bool BlockSynapseAutostart { get; set; } = true;

    /// <summary>Also close a Synapse that is already running when BladeCtl starts. Opt-in.</summary>
    public bool CloseSynapseAtStartup { get; set; }

    /// <summary>Wall-clock of the last successful startup apply, for the session banner.</summary>
    public string? LastStartupApplyUtc { get; set; }

    // ---- v4 ----

    /// <summary>SYSTEM task at boot that applies the saved lighting before anyone signs in.</summary>
    public bool BootLightingTask { get; set; } = true;

    /// <summary>
    /// Put the Blade's keyboard controller in NORMAL mode (0x00) before lighting commands instead of
    /// leaving it in Synapse's DRIVER mode (0x03). Set true once the 2026-09-05 colour test shows the
    /// mode matters for static colour.
    /// </summary>
    public bool BladeNormalMode { get; set; }

    /// <summary>Saved lighting per accessory, keyed by 4-hex-digit product id.</summary>
    public Dictionary<string, AccessoryLighting> Accessories { get; set; } = new();

    // ---- fan target loop (FanMode == "Target") ----
    public int TargetTempC { get; set; } = 75;
    public string TargetSensor { get; set; } = "Hottest";   // Hottest | CPU | GPU

    // ---- battery profile (2.6.0: run cool on battery) ----
    // On battery: Custom power mode at the levels below and the built-in panel at BatteryRefreshHz.
    // PowerMode/CpuBoost/GpuBoost above are never overwritten by it: plugging in (main charger or USB-C)
    // re-applies them. The keyboard backlight is deliberately left alone.

    /// <summary>Switch to the battery profile automatically when unplugged. Default on.</summary>
    public bool BatteryProfile { get; set; } = true;
    public int BatteryCpuBoost { get; set; } = 0;   // 0 Low
    public int BatteryGpuBoost { get; set; } = 0;   // 0 Low
    /// <summary>Built-in panel refresh on battery (the panel offers 60 and 360 Hz). 0 = leave the refresh rate alone.</summary>
    public int BatteryRefreshHz { get; set; } = 60;
    /// <summary>Runtime state, persisted so a restart on battery / after plugging in still ends up right.</summary>
    public bool BatteryProfileEngaged { get; set; }
    /// <summary>The built-in panel's refresh before BladeCtl lowered it for battery; 0 = BladeCtl has not changed it.</summary>
    public int PanelAcRefreshHz { get; set; }
    /// <summary>
    /// Windows services stopped while on battery and started again on AC (2.6.1). Measured 2026-10-03 on battery:
    /// NVIDIA's background services kept the RTX 3070 awake with no app using it (43.7 W); stopped, it slept (22.8 W).
    /// Needs the elevated logon instance; an unelevated BladeCtl only logs that it could not.
    /// </summary>
    public List<string> BatteryStopServices { get; set; } = new() { "NVDisplay.ContainerLocalSystem" };
    /// <summary>Runtime state: services BladeCtl stopped for battery, so they are restarted even after a crash or restart.</summary>
    public List<string> BatteryServicesStopped { get; set; } = new();

    // ---- paths ----

    private static string? _overridePath;
    /// <summary>Use a specific settings file (the SYSTEM boot task passes the user's).</summary>
    public static void OverridePath(string path) => _overridePath = path;

    private static string Dir => _overridePath != null ? (Path.GetDirectoryName(_overridePath) ?? Log.AppDataDir) : Log.AppDataDir;
    public static string FilePath => _overridePath ?? Path.Combine(Log.AppDataDir, "settings.json");

    /// <summary>True when no settings file existed at load time (a genuine first run).</summary>
    public bool WasMissingOnLoad { get; private set; }

    /// <summary>Set when the file existed but could not be parsed; surfaced in the UI.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Human summary for the log.</summary>
    public string Summary =>
        $"power={PowerMode} fan={FanMode} rpm={ManualRpm} rgb={Rgb} colour=#{ColorR:X2}{ColorG:X2}{ColorB:X2} bright={Brightness} " +
        $"applyOnStartup={ApplyOnStartup} showWindow={ShowWindowAtStartup} blockSynapse={BlockSynapseAutostart} closeSynapse={CloseSynapseAtStartup} " +
        $"bootTask={BootLightingTask} bladeNormalMode={BladeNormalMode} accessories={Accessories.Count} target={TargetTempC}C/{TargetSensor} " +
        $"boost={CpuBoost}/{GpuBoost} logo={Logo} lockOff={LightsOffWhenLocked} idleOff={IdleOffMinutes}m " +
        $"battery={(BatteryProfile ? $"on(CPU {BatteryCpuBoost}/GPU {BatteryGpuBoost}, {BatteryRefreshHz} Hz){(BatteryProfileEngaged ? " ENGAGED" : "")}" : "off")} " +
        $"analog={(Analog?.Enabled == true ? $"ON make {Analog.MakeMm:F1} reset {Analog.BreakMm:F1}{(Analog.RapidTrigger ? $" rt {Analog.RapidTriggerMm:F1}" : "")}" : "off")} verbose={VerboseLog}";

    public AccessoryLighting AccessoryFor(ushort pid)
    {
        string key = pid.ToString("X4");
        if (!Accessories.TryGetValue(key, out var l)) { l = new AccessoryLighting(); Accessories[key] = l; }
        return l;
    }

    public static Settings Load()
    {
        if (!File.Exists(FilePath))
            return new Settings { WasMissingOnLoad = true };

        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            if (s == null) throw new InvalidDataException("settings.json deserialized to null");
            s.Migrate();
            return s;
        }
        catch (Exception ex)
        {
            // Quarantine rather than silently discard, so the user can see what was lost.
            string quarantine = FilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(FilePath, quarantine); } catch { }
            Log.Error($"settings.json unreadable ({ex.GetType().Name}: {ex.Message}); kept as {Path.GetFileName(quarantine)}");
            return new Settings
            {
                WasMissingOnLoad = true,
                LoadProblem = $"Your settings file was unreadable and has been reset. The old file was kept as {Path.GetFileName(quarantine)}.",
            };
        }
    }

    private void Migrate()
    {
        if (SchemaVersion < 2)
        {
            switch (Mode)
            {
                case "Manual": FanMode = "Manual"; PowerMode = "Balanced"; break;
                case "Gaming": PowerMode = "Gaming"; FanMode = "Auto"; break;
                default: PowerMode = "Balanced"; FanMode = "Auto"; break;
            }
            Log.Write($"migrated settings v1 -> v2 (Mode='{Mode}' => PowerMode={PowerMode}, FanMode={FanMode})");
        }
        if (SchemaVersion < 3)
        {
            // The v2 default of showing the window at every logon is what made the app a nuisance.
            ShowWindowAtStartup = false;
            BlockSynapseAutostart = true;
            CloseSynapseAtStartup = false;
            // A manual floor saved weeks ago must not be re-armed by an upgrade; engage it again deliberately.
            string fanNote = "";
            if (FanMode == "Manual") { FanMode = "Auto"; fanNote = $", saved manual floor {ManualRpm} RPM cleared (engage it again on purpose)"; }
            Log.Write("migrated settings v2 -> v3: ShowWindowAtStartup=false, BlockSynapseAutostart=true" + fanNote);
        }
        if (SchemaVersion < 4)
        {
            BootLightingTask = true;
            Accessories ??= new();
            Log.Write("migrated settings v3 -> v4: accessory lighting + boot lighting task enabled");
        }
        if (SchemaVersion < 5)
        {
            CpuBoost = 1; GpuBoost = 1; Logo = "Off"; RgbDirection = 1; RgbSpeed = 2;
            LightsOffWhenLocked = true; IdleOffMinutes = 0;
            Log.Write("migrated settings v4 -> v5: boost levels, logo, effect options, lights-off on lock");
        }
        if (SchemaVersion < 6)
        {
            Analog ??= new AnalogSettings();
            Analog.Enabled = false;   // the engine is opt-in: typing depends on it while it runs
            Log.Write("migrated settings v5 -> v6: Huntsman analog engine (off)");
        }
        if (SchemaVersion != CurrentSchema)
        {
            SchemaVersion = CurrentSchema;
            Mode = null;
            Save();
        }
    }

    /// <summary>Atomic save. Returns false (and logs) on failure so callers can tell the user it won't persist.</summary>
    public bool Save(out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error($"FAILED to save settings: {error}");
            return false;
        }
    }

    public bool Save() => Save(out _);

    public BladeCtl.Core.PerfMode PerfModeValue => PowerMode switch
    {
        "Gaming" => BladeCtl.Core.PerfMode.Gaming,
        "Creator" => BladeCtl.Core.PerfMode.Creator,
        "Custom" => BladeCtl.Core.PerfMode.Custom,
        _ => BladeCtl.Core.PerfMode.Balanced,
    };

    public static string PowerModeName(BladeCtl.Core.PerfMode m) => m switch
    {
        BladeCtl.Core.PerfMode.Gaming => "Gaming",
        BladeCtl.Core.PerfMode.Creator => "Creator",
        BladeCtl.Core.PerfMode.Custom => "Custom",
        _ => "Balanced",
    };

    /// <summary>The mode the device should be in right now: Custom while the battery profile is engaged, else the saved mode.</summary>
    public BladeCtl.Core.PerfMode ExpectedPerfMode => BatteryProfileEngaged ? BladeCtl.Core.PerfMode.Custom : PerfModeValue;
    public string ExpectedPowerModeName => BatteryProfileEngaged ? "Custom (battery profile)" : PowerMode;

    public bool ManualFanRequested => FanMode == "Manual";
    public bool TargetFanRequested => FanMode == "Target";
    /// <summary>Either kind of software fan control is armed.</summary>
    public bool AnyFanControl => ManualFanRequested || TargetFanRequested;
}
