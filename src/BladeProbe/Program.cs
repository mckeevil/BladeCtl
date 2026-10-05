using System.Diagnostics;
using BladeCtl.Core;
using HidSharp;

// BladeProbe — staged, incremental hardware verification for BladeCtl.
//   (no args)                 read-only probe: discovery + GET everything. Sends no state changes.
//   --enum                    every Razer HID collection on the system + which speak the control protocol (read-only)
//   --write-noop              re-writes the CURRENT power mode (validates the write path, no visible change)
//   --mode balanced|gaming    set performance mode (auto fan)
//   --bho on|off [pct]        set battery charge limit
//   --fan-test <rpm> <sec>    manual fan test with live temp/rpm polling; ALWAYS restores auto at the end
//   --restore-auto            restore automatic fan control + balanced mode
//   --rgb <r> <g> <b>         static keyboard color
//   --rgb-test [sec]          step through every candidate static-colour command family
//   --spectrum                spectrum effect
//   --brightness <0-255>      keyboard brightness

var log = (string s) => Console.WriteLine(s);

// --enum: every Razer (VID 1532) HID collection on the system, which ones speak the 90-byte control
// protocol, and with which transaction id. Read-only: firmware GET, serial GET, brightness GETs.
if (args.Length > 0 && args[0] == "--enum")
{
    var all = DeviceList.Local.GetHidDevices().Where(d => d.VendorID == 0x1532).ToList();
    Console.WriteLine($"{all.Count} Razer HID collections");
    foreach (var grp in all.GroupBy(d => d.ProductID).OrderBy(g => g.Key))
    {
        string name = grp.Select(d => { try { return d.GetProductName(); } catch { return null; } }).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "?";
        Console.WriteLine();
        Console.WriteLine($"== PID 0x{grp.Key:X4}  {name}  ({grp.Count()} collections)");
        foreach (var d in grp)
        {
            int fl = -1, il = -1, ol = -1;
            try { fl = d.GetMaxFeatureReportLength(); il = d.GetMaxInputReportLength(); ol = d.GetMaxOutputReportLength(); } catch { }
            var m = System.Text.RegularExpressions.Regex.Match(d.DevicePath, @"mi_(\d\d)(&col(\d\d))?");
            string where = m.Success ? ("mi_" + m.Groups[1].Value + (m.Groups[3].Success ? " col" + m.Groups[3].Value : "")) : d.DevicePath;
            Console.WriteLine($"   {where,-14} feat={fl,3} in={il,3} out={ol,3}");
            if (fl < RazerPacket.BufferLen) continue;
            using var hd = BladeDevice.OpenPath(d.DevicePath);
            if (hd == null) { Console.WriteLine("      cannot open"); continue; }
            bool any = false;
            foreach (byte tid in new byte[] { 0x1F, 0x3F, 0xFF, 0x9F, 0x08, 0x88 })
            {
                var r = hd.Transact(RazerPacket.Create(tid, 0x00, 0x81, 0x02));
                if (r != null && r.Status == RazerPacket.StatusCode.Success)
                {
                    any = true;
                    Console.WriteLine($"      tid 0x{tid:X2}: ANSWERS  firmware v{r.Args[0]}.{r.Args[1]}  (response tid 0x{r.TransactionId:X2})");
                    var sr = hd.Transact(RazerPacket.Create(tid, 0x00, 0x82, 0x16));
                    if (sr?.Status == RazerPacket.StatusCode.Success) Console.WriteLine($"      serial: {System.Text.Encoding.ASCII.GetString(sr.Args, 0, 22).TrimEnd('\0')}");
                    var dm = hd.Transact(RazerPacket.Create(tid, 0x00, 0x84, 0x02, 0x00, 0x00));
                    Console.WriteLine($"      device mode GET (0x00/0x84): {(dm == null ? "no answer" : dm.Status == RazerPacket.StatusCode.Success ? $"mode={dm.Args[0]} param={dm.Args[1]}" : dm.StatusName)}");
                    var b = hd.Transact(RazerPacket.Create(tid, 0x0F, 0x84, 0x03, 0x01, 0x05, 0x00));
                    Console.WriteLine($"      ext brightness GET (0x0F/0x84 varstore led05): {(b == null ? "no answer" : b.Status == RazerPacket.StatusCode.Success ? b.Args[2].ToString() : b.StatusName)}");
                    var bb = hd.Transact(RazerPacket.Create(tid, 0x0E, 0x84, 0x02, 0x01, 0x00));
                    Console.WriteLine($"      blade brightness GET (0x0E/0x84): {(bb == null ? "no answer" : bb.Status == RazerPacket.StatusCode.Success ? bb.Args[1].ToString() : bb.StatusName)}");
                    var lb = hd.Transact(RazerPacket.Create(tid, 0x03, 0x83, 0x03, 0x01, 0x05, 0x00));
                    Console.WriteLine($"      std led brightness GET (0x03/0x83 varstore led05): {(lb == null ? "no answer" : lb.Status == RazerPacket.StatusCode.Success ? lb.Args[2].ToString() : lb.StatusName)}");
                    break;
                }
                Console.WriteLine($"      tid 0x{tid:X2}: {(r == null ? "no answer" : r.StatusName)}");
            }
            if (!any)
            {
                // Some accessories (OpenRGB: Leviathan V2 X, extended matrix, tid 0x1F) may not implement the
                // firmware GET. Fall back to lighting GETs, which every extended-matrix device answers.
                foreach (byte tid in new byte[] { 0x1F, 0x3F, 0xFF, 0x9F })
                {
                    var b = hd.Transact(RazerPacket.Create(tid, 0x0F, 0x84, 0x03, 0x01, 0x00, 0x00));
                    var dm = hd.Transact(RazerPacket.Create(tid, 0x00, 0x84, 0x02, 0x00, 0x00));
                    Console.WriteLine($"      fallback tid 0x{tid:X2}: ext brightness GET led00 -> {(b == null ? "no answer" : b.Status == RazerPacket.StatusCode.Success ? b.Args[2].ToString() : b.StatusName)}; device mode GET -> {(dm == null ? "no answer" : dm.Status == RazerPacket.StatusCode.Success ? dm.Args[0].ToString() : dm.StatusName)}");
                    if (b != null || dm != null) { any = true; break; }
                }
                if (!any) Console.WriteLine("      -> nothing answered on this collection");
                // Silent device: dump the report descriptor's report ids and try the raw write on a R/W handle.
                try
                {
                    var rd = d.GetReportDescriptor();
                    foreach (var rep in rd.Reports)
                        Console.WriteLine($"      descriptor report: type={rep.ReportType} id=0x{rep.ReportID:X2} len={rep.Length}");
                }
                catch (Exception ex) { Console.WriteLine("      descriptor: " + ex.Message); }
                using var rw = BladeDevice.OpenPathReadWrite(d.DevicePath, s => Console.WriteLine(s));
                if (rw != null)
                {
                    var pkt = RazerPacket.Create(0x1F, 0x00, 0x81, 0x02).ToBuffer();
                    var payload = new byte[90]; Array.Copy(pkt, 1, payload, 0, 90);
                    foreach (byte rid in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 })
                    {
                        bool ok = rw.SetFeatureRaw(rid, payload, out int err);
                        string got = "";
                        if (ok)
                        {
                            Thread.Sleep(40);
                            if (rw.GetFeatureRaw(rid, out var back, out int err2))
                                got = " get ok: " + string.Join(" ", back.Take(12).Select(x => x.ToString("X2")));
                            else got = $" get failed (win32 {err2})";
                        }
                        Console.WriteLine($"      raw report id 0x{rid:X2}: set {(ok ? "OK" : $"failed (win32 {err})")}{got}");
                    }
                }
            }
        }
    }
    return 0;
}

