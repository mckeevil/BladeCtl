using System.Text.Json;

namespace BladeCtl.Core;

/// <summary>One analog key: Razer's analog key id (what the depth stream reports), the USB HID usage, and what SendInput needs.</summary>
public sealed record AnalogKey(byte Id, string Name, int Usage, ushort Scan, bool Extended, ushort Vk);

/// <summary>
/// Synapse's analog key-id table for the Huntsman V2 Analog (extracted 2026-09-05 from its cached device
/// module, huntsman-analog-keys.json) joined with the USB-HID-usage to PS/2-scan-code map that SendInput needs.
/// </summary>
public static class HuntsmanKeys
{
    public static readonly IReadOnlyDictionary<byte, AnalogKey> ById;
    public static readonly IReadOnlyDictionary<string, AnalogKey> ByName;   // case-insensitive, with aliases
    public static readonly int Count;

    static HuntsmanKeys()
    {
        var byId = new Dictionary<byte, AnalogKey>();
        var byName = new Dictionary<string, AnalogKey>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = typeof(HuntsmanKeys).Assembly.GetManifestResourceStream("BladeCtl.Core.huntsman-analog-keys.json");
            if (s != null)
            {
                using var doc = JsonDocument.Parse(s);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    int id = e.GetProperty("analogKeyID").GetInt32();
                    if (id <= 0 || id > 255) continue;
                    string name = e.GetProperty("name").GetString() ?? "";
                    int usage = e.GetProperty("hid").GetInt32();
                    if (!ScanFor(usage, out var scan, out var ext, out var vk)) continue;
                    var k = new AnalogKey((byte)id, name, usage, scan, ext, vk);
                    if (!byId.ContainsKey((byte)id)) byId[(byte)id] = k;
                    if (!byName.ContainsKey(name)) byName[name] = k;
                }
            }
        }
        catch { }

        void Alias(string alias, string name) { if (byName.TryGetValue(name, out var k) && !byName.ContainsKey(alias)) byName[alias] = k; }
        Alias("Left Shift", "Shift"); Alias("LShift", "Shift"); Alias("RShift", "Right Shift");
        Alias("Left Ctrl", "Ctrl"); Alias("LCtrl", "Ctrl"); Alias("RCtrl", "Right Ctrl"); Alias("Control", "Ctrl");
        Alias("Left Alt", "Alt"); Alias("LAlt", "Alt"); Alias("RAlt", "Right Alt");
        Alias("Win", "Windows"); Alias("Super", "Windows"); Alias("Escape", "Esc"); Alias("Return", "Enter");
        Alias("Spacebar", "Space"); Alias("CapsLock", "Caps Lock"); Alias("Backspace", "Backspace");
        Alias("PgUp", "Page Up"); Alias("PgDn", "Page Down"); Alias("Del", "Delete"); Alias("Ins", "Insert");
        Alias("PrtSc", "Print Screen"); Alias("Apps", "Menu");

        ById = byId; ByName = byName; Count = byId.Count;
    }

    /// <summary>Parse "W A S D = 1.0 / 0.8" style lines into key ids. Returns the keys named on one line, or null if none match.</summary>
    public static List<AnalogKey>? ParseNames(string names)
    {
        var list = new List<AnalogKey>();
        foreach (var raw in names.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string n = raw.Trim();
            if (ByName.TryGetValue(n, out var k)) list.Add(k);
            else if (n.Length == 1 && ByName.TryGetValue(n.ToUpperInvariant(), out k)) list.Add(k);
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>USB HID keyboard usage -> scan code set 1 (+extended flag) and virtual key.</summary>
    public static bool ScanFor(int u, out ushort scan, out bool ext, out ushort vk)
    {
        scan = 0; ext = false; vk = 0;
        switch (u)
        {
            case >= 0x04 and <= 0x1D: scan = LetterScan[u - 0x04]; vk = (ushort)('A' + (u - 0x04)); return true;
            case >= 0x1E and <= 0x26: scan = (ushort)(0x02 + (u - 0x1E)); vk = (ushort)('1' + (u - 0x1E)); return true;
            case 0x27: scan = 0x0B; vk = '0'; return true;
            case 0x28: scan = 0x1C; vk = 0x0D; return true;          // Enter
            case 0x29: scan = 0x01; vk = 0x1B; return true;          // Esc
            case 0x2A: scan = 0x0E; vk = 0x08; return true;          // Backspace
            case 0x2B: scan = 0x0F; vk = 0x09; return true;          // Tab
            case 0x2C: scan = 0x39; vk = 0x20; return true;          // Space
            case 0x2D: scan = 0x0C; vk = 0xBD; return true;          // -
            case 0x2E: scan = 0x0D; vk = 0xBB; return true;          // =
            case 0x2F: scan = 0x1A; vk = 0xDB; return true;          // [
            case 0x30: scan = 0x1B; vk = 0xDD; return true;          // ]
            case 0x31: case 0x32: scan = 0x2B; vk = 0xDC; return true; // \ and non-US #
            case 0x33: scan = 0x27; vk = 0xBA; return true;          // ;
            case 0x34: scan = 0x28; vk = 0xDE; return true;          // '
            case 0x35: scan = 0x29; vk = 0xC0; return true;          // `
            case 0x36: scan = 0x33; vk = 0xBC; return true;          // ,
            case 0x37: scan = 0x34; vk = 0xBE; return true;          // .
            case 0x38: scan = 0x35; vk = 0xBF; return true;          // /
            case 0x39: scan = 0x3A; vk = 0x14; return true;          // Caps Lock
            case >= 0x3A and <= 0x45:                                 // F1-F12
                scan = u <= 0x43 ? (ushort)(0x3B + (u - 0x3A)) : u == 0x44 ? (ushort)0x57 : (ushort)0x58; vk = (ushort)(0x70 + (u - 0x3A)); return true;
            case 0x46: scan = 0x37; ext = true; vk = 0x2C; return true; // Print Screen
            case 0x47: scan = 0x46; vk = 0x91; return true;             // Scroll Lock
            case 0x48: scan = 0x45; vk = 0x13; return true;             // Pause (sent by virtual key)
            case 0x49: scan = 0x52; ext = true; vk = 0x2D; return true; // Insert
            case 0x4A: scan = 0x47; ext = true; vk = 0x24; return true; // Home
            case 0x4B: scan = 0x49; ext = true; vk = 0x21; return true; // Page Up
            case 0x4C: scan = 0x53; ext = true; vk = 0x2E; return true; // Delete
            case 0x4D: scan = 0x4F; ext = true; vk = 0x23; return true; // End
            case 0x4E: scan = 0x51; ext = true; vk = 0x22; return true; // Page Down
            case 0x4F: scan = 0x4D; ext = true; vk = 0x27; return true; // Right
            case 0x50: scan = 0x4B; ext = true; vk = 0x25; return true; // Left
            case 0x51: scan = 0x50; ext = true; vk = 0x28; return true; // Down
            case 0x52: scan = 0x48; ext = true; vk = 0x26; return true; // Up
            case 0x53: scan = 0x45; vk = 0x90; return true;             // Num Lock
            case 0x54: scan = 0x35; ext = true; vk = 0x6F; return true; // KP /
            case 0x55: scan = 0x37; vk = 0x6A; return true;             // KP *
            case 0x56: scan = 0x4A; vk = 0x6D; return true;             // KP -
            case 0x57: scan = 0x4E; vk = 0x6B; return true;             // KP +
            case 0x58: scan = 0x1C; ext = true; vk = 0x0D; return true; // KP Enter
            case >= 0x59 and <= 0x61: scan = KpScan[u - 0x59]; vk = (ushort)(0x61 + (u - 0x59)); return true; // KP 1-9
            case 0x62: scan = 0x52; vk = 0x60; return true;             // KP 0
            case 0x63: scan = 0x53; vk = 0x6E; return true;             // KP .
            case 0x64: scan = 0x56; vk = 0xE2; return true;             // non-US backslash
            case 0x65: scan = 0x5D; ext = true; vk = 0x5D; return true; // Menu
            case 0xE0: scan = 0x1D; vk = 0xA2; return true;             // Left Ctrl
            case 0xE1: scan = 0x2A; vk = 0xA0; return true;             // Left Shift
            case 0xE2: scan = 0x38; vk = 0xA4; return true;             // Left Alt
            case 0xE3: scan = 0x5B; ext = true; vk = 0x5B; return true; // Left Win
            case 0xE4: scan = 0x1D; ext = true; vk = 0xA3; return true; // Right Ctrl
            case 0xE5: scan = 0x36; vk = 0xA1; return true;             // Right Shift
            case 0xE6: scan = 0x38; ext = true; vk = 0xA5; return true; // Right Alt
            case 0xE7: scan = 0x5C; ext = true; vk = 0x5C; return true; // Right Win
            default: return false;
        }
    }

    private static readonly ushort[] LetterScan = { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
    private static readonly ushort[] KpScan = { 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 };
}
