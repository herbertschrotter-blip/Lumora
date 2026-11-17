using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Domain.Enums;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// Auftrag zur Thumbnail-Erzeugung für eine einzelne Datei. Die aufrufende Schicht
/// (Gallery) liefert alle nötigen Quell-Infos mit, sodass der Worker ohne weiteren
/// DB-Roundtrip rendern kann.
/// </summary>
public sealed record ThumbnailRequest(
    int LibraryId,
    int FileId,
    string SourcePath,
    MediaType MediaType,
    DateTime SourceModifiedAtUtc);

/// <summary>Meldung, dass ein Thumbnail bereitsteht (neu erzeugt oder bereits im Cache gültig).</summary>
public sealed record ThumbnailReadyEventArgs(int LibraryId, int FileId, string ThumbnailPath);

/// <summary>
/// Hintergrund-Pipeline für Thumbnail-Erzeugung gemäß ADR-0007 / PLAN Kap. 13.
/// V0.1: eine Größe (320px), getrennte Worker-Pools für Bild (WIC) und Video (FFmpeg),
/// In-Memory-Dedupe, DB-Status-Lifecycle + atomic Write in den Renderern.
/// Die volle 4-Prio-Multi-Channel-Queue (Background-Batching für 500k) folgt in einem
/// Folge-Slice — dieses Interface trägt sie ohne Bruch.
/// </summary>
public interface IThumbnailService
{
    /// <summary>Kantenlänge (px) der V0.1-Thumbnails.</summary>
    int ThumbnailSize { get; }

    /// <summary>Anzahl der eingereihten oder gerade laufenden Aufträge (Statusleiste, ADR-0013). Thread-sicher lesbar.</summary>
    int PendingCount { get; }

    /// <summary>
    /// Wird gefeuert, sobald ein Thumbnail bereitsteht. Achtung: läuft auf einem
    /// Worker-Thread — Abonnenten müssen selbst auf den UI-Thread dispatchen.
    /// </summary>
    event Action<ThumbnailReadyEventArgs>? ThumbnailReady;

    /// <summary>
    /// Reiht eine Batch von Aufträgen ein (typischerweise die sichtbare Grid-Page).
    /// Bereits gültig vorhandene Thumbnails werden sofort via <see cref="ThumbnailReady"/>
    /// gemeldet, der Rest wird dedupliziert in die Worker-Queues geschrieben.
    /// </summary>
    Task RequestThumbnailsAsync(
        IReadOnlyList<ThumbnailRequest> requests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verwirft den In-Memory-Zustand (erledigt/fehlgeschlagen) für diese Dateien, sodass der nächste
    /// Request wieder gegen DB und Cache prüft — z. B. nach Papierkorb, Rescan oder wenn der Benutzer
    /// einen erneuten Versuch wünscht. Laufende Aufträge bleiben unberührt. Standard: keine Wirkung.
    /// </summary>
    void Invalidate(IReadOnlyList<int> fileIds)
    {
    }

    /// <summary>
    /// Verwirft den gesamten In-Memory-Zustand (alle erledigten/fehlgeschlagenen Dateien) — z. B. nachdem der
    /// Thumbnail-Cache in den Einstellungen geleert wurde. Laufende Aufträge bleiben unberührt. Standard: keine Wirkung.
    /// </summary>
    void InvalidateAll()
    {
    }

    /// <summary>
    /// Hält die Worker an (Umzug der Katalog-DB, ADR-0026): kein neuer Auftrag beginnt, neue Requests werden ohne
    /// DB-Zugriff zurückgestellt; kehrt zurück, sobald die laufenden Aufträge fertig sind. Eingereihte Aufträge bleiben
    /// in der Queue. Standard: keine Wirkung.
    /// </summary>
    Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Hebt <see cref="PauseAsync"/> auf und reicht die zurückgestellten Requests sofort nach. Standard: keine Wirkung.</summary>
    void Resume()
    {
    }

    /// <summary>
    /// Vorschaubild auf Vorrat (0.5.40): erzeugt es für diese Datei sofort auf dem Thread des Aufrufers — gleicher Renderer,
    /// gleiche Cache-Datei, gleicher DB-Status wie ein Galerie-Auftrag, aber ohne Warteschlange, ohne Vorrang und ohne
    /// <see cref="PendingCount"/>. Der Aufrufer prüft vorher, dass die Datei lokal liegt (kein Cloud-Platzhalter). Standard:
    /// nichts tun (<see cref="ThumbnailStockOutcome.Skipped"/>).
    /// </summary>
    Task<ThumbnailStockOutcome> CreateStockAsync(ThumbnailRequest request, CancellationToken ct = default)
        => Task.FromResult(ThumbnailStockOutcome.Skipped);
}

/// <summary>Ergebnis von <see cref="IThumbnailService.CreateStockAsync"/>.</summary>
public enum ThumbnailStockOutcome
{
    /// <summary>Vorschaubild neu erzeugt.</summary>
    Generated,

    /// <summary>Renderer gescheitert (DB-Status „Failed“, Versuch gezählt).</summary>
    Failed,

    /// <summary>Nichts zu tun: schon gültig, gerade in Arbeit, endgültig gescheitert, Archiv oder kein FFmpeg.</summary>
    Skipped,

    /// <summary>Dienst pausiert (Katalog-Umzug) — später noch einmal.</summary>
    Deferred,
}
