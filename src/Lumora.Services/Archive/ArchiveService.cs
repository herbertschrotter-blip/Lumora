using System.IO.Compression;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Media;
using Lumora.Services.Operations;
using Lumora.Services.Scanning;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Lumora.Services.Archive;

/// <summary>
/// Produktive Implementierung von <see cref="IArchiveService"/> (ADR-0022).
/// <list type="bullet">
/// <item>ZIP/RAR/7z/TAR: <c>ArchiveFactory</c> (Inhaltsverzeichnis aus den Headern). Entpacken: solide RAR und 7z
/// sequenziell über <c>ExtractAllEntries()</c>, alle anderen Eintrag für Eintrag; ZIP fällt bei einem
/// SharpCompress-Fehler vor dem ersten Eintrag auf System.IO.Compression zurück.</item>
/// <item>TAR-Verwaltungseinträge (PAX-Header, @LongLink, pax_global_header) werden übersprungen.</item>
/// <item>GZ/TGZ/BZ2/XZ: <c>ReaderFactory</c> (Stream, ein Durchlauf; TAR darin wird erkannt).</item>
/// <item>Zip-Slip-Schutz: jeder Zielpfad wird bereinigt (keine Laufwerke, keine „..“, keine ungültigen Zeichen) und muss
/// unterhalb des Zielordners liegen, sonst gilt der Eintrag als Fehler.</item>
/// <item>Runden-System wie V2 <c>Invoke-ArchiveExtraction</c>: Archive, die beim Entpacken entstehen, werden in
/// weiteren Runden (max. <see cref="ArchiveExtractOptions.MaxNestedDepth"/>) neben sich entpackt und danach gelöscht
/// (sie sind Zwischenprodukte dieses Laufs, keine Benutzerdateien).</item>
/// <item>Dateien entstehen als „.lumorapart“ und werden erst fertig umbenannt — eine Ordnerüberwachung sieht keine
/// halben Fotos.</item>
/// </list>
/// Alles läuft auf dem Thread-Pool; Fortschritt kommt pro Datei und etwa je MB.
/// </summary>
public sealed class ArchiveService : IArchiveService
{
    private const string PartSuffix = ".lumorapart";
    private const int CopyBufferSize = 256 * 1024;
    private const long ProgressByteStep = 1024 * 1024;

    private static readonly HashSet<string> ArchiveApiExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar" };

    private readonly IMediaFormatRegistry _formats;
    private readonly ILogger<ArchiveService> _logger;
    private readonly IScanService? _scanner;
    private readonly IFileOperationService? _fileOps;
    private readonly IMessenger _messenger;

    /// <param name="scanner">Optional: Incremental-Scan nach dem Entpacken (null = kein Scan, z. B. in Tests).</param>
    /// <param name="fileOps">Optional: Papierkorb für das Archiv nach dem Entpacken (null = nie).</param>
    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default — für <see cref="ArchiveExtractedMessage"/>.</param>
    public ArchiveService(
        IMediaFormatRegistry formats,
        ILogger<ArchiveService> logger,
        IScanService? scanner = null,
        IFileOperationService? fileOps = null,
        IMessenger? messenger = null)
    {
        _formats = formats;
        _logger = logger;
        _scanner = scanner;
        _fileOps = fileOps;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public bool IsArchive(string? path)
        => _formats.TryClassify(Path.GetExtension(path), out var mediaType) && mediaType == MediaType.Archive;

    public string GetDefaultTargetFolder(string archivePath)
    {
        var directory = Path.GetDirectoryName(archivePath) ?? string.Empty;
        return Path.Combine(directory, GetArchiveBaseName(Path.GetFileName(archivePath)));
    }

    /// <summary>Archivname ohne Endung; „x.tar.gz“/„x.tar.bz2“/„x.tar.xz“ → „x“.</summary>
    public static string GetArchiveBaseName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        if (ext is ".gz" or ".bz2" or ".xz" && baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        return string.IsNullOrWhiteSpace(baseName) ? "archiv" : baseName;
    }

    public Task<ArchiveListing> ListAsync(string archivePath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var entries = ListEntries(archivePath, ct)
                .Where(e => !e.IsDirectory)
                .Select(e => ToInfo(e, archivePath))
                .ToList();
            return new ArchiveListing(archivePath, entries);
        }, ct);

