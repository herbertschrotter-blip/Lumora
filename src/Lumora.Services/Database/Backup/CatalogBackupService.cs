using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Infrastructure;

namespace Lumora.Services.Database.Backup;

/// <summary>Anlass einer Katalog-Sicherung (ADR-0069); bestimmt Dateinamen-Anhang und Aufbewahrung.</summary>
public enum CatalogBackupKind
{
    /// <summary>Automatisch, bevor EF-Migrationen einen bestehenden Katalog ändern.</summary>
    BeforeMigration,

    /// <summary>Von Hand: Einstellungen › Speicher &amp; Wartung › „Katalog jetzt sichern“.</summary>
    Manual,

    /// <summary>Der Katalog, der beim Wiederherstellen ersetzt wurde.</summary>
    BeforeRestore,
}

/// <summary>Eine Sicherungsdatei im Sicherungsordner.</summary>
public sealed record CatalogBackupInfo(string FilePath, CatalogBackupKind Kind, string Version, DateTime CreatedLocal, long SizeBytes)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>Sicherung oder Wiederherstellung ging nicht — der Katalog bleibt unverändert; Text für den Benutzer.</summary>
public sealed class CatalogBackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Einstellungen der Katalog-Sicherung (ADR-0069).</summary>
/// <param name="AppVersion">Lumora-Version im Dateinamen; null = Version der gestarteten App.</param>
/// <param name="KeepBeforeMigration">So viele automatische Sicherungen vor Migrationen bleiben, ältere werden gelöscht.</param>
/// <param name="KeepManual">So viele Sicherungen von Hand bleiben.</param>
/// <param name="KeepBeforeRestore">So viele „vor Wiederherstellung“ bleiben.</param>
public sealed record CatalogBackupOptions(
    string? AppVersion = null,
    int KeepBeforeMigration = 5,
    int KeepManual = 10,
    int KeepBeforeRestore = 3);

/// <summary>
/// Konsistente Kopien der Katalog-DB (ADR-0069) über die SQLite-Online-Backup-API — auch bei offenem WAL und
/// gleichzeitigen Lesern/Schreibern ein in sich stimmiger Stand, nie eine Dateikopie. Sicherungen liegen im Ordner
/// <c>backups</c> der App-Wurzel der Sitzung: normal <c>&lt;Datenordner&gt;\backups</c>, in einer versteckten Bibliothek
/// <c>&lt;Bibliotheksordner&gt;\.lumora\backups</c> im Container — nie außerhalb (ADR-0049). Vorschaubilder werden nicht
/// gesichert (ableitbar). Wiederherstellen wird nur vorgemerkt und beim nächsten Start der Sitzung ausgeführt, bevor
/// irgendetwas die DB öffnet.
/// </summary>
public interface ICatalogBackupService
{
    /// <summary>Ordner der Sicherungen (wird bei Bedarf angelegt).</summary>
    string BackupFolder { get; }

    /// <summary>Legt eine Sicherung an, prüft sie (lesbar) und räumt ältere desselben Anlasses ab.</summary>
    /// <exception cref="CatalogBackupException">Sicherung nicht möglich (kein Platz, Datei gesperrt …).</exception>
    Task<CatalogBackupInfo> CreateBackupAsync(CatalogBackupKind kind, CancellationToken ct = default);

    /// <summary>Alle Sicherungen, neueste zuerst.</summary>
    IReadOnlyList<CatalogBackupInfo> List();

    /// <summary>Vorgemerkte Wiederherstellung, null wenn keine.</summary>
    CatalogBackupInfo? ScheduledRestore { get; }

    /// <summary>Merkt die Sicherung für den nächsten Start vor (sie muss im Sicherungsordner liegen).</summary>
    /// <exception cref="CatalogBackupException">Sicherung fehlt oder stammt von einer neueren Lumora (unbekannte Migrationen).</exception>
    void ScheduleRestore(CatalogBackupInfo backup);

