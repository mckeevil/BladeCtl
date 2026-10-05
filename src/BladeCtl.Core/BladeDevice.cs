using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace BladeCtl.Core;

/// <summary>
/// Low-level HID transport for the Blade 15 Advanced (Mid 2021) internal control device
/// (VID 0x1532, PID 0x0276). Finds the HID collection that accepts 91-byte feature
/// reports and answers Razer control packets, then provides a locked send/receive pipe.
///
/// Windows blocks read/write handles on system keyboard/mouse HID collections, so we
/// open with dwDesiredAccess = 0 (the same fallback hidapi/OpenRGB use): feature
/// reports go through IOCTLs and work fine on a zero-access handle.
///
/// Send flow (mirrors razer-laptop-control-no-dkms device.rs send_report):
///   HidD_SetFeature(91B) -> short wait -> HidD_GetFeature(91B) -> validate, retry x3.
/// </summary>
public sealed class BladeDevice : IDisposable
{
    public const int VendorId = 0x1532;
    public const int ProductId = 0x0276;

    private readonly string _path;
    private SafeFileHandle _handle;
    private readonly object _lock = new();
    private volatile bool _disposed;   // set under _lock: no transaction and no re-open on a device that was dropped

    public string DevicePath => _path;

    /// <summary>HID feature report id the control packets ride on. 0x00 for the Blade and most devices; 0x07 for the Leviathan V2 X.</summary>
    public byte ReportId { get; set; } = 0x00;

    private BladeDevice(string path, SafeFileHandle handle)
    {
        _path = path;
        _handle = handle;
    }

    /// <summary>
    /// Enumerate all HID collections for 1532:0276 and return the first one that
    /// answers a benign GET (power mode, class 0x0d cmd 0x82) with status SUCCESS.
    /// </summary>
    public static BladeDevice? Find(Action<string>? log = null)
    {
        var candidates = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
        log?.Invoke($"Found {candidates.Count} HID collections for {VendorId:X4}:{ProductId:X4}");

        foreach (var dev in candidates)
        {
            int maxFeature;
            try { maxFeature = dev.GetMaxFeatureReportLength(); }
            catch { continue; }

            log?.Invoke($"  {dev.DevicePath}  maxFeature={maxFeature}");
            if (maxFeature < RazerPacket.BufferLen)
                continue;

            var handle = OpenHandle(dev.DevicePath, log);
            if (handle == null)
                continue;

            var cand = new BladeDevice(dev.DevicePath, handle);
            var probe = RazerPacket.Create(0x1F, 0x0d, 0x82, 0x04, 0x00, 0x01, 0x00, 0x00);
            var resp = cand.Transact(probe, log);
            if (resp is { Status: RazerPacket.StatusCode.Success } && resp.CommandClass == 0x0d)
            {
                log?.Invoke($"    -> ANSWERS razer control packets (power mode = {resp.Args[2]}, manualFan = {resp.Args[3]})");
                return cand;
            }

            log?.Invoke($"    -> no valid answer ({(resp == null ? "null" : resp.ToString())})");
            cand.Dispose();
        }

        return null;
    }

    /// <summary>Open a specific HID collection path with no probe (accessory enumeration / generic Chroma devices).</summary>
    public static BladeDevice? OpenPath(string path, Action<string>? log = null, byte reportId = 0x00)
    {
        var h = OpenHandle(path, log);
        return h == null ? null : new BladeDevice(path, h) { ReportId = reportId };
    }