// --getsweep <pid> <tid> <class> <ds> [arg bytes...] : read-only discovery: every GET command id (0x80-0xFF) of one class.
if (args.Length > 4 && args[0] == "--getsweep")
{
    int pid = Convert.ToInt32(args[1], 16);
    byte tid = Convert.ToByte(args[2], 16), cls = Convert.ToByte(args[3], 16), ds = Convert.ToByte(args[4], 16);
    var a = args.Skip(5).Select(x => Convert.ToByte(x, 16)).ToArray();
    var info = RazerEnumerator.Enumerate().FirstOrDefault(i => i.Pid == pid && i.SpeaksProtocol);
    if (info == null) { Console.WriteLine("no controllable device with that pid"); return 2; }
    using var d = BladeDevice.OpenPath(info.ControlPath!, null, info.ReportId);
    if (d == null) { Console.WriteLine("cannot open"); return 2; }
    int ok = 0, noans = 0;
    for (int cmd = 0x80; cmd <= 0xFF; cmd++)
    {
        var r = d.Transact(RazerPacket.Create(tid, cls, (byte)cmd, ds, a));
        if (r == null) { noans++; continue; }
        if (r.Status == RazerPacket.StatusCode.Success)
        {
            ok++;
            Console.WriteLine($"  class 0x{cls:X2} cmd 0x{cmd:X2}: SUCCESS ds {r.DataSize}  args {BitConverter.ToString(r.Args, 0, 16)}");
        }
        else if (r.Status != RazerPacket.StatusCode.NotSupported)
            Console.WriteLine($"  class 0x{cls:X2} cmd 0x{cmd:X2}: {r.StatusName}");
    }
    Console.WriteLine($"class 0x{cls:X2}: {ok} GETs answered SUCCESS, {noans} silent");
    return 0;
}

