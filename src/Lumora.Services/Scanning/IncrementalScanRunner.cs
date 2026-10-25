using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Data.RawSql;
using Lumora.Services.Database;

namespace Lumora.Services.Scanning;

/// <summary>
/// Diff-Re-Scan EINER Wurzel einer Bibliothek mit stabilen FileIds (ScanMode.Incremental) und Offline-vs-Missing-Guard
/// (PLAN Kap. 12); der ScanService ruft ihn je Wurzel auf (ADR-0020). Ablauf:
/// <list type="number">
/// <item>Katalog-Snapshot der Wurzel laden (Folders.RootId): Ordner nach Pfad, Dateien nach (FolderId, Name) — beides case-insensitiv.</item>
/// <item>Dateisystem enumerieren und abgleichen: unbekannt → neu (fortlaufende Id, Ordnerkette anlegen);
/// bekannt mit anderer Größe/Änderungszeit → geändert; bekannt und bisher Missing → wieder da.
/// Alle 2000 Dateien und bei jedem IO-Fehler wird der Root erneut geprüft.</item>
/// <item>Persistieren in Batches (je Batch Root-Recheck, eigene Transaktion): neue Ordner, neue Dateien samt
/// gezielter FTS-Indexierung, Updates. Zuletzt — nur wenn der Root erreichbar ist und der
/// Mass-Missing-Schwellwert nicht greift (oder bestätigt wurde) — die Missing-Markierungen.</item>
/// </list>
/// Geht der Root unterwegs verloren, endet der Scan mit <see cref="ScanOutcome.LibraryOffline"/>: bereits
/// geschriebene neue/geänderte Dateien bleiben (sie sind real), markiert wird nichts.
/// </summary>
internal sealed class IncrementalScanRunner
{
    /// <summary>Zeilen je Transaktion (PLAN Kap. 11: DbInsertBatchSize; ohne Storage-Profil-Service fix 5000).</summary>
    private const int PersistBatchSize = 5_000;

    /// <summary>Root-Recheck während der Enumeration (PLAN Kap. 12: „Vor jedem Scan-Batch“).</summary>
    private const int RootRecheckFileInterval = 2_000;

    private readonly ILibraryContextFactory _contextFactory;
    private readonly FolderEnumerator _enumerator;
    private readonly BulkScanWriter _bulkWriter;
    private readonly ILogger _logger;

    public IncrementalScanRunner(
        ILibraryContextFactory contextFactory,
        FolderEnumerator enumerator,
        BulkScanWriter bulkWriter,
        ILogger logger)
    {
        _contextFactory = contextFactory;
        _enumerator = enumerator;
        _bulkWriter = bulkWriter;
        _logger = logger;
    }

    /// <summary>Ergebnis samt Id-Listen für die FilesChangedMessages (nur tatsächlich persistierte Zeilen).</summary>
    public sealed record Outcome(
        ScanResult Result,
        IReadOnlyList<int> AddedIds,
        IReadOnlyList<int> ModifiedIds,
        IReadOnlyList<int> RemovedIds);

