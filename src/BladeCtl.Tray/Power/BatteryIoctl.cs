using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BladeCtl.Tray.Power;

/// <summary>
/// Battery readings straight from the battery driver (spec 4.2), read-only. IOCTL_BATTERY_QUERY_STATUS refreshes about
/// every 2 s, where WMI's BatteryStatus lags 15-20 s, so WMI is never used for battery data. One battery on this laptop;
/// values are summed in case of more. Handles stay open and are reopened after any failure.
/// </summary>
internal sealed class BatteryIoctl : IDisposable
{
    private static readonly Guid GUID_DEVICE_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA did);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfoData);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    private sealed class Bat { public string Path = ""; public SafeFileHandle? H; public uint Tag; }
    private readonly List<Bat> _bats = new();
    private DateTime _enumAt = DateTime.MinValue;

    public sealed record Status(bool Ok, uint PowerState, bool RateKnown, int RateMw, bool CapKnown, uint CapMwh, uint VoltageMv);
    public sealed record Info(uint DesignMwh, uint FullMwh);

    private void Enumerate()
    {
        if (_bats.Count > 0 || DateTime.UtcNow - _enumAt < TimeSpan.FromSeconds(30)) return;
        _enumAt = DateTime.UtcNow;
        var g = GUID_DEVICE_BATTERY;
        IntPtr set = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
                SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, need, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (path.Length > 0) _bats.Add(new Bat { Path = path });
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private bool Open(Bat b)
    {
        if (b.H is { IsInvalid: false, IsClosed: false } && b.Tag != 0) return true;
        b.H?.Dispose();
        b.H = CreateFileW(b.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (b.H.IsInvalid) { b.H.Dispose(); b.H = null; return false; }
        var outb = new byte[4];
        if (!DeviceIoControl(b.H, IOCTL_BATTERY_QUERY_TAG, new byte[4], 4, outb, 4, out _, IntPtr.Zero)) { Close(b); return false; }
        b.Tag = BitConverter.ToUInt32(outb, 0);
        if (b.Tag == 0) { Close(b); return false; }
        return true;
    }

    private static void Close(Bat b) { try { b.H?.Dispose(); } catch { } b.H = null; b.Tag = 0; }

    public Status QueryStatus()
    {
        Enumerate();
        bool any = false, rateKnown = false, capKnown = false; uint ps = 0, cap = 0, mv = 0; int rate = 0;
        foreach (var b in _bats)
        {
            if (!Open(b)) continue;
            var inb = new byte[20];
            BitConverter.GetBytes(b.Tag).CopyTo(inb, 0);   // Timeout, PowerState, Low, High = 0
            var outb = new byte[16];
            if (!DeviceIoControl(b.H!, IOCTL_BATTERY_QUERY_STATUS, inb, inb.Length, outb, outb.Length, out int got, IntPtr.Zero) || got < 16) { Close(b); continue; }
            any = true;
            ps |= BitConverter.ToUInt32(outb, 0);
            uint c = BitConverter.ToUInt32(outb, 4); uint v = BitConverter.ToUInt32(outb, 8); int r = BitConverter.ToInt32(outb, 12);
            if (c != 0xFFFFFFFF) { cap += c; capKnown = true; }
            if (v != 0xFFFFFFFF) mv = Math.Max(mv, v);
            if (r != unchecked((int)0x80000000)) { rate += r; rateKnown = true; }
        }
        if (!any) _bats.RemoveAll(b => b.H == null && b.Tag == 0 && DateTime.UtcNow - _enumAt > TimeSpan.FromSeconds(30));
        return new Status(any, ps, rateKnown, rate, capKnown, cap, mv);
    }

    private byte[]? QueryInfo(Bat b, int level, int outSize)
    {
        var inb = new byte[12];
        BitConverter.GetBytes(b.Tag).CopyTo(inb, 0);
        BitConverter.GetBytes(level).CopyTo(inb, 4);
        var outb = new byte[outSize];
        if (!DeviceIoControl(b.H!, IOCTL_BATTERY_QUERY_INFORMATION, inb, inb.Length, outb, outb.Length, out int got, IntPtr.Zero) || got < 4) return null;
        return outb;
    }

    /// <summary>BatteryInformation (level 0): DesignedCapacity at +12, FullChargedCapacity at +16 (mWh).</summary>
    public Info? QueryInformation()
    {
        Enumerate();
        uint design = 0, full = 0; bool any = false;
        foreach (var b in _bats)
        {
            if (!Open(b)) continue;
            var o = QueryInfo(b, 0, 36);
            if (o == null) { Close(b); continue; }
            design += BitConverter.ToUInt32(o, 12); full += BitConverter.ToUInt32(o, 16); any = true;
        }
        return any ? new Info(design, full) : null;
    }

    /// <summary>Windows' own estimate in seconds (BatteryEstimatedTime, level 3), or -1 when unknown.</summary>
    public int QueryEstimatedSec()
    {
        Enumerate();
        foreach (var b in _bats)
        {
            if (!Open(b)) continue;
            var o = QueryInfo(b, 3, 4);
            if (o == null) continue;
            uint s = BitConverter.ToUInt32(o, 0);
            return s == 0xFFFFFFFF ? -1 : (int)Math.Min(int.MaxValue, s);
        }
        return -1;
    }

    public void Dispose() { foreach (var b in _bats) Close(b); _bats.Clear(); }
}
