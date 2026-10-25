using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Media;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Watch;
using LibraryEntity = Lumora.Domain.Entities.Library; // „Library“ allein wäre der Namespace Services.Library

namespace Lumora.Services.Operations;

/// <summary>
/// Datei-Operationen (Papierkorb, Verschieben, Umbenennen, Undo) mit Protokoll in
/// OperationBatches/FileOperations gemäß ADR-0012.
/// Ablauf pro Batch: Batch + alle Items als Pending persistieren → Applying → je Datei erst
/// Item auf Applying, dann Dateisystem, dann Katalog (Files, Folders, Thumbnails, FTS) in einer
/// kurzen Transaktion → Applied/Failed mit CompletedAtUtc. Fehler einzelner Dateien brechen den
/// Batch nicht ab; der Batch ist nur Failed, wenn kein Item gelingt.
/// Status-Regel: der Item-Status beschreibt immer den Dateisystem-Zustand. Sobald der
/// Dateisystem-Schritt erfolgt ist, läuft die Katalog-Buchung ohne CancellationToken zu Ende;
/// scheitert nur das Katalog-Update, bleibt Applied/Undone stehen und <c>Error</c> trägt den Grund
/// (der nächste Scan heilt den Katalog). Ein Item im Status Applying nach einem Absturz heißt:
/// Dateisystem-Schritt begonnen, Ausgang unbekannt (Recovery-Matrix ADR-0012).
/// Single-Writer: alle Aufrufe laufen nacheinander durch ein SemaphoreSlim; gegenüber anderen
/// Schreibern (ThumbnailService) fängt busy_timeout=5000 SQLITE_BUSY ab (Transaktionen sind
/// BEGIN IMMEDIATE, Microsoft.Data.Sqlite-Standard).
/// Scan-Sperre (0.2.17): jeder Batch belegt über <see cref="IScanGate"/> die beteiligten Bibliotheken (Dateien, Ziel,
/// bei Undo die Wurzeln der protokollierten Pfade), bevor er den Katalog liest — ein Scan derselben Bibliothek wartet
/// auf das Batch-Ende und umgekehrt. Die Sperre ist vor der Rückkehr wieder frei; Aufrufer, die danach scannen
/// (Entpacken), halten sie nie selbst.
/// Eigene Pfade (0.2.17): vor jedem Dateisystem-Schritt kommen Quelle, Ziel und neu angelegte Ordner in die
/// <see cref="IOwnChangeRegistry"/>, damit die Ordnerüberwachung keinen leeren Scan auslöst. Nur Pfade, deren Katalog
/// mitgebucht wird — ein Ziel außerhalb der Bibliothek der Datei bleibt fremd. Scheitert der Schritt oder die Buchung,
/// wird der Pfad zurückgenommen (die Überwachung zählt ihn dann).
/// Nachrichten: <see cref="FilesChangedMessage"/> (Moved/Removed/Added) je Library nach Batch-Ende —
/// auch nach einem Abbruch für die bereits verarbeiteten Dateien.
/// </summary>
public sealed partial class FileOperationService : IFileOperationService, IDisposable
{
    private const int QueryChunkSize = 500;
    private const int ErrorMaxLen = 500;

    /// <summary>Windows-Gerätenamen: als Dateistamm (auch mit Endung, „CON.jpg“) unter Windows 10 und im Explorer unbrauchbar.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IRecycleBin _recycleBin;
    private readonly IMediaFormatRegistry _formats;
    private readonly ILogger<FileOperationService> _logger;
    private readonly IMessenger _messenger;
    private readonly IScanGate _scanGate;
    private readonly IOwnChangeRegistry _ownChanges;
    private readonly Scanning.Exclusions.IScanExclusionStore _exclusions; // ADR-0066
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Ein FTS-'delete' ist gescheitert; nach dem nächsten SaveChanges wird der Index neu aufgebaut (nur unter _writeLock).</summary>
    private bool _ftsRebuildPending;

    /// <param name="scanGate">Optional (Tests); produktiv der Singleton, den auch ScanService und Ordnerüberwachung nutzen.</param>
    /// <param name="ownChanges">Optional (Tests); produktiv der Singleton, den die Ordnerüberwachung abfragt.</param>
    /// <param name="exclusions">Ausschlüsse beim Einlesen (ADR-0066); ohne Angabe aus der Settings-Tabelle desselben Katalogs.</param>
    public FileOperationService(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IRecycleBin recycleBin,
        IMediaFormatRegistry formats,
        ILogger<FileOperationService> logger,
        IMessenger? messenger = null,
        IScanGate? scanGate = null,
        IOwnChangeRegistry? ownChanges = null,
        Scanning.Exclusions.IScanExclusionStore? exclusions = null)
    {
        _factory = factory;
        _paths = paths;
        _recycleBin = recycleBin;
        _formats = formats;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _scanGate = scanGate ?? new ScanGate();
        _ownChanges = ownChanges ?? new OwnChangeRegistry();
        _exclusions = exclusions ?? new Scanning.Exclusions.ScanExclusionStore(factory, _messenger);
    }

    // ------------------------------------------------------------------ Verträge

    public Task<FileOperationBatchResult> RecycleAsync(IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        var ids = NormalizeIds(fileIds);
        return RunBatchAsync(
            OperationType.Recycle,
            ids,
            describe: known => $"{CountText(known.Count)} in den Papierkorb",
            destOf: _ => null,
            prepare: null,
            executeItem: ExecuteRecycleItemAsync,
            ct);
    }

    public Task<FileOperationBatchResult> MoveAsync(IReadOnlyList<int> fileIds, string targetFolderPath, CancellationToken ct = default)
    {
        var ids = NormalizeIds(fileIds);
        if (string.IsNullOrWhiteSpace(targetFolderPath))
        {
            throw new ArgumentException("Der Zielordner darf nicht leer sein.", nameof(targetFolderPath));
        }

        var target = NormalizeDirectoryPath(targetFolderPath);
        var targetFolders = new Dictionary<int, Folder>(); // Zielordner-Zeile je Library (nur innerhalb der Root)

        return RunBatchAsync(
            OperationType.Move,
            ids,
            describe: known => $"{CountText(known.Count)} nach „{target}“ verschoben",
            destOf: cf => Path.Combine(target, cf.File.Name),
            prepare: (ctx, run, token) => PrepareMoveAsync(ctx, run, target, token),
            executeItem: (ctx, run, cf, op, token) => ExecuteMoveItemAsync(ctx, run, cf, op, target, targetFolders, token),
            ct,
            gateDirectory: target);
    }

    public Task<FileOperationBatchResult> RenameAsync(int fileId, string newName, CancellationToken ct = default)
    {
        var name = ValidateFileName(newName);
        return RunBatchAsync(
            OperationType.Rename,
            new[] { fileId },
            describe: known => $"„{known[0].File.Name}“ in „{name}“ umbenannt",
            destOf: cf => Path.Combine(cf.Folder.Path, name),
            prepare: null,
            executeItem: (ctx, run, cf, op, token) => ExecuteRenameItemAsync(ctx, run, cf, op, name, token),
            ct);
    }

