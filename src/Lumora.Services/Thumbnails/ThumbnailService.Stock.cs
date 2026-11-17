using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Domain.Enums;

namespace Lumora.Services.Thumbnails;

/// <summary>
/// Vorschaubilder auf Vorrat (0.5.40, ADR-0074 Nachtrag 0.5.40): <see cref="CreateStockAsync"/> erzeugt ein Vorschaubild
/// sofort auf dem Thread des Aufrufers (<see cref="ThumbnailStockService"/>) — mit demselben Renderer, derselben Cache-Datei
/// und demselben DB-Status wie ein Auftrag der Galerie, aber ohne Warteschlange: Galerie-Aufträge überholen ihn so immer, und
/// er zählt weder in <see cref="PendingCount"/> noch als Vordergrund-Arbeit. Doppelarbeit verhindert der gemeinsame
/// In-Memory-Zustand: läuft die Datei schon (Galerie, Pflege) oder ist sie in dieser Sitzung fertig bzw. gescheitert, tut der
/// Aufruf nichts. Nach Erfolg wird der Eintrag wie bei der Pflege verworfen (500 000 Dateien bleiben nicht im Speicher).
/// </summary>
public sealed partial class ThumbnailService
{
    public async Task<ThumbnailStockOutcome> CreateStockAsync(ThumbnailRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0 || request.MediaType == MediaType.Archive)
        {
            return ThumbnailStockOutcome.Skipped;
        }

        if (IsPaused)
        {
            return ThumbnailStockOutcome.Deferred; // Katalog zieht um (ADR-0026) oder Cache wird geleert: später noch einmal
        }

        if (request.MediaType == MediaType.Video && !_videoRenderer.IsAvailable)
        {
            return ThumbnailStockOutcome.Skipped;
        }

        if (!TryClaimForStock(request))
        {
            return ThumbnailStockOutcome.Skipped;
        }

        EnsureStarted(); // Pflege und .tmp-Bereinigung laufen auch dann, wenn noch keine Galerie angefragt hat
        try
        {
            await EnterJobAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseStockClaim(request);
            throw;
        }

        try
        {
            bool settled;
            try
            {
                settled = await IsSettledInCatalogAsync(request, ct).ConfigureAwait(false);
            }
            catch
            {
                ReleaseStockClaim(request); // Katalog gesperrt/belegt, Abbruch: sonst bliebe die Datei für die Sitzung „in Arbeit“
                throw;
            }

            if (settled)
            {
                ReleaseStockClaim(request);
                return ThumbnailStockOutcome.Skipped; // inzwischen erzeugt (z. B. von der Pflege) oder endgültig gescheitert
            }

            if (Cloud.CloudFiles.IsOnlineOnly(request.SourcePath))
            {
                ReleaseStockClaim(request);
                return ThumbnailStockOutcome.Skipped; // inzwischen nur online: nie auf Vorrat, nie das Original lesen (ADR-0077)
            }

            // ProcessJobAsync wirft nicht (Fehler → „Failed“, Abbruch beim Beenden → Zustand verworfen)
            if (await ProcessJobAsync(request, queued: false).ConfigureAwait(false))
            {
                return ThumbnailStockOutcome.Generated;
            }

            return _cts.IsCancellationRequested ? ThumbnailStockOutcome.Skipped : ThumbnailStockOutcome.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref _activeJobs);
        }
    }

    /// <summary>
    /// Merkt die Datei als „in Arbeit (Vorrat)“. false = läuft schon, in dieser Sitzung fertig (Cache-Datei vorhanden) oder
    /// gescheitert (für die Sitzung gesperrt, außer äußere Ursache nach <see cref="RetryCooldown"/>).
    /// </summary>
    private bool TryClaimForStock(ThumbnailRequest request)
    {
        if (_states.TryGetValue(request.FileId, out var known))
        {
            if (known.Outcome == JobOutcome.Queued)
            {
                return false;
            }

            if (known.SourceModifiedAtUtc == request.SourceModifiedAtUtc)
            {
                var settled = known.Outcome == JobOutcome.Done
                    ? File.Exists(BuildCachePath(request.LibraryId, request.FileId))
                    : !(known.Retryable && DateTime.UtcNow - known.AtUtc >= RetryCooldown);
                if (settled)
                {
                    return false;
                }
            }

            _states.TryRemove(new KeyValuePair<int, JobState>(request.FileId, known)); // veraltet: neu prüfen
        }

        return _states.TryAdd(
            request.FileId,
            new JobState(JobOutcome.Queued, request.SourceModifiedAtUtc, DateTime.UtcNow, Retryable: false, Background: true));
    }

    private void ReleaseStockClaim(ThumbnailRequest request)
    {
        if (_states.TryGetValue(request.FileId, out var state) && state is { Outcome: JobOutcome.Queued, Background: true })
        {
            _states.TryRemove(new KeyValuePair<int, JobState>(request.FileId, state));
        }
    }

    /// <summary>
    /// Zeile der aktuellen Größe schon gültig (gleiche Dateiversion, Cache-Datei vorhanden) oder endgültig gescheitert
    /// (<see cref="MaxAttempts"/>)? Ein Zugriff über den Primärschlüssel — billig gegen ein Rendern.
    /// </summary>
    private async Task<bool> IsSettledInCatalogAsync(ThumbnailRequest request, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var row = await ctx.Thumbnails
            .FindAsync(new object[] { request.FileId, Size, ThumbnailKind.Still }, ct)
            .ConfigureAwait(false);
        return row switch
        {
            null => false,
            { Status: ThumbnailStatus.Generated } => row.SourceModifiedAtUtc == request.SourceModifiedAtUtc && File.Exists(row.CachePath),
            { Status: ThumbnailStatus.Failed } => row.AttemptCount >= MaxAttempts,
            _ => false,
        };
    }
}