    public async Task<ArchiveExtractResult> ExtractAsync(
        string archivePath,
        ArchiveExtractOptions options,
        IProgress<ArchiveExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archiv nicht gefunden.", archivePath);
        }

        var run = new ExtractRun(archivePath, Path.GetFullPath(options.TargetFolder ?? GetDefaultTargetFolder(archivePath)));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Hauptarchiv (Runde 0) und verschachtelte Runden — synchrone I/O auf dem Thread-Pool.
        await Task.Run(() => ExtractRounds(run, options, progress, ct), CancellationToken.None).ConfigureAwait(false);

        // Vor dem Scan melden, welcher Ordner aus dem Archiv entstand: das automatische Entpacken (ADR-0032) fasst darin
        // gefundene Archive nicht noch einmal an (verschachtelte Archive behandelt allein das Runden-System oben).
        if (Directory.Exists(run.TargetFolder))
        {
            try
            {
                _messenger.Send(new ArchiveExtractedMessage(archivePath, run.TargetFolder));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Empfänger der ArchiveExtractedMessage warf eine Ausnahme");
            }
        }

        // Nachlauf: Papierkorb und Scan nur nach vollständigem, fehlerfreiem Lauf.
        var recycled = false;
        ScanResult? scan = null;
        if (!run.Canceled)
        {
            recycled = await RecycleArchiveAsync(archivePath, options, run, ct).ConfigureAwait(false);
            scan = await ScanLibraryAsync(options, run, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Archiv entpackt: {Archive} → {Target}: {Files} Dateien, {Skipped} übersprungen, {Nested} verschachtelt, {Rounds} Runden, {Bytes} Bytes, {Errors} Fehler, abgebrochen={Canceled}, Papierkorb={Recycled}, Scan={Scan} in {Ms:N0} ms",
            archivePath, run.TargetFolder, run.FilesExtracted, run.FilesSkipped, run.NestedExtracted, run.Rounds, run.BytesExtracted,
            run.Errors.Count, run.Canceled, recycled, scan is null ? "-" : $"{scan.Added} neu", sw.ElapsedMilliseconds);

        return new ArchiveExtractResult(
            archivePath, run.TargetFolder, run.FilesExtracted, run.FilesSkipped, run.NestedExtracted, run.Rounds,
            run.BytesExtracted, run.Errors, run.Canceled, recycled, scan);
    }

    // ---- Runden ---------------------------------------------------------------------------------

    private void ExtractRounds(ExtractRun run, ArchiveExtractOptions options, IProgress<ArchiveExtractProgress>? progress, CancellationToken ct)
    {
        try
        {
            run.Rounds = 1;
            var nested = ExtractOne(run.ArchivePath, run.TargetFolder, round: 0, options, run, progress, ct);

            for (var depth = 1; depth <= options.MaxNestedDepth && nested.Count > 0 && options.ExtractNested; depth++)
            {
                run.Rounds++;
                var next = new List<string>();
                foreach (var nestedPath in nested)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var produced = ExtractOne(nestedPath, GetDefaultTargetFolder(nestedPath), depth, options, run, progress, ct);
                        next.AddRange(produced);
                        run.NestedExtracted++;
                        TryDeleteNested(nestedPath);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        run.Errors.Add(new ArchiveExtractError(Path.GetFileName(nestedPath), $"Verschachteltes Archiv nicht entpackt: {ex.Message}"));
                        _logger.LogWarning(ex, "Verschachteltes Archiv nicht entpackt: {Path}", nestedPath);
                    }
                }

                nested = next;
            }