// --kbd-watch <seconds>: Raw Input watcher. Classifies every keystroke Windows receives by origin:
// hardware from the Huntsman (VID_1532&PID_0266), hardware from another keyboard, or injected (SendInput,
// hDevice 0). Output: kbd-watch.txt next to BladeCtl.exe. Run with the analog engine on to see whether the
// firmware still types in driver mode.
if (args.Length > 1 && args[0] == "--kbd-watch")
{
    int secs = Math.Clamp(int.Parse(args[1]), 1, 300);
    string outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "kbd-watch.txt"));
    using var sw = new StreamWriter(outPath, false) { AutoFlush = true };
    void L(string s) { Console.WriteLine(s); sw.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
    L($"watching keyboard input for {secs} s");
    var counts = RawInputWatch.Run(secs, L);
    L("summary: " + string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
    return 0;
}

// --analog-capture <seconds>: put the Huntsman V2 Analog in driver mode (3), read its analog stream (HID input
// report 7: pairs of analog key id + depth 0-255), log every change plus any DIGITAL key events Windows still sees,
// then restore normal mode (0). Output: analog-capture.txt next to BladeCtl.exe.
if (args.Length > 1 && args[0] == "--analog-capture")
{
    int secs = Math.Clamp(int.Parse(args[1]), 1, 120);
    string outPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "analog-capture.txt"));
    using var sw = new StreamWriter(outPath, false) { AutoFlush = true };
    void L(string s) { Console.WriteLine(s); sw.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
    var info = RazerEnumerator.Enumerate().FirstOrDefault(i => i.Pid == 0x0266 && i.SpeaksProtocol);
    if (info == null) { L("no Huntsman V2 Analog control interface"); return 2; }
    HidSharp.HidDevice? an = null;
    foreach (var hd in HidSharp.DeviceList.Local.GetHidDevices(0x1532, 0x0266))
    {
        try
        {
            var rd = hd.GetReportDescriptor();
            if (rd.Reports.Any(r => r.ReportType == HidSharp.Reports.ReportType.Input && r.ReportID == 7 && r.Length == 24)) { an = hd; break; }
        }
        catch { }
    }
    if (an == null) { L("analog collection (input report 7, 24 bytes) not found"); return 2; }
    L($"analog collection: {an.DevicePath}");
    if (!an.TryOpen(out var st)) { L("cannot open the analog collection for reading"); return 2; }
    using var actl = RazerEnumerator.Open(info);
    if (actl == null) { L("cannot open control interface"); return 2; }
    L($"device mode before: {actl.GetDeviceMode()}");
    bool ok = actl.SetDeviceMode(3); Thread.Sleep(100);
    L($"set DRIVER mode 3 -> {(ok ? "acked" : "REJECTED")}, reads back {actl.GetDeviceMode()}. Capturing {secs} s - type on the Huntsman now.");
    var keyState = new bool[256];
    int packets = 0, withKeys = 0, digital = 0;
    var end = DateTime.Now.AddSeconds(secs);
    st.ReadTimeout = 20;
    var buf = new byte[Math.Max(24, an.GetMaxInputReportLength())];
    string lastLog = "";
    try
    {
        while (DateTime.Now < end)
        {
            for (int vk = 8; vk < 256; vk++)
            {
                bool down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down != keyState[vk]) { keyState[vk] = down; digital++; L($"DIGITAL vk 0x{vk:X2} {(down ? "down" : "up")}"); }
            }
            int n;
            try { n = st.Read(buf, 0, buf.Length); } catch (TimeoutException) { continue; }
            if (n <= 0) continue;
            packets++;
            var pairs = new List<string>();
            for (int i = 1; i + 1 < n; i += 2) { if (buf[i] == 0) break; pairs.Add($"{buf[i]}:{buf[i + 1]}"); }
            if (pairs.Count > 0) withKeys++;
            string line = pairs.Count == 0 ? "(no keys)" : string.Join(" ", pairs);
            if (line != lastLog) { L($"REPORT id {buf[0]} len {n} {line}"); lastLog = line; }
        }
    }
    finally
    {
        bool back = actl.SetDeviceMode(0); Thread.Sleep(100);
        L($"restored NORMAL mode 0 -> {(back ? "acked" : "REJECTED")}, reads back {actl.GetDeviceMode()}. packets {packets}, with keys {withKeys}, digital events {digital}");
        st.Dispose();
    }
    return 0;
}

// --rz <pid> <tid> <class> <cmd> <ds> [arg bytes...] : one raw transaction (all hex), prints status + args.
if (args.Length > 5 && args[0] == "--rz")
{
    int pid = Convert.ToInt32(args[1], 16);
    byte tid = Convert.ToByte(args[2], 16), cls = Convert.ToByte(args[3], 16), cmd = Convert.ToByte(args[4], 16), ds = Convert.ToByte(args[5], 16);
    var a = args.Skip(6).Select(x => Convert.ToByte(x, 16)).ToArray();
    var info = RazerEnumerator.Enumerate().FirstOrDefault(i => i.Pid == pid && i.SpeaksProtocol);
    if (info == null) { Console.WriteLine("no controllable device with that pid"); return 2; }
    using var d = BladeDevice.OpenPath(info.ControlPath!, null, info.ReportId);
    if (d == null) { Console.WriteLine("cannot open"); return 2; }
    var r = d.Transact(RazerPacket.Create(tid, cls, cmd, ds, a));
    if (r == null) { Console.WriteLine("no answer"); return 1; }
    Console.WriteLine($"status {r.StatusName}  ds {r.DataSize}  args: {BitConverter.ToString(r.Args, 0, Math.Max(1, Math.Min(80, (int)Math.Max(r.DataSize, ds))))}");
    return r.Status == RazerPacket.StatusCode.Success ? 0 : 1;
}

// --hidsweep <pid> [--sweep]: every HID collection of one product with usage pages and report ids from
// its descriptor; with --sweep, a read-only GetFeature try on every report id of each feature-capable collection.
if (args.Length > 1 && args[0] == "--hidsweep")
{
    int pid = Convert.ToInt32(args[1], 16);
    bool sweep = args.Length > 2 && args[2] == "--sweep";
    foreach (var hd in HidSharp.DeviceList.Local.GetHidDevices(0x1532, pid).OrderBy(h => h.DevicePath))
    {
        string path = hd.DevicePath;
        string mi = System.Text.RegularExpressions.Regex.Match(path, "mi_0[0-9]").Value;
        string col = System.Text.RegularExpressions.Regex.Match(path, "col0[0-9]").Value;
        int f = 0, i = 0, o = 0;
        try { f = hd.GetMaxFeatureReportLength(); i = hd.GetMaxInputReportLength(); o = hd.GetMaxOutputReportLength(); } catch { }
        string usages = "", reports = "";
        try
        {
            var rd = hd.GetReportDescriptor();
            usages = string.Join(" | ", rd.DeviceItems.Select(di => string.Join(",", di.Usages.GetAllValues().Select(u => $"0x{u:X8}"))));
            reports = string.Join(" ", rd.Reports.Select(r => $"{r.ReportType}#{r.ReportID}:{r.Length}"));
        }
        catch (Exception ex) { usages = "desc? " + ex.Message; }
        Console.WriteLine($"{mi} {col,-5} feat={f,3} in={i,3} out={o,3}  usages {usages}");
        Console.WriteLine($"      reports: {reports}");
        if (sweep && f > 0)
        {
            if (!hd.TryOpen(out var st)) { Console.WriteLine("      cannot open"); continue; }
            using (st)
            {
                st.ReadTimeout = 300;
                int answered = 0;
                for (int id = 0; id < 256; id++)
                {
                    var buf = new byte[f]; buf[0] = (byte)id;
                    try
                    {
                        st.GetFeature(buf);
                        answered++;
                        Console.WriteLine($"      feature id 0x{id:X2} answers: {BitConverter.ToString(buf, 0, Math.Min(32, f))}");
                    }
                    catch { }
                }
                Console.WriteLine($"      {answered} feature ids answered");
            }
        }
    }
    return 0;
}

