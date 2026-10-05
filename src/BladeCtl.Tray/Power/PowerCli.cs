using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BladeCtl.Core;
using BladeCtl.Core.Power;

namespace BladeCtl.Tray.Power;

/// <summary>
/// Read-only command lines for the Power card (spec 12.7 and verification):
///   --power-probe &lt;seconds&gt;   live headless sampling at 2 s (A1 tests). No mutex, no tray, no window, no settings
///                               applied or saved, no learned data written. EC: GETs only. NVML only in D0 (R1).
///   --power-preview &lt;png&gt; [scenario[+open]] [width]   renders the card from a synthetic model, touching no hardware at all.
/// </summary>
internal static class PowerCli
{
    /// <summary>Settings read straight from the file (no migration, never saved), or defaults.</summary>
    private static Settings ReadSettingsOnly()
    {
        try { return File.Exists(Settings.FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath)) ?? new Settings() : new Settings(); }
        catch { return new Settings(); }
    }

    public static int Probe(string? secondsArg)
    {
        int seconds = int.TryParse(secondsArg, out var n) ? Math.Clamp(n, 4, 3600) : 120;
        string dir = AppContext.BaseDirectory;
        string outPath = Path.Combine(dir, $"power-probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        void Out(string line) { Console.WriteLine(line); sb.AppendLine(line); }
        // Keep the probe's log lines out of the live instance's log file.
        Log.OverrideDir(Path.Combine(dir, "probe-log"));
        Log.EntryAdded += e => Out($"  log {e.Time:HH:mm:ss.fff} {e.Level}: {e.Message}");

        Out($"BladeCtl {Program.Version} --power-probe {seconds} s  started {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (read-only: EC GETs only, NVML only in D0)");
        int? d0 = GpuPower.NvidiaDState();
        Out($"dGPU D-state before anything else: {(d0?.ToString() ?? "unreadable")} (1 = D0, 4 = D3)");
        using var dev = BladeDevice.Find(Log.CoreLog);
        using var ctl = dev != null ? new BladeController(dev, Log.CoreLog) : null;
        Out(ctl != null ? $"Blade EC: {dev!.DevicePath} firmware {ctl.GetFirmwareVersion() ?? "?"}" : "Blade EC: no control device answered (EC reads skipped)");
        if (ctl != null)
        {
            var a = ctl.GetAdapterWattage();
            Out($"EC 0x07/0x8C adapter wattage: {(a is { } x ? $"{x.Level:X2}-{x.Rec:X2} = {(RazerAdapter.Watts(x.Level)?.ToString() ?? "?")} W of {(RazerAdapter.Watts(x.Rec)?.ToString() ?? "?")} W recommended" : "no answer")}");
            var b7 = ctl.GetExternalPowerStatus();
            Out($"EC 0x00/0xB7 external power status: {(b7 is byte v ? v.ToString("X2") : "no answer")}");
        }
        var luid = GpuPower.NvidiaLuid();
        Out($"dGPU LUID: {(luid is { } l ? $"high 0x{(uint)l.High:X8} low 0x{l.Low:X8}" : "unreadable")}  display on dGPU: {(luid is { } l2 ? PanelRefresh.AnyTargetOnAdapter(l2.Low, l2.High)?.ToString() ?? "?" : "?")}");
        var caps = PowerPolicy.Read();
        Out($"powrprof: max state AC {caps.AcMaxPct}% DC {caps.DcMaxPct}%, boost AC {caps.AcBoost} DC {caps.DcBoost}; power mode AC {caps.AcMode} {{{caps.AcModeGuid}}} DC {caps.DcMode} {{{caps.DcModeGuid}}}");

        var settings = ReadSettingsOnly();
        using var sampler = new PowerSampler(settings, probe: true) { ControllerSource = () => ctl, Live = true };
        var inv = CultureInfo.InvariantCulture;
        string F(double v, string f = "F1") => v < 0 ? "-" : v.ToString(f, inv);
        sampler.ProbeSink = (s, vo, cpu, text) =>
        {
            var d = vo?.D;
            Out($"t={s.T,6:F1} D={s.DState} ac={(s.OnAC ? 1 : 0)} src={s.Class}{(s.AdapterW > 0 ? s.AdapterW.ToString() : "")} rate={(s.RateKnown ? s.RateMw.ToString() : "?")}mW soc={s.Soc} " +
                // R4: the foreground app prints as the verdict may use it (0 cores when private or unreadable), never a list flag
                $"pkg={F(s.PkgW)}W f={F(s.FreqMHz, "F0")} lim={F(s.PerfLimitPct, "F0")} flags={F(s.PerfLimitFlags, "F0")} fg={PowerDump.FgCoresShown(s).ToString("F2", inv)} " +
                $"busy={F(s.DBusy, "F0")}% disp={(s.DisplayOnD ? 1 : 0)} fans={s.FanRpm1?.ToString() ?? "?"}/{s.FanRpm2?.ToString() ?? "?"} nv={s.NvState}{(s.NvOk ? "" : "")} " +
                (d is { Nv: true } ? $"P={F(d.P)} L={F(d.L)} Ldef={F(d.Ldef, "F0")} Lmax={F(d.Lmax, "F0")} U={F(d.U, "F0")} r=0x{d.ReasonsOr:X} d74={F(d.Duty74, "F2")} d269={F(d.Duty269, "F2")}{(d.Alias269 ? "=alias" : "")} 0x20|0x4={d.With04And20}/{d.With04} psrc={d.Psrc} T={d.T} " : "") +
                $"raw={vo?.Raw.ToString() ?? "-"} shown={vo?.Shown.ToString() ?? "-"} cpu={PowerDump.RowName(cpu?.Code)} | {text}");
        };
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var tickStart = DateTime.UtcNow;
            int? ds = GpuPower.NvidiaDState();
            PerfMode? mode = null; byte? cb = null, gb = null; double? cpuC = null, gpuC = null;
            if (ctl != null)
            {
                mode = ctl.GetPowerState(1)?.Mode; cb = ctl.GetCpuBoost(); gb = ctl.GetGpuBoost();
                var th = ctl.GetThermalReading(); cpuC = th?.Cpu; gpuC = th?.Gpu;
            }
            sampler.OnTick(new TickInput(ctl, ds, mode, cb, gb, cpuC, gpuC));
            if (ctl != null) Out($"        fans: real (0x0D/0x88) {ctl.GetFanCurrentRpm(1)?.ToString() ?? "?"}/{ctl.GetFanCurrentRpm(2)?.ToString() ?? "?"} rpm vs setpoint (0x0D/0x81) {ctl.GetFanRpm(1)?.ToString() ?? "?"}/{ctl.GetFanRpm(2)?.ToString() ?? "?"} rpm; EC temps CPU {cpuC?.ToString("F0", inv) ?? "?"} GPU {gpuC?.ToString("F0", inv) ?? "?"}");
            var wait = TimeSpan.FromSeconds(2) - (DateTime.UtcNow - tickStart);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
        Out("--- summary ---");
        foreach (var line in sampler.DumpLines()) Out(line);
        sampler.Live = false;
        try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); Console.WriteLine("written: " + outPath); }
        catch (Exception ex) { Console.WriteLine("could not write " + outPath + ": " + ex.Message); }
        return 0;
    }

    // ---------- --power-preview ----------

    public static int Preview(string? png, string? scenario, string? widthArg)
    {
        png = Path.GetFullPath(png ?? Path.Combine(AppContext.BaseDirectory, "power-preview.png"));
        double width = double.TryParse(widthArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 372 + 36;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BladeCtl;component/Theme.xaml", UriKind.Relative) });
        var vm = new PowerVm(new PowerSampler(new Settings(), probe: true), Dispatcher.CurrentDispatcher, _ => Task.CompletedTask, () => Task.CompletedTask);
        // "battery+open": the same scenario with the battery details (sparkline, health, sources) open
        bool open = scenario?.EndsWith("+open", StringComparison.OrdinalIgnoreCase) == true;
        if (open) scenario = scenario![..^5];
        vm.Apply(Scenario(scenario ?? "barrel"));
        vm.DetailsOpen = open;
        var card = new PowerCard { DataContext = vm, Width = width };
        var host = new Border { Background = (Brush)app.Resources["BgBrush"], Padding = new Thickness(16), Child = card };
        host.Measure(new Size(width + 32, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        host.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        using (var fs = File.Create(png)) enc.Save(fs);
        Console.WriteLine($"{png}  card {card.ActualWidth:F0} x {card.ActualHeight:F0} DIP (scenario {scenario ?? "barrel"})");
        return 0;
    }

    /// <summary>Synthetic models built with the same wording code the sampler uses; numbers from the spec's examples.</summary>
    private static PowerView Scenario(string name)
    {
        var refs = new GpuRefs { LrefW = 93, LrefLearned = true, LrefDate = "2026-10-03", BarrelLacW = 93, LbatW = 35, LbatDate = "2026-10-03" };
        string footer = "Battery: Windows · CPU: Intel RAPL · GPU: NVIDIA, only while awake · charger: Razer EC · verdict: BladeCtl";
        var spark = new List<SparkPt>();
        switch (name)
        {
            case "battery":
            {
                var d = new Derived { Nv = true, HaveP = true, P = 34.6, L = 35, Ldef = 80, Lmax = 105, U = 97, Lref = 93, LrefLearned = true, PkgW = 18, Freq = 1840 };
                var cpu = new CpuRowOut(CpuCode.C6_Profile, "HELD BACK", "warn", "Battery profile: Razer Custom, CPU Low. Plugged in: ~45 W (learned Oct 3)", false, "18 W");
                var ctx = new WordCtx { St = St.BATTERY_LIMITED, Sub = Sub.Power, D = d, Lv = refs, OnAC = false, Class = SupplyClass.Battery, ProfileEngaged = true, Cpu = cpu };
                for (int i = 0; i <= 60; i++) spark.Add(new SparkPt(600 - i * 10, i < 20 ? 30 : -58 - (i % 3), i < 20 ? 0 : 1, false, i == 20));
                var gw = Wording.GpuRow(St.BATTERY_LIMITED, Sub.Power, d, refs, false, SupplyClass.Battery, 0, true);
                return new PowerView
                {
                    Header = new("battery", "Battery", "Windows reports battery power.", false, "Windows"),
                    Strip = new("warn", "cap", Wording.Status(ctx), Wording.Action(ctx), false, PowerButtons.FullPower, PowerButtons.Label(PowerButtons.FullPower)),
                    Flow = new FlowModel(false, "battery", 0, false, "", "", false, -58, "−58 W", BatteryModel.PctPerHour(-58, 69449), false, false, 54, "~35 left",
                                         18, 34.6, 5.4, false, "CPU 18 · GPU 35 · 5", "Running on battery; battery discharging at 58 W, 54%, about 35 minutes left"),
                    Cpu = new RowModel(true, "CPU", "18 W", cpu.Word, cpu.Kind, "cap", cpu.Reason, new BarModel(60, 18, false, -1, false, "", 45, false, false, "cpu", "")),
                    Gpu = new RowModel(true, "GPU", gw.Numbers, gw.Word, gw.Kind, gw.Glyph, gw.Reason, new BarModel(105, 34.6, false, 35, false, "limit", 93, false, false, "gpu", "", ShowMissing: true)),
                    Chips = new[] { new ChipModel("Battery profile · CPU Low · GPU Low · 120 Hz", true), new ChipModel("NVIDIA service stopped (saves ~20 W)", false), new ChipModel("Windows battery cap · CPU 80% · boost off", false) },
                    Spark = new SparkModel(spark, 60, "+30 / −60 W", "battery rate, last 10 minutes"),
                    BatteryLine = "Battery 54% · ~35 min left at 58 W", HealthLine = "Full charge 69.4 of 80.0 Wh (87%)", Footer = footer,
                };
            }
            case "usbc":
            {
                var d = new Derived { Nv = true, HaveP = true, P = 84, L = 85, Ldef = 80, Lmax = 105, U = 98, DrainW = 9, PkgW = 38, Freq = 3800 };
                var cpu = new CpuRowOut(CpuCode.C4_Charger, "HELD BACK", "warn", "On USB-C? (65 W, Razer EC): 38 W vs ~45 W on the 230 W charger (learned Oct 3)", false, "38 W");
                var ctx = new WordCtx { St = St.CHARGER_LIMITED, Sub = Sub.Draining, D = d, Lv = refs, OnAC = true, Class = SupplyClass.UsbC, AdapterW = 65, Supply = SupplyState.CANT_KEEP_UP, Cpu = cpu, UsbcConfirmed = false };
                for (int i = 0; i <= 60; i++) spark.Add(new SparkPt(600 - i * 10, i < 30 ? 12 : -9, i < 30 ? 0 : 2, i < 10, false));
                var gw = Wording.GpuRow(St.CHARGER_LIMITED, Sub.Draining, d, refs, true, SupplyClass.UsbC, 65, true, usbcConfirmed: false);
                return new PowerView
                {
                    Header = new("usbc", "USB-C? · 65 W", "Razer EC reports a 65 W supply (recommended 230 W). USB-C is inferred: Synapse's logs on this laptop show 65 W on USB-C", true, "Razer EC"),
                    Strip = new("warn", "cap", Wording.Status(ctx), Wording.Action(ctx), false, null, ""),
                    Flow = new FlowModel(true, "usbc", 65, false, "65 W", "rated", true, -9, "−9 W", BatteryModel.PctPerHour(-9, 69449), true, false, 60, "",
                                         38, 84, -1, true, "CPU 38 · GPU 84 · rest ?", "USB-C 65 W supply; battery draining at 9 W, 60%"),
                    Cpu = new RowModel(true, "CPU", "38 W", cpu.Word, cpu.Kind, "cap", cpu.Reason, new BarModel(60, 38, false, -1, false, "", 45, false, false, "cpu", "")),
                    Gpu = new RowModel(true, "GPU", gw.Numbers, gw.Word, gw.Kind, gw.Glyph, gw.Reason, new BarModel(105, 84, false, 85, false, "limit", 93, false, false, "gpu", "", ShowMissing: true)),
                    Supply = new RowModel(true, "Supply", "65 W", "CAN'T KEEP UP", "warn", "cap", Wording.DrainSentence(9), new BarModel(240, -1, true, 60, false, "~usable", 230, false, true, "supply", "", ShowMissing: true)),
                    Chips = new[] { new ChipModel("USB-C? · 65 W (Razer EC)", true), new ChipModel("Razer Balanced", false) },
                    Spark = new SparkModel(spark, 20, "+12 / −9 W", "battery rate, last 10 minutes"),
                    BatteryLine = "Battery 60% · plugged in but not charging (draining 9 W, charger can't keep up)", HealthLine = "Full charge 69.4 of 80.0 Wh (87%)", Footer = footer,
                };
            }
            default:   // barrel, light load: today's numbers
            {
                var d = new Derived { Nv = true, HaveP = true, P = 18, L = 91.4, Ldef = 80, Lmax = 105, U = 12, PkgW = 22, Freq = 3432 };
                var cpu = new CpuRowOut(CpuCode.C2_Idle, "IDLE", "info", "", false, "22 W");
                var ctx = new WordCtx { St = St.LIGHT, D = d, Lv = refs, OnAC = true, Class = SupplyClass.Barrel, AdapterW = 230, Supply = SupplyState.CHARGING, Cpu = cpu };
                for (int i = 0; i <= 60; i++) spark.Add(new SparkPt(600 - i * 10, i < 15 ? -18 : 37 + (i % 4) * 0.5, i < 15 ? 1 : 0, false, i == 15));
                var gw = Wording.GpuRow(St.LIGHT, Sub.None, d, refs, true, SupplyClass.Barrel, 230, true);
                return new PowerView
                {
                    Header = new("plug", "230 W charger", "Razer EC: adapter 230 W, recommended 230 W (read 19:02:11)", false, "Razer EC"),
                    Strip = new("ok", "check", Wording.Status(ctx), Wording.Action(ctx), false, null, ""),
                    Flow = new FlowModel(true, "plug", 100, true, "~100 W", "est. supply", false, 37, "+37 W", BatteryModel.PctPerHour(37, 69449), false, true, 66, "full ~1 h 05",
                                         22, 18, 13, true, "CPU 22 · GPU 18 · ~13", "230 W charger supplying about 100 W; battery charging at 37 W, 66%, full about 1 hour 05"),
                    Cpu = new RowModel(true, "CPU", "22 W", cpu.Word, cpu.Kind, "sleep", "", new BarModel(60, 22, false, -1, false, "", 45, false, false, "cpu", "")),
                    Gpu = new RowModel(true, "GPU", gw.Numbers, gw.Word, gw.Kind, gw.Glyph, gw.Reason, new BarModel(105, 18, false, 91.4, false, "limit", 93, false, false, "gpu", "")),
                    Chips = Array.Empty<ChipModel>(),
                    Spark = new SparkModel(spark, 40, "+39 / −18 W", "battery rate, last 10 minutes"),
                    BatteryLine = "Battery 66% · full in ~1 h 05 min (charging 37 W)", HealthLine = "Full charge 69.4 of 80.0 Wh (87%)", Footer = footer,
                };
            }
        }
    }
}