            if (nested.Count > 0 && options.ExtractNested)
            {
                foreach (var left in nested)
                {
                    run.Errors.Add(new ArchiveExtractError(Path.GetFileName(left), $"Verschachtelungstiefe {options.MaxNestedDepth} erreicht — Archiv bleibt ungeöffnet liegen."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            run.Canceled = true;
            _logger.LogInformation("Entpacken abgebrochen: {Archive}", run.ArchivePath);
        }
    }

    /// <summary>Entpackt ein Archiv in einen Ordner; liefert die dabei entstandenen Archive (für die nächste Runde).</summary>
    private List<string> ExtractOne(
        string archivePath,
        string targetFolder,
        int round,
        ArchiveExtractOptions options,
        ExtractRun run,
        IProgress<ArchiveExtractProgress>? progress,
        CancellationToken ct)
    {
        var archiveName = Path.GetFileName(archivePath);
        var selected = ListEntries(archivePath, ct)
            .Where(e => !e.IsDirectory && IsSelected(e, options))
            .ToList();
        if (selected.Any(e => e.IsEncrypted))
        {
            throw new ArchiveEncryptedException(archivePath);
        }

        var filesTotal = selected.Count;
        var bytesTotal = selected.Sum(e => Math.Max(0, e.Size));
        var filesDone = 0;
        long bytesDone = 0;
        var produced = new List<string>();
        Directory.CreateDirectory(targetFolder);
        progress?.Report(new ArchiveExtractProgress(round, archiveName, 0, filesTotal, 0, bytesTotal, null));

        ForEachEntry(archivePath, ct, (entry, open) =>
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || !IsSelected(entry, options))
            {
                return;
            }

            var destination = ResolveSafePath(targetFolder, entry.Key);
            if (destination is null)
            {
                run.Errors.Add(new ArchiveExtractError(entry.Key, "Unsicherer Pfad im Archiv (außerhalb des Zielordners) — übersprungen."));
                filesDone++;
                return;
            }

            if (File.Exists(destination))
            {
                switch (options.OverwriteMode)
                {
                    case ArchiveOverwriteMode.Skip:
                        run.FilesSkipped++;
                        filesDone++;
                        bytesDone += Math.Max(0, entry.Size);
                        progress?.Report(new ArchiveExtractProgress(round, archiveName, filesDone, filesTotal, bytesDone, bytesTotal, entry.Key));
                        return;
                    case ArchiveOverwriteMode.Rename:
                        destination = NextFreeName(destination);
                        break;
                }
            }

            try
            {
                var written = WriteEntry(open, destination, entry, options.OverwriteMode == ArchiveOverwriteMode.Overwrite, ct, b =>
                {
                    progress?.Report(new ArchiveExtractProgress(round, archiveName, filesDone, filesTotal, bytesDone + b, bytesTotal, entry.Key));
                });
                bytesDone += written;
                run.BytesExtracted += written;
                run.FilesExtracted++;
                if (options.ExtractNested && IsArchive(destination))
                {
                    produced.Add(destination);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                run.Errors.Add(new ArchiveExtractError(entry.Key, ex.Message));
                _logger.LogWarning(ex, "Eintrag nicht entpackt: {Archive} → {Entry}", archivePath, entry.Key);
            }

            filesDone++;
            progress?.Report(new ArchiveExtractProgress(round, archiveName, filesDone, filesTotal, bytesDone, bytesTotal, entry.Key));
        });

        return produced;
    }

    private bool IsSelected(EntrySnapshot entry, ArchiveExtractOptions options)
    {
        if (!options.MediaOnly)
        {
            return true;
        }

        var ext = Path.GetExtension(entry.Key);
        return _formats.IsMedia(ext) || (options.ExtractNested && IsArchive(entry.Key));
    }

    /// <summary>Schreibt einen Eintrag atomar (.lumorapart → Zielname) und liefert die geschriebenen Bytes.</summary>
    private static long WriteEntry(Func<Stream> open, string destination, EntrySnapshot entry, bool overwrite, CancellationToken ct, Action<long> onBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + PartSuffix;
        long written = 0;
        try
        {
            using (var source = open())
            using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                var buffer = new byte[CopyBufferSize];
                long sinceReport = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    written += read;
                    sinceReport += read;
                    if (sinceReport >= ProgressByteStep)
                    {
                        sinceReport = 0;
                        onBytes(written);
                    }
                }
            }

            File.Move(part, destination, overwrite);
            if (entry.ModifiedAtUtc is { } modified)
            {
                try
                {
                    File.SetLastWriteTimeUtc(destination, modified);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Zeitstempel ist Komfort, kein Fehler.
                }
            }

            return written;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private void TryDeleteNested(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Verschachteltes Archiv konnte nach dem Entpacken nicht entfernt werden: {Path}", path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rest bleibt liegen — als .lumorapart erkennbar.
        }
    }

