using System.Runtime.InteropServices;
using System.Text;
using BladeCtl.Core;
using HidSharp;
using HidSharp.Reports;

namespace BladeCtl.Tray;

/// <summary>Adjustable-actuation settings for the Huntsman V2 Analog. Millimetres of key travel (0 = top, 4.0 = bottom).</summary>
public sealed class AnalogSettings
{
    public bool Enabled { get; set; }
    /// <summary>Depth at which a key counts as pressed.</summary>
    public double MakeMm { get; set; } = 1.5;
    /// <summary>Depth at which a pressed key counts as released (shallower than make gives hysteresis).</summary>
    public double BreakMm { get; set; } = 1.2;
    /// <summary>Rapid trigger: release as soon as the key rises this much from its deepest point, re-press as soon as it sinks this much again.</summary>
    public bool RapidTrigger { get; set; }
    public double RapidTriggerMm { get; set; } = 0.3;
    /// <summary>Per-key overrides, one per line: "W A S D = 1.0 / 0.8" (make / break in mm; break optional).</summary>
    public string Overrides { get; set; } = "";
    /// <summary>Software game mode while the engine runs (the firmware's Windows-key lock only applies in normal mode).</summary>
    public bool BlockWindowsKey { get; set; }

    // ---- controller mode: four keys become the left stick of a virtual Xbox 360 controller ----
    public bool Joystick { get; set; }
    /// <summary>Up, left, down, right.</summary>
    public string JoystickKeys { get; set; } = "W A S D";
    /// <summary>Depth below which the stick stays centred.</summary>
    public double JoystickDeadzoneMm { get; set; } = 0.3;
    /// <summary>Depth at which the stick is fully deflected.</summary>
    public double JoystickFullMm { get; set; } = 3.0;
    /// <summary>Also send the stick keys as keystrokes (off: games see only the stick).</summary>
    public bool JoystickKeysAlsoType { get; set; }
    /// <summary>Optional key = button pairs: "Space=A, Shift=LS, Ctrl=B, Q=LB, E=RB, R=X, F=Y, Tab=Back, Esc=Start, G=LT, V=RT".</summary>
    public string JoystickButtons { get; set; } = "";

    // ---- typo guard: ignore presses far shallower than how you usually press that key ----
    public bool Adaptive { get; set; }
    /// <summary>A press must reach at least this percent of the key's usual depth to count.</summary>
    public int AdaptivePercent { get; set; } = 30;
    /// <summary>Learned usual depth per analog key id (0-255), carried across sessions.</summary>
    public Dictionary<string, int> LearnedDepth { get; set; } = new();
}

/// <summary>
/// What Synapse's mapping engine did for this keyboard, done here: put the Huntsman V2 Analog in driver
/// mode (3), read its analog stream (HID input report 7: pairs of analog key id + depth 0-255, up to 11
/// keys, sent on change), decide per key when a press starts and ends from the configured depths, and
/// inject the keystrokes with SendInput (scan codes, so every app sees a normal keyboard). Also does the
/// key repeat Windows would otherwise do for a real keyboard.
///
/// Safety: normal mode (0) is restored on Stop, on exit, when the process dies cleanly, and whenever the
/// input desktop is not the default one (lock screen, UAC prompt, Ctrl+Alt+Del) because injected input
/// cannot reach the secure desktop; there the firmware types at its fixed depth. A killed process leaves
/// the keyboard in driver mode: `BladeCtl.exe --analog off`, a BladeCtl restart, or re-plugging fixes it.
/// </summary>
public sealed class AnalogEngine : IDisposable
{
    public const double TravelMm = 4.0;        // depth 255 = full travel (the firmware's 1.6 mm default reads 102)
    public const byte ReportId = 7;

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private HidStream? _stream;
    private RazerDeviceInfo? _info;
    private volatile Config _cfg = Build(new AnalogSettings());

