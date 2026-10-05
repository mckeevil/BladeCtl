using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BladeCtl.Tray;

/// <summary>
/// One virtual Xbox 360 controller on the ViGEm bus (the open-source driver Synapse bundled; it must be
/// installed). Exists only while controller mode runs; games see a controller appear when
/// it is switched on and vanish when it is switched off.
/// </summary>
public sealed class VirtualPad : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private short _x, _y;
    private readonly bool[] _buttons = new bool[32];
    private byte _lt, _rt;

    public bool Connected => _pad != null;

    public static readonly string[] ButtonNames = { "A", "B", "X", "Y", "LB", "RB", "LS", "RS", "Start", "Back", "DUp", "DDown", "DLeft", "DRight", "LT", "RT", "Guide" };

    public static int ButtonIndex(string name) => Array.FindIndex(ButtonNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static Xbox360Button? ButtonFor(int idx) => idx switch
    {
        0 => Xbox360Button.A, 1 => Xbox360Button.B, 2 => Xbox360Button.X, 3 => Xbox360Button.Y,
        4 => Xbox360Button.LeftShoulder, 5 => Xbox360Button.RightShoulder, 6 => Xbox360Button.LeftThumb, 7 => Xbox360Button.RightThumb,
        8 => Xbox360Button.Start, 9 => Xbox360Button.Back, 10 => Xbox360Button.Up, 11 => Xbox360Button.Down, 12 => Xbox360Button.Left, 13 => Xbox360Button.Right,
        16 => Xbox360Button.Guide, _ => null,
    };

    public bool Connect(out string error)
    {
        error = "";
        try
        {
            _client = new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.Connect();
            Submit();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            try { _client?.Dispose(); } catch { }
            _client = null; _pad = null;
            return false;
        }
    }

    /// <summary>x, y in -1..1 (y positive = up).</summary>
    public void SetLeftStick(double x, double y)
    {
        if (_pad == null) return;
        short sx = (short)Math.Round(Math.Clamp(x, -1, 1) * 32767), sy = (short)Math.Round(Math.Clamp(y, -1, 1) * 32767);
        if (sx == _x && sy == _y) return;
        _x = sx; _y = sy;
        try { _pad.SetAxisValue(Xbox360Axis.LeftThumbX, sx); _pad.SetAxisValue(Xbox360Axis.LeftThumbY, sy); Submit(); } catch { }
    }

    public void SetButton(int idx, bool on)
    {
        if (_pad == null || idx < 0 || idx >= _buttons.Length || _buttons[idx] == on) return;
        _buttons[idx] = on;
        try
        {
            if (idx == 14) { _lt = on ? (byte)255 : (byte)0; _pad.SetSliderValue(Xbox360Slider.LeftTrigger, _lt); }
            else if (idx == 15) { _rt = on ? (byte)255 : (byte)0; _pad.SetSliderValue(Xbox360Slider.RightTrigger, _rt); }
            else if (ButtonFor(idx) is Xbox360Button b) _pad.SetButtonState(b, on);
            Submit();
        }
        catch { }
    }

    public void ReleaseAll()
    {
        for (int i = 0; i < _buttons.Length; i++) if (_buttons[i]) SetButton(i, false);
        SetLeftStick(0, 0);
    }

    private void Submit() { try { _pad?.SubmitReport(); } catch { } }

    public void Dispose()
    {
        try { ReleaseAll(); } catch { }
        try { _pad?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _pad = null; _client = null;
    }
}
