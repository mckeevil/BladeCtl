using System.Globalization;

namespace BladeCtl.Core.Power;

/// <summary>
/// The foreground-app part of the dump, Copy diagnostics, the power-learn log line and --power-probe (rule R4, spec 4.7):
/// whether the app is on the private list is never written anywhere. A private app in front prints exactly what an app that
/// could not be read prints (0 cores, not busy, CPU row C2), which is also what an idle app prints, so no file or log line
/// can be lined up with the focused window to learn what the list holds.
/// </summary>
public static class PowerDump
{
    /// <summary>Cores of the app in front as far as the verdict may use them: 0 when it is private or unreadable.</summary>
    public static double FgCoresShown(PowerSample s) => s.FgKnown && !s.FgPrivate ? s.FgCores : 0;

    /// <summary>CPU row code for the logs: C1 (private, numbers only) prints as C2_Idle, the code of an unreadable app.</summary>
    public static string RowName(CpuCode? code) => code is null ? "-" : code == CpuCode.C1_Private ? nameof(CpuCode.C2_Idle) : code.Value.ToString();

    public static string FgFields(PowerSample s, CpuCode? row, Derived? d) =>
        $"row={RowName(row)} fgCores={FgCoresShown(s).ToString("F2", CultureInfo.InvariantCulture)} busy1T={(d?.Busy1T == true ? 1 : 0)} busyMT={(d?.BusyMT == true ? 1 : 0)}";
}