    // ---- Nachlauf: Papierkorb + Scan ------------------------------------------------------------

    private async Task<bool> RecycleArchiveAsync(string archivePath, ArchiveExtractOptions options, ExtractRun run, CancellationToken ct)
    {
        if (!options.RecycleArchiveAfterExtract || options.ArchiveFileId is not { } fileId || _fileOps is null)
        {
            return false;
        }

        if (run.Errors.Count > 0)
        {
            run.Errors.Add(new ArchiveExtractError(Path.GetFileName(archivePath), "Archiv bleibt erhalten, weil beim Entpacken Fehler auftraten."));
            return false;
        }

        try
        {
            using var quiet = Activity.ActivityScope.Suppress(); // Teil des Entpackens, kein eigener Eintrag in der Aktivitäts-Anzeige (ADR-0067)
            var batch = await _fileOps.RecycleAsync([fileId], ct).ConfigureAwait(false);
            if (batch.Succeeded == 1)
            {
                return true;
            }

            var error = batch.Items.FirstOrDefault()?.Error ?? "unbekannter Fehler";
            run.Errors.Add(new ArchiveExtractError(Path.GetFileName(archivePath), $"Archiv nicht in den Papierkorb verschoben: {error}"));
        }
        catch (OperationCanceledException)
        {
            run.Canceled = true;
        }
        catch (Exception ex)
        {
            run.Errors.Add(new ArchiveExtractError(Path.GetFileName(archivePath), $"Archiv nicht in den Papierkorb verschoben: {ex.Message}"));
            _logger.LogWarning(ex, "Archiv nach dem Entpacken nicht in den Papierkorb verschoben: {Path}", archivePath);
        }

        return false;
    }

