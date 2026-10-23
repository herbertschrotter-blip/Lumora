using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora;

/// <summary>
/// Einsprungpunkt von Lumora.exe (<c>StartupObject</c>, seit 0.4.26). Vor allem anderen — vor Logger, Datenordner-Umzug,
/// Katalog, Single-Instance-Prüfungen, Oberfläche und <c>_hidden</c> — erkennt er den erhöhten Container-Schritt
/// <c>--container-create-vhdx</c> (ADR-0061, Nachtrag 0.4.26) und führt nur ihn aus: kein Fenster, kein Katalog, keine
/// Hintergrunddienste, kein Protokoll, Ergebnis nur als Exit-Code. Sonst startet die App wie bisher.
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
}
