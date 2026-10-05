namespace BladeCtl.Core;

public enum PerfMode : byte
{
    Balanced = 0,
    Gaming = 1,
    Creator = 2,
    Custom = 4,
}

public enum KbdEffect
{
    Off,
    Static,
    Spectrum,
    Breathing,
}

/// <summary>
/// High-level Blade control API with hard safety clamps.
///
/// Command sources (all verified for PID 0x0276, Blade 15 Advanced Mid 2021):
///  - fan / power mode: razer-laptop-control-no-dkms daemon/device.rs + librazerblade PacketFactory.cpp
///  - BHO (battery health optimizer): razer-laptop-control-no-dkms (0x07/0x12, 0x07/0x92)
///  - chroma static/effects/brightness: OpenRazer razerkbd_driver.c mapping for
///    USB_DEVICE_ID_RAZER_BLADE_15_ADV_MID_2021 (standard matrix 0x03/0x0A, transaction id 0xFF,
///    blade brightness 0x0E/0x04)
/// </summary>
public sealed class BladeController : IDisposable
{
    // THERMAL SAFETY: hard clamp for manual fan control.
    // razer-laptop-control-no-dkms laptops.json for pid 0276: "fan": [3500, 5000].
    // (librazerblade would allow 3100-5300; we take the conservative intersection.)
    // There is NO fan-off/0-RPM command in this protocol: RPM 0 simply returns
    // control to the EC's automatic fan curve, and the EC's own thermal
    // protection is never touched by any command we send.
    public const int FanMinRpm = 3500;
    public const int FanMaxRpm = 5000;

    private const byte TidSystem = 0x1F; // razer-laptop-control + librazerblade use this for everything
    private const byte TidChroma = 0xFF; // OpenRazer uses this for chroma cmds on this model

    private readonly BladeDevice _dev;
    private readonly Action<string>? _log;

    /// <summary>
    /// Consecutive failed transactions. The tray's DeviceMonitor uses this to decide the device
    /// has really gone away (undock/sleep) and to re-run Find(), instead of assuming a controller
    /// that was once valid stays valid forever.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    public BladeController(BladeDevice dev, Action<string>? log = null)
    {
        _dev = dev;
        _log = log;
    }

    private RazerPacket? Tx(RazerPacket p)
    {
        var r = _dev.Transact(p, _log);
        if (r == null) ConsecutiveFailures++;
        else ConsecutiveFailures = 0;
        return r;
    }

    // ---------- power / fan ----------