    void CancelScheduledRestore();

    /// <summary>
    /// Beim Start, bevor die DB geöffnet wird: führt eine vorgemerkte Wiederherstellung aus (aktuelle DB →
    /// „…-vor-wiederherstellung.db“, Sicherung → <c>library.db</c>, Vorschaubilder verworfen). null, wenn nichts vorgemerkt
    /// war oder die Sicherung nicht verwendbar ist (fehlt, beschädigt, neuere Version) — dann ist die Vormerkung gelöscht,
    /// der Katalog unverändert und <see cref="LastRestoreProblem"/> nennt den Grund.
    /// </summary>
    /// <exception cref="CatalogBackupException">Austausch ging nicht (Datei gesperrt …) — die Vormerkung bleibt.</exception>
    CatalogBackupInfo? ApplyScheduledRestore();

    /// <summary>Warum die letzte vorgemerkte Wiederherstellung beim Start nicht ausgeführt wurde; null = kein Problem.</summary>
    string? LastRestoreProblem { get; }
}

public sealed partial class CatalogBackupService : ICatalogBackupService
{
    public const string FolderName = "backups";
    public const string RestoreRequestFileName = "restore-request.json";
    private const string ManualSuffix = "manuell";
    private const string BeforeRestoreSuffix = "vor-wiederherstellung";
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>Rückgängig-Ablage unter <c>thumbs\{libraryId}\</c> (ADR-0065 Nachtrag) — beim Verwerfen der Vorschaubilder behalten.</summary>
    private const string UndoStashFolderName = "_undo";

    private readonly IAppDataPaths _paths;
    private readonly CatalogBackupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILibraryContextFactory? _factory;

    /// <param name="factory">Katalog der Sitzung: Umzugssperre (ADR-0026) und bekannte Migrationen; null = ohne beide Prüfungen (Tests).</param>
    public CatalogBackupService(
        IAppDataPaths paths,
        ILogger<CatalogBackupService>? logger = null,
        CatalogBackupOptions? options = null,
        TimeProvider? time = null,
        ILibraryContextFactory? factory = null)
    {
        _paths = paths;
        _options = options ?? new CatalogBackupOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CatalogBackupService>.Instance;
        _factory = factory;
        BackupFolder = Path.Combine(paths.AppDataRoot, FolderName);
    }

    public string BackupFolder { get; }

    public string? LastRestoreProblem { get; private set; }

    private string Version => _options.AppVersion ?? CurrentAppVersion();

    private string RestoreRequestPath => Path.Combine(BackupFolder, RestoreRequestFileName);

    public async Task<CatalogBackupInfo> CreateBackupAsync(CatalogBackupKind kind, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CreateBackupCore(kind), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private CatalogBackupInfo CreateBackupCore(CatalogBackupKind kind)
    {
        var source = _paths.LibraryDatabasePath;
        if (!File.Exists(source))
        {
            throw new CatalogBackupException("Es gibt noch keinen Katalog, der gesichert werden könnte.");
        }

        if ((_factory as IDatabaseAccessGate)?.IsSuspended == true)
        {
            throw new CatalogBackupException("Der Katalog ist gerade gesperrt (Speicherort wird umgezogen) — bitte danach sichern.");
        }

        var target = Path.Combine(BackupFolder, BuildFileName(kind, Version, _time.GetLocalNow().DateTime));
        var temp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(BackupFolder);
            EnsureFreeSpace(source);
            CopyOnline(source, temp); // Online-Backup-API: ein Schritt, stimmiger Stand inkl. WAL-Inhalt

            VerifyReadable(temp, full: false);
            File.Move(temp, target);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new CatalogBackupException($"Die Katalog-Sicherung konnte nicht angelegt werden: {ex.Message}", ex);
        }

        var info = Describe(target) ?? throw new CatalogBackupException("Die Sicherung wurde angelegt, ist aber nicht auffindbar.");
        _logger.LogInformation(
            "Katalog gesichert ({Kind}): {File}, {Size} Bytes", kind, info.FileName, info.SizeBytes);
        Prune(kind);
        return info;
    }

