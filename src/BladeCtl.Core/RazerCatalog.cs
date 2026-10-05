namespace BladeCtl.Core;

/// <summary>Which command family a device's lighting answers to.</summary>
public enum EffectFamily
{
    /// <summary>0x0F/0x02 effects + 0x0F/0x04 brightness, with a storage byte (VARSTORE persists on the device).</summary>
    ExtendedMatrix,
    /// <summary>Blade laptops: 0x03/0x0A effects (no storage byte) + 0x0E/0x04 brightness.</summary>
    StandardMatrix,
    /// <summary>Speaks the 90-byte protocol but no lighting family is known for it.</summary>
    ProtocolOnly,
    /// <summary>Does not answer the 90-byte protocol at all (inventory only).</summary>
    None,
}

public sealed record RazerModel(
    ushort Pid,
    string Name,
    string Kind,
    EffectFamily Family,
    byte Tid,
    byte LedId,
    bool OnboardMemory,
    string Notes,
    byte ReportId = 0x00,
    /// <summary>False when 0x0F/0x82 answers with data unrelated to the effect just written (Leviathan V2 X).</summary>
    bool EffectReadback = true);

/// <summary>
/// What is known about each Razer product id, from OpenRazer's driver tables and from probing
/// the author's devices on 2026-09-05. Anything not listed is enumerated generically and probed.
/// </summary>
public static class RazerCatalog
{
    public const byte NOSTORE = 0x00, VARSTORE = 0x01;
    public const byte ZERO_LED = 0x00, LOGO_LED = 0x04, BACKLIGHT_LED = 0x05;

    public static readonly IReadOnlyDictionary<ushort, RazerModel> Known = new Dictionary<ushort, RazerModel>
    {
        [0x0276] = new(0x0276, "Razer Blade 15 Advanced (Mid 2021)", "Laptop", EffectFamily.StandardMatrix, 0xFF, BACKLIGHT_LED, false,
            "Fan and power via the Blade controller. Lighting effects are write-only. Static colour confirmed by eye on 2026-09-05 once Synapse was closed; the controller does not store it for power-on, so the boot task re-applies it."),
        [0x0266] = new(0x0266, "Razer Huntsman V2 Analog", "Keyboard", EffectFamily.ExtendedMatrix, 0x1F, BACKLIGHT_LED, true,
            "Effects, brightness, game mode and polling rate are written to onboard memory and read back. Per-key actuation is NOT stored by this generation's firmware (write test 2026-09-05): Synapse applied it host-side in driver mode."),
        [0x0F21] = new(0x0F21, "Razer Thunderbolt 4 Dock Chroma", "Dock", EffectFamily.ExtendedMatrix, 0x1F, ZERO_LED, true,
            "Underglow strip. Effects written to onboard memory."),
        [0x054A] = new(0x054A, "Razer Leviathan V2 X", "Speaker", EffectFamily.ExtendedMatrix, 0x1F, ZERO_LED, true,
            "14-LED underglow. Speaks the standard protocol on HID feature report id 0x07 (found 2026-09-05; Synapse's log agrees). Its effect register reads back unrelated data, so effects stay 'accepted'. Audio works through Windows without Synapse.",
            ReportId: 0x07, EffectReadback: false),
        [0x0565] = new(0x0565, "Razer BlackShark V2 HyperSpeed", "Headset (2.4 GHz dongle)", EffectFamily.None, 0x1F, ZERO_LED, false,
            "No lighting. EQ, mic and sidetone use an undocumented 64-byte protocol; audio works through Windows without Synapse."),
    };

    public static RazerModel? Lookup(ushort pid) => Known.TryGetValue(pid, out var m) ? m : null;

    public static string NameFor(ushort pid, string? usbName) =>
        Known.TryGetValue(pid, out var m) ? m.Name : (string.IsNullOrWhiteSpace(usbName) ? $"Razer device 0x{pid:X4}" : usbName!);
}
