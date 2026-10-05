using System.Globalization;
using System.Text.Json;

namespace BladeCtl.Core.Power;

// ---- persisted shape of %APPDATA%\BladeCtl\power-learned.json (spec 9) ----
public sealed class HistEntry { public Dictionary<string, int> Hist { get; set; } = new(); public int Count { get; set; } public string Updated { get; set; } = ""; public long Touched { get; set; } }
public sealed class MedEntry { public double M { get; set; } public double Secs { get; set; } public string Updated { get; set; } = ""; public long Touched { get; set; } }
public sealed class CpuEntry { public double W { get; set; } public double MHz { get; set; } public double Secs { get; set; } public string Updated { get; set; } = ""; public long Touched { get; set; } }
public sealed class ChgEntry { public double Mw { get; set; } public int N { get; set; } public long Touched { get; set; } }
public sealed class RestEntry { public double W { get; set; } public double Secs { get; set; } public long Touched { get; set; } }
public sealed class EcEntry { public string Utc { get; set; } = ""; public int Lvl { get; set; } public int Rec { get; set; } public bool Ac { get; set; } }

public sealed class LearnData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, HistEntry> Gpu { get; set; } = new();
    public Dictionary<string, MedEntry> GClk { get; set; } = new();
    public Dictionary<string, CpuEntry> Cpu { get; set; } = new();
    public Dictionary<string, ChgEntry> Chg { get; set; } = new();
    public Dictionary<string, RestEntry> Rest { get; set; } = new();
    public List<EcEntry> EcLog { get; set; } = new();
    /// <summary>Last NVIDIA driver NVML reported, so learned keys resolve after a restart before NVML has run.</summary>
    public string LastDriver { get; set; } = "";
    /// <summary>Test B1 confirmed that the dock-only reading is USB-C (`power-usbc confirmed`).</summary>
    public bool UsbcConfirmed { get; set; }
}

/// <summary>Learning-key parts (spec 9).</summary>
public static class PowerKeys
{
    public static string Bst(string mode, int? cpuBoost, int? gpuBoost) =>
        mode == "Custom" ? $"c{cpuBoost?.ToString() ?? "?"}g{gpuBoost?.ToString() ?? "?"}" : "x";
    public static string Prof(bool engaged) => engaged ? "P1" : "P0";
    public static string Gpu(string drv, string src, string mode, string bst, string prof) => $"gpu.{Drv(drv)}.{src}.{mode}.{bst}.{prof}";
    public static string GClk(string drv, string src, string mode, string bst, string prof) => $"gclk.{Drv(drv)}.{src}.{mode}.{bst}.{prof}";
    public static string Cpu(string src, string mode, string bst, string prof, bool oneThread) => $"cpu.{src}.{mode}.{bst}.{prof}.{(oneThread ? "1T" : "MT")}";
    public static string Chg(string src, int bin) => $"chg.{src}.{bin}";
    public static string Rest(bool panelOn, int hz) => $"rest.{(panelOn ? 1 : 0)}.{(panelOn ? hz : 0)}";
    private static string Drv(string d) => string.IsNullOrWhiteSpace(d) ? "na" : d.Trim();

    /// <summary>Parse the four trailing parts of a gpu/gclk key: src, mode, bst, prof (the driver may contain dots).</summary>
    public static (string Drv, string Src, string Mode, string Bst, string Prof)? ParseGpu(string key)
    {
        var p = key.Split('.');
        if (p.Length < 6) return null;
        return (string.Join(".", p[1..^4]), p[^4], p[^3], p[^2], p[^1]);
    }

    public static (string Src, string Mode, string Bst, string Prof, string Shape)? ParseCpu(string key)
    {
        var p = key.Split('.');
        return p.Length == 6 ? (p[1], p[2], p[3], p[4], p[5]) : null;
    }
}

