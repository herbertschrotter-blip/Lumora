using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data.RawSql;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Library;
using Lumora.Services.Messaging;

namespace Lumora.Services.Scanning;

public interface IScanService
{
    /// <summary>Scan mit Standardoptionen (<see cref="ScanOptions.Default"/>).</summary>
    Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Scan mit Optionen — z. B. <see cref="ScanOptions.ConfirmMassMissing"/>, wenn der Benutzer nach
    /// <see cref="ScanOutcome.MassMissingBlocked"/> „Als fehlend markieren“ gewählt hat.
    /// </summary>
    Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Eine Wurzel der Bibliothek, wie der Scanner sie sieht (ADR-0020).</summary>
internal sealed record ScanRoot(int Id, string Path, LibraryRootRole Role);

/// <summary>
/// Orchestriert einen Scan über alle Wurzeln der Bibliothek (ADR-0020): Wurzeln laden, jede mit Timeout prüfen
/// (PLAN Kap. 12), dann je nach Modus
/// <list type="bullet">
/// <item>Fresh/ClearAndRescan: alle Wurzeln müssen erreichbar sein (ein Offline-Laufwerk darf den Katalog nie
/// leeren); jede Wurzel traversieren, dann in EINER Transaktion (<see cref="FullScanCatalogWriter"/>, ADR-0028)
/// bei ClearAndRescan die Benutzerdaten sichern und die alten Zeilen ersetzen, Folders (mit RootId) + Files per
/// Raw-SQLite-Bulk-Insert schreiben, Favoriten/Bewertungen/Tags/Kategorie-Werte zurückschreiben
/// (<see cref="UserDataCarryOver"/>, ADR-0023) und FTS5 komplett aufbauen — Abbruch oder Fehler ändern nichts;</item>
/// <item>Incremental: je Wurzel ein Diff gegen den Katalog mit stabilen FileIds, Offline- und Mass-Missing-Guard
/// je Wurzel (<see cref="IncrementalScanRunner"/>); eine nicht erreichbare Wurzel wird übersprungen, die übrigen
/// werden abgeglichen.</item>
/// </list>
/// Fortschritt geht an das optionale IProgress und gedrosselt als ScanProgressMessage an den Messenger. Am Ende
/// ScanCompletedMessage (Completed, MassMissingBlocked) bzw. ScanAbortedMessage (LibraryOffline, Abbruch, Fehler);
/// der Incremental-Scan sendet zusätzlich FilesChangedMessages (Added/Metadata/Removed) für Galerie und Ordnerbaum.
/// </summary>
public sealed class ScanService : IScanService
{
    private readonly ILibraryContextFactory _contextFactory;
    private readonly FolderEnumerator _enumerator;
    private readonly BulkScanWriter _bulkWriter;
    private readonly FtsIndexer _ftsIndexer;
    private readonly ILibraryService _libraryService;
    private readonly ILogger<ScanService> _logger;
    private readonly IMessenger _messenger;
    private readonly IScanGate _scanGate;
    private readonly Exclusions.IScanExclusionStore _exclusions;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="scanGate">Optional (Tests: eigene Instanz); produktiv der gemeinsame Singleton (auch für die Ordnerüberwachung).</param>
    /// <param name="exclusions">Ausschlüsse beim Einlesen (ADR-0066); ohne Angabe aus der Settings-Tabelle desselben Katalogs.</param>
    public ScanService(
        ILibraryContextFactory contextFactory,
        FolderEnumerator enumerator,
        BulkScanWriter bulkWriter,
        FtsIndexer ftsIndexer,
        ILibraryService libraryService,
        ILogger<ScanService> logger,
        IMessenger? messenger = null,
        IScanGate? scanGate = null,
        Exclusions.IScanExclusionStore? exclusions = null)
    {
        _contextFactory = contextFactory;
        _enumerator = enumerator;
        _bulkWriter = bulkWriter;
        _ftsIndexer = ftsIndexer;
        _libraryService = libraryService;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _scanGate = scanGate ?? new ScanGate();
        _exclusions = exclusions ?? new Exclusions.ScanExclusionStore(contextFactory, _messenger);
    }

    public Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
        => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

