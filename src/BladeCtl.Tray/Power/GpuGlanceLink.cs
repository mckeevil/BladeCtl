using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BladeCtl.Core.Power;

namespace BladeCtl.Tray.Power;

/// <summary>
/// Read-only links to the sister app GPU Glance (spec 13), so the two never disagree:
///  - GpuGlance.ini [Battery] ChargeCurve / ChargeCurveCount (UTF-16LE with BOM): the charge curve used first for the ETA;
///  - GpuGlance.ini [Limit.*] AC p95: the last fallback for the battery comparison's reference limit;
///  - Mode S: the shared state block Local\GpuGlance.State.v1. While it is fresh BladeCtl shows GPU Glance's state and
///    skips its own NVML while Live. Inert until GPU Glance publishes it.
/// Nothing is ever written to GPU Glance's files.
/// </summary>
internal sealed class GpuGlanceLink
{
    public static string IniPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Tools", "GpuGlance", "GpuGlance.ini");

    private DateTime _readAt = DateTime.MinValue;
    public ChargeBin[]? Curve { get; private set; }
    public double AcP95W { get; private set; } = -1;
    public string IniState { get; private set; } = "not read";

    /// <summary>Re-read the ini (when the window opens and every 5 minutes while Live).</summary>
    public void Refresh(bool force)
    {
        if (!force && DateTime.UtcNow - _readAt < TimeSpan.FromMinutes(5)) return;
        _readAt = DateTime.UtcNow;
        try
        {
            if (!File.Exists(IniPath)) { Curve = null; AcP95W = -1; IniState = "absent"; return; }
            string text;
            using (var fs = new FileStream(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.Unicode, detectEncodingFromByteOrderMarks: true))
                text = sr.ReadToEnd();
            var sec = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[' && line.EndsWith(']')) { cur = new(StringComparer.OrdinalIgnoreCase); sec[line[1..^1]] = cur; continue; }
                int eq = line.IndexOf('=');
                if (cur != null && eq > 0) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            ChargeBin[]? curve = null;
            if (sec.TryGetValue("Battery", out var b) && b.TryGetValue("ChargeCurve", out var cc) && b.TryGetValue("ChargeCurveCount", out var cn))
            {
                curve = new ChargeBin[BatteryEta.Bins];
                foreach (var part in cc.Split(',', StringSplitOptions.RemoveEmptyEntries)) { var kv = part.Split(':'); if (kv.Length == 2 && int.TryParse(kv[0], out var i) && i is >= 0 and < BatteryEta.Bins) curve[i].Mw = double.Parse(kv[1], CultureInfo.InvariantCulture); }
                foreach (var part in cn.Split(',', StringSplitOptions.RemoveEmptyEntries)) { var kv = part.Split(':'); if (kv.Length == 2 && int.TryParse(kv[0], out var i) && i is >= 0 and < BatteryEta.Bins) curve[i].N = int.Parse(kv[1], CultureInfo.InvariantCulture); }
                if (!curve.Any(x => x.N >= 3 && x.Mw > 0)) curve = null;
            }
            Curve = curve;
            double best = -1;
            foreach (var kv in sec)
                if (kv.Key.StartsWith("Limit.", StringComparison.OrdinalIgnoreCase) && kv.Key.Contains(".AC.", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.TryGetValue("P95Mw", out var p) && kv.Value.TryGetValue("Count", out var n)
                    && int.TryParse(n, out var cnt) && cnt >= 60 && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var mw) && mw > 0)
                    best = Math.Max(best, mw / 1000.0);
            AcP95W = best;
            IniState = $"read ({(Curve != null ? Curve.Count(x => x.N >= 3) + " curve bins" : "no curve")}, AC p95 {(best > 0 ? best.ToString("F0", CultureInfo.InvariantCulture) + " W" : "none")})";
        }
        catch (Exception ex) { IniState = "unreadable (" + ex.GetType().Name + ")"; }
    }

    // ---------- Mode S ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    private const uint FILE_MAP_READ = 0x4;
    private const uint Magic = 0x31534747;   // 'GGS1'

    public sealed record ModeS(St Shown, Sub ShownSub, St Raw, Sub RawSub, bool OnAC, double P, double L, double Ldef, double Lmax, double Lref,
                               bool LrefLearned, double U, double T, double Duty74, double Duty270, double Duty271, double FracBrake);

    /// <summary>GPU Glance's verdict when its block is fresh (seq read twice, equal and even, written within 5 s), else null.</summary>
    public static ModeS? ReadModeS()
    {
        IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, @"Local\GpuGlance.State.v1");
        if (h == IntPtr.Zero) return null;
        IntPtr v = IntPtr.Zero;
        try
        {
            v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (UIntPtr)128);
            if (v == IntPtr.Zero) return null;
            if ((uint)Marshal.ReadInt32(v, 0) != Magic || Marshal.ReadInt32(v, 4) != 1) return null;
            uint seq1 = (uint)Marshal.ReadInt32(v, 8);
            var b = new byte[128];
            Marshal.Copy(v, b, 0, 128);
            uint seq2 = (uint)Marshal.ReadInt32(v, 8);
            if (seq1 != seq2 || (seq1 & 1) != 0) return null;
            ulong tick = BitConverter.ToUInt64(b, 16);
            if (GetTickCount64() - tick > 5000) return null;
            int I(int o) => BitConverter.ToInt32(b, o);
            float F(int o) => BitConverter.ToSingle(b, o);
            St Sx(int o) => Enum.IsDefined(typeof(St), I(o)) ? (St)I(o) : St.UNKNOWN;
            Sub Ux(int o) => Enum.IsDefined(typeof(Sub), I(o)) ? (Sub)I(o) : Sub.None;
            return new ModeS(Sx(24), Ux(28), Sx(32), Ux(36), I(40) != 0, F(44), F(48), F(52), F(56), F(60), I(64) != 0, F(68), F(72), F(76), F(80), F(84), F(88));
        }
        catch { return null; }
        finally { if (v != IntPtr.Zero) UnmapViewOfFile(v); CloseHandle(h); }
    }
}