    /// <summary>Read power mode + manual-fan flag for a zone (1 = zone1, 2 = zone2).</summary>
    public (PerfMode Mode, bool ManualFan)? GetPowerState(byte zone = 1)
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x82, 0x04, 0x00, zone, 0x00, 0x00));
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        return ((PerfMode)r.Args[2], r.Args[3] != 0);
    }

    /// <summary>Read current fan RPM setpoint for a zone. 0 = automatic.</summary>
    /// <summary>
    /// The EC's own thermal sensors: "Get Thermal Reading" in Synapse's command table (class 0x0D, command
    /// 0x85, 80-byte payload). Layout is the same shape as the fan id list: args[0] = sensor count, then one
    /// degrees-Celsius byte per sensor, CPU first then GPU.
    ///
    /// Verified on this hardware 2026-09-07: args[2] tracked nvidia-smi to within 1 C across idle and load,
    /// and args[1] moved 89 -> 98 C under an all-core CPU load. This is the real CPU die temperature, and
    /// unlike the ACPI thermal zone it needs no administrator rights.
    /// </summary>
    public (double Cpu, double? Gpu)? GetThermalReading()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x85, 0x50));
        if (r?.Status != RazerPacket.StatusCode.Success || r.Args[0] < 2) return null;
        double cpu = r.Args[1], gpu = r.Args[2];
        // Guard against a not-yet-populated EC: 0 and 255 are both "no reading", not a temperature.
        // The two sensors are judged separately (2.6.0): a powered-off dGPU must not blind the CPU reading,
        // which the 97 C CPU auto-revert depends on.
        if (cpu is <= 0 or >= 125) return null;
        return (cpu, gpu is <= 0 or >= 125 ? null : gpu);
    }

    public int? GetFanRpm(byte zone = 1)
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x81, 0x03, 0x00, zone, 0x00));
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        return r.Args[2] * 100;
    }

    // ---------- 2.7.0 Power card: read-only GETs (command id bit 0x80 set; a unit test enforces it) ----------

    /// <summary>"Get Adapter Wattage Level": class 0x07, id 0x8C, data size 2. args[0] = adapter level, args[1] = recommended level.</summary>
    public static RazerPacket AdapterWattagePacket() => RazerPacket.Create(TidSystem, 0x07, 0x8C, 0x02);
    /// <summary>"Get Thermal Fan Current Speed": class 0x0D, id 0x88, ds 3, [00, zone, 00] -> args[2] x 100 = REAL rpm (0x81 is the setpoint).</summary>
    public static RazerPacket FanCurrentRpmPacket(byte zone) => RazerPacket.Create(TidSystem, 0x0D, 0x88, 0x03, 0x00, zone, 0x00);
    /// <summary>"Get Device External Power Supply Status": class 0x00, id 0xB7, ds 1. Logged only (settles whether it is an AC-present flag).</summary>
    public static RazerPacket ExternalPowerPacket() => RazerPacket.Create(TidSystem, 0x00, 0xB7, 0x01);

    /// <summary>Connected adapter level and recommended level, or null when the EC did not answer SUCCESS. Validate by value: this EC answers every class-0x07 GET.</summary>
    public (byte Level, byte Rec)? GetAdapterWattage()
    {
        var r = Tx(AdapterWattagePacket());
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        return (r.Args[0], r.Args[1]);
    }

    /// <summary>Real current fan speed for a zone (1 or 2), in RPM.</summary>
    public int? GetFanCurrentRpm(byte zone)
    {
        var r = Tx(FanCurrentRpmPacket(zone));
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        // Replies are matched on class and id only: a reply that names the OTHER fan zone (a late answer to the previous
        // read, or another process reading the EC) is not this zone's speed. A reply that echoes no zone is accepted.
        if (r.Args[1] is 1 or 2 && r.Args[1] != zone) return null;
        return r.Args[2] * 100;
    }

    /// <summary>args[0] of 0x00/0xB7 (1 connected, 0 removed, per Synapse's descriptor), or null.</summary>
    public byte? GetExternalPowerStatus()
    {
        var r = Tx(ExternalPowerPacket());
        return r?.Status == RazerPacket.StatusCode.Success ? r.Args[0] : null;
    }

    /// <summary>
    /// Set performance mode with automatic fan control (the safe default).
    /// Sequence per razer-laptop-control-no-dkms set_fan_rpm(0)/set_power_mode:
    /// for each zone: GET power (EC sync read), SET power [0, zone, mode, manual=0].
    /// </summary>
    public bool SetMode(PerfMode mode) => ApplyPowerAndFan(mode, manualRpm: null).AllOk;

    /// <summary>Per-zone detail for a power/fan apply, so a partial success is reportable as such.</summary>
    public sealed record ZoneAck(byte Zone, bool PowerOk, bool? RpmOk);

    public sealed record ApplyResult(bool AllOk, ZoneAck[] Zones)
    {
        public string Describe() => string.Join(", ", Zones.Select(z =>
            $"z{z.Zone}: power={(z.PowerOk ? "ok" : "FAIL")}" + (z.RpmOk.HasValue ? $" rpm={(z.RpmOk.Value ? "ok" : "FAIL")}" : "")));
    }

    public ApplyResult SetModeVerbose(PerfMode mode, byte? cpuBoost = null, byte? gpuBoost = null) => ApplyPowerAndFan(mode, null, cpuBoost, gpuBoost);
    public ApplyResult SetManualFanVerbose(PerfMode mode, int rpm, byte? cpuBoost = null, byte? gpuBoost = null) => ApplyPowerAndFan(mode, ClampRpm(rpm), cpuBoost, gpuBoost);
    public ApplyResult RestoreAutoFanVerbose(PerfMode mode, byte? cpuBoost = null, byte? gpuBoost = null) => ApplyPowerAndFan(mode, null, cpuBoost, gpuBoost);

    /// <summary>
    /// Set manual fan RPM (clamped to [3500, 5000]) with the given performance mode.
    /// Sequence per zone: GET power, SET power [0, zone, mode, manual=1], SET rpm [0, zone, rpm/100].
    /// </summary>
    public bool SetManualFan(PerfMode mode, int rpm) => ApplyPowerAndFan(mode, manualRpm: ClampRpm(rpm)).AllOk;

    /// <summary>RESTORE AUTO: hand fan control back to the EC firmware curve immediately.</summary>
    public bool RestoreAutoFan(PerfMode mode) => ApplyPowerAndFan(mode, manualRpm: null).AllOk;

    public static int ClampRpm(int rpm) => Math.Clamp(rpm, FanMinRpm, FanMaxRpm);

    /// <summary>Boost levels for Custom power mode (razer-laptop-control): CPU 0 Low, 1 Medium, 2 High, 3 Boost; GPU 0 Low, 1 Medium, 2 High.</summary>
    public byte? GetCpuBoost()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x87, 0x03, 0x00, 0x01, 0x00));
        return r?.Status == RazerPacket.StatusCode.Success ? r.Args[2] : null;
    }

    public byte? GetGpuBoost()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x87, 0x03, 0x00, 0x02, 0x00));
        return r?.Status == RazerPacket.StatusCode.Success ? r.Args[2] : null;
    }

    public bool SetCpuBoost(byte boost) =>
        Tx(RazerPacket.Create(TidSystem, 0x0d, 0x07, 0x03, 0x00, 0x01, (byte)Math.Clamp((int)boost, 0, 3)))?.Status == RazerPacket.StatusCode.Success;

    public bool SetGpuBoost(byte boost) =>
        Tx(RazerPacket.Create(TidSystem, 0x0d, 0x07, 0x03, 0x00, 0x02, (byte)Math.Clamp((int)boost, 0, 2)))?.Status == RazerPacket.StatusCode.Success;

    /// <summary>Custom power mode (4) with explicit boost levels; fan stays automatic (laptop-control does the same).</summary>
    public ApplyResult SetCustomModeVerbose(byte cpuBoost, byte gpuBoost) => ApplyPowerAndFan(PerfMode.Custom, null, cpuBoost, gpuBoost);

    private ApplyResult ApplyPowerAndFan(PerfMode mode, int? manualRpm, byte? cpuBoost = null, byte? gpuBoost = null)
    {
        // Modes: 0 Balanced, 1 Gaming, 2 Creator, 4 Custom (boost levels apply). Anything else -> Balanced.
        if (mode is not (PerfMode.Balanced or PerfMode.Gaming or PerfMode.Creator or PerfMode.Custom))
            mode = PerfMode.Balanced;

        var acks = new List<ZoneAck>(2);
        foreach (byte zone in new byte[] { 1, 2 })
        {
            GetPowerState(zone); // EC sync read before write, mirrors reference implementation

            byte manualFlag = (byte)(manualRpm.HasValue ? 1 : 0);
            var pw = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x02, 0x04, 0x00, zone, (byte)mode, manualFlag));
            bool powerOk = pw?.Status == RazerPacket.StatusCode.Success;

            // razer-laptop-control writes the boost levels between zone 1 and zone 2 in Custom mode.
            if (zone == 1 && mode == PerfMode.Custom)
            {
                GetCpuBoost(); if (cpuBoost.HasValue) SetCpuBoost(cpuBoost.Value);
                GetGpuBoost(); if (gpuBoost.HasValue) SetGpuBoost(gpuBoost.Value);
            }

            bool? rpmOk = null;
            if (manualRpm.HasValue)
            {
                // Belt-and-suspenders: clamp again right before the byte goes on the wire.
                int rpm = ClampRpm(manualRpm.Value);
                var fr = Tx(RazerPacket.Create(TidSystem, 0x0d, 0x01, 0x03, 0x00, zone, (byte)(rpm / 100)));
                rpmOk = fr?.Status == RazerPacket.StatusCode.Success;
            }

            acks.Add(new ZoneAck(zone, powerOk, rpmOk));
        }

        var arr = acks.ToArray();
        return new ApplyResult(arr.All(a => a.PowerOk && a.RpmOk != false), arr);
    }

    // ---------- battery health optimizer (charge limit) ----------

    /// <summary>Read BHO state. Returns null if the EC does not support it (status NOT_SUPPORTED) or on I/O failure.</summary>
    public (bool Enabled, int ThresholdPercent)? GetBho()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x07, 0x92, 0x01, 0x00));
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        return ((r.Args[0] & 0x80) != 0, r.Args[0] & 0x7F);
    }

    /// <summary>Enable/disable the battery charge limit. Threshold clamped to [50, 80] percent.</summary>
    public bool SetBho(bool enabled, int thresholdPercent = 80)
    {
        int t = Math.Clamp(thresholdPercent, 50, 80);
        byte arg = (byte)((enabled ? 0x80 : 0x00) | t);
        var r = Tx(RazerPacket.Create(TidSystem, 0x07, 0x12, 0x01, arg));
        return r?.Status == RazerPacket.StatusCode.Success;
    }

    // ---------- keyboard lighting ----------

    /// <summary>Static color across the whole keyboard. OpenRazer: 0x03/0x0A args [0x06, r, g, b], tid 0xFF.</summary>
    public bool SetStaticColor(byte r, byte g, byte b)
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x04, 0x06, r, g, b));
        return resp?.Status == RazerPacket.StatusCode.Success;
    }

    /// <summary>Spectrum cycling effect. OpenRazer: 0x03/0x0A args [0x04].</summary>
    public bool SetSpectrum()
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x01, 0x04));
        return resp?.Status == RazerPacket.StatusCode.Success;
    }

    /// <summary>Single-color breathing effect. OpenRazer: 0x03/0x0A args [0x03, 0x01, r, g, b].</summary>
    public bool SetBreathing(byte r, byte g, byte b)
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x08, 0x03, 0x01, r, g, b));
        return resp?.Status == RazerPacket.StatusCode.Success;
    }

    /// <summary>All keyboard LEDs off. OpenRazer: 0x03/0x0A args [0x00].</summary>
    public bool SetLightsOff()
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x01, 0x00));
        return resp?.Status == RazerPacket.StatusCode.Success;
    }

    /// <summary>Keyboard backlight brightness 0-255. OpenRazer blade brightness: 0x0E/0x04 args [0x01, n].</summary>
    public bool SetBrightness(byte brightness)
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x0E, 0x04, 0x02, 0x01, brightness));
        return resp?.Status == RazerPacket.StatusCode.Success;
    }

    public int? GetBrightness()
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x0E, 0x84, 0x02, 0x01));
        if (resp?.Status != RazerPacket.StatusCode.Success) return null;
        return resp.Args[1];
    }

    // ---------- lid logo (LOGO_LED 0x04, standard led commands, tid 0xFF) ----------

    /// <summary>Lid logo lit? 0x03/0x80 [VARSTORE, LOGO_LED].</summary>
    public bool? GetLogoLed()
    {
        var r = Tx(RazerPacket.Create(TidChroma, 0x03, 0x80, 0x03, 0x01, 0x04, 0x00));
        return r?.Status == RazerPacket.StatusCode.Success ? r.Args[2] != 0 : null;
    }

    /// <summary>Logo effect: 0 steady, 2 blink (0x03/0x82).</summary>
    public byte? GetLogoEffect()
    {
        var r = Tx(RazerPacket.Create(TidChroma, 0x03, 0x82, 0x03, 0x01, 0x04, 0x00));
        return r?.Status == RazerPacket.StatusCode.Success ? r.Args[2] : null;
    }

    /// <summary>mode 0 off, 1 on (steady), 2 blink. Same sequence as razer-laptop-control set_logo_led_state.</summary>
    public bool SetLogo(byte mode)
    {
        if (mode > 0)
            Tx(RazerPacket.Create(TidChroma, 0x03, 0x02, 0x03, 0x01, 0x04, (byte)(mode == 2 ? 0x02 : 0x00)));
        var r = Tx(RazerPacket.Create(TidChroma, 0x03, 0x00, 0x03, 0x01, 0x04, (byte)(mode == 0 ? 0 : 1)));
        return r?.Status == RazerPacket.StatusCode.Success;
    }

    // ---------- more keyboard effects (standard matrix 0x03/0x0A, write-only) ----------

    /// <summary>direction 1 left-to-right, 2 right-to-left.</summary>
    public bool SetWave(byte direction) =>
        Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x02, 0x01, (byte)Math.Clamp((int)direction, 1, 2)))?.Status == RazerPacket.StatusCode.Success;

    /// <summary>speed 1 (short) .. 4 (long).</summary>
    public bool SetReactive(byte speed, byte r, byte g, byte b) =>
        Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x05, 0x02, (byte)Math.Clamp((int)speed, 1, 4), r, g, b))?.Status == RazerPacket.StatusCode.Success;

    /// <summary>speed 1 (fast) .. 3 (slow), single colour.</summary>
    public bool SetStarlight(byte speed, byte r, byte g, byte b) =>
        Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x09, 0x19, 0x01, (byte)Math.Clamp((int)speed, 1, 3), r, g, b, 0, 0, 0))?.Status == RazerPacket.StatusCode.Success;

    public bool SetBreathingRandom() =>
        Tx(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x02, 0x03, 0x03))?.Status == RazerPacket.StatusCode.Success;

    // ---------- device mode ----------

    /// <summary>0x00 normal (firmware runs its own effects), 0x03 driver (host-driven). Synapse leaves the Blade in 3.</summary>
    public byte? GetDeviceMode()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x00, 0x84, 0x02, 0x00, 0x00));
        return r?.Status == RazerPacket.StatusCode.Success ? r.Args[0] : null;
    }

    public bool SetDeviceMode(byte mode)
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x00, 0x04, 0x02, mode, 0x00));
        return r?.Status == RazerPacket.StatusCode.Success;
    }

    /// <summary>Extended-matrix static (0x0F/0x02, VARSTORE, BACKLIGHT_LED). Acks on this Blade; whether it paints is what the colour test decides.</summary>
    public bool SetStaticColorExtended(byte r, byte g, byte b)
    {
        var resp = Tx(RazerPacket.Create(TidChroma, 0x0F, 0x02, 0x09, 0x01, 0x05, 0x01, 0x00, 0x00, 0x01, r, g, b));
        return resp?.Status == RazerPacket.StatusCode.Success;
    }

    // ---------- device info (benign GETs, useful for verification) ----------

    public string? GetFirmwareVersion()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x00, 0x81, 0x02));
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        return $"v{r.Args[0]}.{r.Args[1]}";
    }

    public string? GetSerial()
    {
        var r = Tx(RazerPacket.Create(TidSystem, 0x00, 0x82, 0x16));
        if (r?.Status != RazerPacket.StatusCode.Success) return null;
        var s = System.Text.Encoding.ASCII.GetString(r.Args, 0, 22).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public void Dispose() => _dev.Dispose();
}