    public async Task<ScanResult> ScanAsync(
        int libraryId,
        ScanMode mode,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Enumeration und Raw-SQLite-Persistenz sind synchrone I/O-Arbeit → nie auf dem UI-Thread laufen lassen.
        // Das Token geht nur in den Kern (nicht an Task.Run), damit auch ein bereits abgebrochener Aufruf die
        // ScanAbortedMessage sendet und Statusleiste/Seiten den Scan-Zustand sauber beenden.
        return await Task.Run(() => ScanCoreAsync(libraryId, mode, options, progress, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<ScanResult> ScanCoreAsync(
        int libraryId,
        ScanMode mode,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var reporter = new ScanProgressReporter(libraryId, progress, _messenger);
        try
        {
            // Nie zwei Scans derselben Bibliothek gleichzeitig (manuell, Entpacken, Ordnerüberwachung): der spätere wartet.
            // Im try, damit auch ein beim Warten abgebrochener Aufruf seine ScanAbortedMessage sendet.
            using var lease = await _scanGate.AcquireAsync(libraryId, options.GateCallerId, ct).ConfigureAwait(false);
            var result = mode == ScanMode.Incremental
                ? await RunIncrementalScanAsync(libraryId, options, reporter, ct).ConfigureAwait(false)
                : await RunFullScanAsync(libraryId, mode, options, reporter, ct).ConfigureAwait(false);

            if (result.Outcome == ScanOutcome.LibraryOffline)
            {
                _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: false, LibraryOfflineException.Describe(result.RootPath), LibraryOffline: true));
            }
            else
            {
                _messenger.Send(new ScanCompletedMessage(libraryId, result));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scan abgebrochen: Library={LibraryId}", libraryId);
            _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: true, "Scan abgebrochen."));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan fehlgeschlagen: Library={LibraryId}", libraryId);
            _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: false, ex.Message));
            throw;
        }
    }

    // ---- Incremental ----------------------------------------------------------------------------

    private async Task<ScanResult> RunIncrementalScanAsync(int libraryId, ScanOptions options, ScanProgressReporter reporter, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var roots = await LoadRootsAsync(libraryId, ct).ConfigureAwait(false);
        var exclusions = await LoadExclusionsAsync(libraryId, ct).ConfigureAwait(false); // ADR-0066
        var runner = new IncrementalScanRunner(_contextFactory, _enumerator, _bulkWriter, _logger);

        var outcomes = new List<(ScanRoot Root, IncrementalScanRunner.Outcome Outcome)>();
        string? offlineRoot = null;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            // ADR-0068: „Neu einlesen“ eines Ordners — nur die Wurzel, unter der er liegt
            if (options.SubtreePath is { } subtree && !IncrementalScanRunner.IsUnder(subtree, root.Path))
            {
                continue;
            }

            // Vorbedingung je Wurzel: erreichbar (Timeout lokal 1 s, UNC 3 s) — sonst keine Katalogänderung für sie
            if (!await RootAvailabilityProbe.IsAvailableAsync(root.Path, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Incremental-Scan für Wurzel übersprungen, offline: {Root}", root.Path);
                offlineRoot ??= root.Path;
                continue;
            }

            // Start-Ids vor jeder Wurzel neu lesen: die vorige Wurzel hat bereits Zeilen geschrieben.
            var (nextFolderId, nextFileId) = ReadNextIds();
            var outcome = await runner.RunAsync(libraryId, root.Id, root.Path, options, nextFolderId, nextFileId, reporter, ct, exclusions).ConfigureAwait(false);
            outcomes.Add((root, outcome));
            if (outcome.Result.Outcome == ScanOutcome.LibraryOffline)
            {
                offlineRoot ??= root.Path;
            }
        }

        var result = Aggregate(libraryId, roots, outcomes.Select(o => o.Outcome.Result).ToList(), offlineRoot, sw.Elapsed);

        _logger.LogInformation(
            "Incremental-Scan fertig ({Outcome}, {Roots} Wurzeln): {Added} neu, {Modified} geändert, {Missing} fehlend markiert ({Detected} erkannt), {Reappeared} wieder da, {Folders} neue Ordner, {Fts} FTS-Zeilen in {Sec:N2} s",
            result.Outcome, roots.Count, result.Added, result.Modified, result.MarkedMissing, result.MissingDetected, result.Reappeared, result.FoldersAdded, result.FtsRowsInserted, result.TotalDuration.TotalSeconds);

        // Galerie, Ordnerbaum und Lightbox nachziehen — nur für tatsächlich geschriebene Zeilen
        SendFilesChanged(libraryId, FilesChangeKind.Added, outcomes.SelectMany(o => o.Outcome.AddedIds).ToList());
        SendFilesChanged(libraryId, FilesChangeKind.Metadata, outcomes.SelectMany(o => o.Outcome.ModifiedIds).ToList());
        SendFilesChanged(libraryId, FilesChangeKind.Removed, outcomes.SelectMany(o => o.Outcome.RemovedIds).ToList());

        return result;
    }

    /// <summary>
    /// Fasst die Ergebnisse je Wurzel zusammen. Outcome: MassMissingBlocked, sobald eine Wurzel die Rückfrage
    /// braucht (die Wiederholung mit ConfirmMassMissing läuft wieder über alle Wurzeln); sonst LibraryOffline,
    /// wenn eine Wurzel nicht erreichbar war (RootPath = diese Wurzel); sonst Completed.
    /// </summary>
    private static ScanResult Aggregate(int libraryId, IReadOnlyList<ScanRoot> roots, IReadOnlyList<ScanResult> parts, string? offlineRoot, TimeSpan elapsed)
    {
        var outcome = parts.Any(p => p.Outcome == ScanOutcome.MassMissingBlocked)
            ? ScanOutcome.MassMissingBlocked
            : offlineRoot is not null ? ScanOutcome.LibraryOffline : ScanOutcome.Completed;

        return new ScanResult(
            LibraryId: libraryId,
            RootPath: offlineRoot ?? ArchivePath(roots),
            FoldersInserted: parts.Sum(p => p.FoldersInserted),
            FilesInserted: parts.Sum(p => p.FilesInserted),
            FtsRowsInserted: parts.Sum(p => p.FtsRowsInserted),
            TotalBytes: parts.Sum(p => p.TotalBytes),
            TotalDuration: elapsed,
            EnumerationDuration: TimeSpan.FromTicks(parts.Sum(p => p.EnumerationDuration.Ticks)),
            PersistenceDuration: TimeSpan.FromTicks(parts.Sum(p => p.PersistenceDuration.Ticks)),
            FtsDuration: TimeSpan.FromTicks(parts.Sum(p => p.FtsDuration.Ticks)))
        {
            Mode = ScanMode.Incremental,
            Outcome = outcome,
            Added = parts.Sum(p => p.Added),
            FoldersAdded = parts.Sum(p => p.FoldersAdded),
            Modified = parts.Sum(p => p.Modified),
            Reappeared = parts.Sum(p => p.Reappeared),
            MarkedMissing = parts.Sum(p => p.MarkedMissing),
            MissingDetected = parts.Sum(p => p.MissingDetected),
            RootsScanned = parts.Count,
        };
    }

    private void SendFilesChanged(int libraryId, FilesChangeKind kind, IReadOnlyList<int> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        try
        {
            _messenger.Send(new FilesChangedMessage(libraryId, kind, fileIds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der FilesChangedMessage ({Kind}) warf eine Ausnahme", kind);
        }
    }

    // ---- Fresh / ClearAndRescan ------------------------------------------------------------------

    private async Task<ScanResult> RunFullScanAsync(int libraryId, ScanMode mode, ScanOptions options, ScanProgressReporter reporter, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        // 1) Wurzeln laden
        var roots = await LoadRootsAsync(libraryId, ct).ConfigureAwait(false);

        // 2) Alle Wurzeln erreichbar? — VOR dem Leeren prüfen, damit ein Offline-Laufwerk nie den Katalog löscht (PLAN Kap. 12)
        foreach (var root in roots)
        {
            if (!await RootAvailabilityProbe.IsAvailableAsync(root.Path, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Scan nicht gestartet, Wurzel offline: {Root} (Mode={Mode})", root.Path, mode);
                return OfflineResult(libraryId, root.Path, mode, totalSw.Elapsed);
            }
        }

        // 3) Fresh-Check: keine bestehenden Folders/Files für diese Library. ClearAndRescan lässt den bisherigen
        //    Katalog bis zum Commit in Schritt 7 unangetastet (ADR-0028) — Abbruch oder Fehler davor ändern nichts.
        if (mode == ScanMode.Fresh)
        {
            await EnsureLibraryIsEmptyAsync(libraryId, ct).ConfigureAwait(false);
        }

        // 4/5) Start-Ids: fortlaufend über alle Libraries und nie wiederverwendet (sqlite_sequence; bei ClearAndRescan
        //    hinter den noch vorhandenen alten Ids) — sonst kollidieren Ids und INSERT OR IGNORE verwirft Zeilen still.
        var (nextFolderId, nextFileId) = ReadNextIds();

        _logger.LogInformation(
            "Scan startet: Library={LibraryId} Wurzeln={Roots} Mode={Mode} StartIds Folder={FolderId} File={FileId}",
            libraryId, string.Join(" | ", roots.Select(r => $"{r.Role}:{r.Path}")), mode, nextFolderId, nextFileId);

        // 6) Enumeration + In-Memory-Aggregation je Wurzel; ausgeschlossene Ordner werden nicht betreten (ADR-0066)
        var exclusions = await LoadExclusionsAsync(libraryId, ct).ConfigureAwait(false);

        // #22 (Nachtrag ADR-0066): „Einträge behalten“ — schon katalogisierte Dateien in ausgeschlossenen Ordnern bleiben
        var keep = mode == ScanMode.ClearAndRescan && options.KeepCatalogedInExcludedFolders
            ? await ExcludedEntriesKeeper.LoadAsync(_contextFactory, libraryId, exclusions, ct).ConfigureAwait(false)
            : [];
        var rootPaths = roots.Select(r => r.Path).ToList();
        var rootIds = roots.Select(r => r.Id).ToHashSet();
        var keptCount = 0;
        var enumSw = Stopwatch.StartNew();
        var folderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderRows = new List<BulkScanWriter.FolderRow>();
        var fileRows = new List<BulkScanWriter.FileRow>();
        long totalBytes = 0;
        var scannedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var root in roots)
        {
            var rootPath = root.Path;
            var rootKey = TrimSeparators(rootPath);

            // Legt den Ordner und alle fehlenden Zwischenordner bis zur Wurzel an, damit die ParentId-Kette
            // vollständig ist (Ordnerbaum + Container-Kacheln brauchen auch Ordner ohne direkte Medien).
            int GetOrCreateFolderId(string folderPath)
            {
                var isRoot = string.Equals(TrimSeparators(folderPath), rootKey, StringComparison.OrdinalIgnoreCase);
                if (isRoot)
                {
                    folderPath = rootPath; // kanonische Schreibweise der Wurzel
                }

                if (folderMap.TryGetValue(folderPath, out var existingId))
                {
                    return existingId;
                }

                int? parentId = null;
                if (!isRoot)
                {
                    var parentPath = Path.GetDirectoryName(folderPath);
                    if (!string.IsNullOrEmpty(parentPath) && TrimSeparators(parentPath).Length >= rootKey.Length)
                    {
                        parentId = GetOrCreateFolderId(parentPath);
                    }
                }

                var id = nextFolderId++;
                folderMap[folderPath] = id;
                folderRows.Add(new BulkScanWriter.FolderRow(id, libraryId, folderPath, parentId, FolderDisplayName(folderPath), scannedAtUnix, root.Id));
                return id;
            }

            foreach (var df in _enumerator.Enumerate(rootPath, ct, exclusions: exclusions))
            {
                var folderId = GetOrCreateFolderId(df.FolderPath);

                fileRows.Add(new BulkScanWriter.FileRow(
                    Id: nextFileId++,
                    LibraryId: libraryId,
                    FolderId: folderId,
                    Name: df.Name,
                    Extension: df.Extension,
                    Size: df.Size,
                    CreatedAtUnix: ToUnix(df.CreatedAtUtc),
                    ModifiedAtUnix: ToUnix(df.ModifiedAtUtc),
                    MediaType: (int)df.MediaType,
                    Width: null,
                    Height: null,
                    DurationMs: null,
                    Codec: null,
                    IsFavorite: false,
                    Rating: null,
                    Missing: false));

                totalBytes += df.Size;
                reporter.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, df.FolderPath));
            }

            // #22: behaltene Einträge dieser Wurzel mit aktuellen Werten von der Platte; nicht mehr vorhandene fallen weg
            HashSet<(int FolderId, string Name)>? enumerated = null; // nur falls ein behaltener Ordner doch durchlaufen wurde
            foreach (var kept in ExcludedEntriesKeeper.ForRoot(keep, root.Id, rootPath, rootPaths, rootIds))
            {
                ct.ThrowIfCancellationRequested();
                long size;
                DateTime created, modified;
                try
                {
                    var info = new FileInfo(Path.Combine(kept.FolderPath, kept.Name));
                    if (!info.Exists)
                    {
                        continue;
                    }

                    (size, created, modified) = (info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    continue;
                }

                var seenFolder = folderMap.ContainsKey(kept.FolderPath);
                var keptFolderId = GetOrCreateFolderId(kept.FolderPath);
                if (seenFolder)
                {
                    enumerated ??= fileRows.Select(r => (r.FolderId, r.Name.ToUpperInvariant())).ToHashSet();
                    if (!enumerated.Add((keptFolderId, kept.Name.ToUpperInvariant())))
                    {
                        continue; // schon vom Durchlauf erfasst
                    }
                }

                fileRows.Add(new BulkScanWriter.FileRow(
                    Id: nextFileId++,
                    LibraryId: libraryId,
                    FolderId: keptFolderId,
                    Name: kept.Name,
                    Extension: kept.Extension,
                    Size: size,
                    CreatedAtUnix: ToUnix(created),
                    ModifiedAtUnix: ToUnix(modified),
                    MediaType: (int)kept.MediaType,
                    Width: null,
                    Height: null,
                    DurationMs: null,
                    Codec: null,
                    IsFavorite: false,
                    Rating: null,
                    Missing: false));
                totalBytes += size;
                keptCount++;
            }
        }

        if (keep.Count > 0)
        {
            _logger.LogInformation("Neu aufbauen: {Kept} von {Total} Einträgen in ausgeschlossenen Ordnern behalten", keptCount, keep.Count);
        }

        enumSw.Stop();
        reporter.Report(new ScanProgress(folderRows.Count, fileRows.Count, totalBytes, null), force: true);
        _logger.LogInformation(
            "Enumeration fertig: {Folders} Folders, {Files} Files, {Bytes:N0} bytes in {Sec:N2} s",
            folderRows.Count, fileRows.Count, totalBytes, enumSw.Elapsed.TotalSeconds);
        if (fileRows.Count == 0)
        {
            _logger.LogWarning(
                "Keine Mediendateien unter den Wurzeln der Bibliothek {LibraryId} gefunden — Ordner leer, nur nicht unterstützte Formate oder fehlende Leserechte.",
                libraryId);
        }

        // Abbruch vor der Persistenz: nichts schreiben
        ct.ThrowIfCancellationRequested();

        // 7) Persistenz via Raw-SQLite in EINER Transaktion (ADR-0028): ClearAndRescan sichert die Benutzerdaten,
        //    ersetzt die alten Zeilen, schreibt die Daten zurück und baut den Suchindex — alles oder nichts.
        var persistSw = Stopwatch.StartNew();
        FullScanCatalogWriter.Report report;
        using (var connection = _contextFactory.OpenSqliteConnection())
        {
            report = new FullScanCatalogWriter(_bulkWriter, _ftsIndexer, _logger)
                .Write(connection, libraryId, mode, folderRows, fileRows, totalBytes, reporter, ct);
        }

        // 8) Erst nach dem Commit: Vorschaubilder der alten FileIds verwerfen (best effort; FileIds werden nie
        //    wiederverwendet, ein Rest nach einem Absturz genau hier ist nur toter Platz).
        if (mode == ScanMode.ClearAndRescan)
        {
            await _libraryService.DeleteThumbnailCacheAsync(libraryId).ConfigureAwait(false);
        }

        persistSw.Stop();
        totalSw.Stop();

        return new ScanResult(
            LibraryId: libraryId,
            RootPath: ArchivePath(roots),
            FoldersInserted: report.Folders.Inserted,
            FilesInserted: report.Files.Inserted,
            FtsRowsInserted: (int)report.Fts.RowsInserted,
            TotalBytes: totalBytes,
            TotalDuration: totalSw.Elapsed,
            EnumerationDuration: enumSw.Elapsed,
            PersistenceDuration: persistSw.Elapsed - report.Fts.Duration,
            FtsDuration: report.Fts.Duration)
        {
            Mode = mode,
            RootsScanned = roots.Count,
            KeptExcluded = keptCount,
        };
    }

    // ---- Gemeinsame Helfer ----------------------------------------------------------------------

    /// <summary>Ausschlüsse der Bibliothek (ADR-0066); Info-Log nur mit Zahlen.</summary>
    private async Task<Exclusions.ScanExclusionSet> LoadExclusionsAsync(int libraryId, CancellationToken ct)
    {
        var set = await _exclusions.LoadSetAsync(libraryId, ct).ConfigureAwait(false);
        _logger.LogInformation("Ausschlüsse beim Einlesen: {Names} Ordnernamen, {Folders} Ordner der Bibliothek {LibraryId}", set.FolderNames.Count, set.FolderPaths.Count, libraryId);
        return set;
    }

    private static ScanResult OfflineResult(int libraryId, string rootPath, ScanMode mode, TimeSpan elapsed) =>
        new(libraryId, rootPath, 0, 0, 0, 0, elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
        {
            Mode = mode,
            Outcome = ScanOutcome.LibraryOffline,
        };

    private static string ArchivePath(IReadOnlyList<ScanRoot> roots) =>
        (roots.FirstOrDefault(r => r.Role == LibraryRootRole.Archive) ?? roots[0]).Path;

    /// <summary>Wurzeln der Library: Archiv zuerst, dann Eingänge nach SortOrder (ADR-0020).</summary>
    private async Task<IReadOnlyList<ScanRoot>> LoadRootsAsync(int libraryId, CancellationToken ct)
    {
        await using var ctx = _contextFactory.Create();
        var roots = await ctx.LibraryRoots.AsNoTracking()
            .Where(r => r.LibraryId == libraryId)
            .OrderBy(r => r.Role == LibraryRootRole.Archive ? 0 : 1)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .Select(r => new ScanRoot(r.Id, r.Path, r.Role))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (roots.Count > 0)
        {
            return roots;
        }

        var exists = await ctx.Libraries.AnyAsync(l => l.Id == libraryId, ct).ConfigureAwait(false);
        throw new InvalidOperationException(exists
            ? $"Library Id={libraryId} hat keine Wurzel."
            : $"Library Id={libraryId} nicht gefunden.");
    }

    private async Task EnsureLibraryIsEmptyAsync(int libraryId, CancellationToken ct)
    {
        await using var ctx = _contextFactory.Create();
        var existingFolders = await ctx.Folders.CountAsync(f => f.LibraryId == libraryId, ct).ConfigureAwait(false);
        var existingFiles = await ctx.Files.CountAsync(f => f.LibraryId == libraryId, ct).ConfigureAwait(false);
        if (existingFolders > 0 || existingFiles > 0)
        {
            throw new InvalidOperationException(
                $"ScanMode.Fresh: Library {libraryId} ist nicht leer ({existingFolders} Folders, {existingFiles} Files). " +
                "Für einen Neuaufbau ScanMode.ClearAndRescan, für ein Update ScanMode.Incremental verwenden.");
        }
    }

    /// <summary>
    /// Nächste freie Id je Tabelle: Maximum aus MAX(Id) und dem AUTOINCREMENT-Zähler (sqlite_sequence),
    /// damit nach ClearAsync keine Ids wiederverwendet werden (Thumbnail-Dedupe und Cache-Dateien sind FileId-basiert).
    /// Der Incremental-Scan setzt die Vergabe damit lückenlos hinter dem bisherigen Maximum fort.
    /// </summary>
    private (int NextFolderId, int NextFileId) ReadNextIds()
    {
        using var connection = _contextFactory.OpenSqliteConnection();
        return (ReadNextId(connection, "Folders"), ReadNextId(connection, "Files"));
    }

    private static int ReadNextId(SqliteConnection connection, string table)
        => FullScanCatalogWriter.ReadNextId(connection, transaction: null, table);

    private static string FolderDisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name))
        {
            // Trailing-Slash-Fall: nimm den parent directory-Namen
            name = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? fullPath);
            if (string.IsNullOrEmpty(name))
            {
                name = fullPath; // Laufwerkswurzel
            }
        }

        return name;
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
