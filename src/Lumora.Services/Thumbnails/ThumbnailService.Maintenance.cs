using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;

namespace Lumora.Services.Thumbnails;

/// <summary>Ergebnis eines Pflege-Durchgangs (<see cref="ThumbnailService.RunMaintenancePassAsync"/>).</summary>
/// <param name="UpgradesQueued">Vorschaubilder älterer Größe, deren Ersatz eingereiht wurde.</param>
/// <param name="UpgradesSkipped">Ältere Vorschaubilder, deren Original fehlt, „fehlt“-markiert oder nur online ist (bleiben bis zum Ansehen).</param>
/// <param name="DimensionsWritten">Dateien, deren Bildmaße aus dem Dateikopf nachgetragen wurden.</param>
public sealed record ThumbnailMaintenanceReport(int UpgradesQueued, int UpgradesSkipped, int DimensionsWritten);

/// <summary>
/// Pflege im Hintergrund (ADR-0043), einmal je Sitzung, beginnt <see cref="MaintenanceStartDelay"/> nach dem ersten
/// Request:
/// <list type="number">
/// <item><b>Ältere Größen ersetzen</b> (<see cref="ThumbnailSizing.LegacySizes"/>): in Stapeln zu
/// <see cref="UpgradeBatchSize"/> nach FileId, jeder Stapel erst, wenn die Warteschlange leer ist — angeforderte
/// (sichtbare) Kacheln der Galerie warten so höchstens einen kleinen Stapel. Fertige neue Vorschaubilder löschen Zeile
/// und Datei der alten Größe (<see cref="ThumbnailService"/>, gleiche Transaktion). Fortschritt: die noch nicht
/// eingereihten zählen in <see cref="PendingCount"/> mit (Statusleiste „Vorschaubilder: N in Warteschlange“).</item>
/// <item><b>Bildmaße nachtragen</b>: Dateien mit neuem Vorschaubild, aber ohne <c>Files.Width/Height</c> — nur der
/// Dateikopf wird gelesen (<see cref="IImageThumbnailRenderer.ReadDimensions"/>), je Stapel eine Transaktion.</item>
/// </list>
/// Beim Start wird nur geplant (eine Zählung, dann Stapel per Index-Bereich), nichts auf einmal eingereiht. Pausiert,
/// solange der Dienst pausiert (Umzug der Katalog-DB) oder ein Scan bzw. Datei-Batch die Bibliothek belegt (Scan-Sperre);
/// Originale, die nur online liegen (OneDrive-Platzhalter), werden nicht angefasst, damit nichts heruntergeladen wird.
/// </summary>
public sealed partial class ThumbnailService
{
    /// <summary>Stapelgröße beim Ersetzen älterer Größen (≈ 0,5 s Arbeit für die Worker).</summary>
    public const int UpgradeBatchSize = 32;

    /// <summary>Stapelgröße beim Nachtragen der Bildmaße (nur Dateiköpfe, eine Transaktion je Stapel).</summary>
    public const int DimensionBatchSize = 200;

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GateBusyDelay = TimeSpan.FromSeconds(2);

    private int _upgradeRemaining;

    /// <summary>
    /// Wartezeit zwischen dem ersten Request (Start der Worker) und der Pflege — der Start der App und die erste Seite
    /// gehen vor. <see cref="Timeout.InfiniteTimeSpan"/> schaltet die automatische Pflege ab (Tests rufen
    /// <see cref="RunMaintenancePassAsync"/> selbst).
    /// </summary>
    public TimeSpan MaintenanceStartDelay { get; init; } = TimeSpan.FromSeconds(20);

    private readonly record struct MaintenanceRow(
        int FileId, int LibraryId, string FolderPath, string Name, MediaType MediaType, DateTime ModifiedAtUtc, bool Missing);

