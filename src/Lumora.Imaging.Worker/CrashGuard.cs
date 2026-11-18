using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumora.Imaging.Worker;

/// <summary>
/// Abstürze des Hilfsprozesses enden sofort und still (ADR-0078, 0.5.48) — nur mit prozesslokalen Mitteln, nie über Registry
/// oder Systemeinstellungen:
/// <list type="bullet">
/// <item><c>SetErrorMode</c>: kein Fehlerbericht- bzw. „Datenträger fehlt“-Dialog, der den Prozess offen hielte (Lumora wartete
/// sonst bis zur Stille-Grenze).</item>
/// <item>Verwaltete Ausnahme ohne Fänger: sofort <c>TerminateProcess</c> mit <see cref="ExitUnhandled"/> — die Laufzeit kommt
/// nicht mehr dazu, die Windows-Fehlerberichterstattung zu starten.</item>
/// </list>
/// Was bleibt: ein nativer Absturz in OpenCV beendet die .NET-Laufzeit selbst per Fast-Fail (0xC0000409) — ein eigener Filter für
/// unbehandelte Ausnahmen kommt dabei nie zum Zug (gemessen 0.5.48, daher nicht eingebaut). Der Prozess endet ohne Dialog in
/// Sekundenbruchteilen; die Windows-Fehlerberichterstattung kann aber einen Bericht außerhalb des Containers ablegen (PLAN
/// Kap. 15). Die Abbilder der .NET-Laufzeit schaltet Lumora beim Start über die Umgebung ab.
/// </summary>
internal static class CrashGuard
{
    /// <summary>Exit-Code nach einer verwalteten Ausnahme ohne Fänger.</summary>
    public const int ExitUnhandled = 5;

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;

    public static void Install()
    {
        try
        {
            SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
            AppDomain.CurrentDomain.UnhandledException += (_, _) => Terminate(ExitUnhandled);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // ohne Schutz weiter — der Prozess rechnet trotzdem
        }
    }

    /// <summary>Hintergrund (weniger Threads als Kerne): niedrigere Priorität — wirkt sofort, auch mitten in einer Arbeit.</summary>
    public static void ApplyPriority(int threads)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = threads < Environment.ProcessorCount ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Priorität bleibt
        }
    }

    private static void Terminate(int code) => TerminateProcess(GetCurrentProcess(), (uint)code);

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte Quellgenerator-Marshalling; diese Aufrufe sind blittable.
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);
#pragma warning restore SYSLIB1054
}