// --explore: read-only sweep of every documented GET on every controllable device, to see which
// registers this hardware actually implements before any UI is built for them.
if (args.Length > 0 && args[0] == "--explore")
{
    foreach (var info in RazerEnumerator.Enumerate())
    {
        if (!info.SpeaksProtocol) { Console.WriteLine($"== {info.Name} [{info.Pid:X4}]: no protocol"); continue; }
        Console.WriteLine($"== {info.Name} [{info.Pid:X4}] tid 0x{info.Tid:X2} rid 0x{info.ReportId:X2} fw {info.Firmware}");
        using var d = BladeDevice.OpenPath(info.ControlPath!, null, info.ReportId);
        if (d == null) { Console.WriteLine("   cannot open"); continue; }
        RazerPacket? Q(byte tid, byte cls, byte cmd, byte ds, params byte[] a) => d.Transact(RazerPacket.Create(tid, cls, cmd, ds, a));
        string S(RazerPacket? r, int n = 6) => r == null ? "no answer" : r.Status != RazerPacket.StatusCode.Success ? r.StatusName : string.Join(" ", r.Args.Take(n).Select(x => x.ToString("X2")));
        byte t = info.Tid;
        Console.WriteLine($"   device mode 0x00/0x84: {S(Q(t, 0x00, 0x84, 0x02, 0x00, 0x00), 2)}   keyboard layout 0x00/0x86: {S(Q(t, 0x00, 0x86, 0x02), 2)}");
        Console.WriteLine($"   polling 0x00/0x85: {S(Q(t, 0x00, 0x85, 0x01), 2)}   hyperpolling 0x00/0xC0: {S(Q(t, 0x00, 0xC0, 0x01), 3)}");
        Console.WriteLine($"   game LED 0x03/0x80 [VAR,08] tidFF: {S(Q(0xFF, 0x03, 0x80, 0x03, 0x01, 0x08, 0x00), 3)}   tid{t:X2}: {S(Q(t, 0x03, 0x80, 0x03, 0x01, 0x08, 0x00), 3)}");
        Console.WriteLine($"   logo LED state 0x03/0x80 [VAR,04] tidFF: {S(Q(0xFF, 0x03, 0x80, 0x03, 0x01, 0x04, 0x00), 3)}   logo effect 0x03/0x82: {S(Q(0xFF, 0x03, 0x82, 0x03, 0x01, 0x04, 0x00), 3)}");
        Console.WriteLine($"   std brightness 0x03/0x83 [VAR,05] tidFF: {S(Q(0xFF, 0x03, 0x83, 0x03, 0x01, 0x05, 0x00), 3)}   ext brightness led00: {S(Q(t, 0x0F, 0x84, 0x03, 0x01, 0x00, 0x00), 3)}   led05: {S(Q(t, 0x0F, 0x84, 0x03, 0x01, 0x05, 0x00), 3)}");
        Console.WriteLine($"   ext effect GET 0x0F/0x82 led00: {S(Q(t, 0x0F, 0x82, 0x06, 0x01, 0x00, 0x00), 9)}   led05: {S(Q(t, 0x0F, 0x82, 0x06, 0x01, 0x05, 0x00), 9)}");
        if (info.IsBlade)
        {
            Console.WriteLine($"   power z1 0x0d/0x82: {S(Q(0x1F, 0x0D, 0x82, 0x04, 0x00, 0x01, 0x00, 0x00), 4)}   z2: {S(Q(0x1F, 0x0D, 0x82, 0x04, 0x00, 0x02, 0x00, 0x00), 4)}");
            Console.WriteLine($"   cpu boost 0x0d/0x87 [0,1]: {S(Q(0x1F, 0x0D, 0x87, 0x03, 0x00, 0x01, 0x00), 3)}   gpu boost [0,2]: {S(Q(0x1F, 0x0D, 0x87, 0x03, 0x00, 0x02, 0x00), 3)}");
            Console.WriteLine($"   fan z1 0x0d/0x81: {S(Q(0x1F, 0x0D, 0x81, 0x03, 0x00, 0x01, 0x00), 3)}   BHO 0x07/0x92: {S(Q(0x1F, 0x07, 0x92, 0x01, 0x00), 1)}");
            Console.WriteLine($"   blade brightness 0x0E/0x84: {S(Q(0xFF, 0x0E, 0x84, 0x02, 0x01, 0x00), 2)}");
        }
    }
    return 0;
}

