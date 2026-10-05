using BladeCtl.Core;

namespace BladeCtl.Tray;

/// <summary>
/// Razer accessories: enumeration (at start, on hot-plug, on demand) and lighting control through
/// the extended-matrix command family. Effects are write-only on every Razer device; brightness
/// reads back. With VARSTORE the device keeps the effect in its own memory, which is what lets
/// Synapse be removed: the keyboard and dock boot into whatever was last written.
/// </summary>
public sealed class AccessoryService : IDisposable
{
    public sealed record ApplyOutcome(bool Ok, string Message, string Kind); // Kind: ok | warn | bad

    private readonly Settings _settings;
    private readonly object _gate = new();
    private List<RazerDeviceInfo> _devices = new();
    private System.Threading.Timer? _debounce;
    private DateTime _lastRefresh = DateTime.MinValue;

    public AccessoryService(Settings settings) { _settings = settings; }

    public IReadOnlyList<RazerDeviceInfo> Devices { get { lock (_gate) return _devices.ToList(); } }
    public DateTime LastRefresh => _lastRefresh;

    /// <summary>Raised on a pool thread after every refresh.</summary>
    public event Action? Changed;

    /// <summary>Raised on a pool thread after every apply.</summary>
    public event Action<RazerDeviceInfo, ApplyOutcome>? Applied;

    public Task RefreshAsync(string why) => Task.Run(() => Refresh(why));