    public async Task<Outcome> RunAsync(
        int libraryId,
        int rootId,
        string rootPath,
        ScanOptions options,
        int nextFolderId,
        int nextFileId,
        ScanProgressReporter reporter,
        CancellationToken ct,
        Exclusions.ScanExclusionSet? exclusions = null)
    {
        var totalSw = Stopwatch.StartNew();

        // 1) Katalog-Snapshot der Wurzel — ohne Dateien in ausgeschlossenen Ordnern (ADR-0066): die bleiben, wie sie sind
        //    (weder „fehlt“ noch aktualisiert), bis der Benutzer sie bewusst aus der Bibliothek entfernt.
        CatalogSnapshot catalog;
        using (var connection = _contextFactory.OpenSqliteConnection())
        {
            catalog = CatalogSnapshot.Load(connection, libraryId, rootId, _logger, exclusions, options.SubtreePath); // ADR-0068: nur ein Ordner
        }

        _logger.LogInformation(
            "Incremental-Scan startet: Library={LibraryId} Wurzel={RootId} Root={Root} Katalog: {Folders} Ordner, {Files} Dateien ({Present} vorhanden) StartIds Folder={FolderId} File={FileId}",
            libraryId, rootId, rootPath, catalog.FolderIdsByPath.Count, catalog.AllFiles.Count, catalog.PresentCount, nextFolderId, nextFileId);

        // Diff-Ergebnis der Enumeration
        var newFolderRows = new List<BulkScanWriter.FolderRow>();
        var newFileRows = new List<BulkScanWriter.FileRow>();
        var updates = new List<IncrementalCatalogWriter.FileUpdate>(); // geändert + wieder da, in Enumerationsreihenfolge
        var updateIsReappeared = new List<bool>();
        var missingIds = new List<int>();
        long totalBytes = 0;
        var filesSeen = 0;
        var duplicates = 0;

        // Persistenz-Zähler (bleiben auch bei Offline-Abbruch aussagekräftig)
        var foldersInserted = 0;
        var filesInserted = 0;
        long ftsRows = 0;
        var ftsDuration = TimeSpan.Zero;
        var modifiedApplied = 0;
        var reappearedApplied = 0;
        var marked = 0;
        var addedIds = new List<int>();
        var modifiedIds = new List<int>();
        var reappearedIds = new List<int>();
        var removedIds = new List<int>();
        var outcome = ScanOutcome.Completed;

        var enumSw = Stopwatch.StartNew();
        var persistSw = new Stopwatch();

        try
        {
            // 2) Enumeration + Abgleich
            var scannedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var rootKey = TrimSeparators(rootPath);
            var folderMap = catalog.FolderIdsByPath;

            // Legt den Ordner und alle fehlenden Zwischenordner bis zum Root an (vollständige ParentId-Kette).
            int GetOrCreateFolderId(string folderPath)
            {
                var isRoot = string.Equals(TrimSeparators(folderPath), rootKey, StringComparison.OrdinalIgnoreCase);
                if (isRoot)
                {
                    folderPath = rootPath; // kanonische Schreibweise des Roots
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
                newFolderRows.Add(new BulkScanWriter.FolderRow(id, libraryId, folderPath, parentId, FolderDisplayName(folderPath), scannedAtUnix, rootId));
                return id;
            }

            // IOException während der Enumeration → Root-Recheck → offline → Scan beenden (keine Missing-Markierungen).
            void OnIoError(string path, Exception ex)
            {
                _logger.LogWarning("IO-Fehler bei {Path} — Root wird erneut geprüft", path);
                if (!RootAvailabilityProbe.IsAvailableAsync(rootPath, ct).GetAwaiter().GetResult())
                {
                    throw new LibraryOfflineException(rootPath);
                }
            }

            var filesSinceRecheck = 0;
            foreach (var df in _enumerator.Enumerate(options.SubtreePath ?? rootPath, ct, OnIoError, exclusions))
            {
                var folderId = GetOrCreateFolderId(df.FolderPath);
                filesSeen++;
                totalBytes += df.Size;
                var createdUnix = ToUnix(df.CreatedAtUtc);
                var modifiedUnix = ToUnix(df.ModifiedAtUtc);

                if (catalog.TryGetFile(folderId, df.Name, out var existing))
                {
                    if (existing.Seen)
                    {
                        // Zwei Dateien, die sich nur in der Schreibweise unterscheiden (case-sensitives Volume):
                        // die erste zählt, die zweite wird übersprungen — der Katalog ist case-insensitiv.
                        duplicates++;
                    }
                    else
                    {
                        existing.Seen = true;
                        var changed = existing.Size != df.Size || existing.ModifiedAtUnix != modifiedUnix;
                        if (existing.Missing)
                        {
                            updates.Add(new IncrementalCatalogWriter.FileUpdate(existing.Id, df.Size, createdUnix, modifiedUnix));
                            updateIsReappeared.Add(true);
                        }
                        else if (changed)
                        {
                            updates.Add(new IncrementalCatalogWriter.FileUpdate(existing.Id, df.Size, createdUnix, modifiedUnix));
                            updateIsReappeared.Add(false);
                        }
                    }
                }
                else
                {
                    newFileRows.Add(new BulkScanWriter.FileRow(
                        Id: nextFileId++,
                        LibraryId: libraryId,
                        FolderId: folderId,
                        Name: df.Name,
                        Extension: df.Extension,
                        Size: df.Size,
                        CreatedAtUnix: createdUnix,
                        ModifiedAtUnix: modifiedUnix,
                        MediaType: (int)df.MediaType,
                        Width: null,
                        Height: null,
                        DurationMs: null,
                        Codec: null,
                        IsFavorite: false,
                        Rating: null,
                        Missing: false));
                }

                reporter.Report(new ScanProgress(folderMap.Count, filesSeen, totalBytes, df.FolderPath));

                if (++filesSinceRecheck >= RootRecheckFileInterval)
                {
                    filesSinceRecheck = 0;
                    await EnsureRootOnlineAsync(rootPath, ct).ConfigureAwait(false);
                }
            }

            enumSw.Stop();

            // Ein Laufwerk, das während der Enumeration verschwunden ist, darf keine Missing-Welle auslösen.
            await EnsureRootOnlineAsync(rootPath, ct).ConfigureAwait(false);

            foreach (var file in catalog.AllFiles)
            {
                if (!file.Missing && !file.Seen)
                {
                    missingIds.Add(file.Id);
                }
            }

            reporter.Report(new ScanProgress(folderMap.Count, filesSeen, totalBytes, null), force: true);
            _logger.LogInformation(
                "Abgleich fertig: {Seen} Dateien gesehen, {New} neu, {Updates} geändert/wieder da, {Missing} fehlend, {NewFolders} neue Ordner, {Dups} Duplikate in {Sec:N2} s",
                filesSeen, newFileRows.Count, updates.Count, missingIds.Count, newFolderRows.Count, duplicates, enumSw.Elapsed.TotalSeconds);

            // Abbruch vor der Persistenz: nichts schreiben
            ct.ThrowIfCancellationRequested();

            // 3) Persistenz in Batches, je Batch Root-Recheck
            persistSw.Start();
            using var connection = _contextFactory.OpenSqliteConnection();

            if (newFolderRows.Count > 0)
            {
                await EnsureRootOnlineAsync(rootPath, ct).ConfigureAwait(false);
                reporter.Report(new ScanProgress(folderMap.Count, 0, totalBytes, null) { Phase = ScanPhase.Persisting }, force: true);
                var folderReport = _bulkWriter.InsertFolders(connection, newFolderRows);
                foldersInserted = folderReport.Inserted;
                _logger.LogInformation("Neue Ordner: {In}/{At} in {Ms:N0} ms", folderReport.Inserted, folderReport.Attempted, folderReport.Duration.TotalMilliseconds);
            }

            foreach (var batch in newFileRows.Chunk(PersistBatchSize))
            {
                await EnsureRootOnlineAsync(rootPath, ct).ConfigureAwait(false);
                var report = IncrementalCatalogWriter.InsertFilesWithFts(connection, libraryId, batch, ct);
                filesInserted += report.Inserted;
                ftsRows += report.FtsRows;
                ftsDuration += report.FtsDuration;
                foreach (var row in batch)
                {
                    addedIds.Add(row.Id);
                }

                reporter.Report(new ScanProgress(folderMap.Count, filesInserted, totalBytes, null) { Phase = ScanPhase.Persisting }, force: true);
            }

            var updateIndex = 0;
            foreach (var batch in updates.Chunk(PersistBatchSize))
            {
                await EnsureRootOnlineAsync(rootPath, ct).ConfigureAwait(false);
                IncrementalCatalogWriter.UpdateFiles(connection, batch, ct);
                foreach (var u in batch)
                {
                    if (updateIsReappeared[updateIndex++])
                    {
                        reappearedApplied++;
                        reappearedIds.Add(u.Id);
                    }
                    else
                    {
                        modifiedApplied++;
                        modifiedIds.Add(u.Id);
                    }
                }
            }

            if (missingIds.Count > 0)
            {
                if (!options.ConfirmMassMissing && MassMissingGuard.Exceeds(missingIds.Count, catalog.PresentCount))
                {
                    outcome = ScanOutcome.MassMissingBlocked;
                    _logger.LogWarning(
                        "Mass-Missing-Guard: {Missing} von {Present} Dateien fehlen (> {Abs} oder > {Pct} %) — nicht markiert, Rückfrage nötig",
                        missingIds.Count, catalog.PresentCount, MassMissingGuard.AbsoluteThreshold, MassMissingGuard.PercentThreshold);
                }
                else
                {
                    // Grundregel PLAN Kap. 12: Nur wenn der Root erreichbar ist, dürfen Dateien als fehlend markiert werden.
                    foreach (var batch in missingIds.Chunk(PersistBatchSize))
                    {
                        await EnsureRootOnlineAsync(rootPath, ct).ConfigureAwait(false);
                        marked += IncrementalCatalogWriter.MarkMissing(connection, batch, ct);
                        removedIds.AddRange(batch);
                    }
                }
            }
        }
        catch (LibraryOfflineException ex)
        {
            outcome = ScanOutcome.LibraryOffline;
            _logger.LogWarning(
                "Incremental-Scan abgebrochen, Bibliothek offline: {Root} — bisher geschrieben: {Folders} Ordner, {Files} neue, {Updates} aktualisierte Dateien; nichts als fehlend markiert ({Message})",
                rootPath, foldersInserted, filesInserted, modifiedApplied + reappearedApplied, ex.Message);
        }

        enumSw.Stop();
        persistSw.Stop();
        totalSw.Stop();

        var result = new ScanResult(
            LibraryId: libraryId,
            RootPath: rootPath,
            FoldersInserted: foldersInserted,
            FilesInserted: filesInserted,
            FtsRowsInserted: (int)ftsRows,
            TotalBytes: totalBytes,
            TotalDuration: totalSw.Elapsed,
            EnumerationDuration: enumSw.Elapsed,
            PersistenceDuration: persistSw.Elapsed - ftsDuration,
            FtsDuration: ftsDuration)
        {
            Mode = ScanMode.Incremental,
            Outcome = outcome,
            Added = filesInserted,
            FoldersAdded = foldersInserted,
            Modified = modifiedApplied,
            Reappeared = reappearedApplied,
            MarkedMissing = marked,
            MissingDetected = missingIds.Count,
        };

        // Wieder aufgetauchte Dateien sind für Galerie/Ordnerbaum „neu sichtbar“ → zusammen mit den neuen melden.
        addedIds.AddRange(reappearedIds);
        return new Outcome(result, addedIds, modifiedIds, removedIds);
    }

    private static async Task EnsureRootOnlineAsync(string rootPath, CancellationToken ct)
    {
        if (!await RootAvailabilityProbe.IsAvailableAsync(rootPath, ct).ConfigureAwait(false))
        {
            throw new LibraryOfflineException(rootPath);
        }
    }

    private static string FolderDisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name))
        {
            name = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? fullPath);
            if (string.IsNullOrEmpty(name))
            {
                name = fullPath; // Laufwerkswurzel
            }
        }

        return name;
    }

    /// <summary>true, wenn <paramref name="path"/> gleich <paramref name="folder"/> ist oder darunter liegt (ohne Groß-/Kleinschreibung).</summary>
    internal static bool IsUnder(string path, string folder)
    {
        var p = TrimSeparators(path);
        var f = TrimSeparators(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Bekannte Datei des Katalogs; Seen wird während der Enumeration gesetzt.</summary>
    private sealed class CatalogFile
    {
        public int Id;
        public long Size;
        public long ModifiedAtUnix;
        public bool Missing;
        public bool Seen;
    }

    /// <summary>
    /// In-Memory-Abbild des Katalogs einer Library (nur die für den Diff nötigen Spalten). Für 500k Dateien
    /// einige zehn MB — derselbe Rahmen wie die In-Memory-Aggregation des Fresh-Scans.
    /// </summary>
    private sealed class CatalogSnapshot
    {
        public Dictionary<string, int> FolderIdsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<CatalogFile> AllFiles { get; } = new();

        /// <summary>Dateien mit Missing = 0 vor dem Scan (Basis der 20-%-Regel).</summary>
        public int PresentCount { get; private set; }

        private readonly Dictionary<int, Dictionary<string, CatalogFile>> _filesByFolder = new();

        public bool TryGetFile(int folderId, string name, out CatalogFile file)
        {
            if (_filesByFolder.TryGetValue(folderId, out var byName) && byName.TryGetValue(name, out var found))
            {
                file = found;
                return true;
            }

            file = null!;
            return false;
        }

        /// <summary>Nur Ordner (und ihre Dateien) der angegebenen Wurzel — andere Wurzeln bleiben unberührt (ADR-0020).</summary>
        /// <param name="subtree">ADR-0068: nur Ordner unter diesem Pfad (samt ihm selbst) zählen; Dateien anderer Ordner bleiben unberührt.</param>
        public static CatalogSnapshot Load(SqliteConnection connection, int libraryId, int rootId, ILogger logger, Exclusions.ScanExclusionSet? exclusions = null, string? subtree = null)
        {
            var snapshot = new CatalogSnapshot();
            var excludedFolderIds = new HashSet<int>();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Path FROM Folders WHERE LibraryId = $lib AND RootId = $root;";
                cmd.Parameters.AddWithValue("$lib", libraryId);
                cmd.Parameters.AddWithValue("$root", rootId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var path = reader.GetString(1);
                    if (exclusions?.IsExcludedPath(path) == true || (subtree is not null && !IsUnder(path, subtree)))
                    {
                        excludedFolderIds.Add(id); // Ordnerzeile bleibt im Pfad-Index (keine Doppelanlage), Dateien zählen nicht
                    }

                    if (!snapshot.FolderIdsByPath.TryAdd(path, id))
                    {
                        logger.LogWarning("Doppelter Ordnerpfad im Katalog (nur Schreibweise verschieden): {Path} — Id {Id} wird ignoriert", path, id);
                    }
                }
            }

            var duplicateFiles = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT f.Id, f.FolderId, f.Name, f.Size, f.ModifiedAtUtc, f.Missing
                    FROM Files f
                    WHERE f.LibraryId = $lib
                      AND f.FolderId IN (SELECT Id FROM Folders WHERE LibraryId = $lib AND RootId = $root);
                    """;
                cmd.Parameters.AddWithValue("$lib", libraryId);
                cmd.Parameters.AddWithValue("$root", rootId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var file = new CatalogFile
                    {
                        Id = reader.GetInt32(0),
                        Size = reader.GetInt64(3),
                        ModifiedAtUnix = reader.GetInt64(4),
                        Missing = reader.GetInt64(5) != 0,
                    };
                    var folderId = reader.GetInt32(1);
                    if (excludedFolderIds.Contains(folderId))
                    {
                        continue;
                    }

                    var name = reader.GetString(2);

                    if (!snapshot._filesByFolder.TryGetValue(folderId, out var byName))
                    {
                        byName = new Dictionary<string, CatalogFile>(StringComparer.OrdinalIgnoreCase);
                        snapshot._filesByFolder[folderId] = byName;
                    }

                    if (!byName.TryAdd(name, file))
                    {
                        // Zweite Zeile für denselben Namen (nur Schreibweise verschieden): bleibt im Katalog,
                        // gilt aber als nicht gesehen und wird wie eine fehlende Datei behandelt.
                        duplicateFiles++;
                    }

                    snapshot.AllFiles.Add(file);
                    if (!file.Missing)
                    {
                        snapshot.PresentCount++;
                    }
                }
            }

            if (duplicateFiles > 0)
            {
                logger.LogWarning("{Count} Dateizeilen mit doppeltem Namen (nur Schreibweise verschieden) im Katalog der Bibliothek {LibraryId}", duplicateFiles, libraryId);
            }

            return snapshot;
        }
    }
}
