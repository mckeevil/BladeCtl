namespace BladeCtl.Core;

/// <summary>
/// Lighting control for Razer accessories that use the EXTENDED matrix command family
/// (0x0F/0x02 effects, 0x0F/0x04 brightness). Verified on the Huntsman V2 Analog (tid 0x1F,
/// BACKLIGHT_LED) and the Thunderbolt 4 Dock Chroma (tid 0x1F, ZERO_LED); layouts from OpenRazer
/// razerchromacommon.c. With storage = VARSTORE the device keeps the effect in its own memory.
/// </summary>
public sealed class ChromaController : IDisposable
{
    public const byte EffectNone = 0x00, EffectStatic = 0x01, EffectBreathing = 0x02, EffectSpectrum = 0x03,
                      EffectWave = 0x04, EffectReactive = 0x05, EffectStarlight = 0x07;

    private readonly BladeDevice _dev;
    private readonly Action<string>? _log;

    public byte Tid { get; }
    public byte LedId { get; }
    public int ConsecutiveFailures { get; private set; }

    public ChromaController(BladeDevice dev, byte tid, byte ledId, Action<string>? log = null)
    {
        _dev = dev; Tid = tid; LedId = ledId; _log = log;
    }

    private RazerPacket? Tx(RazerPacket p)
    {
        var r = _dev.Transact(p, _log);
        if (r == null) ConsecutiveFailures++; else ConsecutiveFailures = 0;
        return r;
    }

    private bool Ok(RazerPacket? r) => r?.Status == RazerPacket.StatusCode.Success;

    // ---------- identity ----------

    public string? GetFirmwareVersion()
    {
        var r = Tx(RazerPacket.Create(Tid, 0x00, 0x81, 0x02));
        return Ok(r) ? $"v{r!.Args[0]}.{r.Args[1]}" : null;
    }

    public string? GetSerial()
    {
        var r = Tx(RazerPacket.Create(Tid, 0x00, 0x82, 0x16));
        if (!Ok(r)) return null;
        var s = System.Text.Encoding.ASCII.GetString(r!.Args, 0, 22).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) || s.All(c => c == '?') ? null : s;
    }

    /// <summary>0x00 normal (onboard effects), 0x03 driver (host drives lighting).</summary>
    public byte? GetDeviceMode()
    {
        var r = Tx(RazerPacket.Create(Tid, 0x00, 0x84, 0x02, 0x00, 0x00));
        return Ok(r) ? r!.Args[0] : null;
    }

    public bool SetDeviceMode(byte mode) => Ok(Tx(RazerPacket.Create(Tid, 0x00, 0x04, 0x02, mode, 0x00)));

    // ---------- effects (extended matrix) ----------

    public bool SetNone(byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x06, storage, LedId, EffectNone)));

    public bool SetStatic(byte r, byte g, byte b, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x09, storage, LedId, EffectStatic, 0x00, 0x00, 0x01, r, g, b)));

    public bool SetSpectrum(byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x06, storage, LedId, EffectSpectrum)));

    public bool SetBreathing(byte r, byte g, byte b, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x09, storage, LedId, EffectBreathing, 0x01, 0x00, 0x01, r, g, b)));

    /// <summary>direction 1 = left-to-right, 2 = right-to-left.</summary>
    public bool SetWave(byte direction = 1, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x06, storage, LedId, EffectWave, (byte)Math.Clamp((int)direction, 1, 2), 0x28)));

    /// <summary>speed 1 (short) .. 4 (long).</summary>
    public bool SetReactive(byte r, byte g, byte b, byte speed = 2, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x09, storage, LedId, EffectReactive, (byte)Math.Clamp((int)speed, 1, 4), 0x00, 0x01, r, g, b)));

    /// <summary>speed 1 (fast) .. 3 (slow), single colour.</summary>
    public bool SetStarlight(byte r, byte g, byte b, byte speed = 2, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x09, storage, LedId, EffectStarlight, 0x01, (byte)Math.Clamp((int)speed, 1, 3), 0x01, r, g, b)));

    public bool SetBreathingRandom(byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x06, storage, LedId, EffectBreathing, 0x00)));

    public bool SetBreathingDual(byte r, byte g, byte b, byte r2, byte g2, byte b2, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x02, 0x0C, storage, LedId, EffectBreathing, 0x02, 0x00, 0x02, r, g, b, r2, g2, b2)));

    /// <summary>Read the stored effect id + params (0x0F/0x82). Not every device implements it.</summary>
    public byte[]? GetEffect(byte storage = RazerCatalog.VARSTORE)
    {
        var r = Tx(RazerPacket.Create(Tid, 0x0F, 0x82, 0x06, storage, LedId, 0x00));
        return Ok(r) ? r!.Args.Take(12).ToArray() : null;
    }

    // ---------- keyboard extras (standard led commands; OpenRazer uses tid 0xFF for the Huntsman V2 Analog here) ----------

    public const byte GAME_LED = 0x08;

    /// <summary>Game mode (Windows key disabled). 0x03/0x00 [VARSTORE, GAME_LED, state].</summary>
    public bool SetGameMode(bool on, byte tid = 0xFF) =>
        Ok(Tx(RazerPacket.Create(tid, 0x03, 0x00, 0x03, RazerCatalog.VARSTORE, GAME_LED, (byte)(on ? 1 : 0))));

    public bool? GetGameMode(byte tid = 0xFF)
    {
        var r = Tx(RazerPacket.Create(tid, 0x03, 0x80, 0x03, RazerCatalog.VARSTORE, GAME_LED, 0x00));
        return Ok(r) ? r!.Args[2] != 0 : null;
    }

    /// <summary>
    /// Polling rate, classic command 0x00/0x85 (the Huntsman V2 Analog answers this one; the HyperPolling
    /// 0x00/0xC0 register is NOT_SUPPORTED on it - verified 2026-09-05). Code 0x01 = 1000 Hz, 0x02 = 500, 0x08 = 125.
    /// </summary>
    public int? GetPollingHz()
    {
        var r = Tx(RazerPacket.Create(Tid, 0x00, 0x85, 0x01));
        return Ok(r) ? PollingHz(r!.Args[0]) : null;
    }

    public bool SetPollingHz(int hz) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x00, 0x05, 0x01, PollingCodeFor(hz))));

    public static int PollingHz(byte code) => code switch { 0x01 => 1000, 0x02 => 500, 0x08 => 125, _ => 0 };
    public static byte PollingCodeFor(int hz) => hz switch { 1000 => 0x01, 500 => 0x02, 125 => 0x08, _ => 0x01 };

    // ---------- brightness (readable) ----------

    public bool SetBrightness(byte brightness, byte storage = RazerCatalog.VARSTORE) =>
        Ok(Tx(RazerPacket.Create(Tid, 0x0F, 0x04, 0x03, storage, LedId, brightness)));

    public int? GetBrightness(byte storage = RazerCatalog.VARSTORE)
    {
        var r = Tx(RazerPacket.Create(Tid, 0x0F, 0x84, 0x03, storage, LedId, 0x00));
        return Ok(r) ? r!.Args[2] : null;
    }

    public void Dispose() => _dev.Dispose();
}