    public string Status { get; private set; } = "off";
    public bool Running { get { lock (_gate) return _thread != null && _thread.IsAlive; } }
    public bool Suspended { get; private set; }
    public long Packets { get; private set; }
    public long KeyEvents { get; private set; }
    public ushort? DevicePid => _info?.Pid;

    /// <summary>Raised on the engine thread or a pool thread whenever Status changes.</summary>
    public event Action? Changed;

    private sealed class Config
    {
        public readonly byte[] Make = new byte[256];
        public readonly byte[] Break = new byte[256];
        public bool Rt; public byte RtDepth; public bool BlockWin;
        // controller mode
        public bool Joy; public byte JoyUp, JoyLeft, JoyDown, JoyRight; public byte JoyDz = 19, JoyFull = 191; public bool JoyType;
        public readonly sbyte[] Button = new sbyte[256];   // -1 none, else VirtualPad button index
        public int ButtonCount;
        // typo guard
        public bool Adaptive; public int AdaptivePct = 30;
        // raw key ids the table does not know, mapped by the user ("70 = MediaNext")
        public readonly AnalogKey?[] Extra = new AnalogKey?[256];
        public int ExtraCount;
        public string Summary = "";
        public Config() { Array.Fill(Button, (sbyte)-1); }
        public bool IsStickKey(int k) => Joy && (k == JoyUp || k == JoyLeft || k == JoyDown || k == JoyRight);
    }

    public static byte ToDepth(double mm) => (byte)Math.Clamp(Math.Round(mm / TravelMm * 255.0), 1, 255);
    public static double ToMm(byte d) => d / 255.0 * TravelMm;

