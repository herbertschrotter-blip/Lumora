using Lumora.Imaging.Protocol;

namespace Lumora.Imaging.Worker;

/// <summary>
/// Start und Grenzen des Hilfsprozesses „Zusammenfügen“ (ADR-0078, Bau Stufe 1 Teil A). Der Hilfsprozess liegt neben
/// Lumora.exe; <see cref="ExePathVariable"/> zeigt für Tests auf eine andere Datei.
/// </summary>
public sealed record ImagingWorkerOptions
{
    /// <summary>Umgebungsvariable mit dem vollen Pfad zu <see cref="WorkerProtocol.WorkerExeName"/> (nur Tests/Entwicklung).</summary>
    public const string ExePathVariable = "LUMORA_IMAGING_WORKER";

    /// <summary>Pfad zur Programmdatei; null = neben Lumora.exe bzw. aus <see cref="ExePathVariable"/>.</summary>
    public string? ExePath { get; init; }

    /// <summary>Bis zur ersten Zeile („ready“) — OpenCV lädt die native DLL (≈ 70 MB).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ohne jede Meldung des Hilfsprozesses gilt eine Arbeit als hängend (einzelne OpenCV-Aufrufe dauern bei großen Kugeln
    /// Minuten). Danach wird der Prozess beendet und die Arbeit scheitert — Lumora läuft weiter.
    /// </summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Höchstdauer einer Arbeit (volle Auflösung einer Drohnen-Kugel ≈ 2 min, Reserve für langsame Rechner).</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Nach „Abbrechen“ so lange auf die Antwort warten, dann den Prozess beenden.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Weitere Umgebungsvariablen (Tests: Prüfhaken).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Pfad zur Programmdatei des Hilfsprozesses.</summary>
    public string ResolveExePath()
        => ExePath
           ?? (System.Environment.GetEnvironmentVariable(ExePathVariable) is { Length: > 0 } fromEnv ? fromEnv : null)
           ?? Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerExeName);
}
