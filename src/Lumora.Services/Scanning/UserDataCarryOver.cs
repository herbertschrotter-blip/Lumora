using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Lumora.Services.Scanning;

/// <summary>
/// Trägt die Benutzerdaten einer Bibliothek über „Neu aufbauen“ (<see cref="ScanMode.ClearAndRescan"/>) hinweg
/// (ADR-0023): Favorit (<c>Files.IsFavorite</c>), Bewertung (<c>Files.Rating</c>), Tag-Zuweisungen
/// (<c>FileTags</c>), Kategorie-Werte (<c>FileCategoryValues</c>, ADR-0027) manuell gesetzte Aufnahmedaten (ADR-0046) und manuell gesetzte bzw. entfernte Orte (ADR-0050). <see cref="Capture"/> sichert sie VOR dem
/// Löschen der alten Zeilen in TEMP-Tabellen, <see cref="Restore"/> schreibt sie NACH dem Bulk-Insert auf die neuen
/// FileIds zurück — mengenbasiert (INSERT…SELECT / UPDATE…FROM, keine Zeilenschleife in C#). Beides läuft in der
/// EINEN Schreibtransaktion, in der <see cref="FullScanCatalogWriter"/> den Katalog ersetzt (ADR-0028).
/// <para>
/// Schlüssel ist dieselbe Identität wie beim Incremental-Scan (ADR-0019): voller Ordnerpfad (<c>Folders.Path</c>,
/// damit auch Wurzel-übergreifend eindeutig, ADR-0020) + Dateiname, ohne Groß-/Kleinschreibung (Unicode, wie
/// <see cref="StringComparer.OrdinalIgnoreCase"/>). Keine Prüfung von Größe/Änderungszeit: eine an Ort und Stelle
/// geänderte Datei behält ihre Daten — wie beim Incremental-Scan, wo sie ihre FileId behält. Dateien, die nach dem
/// Neuscan nicht mehr existieren (auch zuvor „fehlt“-markierte), verlieren ihre Daten. Die Tabellen <c>Tags</c>,
/// <c>Categories</c> und <c>CategoryValues</c> selbst werden nie angefasst.
/// </para>
/// <para>
/// Die TEMP-Tabellen leben nur in der Verbindung des Aufrufers (<c>temp_store=MEMORY</c>, Pooling aus) und werden in
/// dessen Transaktion angelegt: ein Rollback verwirft sie mit, ein Absturz ebenso — dann sind aber auch die alten
/// Zeilen samt Benutzerdaten unverändert, weil die Transaktion nie committet wurde (ADR-0028).
/// </para>
/// </summary>
public sealed partial class UserDataCarryOver
{
    /// <summary>Ergebnis des Zurückschreibens; <see cref="LostFiles"/> = gesicherte Dateien ohne Gegenstück im Neuscan.</summary>
    public readonly record struct RestoreReport(
        int CapturedFiles,
        int RestoredFiles,
        int LostFiles,
        int FavoritesOrRatingsRestored,
        int TagAssignmentsRestored,
        TimeSpan Duration,
        int CategoryValuesRestored = 0,
        int FacesRestored = 0,
        int CaptureDatesRestored = 0,
        int GeoLocationsRestored = 0);

    private const string CaseFoldFunction = "pv_casefold";

    private const string CreateTablesSql = """
        DROP TABLE IF EXISTS temp.pv_carry_files;
        DROP TABLE IF EXISTS temp.pv_carry_tags;
        DROP TABLE IF EXISTS temp.pv_carry_categories;
        DROP TABLE IF EXISTS temp.pv_carry_folders;
        DROP TABLE IF EXISTS temp.pv_carry_map;
        CREATE TEMP TABLE pv_carry_files(
            OldFileId  INTEGER PRIMARY KEY,
            FolderKey  TEXT NOT NULL,
            NameKey    TEXT NOT NULL,
            Name       TEXT NOT NULL,
            IsFavorite INTEGER NOT NULL,
            Rating     INTEGER,
            CapturedAt INTEGER,
            CapturedAtPrecision INTEGER,
            CapturedAtOffset INTEGER,
            ManualDate INTEGER NOT NULL DEFAULT 0,
            Latitude REAL,
            Longitude REAL,
            PlaceId INTEGER,
            ManualGeo INTEGER NOT NULL DEFAULT 0,
            GeoSuggestionState INTEGER NOT NULL DEFAULT 0,
            UNIQUE(FolderKey, NameKey));
        CREATE TEMP TABLE pv_carry_tags(
            OldFileId INTEGER NOT NULL,
            TagId     INTEGER NOT NULL,
            PRIMARY KEY(OldFileId, TagId)) WITHOUT ROWID;
        CREATE TEMP TABLE pv_carry_categories(
            OldFileId  INTEGER NOT NULL,
            CategoryId INTEGER NOT NULL,
            ValueId    INTEGER NOT NULL,
            PRIMARY KEY(OldFileId, CategoryId)) WITHOUT ROWID;
        CREATE TEMP TABLE pv_carry_folders(
            FolderKey TEXT PRIMARY KEY,
            FolderId  INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TEMP TABLE pv_carry_map(
            OldFileId INTEGER PRIMARY KEY,
            NewFileId INTEGER NOT NULL);
        """;

