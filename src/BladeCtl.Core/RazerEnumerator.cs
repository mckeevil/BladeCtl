using HidSharp;

namespace BladeCtl.Core;

/// <summary>Everything discoverable about one attached Razer product without changing it.</summary>
public sealed record RazerDeviceInfo(
    ushort Pid,
    string Name,
    string Kind,
    RazerModel? Model,
    string? ControlPath,
    string? CandidatePath,
    byte Tid,
    byte ReportId,
    string? Firmware,
    string? Serial,
    byte? DeviceMode,
    int? Brightness,
    int Collections,
    string Detail,
    bool? GameMode = null,
    int? PollingHz = null,
    byte[]? Effect = null)
{
    public bool SpeaksProtocol => ControlPath != null;
    public EffectFamily Family => Model?.Family ?? (SpeaksProtocol ? EffectFamily.ProtocolOnly : EffectFamily.None);
    public bool IsBlade => Pid == BladeDevice.ProductId;
    /// <summary>Keyboard extras (game mode, polling) answered on this device.</summary>
    public bool IsKeyboard => Kind == "Keyboard" && !IsBlade;
    /// <summary>Stored effect id from 0x0F/0x82, when the device reports one (Huntsman, dock: yes; Leviathan: unrelated data).</summary>
    public byte? EffectId => Effect != null && Effect.Length > 2 ? Effect[2] : null;
}

/// <summary>
/// Finds every Razer (VID 0x1532) HID product on the system and, for each, the collection that
/// answers the 90-byte control protocol (91-byte feature report, firmware GET echoes). Read-only.
/// </summary>
public static class RazerEnumerator
{
    private static readonly byte[] TidCandidates = { 0x1F, 0x3F, 0xFF, 0x9F, 0x08, 0x88 };
    /// <summary>Feature report ids seen carrying the protocol: 0x00 (almost everything), 0x07 (Leviathan V2 X).</summary>
    private static readonly byte[] ReportIdCandidates = { 0x00, 0x07 };

    public static List<RazerDeviceInfo> Enumerate(Action<string>? log = null)
    {
        var result = new List<RazerDeviceInfo>();
        List<HidDevice> all;
        try { all = DeviceList.Local.GetHidDevices().Where(d => d.VendorID == BladeDevice.VendorId).ToList(); }
        catch (Exception ex) { log?.Invoke("hid enumeration failed: " + ex.Message); return result; }

        foreach (var grp in all.GroupBy(d => (ushort)d.ProductID).OrderBy(g => g.Key))
        {
            ushort pid = grp.Key;
            var model = RazerCatalog.Lookup(pid);
            string? usbName = grp.Select(d => { try { return d.GetProductName(); } catch { return null; } })
                                 .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            string name = RazerCatalog.NameFor(pid, usbName);
            string kind = model?.Kind ?? "Device";

            string? controlPath = null, candidatePath = null; byte tid = model?.Tid ?? 0x1F; byte reportId = model?.ReportId ?? 0x00;
            string? fw = null, serial = null; byte? mode = null; int? bright = null;
            bool? gameMode = null; int? pollingHz = null; byte[]? effect = null;
            var detail = new List<string>();

            foreach (var d in grp)
            {
                int fl; try { fl = d.GetMaxFeatureReportLength(); } catch { continue; }
                if (fl < RazerPacket.BufferLen) continue;
                candidatePath ??= d.DevicePath;

                // Probing is expected to fail on the wrong report id / tid, so it runs without the logger
                // (a miss is a "win32 87" per attempt and would flood the log at every scan).
                using var dev = BladeDevice.OpenPath(d.DevicePath, null);
                if (dev == null) { detail.Add("91-byte collection could not be opened"); continue; }

                // Catalogue report id and tid first, then the rest.
                var rids = new List<byte> { reportId }; rids.AddRange(ReportIdCandidates.Where(x => x != reportId));
                var order = new List<byte> { tid }; order.AddRange(TidCandidates.Where(t => t != tid));
                foreach (var rid in rids)
                {
                    dev.ReportId = rid;
                    foreach (var t in order)
                    {
                        var r = dev.Transact(RazerPacket.Create(t, 0x00, 0x81, 0x02), null);
                        if (r?.Status != RazerPacket.StatusCode.Success) continue;
                        controlPath = d.DevicePath; tid = t; reportId = rid;
                        fw = $"v{r.Args[0]}.{r.Args[1]}";
                        var ctl = new ChromaController(dev, t, model?.LedId ?? RazerCatalog.ZERO_LED, log);
                        serial = ctl.GetSerial();
                        mode = ctl.GetDeviceMode();
                        if (model?.Family == EffectFamily.ExtendedMatrix)
                        {
                            bright = ctl.GetBrightness();
                            effect = ctl.GetEffect();
                            if (kind == "Keyboard") { gameMode = ctl.GetGameMode(); pollingHz = ctl.GetPollingHz(); }
                        }
                        else if (pid == BladeDevice.ProductId)
                        {
                            var b = dev.Transact(RazerPacket.Create(0xFF, 0x0E, 0x84, 0x02, 0x01, 0x00), log);
                            if (b?.Status == RazerPacket.StatusCode.Success) bright = b.Args[1];
                        }
                        break;
                    }
                    if (controlPath != null) break;
                }
                if (controlPath != null) break;
                detail.Add("91-byte collection answered no report id / transaction id");
            }

            if (controlPath == null && !grp.Any(d => { try { return d.GetMaxFeatureReportLength() >= RazerPacket.BufferLen; } catch { return false; } }))
                detail.Add("no 91-byte control collection");

            result.Add(new RazerDeviceInfo(pid, name, kind, model, controlPath, candidatePath, tid, reportId, fw, serial, mode, bright, grp.Count(),
                string.Join("; ", detail), gameMode, pollingHz, effect));
        }
        return result;
    }

    /// <summary>Open a ChromaController on a device found by <see cref="Enumerate"/>. Caller disposes.</summary>
    public static ChromaController? Open(RazerDeviceInfo info, Action<string>? log = null)
    {
        if (info.ControlPath == null) return null;
        var dev = BladeDevice.OpenPath(info.ControlPath, log, info.ReportId);
        return dev == null ? null : new ChromaController(dev, info.Tid, info.Model?.LedId ?? RazerCatalog.ZERO_LED, log);
    }
}
