namespace BladeCtl.Tray;

/// <summary>
/// Single-instance coordination that is never silent.
///
/// Instance #2 signals instance #1 (show / restore-auto / exit / capture) through named events
/// that a non-elevated launch can open on the elevated instance (same-user DACL, verified).
/// </summary>
public static class SingleInstance
{
    public enum Sig { Show, RestoreAuto, Exit, Capture }

    private const string MutexName = "BladeCtl_SingleInstance_v2";
    private const string LegacyMutexName = "BladeCtl_SingleInstance_9f2c"; // v1 (July 2026 build)
    private static readonly Dictionary<Sig, string> EventNames = new()
    {
        [Sig.Show] = "BladeCtl_Show_v2",
        [Sig.RestoreAuto] = "BladeCtl_RestoreAuto_v2",
        [Sig.Exit] = "BladeCtl_Exit_v2",
        [Sig.Capture] = "BladeCtl_Capture_v2",
    };

    /// <summary>
    /// Path handed from `--capture &lt;png&gt;` to the running instance. Lives NEXT TO THE EXE, not in
    /// %APPDATA%: a sandboxed or virtualised shell can see a copy-on-write view of the profile, so a request
    /// dropped there would be invisible to the real elevated instance. The exe directory is shared by both.
    /// </summary>
    public static string CaptureRequestFile => Path.Combine(AppContext.BaseDirectory, "capture-request.txt");

    /// <summary>Drop this file next to the exe and the running instance writes bladectl-dump.txt + bladectl-window.png beside it within 5 s.</summary>
    public static string DumpRequestFile => Path.Combine(AppContext.BaseDirectory, "dump-request.txt");

    private static Mutex? _mutex;
    private static readonly List<EventWaitHandle> Events = new();
    private static readonly List<RegisteredWaitHandle> Regs = new();

    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool isNew);
        return isNew;
    }

    /// <summary>True while some instance holds the mutex.</summary>
    public static bool IsRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch { return true; }
    }

    /// <summary>True while a v1 (WinForms, July 2026) instance still holds its old mutex. It dies at the next sign-out.</summary>
    public static bool LegacyV1Running()
    {
        try { using var m = Mutex.OpenExisting(LegacyMutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch { return true; }
    }

    /// <summary>Instance #1: listen for signals from later launches. Handler runs on a pool thread.</summary>
    public static void Listen(Action<Sig> handler)
    {
        foreach (var (sig, name) in EventNames)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            Events.Add(ev);
            var s = sig;
            Regs.Add(ThreadPool.RegisterWaitForSingleObject(ev,
                (_, _) => { try { handler(s); } catch (Exception ex) { Log.Error($"{s} signal handler: {ex.Message}"); } },
                null, Timeout.Infinite, false));
        }
    }

    /// <summary>Instance #2: poke the running instance. Returns false if it could not be reached.</summary>
    public static bool Signal(Sig sig)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventNames[sig]);
            ev.Set();
            Log.Write($"second launch -> signalled running instance ({sig})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not signal running instance ({sig}): {ex.Message}");
            return false;
        }
    }

    public static void Release()
    {
        foreach (var r in Regs) { try { r.Unregister(null); } catch { } }
        foreach (var e in Events) { try { e.Dispose(); } catch { } }
        Regs.Clear(); Events.Clear();
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}
