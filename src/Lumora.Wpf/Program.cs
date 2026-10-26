using System.Runtime.InteropServices;
using Lumora.AppConnect;
using Lumora.Services.HiddenLibraries;
using Lumora.Services.HiddenLibraries.Containers;
using Lumora.Services.Infrastructure;

namespace Lumora;

/// <summary>
/// Einsprungpunkt von Lumora.exe (<c>StartupObject</c>, seit 0.4.26). Vor allem anderen — vor Logger, Datenordner-Umzug,
/// Katalog, Single-Instance-Prüfungen, Oberfläche und <c>_hidden</c> — erkennt er den erhöhten Container-Schritt
/// <c>--container-create-vhdx</c> (ADR-0061, Nachtrag 0.4.26) und führt nur ihn aus: kein Fenster, kein Katalog, keine
/// Hintergrunddienste, kein Protokoll, Ergebnis nur als Exit-Code. Danach gilt „eine Lumora je Benutzer“ (App-Verbindung,
/// ADR-0073): läuft schon eine, holt ein weiterer Start ihr Fenster nach vorn und beendet sich. Sonst startet die App.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (TryRunElevatedVerb(args, () => RunVhdxJob, out var exitCode))
        {
            return exitCode;
        }

        // App-Verbindung (ADR-0073): mit _hidden gar nicht, mit LUMORA_APPDATA (Tests) ohne Mutex, sonst eine Instanz je Benutzer
        var participation = AppConnectHost.Decide(
            HiddenModeSwitch.IsRequested(args),
            Lumora.Shell.AppDataRootOverride.ResolveLocation().Mode == AppDataLocationMode.EnvironmentVariable);
        SingleInstance? single = null;
        if (participation == AppConnectParticipation.Normal)
        {
            single = SingleInstance.TryAcquire();
            if (single is null)
            {
                // Die laufende Lumora darf ihr Fenster nach vorn holen (Windows erlaubt das sonst nur dem Vordergrundprozess)
                _ = AllowSetForegroundWindow(AsfwAny);
                if (!SingleInstance.SendShowWindow())
                {
                    // Die laufende Lumora antwortet nicht (startet noch oder hängt) — nicht stumm verschwinden
                    System.Windows.MessageBox.Show(
                        "Lumora läuft bereits, reagiert aber gerade nicht. Bitte kurz warten und das vorhandene Fenster verwenden.",
                        "Lumora", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }

                return 0;
            }
        }

        App.AppConnectStart = new AppConnectStart(participation, single);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// true = die Befehlszeile ist ein erhöhter Schritt; dann ist <paramref name="exitCode"/> sein Ergebnis und die App
    /// startet nicht. <paramref name="job"/> wird nur in diesem Fall erzeugt (Tests geben einen Fake).
    /// </summary>
    internal static bool TryRunElevatedVerb(string[] args, Func<Func<IReadOnlyList<string>, int>> job, out int exitCode)
    {
        exitCode = 0;
        if (!VhdxElevatedCommand.IsRequested(args))
        {
            return false;
        }

        exitCode = job()(args);
        return true;
    }

    /// <summary>Der echte erhöhte Lauf: Windows-Seite nur für die Dauer des Auftrags.</summary>
    private static int RunVhdxJob(IReadOnlyList<string> args)
    {
        using var api = new WindowsVirtualDiskApi();
        return new VhdxElevatedJob(api).Run(args);
    }

    private const int AsfwAny = -1;

#pragma warning disable SYSLIB1054 // LibraryImport bräuchte AllowUnsafeBlocks; DllImport reicht hier.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
#pragma warning restore SYSLIB1054
}

/// <summary>Teilnahme an der App-Verbindung, im Einsprung bestimmt (ADR-0073); <see cref="Single"/> nur bei normalem Start.</summary>
public sealed record AppConnectStart(AppConnectParticipation Participation, SingleInstance? Single);
