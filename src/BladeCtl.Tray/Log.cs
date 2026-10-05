using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BladeCtl.Tray;

public enum LogLevel { Info, Ok, Warn, Error, Trace }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string Tag => Level switch
    {
        LogLevel.Ok => "OK  ",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Trace => "hid ",
        _ => "info",
    };
}

/// <summary>
/// Append-only log with an in-memory ring buffer for the Activity panel.
///
/// v2 lessons: the v1 log went silent for six weeks and nobody could tell whether the app had
/// been running, applying settings, or failing. So this logger
///   - writes a SESSION BANNER at every start (version, exe, args, elevation, user, resolved
///     directories, settings summary) so a cold read of the file tells you what was running;
///   - FALLS BACK to %LOCALAPPDATA% then %TEMP% if the primary directory is not writable, and
///     records which one it ended up in (and why) instead of swallowing the failure;
///   - keeps a HEARTBEAT (DeviceMonitor writes one every 15 minutes) so "alive but idle" is
///     distinguishable from "dead";
///   - rotates at 1 MB and keeps three generations.
/// </summary>
public static class Log
{
    public const long MaxBytes = 1_000_000;
    public const int KeepGenerations = 3;
    public const int RingSize = 400;

    /// <summary>Where settings.json lives: always %APPDATA%\BladeCtl, independent of log fallback.</summary>
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BladeCtl");

    /// <summary>Directory the log is actually being written to (primary or a fallback).</summary>
    public static string Dir { get; private set; } = AppDataDir;

    private static string? _forcedDir;
    /// <summary>The SYSTEM boot task passes the user's log directory so both write the same file.</summary>
    public static void OverrideDir(string dir) { _forcedDir = dir; Dir = dir; _dirResolved = false; }

    /// <summary>Why a fallback directory is in use, or null when the primary is fine.</summary>
    public static string? DirNote { get; private set; }

    public static string FilePath => Path.Combine(Dir, "bladectl.log");

    /// <summary>Last exception swallowed while writing (null if healthy).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Chatty per-transaction HID lines are only written when this is on.</summary>
    public static bool Verbose { get; set; }

    /// <summary>Raised on the writer's thread for every entry (not for Trace). UI must marshal.</summary>
    public static event Action<LogEntry>? EntryAdded;

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    // BOM so PowerShell's Get-Content (ANSI by default) reads the em-dashes correctly; only written to a new file.
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static bool _dirResolved;
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public static IReadOnlyList<LogEntry> Recent => Ring.ToArray();
    public static TimeSpan UptimeSpan => Uptime.Elapsed;

    // ---------- public API ----------

    public static void Write(string msg) => Emit(LogLevel.Info, msg);
    public static void Ok(string msg) => Emit(LogLevel.Ok, msg);
    public static void Warn(string msg) => Emit(LogLevel.Warn, msg);
    public static void Error(string msg) => Emit(LogLevel.Error, msg);

    /// <summary>Verbose-gated write, used for the Core's per-transaction diagnostics.</summary>
    public static void Trace(string msg)
    {
        if (Verbose) Emit(LogLevel.Trace, msg);
    }