    // Nur Dateien mit Benutzerdaten. Bei kollidierenden Schlüsseln (nur auf case-sensitiven Ordnern möglich) gewinnt
    // die vorhandene vor der „fehlt“-markierten Zeile, sonst die ältere.
    private const string CaptureFilesSql = """
        INSERT OR IGNORE INTO temp.pv_carry_files(OldFileId, FolderKey, NameKey, Name, IsFavorite, Rating, CapturedAt, CapturedAtPrecision, CapturedAtOffset, ManualDate, Latitude, Longitude, PlaceId, ManualGeo, GeoSuggestionState)
        SELECT f.Id, pv_casefold(d.Path), pv_casefold(f.Name), f.Name, f.IsFavorite, f.Rating,
               f.CapturedAt, f.CapturedAtPrecision, f.CapturedAtOffset, CASE WHEN f.CapturedAtSource = 4 THEN 1 ELSE 0 END,
               f.Latitude, f.Longitude, f.PlaceId, CASE WHEN f.GeoSource IN (3, 4, 5) THEN f.GeoSource ELSE 0 END, f.GeoSuggestionState
        FROM Files f JOIN Folders d ON d.Id = f.FolderId
        WHERE f.LibraryId = $lib
          AND (f.IsFavorite <> 0 OR f.Rating IS NOT NULL OR f.CapturedAtSource = 4 OR f.GeoSource IN (3, 4, 5) OR f.GeoSuggestionState <> 0 -- ADR-0062
               OR EXISTS (SELECT 1 FROM FileTags t WHERE t.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM FileCategoryValues v WHERE v.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM FaceScans s WHERE s.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM Faces x WHERE x.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM FileEmbeddings e WHERE e.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM AlbumItems ai WHERE ai.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM FolderLinks fl WHERE fl.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM Albums a WHERE a.CoverFileId = f.Id)
               OR EXISTS (SELECT 1 FROM StoryItems si WHERE si.FileId = f.Id) -- Storys (ADR-0058)
               OR EXISTS (SELECT 1 FROM Stories st WHERE st.CoverFileId = f.Id)
               OR EXISTS (SELECT 1 FROM Folders fo WHERE fo.CoverFileId = f.Id) -- Ordner-Titelbild (ADR-0068)
               OR EXISTS (SELECT 1 FROM FileUsageDays u WHERE u.FileId = f.Id) -- Nutzungs-Signale (ADR-0072)
               OR EXISTS (SELECT 1 FROM PhotoVariants pv WHERE pv.FileId = f.Id OR pv.OriginalFileId = f.Id)) -- Fassungen (ADR-0071)
        ORDER BY f.Missing, f.Id;
        """;

    private const string CaptureTagsSql = """
        INSERT OR IGNORE INTO temp.pv_carry_tags(OldFileId, TagId)
        SELECT t.FileId, t.TagId FROM temp.pv_carry_files c CROSS JOIN FileTags t ON t.FileId = c.OldFileId;
        """;

    private const string CaptureCategoriesSql = """
        INSERT OR IGNORE INTO temp.pv_carry_categories(OldFileId, CategoryId, ValueId)
        SELECT v.FileId, v.CategoryId, v.ValueId FROM temp.pv_carry_files c CROSS JOIN FileCategoryValues v ON v.FileId = c.OldFileId;
        """;

    // Ordner-Schlüssel des Neuscans: eine Funktionsauswertung je Ordner, nicht je Datei.
    private const string NewFoldersSql = """
        INSERT OR IGNORE INTO temp.pv_carry_folders(FolderKey, FolderId)
        SELECT pv_casefold(Path), Id FROM Folders WHERE LibraryId = $lib ORDER BY Id;
        """;

    // Zuordnung alt → neu, getrieben von den gesicherten Zeilen (CROSS JOIN legt die Reihenfolge fest):
    // Ordner per Primärschlüssel, Datei per IX_Files_Folder_Name mit exaktem Namen … Kein Filter auf Files.LibraryId:
    // pv_carry_folders enthält nur Ordner dieser Bibliothek, und der Filter lockte den Planer auf
    // IX_Files_Library_Folder (Ordner-Scan statt Namens-Lookup; 200k Dateien: 7,6 s statt 1,0 s).
    private const string MapExactSql = """
        INSERT OR IGNORE INTO temp.pv_carry_map(OldFileId, NewFileId)
        SELECT c.OldFileId, f.Id
        FROM temp.pv_carry_files c
        CROSS JOIN temp.pv_carry_folders nf ON nf.FolderKey = c.FolderKey
        CROSS JOIN Files f ON f.FolderId = nf.FolderId AND f.Name = c.Name;
        """;