// --leviathan2: Windows' own caps for the speaker, a feature-id sweep, control-endpoint output writes and
// control-endpoint input reads. Nothing here needs a driver install.
if (args.Length > 0 && args[0] == "--leviathan2")
{
    var lvs = DeviceList.Local.GetHidDevices(0x1532, 0x054A).ToList();
    Console.WriteLine($"{lvs.Count} HID collections for 054A");
    foreach (var lv in lvs)
    {
        Console.WriteLine($"-- {lv.DevicePath}");
        using var hd2 = BladeDevice.OpenPathReadWrite(lv.DevicePath, s => Console.WriteLine(s));
        if (hd2 == null) continue;
        var caps = hd2.GetCaps();
        if (caps is { } c)
            Console.WriteLine($"   caps: usagePage=0x{c.UsagePage:X4} usage=0x{c.Usage:X4} in={c.InputReportByteLength} out={c.OutputReportByteLength} feat={c.FeatureReportByteLength} featButtonCaps={c.NumberFeatureButtonCaps} featValueCaps={c.NumberFeatureValueCaps} outValueCaps={c.NumberOutputValueCaps} inValueCaps={c.NumberInputValueCaps}");
        else Console.WriteLine("   caps: unavailable");

        int featLen = caps?.FeatureReportByteLength ?? 0;
        if (featLen > 0)
        {
            Console.WriteLine($"   feature-id sweep (len {featLen}):");
            var getFw = RazerPacket.Create(0x1F, 0x00, 0x81, 0x02).ToBuffer();
            var payload = new byte[featLen - 1]; Array.Copy(getFw, 1, payload, 0, Math.Min(payload.Length, 90));
            int okCount = 0;
            for (int id = 0; id <= 255; id++)
            {
                if (hd2.SetFeatureRaw((byte)id, payload, out int err))
                {
                    okCount++;
                    Thread.Sleep(40);
                    string back = hd2.GetFeatureRaw((byte)id, out var b, out int e2) ? string.Join(" ", b.Take(14).Select(x => x.ToString("X2"))) : $"get failed {e2}";
                    Console.WriteLine($"      id 0x{id:X2}: SET OK, GET -> {back}");
                }
            }
            if (okCount == 0) Console.WriteLine("      no feature report id accepted a write");
        }

        int outLen = caps?.OutputReportByteLength ?? 92;
        int inLen = caps?.InputReportByteLength ?? 16;
        byte[] Frame(RazerPacket p, int layout, byte rid)
        {
            var full = p.ToBuffer();
            var o = new byte[outLen]; o[0] = rid;
            if (layout == 0) Array.Copy(full, 0, o, 1, Math.Min(91, outLen - 1));
            else Array.Copy(full, 1, o, 1, Math.Min(90, outLen - 1));
            return o;
        }
        void ReadInputs(string when)
        {
            foreach (byte rid in new byte[] { 0x01, 0x05 })
            {
                if (hd2.GetInputReportRaw(rid, inLen, out var b, out int e))
                    Console.WriteLine($"      {when} GetInputReport 0x{rid:X2}: {string.Join(" ", b.Take(16).Select(x => x.ToString("X2")))}");
                else Console.WriteLine($"      {when} GetInputReport 0x{rid:X2}: failed (win32 {e})");
            }
        }
        ReadInputs("before");
        var tests = new (string Name, RazerPacket P)[]
        {
            ("firmware GET tid1F", RazerPacket.Create(0x1F, 0x00, 0x81, 0x02)),
            ("ext static RED tid1F", RazerPacket.Create(0x1F, 0x0F, 0x02, 0x09, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 255, 0, 0)),
            ("ext static GREEN tid3F", RazerPacket.Create(0x3F, 0x0F, 0x02, 0x09, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0, 255, 0)),
            ("ext brightness 255 tid1F", RazerPacket.Create(0x1F, 0x0F, 0x04, 0x03, 0x00, 0x00, 255)),
            ("ext static WHITE tid1F varstore", RazerPacket.Create(0x1F, 0x0F, 0x02, 0x09, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 255, 255, 255)),
        };
        foreach (var layout in new[] { 0, 1 })
        {
            Console.WriteLine(layout == 0 ? "   control-endpoint output, layout [03][00][pkt]" : "   control-endpoint output, layout [03][pkt][00]");
            foreach (var t in tests)
            {
                bool ok = hd2.SetOutputReportRaw(Frame(t.P, layout, 0x03), out int err);
                Console.WriteLine($"      {DateTime.Now:HH:mm:ss} {t.Name}: {(ok ? "SET_REPORT OK" : $"failed (win32 {err})")}");
                if (ok) { Thread.Sleep(120); ReadInputs("after"); }
                Thread.Sleep(2500);
            }
        }
    }
    return 0;
}

// --leviathan: the Leviathan V2 X has no feature reports; its descriptor is OUTPUT 0x03 (92 bytes) +
// INPUT 0x01/0x05 (16 bytes). Send the Razer packet as output report 0x03 and read whatever comes back.
if (args.Length > 0 && args[0] == "--leviathan")
{
    var lv = DeviceList.Local.GetHidDevices(0x1532, 0x054A).FirstOrDefault(d => { try { return d.GetMaxOutputReportLength() >= 92; } catch { return false; } });
    if (lv == null) { Console.WriteLine("Leviathan V2 X output-report collection not found"); return 1; }
    Console.WriteLine($"opening {lv.DevicePath}");
    var cfg = new OpenConfiguration(); cfg.SetOption(OpenOption.Exclusive, false);
    using var stream = lv.Open(cfg);
    stream.ReadTimeout = 600; stream.WriteTimeout = 1000;
    byte[] Frame(RazerPacket p, int layout)
    {
        var full = p.ToBuffer();
        var o = new byte[92]; o[0] = 0x03;
        if (layout == 0) Array.Copy(full, 0, o, 1, 91);
        else Array.Copy(full, 1, o, 1, 90);
        return o;
    }
    string Rx()
    {
        try { var b = new byte[64]; int n = stream.Read(b, 0, b.Length); return n > 0 ? string.Join(" ", b.Take(n).Select(x => x.ToString("X2"))) : "(empty)"; }
        catch (TimeoutException) { return "(no input report within 600 ms)"; }
        catch (Exception ex) { return "read error: " + ex.Message; }
    }
    var tests = new (string Name, RazerPacket P)[]
    {
        ("firmware GET tid1F", RazerPacket.Create(0x1F, 0x00, 0x81, 0x02)),
        ("ext static RED led0 nostore tid1F", RazerPacket.Create(0x1F, 0x0F, 0x02, 0x09, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 255, 0, 0)),
        ("ext brightness 255 led0 nostore tid1F", RazerPacket.Create(0x1F, 0x0F, 0x04, 0x03, 0x00, 0x00, 255)),
        ("ext spectrum led0 nostore tid1F", RazerPacket.Create(0x1F, 0x0F, 0x02, 0x06, 0x00, 0x00, 0x03)),
        ("ext static WHITE led0 varstore tid1F", RazerPacket.Create(0x1F, 0x0F, 0x02, 0x09, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 255, 255, 255)),
    };
    foreach (var layout in new[] { 0, 1 })
    {
        Console.WriteLine(layout == 0 ? "--- layout [03][00][pkt]" : "--- layout [03][pkt][00]");
        foreach (var t in tests)
        {
            try { stream.Write(Frame(t.P, layout)); Console.WriteLine($"  {DateTime.Now:HH:mm:ss} {t.Name}: written; reply {Rx()}"); }
            catch (Exception ex) { Console.WriteLine($"  {t.Name}: write failed: {ex.Message}"); }
            Thread.Sleep(2500);
        }
    }
    return 0;
}