    /// <summary>
    /// Log delegate handed to BladeDevice/BladeController. Retry-exhaustion and win32 errors are
    /// always recorded; the noisy "re-sending" lines are verbose-gated.
    /// </summary>
    public static void CoreLog(string msg)
    {
        if (msg.Contains("win32", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("error", StringComparison.OrdinalIgnoreCase))
            Emit(LogLevel.Warn, "[hid] " + msg);
        else
            Trace("[hid] " + msg);
    }

    /// <summary>Session banner. Call once, first thing, with everything a cold read needs.</summary>
    public static void Session(string version, string[] args, bool elevated, IEnumerable<string> extra)
    {
        ResolveDir();
        ArchiveV1Log();
        var sb = new StringBuilder();
        sb.Append("==== SESSION START BladeCtl ").Append(version)
          .Append(" pid=").Append(Environment.ProcessId)
          .Append(" args=[").Append(string.Join(' ', args)).Append(']')
          .Append(" elevated=").Append(elevated)
          .Append(" user=").Append(Environment.UserName)
          .Append(" os=").Append(Environment.OSVersion.Version)
          .Append(" exe=").Append(Environment.ProcessPath ?? "?")
          .Append(" cwd=").Append(Environment.CurrentDirectory);
        Emit(LogLevel.Info, sb.ToString());
        Emit(LogLevel.Info, $"log dir={Dir}{(DirNote is null ? "" : " (FALLBACK: " + DirNote + ")")} appdata={AppDataDir}");
        foreach (var line in extra) Emit(LogLevel.Info, line);
    }

    public static void SessionEnd(string reason)
    {
        Emit(LogLevel.Info, $"==== SESSION END ({reason}) uptime={Uptime.Elapsed:hh\\:mm\\:ss}");
    }

    public static string[] Tail(int lines)
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            return File.ReadLines(FilePath).TakeLast(lines).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---------- plumbing ----------

    private static void Emit(LogLevel level, string msg)
    {
        var entry = new LogEntry(DateTime.Now, level, msg);
        if (level != LogLevel.Trace)
        {
            Ring.Enqueue(entry);
            while (Ring.Count > RingSize && Ring.TryDequeue(out _)) { }
            try { EntryAdded?.Invoke(entry); } catch { }
        }

        try
        {
            lock (Gate)
            {
                ResolveDir();
                Rotate();
                File.AppendAllText(FilePath,
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.Tag}] {msg}{Environment.NewLine}", Utf8Bom);
                LastError = null;
            }
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            // One more try: the directory may have just become unwritable (ACL change, CFA block).
            try
            {
                lock (Gate)
                {
                    _dirResolved = false;
                    ResolveDir(forceProbe: true);
                    File.AppendAllText(FilePath,
                        $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.Tag}] {msg}{Environment.NewLine}", Utf8Bom);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Pick a writable directory: %APPDATA%\BladeCtl, else %LOCALAPPDATA%\BladeCtl, else %TEMP%\BladeCtl.
    /// A write PROBE is performed rather than trusting Directory.Exists, because the v1 failure mode
    /// was precisely "directory exists, writes silently fail".
    /// </summary>
    private static void ResolveDir(bool forceProbe = false)
    {
        if (_dirResolved && !forceProbe) return;

        var candidates = new (string Path, string Label)[]
        {
            (_forcedDir ?? AppDataDir, "primary"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BladeCtl"), "%LOCALAPPDATA%"),
            (Path.Combine(Path.GetTempPath(), "BladeCtl"), "%TEMP%"),
        };

        string? firstFailure = null;
        foreach (var (path, label) in candidates)
        {
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".write-probe");
                File.WriteAllText(probe, DateTime.Now.ToString("o"));
                File.Delete(probe);
                Dir = path;
                DirNote = label == "primary" ? null : $"{label} because {AppDataDir} failed: {firstFailure}";
                _dirResolved = true;
                return;
            }
            catch (Exception ex)
            {
                firstFailure ??= $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        // Nothing writable: keep the primary path so the error surfaces in LastError.
        Dir = AppDataDir;
        DirNote = $"no writable directory ({firstFailure})";
        _dirResolved = true;
    }

    /// <summary>The v1 log has no BOM and a different line format; keep it as bladectl-v1.log rather than mixing.</summary>
    private static void ArchiveV1Log()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return;
                using (var fs = File.OpenRead(FilePath))
                {
                    if (fs.Length >= 3 && fs.ReadByte() == 0xEF && fs.ReadByte() == 0xBB && fs.ReadByte() == 0xBF) return;
                }
                var archive = Path.Combine(Dir, "bladectl-v1.log");
                if (File.Exists(archive)) File.AppendAllText(archive, File.ReadAllText(FilePath));
                else File.Move(FilePath, archive);
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
        }
        catch { }
    }

    private static void Rotate()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            for (int g = KeepGenerations; g >= 1; g--)
            {
                var older = $"{FilePath}.{g}";
                var newer = g == 1 ? FilePath : $"{FilePath}.{g - 1}";
                if (File.Exists(older)) File.Delete(older);
                if (File.Exists(newer)) File.Move(newer, older);
            }
        }
        catch { /* rotation is best-effort; never block the actual write */ }
    }
}