/// <summary>
/// Learned power data (spec 9): the GPU enforced limit per source/mode, graphics clock, CPU package watts and MHz per load
/// shape, charge power per 5% band, the rest of the laptop on battery, and the last EC charger readings. Separate file from
/// settings.json on purpose (its schema and migration chain stay untouched). Thread-safe: the monitor thread learns, the
/// UI thread may reset.
/// </summary>
public sealed class PowerLearn
{
    public const int MaxKeysPerFamily = 128;
    private readonly object _gate = new();
    private LearnData _d = new();
    public bool Dirty { get; private set; }
    public DateTime LastSaveUtc { get; private set; } = DateTime.MinValue;
    public string FileState { get; private set; } = "new";
    /// <summary>Current (or last seen) NVIDIA driver, persisted so the learned keys resolve after a restart.</summary>
    public string? Driver
    {
        get { lock (_gate) return _d.LastDriver.Length > 0 ? _d.LastDriver : null; }
        set { lock (_gate) { string v = value ?? ""; if (v.Length > 0 && v != _d.LastDriver) { _d.LastDriver = v; Dirty = true; } } }
    }

    /// <summary>Test B1's confirmation that the dock-only reading is USB-C (persisted; survives "Reset learned").</summary>
    public bool UsbcConfirmed
    {
        get { lock (_gate) return _d.UsbcConfirmed; }
        set { lock (_gate) { if (_d.UsbcConfirmed != value) { _d.UsbcConfirmed = value; Dirty = true; } } }
    }

    /// <summary>Local clock for dates and "learned Oct 3" labels; tests pin it.</summary>
    public static Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public static string Today => Clock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static void SignStep(ref double m, ref bool init, double x)
    {
        if (!init) { m = x; init = true; return; }
        double step = Math.Max(0.25, 0.02 * Math.Abs(m));
        if (x > m) m += step; else if (x < m) m -= step;
    }

    // ---------- file ----------