    private static Config Build(AnalogSettings a)
    {
        var c = new Config();
        // controller mode
        c.Joy = a.Joystick;
        var jk = HuntsmanKeys.ParseNames(a.JoystickKeys ?? "") ?? new List<AnalogKey>();
        if (jk.Count >= 4) { c.JoyUp = jk[0].Id; c.JoyLeft = jk[1].Id; c.JoyDown = jk[2].Id; c.JoyRight = jk[3].Id; }
        else c.Joy = false;
        c.JoyDz = ToDepth(Math.Clamp(a.JoystickDeadzoneMm, 0.05, 2.0));
        c.JoyFull = ToDepth(Math.Clamp(Math.Max(a.JoystickFullMm, a.JoystickDeadzoneMm + 0.3), 0.5, 3.9));
        c.JoyType = a.JoystickKeysAlsoType;
        if (c.Joy)
        {
            foreach (var pair in (a.JoystickButtons ?? "").Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var keys = HuntsmanKeys.ParseNames(pair[..eq]);
                int bi = VirtualPad.ButtonIndex(pair[(eq + 1)..].Trim());
                if (keys == null || bi < 0) continue;
                foreach (var k in keys) { c.Button[k.Id] = (sbyte)bi; c.ButtonCount++; }
            }
        }
        c.Adaptive = a.Adaptive; c.AdaptivePct = Math.Clamp(a.AdaptivePercent, 5, 90);
        byte make = ToDepth(Math.Clamp(a.MakeMm, 0.2, 3.9));
        byte brk = ToDepth(Math.Clamp(Math.Min(a.BreakMm, a.MakeMm - 0.1), 0.1, 3.8));
        for (int i = 0; i < 256; i++) { c.Make[i] = make; c.Break[i] = brk; }
        int overridden = 0;
        foreach (var raw in (a.Overrides ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string left = line[..eq].Trim(), right = line[(eq + 1)..].Trim();

            // "70 = MediaNext": a raw key id the table does not know, given an action
            if (ActionVk(right) is ushort avk)
            {
                foreach (var tok in left.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(tok.TrimStart('#'), out var id) && id > 0 && id < 256) { c.Extra[id] = new AnalogKey((byte)id, right, 0, 0, false, avk); c.ExtraCount++; }
                continue;
            }

            var keys = HuntsmanKeys.ParseNames(left);
            if (keys == null) continue;
            var nums = right.Split('/');
            if (!double.TryParse(nums[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m)) continue;
            double b = nums.Length > 1 && double.TryParse(nums[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bb) ? bb : m - 0.3;
            byte km = ToDepth(Math.Clamp(m, 0.2, 3.9)), kb = ToDepth(Math.Clamp(Math.Min(b, m - 0.1), 0.1, 3.8));
            foreach (var k in keys) { c.Make[k.Id] = km; c.Break[k.Id] = kb; overridden++; }
        }
        c.Rt = a.RapidTrigger; c.RtDepth = (byte)Math.Clamp(Math.Round(Math.Clamp(a.RapidTriggerMm, 0.1, 2.0) / TravelMm * 255.0), 3, 128);
        c.BlockWin = a.BlockWindowsKey;
        c.Summary = $"make {ToMm(make):F1} mm, reset {ToMm(brk):F1} mm{(c.Rt ? $", rapid trigger {ToMm(c.RtDepth):F1} mm" : "")}{(overridden > 0 ? $", {overridden} key overrides" : "")}{(c.BlockWin ? ", Windows key blocked" : "")}" +
                    (c.Joy ? $", controller: {(a.JoystickKeys ?? "").Trim()} -> left stick ({ToMm(c.JoyDz):F1}-{ToMm(c.JoyFull):F1} mm{(c.JoyType ? ", keys also type" : "")}{(c.ButtonCount > 0 ? $", {c.ButtonCount} buttons" : "")})" : "") +
                    (c.Adaptive ? $", typo guard {c.AdaptivePct}%" : "") + (c.ExtraCount > 0 ? $", {c.ExtraCount} extra keys" : "");
        return c;
    }

    /// <summary>Named actions for raw key ids: media and volume keys go out as virtual keys.</summary>
    public static ushort? ActionVk(string name) => name.Replace(" ", "").ToLowerInvariant() switch
    {
        "medianext" or "nexttrack" or "next" => 0xB0,
        "mediaprev" or "mediaprevious" or "previoustrack" or "prev" => 0xB1,
        "mediaplay" or "playpause" or "play" => 0xB3,
        "mediastop" or "stop" => 0xB2,
        "mute" or "mediamute" or "volumemute" => 0xAD,
        "volumeup" or "volup" => 0xAF,
        "volumedown" or "voldown" => 0xAE,
        _ => null,
    };

    public byte LastUnknownId { get; private set; }
    public DateTime LastUnknownAt { get; private set; } = DateTime.MinValue;

    /// <summary>Apply new thresholds live; the next packet uses them. Controller mode is (dis)connected on the fly.</summary>
    public void Configure(AnalogSettings a)
    {
        _cfg = Build(a);
        ImportLearned(a);
        lock (_gate)
        {
            if (_thread != null && _thread.IsAlive) SyncPad();
        }
    }
    public string ConfigSummary => _cfg.Summary;

    // ---------- controller mode ----------

    private VirtualPad? _pad;
    public string PadStatus { get; private set; } = "";

    private void SyncPad()
    {
        if (_cfg.Joy && _pad == null)
        {
            var p = new VirtualPad();
            if (p.Connect(out var err)) { _pad = p; PadStatus = "virtual Xbox 360 controller connected"; Log.Ok("analog engine: " + PadStatus); }
            else { PadStatus = "controller mode unavailable: " + err; Log.Warn("analog engine: " + PadStatus); p.Dispose(); }
        }
        else if (!_cfg.Joy && _pad != null)
        {
            var p = _pad; _pad = null;
            p.Dispose();
            PadStatus = ""; Log.Write("analog engine: virtual controller removed");
        }
    }

    // ---------- typo guard (adaptive actuation) ----------

    private readonly byte[] _typical = new byte[256];   // learned usual peak depth per key, 0 = unknown
    private readonly byte[] _samples = new byte[256];
    private byte _typicalAll; private int _samplesAll;
    public int LearnedKeys => _typical.Count(t => t > 0);

    private void Learn(int k, byte peak)
    {
        if (peak < 8) return;
        _typical[k] = _typical[k] == 0 ? peak : (byte)((_typical[k] * 3 + peak) / 4);
        if (_samples[k] < 255) _samples[k]++;
        _typicalAll = _typicalAll == 0 ? peak : (byte)((_typicalAll * 7 + peak) / 8);
        _samplesAll++;
    }

    /// <summary>Effective make depth: the configured one, raised to a share of how deep this key is usually pressed.</summary>
    private byte EffectiveMake(Config cfg, int k)
    {
        byte make = cfg.Make[k];
        if (!cfg.Adaptive) return make;
        byte typical = _samples[k] >= 3 ? _typical[k] : _samplesAll >= 10 ? _typicalAll : (byte)0;
        if (typical == 0) return make;
        int adaptive = typical * cfg.AdaptivePct / 100;
        return (byte)Math.Clamp(Math.Max(make, adaptive), 1, 250);
    }

    public void ExportLearned(AnalogSettings a)
    {
        var d = new Dictionary<string, int>();
        for (int k = 1; k < 256; k++) if (_typical[k] > 0 && _samples[k] >= 3) d[k.ToString()] = _typical[k];
        if (d.Count > 0) a.LearnedDepth = d;
    }

    private void ImportLearned(AnalogSettings a)
    {
        if (a.LearnedDepth == null) return;
        foreach (var kv in a.LearnedDepth)
            if (int.TryParse(kv.Key, out var k) && k > 0 && k < 256 && _typical[k] == 0 && kv.Value is > 0 and < 256) { _typical[k] = (byte)kv.Value; _samples[k] = 3; }
    }

    /// <summary>Human line for the card: typical depth over all keys, and per-key count.</summary>
    public string LearnedSummary => _samplesAll == 0 && LearnedKeys == 0 ? "nothing learned yet"
        : $"usual press {ToMm(_typicalAll == 0 ? _typical.Where(t => t > 0).DefaultIfEmpty((byte)0).Max() : _typicalAll):F1} mm · {LearnedKeys} keys learned";

    private readonly HashSet<byte> _unknownIds = new();

    // ---------- lifecycle ----------

    public bool Start(RazerDeviceInfo info, out string error)
    {
        lock (_gate)
        {
            error = "";
            if (_thread != null && _thread.IsAlive) return true;
            HidDevice? an = null;
            foreach (var hd in DeviceList.Local.GetHidDevices(BladeDevice.VendorId, info.Pid))
            {
                try
                {
                    var rd = hd.GetReportDescriptor();
                    if (rd.Reports.Any(r => r.ReportType == ReportType.Input && r.ReportID == ReportId && r.Length == 24)) { an = hd; break; }
                }
                catch { }
            }
            if (an == null) { error = "the analog collection (HID input report 7) was not found"; return Fail(error); }
            if (!an.TryOpen(out var st)) { error = "the analog collection could not be opened for reading"; return Fail(error); }
            st.ReadTimeout = 15;
            _info = info; _stream = st;

            // The guardian goes up BEFORE the device can be in driver mode. It is the only thing that
            // survives a hard kill or a runtime FailFast, so it must cover the whole window, not just
            // the part after a successful start.
            StartGuardian();

            // Treat "asked for driver mode" as "the device may be in driver mode" from this instant.
            // The SET can land on the EC even when the read-back fails, and 2.4.1 returned in that case
            // without undoing it - which is how the keyboard was left dead with nothing reading it.
            _driverModeApplied = true;
            if (!SetMode(3, out error))
            {
                string undo = Teardown("failed start");
                Log.Warn($"analog engine: start failed ({error}); {undo}");
                return Fail(error);
            }
            _stop = false; Suspended = false; Packets = 0; KeyEvents = 0;
            _thread = new Thread(Loop) { IsBackground = true, Name = "analog-engine", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            Status = "running";
            Log.Ok($"analog engine: running on {info.Name} ({_cfg.Summary}); {HuntsmanKeys.Count} keys mapped");
            SyncPad();
            _dial = new DialGuard();
            _dial.Start();
        }
        Notify();
        return true;
    }

    /// <summary>
    /// True while this process has written driver mode to the keyboard and has not since confirmed it back
    /// in normal mode. Deliberately independent of the reader thread: a failed start, or a loop that died on
    /// its own, both leave the device stranded with no thread to hang the cleanup off.
    /// </summary>
    private volatile bool _driverModeApplied;
    public bool DriverModeOutstanding => _driverModeApplied;

    private DialGuard? _dial;
    public string DialStatus => _dial?.Status ?? "";

    // ---------- guardian: a second process that restores normal mode if this one dies ----------

    private System.Diagnostics.Process? _guardian;

    /// <summary>
    /// Exit handlers do not run on a hard kill or a runtime FailFast (2026-09-06 14:12 proved it: the keyboard
    /// stayed in driver mode until it was replugged). So a tiny second BladeCtl process waits on this one and
    /// puts the keyboard back in normal mode the moment this process is gone, however it went.
    /// </summary>
    private void StartGuardian()
    {
        try
        {
            StopGuardian();
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? "BladeCtl.exe", $"--guard-analog {Environment.ProcessId}")
            { UseShellExecute = false, CreateNoWindow = true };
            _guardian = System.Diagnostics.Process.Start(psi);
            Log.Write($"analog engine: guardian process {_guardian?.Id} watching pid {Environment.ProcessId}");
        }
        catch (Exception ex) { Log.Warn("analog engine: guardian could not start: " + ex.Message); }
    }

    private void StopGuardian()
    {
        var g = _guardian; _guardian = null;
        if (g == null) return;
        try { if (!g.HasExited) g.Kill(); } catch { }
        try { g.Dispose(); } catch { }
    }

    private bool Fail(string error) { Status = "failed: " + error; Log.Error("analog engine: " + error); Notify(); return false; }

    public void Stop(string why)
    {
        Thread? t;
        lock (_gate) { t = _thread; _thread = null; }
        if (t != null)
        {
            _stop = true;
            try { t.Join(2000); } catch { }
        }
        // NOT gated on the thread. 2.4.1 returned here whenever _thread was null, so a failed Start - and a
        // loop that had already died and nulled _thread itself - could never be cleaned up by exit, by
        // Dispose, or by the user switching the engine off.
        else if (!_driverModeApplied && _stream == null && _pad == null && _dial == null && _guardian == null)
            return;

        string restore = Teardown(why);
        Status = "off";
        Log.Write($"analog engine: stopped ({why}); {restore}; {Packets} packets, {KeyEvents} key events");
        Notify();
    }

    /// <summary>
    /// Release everything the engine owns and put the keyboard back in normal mode. The single exit path:
    /// safe to call twice, and safe to call when the engine never fully started.
    /// </summary>
    private string Teardown(string why)
    {
        string restore = _driverModeApplied
            ? (RestoreNormal(out var err) ? "normal mode restored" : "COULD NOT restore normal mode: " + err)
            : "device was not in driver mode";
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        var pad = _pad; _pad = null;
        try { pad?.Dispose(); } catch { }
        PadStatus = "";
        var dial = _dial; _dial = null;
        try { dial?.Dispose(); } catch { }
        StopGuardian();
        return restore;
    }

    /// <summary>Restore normal mode with retries. The flag clears only on a CONFIRMED read-back of 0.</summary>
    private bool RestoreNormal(out string err)
    {
        err = "no device";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (SetMode(0, out err)) { _driverModeApplied = false; return true; }
            Thread.Sleep(120);
        }
        return false;
    }

    private void Notify() { try { Changed?.Invoke(); } catch { } }

    private bool SetMode(byte mode, out string err)
    {
        err = "";
        if (_info == null) { err = "no device"; return false; }
        using var ctl = RazerEnumerator.Open(_info, Log.CoreLog);
        if (ctl == null) { err = "control interface could not be opened"; return false; }
        bool ok = ctl.SetDeviceMode(mode);
        Thread.Sleep(60);
        var rb = ctl.GetDeviceMode();
        if (!ok || rb != mode) { err = $"device mode {mode} {(ok ? "acked" : "rejected")}, reads back {rb?.ToString() ?? "?"}"; return false; }
        return true;
    }

    /// <summary>The guardian process body: wait for the parent to die, then restore normal mode if the keyboard is still in driver mode.</summary>
    public static int RunGuardian(int parentPid)
    {
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch { /* parent already gone */ }
        try
        {
            // Go STRAIGHT to the restore. 2.4.1 enumerated once to decide whether to act and ForceNormalMode
            // enumerated again to act, and a full enumeration probes six transaction ids across two report ids
            // on every Razer product - measured at ~15 s end to end on 2026-09-07, all of it with the user
            // unable to type. Writing normal mode to an already-normal keyboard is harmless, so the check was
            // costing more than it saved. It also removes the `!= 3` test, which treated an unreadable mode as
            // "already fine" and skipped the restore in exactly the flaky case that most needs it.
            Log.Warn($"analog guardian: pid {parentPid} ended -> " + ForceNormalMode());
            return 0;
        }
        catch (Exception ex) { Log.Error("analog guardian: " + ex.Message); return 1; }
    }

    /// <summary>Emergency: put every Huntsman back in normal mode without an engine instance (CLI, startup after a crash).</summary>
    public static string ForceNormalMode()
    {
        var sb = new StringBuilder();
        foreach (var d in RazerEnumerator.Enumerate().Where(d => d.Pid == 0x0266 && d.SpeaksProtocol))
        {
            using var ctl = RazerEnumerator.Open(d, Log.CoreLog);
            if (ctl == null) { sb.Append($"{d.Name}: could not open; "); continue; }
            // Retry until the device CONFIRMS normal mode: this is the last line of defence for a keyboard
            // that cannot type, so one unverified write is not good enough.
            byte? rb = null; bool ok = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ok = ctl.SetDeviceMode(0);
                Thread.Sleep(80);
                rb = ctl.GetDeviceMode();
                if (rb == 0) { sb.Append($"{d.Name}: normal mode confirmed (attempt {attempt}); "); break; }
            }
            if (rb != 0) sb.Append($"{d.Name}: normal mode {(ok ? "acked" : "REJECTED")} but reads back {rb?.ToString() ?? "?"} after 3 attempts; ");
        }
        return sb.Length == 0 ? "no Huntsman V2 Analog found" : sb.ToString().TrimEnd(' ', ';');
    }

    // ---------- the loop ----------

    private void Loop()
    {
        var down = new bool[256]; var peak = new byte[256]; var trough = new byte[256]; var cur = new byte[256];
        byte lastKey = 0;
        var repeatDelay = TimeSpan.FromMilliseconds(RepeatDelayMs());
        var repeatInterval = TimeSpan.FromMilliseconds(RepeatIntervalMs());
        DateTime repeatAt = DateTime.MaxValue, nextDesktopCheck = DateTime.MinValue;
        var buf = new byte[64];

        try
        {
            while (!_stop)
            {
                var now = DateTime.UtcNow;
                if (now >= nextDesktopCheck)
                {
                    nextDesktopCheck = now.AddMilliseconds(250);
                    bool onDefault = InputDesktopIsDefault();
                    if (!onDefault && !Suspended)
                    {
                        ReleaseAll(down); lastKey = 0; repeatAt = DateTime.MaxValue;
                        Suspended = SetMode(0, out var e1);
                        Status = Suspended ? "suspended (secure desktop) — firmware typing" : "running (could not suspend: " + e1 + ")";
                        Log.Write("analog engine: " + Status); Notify();
                    }
                    else if (onDefault && Suspended)
                    {
                        if (SetMode(3, out var e2)) { Suspended = false; Status = "running"; Log.Write("analog engine: resumed on the default desktop"); }
                        else { Status = "suspended (resume failed: " + e2 + ")"; Log.Warn("analog engine: " + Status); }
                        Notify();
                    }
                }

                int n;
                try { n = _stream!.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { n = 0; }
                catch (Exception ex)
                {
                    Status = "stopped: the analog stream went away (" + ex.GetType().Name + ")";
                    Log.Warn("analog engine: " + Status);
                    break;
                }

                if (n > 2 && !Suspended && buf[0] == ReportId)
                {
                    Packets++;
                    Array.Clear(cur, 0, 256);
                    for (int i = 1; i + 1 < n; i += 2) { byte id = buf[i]; if (id == 0) break; cur[id] = buf[i + 1]; }
                    var cfg = _cfg;
                    for (int k = 1; k < 256; k++)
                    {
                        byte d = cur[k];
                        if (!down[k] && d == 0) { trough[k] = 0; continue; }
                        if (!HuntsmanKeys.ById.TryGetValue((byte)k, out var key))
                        {
                            key = cfg.Extra[k];
                            if (key == null)
                            {
                                if (d > 0)
                                {
                                    LastUnknownId = (byte)k; LastUnknownAt = DateTime.Now;
                                    if (_unknownIds.Add((byte)k)) { Log.Warn($"analog engine: key id {k} is not in the table (depth {d}); add \"{k} = MediaNext\" (or MediaPrev, MediaPlay, Mute) to the overrides box to map it"); Notify(); }
                                }
                                continue;
                            }
                        }
                        if (!down[k])
                        {
                            byte make = EffectiveMake(cfg, k);
                            byte brk = (byte)Math.Min(cfg.Break[k], Math.Max(1, make - 6));
                            bool press = d >= make || (cfg.Rt && trough[k] > 0 && d >= brk && d >= trough[k] + cfg.RtDepth);
                            if (press)
                            {
                                bool typed = Emit(k, key, true, cfg);
                                down[k] = true; peak[k] = d; KeyEvents++;
                                if (typed) { lastKey = (byte)k; repeatAt = DateTime.UtcNow + repeatDelay; }
                            }
                            else if (trough[k] == 0 || d < trough[k]) trough[k] = d;
                        }
                        else
                        {
                            byte brk = (byte)Math.Min(cfg.Break[k], Math.Max(1, EffectiveMake(cfg, k) - 6));
                            bool release = d <= brk || (cfg.Rt && d + cfg.RtDepth <= peak[k]);
                            if (release)
                            {
                                Emit(k, key, false, cfg); down[k] = false; trough[k] = d; KeyEvents++;
                                Learn(k, peak[k]);
                                if (lastKey == k) { lastKey = 0; repeatAt = DateTime.MaxValue; }
                            }
                            else if (d > peak[k]) peak[k] = d;
                        }
                    }

                    // left stick from the four movement keys' depths
                    var pad = _pad;
                    if (pad != null && cfg.Joy)
                    {
                        double Axis(byte depth) => depth <= cfg.JoyDz ? 0 : depth >= cfg.JoyFull ? 1 : (depth - cfg.JoyDz) / (double)(cfg.JoyFull - cfg.JoyDz);
                        double x = Axis(cur[cfg.JoyRight]) - Axis(cur[cfg.JoyLeft]);
                        double y = Axis(cur[cfg.JoyUp]) - Axis(cur[cfg.JoyDown]);
                        pad.SetLeftStick(x, y);
                    }
                }

                if (lastKey != 0 && down[lastKey] && DateTime.UtcNow >= repeatAt && HuntsmanKeys.ById.TryGetValue(lastKey, out var rk))
                {
                    Send(rk, true, _cfg);
                    repeatAt = DateTime.UtcNow + repeatInterval;
                }
            }
        }
        catch (Exception ex) { Status = "crashed: " + ex.Message; Log.Error("analog engine loop: " + ex); }
        finally
        {
            ReleaseAll(down);
            if (_stop) { /* Stop() owns the teardown */ }
            else
            {
                // The loop died on its own (device gone, stream error). Reclaim EVERYTHING: 2.4.1 restored the
                // mode here but left the stream, the ViGEm pad, the dial guard's global mouse hook and the
                // guardian process alive - and nulled _thread, so Stop() could never reclaim them either.
                // Every keyboard replug stranded another set.
                lock (_gate) _thread = null;
                string restore = Teardown("loop ended");
                Status += " · " + restore;
                Log.Warn("analog engine: " + Status);
                Notify();
            }
        }
    }

    private void ReleaseAll(bool[] down)
    {
        var cfg = _cfg;
        for (int k = 1; k < 256; k++)
            if (down[k]) { if (HuntsmanKeys.ById.TryGetValue((byte)k, out var key)) Emit(k, key, false, cfg); down[k] = false; }
        try { _pad?.ReleaseAll(); } catch { }
    }

    /// <summary>Route a press/release: controller button, stick key (silent unless "keys also type"), or keystroke. Returns true when a keystroke went out.</summary>
    private bool Emit(int k, AnalogKey key, bool press, Config cfg)
    {
        var pad = _pad;
        if (pad != null && cfg.Button[k] >= 0) { pad.SetButton(cfg.Button[k], press); return false; }
        if (cfg.IsStickKey(k) && !cfg.JoyType) return false;
        return Send(key, press, cfg);
    }

    // ---------- injection ----------

    private static bool Send(AnalogKey key, bool press, Config cfg)
    {
        if (cfg.BlockWin && key.Usage is 0xE3 or 0xE7) return false;
        var inp = new INPUT { type = 1 };
        if (key.Usage == 0x48 || key.Scan == 0) { inp.u.ki.wVk = key.Vk; inp.u.ki.dwFlags = press ? 0u : KEYEVENTF_KEYUP; }   // Pause, media/volume actions
        else
        {
            inp.u.ki.wScan = key.Scan;
            inp.u.ki.dwFlags = KEYEVENTF_SCANCODE | (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0u) | (press ? 0u : KEYEVENTF_KEYUP);
        }
        inp.u.ki.dwExtraInfo = (IntPtr)0xB1ADE;
        return SendInput(1, ref inp, Marshal.SizeOf<INPUT>()) == 1;
    }

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, ref INPUT input, int size);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint ini);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder buf, int len, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr h);

    private static int RepeatDelayMs() => SystemParametersInfo(0x0016, 0, out var d, 0) ? (Math.Clamp(d, 0, 3) + 1) * 250 : 500;
    private static int RepeatIntervalMs()
    {
        if (!SystemParametersInfo(0x000A, 0, out var s, 0)) return 33;
        double cps = 2.5 + 27.5 * Math.Clamp(s, 0, 31) / 31.0;
        return (int)Math.Round(1000.0 / cps);
    }

    /// <summary>False on the lock screen, UAC prompts and Ctrl+Alt+Del, where injected input cannot go.</summary>
    private static bool InputDesktopIsDefault()
    {
        var h = OpenInputDesktop(0, false, 0x0001);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(64);
            return GetUserObjectInformationW(h, 2, sb, 64, out _) && sb.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(h); }
    }

    public void Dispose() => Stop("dispose");
}
