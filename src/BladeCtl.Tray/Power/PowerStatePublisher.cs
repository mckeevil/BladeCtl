using System.Text.Json;

namespace BladeCtl.Tray.Power;

/// <summary>
/// %APPDATA%\BladeCtl\power-state.json (spec 13.2), next to settings.json, which GPU Glance can read to name the charger
/// and see the battery profile. Written atomically, only when something changed, at most every 10 s. No names in it.
/// </summary>
internal sealed class PowerStatePublisher
{
    public static string FilePath => Path.Combine(Log.AppDataDir, "power-state.json");

    public sealed record Doc(bool OnAC, int EcAdapterW, int EcRecommendedW, string Class, string RazerMode, int? CpuBoost, int? GpuBoost,
                             bool BatteryProfileEngaged, bool FullPowerUntilAc, bool NvSvcStoppedByBladeCtl);

    private Doc? _written, _pending;
    private DateTime _lastWrite = DateTime.MinValue;
    public string State { get; private set; } = "not written";

    public void Publish(Doc doc, bool force = false)
    {
        if (doc == _written && !force) { _pending = null; return; }
        _pending = doc;
        if (!force && DateTime.UtcNow - _lastWrite < TimeSpan.FromSeconds(10)) return;
        Write(doc);
    }

    /// <summary>Called every tick: writes a deferred change once 10 s have passed.</summary>
    public void Flush() { if (_pending != null && _pending != _written && DateTime.UtcNow - _lastWrite >= TimeSpan.FromSeconds(10)) Write(_pending); }

    /// <summary>Suspend / exit: write a deferred change now, so GPU Glance never reads a stale charger or flag.</summary>
    public void FlushNow() { if (_pending != null && _pending != _written) Write(_pending); }

    private void Write(Doc d)
    {
        try
        {
            var o = new Dictionary<string, object?>
            {
                ["v"] = 1, ["utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                ["onAC"] = d.OnAC, ["ecAdapterW"] = d.EcAdapterW, ["ecRecommendedW"] = d.EcRecommendedW, ["class"] = d.Class,
                ["razerMode"] = d.RazerMode, ["cpuBoost"] = d.CpuBoost, ["gpuBoost"] = d.GpuBoost,
                ["batteryProfileEngaged"] = d.BatteryProfileEngaged, ["fullPowerUntilAc"] = d.FullPowerUntilAc, ["nvSvcStoppedByBladeCtl"] = d.NvSvcStoppedByBladeCtl,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(o));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            _written = d; _pending = null; _lastWrite = DateTime.UtcNow; State = $"written {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { State = "write failed (" + ex.GetType().Name + ")"; _lastWrite = DateTime.UtcNow; }
    }
}