Console.WriteLine("=== BladeProbe: Razer Blade 15 Advanced (Mid 2021) 1532:0276 ===");
var dev = BladeDevice.Find(log);
if (dev == null)
{
    Console.WriteLine("FATAL: no HID collection answered the benign power-mode GET. Nothing was changed.");
    return 1;
}

using var ctl = new BladeController(dev, log);
Console.WriteLine($"\nUsing: {dev.DevicePath}\n");

// ---------- read-only dump (always) ----------
Console.WriteLine("--- Read-only state dump ---");
Console.WriteLine($"Firmware:   {ctl.GetFirmwareVersion() ?? "(no answer)"}");
Console.WriteLine($"Serial:     {ctl.GetSerial() ?? "(no answer)"}");
for (byte z = 1; z <= 2; z++)
{
    var ps = ctl.GetPowerState(z);
    var rpm = ctl.GetFanRpm(z);
    Console.WriteLine($"Zone {z}:     mode={(ps.HasValue ? ps.Value.Mode.ToString() : "?")} manualFan={(ps.HasValue ? ps.Value.ManualFan.ToString() : "?")} fanSetpoint={(rpm.HasValue ? rpm.Value + " rpm (0 = auto)" : "?")}");
}
var bho = ctl.GetBho();
Console.WriteLine($"BHO:        {(bho.HasValue ? $"enabled={bho.Value.Enabled} threshold={bho.Value.ThresholdPercent}%" : "NOT SUPPORTED or no answer")}");
var bri = ctl.GetBrightness();
Console.WriteLine($"Brightness: {(bri.HasValue ? bri.Value.ToString() : "(no answer)")}");
Console.WriteLine();

if (args.Length == 0)
{
    Console.WriteLine("Read-only probe complete. No state was changed.");
    return 0;
}