    public void Load(string path, Action<string>? log = null)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) { _d = new LearnData(); FileState = "new"; return; }
            try
            {
                var d = JsonSerializer.Deserialize<LearnData>(File.ReadAllText(path)) ?? throw new InvalidDataException("null");
                d.Gpu ??= new(); d.GClk ??= new(); d.Cpu ??= new(); d.Chg ??= new(); d.Rest ??= new(); d.EcLog ??= new(); d.LastDriver ??= "";
                _d = d; FileState = "OK";
            }
            catch (Exception ex)
            {
                string q = path + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Move(path, q); } catch { }
                log?.Invoke($"power-learned.json unreadable ({ex.GetType().Name}: {ex.Message}); kept as {Path.GetFileName(q)}, starting empty");
                _d = new LearnData(); FileState = "quarantined " + Path.GetFileName(q);
            }
        }
    }

    /// <summary>Atomic save (tmp + File.Replace). <paramref name="force"/> = suspend / session end / exit / reset.</summary>
    public bool Save(string path, bool force, Action<string>? log = null)
    {
        string json;
        lock (_gate)
        {
            if (!Dirty && !force) return true;
            if (!force && DateTime.UtcNow - LastSaveUtc < TimeSpan.FromMinutes(5)) return true;
            if (!Dirty) return true;
            json = JsonSerializer.Serialize(_d, new JsonSerializerOptions { WriteIndented = true });
            Dirty = false; LastSaveUtc = DateTime.UtcNow;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            FileState = "OK";
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) Dirty = true;
            FileState = $"save failed ({ex.GetType().Name})";
            log?.Invoke($"power-learned.json save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Forget everything learned; the driver string and the USB-C confirmation are facts, not learned data, and stay.</summary>
    public void Reset() { lock (_gate) { _d = new LearnData { LastDriver = _d.LastDriver, UsbcConfirmed = _d.UsbcConfirmed }; Dirty = true; } }

    // ---------- L1: GPU enforced limit (1 W histogram) ----------

    public void AddGpuLimit(string key, double limitW, int weight)
    {
        if (limitW <= 0 || weight <= 0) return;
        lock (_gate)
        {
            if (!_d.Gpu.TryGetValue(key, out var h)) { Evict(_d.Gpu, e => e.Touched); _d.Gpu[key] = h = new HistEntry(); }
            string bin = Math.Clamp((int)Math.Round(limitW, MidpointRounding.AwayFromZero), 0, 150).ToString(CultureInfo.InvariantCulture);
            h.Hist[bin] = (h.Hist.TryGetValue(bin, out var n) ? n : 0) + weight;
            int before = h.Count;
            h.Count += weight; h.Updated = Today; h.Touched = Now; Dirty = true;
            var p = PowerKeys.ParseGpu(key);
            int need = p?.Src == "DC" ? 15 : 60;
            if (before < need && h.Count >= need && p is { } k)
                _promotions.Add($"learned {(k.Src == "DC" ? "Lbat" : "Lac")} {Percentile(h, k.Src == "DC" ? 0.5 : 0.95)} W for {k.Src}.{k.Mode}{(k.Bst == "x" ? "" : "." + k.Bst)}.{k.Prof}");
        }
    }

    private readonly List<string> _promotions = new();
    /// <summary>"learned Lac 93 W for B230.Balanced.P0" lines since the last call, for the log.</summary>
    public List<string> TakePromotions() { lock (_gate) { var l = _promotions.ToList(); _promotions.Clear(); return l; } }

    private static int Percentile(HistEntry h, double p)
    {
        if (h.Count <= 0) return -1;
        double want = p * h.Count; int acc = 0;
        var bins = h.Hist.Select(kv => (W: int.TryParse(kv.Key, out var w) ? w : -1, N: kv.Value)).Where(b => b.W >= 0).OrderBy(b => b.W).ToList();
        foreach (var b in bins) { acc += b.N; if (acc >= want) return b.W; }
        return bins.Count == 0 ? -1 : bins[^1].W;
    }

    /// <summary>Valid learned limit for a key: AC keys p95 at count &gt;= 60, DC keys p50 at count &gt;= 15.</summary>
    public (double W, string Date, int Count)? GpuLimit(string key)
    {
        lock (_gate)
        {
            if (!_d.Gpu.TryGetValue(key, out var h)) return null;
            bool dc = PowerKeys.ParseGpu(key)?.Src == "DC";
            if (h.Count < (dc ? 15 : 60)) return null;
            int w = Percentile(h, dc ? 0.5 : 0.95);
            return w > 0 ? (w, h.Updated, h.Count) : null;
        }
    }

    /// <summary>Highest valid learned limit among keys matching <paramref name="pred"/> (parsed key parts).</summary>
    public (double W, string Date)? GpuLimitBest(Func<(string Drv, string Src, string Mode, string Bst, string Prof), bool> pred)
    {
        List<string> keys;
        lock (_gate) keys = _d.Gpu.Keys.ToList();
        (double W, string Date)? best = null;
        foreach (var k in keys)
        {
            var p = PowerKeys.ParseGpu(k);
            if (p is null || !pred(p.Value)) continue;
            var v = GpuLimit(k);
            if (v is { } x && (best is null || x.W > best.Value.W)) best = (x.W, x.Date);
        }
        return best;
    }

    // ---------- L2: graphics clock ----------

    public void LearnGClk(string key, double clk, double dt)
    {
        if (clk <= 0) return;
        lock (_gate)
        {
            if (!_d.GClk.TryGetValue(key, out var e)) { Evict(_d.GClk, x => x.Touched); _d.GClk[key] = e = new MedEntry(); }
            double m = e.M; bool init = e.Secs > 0;
            SignStep(ref m, ref init, clk);
            e.M = m; e.Secs += dt; e.Updated = Today; e.Touched = Now; Dirty = true;
        }
    }

    public (double MHz, string Date)? GClk(string key)
    {
        lock (_gate) return _d.GClk.TryGetValue(key, out var e) && e.Secs >= 120 && e.M > 0 ? (e.M, e.Updated) : null;
    }

    // ---------- L3: CPU package W + MHz per load shape ----------

    public void LearnCpu(string key, double w, double mhz, double dt)
    {
        lock (_gate)
        {
            if (!_d.Cpu.TryGetValue(key, out var e)) { Evict(_d.Cpu, x => x.Touched); _d.Cpu[key] = e = new CpuEntry(); }
            int reps = Math.Clamp((int)Math.Round(dt / 2.0), 1, 15);   // dt-weighted: a 30 s closed sample counts like 15 live ones
            bool i1 = e.Secs > 0, i2 = e.Secs > 0 && e.MHz > 0;
            double ew = e.W, em = e.MHz;
            for (int i = 0; i < reps; i++)
            {
                if (w >= 0) SignStep(ref ew, ref i1, w);
                if (mhz > 0) SignStep(ref em, ref i2, mhz);
            }
            e.W = ew; e.MHz = em; e.Secs += dt; e.Updated = Today; e.Touched = Now; Dirty = true;
        }
    }

    public CpuEntry? Cpu(string key)
    {
        lock (_gate) return _d.Cpu.TryGetValue(key, out var e) && e.Secs >= 120 ? new CpuEntry { W = e.W, MHz = e.MHz, Secs = e.Secs, Updated = e.Updated } : null;
    }

    /// <summary>Most recently updated valid CPU entry among keys matching <paramref name="pred"/>.</summary>
    public CpuEntry? CpuBest(Func<(string Src, string Mode, string Bst, string Prof, string Shape), bool> pred)
    {
        lock (_gate)
        {
            CpuEntry? best = null; long bt = long.MinValue;
            foreach (var kv in _d.Cpu)
            {
                var p = PowerKeys.ParseCpu(kv.Key);
                if (p is null || !pred(p.Value) || kv.Value.Secs < 120) continue;
                if (kv.Value.Touched > bt) { bt = kv.Value.Touched; best = new CpuEntry { W = kv.Value.W, MHz = kv.Value.MHz, Secs = kv.Value.Secs, Updated = kv.Value.Updated }; }
            }
            return best;
        }
    }

    // ---------- L4: charge power by SOC band ----------

    public void LearnChg(string src, int bin, double mw)
    {
        if (mw <= 0 || bin < 0 || bin >= BatteryEta.Bins) return;
        string key = PowerKeys.Chg(src, bin);
        lock (_gate)
        {
            if (!_d.Chg.TryGetValue(key, out var e)) { Evict(_d.Chg, x => x.Touched); _d.Chg[key] = e = new ChgEntry(); }
            double m = e.Mw; bool init = e.N > 0;
            SignStep(ref m, ref init, mw);
            e.Mw = m; e.N++; e.Touched = Now; Dirty = true;
        }
    }

    public double ChgBand(string src, int bin)
    {
        lock (_gate) return _d.Chg.TryGetValue(PowerKeys.Chg(src, bin), out var e) && e.N >= 3 ? e.Mw : -1;
    }

    public ChargeBin[]? ChgCurve(string src)
    {
        var c = new ChargeBin[BatteryEta.Bins]; bool any = false;
        lock (_gate)
            for (int b = 0; b < BatteryEta.Bins; b++)
                if (_d.Chg.TryGetValue(PowerKeys.Chg(src, b), out var e)) { c[b] = new ChargeBin { Mw = e.Mw, N = e.N }; any |= e.N >= 3; }
        return any ? c : null;
    }

    // ---------- L5: rest of the laptop ----------

    public void LearnRest(string key, double w, double dt)
    {
        if (w < 0) return;
        lock (_gate)
        {
            if (!_d.Rest.TryGetValue(key, out var e)) { Evict(_d.Rest, x => x.Touched); _d.Rest[key] = e = new RestEntry(); }
            double m = e.W; bool init = e.Secs > 0;
            int reps = Math.Clamp((int)Math.Round(dt / 2.0), 1, 15);
            for (int i = 0; i < reps; i++) SignStep(ref m, ref init, w);
            e.W = m; e.Secs += dt; e.Touched = Now; Dirty = true;
        }
    }

    /// <summary>Learned rest-of-laptop watts for the panel state; on AC (360 Hz) the nearest learned refresh rate is used (U17).</summary>
    public double RestFor(bool panelOn, int hz)
    {
        lock (_gate)
        {
            if (_d.Rest.TryGetValue(PowerKeys.Rest(panelOn, hz), out var exact) && exact.Secs >= 300) return exact.W;
            if (!panelOn) return -1;
            double best = -1; int bestDist = int.MaxValue;
            foreach (var kv in _d.Rest)
            {
                var p = kv.Key.Split('.');
                if (p.Length != 3 || p[1] != "1" || kv.Value.Secs < 300 || !int.TryParse(p[2], out var khz)) continue;
                int dist = Math.Abs(khz - hz);
                if (dist < bestDist) { bestDist = dist; best = kv.Value.W; }
            }
            return best;
        }
    }

    // ---------- L6: EC charger readings ----------

    public void NoteEc(DateTime utc, byte lvl, byte rec, bool ac)
    {
        lock (_gate)
        {
            var last = _d.EcLog.Count > 0 ? _d.EcLog[^1] : null;
            if (last != null && last.Lvl == lvl && last.Rec == rec && last.Ac == ac) return;
            _d.EcLog.Add(new EcEntry { Utc = utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), Lvl = lvl, Rec = rec, Ac = ac });
            while (_d.EcLog.Count > 20) _d.EcLog.RemoveAt(0);
            Dirty = true;
        }
    }

    public List<EcEntry> EcHistory() { lock (_gate) return _d.EcLog.Select(e => new EcEntry { Utc = e.Utc, Lvl = e.Lvl, Rec = e.Rec, Ac = e.Ac }).ToList(); }

    // ---------- labels / summary ----------

    /// <summary>"learned Oct 3", or "learned Oct 3 (older data)" past 30 days.</summary>
    public static string LearnedLabel(string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "learned";
        bool old = (Clock().Date - d.Date).TotalDays > 30;
        return "learned " + d.ToString("MMM d", CultureInfo.InvariantCulture) + (old ? " (older data)" : "");
    }

    public string Summary()
    {
        lock (_gate)
        {
            string top = "";
            foreach (var kv in _d.Gpu.Where(k => PowerKeys.ParseGpu(k.Key) is { } p && p.Src.StartsWith('B') && p.Prof == "P0").OrderByDescending(k => k.Value.Count).Take(1))
            {
                var p = PowerKeys.ParseGpu(kv.Key)!.Value;
                int w = kv.Value.Count >= 60 ? Percentile(kv.Value, 0.95) : -1;
                top = $" ({p.Src}.{p.Mode} Lac {(w > 0 ? w + "W" : "learning")} n={kv.Value.Count})";
            }
            int chgBins = _d.Chg.Count(k => k.Value.N >= 3);
            return $"gpu={_d.Gpu.Count} keys{top} gclk={_d.GClk.Count} cpu={_d.Cpu.Count} chg={chgBins} bins rest={_d.Rest.Count} ec={_d.EcLog.Count} file={FileState} saved={(LastSaveUtc == DateTime.MinValue ? "never" : LastSaveUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))}";
        }
    }

    private void Evict<T>(Dictionary<string, T> fam, Func<T, long> touched)
    {
        while (fam.Count >= MaxKeysPerFamily)
        {
            var oldest = fam.OrderBy(kv => touched(kv.Value)).First().Key;
            fam.Remove(oldest);
        }
    }
}
