namespace Lumora.Services.Background;

/// <summary>
/// Kern-Obergrenze für Hintergrund-Rechnen (ADR-0074 Nachtrag 0.5.15): parallele Schleifen und ONNX-Sitzungen der
/// Hintergrund-Aufgaben nutzen höchstens die Hälfte der Kerne, damit Bedienung, Lightbox und Galerie immer freie Kerne
/// und freie Plätze im Thread-Pool finden. Arbeit, auf die der Benutzer wartet (Foto verbessern, Gesichter gruppieren
/// auf der Personen-Seite), nutzt weiter alle.
/// </summary>
public static class BackgroundCpu
{
    /// <summary>Höchstens so viele Threads je paralleler Hintergrund-Schleife: die Hälfte der Kerne, mindestens 1.</summary>
    public static int MaxThreads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Rechen-Threads einer ONNX-Sitzung im Hintergrund (Gesichter, Motive): ein Viertel der Kerne, 1–4 — beide laufen
    /// neben Vorschaubildern und Dateilesen; auf der Grafikkarte brauchen sie ohnehin nur wenige.
    /// </summary>
    public static int OnnxThreads => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>Optionen für <see cref="Parallel"/> im Hintergrund.</summary>
    public static ParallelOptions Options(CancellationToken ct) => new() { MaxDegreeOfParallelism = MaxThreads, CancellationToken = ct };
}
