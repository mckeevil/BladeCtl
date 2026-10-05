using System.Globalization;
using System.Text;

namespace BladeCtl.Core.Power;

/// <summary>
/// Optional private-apps list (%APPDATA%\BladeCtl\private-apps.txt). When the app in front matches it, the Power card
/// neither uses nor learns from its CPU numbers, and nothing about the match is shown, logged or stored.
/// Each line is a 64-bit FNV-1a hash (16 hex digits, optional 0x) of one lower-case name token over its UTF-16LE bytes,
/// so the file itself names nothing; <c>BladeCtl.exe --private-hash &lt;name&gt;</c> prints the line for a name. Lines
/// starting with # are comments. A token is a path segment, a piece of one split on - _ . or space, or any run of
/// letters and digits. No file means no private apps; a file that exists but cannot be read makes every app private.
/// </summary>
public sealed class PrivateApps
{
    private readonly HashSet<ulong> _hashes;
    private PrivateApps(HashSet<ulong> hashes, bool failedClosed) { _hashes = hashes; FailedClosed = failedClosed; }

    public static PrivateApps None { get; } = new(new HashSet<ulong>(), false);
    public int Count => _hashes.Count;
    /// <summary>The file exists but could not be read: every app counts as private.</summary>
    public bool FailedClosed { get; }

    public static PrivateApps Load(string path)
    {
        if (!File.Exists(path)) return None;
        try
        {
            var set = new HashSet<ulong>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) line = line[2..];
                if (ulong.TryParse(line, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h)) set.Add(h);
            }
            return new PrivateApps(set, false);
        }
        catch { return new PrivateApps(new HashSet<ulong>(), true); }
    }

    /// <summary>64-bit FNV-1a over the UTF-16LE bytes of <paramref name="token"/> (no case folding here).</summary>
    public static ulong Hash(string token)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (char c in token) { h ^= (byte)(c & 0xFF); h *= 0x100000001b3UL; h ^= (byte)(c >> 8); h *= 0x100000001b3UL; }
        return h;
    }

    /// <summary>The line to put in the file for <paramref name="name"/>.</summary>
    public static string Line(string name) => Hash(name.Trim().ToLowerInvariant()).ToString("x16", CultureInfo.InvariantCulture);

    private bool Hit(string tok) => !string.IsNullOrEmpty(tok) && _hashes.Contains(Hash(tok));

    public bool Is(params string[] texts)
    {
        if (FailedClosed) return true;
        if (_hashes.Count == 0) return false;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var low = text.ToLowerInvariant();
            foreach (var seg in low.Split('\\', '/', '"'))
            {
                if (Hit(seg.Trim())) return true;
                foreach (var tok in seg.Split('-', '_', '.', ' ')) if (Hit(tok)) return true;
            }
            // every run of letters/digits too, so names inside arguments (=, :, ;, parentheses, quotes, tabs) still match
            var run = new StringBuilder();
            foreach (char c in low + " ") { if (char.IsLetterOrDigit(c)) run.Append(c); else { if (Hit(run.ToString())) return true; run.Clear(); } }
        }
        return false;
    }
}