    public IReadOnlyList<CatalogBackupInfo> List()
    {
        if (!Directory.Exists(BackupFolder))
        {
            return [];
        }

        return Directory.EnumerateFiles(BackupFolder, "library-*.db")
            .Select(Describe)
            .OfType<CatalogBackupInfo>()
            .OrderByDescending(b => b.CreatedLocal)
            .ThenByDescending(b => b.FileName, StringComparer.Ordinal)
            .ToList();
    }

    public CatalogBackupInfo? ScheduledRestore
    {
        get
        {
            var name = ReadRestoreRequest();
            return name is null ? null : Describe(Path.Combine(BackupFolder, name));
        }
    }

    public void ScheduleRestore(CatalogBackupInfo backup)
    {
        var path = Path.Combine(BackupFolder, backup.FileName);
        if (!File.Exists(path) || Describe(path) is null)
        {
            throw new CatalogBackupException("Die gewählte Sicherung liegt nicht (mehr) im Sicherungsordner.");
        }

        if (NewerSchemaProblem(path, backup) is { } problem)
        {
            throw new CatalogBackupException(problem);
        }

        Directory.CreateDirectory(BackupFolder);
        var json = JsonSerializer.Serialize(new RestoreRequest(backup.FileName, _time.GetUtcNow().UtcDateTime));
        File.WriteAllText(RestoreRequestPath, json);
        _logger.LogInformation("Wiederherstellung beim nächsten Start vorgemerkt: {File}", backup.FileName);
    }

    public void CancelScheduledRestore()
    {
        TryDelete(RestoreRequestPath);
    }

    public CatalogBackupInfo? ApplyScheduledRestore()
    {
        if (!File.Exists(RestoreRequestPath))
        {
            return null;
        }

        var name = ReadRestoreRequest();
        var backupPath = name is null ? null : Path.Combine(BackupFolder, name);
        if (backupPath is null || Describe(backupPath) is not { } backup || !File.Exists(backupPath))
        {
            return SkipRestore("Die zum Wiederherstellen vorgemerkte Sicherung wurde nicht gefunden.", null);
        }

        try
        {
            VerifyReadable(backupPath, full: true);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            return SkipRestore($"Die vorgemerkte Sicherung „{backup.FileName}“ ist beschädigt ({ex.Message}).", ex);
        }

        if (NewerSchemaProblem(backupPath, backup) is { } problem)
        {
            return SkipRestore(problem, null);
        }

        var db = _paths.LibraryDatabasePath;
        try
        {
            if (File.Exists(db))
            {
                KeepCurrentBeforeRestore(db);
            }

            TryDelete(db + "-wal", throwOnError: true);
            TryDelete(db + "-shm", throwOnError: true);
            var temp = db + ".restore.tmp";
            File.Copy(backupPath, temp, overwrite: true);
            File.Move(temp, db, overwrite: true);
            SetWalMode(db);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // Vormerkung bleibt: meist läuft noch eine andere Lumora mit diesem Katalog
            throw new CatalogBackupException(
                $"Der Katalog konnte nicht wiederhergestellt werden: {ex.Message}\n\nLäuft Lumora noch ein zweites Mal? " +
                "Die Wiederherstellung bleibt für den nächsten Start vorgemerkt.", ex);
        }

        CancelScheduledRestore();
        DiscardThumbnails(db);
        _logger.LogInformation("Katalog aus Sicherung {File} (Version {Version}) wiederhergestellt", backup.FileName, backup.Version);
        Prune(CatalogBackupKind.BeforeRestore);
        return backup;
    }

