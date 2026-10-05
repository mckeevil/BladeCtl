using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace BladeCtl.Tray.Power;

/// <summary>
/// View model for the Power card. Updated only from <see cref="PowerSampler.Published"/> (monitor thread) through
/// Dispatcher.BeginInvoke, and only the properties whose values changed are raised, so an unchanged tick redraws nothing.
/// </summary>
public sealed class PowerVm : INotifyPropertyChanged
{
    private readonly Dispatcher _disp;
    private readonly PowerSampler _sampler;
    private readonly Func<string?, Task> _runButton;
    private readonly Func<Task> _resetLearned;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(n); return true;
    }

    public PowerVm(PowerSampler sampler, Dispatcher disp, Func<string?, Task> runButton, Func<Task> resetLearned)
    {
        _sampler = sampler; _disp = disp; _runButton = runButton; _resetLearned = resetLearned;
        _sampler.Published += v => _disp.BeginInvoke(() => Apply(v));
        // The click runs the button the user saw: the id bound as the parameter must still be the strip's id, and it must have
        // been on screen for at least 0.6 s (a verdict change can land between the button being drawn and the click).
        ButtonCommand = new RelayCommand(async p =>
        {
            var id = p as string;
            if (id == null || id != Strip.ButtonId || DateTime.UtcNow - _buttonSince < TimeSpan.FromMilliseconds(600)) return;
            await _runButton(id);
        });
        ResetLearnedCommand = new RelayCommand(async () => await _resetLearned());
    }

    /// <summary>Battery history / health / sources open (session only, closed at start; spec 11.1 collapse order).</summary>
    private static bool s_detailsOpen;
    public bool DetailsOpen { get => s_detailsOpen; set { if (s_detailsOpen == value) return; s_detailsOpen = value; Raise(); } }

    public ICommand ButtonCommand { get; }
    public ICommand ResetLearnedCommand { get; }

    private const string Waiting = "Waiting for the first reading";
    private HeaderModel _header = new("unknown", "Reading…", Waiting, false, "");
    public HeaderModel Header { get => _header; private set => Set(ref _header, value); }
    private StripModel _strip = new("grey", "unknown", "Reading the power state…", "Open on the Blade view; nothing is read while the window is closed beyond a 30 s battery sample.", false, null, "");
    private DateTime _buttonSince = DateTime.MinValue;
    public StripModel Strip
    {
        get => _strip;
        private set
        {
            string? before = _strip.ButtonId;
            if (!Set(ref _strip, value)) return;
            if (value.ButtonId != before) _buttonSince = DateTime.UtcNow;
            Raise(nameof(HasButton)); Raise(nameof(HasAction));
        }
    }
    public bool HasButton => _strip.ButtonId != null;
    public bool HasAction => _strip.Action.Length > 0;
    private FlowModel? _flow;
    public FlowModel? Flow { get => _flow; private set => Set(ref _flow, value); }
    private RowModel _cpu = RowModel.Hidden, _gpu = RowModel.Hidden, _supply = RowModel.Hidden;
    public RowModel Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public RowModel Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public RowModel Supply { get => _supply; private set => Set(ref _supply, value); }
    private IReadOnlyList<ChipModel> _chips = Array.Empty<ChipModel>();
    public IReadOnlyList<ChipModel> Chips { get => _chips; private set { if (!_chips.SequenceEqual(value)) { _chips = value; Raise(); Raise(nameof(HasChips)); } } }
    public bool HasChips => _chips.Count > 0;
    private SparkModel _spark = SparkModel.Empty;
    public SparkModel Spark { get => _spark; private set => Set(ref _spark, value); }
    private string _batteryLine = "", _batteryTip = Waiting, _healthLine = "", _footer = "";
    public string BatteryLine { get => _batteryLine; private set => Set(ref _batteryLine, value); }
    public string BatteryTip { get => _batteryTip; private set => Set(ref _batteryTip, value.Length > 0 ? value : Waiting); }
    public string HealthLine { get => _healthLine; private set => Set(ref _healthLine, value); }
    public string Footer { get => _footer; private set => Set(ref _footer, value); }

    public void Apply(PowerView v)
    {
        Header = v.Header; Strip = v.Strip; Flow = v.Flow; Cpu = v.Cpu; Gpu = v.Gpu; Supply = v.Supply; Chips = v.Chips; Spark = v.Spark;
        BatteryLine = v.BatteryLine; BatteryTip = v.BatteryTip; HealthLine = v.HealthLine; Footer = v.Footer;
    }
}