switch (args[0])
{
    // Empirical A/B of every candidate "solid colour" command family. Each candidate gets a distinct
    // colour and a hold time; the human eye decides. Device acks are printed but prove nothing on
    // this hardware.
    case "--rgb-test":
    {
        const byte TidChroma = 0xFF, TidSystem = 0x1F;
        const byte VARSTORE = 0x01, BACKLIGHT_LED = 0x05;
        int hold = args.Length > 1 && int.TryParse(args[1], out var h) ? h : 6;

        // make sure the panel is lit while we test
        ctl.SetBrightness(255);

        var candidates = new (string Name, string Colour, Func<bool> Apply)[]
        {
            ("A  standard static      (0x03/0x0A ds4, tid FF)", "RED",
                () => Send(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x04, 0x06, 255, 0, 0))),

            ("B  standard static      (0x03/0x0A ds4, tid 1F)", "GREEN",
                () => Send(RazerPacket.Create(TidSystem, 0x03, 0x0A, 0x04, 0x06, 0, 255, 0))),

            ("C  extended static VARSTORE led05 (0x0F/0x02 ds9, tid FF)", "BLUE",
                () => Send(RazerPacket.Create(TidChroma, 0x0F, 0x02, 0x09,
                        VARSTORE, BACKLIGHT_LED, 0x01, 0x00, 0x00, 0x01, 0, 0, 255))),

            ("D  extended static VARSTORE led05 (0x0F/0x02 ds9, tid 1F)", "YELLOW",
                () => Send(RazerPacket.Create(TidSystem, 0x0F, 0x02, 0x09,
                        VARSTORE, BACKLIGHT_LED, 0x01, 0x00, 0x00, 0x01, 255, 255, 0))),

            ("E  custom frame + apply (0x03/0x0B then 0x03/0x0A ds2)", "MAGENTA",
                () => SendCustomFrame(255, 0, 255)),

            ("F  NORMAL mode (0x00/0x04 [0,0] tid 1F) then standard static tid FF", "CYAN",
                () => { var m = dev.Transact(RazerPacket.Create(TidSystem, 0x00, 0x04, 0x02, 0x00, 0x00));
                        Console.WriteLine($"     set mode 0 -> {(m == null ? "no answer" : m.StatusName)}"); Thread.Sleep(150);
                        var g = dev.Transact(RazerPacket.Create(TidSystem, 0x00, 0x84, 0x02, 0x00, 0x00));
                        Console.WriteLine($"     mode now {(g == null ? "?" : g.Args[0].ToString())}");
                        return Send(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x04, 0x06, 0, 255, 255)); }),

            ("H  still NORMAL mode: extended static VARSTORE led05 tid FF", "PURPLE",
                () => Send(RazerPacket.Create(TidChroma, 0x0F, 0x02, 0x09,
                        VARSTORE, BACKLIGHT_LED, 0x01, 0x00, 0x00, 0x01, 128, 0, 255))),

            ("I  back to DRIVER mode (0x00/0x04 [3,0]) then standard static tid FF", "PINK",
                () => { var m = dev.Transact(RazerPacket.Create(TidSystem, 0x00, 0x04, 0x02, 0x03, 0x00));
                        Console.WriteLine($"     set mode 3 -> {(m == null ? "no answer" : m.StatusName)}"); Thread.Sleep(150);
                        return Send(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x04, 0x06, 255, 0, 128)); }),

            ("G  extended static NOSTORE led00 (0x0F/0x02 ds9, tid FF)", "ORANGE",
                () => Send(RazerPacket.Create(TidChroma, 0x0F, 0x02, 0x09,
                        0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 255, 110, 0))),

            ("W  finish: ext static VARSTORE led05 tid FF, then standard static, WHITE", "WHITE",
                () => { bool a = Send(RazerPacket.Create(TidChroma, 0x0F, 0x02, 0x09, VARSTORE, BACKLIGHT_LED, 0x01, 0x00, 0x00, 0x01, 255, 255, 255));
                        bool b = Send(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x04, 0x06, 255, 255, 255)); return a || b; }),
        };

        Console.WriteLine($"RGB COMMAND TEST — {candidates.Length} candidates, {hold}s each.");
        Console.WriteLine("WATCH THE KEYBOARD and note which colours actually appear.\n");

        foreach (var (name, colour, apply) in candidates)
        {
            Console.WriteLine($"  -> {name}   expect: {colour}");
            bool ok = apply();
            Console.WriteLine($"     {DateTime.Now:HH:mm:ss} device ack: {(ok ? "SUCCESS" : "no/failed response")}");
            Console.Out.Flush();
            Thread.Sleep(hold * 1000);
        }

        Console.WriteLine("\nDone. Which colour(s) did you actually SEE?");
        Console.WriteLine("  RED=A  GREEN=B  BLUE=C  YELLOW=D  MAGENTA=E  CYAN=F  ORANGE=G  WHITE=finish");
        return 0;

        bool Send(RazerPacket p)
        {
            var r = dev.Transact(p);
            return r?.Status == RazerPacket.StatusCode.Success;
        }

        // Paint every row of the matrix, then switch the device to CUSTOMFRAME.
        bool SendCustomFrame(byte r, byte g, byte b)
        {
            const int rows = 6, cols = 16;
            bool all = true;
            for (byte row = 0; row < rows; row++)
            {
                // 0x03/0x0B: [frameId 0xFF, row, startCol, stopCol, rgb...]
                var argsBuf = new byte[4 + cols * 3];
                argsBuf[0] = 0xFF; argsBuf[1] = row; argsBuf[2] = 0; argsBuf[3] = cols - 1;
                for (int c = 0; c < cols; c++)
                {
                    argsBuf[4 + c * 3] = r; argsBuf[5 + c * 3] = g; argsBuf[6 + c * 3] = b;
                }
                all &= Send(RazerPacket.Create(TidChroma, 0x03, 0x0B, 0x46, argsBuf));
            }
            // apply the frame: effect CUSTOMFRAME(0x05), arg1 = storage
            all &= Send(RazerPacket.Create(TidChroma, 0x03, 0x0A, 0x02, 0x05, VARSTORE));
            return all;
        }
    }

    case "--write-noop":
    {
        var ps = ctl.GetPowerState(1);
        if (ps == null) { Console.WriteLine("Cannot read current mode; aborting no-op write."); return 1; }
        var mode = ps.Value.Mode is PerfMode.Balanced or PerfMode.Gaming ? ps.Value.Mode : PerfMode.Balanced;
        Console.WriteLine($"Re-writing current mode {mode} (auto fan preserved={!ps.Value.ManualFan})...");
        bool ok = ctl.SetMode(mode);
        Console.WriteLine($"Write result: {(ok ? "SUCCESS" : "FAILED")}");
        Verify();
        break;
    }

    case "--mode":
    {
        var mode = args[1].Equals("gaming", StringComparison.OrdinalIgnoreCase) ? PerfMode.Gaming : PerfMode.Balanced;
        Console.WriteLine($"Setting mode {mode} (auto fan)...");
        Console.WriteLine($"Result: {(ctl.SetMode(mode) ? "SUCCESS" : "FAILED")}");
        Verify();
        break;
    }

    case "--bho":
    {
        bool on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        int pct = args.Length > 2 ? int.Parse(args[2]) : 80;
        Console.WriteLine($"Setting BHO {(on ? "ON" : "OFF")} @ {pct}%...");
        Console.WriteLine($"Result: {(ctl.SetBho(on, pct) ? "SUCCESS" : "FAILED")}");
        Thread.Sleep(300);
        var b2 = ctl.GetBho();
        Console.WriteLine($"Read-back: {(b2.HasValue ? $"enabled={b2.Value.Enabled} threshold={b2.Value.ThresholdPercent}%" : "no answer")}");
        break;
    }

    case "--fan-test":
    {
        int rpm = BladeController.ClampRpm(int.Parse(args[1]));
        int seconds = int.Parse(args[2]);
        var mode = PerfMode.Balanced;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCtrl+C -> RESTORING AUTO FAN");
            ctl.RestoreAutoFan(mode);
            Environment.Exit(2);
        };

        Console.WriteLine($"MANUAL FAN TEST: {rpm} rpm for {seconds}s (clamped to [{BladeController.FanMinRpm},{BladeController.FanMaxRpm}])");
        Console.WriteLine($"Baseline temps: {Temps()}");
        if (!ctl.SetManualFan(mode, rpm))
        {
            Console.WriteLine("SetManualFan FAILED -> restoring auto");
            ctl.RestoreAutoFan(mode);
            return 1;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(5000);
                var r1 = ctl.GetFanRpm(1); var r2 = ctl.GetFanRpm(2);
                Console.WriteLine($"t={sw.Elapsed.TotalSeconds,4:F0}s  setpoint z1={r1} z2={r2}  temps: {Temps()}");
                if (TempsMaxC() is double t and > 92)
                {
                    Console.WriteLine($"ABORT: temperature {t:F0}C > 92C safety limit -> RESTORING AUTO");
                    break;
                }
            }
        }
        finally
        {
            Console.WriteLine("Test done -> RESTORING AUTO FAN");
            bool ok = ctl.RestoreAutoFan(mode);
            Console.WriteLine($"Restore auto: {(ok ? "SUCCESS" : "FAILED — VERIFY MANUALLY")}");
            Verify();
        }
        break;
    }

    case "--restore-auto":
        Console.WriteLine($"Restore auto fan + balanced: {(ctl.RestoreAutoFan(PerfMode.Balanced) ? "SUCCESS" : "FAILED")}");
        Verify();
        break;

    case "--rgb":
        Console.WriteLine($"Static color ({args[1]},{args[2]},{args[3]}): " +
            (ctl.SetStaticColor(byte.Parse(args[1]), byte.Parse(args[2]), byte.Parse(args[3])) ? "SUCCESS" : "FAILED"));
        break;

    case "--spectrum":
        Console.WriteLine($"Spectrum: {(ctl.SetSpectrum() ? "SUCCESS" : "FAILED")}");
        break;

    case "--brightness":
        Console.WriteLine($"Brightness {args[1]}: {(ctl.SetBrightness(byte.Parse(args[1])) ? "SUCCESS" : "FAILED")}");
        Console.WriteLine($"Read-back: {ctl.GetBrightness()}");
        break;

    default:
        Console.WriteLine($"Unknown flag {args[0]}");
        return 1;
}

