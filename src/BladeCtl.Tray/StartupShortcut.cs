using System.Runtime.InteropServices;

namespace BladeCtl.Tray;

/// <summary>
/// Creates/removes a Startup-folder shortcut (.lnk) via IShellLink COM — no installer,
/// no registry Run key, no admin. Shortcut lives in the user's Startup folder so BladeCtl
/// autostarts at logon and can be removed just by deleting the .lnk.
/// </summary>
public static class StartupShortcut
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "BladeCtl.lnk");

    public static bool IsEnabled => File.Exists(ShortcutPath);

    /// <summary>
    /// Create/remove the Startup shortcut. Returns false with a reason instead of failing silently —
    /// this was the one action in the app that could never report a problem.
    /// </summary>
    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            if (enabled) Create(); else Remove();

            // Verify rather than assume.
            if (File.Exists(ShortcutPath) != enabled)
            {
                error = enabled ? "shortcut was not created" : "shortcut could not be deleted";
                Log.Write("startup shortcut verification failed: " + error);
                return false;
            }
            Log.Write($"startup shortcut {(enabled ? "created" : "removed")}: {ShortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Write("startup shortcut error: " + error);
            return false;
        }
    }

    /// <summary>
    /// If the shortcut points at a different exe than the one running (user moved/copied it),
    /// return the stale target so the app can offer to repoint it.
    /// </summary>
    public static string? StaleTarget()
    {
        try
        {
            if (!File.Exists(ShortcutPath)) return null;
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(ShortcutPath, 0);
            var sb = new System.Text.StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            var self = Environment.ProcessPath ?? "";
            return string.Equals(target, self, StringComparison.OrdinalIgnoreCase) ? null : target;
        }
        catch { return null; }
    }

    private static void Remove()
    {
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }

    private static void Create()
    {
        var exe = Environment.ProcessPath ?? "";
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetArguments("--tray");   // autostart should not pop the window every logon
        link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
        link.SetDescription("BladeCtl — Razer Blade fan/power/RGB control");
        ((IPersistFile)link).Save(ShortcutPath, false);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