    /// <summary>Hot-plug: coalesce the burst of WM_DEVICECHANGE messages into one refresh.</summary>
    public void DeviceChangeNoticed()
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => Refresh("device change"), null, 2500, Timeout.Infinite);
    }

    public void Refresh(string why)
    {
        List<RazerDeviceInfo> list;
        try { list = RazerEnumerator.Enumerate(Log.CoreLog); }
        catch (Exception ex) { Log.Error("accessory enumeration failed: " + ex.Message); return; }

        List<RazerDeviceInfo> before;
        lock (_gate) { before = _devices; _devices = list; }
        _lastRefresh = DateTime.Now;

        var added = list.Where(d => before.All(b => b.Pid != d.Pid)).ToList();
        var gone = before.Where(b => list.All(d => d.Pid != b.Pid)).ToList();
        if (added.Count > 0 || gone.Count > 0 || before.Count == 0)
        {
            Log.Write($"accessories ({why}): " + string.Join(" · ", list.Select(Describe)));
            foreach (var d in gone) Log.Write($"accessory removed: {d.Name}");
        }
        try { Changed?.Invoke(); } catch { }
    }

    public static string Describe(RazerDeviceInfo d) =>
        $"{d.Name} [{d.Pid:X4}] " + (d.SpeaksProtocol
            ? $"fw {d.Firmware ?? "?"} tid 0x{d.Tid:X2}{(d.ReportId != 0 ? $" rid 0x{d.ReportId:X2}" : "")} mode {d.DeviceMode?.ToString() ?? "?"}{(d.Brightness is int b ? $" bright {b}" : "")}"
            : $"no protocol ({d.Detail})");

    /// <summary>Apply and persist lighting for one accessory. Blade is handled by CommandRunner, not here.</summary>
    public Task<ApplyOutcome> ApplyAsync(ushort pid, AccessoryLighting l) => Task.Run(() => Apply(pid, l, persist: true));

    private ApplyOutcome Apply(ushort pid, AccessoryLighting l, bool persist)
    {
        var info = Devices.FirstOrDefault(d => d.Pid == pid);
        if (info == null) return Finish(null, new ApplyOutcome(false, $"device {pid:X4} is not connected", "bad"));
        if (info.IsBlade) return Finish(info, new ApplyOutcome(false, "the Blade is controlled from the Blade view", "bad"));

        ApplyOutcome outcome;
        if (info.Family == EffectFamily.ExtendedMatrix && info.ControlPath != null)
        {
            using var ctl = RazerEnumerator.Open(info, Log.CoreLog);
            if (ctl == null) return Finish(info, new ApplyOutcome(false, $"{info.Name}: control interface could not be opened", "bad"));

            byte r = (byte)l.R, g = (byte)l.G, b = (byte)l.B;
            byte speed = (byte)Math.Clamp(l.Speed, 1, 4), dir = (byte)Math.Clamp(l.Direction, 1, 2);
            bool ok = l.Effect switch
            {
                "Off" => ctl.SetNone(),
                "Spectrum" => ctl.SetSpectrum(),
                "Breathing" => ctl.SetBreathing(r, g, b),
                "Wave" => ctl.SetWave(dir),
                "Reactive" => ctl.SetReactive(r, g, b, speed),
                "Starlight" => ctl.SetStarlight(r, g, b, (byte)Math.Min(speed, (byte)3)),
                _ => ctl.SetStatic(r, g, b),
            };
            bool bok = ctl.SetBrightness((byte)Math.Clamp(l.Brightness, 0, 255));
            Thread.Sleep(150);
            var read = ctl.GetBrightness();
            bool bconf = read.HasValue && Math.Abs(read.Value - l.Brightness) <= 2;

            // Effect read-back (0x0F/0x82): the Huntsman and the dock echo the stored effect id and colour;
            // the Leviathan answers with unrelated bytes, so its catalogue entry opts out.
            byte expectedId = EffectId(l.Effect);
            bool canRead = info.Model?.EffectReadback ?? true;
            var eff = canRead ? ctl.GetEffect() : null;
            bool effConfirmed = eff != null && eff.Length > 8 && eff[2] == expectedId &&
                                (l.Effect is not ("Static" or "Breathing" or "Reactive" or "Starlight") || (eff[6] == r && eff[7] == g && eff[8] == b));

            string colour = l.Effect is "Static" or "Breathing" or "Reactive" or "Starlight" ? " " + l.Hex : "";
            string opt = l.Effect switch { "Wave" => dir == 2 ? " right-to-left" : " left-to-right", "Reactive" or "Starlight" => $" speed {Math.Min(speed, (byte)(l.Effect == "Starlight" ? 3 : 4))}", _ => "" };
            if (!ok)
                outcome = new ApplyOutcome(false, $"{info.Name}: {l.Effect}{colour} was rejected by the device", "bad");
            else if (!bok || !bconf)
                outcome = new ApplyOutcome(false, $"{info.Name}: {l.Effect}{colour} accepted, but brightness {l.Brightness} {(read.HasValue ? $"read back {read}" : "did not read back")}", "warn");
            else if (effConfirmed)
                outcome = new ApplyOutcome(true, $"{info.Name}: {l.Effect}{colour}{opt} confirmed by read-back, brightness {l.Brightness} confirmed, saved in the device's memory", "ok");
            else if (canRead && eff != null)
                outcome = new ApplyOutcome(true, $"{info.Name}: {l.Effect}{colour}{opt} accepted, brightness {l.Brightness} confirmed, but the effect register reads back id {eff[2]} (expected {expectedId})", "warn");
            else
                outcome = new ApplyOutcome(true, $"{info.Name}: {l.Effect}{colour}{opt} accepted, brightness {l.Brightness} confirmed, saved in the device's memory", "ok");
        }
        else if (info.CandidatePath != null)
        {
            // Write-only experiment for a device that never answers (Leviathan V2 X). Its descriptor has no
            // feature reports at all: commands go out as OUTPUT report 0x03. OpenRGB models it as an
            // extended-matrix device on tid 0x1F, led 0. Nothing has ever come back, so this stays "sent blind".
            using var od = RazerOutputDevice.Open(pid, 0x03, Log.CoreLog);
            if (od == null) return Finish(info, new ApplyOutcome(false, $"{info.Name}: output-report interface could not be opened", "bad"));
            RazerPacket eff = l.Effect switch
            {
                "Off" => RazerPacket.Create(0x1F, 0x0F, 0x02, 0x06, RazerCatalog.NOSTORE, 0x00, ChromaController.EffectNone),
                "Spectrum" => RazerPacket.Create(0x1F, 0x0F, 0x02, 0x06, RazerCatalog.NOSTORE, 0x00, ChromaController.EffectSpectrum),
                "Breathing" => RazerPacket.Create(0x1F, 0x0F, 0x02, 0x09, RazerCatalog.NOSTORE, 0x00, ChromaController.EffectBreathing, 0x01, 0x00, 0x01, (byte)l.R, (byte)l.G, (byte)l.B),
                "Wave" => RazerPacket.Create(0x1F, 0x0F, 0x02, 0x06, RazerCatalog.NOSTORE, 0x00, ChromaController.EffectWave, 0x01, 0x28),
                _ => RazerPacket.Create(0x1F, 0x0F, 0x02, 0x09, RazerCatalog.NOSTORE, 0x00, ChromaController.EffectStatic, 0x00, 0x00, 0x01, (byte)l.R, (byte)l.G, (byte)l.B),
            };
            bool sent = od.Send(eff, out var resp1, Log.CoreLog);
            Thread.Sleep(60);
            bool sent2 = od.Send(RazerPacket.Create(0x1F, 0x0F, 0x04, 0x03, RazerCatalog.NOSTORE, 0x00, (byte)Math.Clamp(l.Brightness, 0, 255)), out var resp2, Log.CoreLog);
            string raw = resp1 == null && resp2 == null ? "no reply" : "reply " + string.Join(" ", (resp1 ?? resp2)!.Take(12).Select(x => x.ToString("X2")));
            outcome = sent && sent2
                ? new ApplyOutcome(true, $"{info.Name}: {l.Effect} sent blind as output report 0x03 ({raw}). Look at the speaker.", "warn")
                : new ApplyOutcome(false, $"{info.Name}: the output-report write failed", "bad");
        }
        else
        {
            outcome = new ApplyOutcome(false, $"{info.Name}: no control interface for lighting", "bad");
        }

        if (persist && outcome.Kind != "bad")
        {
            var saved = _settings.AccessoryFor(pid);
            saved.Effect = l.Effect; saved.R = l.R; saved.G = l.G; saved.B = l.B; saved.Brightness = l.Brightness;
            saved.Direction = l.Direction; saved.Speed = l.Speed;
            saved.ApplyAtLogon = l.ApplyAtLogon;
            saved.LastAppliedUtc = DateTime.UtcNow.ToString("o");
            _settings.Save();
        }
        return Finish(info, outcome);
    }

    public static byte EffectId(string effect) => effect switch
    {
        "Off" => ChromaController.EffectNone,
        "Spectrum" => ChromaController.EffectSpectrum,
        "Breathing" => ChromaController.EffectBreathing,
        "Wave" => ChromaController.EffectWave,
        "Reactive" => ChromaController.EffectReactive,
        "Starlight" => ChromaController.EffectStarlight,
        _ => ChromaController.EffectStatic,
    };

    public static string EffectName(byte? id) => id switch
    {
        0x00 => "Off", 0x01 => "Static", 0x02 => "Breathing", 0x03 => "Spectrum", 0x04 => "Wave", 0x05 => "Reactive", 0x07 => "Starlight",
        null => "?", _ => $"effect {id}",
    };

    // ---------- keyboard extras (game mode, polling rate) ----------

    public Task<ApplyOutcome> SetGameModeAsync(ushort pid, bool on) => Task.Run(() =>
    {
        var info = Devices.FirstOrDefault(d => d.Pid == pid);
        if (info == null || !info.IsKeyboard || info.ControlPath == null) return Finish(info, new ApplyOutcome(false, $"device {pid:X4} is not a connected keyboard", "bad"));
        using var ctl = RazerEnumerator.Open(info, Log.CoreLog);
        if (ctl == null) return Finish(info, new ApplyOutcome(false, $"{info.Name}: control interface could not be opened", "bad"));
        bool ack = ctl.SetGameMode(on);
        Thread.Sleep(120);
        var read = ctl.GetGameMode();
        ApplyOutcome o;
        if (read == on)
        {
            _settings.AccessoryFor(pid).GameMode = on; _settings.Save();
            o = new ApplyOutcome(true, $"{info.Name}: game mode {(on ? "ON — Windows key disabled" : "off — Windows key active")}, confirmed by read-back", "ok");
        }
        else o = new ApplyOutcome(false, $"{info.Name}: game mode {(on ? "on" : "off")} {(ack ? "acked but" : "rejected;")} device reports {(read == null ? "?" : read == true ? "on" : "off")}", "bad");
        UpdateFacts(pid, d => d with { GameMode = read ?? d.GameMode });
        return Finish(info, o);
    });

    public Task<ApplyOutcome> SetPollingAsync(ushort pid, int hz) => Task.Run(() =>
    {
        var info = Devices.FirstOrDefault(d => d.Pid == pid);
        if (info == null || !info.IsKeyboard || info.ControlPath == null) return Finish(info, new ApplyOutcome(false, $"device {pid:X4} is not a connected keyboard", "bad"));
        using var ctl = RazerEnumerator.Open(info, Log.CoreLog);
        if (ctl == null) return Finish(info, new ApplyOutcome(false, $"{info.Name}: control interface could not be opened", "bad"));
        bool ack = ctl.SetPollingHz(hz);
        Thread.Sleep(120);
        var read = ctl.GetPollingHz();
        ApplyOutcome o;
        if (read == hz)
        {
            _settings.AccessoryFor(pid).PollingHz = hz; _settings.Save();
            o = new ApplyOutcome(true, $"{info.Name}: polling rate {hz} Hz, confirmed by read-back", "ok");
        }
        else o = new ApplyOutcome(false, $"{info.Name}: polling {hz} Hz {(ack ? "acked but" : "rejected;")} device reports {(read?.ToString() ?? "?")} Hz", "bad");
        UpdateFacts(pid, d => d with { PollingHz = read ?? d.PollingHz });
        return Finish(info, o);
    });

    private void UpdateFacts(ushort pid, Func<RazerDeviceInfo, RazerDeviceInfo> f)
    {
        lock (_gate) _devices = _devices.Select(d => d.Pid == pid ? f(d) : d).ToList();
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Send one lighting choice (typically the Blade's) to every controllable accessory. Returns the per-device outcomes.</summary>
    public Task<List<ApplyOutcome>> ApplyToAllAsync(string effect, int r, int g, int b, int brightness) => Task.Run(() =>
    {
        var list = new List<ApplyOutcome>();
        foreach (var d in Devices.Where(d => !d.IsBlade && d.SpeaksProtocol && d.Family == EffectFamily.ExtendedMatrix))
        {
            var saved = _settings.AccessoryFor(d.Pid);
            var l = new AccessoryLighting
            {
                Effect = effect, R = r, G = g, B = b, Brightness = brightness,
                Direction = saved.Direction, Speed = saved.Speed, ApplyAtLogon = saved.ApplyAtLogon,
                GameMode = saved.GameMode, PollingHz = saved.PollingHz,
            };
            list.Add(Apply(d.Pid, l, persist: true));
        }
        return list;
    });

    // ---------- lights off (lock / idle) and back ----------

    private readonly Dictionary<ushort, byte> _dimmedWith = new();

    /// <summary>Brightness 0 on every accessory. NOSTORE first so the onboard setting survives a crash; VARSTORE if the device refuses.</summary>
    public int LightsOff(string why)
    {
        int n = 0;
        foreach (var d in Devices.Where(d => !d.IsBlade && d.SpeaksProtocol && d.Family == EffectFamily.ExtendedMatrix))
        {
            using var ctl = RazerEnumerator.Open(d, Log.CoreLog);
            if (ctl == null) continue;
            byte storage = RazerCatalog.NOSTORE;
            bool ok = ctl.SetBrightness(0, storage);
            if (!ok) { storage = RazerCatalog.VARSTORE; ok = ctl.SetBrightness(0, storage); }
            if (ok) { lock (_dimmedWith) _dimmedWith[d.Pid] = storage; n++; }
            Log.Write($"{why}: {d.Name} lights off {(ok ? (storage == RazerCatalog.NOSTORE ? "(volatile)" : "(stored)") : "FAILED")}");
        }
        return n;
    }

    public int LightsRestore(string why)
    {
        int n = 0;
        List<ushort> pids; lock (_dimmedWith) { pids = _dimmedWith.Keys.ToList(); _dimmedWith.Clear(); }
        foreach (var d in Devices.Where(d => pids.Contains(d.Pid)))
        {
            using var ctl = RazerEnumerator.Open(d, Log.CoreLog);
            if (ctl == null) continue;
            int bright = _settings.Accessories.TryGetValue(d.Pid.ToString("X4"), out var l) ? l.Brightness : (d.Brightness ?? 255);
            bool ok = ctl.SetBrightness((byte)Math.Clamp(bright, 0, 255));
            if (ok) n++;
            Log.Write($"{why}: {d.Name} brightness {bright} {(ok ? "restored" : "FAILED")}");
        }
        return n;
    }

    private ApplyOutcome Finish(RazerDeviceInfo? info, ApplyOutcome o)
    {
        switch (o.Kind) { case "ok": Log.Ok(o.Message); break; case "warn": Log.Warn(o.Message); break; default: Log.Error(o.Message); break; }
        if (info != null) { try { Applied?.Invoke(info, o); } catch { } }
        return o;
    }

    /// <summary>Re-send saved lighting for every present accessory flagged ApplyAtLogon.</summary>
    public int ApplySavedAtLogon(string why)
    {
        int n = 0;
        foreach (var d in Devices.Where(d => !d.IsBlade))
        {
            string key = d.Pid.ToString("X4");
            if (!_settings.Accessories.TryGetValue(key, out var l) || !l.ApplyAtLogon) continue;
            Log.Write($"{why}: re-applying saved lighting to {d.Name}");
            Apply(d.Pid, l, persist: false);
            n++;
        }
        return n;
    }

    public void Dispose() => _debounce?.Dispose();
}