    private async Task<ScanResult?> ScanLibraryAsync(ArchiveExtractOptions options, ExtractRun run, CancellationToken ct)
    {
        if (!options.ScanAfterExtract || options.LibraryId is not { } libraryId || _scanner is null || run.Canceled)
        {
            return null;
        }

        try
        {
            return await _scanner.ScanAsync(libraryId, ScanMode.Incremental, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            run.Canceled = true;
            return null;
        }
        catch (Exception ex)
        {
            run.Errors.Add(new ArchiveExtractError("Katalog", $"Bibliothek konnte nach dem Entpacken nicht aktualisiert werden: {ex.Message}"));
            _logger.LogWarning(ex, "Incremental-Scan nach dem Entpacken fehlgeschlagen: Library={LibraryId}", libraryId);
            return null;
        }
    }

    // ---- Pfade ----------------------------------------------------------------------------------

    /// <summary>
    /// Zip-Slip-Schutz: relativer Pfad ohne Laufwerk/Wurzel, „..“ ist verboten, ungültige Zeichen werden ersetzt; das
    /// Ergebnis muss unterhalb des Zielordners liegen. null = unsicher.
    /// </summary>
    public static string? ResolveSafePath(string targetFolder, string key)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var segments = new List<string>();
        foreach (var raw in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();
            if (segment == "." || segment.Length == 0)
            {
                continue;
            }

            if (segment == "..")
            {
                return null;
            }

            var chars = segment.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            segments.Add(new string(chars).TrimEnd('.', ' '));
        }

        if (segments.Count == 0 || segments.Any(s => s.Length == 0))
        {
            return null;
        }

        var root = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments.ToArray())));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static string NextFreeName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    // ---- Lesen über SharpCompress / System.IO.Compression ----------------------------------------

    private readonly record struct EntrySnapshot(string Key, long Size, long CompressedSize, DateTime? ModifiedAtUtc, bool IsDirectory, bool IsEncrypted);

    private delegate void EntryVisitor(EntrySnapshot entry, Func<Stream> open);

    private ArchiveEntryInfo ToInfo(EntrySnapshot e, string archivePath)
    {
        var path = e.Key.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            name = GetArchiveBaseName(Path.GetFileName(archivePath));
            path = name;
        }

        var ext = Path.GetExtension(name);
        return new ArchiveEntryInfo(path, name, Math.Max(0, e.Size), Math.Max(0, e.CompressedSize), e.ModifiedAtUtc, e.IsEncrypted, _formats.IsMedia(ext), IsArchive(name));
    }

    private static bool UsesArchiveApi(string path) => ArchiveApiExtensions.Contains(Path.GetExtension(path));

    private static bool IsZip(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    private static ReaderOptions CreateReaderOptions() => new() { LookForHeader = true };

    /// <summary>Inhaltsverzeichnis (inkl. Ordnereinträge) — für Archive aus den Headern, für Stream-Formate ein Durchlauf.</summary>
    private IEnumerable<EntrySnapshot> ListEntries(string archivePath, CancellationToken ct)
    {
        try
        {
            if (UsesArchiveApi(archivePath))
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions());
                return archive.Entries.Select(e => Snapshot(e, archivePath)).ToList();
            }

            using var stream = File.OpenRead(archivePath);
            using var reader = ReaderFactory.OpenReader(stream, CreateReaderOptions());
            var list = new List<EntrySnapshot>();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                list.Add(Snapshot(reader.Entry, archivePath));
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SharpCompress.Common.CryptographicException)
        {
            throw new ArchiveEncryptedException(archivePath);
        }
        catch (Exception ex) when (ex is not ArchiveFormatException and not ArchiveEncryptedException)
        {
            if (IsZip(archivePath))
            {
                var fallback = TryListZipFallback(archivePath, ex);
                if (fallback is not null)
                {
                    return fallback;
                }
            }

            throw new ArchiveFormatException(archivePath, ex.Message, ex);
        }
    }

    /// <summary>Sequenzieller Durchlauf zum Entpacken; <paramref name="visit"/> öffnet den Stream nur für gewählte Einträge.</summary>
    private void ForEachEntry(string archivePath, CancellationToken ct, EntryVisitor outer)
    {
        // Fallback nur, solange noch kein Eintrag verarbeitet wurde — sonst würden Einträge doppelt geschrieben.
        var visited = 0;
        EntryVisitor visit = (entry, open) =>
        {
            visited++;
            outer(entry, open);
        };
        var useZipFallback = false;
        try
        {
            if (UsesArchiveApi(archivePath))
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions());

                // SharpCompress 0.50: ExtractAllEntries() nur für solide Archive und 7z (sonst SharpCompressException);
                // ZIP, TAR und nicht-solide RAR werden Eintrag für Eintrag geöffnet.
                if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
                {
                    using var reader = archive.ExtractAllEntries();
                    VisitReader(reader, archivePath, visit, ct);
                }
                else
                {
                    VisitArchiveEntries(archive, archivePath, visit, ct);
                }

                return;
            }

            using var stream = File.OpenRead(archivePath);
            using var streamReader = ReaderFactory.OpenReader(stream, CreateReaderOptions());
            VisitReader(streamReader, archivePath, visit, ct);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SharpCompress.Common.CryptographicException)
        {
            throw new ArchiveEncryptedException(archivePath);
        }
        catch (Exception ex) when (IsZip(archivePath) && visited == 0 && ex is not ArchiveFormatException and not ArchiveEncryptedException)
        {
            _logger.LogDebug(ex, "SharpCompress konnte {Path} nicht lesen — Fallback System.IO.Compression", archivePath);
            useZipFallback = true;
        }
        catch (Exception ex) when (ex is not ArchiveFormatException and not ArchiveEncryptedException)
        {
            // Beschädigtes Archiv mitten im Lauf: klare Meldung statt Bibliotheks-Exception.
            throw new ArchiveFormatException(archivePath, ex.Message, ex);
        }

        if (useZipFallback)
        {
            VisitZipFallback(archivePath, outer, ct);
        }
    }

    private static void VisitReader(IReader reader, string archivePath, EntryVisitor visit, CancellationToken ct)
    {
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Snapshot(reader.Entry, archivePath);
            try
            {
                visit(snapshot, () => reader.OpenEntryStream());
            }
            catch (SharpCompress.Common.CryptographicException)
            {
                throw new ArchiveEncryptedException(archivePath);
            }
        }
    }

    private static void VisitArchiveEntries(IArchive archive, string archivePath, EntryVisitor visit, CancellationToken ct)
    {
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Snapshot(entry, archivePath);
            try
            {
                visit(snapshot, () => entry.OpenEntryStream());
            }
            catch (SharpCompress.Common.CryptographicException)
            {
                throw new ArchiveEncryptedException(archivePath);
            }
        }
    }

    private static EntrySnapshot Snapshot(SharpCompress.Common.IEntry entry, string? archivePath = null)
    {
        var key = entry.Key ?? (archivePath is null ? string.Empty : GetArchiveBaseName(Path.GetFileName(archivePath)));
        var isDirectory = entry.IsDirectory || key.EndsWith('/') || key.EndsWith('\\') || IsTarMetadata(key);
        return new EntrySnapshot(key, entry.Size, entry.CompressedSize, ToUtc(entry.LastModifiedTime), isDirectory, entry.IsEncrypted);
    }

    /// <summary>
    /// Sicherheitsnetz für TAR-Verwaltungseinträge, falls ein Leser sie als Dateien liefert: PAX-Header
    /// („./PaxHeaders.1234/name“), GNU-„@LongLink“, globaler Header von „git archive“. SharpCompress 0.50 wertet PAX und
    /// LongLink selbst aus (SharpCompress 1.0.0, inzwischen zurückgezogen, tat das nicht). Werden wie Ordner übersprungen.
    /// </summary>
    internal static bool IsTarMetadata(string key)
    {
        if (key.Equals("pax_global_header", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var segment in key.Split('/', '\\'))
        {
            if (segment.StartsWith("PaxHeaders.", StringComparison.Ordinal) || segment.Equals("@LongLink", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime? ToUtc(DateTime? value)
    {
        if (value is not { } time || time.Year < 1980)
        {
            return null;
        }

        return time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => DateTime.SpecifyKind(time, DateTimeKind.Local).ToUniversalTime(),
        };
    }

    private List<EntrySnapshot>? TryListZipFallback(string archivePath, Exception original)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            _logger.LogDebug(original, "SharpCompress konnte {Path} nicht lesen — Inhalt über System.IO.Compression", archivePath);
            return zip.Entries.Select(e => Snapshot(e)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "System.IO.Compression konnte {Path} ebenfalls nicht lesen", archivePath);
            return null;
        }
    }

    private static void VisitZipFallback(string archivePath, EntryVisitor visit, CancellationToken ct)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                visit(Snapshot(entry), entry.Open);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new ArchiveFormatException(archivePath, ex.Message, ex);
        }
    }

    private static EntrySnapshot Snapshot(ZipArchiveEntry entry)
    {
        var key = entry.FullName;
        var isDirectory = key.EndsWith('/') || key.EndsWith('\\');
        return new EntrySnapshot(key, entry.Length, entry.CompressedLength, ToUtc(entry.LastWriteTime.UtcDateTime), isDirectory, entry.IsEncrypted);
    }

    /// <summary>Zähler eines Laufs (nur auf dem entpackenden Thread verändert).</summary>
    private sealed class ExtractRun
    {
        public ExtractRun(string archivePath, string targetFolder)
        {
            ArchivePath = archivePath;
            TargetFolder = targetFolder;
        }

        public string ArchivePath { get; }
        public string TargetFolder { get; }
        public int FilesExtracted { get; set; }
        public int FilesSkipped { get; set; }
        public int NestedExtracted { get; set; }
        public int Rounds { get; set; }
        public long BytesExtracted { get; set; }
        public bool Canceled { get; set; }
        public List<ArchiveExtractError> Errors { get; } = new();
    }
}
