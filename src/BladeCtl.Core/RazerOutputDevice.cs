using HidSharp;

namespace BladeCtl.Core;

/// <summary>
/// Transport for Razer devices whose control interface is an OUTPUT report rather than a feature
/// report. The Leviathan V2 X (1532:054A) descriptor is OUTPUT id 0x03 (92 bytes) + INPUT ids
/// 0x01/0x05 (16 bytes). The 90-byte Razer packet is framed as [0x03][0x00][packet]. Nothing has been
/// seen coming back on the input reports, so callers must treat every write as unverified.
/// </summary>
public sealed class RazerOutputDevice : IDisposable
{
    private readonly HidStream _stream;
    public string DevicePath { get; }
    public byte ReportId { get; }

    private RazerOutputDevice(HidStream stream, string path, byte reportId) { _stream = stream; DevicePath = path; ReportId = reportId; }

    /// <summary>Open the first collection of a product that exposes an output report of at least 92 bytes.</summary>
    public static RazerOutputDevice? Open(ushort pid, byte reportId = 0x03, Action<string>? log = null)
    {
        try
        {
            var dev = DeviceList.Local.GetHidDevices(BladeDevice.VendorId, pid)
                .FirstOrDefault(d => { try { return d.GetMaxOutputReportLength() >= RazerPacket.BufferLen + 1; } catch { return false; } });
            if (dev == null) { log?.Invoke($"no output-report collection for 1532:{pid:X4}"); return null; }
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            var s = dev.Open(cfg);
            s.ReadTimeout = 300; s.WriteTimeout = 1000;
            return new RazerOutputDevice(s, dev.DevicePath, reportId);
        }
        catch (Exception ex) { log?.Invoke($"output-report open failed: {ex.Message}"); return null; }
    }

    /// <summary>Write one packet. Returns true when the write completed; the reply (if any) is returned raw.</summary>
    public bool Send(RazerPacket packet, out byte[]? reply, Action<string>? log = null)
    {
        reply = null;
        try
        {
            var full = packet.ToBuffer();                 // [0x00] + 90 bytes
            var o = new byte[RazerPacket.BufferLen + 1];  // 92
            o[0] = ReportId;
            Array.Copy(full, 0, o, 1, RazerPacket.BufferLen);
            _stream.Write(o);
            try
            {
                var b = new byte[64];
                int n = _stream.Read(b, 0, b.Length);
                if (n > 0) reply = b.Take(n).ToArray();
            }
            catch (TimeoutException) { }
            return true;
        }
        catch (Exception ex) { log?.Invoke($"output-report write failed: {ex.Message}"); return false; }
    }

    public void Dispose() { try { _stream.Dispose(); } catch { } }
}