    private void StartMaintenance()
    {
        if (MaintenanceStartDelay == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MaintenanceStartDelay, token).ConfigureAwait(false);
                await RunMaintenancePassAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Beenden
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vorschaubild-Pflege abgebrochen");
            }
        });
    }

    /// <summary>
    /// Ein Pflege-Durchgang: ältere Größen einreihen (Stapel für Stapel bei leerer Warteschlange), danach fehlende
    /// Bildmaße nachtragen. Kehrt zurück, wenn alles eingereiht bzw. geschrieben ist (die letzten Aufträge können noch
    /// laufen). Läuft automatisch einmal je Sitzung; öffentlich für Tests.
    /// </summary>
    public async Task<ThumbnailMaintenanceReport> RunMaintenancePassAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        var (queued, skipped) = await UpgradeLegacyAsync(token).ConfigureAwait(false);
        var dimensions = await BackfillDimensionsAsync(token).ConfigureAwait(false);
        if (queued + skipped + dimensions > 0)
        {
            _logger.LogInformation(
                "Vorschaubild-Pflege: {Queued} ältere Vorschaubilder zum Ersetzen eingereiht, {Skipped} übersprungen (fehlt/nur online), Bildmaße für {Dimensions} Dateien nachgetragen",
                queued, skipped, dimensions);
        }

        return new ThumbnailMaintenanceReport(queued, skipped, dimensions);
    }

    private async Task<(int Queued, int Skipped)> UpgradeLegacyAsync(CancellationToken token)
    {
        var legacySizes = ThumbnailSizing.LegacySizes;
        await WaitForIdleAsync(token).ConfigureAwait(false);
        var total = await AsJobAsync(
            async () =>
            {
                await using var ctx = _factory.Create();
                return await ctx.Thumbnails
                    .CountAsync(t => t.Kind == ThumbnailKind.Still && legacySizes.Contains(t.Size) && t.Status == ThumbnailStatus.Generated, token)
                    .ConfigureAwait(false);
            },
            token).ConfigureAwait(false);

        if (total == 0)
        {
            return (0, 0);
        }

        _logger.LogInformation("Vorschaubilder: {Count} in älterer Größe — werden im Hintergrund durch {Size}-px-Vorschaubilder ersetzt", total, Size);
        Volatile.Write(ref _upgradeRemaining, total);
        var cursor = 0;
        var queued = 0;
        var skipped = 0;
        try
        {
            while (true)
            {
                await WaitForIdleAsync(token).ConfigureAwait(false);
                var batch = await AsJobAsync(() => LoadBatchAsync(legacy: true, cursor, UpgradeBatchSize, token), token).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                if (IsAnyLibraryBusy(batch))
                {
                    await Task.Delay(GateBusyDelay, token).ConfigureAwait(false);
                    continue; // denselben Stapel später noch einmal
                }

                var requests = new List<ThumbnailRequest>(batch.Count);
                var batchSkipped = 0;
                foreach (var row in batch)
                {
                    var path = Path.Combine(row.FolderPath, row.Name);
                    if (row.Missing
                        || row.MediaType == MediaType.Archive
                        || (row.MediaType == MediaType.Video && !_videoRenderer.IsAvailable)
                        || !IsLocallyAvailable(path))
                    {
                        batchSkipped++;
                        continue;
                    }

                    requests.Add(new ThumbnailRequest(row.LibraryId, row.FileId, path, row.MediaType, row.ModifiedAtUtc));
                }

                if (!await RequestCoreAsync(requests, background: true, token).ConfigureAwait(false))
                {
                    continue; // inzwischen pausiert: denselben Stapel nach Resume noch einmal
                }

                cursor = batch[^1].FileId;
                queued += requests.Count;
                skipped += batchSkipped;
                Interlocked.Add(ref _upgradeRemaining, -batch.Count);
            }
        }
        finally
        {
            Volatile.Write(ref _upgradeRemaining, 0);
        }

        return (queued, skipped);
    }

    private async Task<int> BackfillDimensionsAsync(CancellationToken token)
    {
        var cursor = 0;
        var written = 0;
        while (true)
        {
            await WaitForIdleAsync(token).ConfigureAwait(false);
            var batch = await AsJobAsync(() => LoadBatchAsync(legacy: false, cursor, DimensionBatchSize, token), token).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return written;
            }

            if (IsAnyLibraryBusy(batch))
            {
                await Task.Delay(GateBusyDelay, token).ConfigureAwait(false);
                continue;
            }

            cursor = batch[^1].FileId;
            var sizes = new List<(int FileId, DateTime ModifiedAtUtc, int Width, int Height)>(batch.Count);
            foreach (var row in batch)
            {
                var path = Path.Combine(row.FolderPath, row.Name);
                if (!IsLocallyAvailable(path))
                {
                    continue; // fehlt oder nur online: in einer späteren Sitzung
                }

                // Nicht lesbar (z. B. HEIC ohne Windows-Codec): 0 × 0 merken, damit nicht jede Sitzung erneut liest.
                var size = _imageRenderer.ReadDimensions(path) is { Width: > 0, Height: > 0 } read ? read : (0, 0);
                sizes.Add((row.FileId, row.ModifiedAtUtc, size.Item1, size.Item2));
            }

            if (sizes.Count > 0)
            {
                written += await AsJobAsync(() => WriteDimensionsBatchAsync(sizes, token), token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Alle Maße eines Stapels in einer Transaktion (Single-Writer über dieselbe Sperre wie der Status).</summary>
    private async Task<int> WriteDimensionsBatchAsync(IReadOnlyList<(int FileId, DateTime ModifiedAtUtc, int Width, int Height)> sizes, CancellationToken token)
    {
        await _dbWriteLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            await using var tx = await ctx.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            var changed = 0;
            foreach (var (fileId, modifiedAtUtc, width, height) in sizes)
            {
                changed += await WriteDimensionsAsync(ctx, fileId, modifiedAtUtc, width, height, token).ConfigureAwait(false);
            }

            await tx.CommitAsync(token).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            _dbWriteLock.Release();
        }
    }

    /// <param name="legacy">
    /// true: Vorschaubilder älterer Größe (Ersetzen); false: Bilder mit Vorschaubild der aktuellen Größe, aber ohne
    /// Bildmaße (Nachtragen). Beides über den Primärschlüssel der Thumbnails ab <paramref name="afterFileId"/>.
    /// </param>
    private async Task<List<MaintenanceRow>> LoadBatchAsync(bool legacy, int afterFileId, int take, CancellationToken token)
    {
        var legacySizes = ThumbnailSizing.LegacySizes;
        await using var ctx = _factory.Create();
        var thumbs = ctx.Thumbnails.AsNoTracking()
            .Where(t => t.Kind == ThumbnailKind.Still && t.Status == ThumbnailStatus.Generated && t.FileId > afterFileId);
        thumbs = legacy
            ? thumbs.Where(t => legacySizes.Contains(t.Size))
            : thumbs.Where(t => t.Size == Size);
        var files = legacy
            ? ctx.Files.AsNoTracking()
            : ctx.Files.AsNoTracking().Where(f => f.MediaType == MediaType.Image && !f.Missing && (f.Width == null || f.Height == null));

        // Sortiert nach Thumbnails.FileId: der Planer läuft über den Primärschlüssel der Thumbnails ab dem Cursor und
        // holt Files/Folders je Zeile per Primärschlüssel — jeder Stapel kostet so nur seine eigenen Zeilen.
        var rows = await thumbs
            .Join(files, t => t.FileId, f => f.Id, (t, f) => new { t.FileId, f.LibraryId, f.FolderId, f.Name, f.MediaType, f.ModifiedAtUtc, f.Missing })
            .Join(ctx.Folders.AsNoTracking(), x => x.FolderId, d => d.Id, (x, d) => new { x.FileId, x.LibraryId, d.Path, x.Name, x.MediaType, x.ModifiedAtUtc, x.Missing })
            .OrderBy(x => x.FileId)
            .Take(take)
            .ToListAsync(token)
            .ConfigureAwait(false);

        return rows
            .DistinctBy(x => x.FileId)
            .Select(x => new MaintenanceRow(x.FileId, x.LibraryId, x.Path, x.Name, x.MediaType, x.ModifiedAtUtc, x.Missing))
            .ToList();
    }

    /// <summary>
    /// DB-Zugriff der Pflege als „Auftrag“: beginnt nicht, solange der Dienst pausiert (Umzug der Katalog-DB), und
    /// <see cref="PauseAsync"/> wartet auf ihn wie auf einen laufenden Render-Auftrag.
    /// </summary>
    private async Task<T> AsJobAsync<T>(Func<Task<T>> work, CancellationToken token)
    {
        await EnterJobAsync(token).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeJobs);
        }
    }

    /// <summary>Wartet, bis keine Aufträge mehr eingereiht sind und der Dienst nicht pausiert.</summary>
    private async Task WaitForIdleAsync(CancellationToken token)
    {
        while (Volatile.Read(ref _pending) > 0 || IsPaused)
        {
            await Task.Delay(IdlePollInterval, token).ConfigureAwait(false);
        }
    }

    private bool IsPaused
    {
        get
        {
            lock (_pauseLock)
            {
                return _resumed is not null;
            }
        }
    }

    private bool IsAnyLibraryBusy(IReadOnlyList<MaintenanceRow> batch)
        => _scanGate is { } gate && batch.Select(r => r.LibraryId).Distinct().Any(gate.IsBusy);

    /// <summary>
    /// Datei vorhanden und lokal (kein Cloud-Platzhalter, dessen Lesen einen Download auslöste). Seit 0.5.41 eine Weiterleitung
    /// an die zentrale Platzhalter-Erkennung <see cref="Cloud.CloudFiles.IsLocallyAvailable"/> (ADR-0077) — die
    /// Hintergrund-Dienste rufen weiter diese Stelle.
    /// </summary>
    public static bool IsLocallyAvailable(string path) => Cloud.CloudFiles.IsLocallyAvailable(path);
}