    public async Task<FolderOperationResult> CreateFolderAsync(int libraryId, string parentFolderPath, string name, CancellationToken ct = default)
    {
        var folderName = ValidateFileName(name);
        var parent = NormalizeDirectoryPath(parentFolderPath);
        var full = Path.Combine(parent, folderName);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireLibrariesAsync([libraryId], ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            await using var ctx = _factory.Create();
            var root = await FindRootContainingAsync(ctx, libraryId, parent, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"„{parent}“ liegt unter keiner Wurzel der Bibliothek.");

            var batch = new OperationBatch
            {
                OperationType = OperationType.MkDir,
                Status = BatchStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                Description = $"Ordner „{folderName}“ in „{parent}“ angelegt",
            };
            var op = new FileOperation
            {
                Action = OperationType.MkDir,
                SourcePath = full,
                DestPath = null,
                Status = FileOperationStatus.Pending,
            };
            await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                ctx.OperationBatches.Add(batch);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                op.BatchId = batch.Id;
                ctx.FileOperations.Add(op);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            batch.Status = BatchStatus.Applying;
            await MarkApplyingAsync(ctx, op, ct).ConfigureAwait(false);

            var created = MissingDirectoryChain(full);
            AddAll(own, created);
            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RevokeAll(own, created);
                op.Status = FileOperationStatus.Failed;
                op.Error = Truncate($"Ordner konnte nicht angelegt werden: {ex.Message}");
                op.ExecutedAtUtc = DateTime.UtcNow;
                batch.Status = BatchStatus.Failed;
                batch.CompletedAtUtc = DateTime.UtcNow;
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning(ex, "Ordner anlegen fehlgeschlagen: {Path}", full);
                return new FolderOperationResult(batch.Id, false, null, full, op.Error);
            }

            Folder? folder = null;
            var error = await CommitItemAsync(ctx, op, undo: false, null, async token =>
            {
                folder = await EnsureFolderChainAsync(ctx, root, full, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (error is not null)
            {
                RevokeAll(own, created);
            }

            batch.Status = BatchStatus.Applied;
            batch.CompletedAtUtc = DateTime.UtcNow;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Ordner angelegt: {Path} (Batch {BatchId}, FolderId {FolderId})", full, batch.Id, folder?.Id);
            PublishFoldersChanged(libraryId);
            return new FolderOperationResult(batch.Id, error is null, folder?.Id, full, error);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FolderOperationResult> RenameFolderAsync(int folderId, string newName, CancellationToken ct = default)
    {
        var name = ValidateFileName(newName);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var folderLibraryId = await ctx.Folders.AsNoTracking()
                .Where(f => f.Id == folderId)
                .Select(f => (int?)f.LibraryId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Ordner Id={folderId} nicht im Katalog.");
            using var lease = await AcquireLibrariesAsync([folderLibraryId], ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            var folder = await ctx.Folders.FirstOrDefaultAsync(f => f.Id == folderId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Ordner Id={folderId} nicht im Katalog.");
            var oldPath = NormalizeDirectoryPath(folder.Path);
            var root = folder.RootId is { } rootId
                ? await ctx.LibraryRoots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rootId, ct).ConfigureAwait(false)
                : await FindRootContainingAsync(ctx, folder.LibraryId, oldPath, ct).ConfigureAwait(false);
            if (root is not null && PathEquals(oldPath, NormalizeDirectoryPath(root.Path)))
            {
                throw new InvalidOperationException("Ein Wurzelordner der Bibliothek kann nicht umbenannt werden.");
            }

            var parentDir = Path.GetDirectoryName(oldPath)
                ?? throw new InvalidOperationException("Ein Laufwerk kann nicht umbenannt werden.");
            var newPath = Path.Combine(parentDir, name);
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Der neue Name ist identisch mit dem bisherigen.");
            }

            var caseOnly = PathEquals(oldPath, newPath);
            var libraryId = folder.LibraryId;
            if (!caseOnly)
            {
                if (Directory.Exists(newPath) || File.Exists(newPath))
                {
                    throw new InvalidOperationException("Ziel existiert bereits.");
                }

                var lowerNew = AsciiLower(newPath);
                if (await ctx.Folders.AnyAsync(f => f.LibraryId == libraryId && f.Path.ToLower() == lowerNew, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Ziel existiert bereits im Katalog.");
                }
            }

            if (!Directory.Exists(oldPath))
            {
                throw new DirectoryNotFoundException($"Der Ordner „{oldPath}“ ist nicht (mehr) vorhanden.");
            }

            // Unterordner und Dateien laden (getrackt — ihre Pfade werden nach dem Dateisystem-Schritt umgeschrieben)
            var lowerPrefix = AsciiLower(oldPath + Path.DirectorySeparatorChar);
            var descendants = await ctx.Folders
                .Where(f => f.LibraryId == libraryId && f.Path.ToLower().StartsWith(lowerPrefix))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var folderIds = descendants.Select(d => d.Id).Append(folder.Id).ToList();
            var pathsById = descendants.ToDictionary(d => d.Id, d => d.Path);
            pathsById[folder.Id] = folder.Path;
            var files = new List<MediaFile>();
            foreach (var chunk in folderIds.Chunk(QueryChunkSize))
            {
                files.AddRange(await ctx.Files.AsNoTracking().Where(f => chunk.Contains(f.FolderId)).ToListAsync(ct).ConfigureAwait(false));
            }

            // Protokoll (ADR-0012): Ordner-Operation zuerst, dann je Datei Quelle → Ziel
            var batch = new OperationBatch
            {
                OperationType = OperationType.RenameFolder,
                Status = BatchStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                Description = $"Ordner „{folder.Name}“ in „{name}“ umbenannt ({CountText(files.Count)})",
            };
            var folderOp = new FileOperation
            {
                Action = OperationType.RenameFolder,
                SourcePath = oldPath,
                DestPath = newPath,
                Status = FileOperationStatus.Pending,
            };
            var fileOps = new List<FileOperation>(files.Count);
            foreach (var file in files)
            {
                var source = Path.Combine(pathsById[file.FolderId], file.Name);
                fileOps.Add(new FileOperation
                {
                    FileId = file.Id,
                    Action = OperationType.Move,
                    SourcePath = source,
                    DestPath = newPath + source[oldPath.Length..],
                    Status = FileOperationStatus.Pending,
                });
            }

            await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                ctx.OperationBatches.Add(batch);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                folderOp.BatchId = batch.Id;
                ctx.FileOperations.Add(folderOp);
                foreach (var op in fileOps)
                {
                    op.BatchId = batch.Id;
                    ctx.FileOperations.Add(op);
                }

                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            batch.Status = BatchStatus.Applying;
            await MarkApplyingAsync(ctx, folderOp, ct).ConfigureAwait(false);

            string[] ownPaths = [oldPath, newPath]; // ein Renamed-Ereignis für den Ordner, keines je Datei darin
            AddAll(own, ownPaths);
            try
            {
                Directory.Move(oldPath, newPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RevokeAll(own, ownPaths);
                var now = DateTime.UtcNow;
                var message = Truncate($"Umbenennen fehlgeschlagen: {ex.Message}");
                foreach (var op in fileOps.Prepend(folderOp))
                {
                    op.Status = FileOperationStatus.Failed;
                    op.Error = message;
                    op.ExecutedAtUtc = now;
                }

                batch.Status = BatchStatus.Failed;
                batch.CompletedAtUtc = now;
                await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning(ex, "Ordner umbenennen fehlgeschlagen: {Old} → {New}", oldPath, newPath);
                return new FolderOperationResult(batch.Id, false, folder.Id, oldPath, message);
            }

            var error = await CommitItemAsync(ctx, folderOp, undo: false, null, _ =>
            {
                ApplyFolderPaths(folder, descendants, oldPath, newPath, name);
                foreach (var op in fileOps)
                {
                    MarkDone(op, undo: false, null);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
            if (error is not null)
            {
                RevokeAll(own, ownPaths);
            }

            batch.Status = BatchStatus.Applied;
            batch.CompletedAtUtc = DateTime.UtcNow;
            await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var changes = new ChangeSet();
            foreach (var file in files)
            {
                changes.Add(libraryId, FilesChangeKind.Moved, file.Id);
            }

            if (error is null)
            {
                await FollowExclusionsAsync(libraryId, oldPath, newPath).ConfigureAwait(false); // ADR-0066
            }

            Publish(changes);
            PublishFoldersChanged(libraryId);
            _logger.LogInformation("Ordner umbenannt: {Old} → {New} ({Files} Dateien, {Subfolders} Unterordner, Batch {BatchId})", oldPath, newPath, files.Count, descendants.Count, batch.Id);
            return new FolderOperationResult(batch.Id, error is null, folder.Id, newPath, error);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Pfade des Ordners und aller Unterordner umschreiben (Präfix ersetzen), Name des Ordners setzen.</summary>
    private static void ApplyFolderPaths(Folder folder, IEnumerable<Folder> descendants, string oldPath, string newPath, string? newName)
    {
        folder.Path = newPath;
        if (newName is not null)
        {
            folder.Name = newName;
        }

        foreach (var d in descendants)
        {
            var normalized = NormalizeDirectoryPath(d.Path);
            if (normalized.Length > oldPath.Length)
            {
                d.Path = newPath + normalized[oldPath.Length..];
            }
        }
    }

    public async Task<FileOperationBatchResult?> UndoLastAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var batchId = await ctx.OperationBatches
                .Where(b => b.Status == BatchStatus.Applied
                            && (b.OperationType == OperationType.Move || b.OperationType == OperationType.Rename || b.OperationType == OperationType.RenameFolder
                                || b.OperationType == OperationType.AdoptFolder
                                || b.OperationType == OperationType.RecycleFolder || b.OperationType == OperationType.RemoveFromCatalog // ADR-0065
                                || b.OperationType == OperationType.MoveFolder || b.OperationType == OperationType.ExcludeFromCatalog // ADR-0066
                                || b.OperationType == OperationType.Copy // ADR-0068: Kopieren nach…
                                || b.OperationType == OperationType.CreateVariant)) // ADR-0071
                .OrderByDescending(b => b.Id)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return batchId is null
                ? null
                : await UndoLockedAsync(ctx, batchId.Value, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FileOperationBatchResult?> UndoBatchAsync(int batchId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            return await UndoLockedAsync(ctx, batchId, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Undo unter der Scan-Sperre: belegt werden die Bibliotheken, unter deren Wurzeln die protokollierten Quell- und
    /// Zielpfade liegen (die Katalogzeilen können seit dem Batch die Bibliothek verlassen haben).
    /// </summary>
    private async Task<FileOperationBatchResult?> UndoLockedAsync(LibraryContext ctx, int batchId, CancellationToken ct)
    {
        var paths = await ctx.FileOperations.AsNoTracking()
            .Where(o => o.BatchId == batchId)
            .Select(o => new { o.SourcePath, o.DestPath })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var directories = paths
            .SelectMany(p => new[] { p.SourcePath, p.DestPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetDirectoryName(p!))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var libraries = await LibrariesContainingAsync(ctx, directories, ct).ConfigureAwait(false);

        using var lease = await AcquireLibrariesAsync(libraries, ct).ConfigureAwait(false);
        using var own = _ownChanges.Begin();
        return await UndoCoreAsync(ctx, batchId, own, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OperationBatchInfo>> GetRecentBatchesAsync(int take = 50, CancellationToken ct = default)
    {
        take = Math.Max(1, take);
        await using var ctx = _factory.Create();
        var rows = await ctx.OperationBatches
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAtUtc)
            .ThenByDescending(b => b.Id)
            .Take(take)
            .Select(b => new
            {
                b.Id,
                b.OperationType,
                b.Status,
                b.CreatedAtUtc,
                b.CompletedAtUtc,
                b.Description,
                ItemCount = ctx.FileOperations.Count(o => o.BatchId == b.Id),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new OperationBatchInfo(
                r.Id,
                r.OperationType,
                r.Status,
                r.CreatedAtUtc,
                r.CompletedAtUtc,
                r.ItemCount,
                r.Description,
                CanUndo(r.OperationType, r.Status)))
            .ToList();
    }

    public void Dispose() => _writeLock.Dispose();

    // ------------------------------------------------------------------ Batch-Rahmen

    /// <summary>Katalogdatei inkl. Ordner- und Library-Zeile (für Pfade und Root-Vergleich).</summary>
    private sealed record CatalogFile(MediaFile File, Folder Folder, LibraryEntity Library)
    {
        public string FullPath => Path.Combine(Folder.Path, File.Name);
    }

    /// <summary>Sammelt Katalog-Änderungen je Library und Art für die Nachrichten am Batch-Ende.</summary>
    private sealed class ChangeSet
    {
        private readonly Dictionary<(int LibraryId, FilesChangeKind Kind), List<int>> _map = new();

        public void Add(int libraryId, FilesChangeKind kind, int fileId)
        {
            if (!_map.TryGetValue((libraryId, kind), out var list))
            {
                list = new List<int>();
                _map[(libraryId, kind)] = list;
            }

            list.Add(fileId);
        }

        public IEnumerable<FilesChangedMessage> ToMessages()
            => _map.Select(kv => new FilesChangedMessage(kv.Key.LibraryId, kv.Key.Kind, kv.Value));
    }

    private sealed class BatchRun
    {
        public required OperationBatch Batch { get; init; }
        public required List<(CatalogFile File, FileOperation Op)> Items { get; init; }

        /// <summary>Wurzeln der beteiligten Libraries (ADR-0020) — für „Ziel innerhalb der Bibliothek?“.</summary>
        public required List<LibraryRoot> Roots { get; init; }

        /// <summary>Eigene Pfade des Batches für die Ordnerüberwachung.</summary>
        public required IOwnChangeScope Own { get; init; }

        /// <summary>Vom Batch neu angelegte Ordner (Move-Ziel); werden zurückgenommen, wenn kein Item gelingt.</summary>
        public List<string> CreatedDirectories { get; } = new();

        public List<FileOperationItemResult> Results { get; } = new();
        public ChangeSet Changes { get; } = new();
    }

    private delegate Task ItemExecutor(LibraryContext ctx, BatchRun run, CatalogFile cf, FileOperation op, CancellationToken ct);

    /// <summary>
    /// Gemeinsamer Rahmen: Lock → Bibliotheken belegen (Scan-Sperre) → Dateien laden → Batch + Items (Pending)
    /// persistieren → Applying → prepare → Items ausführen → Batch abschließen → Nachrichten senden.
    /// <paramref name="gateDirectory"/>: zusätzlich zu sperrender Ordner (Ziel eines Move).
    /// </summary>
    private async Task<FileOperationBatchResult> RunBatchAsync(
        OperationType type,
        IReadOnlyList<int> ids,
        Func<IReadOnlyList<CatalogFile>, string> describe,
        Func<CatalogFile, string?> destOf,
        Func<LibraryContext, BatchRun, CancellationToken, Task<bool>>? prepare,
        ItemExecutor executeItem,
        CancellationToken ct,
        string? gateDirectory = null)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var gateLibraries = await LoadLibraryIdsAsync(ctx, ids, ct).ConfigureAwait(false);
            if (gateDirectory is not null)
            {
                gateLibraries.AddRange(await LibrariesContainingAsync(ctx, [gateDirectory], ct).ConfigureAwait(false));
            }

            // Erst nach der Sperre laden: ein Scan, auf den gewartet wurde, kann Zeilen verändert haben.
            using var lease = await AcquireLibrariesAsync(gateLibraries, ct).ConfigureAwait(false);
            using var own = _ownChanges.Begin();
            var (known, unknown) = await LoadFilesAsync(ctx, ids, ct).ConfigureAwait(false);
            if (known.Count == 0)
            {
                return new FileOperationBatchResult(0, type, 0, unknown.Count, unknown);
            }

            var libraryIds = known.Select(cf => cf.Library.Id).Distinct().ToList();
            var roots = await ctx.LibraryRoots.AsNoTracking()
                .Where(r => libraryIds.Contains(r.LibraryId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var run = await CreateBatchAsync(
                ctx,
                type,
                describe(known),
                known.Select(cf => (cf, cf.FullPath, destOf(cf))),
                roots,
                own,
                ct).ConfigureAwait(false);

            try
            {
                if (prepare is null || await prepare(ctx, run, ct).ConfigureAwait(false))
                {
                    var itemIndex = 0;
                    foreach (var (cf, op) in run.Items)
                    {
                        ct.ThrowIfCancellationRequested();
                        await executeItem(ctx, run, cf, op, ct).ConfigureAwait(false);
                        Activity.ActivityScope.ReportItems(++itemIndex, run.Items.Count); // Aktivitäts-Anzeige (ADR-0067), gedrosselt in der Zentrale
                    }
                }
            }
            catch (Exception ex)
            {
                if (!run.Results.Any(r => r.Success))
                {
                    RevokeAll(run.Own, run.CreatedDirectories);
                }

                await FinalizeAfterAbortAsync(run, ex).ConfigureAwait(false);
                throw;
            }

            return await FinishBatchAsync(ctx, run, unknown).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<BatchRun> CreateBatchAsync(
        LibraryContext ctx,
        OperationType type,
        string description,
        IEnumerable<(CatalogFile File, string Source, string? Dest)> items,
        List<LibraryRoot> roots,
        IOwnChangeScope own,
        CancellationToken ct)
    {
        var batch = new OperationBatch
        {
            OperationType = type,
            Status = BatchStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            Description = description,
        };
        var list = new List<(CatalogFile File, FileOperation Op)>();

        // ADR-0012: komplette Operationsliste vor dem ersten Dateisystem-Schritt persistieren.
        await using (var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            ctx.OperationBatches.Add(batch);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false); // Batch-Id

            foreach (var (cf, source, dest) in items)
            {
                var op = new FileOperation
                {
                    BatchId = batch.Id,
                    FileId = cf.File.Id,
                    Action = type,
                    SourcePath = source,
                    DestPath = dest,
                    Status = FileOperationStatus.Pending,
                };
                ctx.FileOperations.Add(op);
                list.Add((cf, op));
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        batch.Status = BatchStatus.Applying;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        return new BatchRun { Batch = batch, Items = list, Roots = roots, Own = own };
    }

    /// <summary>Batch-Abschluss ohne CancellationToken: die Dateisystem-Schritte sind erfolgt, die Buchung muss zu Ende laufen.</summary>
    private async Task<FileOperationBatchResult> FinishBatchAsync(
        LibraryContext ctx,
        BatchRun run,
        IReadOnlyList<FileOperationItemResult> unknown)
    {
        var succeeded = run.Results.Count(r => r.Success);
        run.Batch.Status = succeeded > 0 ? BatchStatus.Applied : BatchStatus.Failed;
        run.Batch.CompletedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        if (succeeded == 0)
        {
            RevokeAll(run.Own, run.CreatedDirectories); // leere Zielordner ohne Folders-Zeile: der nächste Scan bucht sie
        }

        Publish(run.Changes);

        var items = run.Results.Concat(unknown).ToList();
        var failed = items.Count - succeeded;
        _logger.LogInformation(
            "Batch {BatchId} ({Type}) abgeschlossen: {Ok} ok, {Failed} fehlgeschlagen — {Description}",
            run.Batch.Id, run.Batch.OperationType, succeeded, failed, run.Batch.Description);

        return new FileOperationBatchResult(run.Batch.Id, run.Batch.OperationType, succeeded, failed, items);
    }

    /// <summary>
    /// Nach einer Ausnahme (inkl. Abbruch) mitten im Batch: offene Items als Failed markieren,
    /// den Batch-Status festschreiben (frischer Context, best-effort) und die Nachrichten für die
    /// bereits verarbeiteten Dateien senden — deren Dateisystem-Zustand hat sich ja geändert.
    /// </summary>
    private async Task FinalizeAfterAbortAsync(BatchRun run, Exception ex)
    {
        var batchId = run.Batch.Id;
        if (ex is OperationCanceledException)
        {
            _logger.LogWarning("Batch {BatchId} abgebrochen", batchId);
        }
        else
        {
            _logger.LogError(ex, "Batch {BatchId} mit Ausnahme abgebrochen", batchId);
        }

        try
        {
            await using var ctx = _factory.Create();
            var now = DateTime.UtcNow;
            var error = Truncate(ex is OperationCanceledException ? "Abgebrochen." : $"Abgebrochen: {ex.Message}");

            await ctx.FileOperations
                .Where(o => o.BatchId == batchId
                            && (o.Status == FileOperationStatus.Pending || o.Status == FileOperationStatus.Applying))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, FileOperationStatus.Failed)
                    .SetProperty(o => o.Error, error)
                    .SetProperty(o => o.ExecutedAtUtc, (DateTime?)now), CancellationToken.None)
                .ConfigureAwait(false);

            var anyApplied = await ctx.FileOperations
                .AnyAsync(o => o.BatchId == batchId && o.Status == FileOperationStatus.Applied, CancellationToken.None)
                .ConfigureAwait(false);
            var status = anyApplied ? BatchStatus.Applied : BatchStatus.Failed;

            await ctx.OperationBatches
                .Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, status)
                    .SetProperty(b => b.CompletedAtUtc, (DateTime?)now), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "Batch {BatchId}: Status nach Abbruch konnte nicht gesichert werden", batchId);
        }

        Publish(run.Changes);
    }

    // ------------------------------------------------------------------ Item-Ausführung

    private async Task ExecuteRecycleItemAsync(LibraryContext ctx, BatchRun run, CatalogFile cf, FileOperation op, CancellationToken ct)
    {
        string? hint = null;
        if (File.Exists(op.SourcePath))
        {
            await MarkApplyingAsync(ctx, op, ct).ConfigureAwait(false);
            run.Own.Add(op.SourcePath);
            try
            {
                _recycleBin.MoveToRecycleBin(op.SourcePath);
            }
            catch (Exception ex)
            {
                // Auch OperationCanceledException: die kommt hier von der Shell (Benutzer-Abbruch im
                // Fehlerdialog), nicht vom CancellationToken — nur dieses Item scheitert.
                run.Own.Revoke(op.SourcePath);
                await FailItemAsync(ctx, run, cf, op, $"Papierkorb: {ex.Message}", ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            hint = "Quelldatei war nicht mehr vorhanden; Katalogeintrag wurde bereinigt.";
        }

        var error = await CommitItemAsync(ctx, op, undo: false, hint,
            token => RemoveCatalogEntryAsync(ctx, cf.File, token, op.Id)).ConfigureAwait(false); // Verknüpfungen gesichert (ADR-0054)

        if (error is not null)
        {
            run.Own.Revoke(op.SourcePath);
        }
        else
        {
            DeleteThumbnailFiles(cf.Library.Id, cf.File.Id);
            run.Changes.Add(cf.Library.Id, FilesChangeKind.Removed, cf.File.Id);
        }

        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, null, error is null, error ?? hint));
    }

    /// <summary>Zielordner im Dateisystem anlegen; scheitert das, sind alle Items Failed.</summary>
    private async Task<bool> PrepareMoveAsync(LibraryContext ctx, BatchRun run, string target, CancellationToken ct)
    {
        // Neue Ordner unter einer Wurzel der beteiligten Bibliotheken bucht der Batch selbst (Folders-Kette je Item).
        if (FindRootContaining(run.Roots, null, target) is not null)
        {
            run.CreatedDirectories.AddRange(MissingDirectoryChain(target));
            AddAll(run.Own, run.CreatedDirectories);
        }

        try
        {
            Directory.CreateDirectory(target);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var (cf, op) in run.Items)
            {
                await FailItemAsync(ctx, run, cf, op, $"Zielordner konnte nicht angelegt werden: {ex.Message}", ct).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task ExecuteMoveItemAsync(
        LibraryContext ctx,
        BatchRun run,
        CatalogFile cf,
        FileOperation op,
        string target,
        Dictionary<int, Folder> targetFolders,
        CancellationToken ct)
    {
        var dest = op.DestPath!;

        if (!File.Exists(op.SourcePath))
        {
            await FailItemAsync(ctx, run, cf, op, "Quelldatei nicht gefunden.", ct).ConfigureAwait(false);
            return;
        }

        if (PathEquals(NormalizeDirectoryPath(cf.Folder.Path), target))
        {
            await FailItemAsync(ctx, run, cf, op, "Datei liegt bereits im Zielordner.", ct).ConfigureAwait(false);
            return;
        }

        if (File.Exists(dest))
        {
            await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits.", ct).ConfigureAwait(false);
            return;
        }

        // Unterhalb einer Wurzel der Library (ADR-0020): Zielordner-Zeile (+ fehlende Zwischenordner) vor dem Move
        // anlegen — außerhalb der Item-Transaktion, damit ein Rollback keine Ordner-Ids verliert.
        Folder? targetFolder = null;
        var targetRoot = FindRootContaining(run.Roots, cf.Library.Id, target);
        if (targetRoot is not null)
        {
            if (!targetFolders.TryGetValue(cf.Library.Id, out targetFolder))
            {
                targetFolder = await EnsureFolderChainAsync(ctx, targetRoot, target, ct).ConfigureAwait(false);
                targetFolders[cf.Library.Id] = targetFolder;
            }

            var folderId = targetFolder.Id;
            var name = cf.File.Name;
            if (await ctx.Files.AnyAsync(f => f.FolderId == folderId && f.Name == name, ct).ConfigureAwait(false))
            {
                await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits im Katalog.", ct).ConfigureAwait(false);
                return;
            }
        }

        // Eigene Pfade: die Quelle immer (Zeile wird verschoben oder entfernt), das Ziel nur innerhalb der Bibliothek der
        // Datei — außerhalb bucht der Katalog es nicht, eine andere überwachte Bibliothek muss es selbst einlesen.
        string[] own = targetFolder is not null ? [op.SourcePath, dest] : [op.SourcePath];
        await MarkApplyingAsync(ctx, op, ct).ConfigureAwait(false);
        AddAll(run.Own, own);
        try
        {
            File.Move(op.SourcePath, dest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(run.Own, own);
            await FailItemAsync(ctx, run, cf, op, $"Verschieben fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        var error = await CommitItemAsync(ctx, op, undo: false, null, async token =>
        {
            if (targetFolder is not null)
            {
                cf.File.FolderId = targetFolder.Id; // FileId bleibt → Thumbnails + FTS bleiben gültig
            }
            else
            {
                await RemoveCatalogEntryAsync(ctx, cf.File, token, op.Id).ConfigureAwait(false); // Verknüpfungen gesichert (ADR-0054)
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            RevokeAll(run.Own, own);
        }
        else
        {
            if (targetFolder is not null)
            {
                run.Changes.Add(cf.Library.Id, FilesChangeKind.Moved, cf.File.Id);
            }
            else
            {
                DeleteThumbnailFiles(cf.Library.Id, cf.File.Id);
                run.Changes.Add(cf.Library.Id, FilesChangeKind.Removed, cf.File.Id);
            }
        }

        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, dest, error is null, error));
    }

    private async Task ExecuteRenameItemAsync(
        LibraryContext ctx,
        BatchRun run,
        CatalogFile cf,
        FileOperation op,
        string name,
        CancellationToken ct)
    {
        var dest = op.DestPath!;
        var oldName = cf.File.Name;
        var oldExt = cf.File.Extension;

        if (string.Equals(oldName, name, StringComparison.Ordinal))
        {
            await FailItemAsync(ctx, run, cf, op, "Der neue Name ist identisch mit dem bisherigen.", ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(op.SourcePath))
        {
            await FailItemAsync(ctx, run, cf, op, "Quelldatei nicht gefunden.", ct).ConfigureAwait(false);
            return;
        }

        // Nur Groß-/Kleinschreibung geändert: auf NTFS ist das dieselbe Datei → Existenzprüfung überspringen.
        var caseOnly = string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly)
        {
            if (File.Exists(dest))
            {
                await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits.", ct).ConfigureAwait(false);
                return;
            }

            var folderId = cf.File.FolderId;
            if (await ctx.Files.AnyAsync(f => f.FolderId == folderId && f.Name == name, ct).ConfigureAwait(false))
            {
                await FailItemAsync(ctx, run, cf, op, "Ziel existiert bereits im Katalog.", ct).ConfigureAwait(false);
                return;
            }
        }

        string[] own = [op.SourcePath, dest];
        await MarkApplyingAsync(ctx, op, ct).ConfigureAwait(false);
        AddAll(run.Own, own);
        try
        {
            File.Move(op.SourcePath, dest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(run.Own, own);
            await FailItemAsync(ctx, run, cf, op, $"Umbenennen fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        var newExt = Path.GetExtension(name).ToLowerInvariant();
        var mediaType = _formats.TryClassify(newExt, out var classified) ? classified : cf.File.MediaType;

        var error = await CommitItemAsync(ctx, op, undo: false, null, async token =>
        {
            await FtsDeleteAsync(ctx, cf.File.Id, oldName, oldExt, token).ConfigureAwait(false);
            cf.File.Name = name;
            cf.File.Extension = newExt;
            cf.File.MediaType = mediaType;
            await FtsInsertAsync(ctx, cf.File.Id, name, newExt, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (error is null)
        {
            run.Changes.Add(cf.Library.Id, FilesChangeKind.Moved, cf.File.Id);
        }
        else
        {
            RevokeAll(run.Own, own);
        }

        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, dest, error is null, error));
    }

    // ------------------------------------------------------------------ Undo

    private async Task<FileOperationBatchResult?> UndoCoreAsync(LibraryContext ctx, int batchId, IOwnChangeScope own, CancellationToken ct)
    {
        var batch = await ctx.OperationBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        // Rückwärts: jüngste Operation zuerst.
        var ops = await ctx.FileOperations
            .Where(o => o.BatchId == batchId)
            .OrderByDescending(o => o.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (!CanUndo(batch.OperationType, batch.Status))
        {
            var reason = IsUndoableType(batch.OperationType)
                ? $"Batch {batch.Id} hat Status {batch.Status} und ist nicht (mehr) rückgängig machbar."
                : $"Batch {batch.Id} ({batch.OperationType}) ist nicht rückgängig machbar.";
            var items = ops.Count > 0
                ? ops.Select(o => new FileOperationItemResult(o.FileId ?? 0, o.SourcePath, o.DestPath, false, reason)).ToList()
                : new List<FileOperationItemResult> { new(0, string.Empty, null, false, reason) };
            return new FileOperationBatchResult(batch.Id, batch.OperationType, 0, items.Count, items);
        }

        batch.Status = BatchStatus.Undoing;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        var results = new List<FileOperationItemResult>();
        var changes = new ChangeSet();
        try
        {
            if (batch.OperationType == OperationType.RenameFolder)
            {
                results.AddRange(await UndoRenameFolderAsync(ctx, ops, changes, own, ct).ConfigureAwait(false));
            }
            else if (batch.OperationType == OperationType.AdoptFolder)
            {
                results.AddRange(await UndoAdoptFolderAsync(ctx, ops, changes, own, ct).ConfigureAwait(false)); // ADR-0034
            }
            else if (batch.OperationType is OperationType.RecycleFolder or OperationType.RemoveFromCatalog or OperationType.ExcludeFromCatalog)
            {
                results.AddRange(await UndoRemovalAsync(ctx, batch, ops, changes, own, ct).ConfigureAwait(false)); // ADR-0065, ADR-0066
            }
            else if (batch.OperationType == OperationType.MoveFolder)
            {
                results.AddRange(await UndoMoveFolderAsync(ctx, ops, changes, own, ct).ConfigureAwait(false)); // ADR-0066
            }
            else if (batch.OperationType == OperationType.Copy)
            {
                foreach (var op in ops.Where(o => o.Status == FileOperationStatus.Applied))
                {
                    ct.ThrowIfCancellationRequested();
                    results.Add(await UndoCopyItemAsync(ctx, op, changes, own, ct).ConfigureAwait(false)); // ADR-0068
                }
            }
            else if (batch.OperationType == OperationType.CreateVariant)
            {
                // ADR-0071: neue Fassungen in den Papierkorb, Katalog sauber (FileOperationService.Variants.cs)
                foreach (var op in ops.Where(o => o.Status == FileOperationStatus.Applied))
                {
                    ct.ThrowIfCancellationRequested();
                    results.Add(await UndoCreateVariantItemAsync(ctx, op, changes, own, ct).ConfigureAwait(false));
                }
            }
            else
            {
                foreach (var op in ops)
                {
                    ct.ThrowIfCancellationRequested();
                    if (op.Status != FileOperationStatus.Applied)
                    {
                        continue; // Failed/Undone: nichts zurückzunehmen
                    }

                    results.Add(await UndoItemAsync(ctx, op, changes, own, ct).ConfigureAwait(false));
                }
            }
        }
        catch (Exception ex)
        {
            await RestoreBatchStatusAfterUndoAbortAsync(batch.Id, ex).ConfigureAwait(false);
            Publish(changes);
            throw;
        }

        // Teilweise zurückgenommen → Undone (offene Items behalten Status Applied + Error);
        // gar nichts zurückgenommen → Applied, erneuter Versuch möglich.
        var undone = results.Count(r => r.Success);
        batch.Status = undone > 0 ? BatchStatus.Undone : BatchStatus.Applied;
        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        Publish(changes);
        _logger.LogInformation(
            "Batch {BatchId} ({Type}) rückgängig: {Ok} ok, {Failed} fehlgeschlagen",
            batch.Id, batch.OperationType, undone, results.Count - undone);

        return new FileOperationBatchResult(batch.Id, batch.OperationType, undone, results.Count - undone, results);
    }

    private async Task<FileOperationItemResult> UndoItemAsync(LibraryContext ctx, FileOperation op, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var source = op.SourcePath;
        var dest = op.DestPath;

        if (string.IsNullOrEmpty(dest))
        {
            return await UndoFailAsync(ctx, op, "Kein Zielpfad protokolliert.", ct).ConfigureAwait(false);
        }

        if (!File.Exists(dest))
        {
            return await UndoFailAsync(ctx, op, "Datei ist am Zielort nicht mehr vorhanden.", ct).ConfigureAwait(false);
        }

        var caseOnly = string.Equals(dest, source, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(source))
        {
            return await UndoFailAsync(ctx, op, "Ursprungsort ist inzwischen belegt.", ct).ConfigureAwait(false);
        }

        // Katalogzeile der Datei am Zielort: zuerst über den aktuellen Pfad — nach einem Rescan sind
        // FileIds neu vergeben, und die protokollierte FileId könnte auf eine fremde Datei zeigen.
        // Die FileId zählt nur, wenn ihre Zeile noch auf DestPath (oder, bei gescheiterter
        // Katalog-Buchung, noch auf SourcePath) zeigt.
        var sourceDir = Path.GetDirectoryName(source)!;
        var sourceName = Path.GetFileName(source);

        var row = await FindCatalogRowByPathAsync(ctx, dest, ct).ConfigureAwait(false)
                  ?? await FindCatalogRowByIdAsync(ctx, op.FileId, dest, source, ct).ConfigureAwait(false);

        // Ursprungsordner unter einer Wurzel (ADR-0020): bei bekannter Zeile nur Wurzeln ihrer Library,
        // sonst irgendeine Wurzel (Datei war aus der Library heraus verschoben worden).
        LibraryEntity? library;
        LibraryRoot? sourceRoot;
        if (row is not null)
        {
            library = await ctx.Libraries.FirstOrDefaultAsync(l => l.Id == row.LibraryId, ct).ConfigureAwait(false);
            sourceRoot = library is null ? null : await FindRootContainingAsync(ctx, library.Id, sourceDir, ct).ConfigureAwait(false);
        }
        else
        {
            sourceRoot = await FindRootContainingAsync(ctx, null, sourceDir, ct).ConfigureAwait(false);
            library = sourceRoot is null ? null : await ctx.Libraries.FirstOrDefaultAsync(l => l.Id == sourceRoot.LibraryId, ct).ConfigureAwait(false);
        }

        Folder? folder = null;
        if (library is not null && sourceRoot is not null)
        {
            folder = await EnsureFolderChainAsync(ctx, sourceRoot, sourceDir, ct).ConfigureAwait(false);
        }

        // Eigene Pfade: der Zielort wird geräumt (die Zeile zieht mit oder es gab keine); Ursprungsort und neu angelegte
        // Ordner nur, wenn der Katalog sie bucht (Ursprung unter einer Wurzel).
        var ownPaths = new List<string> { dest };
        if (folder is not null)
        {
            ownPaths.AddRange(MissingDirectoryChain(sourceDir));
            ownPaths.Add(source);
        }

        AddAll(own, ownPaths);
        try
        {
            Directory.CreateDirectory(sourceDir);
            File.Move(dest, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(own, ownPaths);
            return await UndoFailAsync(ctx, op, $"Zurückverschieben fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false);
        }

        var resultFileId = op.FileId ?? 0;
        int? movedId = null;
        int? addedId = null;
        MediaFile? created = null;
        string? hint = null;

        var error = await CommitItemAsync(ctx, op, undo: true, null, async token =>
        {
            if (row is not null)
            {
                if (folder is not null)
                {
                    row.FolderId = folder.Id;
                }

                if (!string.Equals(row.Name, sourceName, StringComparison.Ordinal))
                {
                    var ext = Path.GetExtension(sourceName).ToLowerInvariant();
                    await FtsDeleteAsync(ctx, row.Id, row.Name, row.Extension, token).ConfigureAwait(false);
                    row.Name = sourceName;
                    row.Extension = ext;
                    row.MediaType = _formats.TryClassify(ext, out var mt) ? mt : row.MediaType;
                    await FtsInsertAsync(ctx, row.Id, row.Name, row.Extension, token).ConfigureAwait(false);
                }

                movedId = row.Id;
            }
            else if (library is not null && folder is not null)
            {
                created = await InsertCatalogRowAsync(ctx, library, folder, source, token).ConfigureAwait(false);
                if (created is not null)
                {
                    addedId = created.Id;
                    await Albums.FileLinkUndoStore.RestoreAsync(ctx, op.Id, created.Id, token).ConfigureAwait(false); // ADR-0054
                }
                else
                {
                    hint = "Datei wiederhergestellt, aber nicht im Katalog (Endung nicht registriert).";
                }
            }
            else
            {
                hint = "Datei wiederhergestellt, liegt aber außerhalb jeder Library (kein Katalogeintrag).";
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            RevokeAll(own, ownPaths); // Katalog nicht gebucht: die Ordnerüberwachung soll nachsehen
        }

        if (error is null)
        {
            if (movedId is int m)
            {
                changes.Add(row!.LibraryId, FilesChangeKind.Moved, m);
            }

            if (addedId is int a)
            {
                changes.Add(library!.Id, FilesChangeKind.Added, a);
                resultFileId = a;
            }
        }
        else if (created is not null)
        {
            // Die Zeile wurde innerhalb der zurückgerollten Transaktion angelegt: aus dem Tracker nehmen,
            // sonst kollidiert ihre (von SQLite wiederverwendete) Id mit dem nächsten Insert.
            ctx.Entry(created).State = EntityState.Detached;
        }

        return new FileOperationItemResult(resultFileId, source, dest, error is null, error ?? hint);
    }

    /// <summary>
    /// Undo eines RenameFolder-Batches: der Ordner wird in einem Schritt zurückbenannt (Dateisystem), danach die
    /// Pfade der Folders-Zeilen; alle Operationen des Batches (Ordner + Dateien) werden Undone.
    /// </summary>
    private async Task<List<FileOperationItemResult>> UndoRenameFolderAsync(LibraryContext ctx, List<FileOperation> ops, ChangeSet changes, IOwnChangeScope own, CancellationToken ct)
    {
        var folderOp = ops.FirstOrDefault(o => o.FileId is null && o.Action == OperationType.RenameFolder);
        var fileOps = ops.Where(o => !ReferenceEquals(o, folderOp)).ToList();
        if (folderOp is null || folderOp.Status != FileOperationStatus.Applied || string.IsNullOrEmpty(folderOp.DestPath))
        {
            return new List<FileOperationItemResult>
            {
                new(0, folderOp?.SourcePath ?? string.Empty, folderOp?.DestPath, false, "Ordner-Operation nicht (mehr) rückgängig machbar."),
            };
        }

        var oldPath = NormalizeDirectoryPath(folderOp.SourcePath);
        var newPath = NormalizeDirectoryPath(folderOp.DestPath);
        if (!Directory.Exists(newPath))
        {
            return new List<FileOperationItemResult> { await UndoFailAsync(ctx, folderOp, "Ordner ist am Zielort nicht mehr vorhanden.", ct).ConfigureAwait(false) };
        }

        var caseOnly = PathEquals(oldPath, newPath);
        if (!caseOnly && (Directory.Exists(oldPath) || File.Exists(oldPath)))
        {
            return new List<FileOperationItemResult> { await UndoFailAsync(ctx, folderOp, "Ursprungsort ist inzwischen belegt.", ct).ConfigureAwait(false) };
        }

        var lowerNew = AsciiLower(newPath);
        var folder = await ctx.Folders.FirstOrDefaultAsync(f => f.Path.ToLower() == lowerNew, ct).ConfigureAwait(false);
        var lowerPrefix = AsciiLower(newPath + Path.DirectorySeparatorChar);
        var descendants = folder is null
            ? new List<Folder>()
            : await ctx.Folders.Where(f => f.LibraryId == folder.LibraryId && f.Path.ToLower().StartsWith(lowerPrefix)).ToListAsync(ct).ConfigureAwait(false);

        // Ohne Katalogzeile bucht das Undo nichts — dann bleibt das Ereignis fremd und die Überwachung liest neu ein.
        string[] ownPaths = folder is not null ? [newPath, oldPath] : [];
        AddAll(own, ownPaths);
        try
        {
            Directory.Move(newPath, oldPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RevokeAll(own, ownPaths);
            return new List<FileOperationItemResult> { await UndoFailAsync(ctx, folderOp, $"Zurückbenennen fehlgeschlagen: {ex.Message}", ct).ConfigureAwait(false) };
        }

        var error = await CommitItemAsync(ctx, folderOp, undo: true, null, _ =>
        {
            if (folder is not null)
            {
                ApplyFolderPaths(folder, descendants, newPath, oldPath, FolderNameOf(oldPath));
            }

            foreach (var op in fileOps)
            {
                if (op.Status == FileOperationStatus.Applied)
                {
                    MarkDone(op, undo: true, null);
                }
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (error is not null)
        {
            RevokeAll(own, ownPaths);
        }

        var results = new List<FileOperationItemResult>(ops.Count)
        {
            new(0, folderOp.SourcePath, folderOp.DestPath, error is null, error),
        };
        foreach (var op in fileOps)
        {
            results.Add(new FileOperationItemResult(op.FileId ?? 0, op.SourcePath, op.DestPath, error is null, error));
            if (error is null && op.FileId is { } fileId && folder is not null)
            {
                changes.Add(folder.LibraryId, FilesChangeKind.Moved, fileId);
            }
        }

        if (folder is not null)
        {
            if (error is null)
            {
                await FollowExclusionsAsync(folder.LibraryId, newPath, oldPath).ConfigureAwait(false); // ADR-0066
            }

            PublishFoldersChanged(folder.LibraryId);
        }

        _logger.LogInformation("Ordner-Umbenennung rückgängig: {New} → {Old} ({Files} Dateien)", newPath, oldPath, fileOps.Count);
        return results;
    }

    private async Task<FileOperationItemResult> UndoFailAsync(LibraryContext ctx, FileOperation op, string message, CancellationToken ct)
    {
        // Status bleibt Applied (nichts passiert) — Error dokumentiert den gescheiterten Undo-Versuch.
        op.Error = Truncate(message);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Undo fehlgeschlagen: {Dest} → {Message}", op.DestPath, message);
        return new FileOperationItemResult(op.FileId ?? 0, op.SourcePath, op.DestPath, false, message);
    }

    private async Task RestoreBatchStatusAfterUndoAbortAsync(int batchId, Exception ex)
    {
        _logger.LogError(ex, "Undo von Batch {BatchId} abgebrochen", batchId);
        try
        {
            await using var ctx = _factory.Create();
            var anyApplied = await ctx.FileOperations
                .AnyAsync(o => o.BatchId == batchId && o.Status == FileOperationStatus.Applied, CancellationToken.None)
                .ConfigureAwait(false);
            var status = anyApplied ? BatchStatus.Applied : BatchStatus.Undone;
            await ctx.OperationBatches
                .Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, status), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "Batch {BatchId}: Status nach Undo-Abbruch konnte nicht gesichert werden", batchId);
        }
    }

    // ------------------------------------------------------------------ Item-Helfer

    /// <summary>Item auf Applying setzen — unmittelbar vor dem Dateisystem-Schritt (Crash-Marker, ADR-0012).</summary>
    private static Task MarkApplyingAsync(LibraryContext ctx, FileOperation op, CancellationToken ct)
    {
        op.Status = FileOperationStatus.Applying;
        return ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Katalog-Teil eines Items in einer kurzen Transaktion; danach Item-Status Applied bzw. Undone.
    /// Läuft bewusst ohne CancellationToken: der Dateisystem-Schritt ist bereits erfolgt, ein Abbruch
    /// hier würde Dateisystem und Katalog auseinanderlaufen lassen. Scheitert der Katalog-Teil, wird
    /// der Tracker zurückgesetzt, der Status trotzdem gesetzt und der Fehler als Text zurückgegeben.
    /// </summary>
    private async Task<string?> CommitItemAsync(
        LibraryContext ctx,
        FileOperation op,
        bool undo,
        string? hint,
        Func<CancellationToken, Task> catalogWork)
    {
        var none = CancellationToken.None;
        try
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(none).ConfigureAwait(false);
            await catalogWork(none).ConfigureAwait(false);
            MarkDone(op, undo, hint);
            await ctx.SaveChangesAsync(none).ConfigureAwait(false);
            if (_ftsRebuildPending)
            {
                // Erst nach dem SaveChanges, damit der Neuaufbau den aktuellen Files-Stand indexiert.
                await ctx.Database.ExecuteSqlRawAsync("INSERT INTO FilesFts(FilesFts) VALUES('rebuild');", none).ConfigureAwait(false);
                _ftsRebuildPending = false;
                _logger.LogWarning("FTS-Index nach fehlgeschlagenem 'delete' neu aufgebaut (Op {OpId})", op.Id);
            }

            await tx.CommitAsync(none).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            _ftsRebuildPending = false;
            DiscardTrackedChanges(ctx);
            var message = Truncate(
                $"Katalog-Update fehlgeschlagen (Datei wurde bereits {(undo ? "zurückverschoben" : "verarbeitet")}): {ex.Message}");
            _logger.LogError(ex, "Katalog-Update fehlgeschlagen: Op {OpId} {Source} — Katalog ist bis zum nächsten Scan inkonsistent", op.Id, op.SourcePath);
            MarkDone(op, undo, message);
            await ctx.SaveChangesAsync(none).ConfigureAwait(false);
            return message;
        }
    }

    private static void MarkDone(FileOperation op, bool undo, string? error)
    {
        var now = DateTime.UtcNow;
        op.Error = error;
        if (undo)
        {
            op.Status = FileOperationStatus.Undone;
            op.UndoneAtUtc = now;
        }
        else
        {
            op.Status = FileOperationStatus.Applied;
            op.ExecutedAtUtc = now;
        }
    }

    private async Task FailItemAsync(LibraryContext ctx, BatchRun run, CatalogFile cf, FileOperation op, string message, CancellationToken ct)
    {
        op.Status = FileOperationStatus.Failed;
        op.Error = Truncate(message);
        op.ExecutedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Item fehlgeschlagen: {Source} → {Message}", op.SourcePath, message);
        run.Results.Add(new FileOperationItemResult(cf.File.Id, op.SourcePath, op.DestPath, false, message));
    }

    /// <summary>Setzt alle offenen Änderungen im Tracker zurück (nach Rollback einer Item-Transaktion).</summary>
    private static void DiscardTrackedChanges(LibraryContext ctx)
    {
        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ Katalog

    private async Task<(List<CatalogFile> Known, List<FileOperationItemResult> Unknown)> LoadFilesAsync(
        LibraryContext ctx,
        IReadOnlyList<int> ids,
        CancellationToken ct)
    {
        var found = new Dictionary<int, CatalogFile>();
        foreach (var chunk in ids.Chunk(QueryChunkSize))
        {
            var rows = await ctx.Files
                .Where(f => chunk.Contains(f.Id))
                .Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { File = f, Folder = d })
                .Join(ctx.Libraries, x => x.File.LibraryId, l => l.Id, (x, l) => new { x.File, x.Folder, Library = l })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var r in rows)
            {
                found[r.File.Id] = new CatalogFile(r.File, r.Folder, r.Library);
            }
        }

        var known = new List<CatalogFile>(ids.Count);
        var unknown = new List<FileOperationItemResult>();
        foreach (var id in ids)
        {
            if (found.TryGetValue(id, out var cf))
            {
                known.Add(cf);
            }
            else
            {
                unknown.Add(new FileOperationItemResult(id, string.Empty, null, false, "Datei nicht im Katalog."));
                _logger.LogWarning("FileId {FileId} nicht im Katalog (oder Ordner/Library fehlt)", id);
            }
        }

        return (known, unknown);
    }

    /// <summary>Files-Zeile zu einem vollständigen Dateipfad (Ordner über Folders.Path, Name case-insensitiv wie NTFS); null, wenn unbekannt.</summary>
    private static async Task<MediaFile?> FindCatalogRowByPathAsync(LibraryContext ctx, string fullPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var normalizedDir = NormalizeDirectoryPath(dir);
        var candidates = new[] { AsciiLower(normalizedDir), AsciiLower(normalizedDir + Path.DirectorySeparatorChar) };
        var folderIds = await ctx.Folders
            .Where(f => candidates.Contains(f.Path.ToLower()))
            .Select(f => f.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (folderIds.Count == 0)
        {
            return null;
        }

        var lowerName = AsciiLower(name);
        return await ctx.Files
            .Where(f => folderIds.Contains(f.FolderId) && f.Name.ToLower() == lowerName)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Files-Zeile zur protokollierten FileId — nur, wenn sie noch auf einen der erwarteten Pfade zeigt.</summary>
    private static async Task<MediaFile?> FindCatalogRowByIdAsync(LibraryContext ctx, int? fileId, string dest, string source, CancellationToken ct)
    {
        if (fileId is not int id)
        {
            return null;
        }

        var hit = await ctx.Files
            .Where(f => f.Id == id)
            .Join(ctx.Folders, f => f.FolderId, d => d.Id, (f, d) => new { File = f, Folder = d })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (hit is null)
        {
            return null;
        }

        var currentPath = Path.Combine(hit.Folder.Path, hit.File.Name);
        return FilePathEquals(currentPath, dest) || FilePathEquals(currentPath, source) ? hit.File : null;
    }

    /// <summary>
    /// FTS-Eintrag (alte Werte), Thumbnails-Zeilen und Files-Zeile entfernen — innerhalb der laufenden Transaktion. Album-
    /// und Ordner-Verknüpfungen gehen per Kaskade mit; mit <paramref name="operationId"/> werden sie vorher zur Operation
    /// gesichert, damit „Rückgängig“ bzw. die Rückkehr der Datei sie wiederherstellt (ADR-0054).
    /// </summary>
    private async Task RemoveCatalogEntryAsync(LibraryContext ctx, MediaFile file, CancellationToken ct, int? operationId = null)
    {
        if (operationId is { } op)
        {
            await Albums.FileLinkUndoStore.CaptureAsync(ctx, op, file.Id, ct).ConfigureAwait(false);
        }

        await FtsDeleteAsync(ctx, file.Id, file.Name, file.Extension, ct).ConfigureAwait(false);
        var fileId = file.Id;
        await ctx.Thumbnails.Where(t => t.FileId == fileId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        ctx.Files.Remove(file);
    }

    /// <summary>Legt eine Files-Zeile aus FileInfo neu an (Undo eines Moves nach außen) inkl. FTS-Insert; null bei unbekannter Endung.</summary>
    private async Task<MediaFile?> InsertCatalogRowAsync(LibraryContext ctx, LibraryEntity library, Folder folder, string fullPath, CancellationToken ct)
    {
        var info = new FileInfo(fullPath);
        var ext = info.Extension.ToLowerInvariant();
        if (!_formats.TryClassify(ext, out var mediaType))
        {
            return null;
        }

        var name = info.Name;
        var folderId = folder.Id;
        var existing = await ctx.Files.FirstOrDefaultAsync(f => f.FolderId == folderId && f.Name == name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var row = new MediaFile
        {
            LibraryId = library.Id,
            FolderId = folder.Id,
            Name = name,
            Extension = ext,
            Size = info.Length,
            CreatedAtUtc = info.CreationTimeUtc,
            ModifiedAtUtc = info.LastWriteTimeUtc,
            MediaType = mediaType,
            IsFavorite = false,
            Rating = null,
            Missing = false,
        };
        ctx.Files.Add(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false); // neue Id
        await FtsInsertAsync(ctx, row.Id, row.Name, row.Extension, ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// FTS5-'delete' mit den ALTEN Werten (Repo-Regel). Scheitert das Kommando (z. B. beschädigter
    /// Index), wird nach dem nächsten SaveChanges der ganze Index per 'rebuild' neu aufgebaut — ein
    /// vorheriges <c>SELECT rowid FROM FilesFts WHERE rowid=?</c> ist bei External-Content nicht möglich
    /// (liest aus Files, nicht aus dem Index).
    /// </summary>
    private async Task FtsDeleteAsync(LibraryContext ctx, int id, string name, string extension, CancellationToken ct)
    {
        try
        {
            await ctx.Database.ExecuteSqlAsync(
                $"INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) VALUES('delete', {id}, {name}, {extension})", ct)
                .ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "FTS-'delete' für rowid {Id} fehlgeschlagen — Index wird nach dem Commit neu aufgebaut", id);
            _ftsRebuildPending = true;
        }
    }

    private static Task FtsInsertAsync(LibraryContext ctx, int id, string name, string extension, CancellationToken ct)
        => ctx.Database.ExecuteSqlAsync(
            $"INSERT INTO FilesFts(rowid, Name, Extension) VALUES({id}, {name}, {extension})", ct);

    /// <summary>
    /// Stellt die Folders-Kette von der Wurzel (ADR-0020) bis <paramref name="folderPath"/> sicher
    /// (Path, RootId, ParentId-Kette, Name, ScannedAt) und liefert die Zeile des Zielordners.
    /// Vergleich ASCII-case-insensitiv (wie SQLite <c>lower()</c>); erwartet eine normalisierte Wurzel.
    /// Der Scanner legt nur Ordner MIT Dateien an, deren ParentId bleibt leer, wenn der Elternordner
    /// keine Dateien hat — wird so ein Elternordner hier nachgetragen, werden diese verwaisten
    /// direkten Kinder an ihn gehängt (gemischte Bäume bleiben konsistent).
    /// </summary>
    private static async Task<Folder> EnsureFolderChainAsync(LibraryContext ctx, LibraryRoot libraryRoot, string folderPath, CancellationToken ct)
    {
        var root = NormalizeDirectoryPath(libraryRoot.Path);
        var target = NormalizeDirectoryPath(folderPath);
        if (!IsInsideRoot(target, root))
        {
            throw new InvalidOperationException($"„{target}“ liegt nicht unterhalb der Wurzel „{root}“.");
        }

        var chain = new List<string> { root };
        if (!PathEquals(target, root))
        {
            var relative = target[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            foreach (var segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                chain.Add(current);
            }
        }

        var candidates = chain.Select(AsciiLower).Append(AsciiLower(libraryRoot.Path)).Distinct().ToList();
        var libraryId = libraryRoot.LibraryId;
        var existing = await ctx.Folders
            .Where(f => f.LibraryId == libraryId && candidates.Contains(f.Path.ToLower()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Folder? parent = null;
        foreach (var path in chain)
        {
            var row = existing.FirstOrDefault(f => PathEquals(NormalizeDirectoryPath(f.Path), path));
            if (row is null)
            {
                row = new Folder
                {
                    LibraryId = libraryId,
                    RootId = libraryRoot.Id,
                    Path = path,
                    ParentId = parent?.Id,
                    Name = FolderNameOf(path),
                    ScannedAt = DateTime.UtcNow,
                };
                ctx.Folders.Add(row);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false); // Id für die ParentId-Kette
                existing.Add(row);
                await AttachOrphanChildrenAsync(ctx, row, existing, ct).ConfigureAwait(false);
            }

            parent = row;
        }

        return parent!;
    }

    /// <summary>Verwaiste direkte Kindordner (ParentId NULL, Pfad = neuer Ordner + ein Segment) an die neue Zeile hängen.</summary>
    private static async Task AttachOrphanChildrenAsync(LibraryContext ctx, Folder created, List<Folder> tracked, CancellationToken ct)
    {
        var prefix = AsciiLower(NormalizeDirectoryPath(created.Path)) + Path.DirectorySeparatorChar;
        var prefixLength = prefix.Length;
        var separator = Path.DirectorySeparatorChar.ToString();
        var libraryId = created.LibraryId;
        var createdId = created.Id;

        var attached = await ctx.Folders
            .Where(f => f.LibraryId == libraryId
                        && f.ParentId == null
                        && f.Id != createdId
                        && f.Path.ToLower().StartsWith(prefix)
                        && !f.Path.Substring(prefixLength).Contains(separator))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, createdId), ct)
            .ConfigureAwait(false);

        if (attached == 0)
        {
            return;
        }

        // Bereits geladene Zeilen (Teil der Kette) nachziehen, damit der Tracker den DB-Stand spiegelt.
        foreach (var f in tracked)
        {
            if (f.Id != createdId && f.ParentId is null && IsDirectChild(f.Path, prefix))
            {
                f.ParentId = createdId;
            }
        }
    }

    private static bool IsDirectChild(string path, string lowerPrefix)
    {
        var lower = AsciiLower(path);
        return lower.StartsWith(lowerPrefix, StringComparison.Ordinal)
               && !lower[lowerPrefix.Length..].Contains(Path.DirectorySeparatorChar);
    }

    /// <summary>Wurzel (ADR-0020), unter der <paramref name="directory"/> liegt — bei mehreren die längste; optional auf eine Library begrenzt.</summary>
    private static async Task<LibraryRoot?> FindRootContainingAsync(LibraryContext ctx, int? libraryId, string directory, CancellationToken ct)
    {
        var roots = await ctx.LibraryRoots.AsNoTracking()
            .Where(r => libraryId == null || r.LibraryId == libraryId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return FindRootContaining(roots, libraryId, directory);
    }

    private static LibraryRoot? FindRootContaining(IEnumerable<LibraryRoot> roots, int? libraryId, string directory)
    {
        var dir = NormalizeDirectoryPath(directory);
        return roots
            .Where(r => (libraryId is null || r.LibraryId == libraryId) && IsInsideRoot(dir, NormalizeDirectoryPath(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------ Scan-Sperre und eigene Pfade

    /// <summary>
    /// Belegt die Bibliotheken für die Dauer eines Batches (aufsteigend nach Id, feste Reihenfolge). Wartet, solange ein
    /// Scan einer davon läuft (abbrechbar). Dispose gibt alle wieder frei.
    /// </summary>
    private async Task<IDisposable> AcquireLibrariesAsync(IEnumerable<int> libraryIds, CancellationToken ct)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var libraryId in libraryIds.Distinct().Order())
            {
                if (_scanGate.IsBusy(libraryId))
                {
                    _logger.LogInformation("Datei-Operation wartet auf den laufenden Scan der Bibliothek {LibraryId}", libraryId);
                }

                leases.Add(await _scanGate.AcquireForFileOperationAsync(libraryId, ct).ConfigureAwait(false));
            }
        }
        catch (ScanGateWaitCanceledException ex)
        {
            // „Abbrechen“ in der Statusleiste (0.2.22): der Batch hat noch nichts protokolliert oder geändert.
            _logger.LogInformation("Datei-Operation beim Warten auf die Bibliothek {LibraryId} abgebrochen", ex.Wait.LibraryId);
            ReleaseAll(leases);
            throw;
        }
        catch
        {
            ReleaseAll(leases);
            throw;
        }

        return new Leases(leases);
    }

    private static void ReleaseAll(List<IDisposable> leases)
    {
        for (var i = leases.Count - 1; i >= 0; i--)
        {
            leases[i].Dispose();
        }
    }

    private sealed class Leases(List<IDisposable> leases) : IDisposable
    {
        public void Dispose() => ReleaseAll(leases);
    }

    /// <summary>Bibliotheken der Katalogzeilen (unbekannte Ids zählen nicht).</summary>
    private static async Task<List<int>> LoadLibraryIdsAsync(LibraryContext ctx, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var result = new List<int>();
        foreach (var chunk in ids.Chunk(QueryChunkSize))
        {
            result.AddRange(await ctx.Files.AsNoTracking()
                .Where(f => chunk.Contains(f.Id))
                .Select(f => f.LibraryId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>Alle Bibliotheken, unter deren Wurzeln (ADR-0020) einer der Ordner liegt — auch bei verschachtelten Wurzeln.</summary>
    private static async Task<List<int>> LibrariesContainingAsync(LibraryContext ctx, IReadOnlyCollection<string> directories, CancellationToken ct)
    {
        if (directories.Count == 0)
        {
            return new List<int>();
        }

        var roots = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var normalizedRoots = roots.Select(r => (r.LibraryId, Path: NormalizeDirectoryPath(r.Path))).ToList();
        var result = new HashSet<int>();
        foreach (var directory in directories)
        {
            var dir = NormalizeDirectoryPath(directory);
            foreach (var (libraryId, rootPath) in normalizedRoots)
            {
                if (IsInsideRoot(dir, rootPath))
                {
                    result.Add(libraryId);
                }
            }
        }

        return result.ToList();
    }

    /// <summary>Noch nicht vorhandene Ordner vom Pfad aufwärts bis zum ersten vorhandenen (die <c>Directory.CreateDirectory</c> anlegen wird).</summary>
    private static List<string> MissingDirectoryChain(string directory)
    {
        var chain = new List<string>();
        var current = directory;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        return chain;
    }

    private static void AddAll(IOwnChangeScope own, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            own.Add(path);
        }
    }

    private static void RevokeAll(IOwnChangeScope own, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            own.Revoke(path);
        }
    }

    private void PublishFoldersChanged(int libraryId)
    {
        try
        {
            _messenger.Send(new FoldersChangedMessage(libraryId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Empfänger der FoldersChangedMessage warf eine Ausnahme");
        }
    }

    private void DeleteThumbnailFiles(int libraryId, int fileId)
    {
        var dir = _paths.GetLibraryThumbnailsPath(libraryId);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{fileId}_*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Thumbnail-Datei konnte nicht gelöscht werden: {Path}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Thumbnail-Cache nicht lesbar: {Dir}", dir);
        }
    }

    private void Publish(ChangeSet changes)
    {
        foreach (var message in changes.ToMessages())
        {
            try
            {
                _messenger.Send(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der FilesChangedMessage ({Kind}) warf eine Ausnahme", message.Kind);
            }
        }
    }

    // ------------------------------------------------------------------ Validierung / Pfade

    private static IReadOnlyList<int> NormalizeIds(IReadOnlyList<int> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Es wurden keine Dateien angegeben.", nameof(fileIds));
        }

        return ids;
    }

    private static string ValidateFileName(string? newName)
    {
        var name = newName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Der Dateiname darf nicht leer sein.", nameof(newName));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException("Der Dateiname enthält ungültige Zeichen.", nameof(newName));
        }

        if (name is "." or ".." || name.EndsWith('.'))
        {
            throw new ArgumentException("Der Dateiname ist ungültig.", nameof(newName));
        }

        var stem = name.Split('.', 2)[0].TrimEnd();
        if (ReservedDeviceNames.Contains(stem))
        {
            throw new ArgumentException($"„{stem}“ ist ein reservierter Windows-Gerätename.", nameof(newName));
        }

        return name;
    }

    private static bool CanUndo(OperationType type, BatchStatus status)
        => status == BatchStatus.Applied && IsUndoableType(type);

    private static bool IsUndoableType(OperationType type)
        => type is OperationType.Move or OperationType.Rename or OperationType.RenameFolder or OperationType.AdoptFolder
            or OperationType.RecycleFolder or OperationType.RemoveFromCatalog // ADR-0065
            or OperationType.MoveFolder or OperationType.ExcludeFromCatalog // ADR-0066
            or OperationType.Copy // ADR-0068
            or OperationType.CreateVariant; // ADR-0071

    /// <summary>Voller Pfad ohne abschließende Trennzeichen; Laufwerks-/UNC-Wurzeln behalten ihres („E:\“).</summary>
    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInsideRoot(string normalizedPath, string normalizedRoot)
    {
        if (PathEquals(normalizedPath, normalizedRoot))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Vollständige Dateipfade vergleichen (voll aufgelöst, case-insensitiv wie NTFS).</summary>
    private static bool FilePathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string FolderNameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Nur A–Z falten — exakt das Verhalten von SQLite <c>lower()</c> ohne ICU.</summary>
    private static string AsciiLower(string value)
        => string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    private static string CountText(int count) => count == 1 ? "1 Datei" : $"{count} Dateien";

    private static string Truncate(string value)
        => value.Length > ErrorMaxLen ? value[..ErrorMaxLen] : value;
}