    // … und für den seltenen Rest (Name nur in Groß-/Kleinschreibung geändert) die Dateien desselben Ordners.
    private const string MapCaseInsensitiveSql = """
        INSERT OR IGNORE INTO temp.pv_carry_map(OldFileId, NewFileId)
        SELECT c.OldFileId, f.Id
        FROM temp.pv_carry_files c
        CROSS JOIN temp.pv_carry_folders nf ON nf.FolderKey = c.FolderKey
        CROSS JOIN Files f ON f.FolderId = nf.FolderId
        WHERE c.OldFileId NOT IN (SELECT OldFileId FROM temp.pv_carry_map)
          AND pv_casefold(f.Name) = c.NameKey;
        """;

    private const string RestoreFilesSql = """
        UPDATE Files SET IsFavorite = c.IsFavorite, Rating = c.Rating
        FROM temp.pv_carry_map m CROSS JOIN temp.pv_carry_files c ON c.OldFileId = m.OldFileId
        WHERE Files.Id = m.NewFileId AND (c.IsFavorite <> 0 OR c.Rating IS NOT NULL);
        """;

    // Manuell gesetztes Aufnahmedatum (ADR-0046) bleibt IMMER erhalten; alle übrigen liest das Nachtragen neu.
    private const string RestoreCaptureDatesSql = """
        UPDATE Files SET CapturedAt = c.CapturedAt, CapturedAtSource = 4, CapturedAtPrecision = c.CapturedAtPrecision, CapturedAtOffset = c.CapturedAtOffset
        FROM temp.pv_carry_map m CROSS JOIN temp.pv_carry_files c ON c.OldFileId = m.OldFileId
        WHERE Files.Id = m.NewFileId AND c.ManualDate = 1;
        """;

    // Manuell gesetzter bzw. entfernter Ort (ADR-0050) und übernommene Vorschläge (GeoSource 4/5, ADR-0062) bleiben
    // erhalten — ManualGeo trägt die Herkunft; alle übrigen liest das Nachtragen neu. Abgelehnte Vorschläge ebenso.
    private const string RestoreGeoSql = """
        UPDATE Files SET Latitude = c.Latitude, Longitude = c.Longitude, PlaceId = c.PlaceId, GeoSource = c.ManualGeo
        FROM temp.pv_carry_map m CROSS JOIN temp.pv_carry_files c ON c.OldFileId = m.OldFileId
        WHERE Files.Id = m.NewFileId AND c.ManualGeo <> 0;
        UPDATE Files SET GeoSuggestionState = c.GeoSuggestionState
        FROM temp.pv_carry_map m CROSS JOIN temp.pv_carry_files c ON c.OldFileId = m.OldFileId
        WHERE Files.Id = m.NewFileId AND c.GeoSuggestionState <> 0;
        """;

    // Ein während des Scans gelöschtes Tag fällt still heraus (sonst verletzt die Zuweisung den Fremdschlüssel).
    private const string RestoreTagsSql = """
        INSERT OR IGNORE INTO FileTags(FileId, TagId)
        SELECT m.NewFileId, t.TagId
        FROM temp.pv_carry_tags t CROSS JOIN temp.pv_carry_map m ON m.OldFileId = t.OldFileId
        WHERE EXISTS (SELECT 1 FROM Tags g WHERE g.Id = t.TagId);
        """;

    // Wie bei Tags: ein während des Scans gelöschter Wert (oder seine Kategorie) fällt still heraus.
    private const string RestoreCategoriesSql = """
        INSERT OR IGNORE INTO FileCategoryValues(FileId, CategoryId, ValueId)
        SELECT m.NewFileId, v.CategoryId, v.ValueId
        FROM temp.pv_carry_categories v CROSS JOIN temp.pv_carry_map m ON m.OldFileId = v.OldFileId
        WHERE EXISTS (SELECT 1 FROM CategoryValues cv WHERE cv.Id = v.ValueId AND cv.CategoryId = v.CategoryId);
        """;

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly int _libraryId;
    private bool _restored;

    private UserDataCarryOver(SqliteConnection connection, SqliteTransaction transaction, int libraryId)
    {
        _connection = connection;
        _transaction = transaction;
        _libraryId = libraryId;
    }

    /// <summary>Anzahl gesicherter Dateien (mit Favorit, Bewertung, mindestens einem Tag oder Kategorie-Wert).</summary>
    public int CapturedFiles { get; private set; }