    /// <summary>
    /// Open with GENERIC_READ|GENERIC_WRITE first (falls back to zero access). Windows refuses R/W on
    /// system keyboard/mouse collections but allows it on vendor collections, and some devices reject
    /// feature-report writes on a zero-access handle.
    /// </summary>
    public static BladeDevice? OpenPathReadWrite(string path, Action<string>? log = null)
    {
        foreach (uint access in new uint[] { GENERIC_READ | GENERIC_WRITE, GENERIC_WRITE, 0 })
        {
            var h = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) { log?.Invoke($"    opened with access 0x{access:X8}"); return new BladeDevice(path, h); }
            log?.Invoke($"    open with access 0x{access:X8} failed (win32 {Marshal.GetLastWin32Error()})");
            h.Dispose();
        }
        return null;
    }

    /// <summary>Raw feature-report write with an explicit report id (some devices are not report id 0).</summary>
    public bool SetFeatureRaw(byte reportId, byte[] payload90, out int win32)
    {
        var buf = new byte[RazerPacket.BufferLen];
        buf[0] = reportId;
        Array.Copy(payload90, 0, buf, 1, Math.Min(90, payload90.Length));
        lock (_lock)
        {
            bool ok = HidD_SetFeature(_handle, buf, (uint)buf.Length);
            win32 = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                      NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    /// <summary>Windows' own view of the collection: usage and the exact report lengths (incl. report id byte).</summary>
    public HidpCaps? GetCaps()
    {
        if (!HidD_GetPreparsedData(_handle, out var pp) || pp == IntPtr.Zero) return null;
        try { return HidP_GetCaps(pp, out var caps) == 0x00110000 ? caps : null; }
        finally { HidD_FreePreparsedData(pp); }
    }

    /// <summary>Output report via the control endpoint (SET_REPORT) instead of the interrupt pipe.</summary>
    public bool SetOutputReportRaw(byte[] buf, out int win32)
    {
        lock (_lock)
        {
            bool ok = HidD_SetOutputReport(_handle, buf, (uint)buf.Length);
            win32 = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
    }

    /// <summary>Input report via the control endpoint (GET_REPORT).</summary>
    public bool GetInputReportRaw(byte reportId, int length, out byte[] buf, out int win32)
    {
        buf = new byte[length];
        buf[0] = reportId;
        lock (_lock)
        {
            bool ok = HidD_GetInputReport(_handle, buf, (uint)buf.Length);
            win32 = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
    }

    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll", SetLastError = true)] private static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, uint len);

    public bool GetFeatureRaw(byte reportId, out byte[] buf, out int win32)
    {
        buf = new byte[RazerPacket.BufferLen];
        buf[0] = reportId;
        lock (_lock)
        {
            bool ok = HidD_GetFeature(_handle, buf, (uint)buf.Length);
            win32 = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
    }

    private static SafeFileHandle? OpenHandle(string path, Action<string>? log)
    {
        // Zero-access open first (works on keyboard/mouse collections), then read/write.
        foreach (uint access in new uint[] { 0, GENERIC_READ | GENERIC_WRITE })
        {
            var h = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid)
                return h;
            h.Dispose();
        }
        log?.Invoke($"    -> cannot open (win32 error {Marshal.GetLastWin32Error()})");
        return null;
    }

    /// <summary>
    /// Send a packet and read the device's response. Returns null on I/O failure.
    ///
    /// The Razer EC on Windows buffers the response: an immediate HidD_GetFeature often
    /// returns a stale report (typically class 0x03/0x0A) before the real answer is ready.
    /// So each SetFeature is followed by an inner read-retry loop that keeps re-reading the
    /// feature report until command_class matches what we sent. Only if that loop exhausts
    /// do we re-send the whole packet.
    /// </summary>
    public RazerPacket? Transact(RazerPacket packet, Action<string>? log = null)
    {
        // This EC is pipelined: the response to a request typically only lands after the
        // NEXT SetFeature, so the first send's reads are usually stale and the re-send gets
        // the real answer. Keep reads-per-send small and allow several re-sends for robustness
        // (all these commands are idempotent, so a repeated send is harmless).
        const int MaxSends = 6;
        const int MaxReadsPerSend = 4;

        lock (_lock)
        {
            if (_disposed) return null;
            var outBuf = packet.ToBuffer(ReportId);

            for (int send = 0; send < MaxSends; send++)
            {
                if (_disposed) return null;
                try
                {
                    if (!HidD_SetFeature(_handle, outBuf, (uint)outBuf.Length))
                    {
                        log?.Invoke($"    SetFeature failed (win32 {Marshal.GetLastWin32Error()})");
                        Thread.Sleep(25);
                        TryReopen();
                        continue;
                    }

                    for (int read = 0; read < MaxReadsPerSend; read++)
                    {
                        Thread.Sleep(read == 0 ? 4 : 8);

                        var inBuf = new byte[RazerPacket.BufferLen];
                        inBuf[0] = ReportId; // report ID we want back
                        if (!HidD_GetFeature(_handle, inBuf, (uint)inBuf.Length))
                        {
                            log?.Invoke($"    GetFeature failed (win32 {Marshal.GetLastWin32Error()})");
                            continue;
                        }

                        var resp = RazerPacket.FromBuffer(inBuf);

                        if (resp.Status == RazerPacket.StatusCode.Busy)
                            continue;

                        // Correlate response to request by command_class AND command_id, exactly
                        // like razer-laptop-control-no-dkms send_report. Matching class alone is
                        // NOT enough: e.g. BHO SET (0x07/0x12) and GET (0x07/0x92) share a class,
                        // so a stale SET echo would be wrongly accepted as the GET answer.
                        if (resp.CommandClass == packet.CommandClass && resp.CommandId == packet.CommandId)
                            return resp;
                    }

                    log?.Invoke($"    no matching response after {MaxReadsPerSend} reads; re-sending (sent class=0x{packet.CommandClass:X2} cmd=0x{packet.CommandId:X2})");
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"    transact error (send {send + 1}): {ex.Message}");
                    Thread.Sleep(25);
                    TryReopen();
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Fire-and-forget for write-only devices: one SetFeature, then a single GetFeature whose raw
    /// content is returned uncorrelated. Returns true when the SetFeature itself succeeded.
    /// </summary>
    public bool SendNoWait(RazerPacket packet, out RazerPacket? firstResponse, Action<string>? log = null)
    {
        firstResponse = null;
        lock (_lock)
        {
            if (_disposed) return false;
            var outBuf = packet.ToBuffer(ReportId);
            bool ok = HidD_SetFeature(_handle, outBuf, (uint)outBuf.Length);
            if (!ok) { log?.Invoke($"    SetFeature failed (win32 {Marshal.GetLastWin32Error()})"); return false; }
            Thread.Sleep(30);
            var inBuf = new byte[RazerPacket.BufferLen];
            inBuf[0] = ReportId;
            if (HidD_GetFeature(_handle, inBuf, (uint)inBuf.Length)) firstResponse = RazerPacket.FromBuffer(inBuf);
            return true;
        }
    }

    private void TryReopen()
    {
        if (_disposed) return;   // never re-open a handle on a device the owner already dropped (it would leak)
        try { _handle.Dispose(); } catch { }
        _handle = OpenHandle(_path, null) ?? _handle;
    }

    /// <summary>Waits for a transaction in progress on another thread (2.7.0 charger reads), then closes the handle for good.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            try { _handle.Dispose(); } catch { }
        }
    }

    // ---------- Win32 ----------

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);
}