return 0;

void Verify()
{
    for (byte z = 1; z <= 2; z++)
    {
        var ps = ctl.GetPowerState(z);
        var rpm = ctl.GetFanRpm(z);
        Console.WriteLine($"Verify zone {z}: mode={ps?.Mode} manualFan={ps?.ManualFan} fanSetpoint={rpm}");
    }
}

string Temps()
{
    var parts = new List<string>();
    if (CpuTempC() is double c) parts.Add($"acpi={c:F0}C");
    if (GpuTempC() is double g) parts.Add($"gpu={g:F0}C");
    return parts.Count > 0 ? string.Join(" ", parts) : "(no sensors readable)";
}

double? TempsMaxC()
{
    double? m = null;
    foreach (var v in new[] { CpuTempC(), GpuTempC() })
        if (v.HasValue && (!m.HasValue || v > m)) m = v;
    return m;
}

double? CpuTempC()
{
    try
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            @"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        foreach (var o in searcher.Get())
            return (Convert.ToDouble(o["CurrentTemperature"]) - 2732) / 10.0;
    }
    catch { }
    return null;
}

double? GpuTempC()
{
    try
    {
        var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        if (p == null) return null;
        var outp = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(3000);
        return double.TryParse(outp, out var t) ? t : null;
    }
    catch { return null; }
}


static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
}


static class RawInputWatch
{
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct WNDCLASSEX { public uint cbSize, style; public WndProcDelegate lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref MSG msg);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

    static Action<string>? _log;
    static readonly Dictionary<IntPtr, string> _names = new();
    static readonly Dictionary<string, int> _counts = new();
    static WndProcDelegate? _proc;

    public static Dictionary<string, int> Run(int secs, Action<string> log)
    {
        _log = log; _counts.Clear();
        _proc = WndProc;
        var cls = new WNDCLASSEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = _proc, hInstance = GetModuleHandleW(null), lpszClassName = "BladeProbeRawInput" };
        if (RegisterClassExW(ref cls) == 0) { log("RegisterClassEx failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return _counts; }
        var hwnd = CreateWindowExW(0, "BladeProbeRawInput", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { log("CreateWindowEx failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return _counts; }
        var rid = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = 0x100, hwndTarget = hwnd } };
        if (!RegisterRawInputDevices(rid, 1, (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTDEVICE>())) { log("RegisterRawInputDevices failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return _counts; }
        var end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
            Thread.Sleep(2);
        }
        DestroyWindow(hwnd);
        return _counts;
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x00FF)
        {
            try
            {
                uint hdr = (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTHEADER>();
                uint size = 0;
                GetRawInputData(lParam, 0x10000003, IntPtr.Zero, ref size, hdr);
                if (size > 0)
                {
                    var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(lParam, 0x10000003, buf, ref size, hdr) == size)
                        {
                            var h = System.Runtime.InteropServices.Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                            if (h.dwType == 1)
                            {
                                var k = System.Runtime.InteropServices.Marshal.PtrToStructure<RAWKEYBOARD>(buf + (int)hdr);
                                string origin = Classify(h.hDevice);
                                bool up = (k.Flags & 1) != 0;
                                _counts[origin] = _counts.GetValueOrDefault(origin) + 1;
                                _log?.Invoke($"{origin,-12} vk 0x{k.VKey:X2} make 0x{k.MakeCode:X2} {(up ? "up  " : "down")} extra 0x{k.ExtraInformation:X}");
                            }
                        }
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
                }
            }
            catch (Exception ex) { _log?.Invoke("wndproc error: " + ex.Message); }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static string Classify(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return "INJECTED";
        if (!_names.TryGetValue(hDevice, out var name))
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, 0x20000007, IntPtr.Zero, ref size);
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)(size * 2 + 2));
            try { GetRawInputDeviceInfoW(hDevice, 0x20000007, buf, ref size); name = System.Runtime.InteropServices.Marshal.PtrToStringUni(buf) ?? ""; }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            _names[hDevice] = name;
            _log?.Invoke($"device {hDevice} = {name}");
        }
        return name.Contains("PID_0266", StringComparison.OrdinalIgnoreCase) ? "HUNTSMAN-HW" : "OTHER-HW";
    }
}