    /// <summary>Anzahl gesicherter Tag-Zuweisungen.</summary>
    public int CapturedTagAssignments { get; private set; }

    /// <summary>Anzahl gesicherter Kategorie-Werte.</summary>
    public int CapturedCategoryValues { get; private set; }

    /// <summary>Dauer der Sicherung.</summary>
    public TimeSpan CaptureDuration { get; private set; }

    /// <summary>
    /// Sichert die Benutzerdaten der Bibliothek in TEMP-Tabellen der Verbindung — innerhalb der offenen
    /// Schreibtransaktion des Aufrufers. Aufrufen, bevor dieselbe Transaktion die alten Zeilen löscht; so sieht die
    /// Sicherung auch Änderungen, die der Benutzer während der Enumeration noch gemacht hat.
    /// </summary>
    public static UserDataCarryOver Capture(SqliteConnection connection, SqliteTransaction transaction, int libraryId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var sw = Stopwatch.StartNew();
        connection.CreateFunction<string, string>(CaseFoldFunction, static s => s.ToUpperInvariant(), isDeterministic: true);
        var carry = new UserDataCarryOver(connection, transaction, libraryId);
        Execute(connection, transaction, CreateTablesSql, libraryId);
        carry.CapturedFiles = Execute(connection, transaction, CaptureFilesSql, libraryId);
        carry.CapturedTagAssignments = Execute(connection, transaction, CaptureTagsSql, libraryId);
        carry.CapturedCategoryValues = Execute(connection, transaction, CaptureCategoriesSql, libraryId);
        carry.ParkFaces(); // Gesichter + FaceScans (ADR-0035, UserDataCarryOver.Faces.cs)
        carry.ParkMotifs(); // Bild-Vektoren + Motiv-Treffer (ADR-0052, UserDataCarryOver.Motifs.cs)
        carry.ParkAlbums(); // Alben, Ordner-Verknüpfungen, Titelbilder (ADR-0054, UserDataCarryOver.Albums.cs)
        carry.ParkStories(); // Story-Einträge und Titelbilder (ADR-0058, UserDataCarryOver.Stories.cs)
        carry.ParkUsage(); // Nutzungs-Signale (ADR-0072, UserDataCarryOver.Usage.cs)
        carry.ParkVariants(); // Fassungen aus „Foto verbessern“ (ADR-0071, UserDataCarryOver.Variants.cs)
        carry.CaptureDuration = sw.Elapsed;
        return carry;
    }

    /// <summary>
    /// Schreibt die gesicherten Daten auf die neuen FileIds zurück — in derselben Transaktion wie <see cref="Capture"/>.
    /// Aufrufen, nachdem der Neuscan seine Files-Zeilen geschrieben hat. Darf nur einmal laufen. FTS-Spalten
    /// (Name, Extension) werden nicht berührt.
    /// </summary>
    public RestoreReport Restore()
    {
        if (_restored)
        {
            throw new InvalidOperationException("Benutzerdaten wurden bereits zurückgeschrieben.");
        }

        _restored = true;
        var sw = Stopwatch.StartNew();
        RemapFileLinkUndo(); // gesicherte Ordner-Verknüpfungen entfernter Dateien auf neue Ordner-Ids (ADR-0054)
        if (CapturedFiles == 0)
        {
            return new RestoreReport(0, 0, 0, 0, 0, sw.Elapsed);
        }

        var tx = _transaction;
        Execute(_connection, tx, NewFoldersSql, _libraryId);
        var mapped = Execute(_connection, tx, MapExactSql, _libraryId);
        mapped += Execute(_connection, tx, MapCaseInsensitiveSql, _libraryId);
        var files = Execute(_connection, tx, RestoreFilesSql, _libraryId);
        var captureDates = Execute(_connection, tx, RestoreCaptureDatesSql, _libraryId); // ADR-0046
        var geo = Execute(_connection, tx, RestoreGeoSql, _libraryId); // ADR-0050
        var tags = Execute(_connection, tx, RestoreTagsSql, _libraryId);
        var categories = Execute(_connection, tx, RestoreCategoriesSql, _libraryId);
        var faces = RestoreFaces(); // ADR-0035
        RestoreMotifs(); // ADR-0052
        RestoreAlbums(); // ADR-0054
        RestoreStories(); // ADR-0058
        RestoreUsage(); // ADR-0072
        RestoreVariants(); // ADR-0071

        return new RestoreReport(CapturedFiles, mapped, CapturedFiles - mapped, files, tags, sw.Elapsed, categories, faces, captureDates, geo);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, int libraryId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (sql.Contains("$lib", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("$lib", libraryId);
        }

        return cmd.ExecuteNonQuery();
    }
}