    /// <summary>Sicherung unbrauchbar: Vormerkung löschen, Grund merken, mit dem unveränderten Katalog weiterstarten.</summary>
    private CatalogBackupInfo? SkipRestore(string reason, Exception? ex)
    {
        CancelScheduledRestore();
        LastRestoreProblem = $"{reason} Der bisherige Katalog ist unverändert in Gebrauch; die Vormerkung ist gelöscht.";
        _logger.LogWarning(ex, "Wiederherstellung nicht ausgeführt: {Reason}", reason);
        return null;
    }

    /// <summary>
    /// Fehlermeldung, wenn die Sicherung Migrationen enthält, die diese Lumora nicht kennt (von einer neueren Version) —
    /// eine ältere Version arbeitete sonst auf einem Schema, das sie nicht versteht. null = passt oder nicht prüfbar.
    /// </summary>
    private string? NewerSchemaProblem(string backupPath, CatalogBackupInfo backup)
    {
        if (_factory is null)
        {
            return null;
        }

        try
        {
            HashSet<string> known;
            using (var ctx = _factory.Create())
            {
                known = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetMigrations(ctx.Database).ToHashSet(StringComparer.Ordinal);
            }

            using var connection = Open(backupPath, SqliteOpenMode.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            using var reader = command.ExecuteReader();
            var unknown = 0;
            while (reader.Read())
            {
                if (!known.Contains(reader.GetString(0)))
                {
                    unknown++;
                }
            }

            return unknown == 0
                ? null
                : $"Die Sicherung „{backup.FileName}“ stammt von einer neueren Lumora-Version ({backup.Version}, {unknown} unbekannte " +
                  $"Katalog-Änderung(en)) und lässt sich mit Version {Version} nicht wiederherstellen.";
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Migrationen der Sicherung nicht prüfbar");
            return null;
        }
    }

    /// <summary>
    /// Vorschaubilder gehören zum ersetzten Katalog: FileIds, die nach der Sicherung vergeben wurden, vergibt der
    /// wiederhergestellte Katalog neu (sqlite_sequence) — alte Cache-Dateien (Vorschaubilder, Gesichts-Ausschnitte unter
    /// <c>thumbs\</c>) zeigten dann fremde Fotos. Deshalb Thumbnails-Zeilen leeren und den Ordner beiseitelegen (Löschen im
    /// Hintergrund); Lumora erzeugt sie bei Bedarf neu.
    /// </summary>
    private void DiscardThumbnails(string db)
    {
        try
        {
            using (var connection = Open(db, SqliteOpenMode.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM Thumbnails";
                command.ExecuteNonQuery();
            }

            var thumbs = _paths.ThumbnailsRoot;
            if (Directory.Exists(thumbs))
            {
                var old = $"{thumbs}.alt-{_time.GetUtcNow().UtcDateTime:yyyyMMddHHmmssfff}";
                Directory.Move(thumbs, old);
                Directory.CreateDirectory(thumbs);
                KeepUndoStashes(old, thumbs);
                _ = Task.Run(() =>
                {
                    try
                    {
                        Directory.Delete(old, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Alte Vorschaubilder nach dem Wiederherstellen nicht gelöscht: {Folder}", Path.GetFileName(old));
                    }
                });
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Vorschaubilder nach dem Wiederherstellen nicht verworfen");
        }
    }

    /// <summary>
    /// Die Rückgängig-Ablage des Katalog-Entfernens (<c>thumbs\{libraryId}\_undo\</c>, ADR-0065 Nachtrag) ist kein Cache — sie
    /// wandert in den neuen <c>thumbs</c>-Ordner zurück statt mit den Vorschaubildern gelöscht zu werden.
    /// </summary>
    private void KeepUndoStashes(string oldThumbs, string thumbs)
    {
        foreach (var library in Directory.EnumerateDirectories(oldThumbs))
        {
            var stash = Path.Combine(library, UndoStashFolderName);
            if (!Directory.Exists(stash))
            {
                continue;
            }

            try
            {
                var target = Path.Combine(thumbs, Path.GetFileName(library));
                Directory.CreateDirectory(target);
                Directory.Move(stash, Path.Combine(target, UndoStashFolderName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Rückgängig-Ablage der Bibliothek {Library} nicht übernommen", Path.GetFileName(library));
            }
        }
    }

    /// <summary>Aktuellen Katalog als „…-vor-wiederherstellung.db“ behalten — per Backup-API (nimmt den WAL mit), sonst verschieben.</summary>
    private void KeepCurrentBeforeRestore(string db)
    {
        Directory.CreateDirectory(BackupFolder);
        var target = Path.Combine(BackupFolder, BuildFileName(CatalogBackupKind.BeforeRestore, Version, _time.GetLocalNow().DateTime));
        var temp = target + ".tmp";
        try
        {
            CopyOnline(db, temp);

            File.Move(temp, target);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is not (11 or 26))
        {
            TryDelete(temp);
            throw;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            // Beschädigter Katalog lässt sich nicht lesen → Dateien unverändert daneben legen (nichts geht verloren).
            // Andere Fehler (voll, gesperrt) brechen die Wiederherstellung ab, die Vormerkung bleibt.
            TryDelete(temp);
            _logger.LogWarning(ex, "Aktueller Katalog nicht lesbar — wird unverändert als {File} behalten", Path.GetFileName(target));
            File.Move(db, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(db + suffix))
                {
                    File.Move(db + suffix, target + suffix);
                }
            }
        }
    }

    private void Prune(CatalogBackupKind kind)
    {
        var keep = kind switch
        {
            CatalogBackupKind.BeforeMigration => _options.KeepBeforeMigration,
            CatalogBackupKind.Manual => _options.KeepManual,
            _ => _options.KeepBeforeRestore,
        };
        var scheduled = ReadRestoreRequest();
        IReadOnlyList<CatalogBackupInfo> all;
        try
        {
            all = List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Sicherungsordner nicht lesbar — ältere Sicherungen bleiben liegen");
            return;
        }

        foreach (var old in all.Where(b => b.Kind == kind).Skip(Math.Max(1, keep)))
        {
            if (string.Equals(old.FileName, scheduled, StringComparison.OrdinalIgnoreCase))
            {
                continue; // die vorgemerkte Sicherung nie wegräumen
            }

            try
            {
                File.Delete(old.FilePath);
                TryDelete(old.FilePath + "-wal");
                TryDelete(old.FilePath + "-shm");
                _logger.LogInformation("Alte Katalog-Sicherung gelöscht: {File}", old.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Alte Katalog-Sicherung ließ sich nicht löschen: {File}", old.FileName);
            }
        }
    }

    private void EnsureFreeSpace(string source)
    {
        try
        {
            var needed = new FileInfo(source).Length;
            var wal = source + "-wal";
            if (File.Exists(wal))
            {
                needed += new FileInfo(wal).Length;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(BackupFolder));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed + 64L * 1024 * 1024)
            {
                throw new CatalogBackupException(
                    $"Zu wenig freier Speicher für die Katalog-Sicherung: {needed / (1024 * 1024)} MB nötig, {free / (1024 * 1024)} MB frei.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Freier Speicher für die Sicherung nicht ermittelbar — versuche es trotzdem");
        }
    }

    /// <summary>Öffnet die Sicherung und liest die Migrations-Tabelle; <paramref name="full"/> zusätzlich <c>PRAGMA quick_check</c>.</summary>
    private static void VerifyReadable(string path, bool full)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
            command.ExecuteScalar();
        }

        if (full)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = check.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(result ?? "Prüfung ohne Ergebnis");
            }
        }
    }

    /// <summary>
    /// Kopiert <paramref name="source"/> per SQLite-Online-Backup-API nach <paramref name="target"/> und stellt die Kopie auf
    /// <c>journal_mode=DELETE</c> — eine einzelne, in sich vollständige Datei ohne -wal/-shm (Lumora stellt beim Öffnen
    /// wieder auf WAL). Die Quelle wird lesend-schreibend geöffnet, weil ein WAL-Katalog sonst ohne -shm nicht lesbar wäre;
    /// geschrieben wird in ihr nichts.
    /// </summary>
    private static void CopyOnline(string source, string target)
    {
        using var from = Open(source, SqliteOpenMode.ReadWrite);
        using var to = Open(target, SqliteOpenMode.ReadWriteCreate);
        from.BackupDatabase(to);
        using var command = to.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        command.ExecuteScalar();
    }

    /// <summary>
    /// Wiederhergestellten Katalog zurück auf WAL stellen: EF prüft die Existenz der DB über eine schreibgeschützte
    /// Verbindung, auf der Lumoras <c>PRAGMA journal_mode=WAL</c> sonst mit „readonly database“ scheiterte.
    /// </summary>
    private static void SetWalMode(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteScalar();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false, // keine offenen Handles nach Dispose (Dateien werden danach verschoben/gelöscht)
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        return connection;
    }

    private string? ReadRestoreRequest()
    {
        try
        {
            if (!File.Exists(RestoreRequestPath))
            {
                return null;
            }

            var request = JsonSerializer.Deserialize<RestoreRequest>(File.ReadAllText(RestoreRequestPath));
            // nur ein Dateiname im Sicherungsordner, nie ein Pfad
            var name = request?.File is { } file ? Path.GetFileName(file) : null;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Vormerkung der Wiederherstellung unlesbar");
            return null;
        }
    }

    /// <summary>„library-0.4.40-20261025-143000-123[-manuell|-vor-wiederherstellung].db“.</summary>
    public static string BuildFileName(CatalogBackupKind kind, string version, DateTime createdLocal)
    {
        var suffix = kind switch
        {
            CatalogBackupKind.Manual => "-" + ManualSuffix,
            CatalogBackupKind.BeforeRestore => "-" + BeforeRestoreSuffix,
            _ => string.Empty,
        };
        return $"library-{version}-{createdLocal.ToString(TimestampFormat, CultureInfo.InvariantCulture)}{suffix}.db";
    }

    /// <summary>Liest Anlass, Version und Zeitpunkt aus dem Dateinamen; null bei fremden Dateien.</summary>
    public static CatalogBackupInfo? Describe(string path)
    {
        var match = FileNameRegex().Match(Path.GetFileName(path));
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
        {
            return null;
        }

        var kind = match.Groups["kind"].Value.ToLowerInvariant() switch
        {
            ManualSuffix => CatalogBackupKind.Manual,
            BeforeRestoreSuffix => CatalogBackupKind.BeforeRestore,
            _ => CatalogBackupKind.BeforeMigration,
        };
        long size = 0;
        try
        {
            size = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
        }

        return new CatalogBackupInfo(path, kind, match.Groups["ver"].Value, created, size);
    }

    /// <summary>Version „x.y.z“ der gestarteten App (InformationalVersion ohne „+Commit“).</summary>
    public static string CurrentAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CatalogBackupService).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3)
                   ?? "0.0.0";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var clean = (plus >= 0 ? text[..plus] : text).Trim();
        return VersionRegex().IsMatch(clean) ? clean : "0.0.0";
    }

    private void TryDelete(string path, bool throwOnError = false)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (!throwOnError && ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Datei ließ sich nicht löschen: {File}", Path.GetFileName(path));
        }
    }

    private sealed record RestoreRequest(string File, DateTime RequestedUtc);

    [GeneratedRegex(@"^library-(?<ver>\d+\.\d+\.\d+)-(?<ts>\d{8}-\d{6}-\d{3})(?:-(?<kind>manuell|vor-wiederherstellung))?\.db$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();
}
