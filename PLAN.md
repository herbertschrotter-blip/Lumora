# Lumora (vormals PhotoViewer V3) — Implementation-Plan (C# Port)

> **Stand:** 2026-10-19 (Release 0.2.0)
> **Quelle:** Konsolidiert aus 3 ChatGPT-Review-Runden mit GPT-5 (siehe `chatgpt-review/`)
> **Status:** ✅ V0.1 und V0.2 umgesetzt, Teile von V0.3/V0.4 vorgezogen (File Sorter, Flatten & Move, Rating,
> Formatregister) — Stand je Punkt in Kap. 6 und 7 (✅ umgesetzt · 🟡 teilweise · ⬜ offen); Änderungen je Version in
> [CHANGELOG.md](CHANGELOG.md), Entscheidungen seit dem Plan in [ADR-0016 … 0019](docs/adr/README.md)

---

## Inhaltsverzeichnis

1. [Vision & Ausgangslage](#1-vision--ausgangslage)
2. [Stack-Entscheidungen](#2-stack-entscheidungen)
3. [Solution-Layout](#3-solution-layout)
4. [Datenbank-Schema](#4-datenbank-schema)
5. [15 finale ADRs](#5-15-finale-adrs)
6. [V0.1-Scope](#6-v01-scope)
7. [Phasen-Roadmap](#7-phasen-roadmap)
8. [6 Pflicht-Spikes](#8-6-pflicht-spikes)
9. [Tag-1–3-Implementation-Plan](#9-tag-13-implementation-plan)
10. [NuGet-Pakete](#10-nuget-pakete)
11. [Storage-Profile](#11-storage-profile)
12. [Offline-vs-Missing-Guard](#12-offline-vs-missing-guard)
13. [Thumbnail-Pipeline](#13-thumbnail-pipeline)
14. [Migration-Disziplin](#14-migration-disziplin)
15. [Risiken & Mitigation](#15-risiken--mitigation)
16. [UI/UX](#16-uiux)

---

## 1. Vision & Ausgangslage

### Was ist das Projekt
Native Windows-Desktop-Anwendung zum Ansehen, Sortieren und Verschieben von Foto-/Video-Bibliotheken (50.000–500.000 Dateien). Ablösung der bestehenden PowerShell-Web-App (HTTP-Server-Notlösung) durch eine richtige Desktop-App.

### Constraints
| | |
|---|---|
| **Plattform** | Windows-only |
| **User** | Single-User, lokal, keine Auth |
| **Bibliotheks-Größe** | 50.000–500.000 Dateien |
| **Storage** | gemischt — wandert zwischen lokaler SSD, externer HDD, NAS |
| **Formate** | Formatregister `IMediaFormatRegistry` ([ADR-0016](docs/adr/0016-formatregister.md)): gängige Foto-Formate (JPG, PNG, GIF, BMP, TIFF, WebP, HEIC/HEIF, AVIF, Kamera-RAW) und Video-Container (MP4, MOV, MKV, AVI, WMV, MTS, …); WIC + Windows-Codec-Pakete, FFmpeg als Fallback — ursprünglich nur JPG / PNG / MP4 |
| **Plan-Apply-Modus** | nur Move im MVP, Copy später |
| **Distribution** | Portable Ordner via `dotnet publish` — seit 0.2.26 `build/publish.ps1`: self-contained `win-x64`, Ordner + ZIP unter `artifacts/`, kein Single-File, kein Trimming; FFmpeg nicht im Paket (PATH/winget), Daten unter `%LocalAppData%\Lumora` |
| **V0.1-Aufwand** | 8–10 Wochen, robust release-fähig |

### Hauptfeatures
- Thumbnail-Grid mit Lazy Loading + Virtualisierung + Paging
- Lightbox-Viewer (Bilder + Videos via LibVLCSharp)
- Foto-Zoom + Pfeiltasten- und Button-Navigation
- Datei-Operationen: Verschieben / Kopieren / Umbenennen / Löschen → Papierkorb
- **Plan-Ordner** (virtuelle Sortier-Schablone, später auf Dateisystem anwendbar)
- Tags + Favoriten + Bewertungen
- Suche via SQLite FTS5
- Filter (Bilder / Videos / Favoriten)
- **Token-basierter File-Sorter** (Phase 8) — Generator für Plan-Ordner
- Auto-Extract `.zip`/`.rar`/`.7z` (Phase 8)
- Animierte Multi-Frame Video-Hover-Thumbs (Phase 8, on-demand)
- Gesichtserkennung offline (Phase 9)
- Orte: Weltkarte mit Foto-Gruppen, die beim Zoomen feiner werden, Verorten per Ziehen (Welle 10, ADR-0051)

---

## 2. Stack-Entscheidungen

| Komponente | Wahl | Begründung |
|---|---|---|
| **Runtime** | .NET 10 (LTS) | Microsoft, LTS bis 2028, C# 14 |
| **UI-Framework** | WPF | Mature, Windows-nativ, ideal für Media + komplexes Grid |
| **Design-System** | WPF-UI (lepoco) v4.* | Fluent / WinUI 3 Look, Mica, Segoe Fluent Icons |
| **MVVM** | CommunityToolkit.Mvvm 8.4.2 | Source-Generators, Microsoft-offiziell |
| **DI** | Microsoft.Extensions.DependencyInjection | Microsoft-Standard |
| **Hosting** | Microsoft.Extensions.Hosting (HostBuilder) | IHostedService für Background-Worker |
| **Datenbank** | SQLite | Most-deployed DB der Welt |
| **ORM** | Entity Framework Core 10 | für CRUD + Migrations |
| **Raw SQL** | Microsoft.Data.Sqlite 10 | für Bulk-Insert + FTS5 + Performance-Pfade |
| **Video-Player** | LibVLCSharp 3.9.7.1 + VideoLAN.LibVLC.Windows 3.* | spielt alles, eigene Codecs |
| **Bild-Thumbs** | WIC (`BitmapDecoder`) | Windows-Built-in, kleinerer Footprint, Lizenz-frei |
| **FFmpeg-Aufruf** | **eigener `ProcessStartInfo`-Wrapper** | volle Kontrolle über CancelAfter/Kill (statt FFMpegCore) |
| **Logging** | Serilog 9.* + Sinks.File 6.* | Rolling File-Logs |
| **Tests** | xUnit 2.* + FluentAssertions 8.* | Standard |
| **Papierkorb** | Microsoft.VisualBasic.FileIO | `FileSystem.DeleteFile` mit `RecycleOption` |
| **Archive (Phase 8)** | SharpCompress | Pure C#, ZIP/RAR/7z |
| **Animierte GIFs (Phase 8)** | WpfAnimatedGif | etabliert |
| **Face Recognition (Phase 9)** | Microsoft.ML.OnnxRuntime + InsightFace | offline, kein FaceONNX-Wrapper |

---

## 3. Solution-Layout

**Multi-Project ab Tag 1** (nicht Single-Project). Projekte, Ordner und Namespaces heißen seit 0.4.3 `Lumora.*`, vorher
`PhotoViewerV3.*` ([ADR-0056](docs/adr/0056-technische-namen-lumora.md)). Reales Layout seit 0.2.0 — Module statt technischer Ordner
(`Views/`, `ViewModels/`), jedes Modul mit eigener DI-Registrierung `AddXyz()` ([ADR-0017](docs/adr/0017-modul-entkopplung-messenger.md)):

```
Lumora.slnx                       # Solution (.slnx), Directory.Packages.props = Central Package Management
build/                                   # publish.ps1 (portable Version → artifacts/), run-smoke.ps1 (Rauchtest gegen eine EXE)
src/
  Lumora.Domain/                  # net10.0 — keine externen Deps
    Entities/                         # Library, MediaFile, Folder, Thumbnail, OperationBatch, FileOperation, Setting
    Enums/                            # MediaType, ThumbnailStatus/Kind, BatchStatus, FileOperationStatus, OperationType
  Lumora.Data/                    # → Domain
    LibraryContext.cs                 # DbContext (+ DesignTimeLibraryContextFactory)
    EntitiesMapping/                  # EF Core Configurations, ValueConverters
    RawSql/                           # BulkScanWriter, FtsIndexer, FtsQueryBuilder
    Migrations/                       # EF Core (InitialV01)
  Lumora.Services/                # → Domain + Data, WPF-frei; AddLumoraServices()
    Catalog/                          # CatalogUpdateService (Favorit/Bewertung)
    Database/                         # LibraryContextFactory (+ IDatabaseAccessGate), DatabaseInitializer, SqlitePragmaInterceptor
    Duplicates/                       # Doppelte (ADR-0053): ContentHasher (SHA-256), PerceptualHash (pHash), SimilarGrouping (Bucket-Index + Stern-Gruppen), KeeperRule, DuplicateIndexService (Hintergrund), DuplicateService (Abfragen), DuplicateCleanupService (Papierkorb + Rückgängig, CatalogRowSnapshot, MetadataTransfer)
    Faces/                            # Gesichtserkennung (ADR-0035): FaceEmbeddingCodec, Detection (SCRFD), Alignment, Onnx (DirectML/CPU), Models (Download), Indexing (Hintergrund-Index); Recognition/ = Lern-/Gruppierlogik (ADR-0036); Assignment/ = Zuordnungsdienst, Crops/ = Gesichts-Ausschnitte (ADR-0037); Erase/ = „Gesichtsdaten löschen“ je Bibliothek (ADR-0038)
      Relocation/                     # DatabaseRelocationService, StorageProbe, ThumbnailInventory — Katalog + Thumbnails umziehen (ADR-0026)
    Infrastructure/                   # AppDataPaths, DatabaseLocationStore (database-location.json, ADR-0026)
    Library/                          # LibraryService, LibraryCatalogCleanup
    Media/                            # MediaFormatRegistry (ADR-0016)
    Messaging/                        # Nachrichten-Records für WeakReferenceMessenger (ADR-0017)
    Operations/                       # FileOperationService, WindowsRecycleBin (ADR-0012/0018); Ordner in den Papierkorb, Fehlende entfernen, RemovalUndoStore (ADR-0065); Ordner verschieben (FolderMoveRules), Ausgeschlossene entfernen (ADR-0066)
    Removal/                          # RemovalService: Vorschau/Zahlen, Offline-Guard, Ausführung über die Datei-Operationen (ADR-0065)
    Places/Clustering/                # GeoClusterIndex (Morton-Raster, Gruppen je Zoomstufe), MercatorProjection (ADR-0051)
    Places/Accuracy/                  # Ungenaue Orte (ADR-0062): GeoAccuracyRules, GeoJumpDetector (rein), GeoAccuracyService (Hintergrund)
    Places/Tracks/                    # GPS-Spuren (ADR-0062): TrackCodec, FitTrackReader, XmlTrackReader (GPX/TCX), GpsTrackStore, GpsTrackImportService (ZIP), GpsTrackAutoImport (Ordner/USB), GeoTrackSettings
    Places/Suggestions/               # Vorschläge (ADR-0062): PhotoTimeResolver, CameraOffsetEstimator, GeoSuggestionEngine (rein), GeoSuggestionService
    Scanning/                         # ScanService, FolderEnumerator, IncrementalScanRunner, RootAvailabilityProbe, MassMissingGuard (ADR-0019); Exclusions/ = Ausschlüsse beim Einlesen: ScanExclusionSet, ScanExclusionStore, ScanExclusionService (ADR-0066)
    Sorter/                           # FileSorterService, FlattenService, SorterPatternEngine, SorterProfileStore
    Stories/                          # Storys (ADR-0058): StoryDetector (rein), StoryReconciler (Signatur + gemeinsame Dateien), StoryTitleBuilder (sachliche Überschriften), StoryIndexService (Hintergrund, voll/inkrementell), StoryService (IStoryService für die Story-Seite), StorySettingsStore (stories.*), BasicHighlightSelector (Ersatz bis ADR-0057)
    Quality/                          # IHighlightSelector — Vertrag zwischen Foto-Güte (ADR-0057) und Storys
    Thumbnails/                       # ThumbnailService, FfmpegVideoThumbnailer, Renderer-Interfaces
  Lumora.Wpf/                     # net10.0-windows → alle; App.xaml.cs = Host + DI, MainWindow = NavigationView
    Faces/                            # WicFaceImageLoader (IFaceImageLoader), Einstellungs-Abschnitt Gesichtserkennung
    Duplicates/                       # Seite „Doppelte“ (ADR-0053): DuplicatesViewModel, Gruppen-/Datei-Karten, Vorschaubilder
    Folders/                          # Ordnerleiste der Galerie (FolderPane, seit 0.4.44; bis 0.4.37 Ordnerbaum-Seite)
    Gallery/                          # Galerie: Data (GalleryPageSource, .Persons = Personen-Filter/-Suche/-Spalte), ViewModels, Views, Controls, Converters, Services, People (Personen-Filter-Dropdown)
    Library/                          # Bibliotheks-Seite (Namespace Lumora.Libraries)
    Lightbox/                         # LightboxWindow/-ViewModel/-Service, ZoomPanControl (+ Overlay), LibVlcHost, Imaging, Faces (Gesichtsrahmen, Taste F)
    Persons/                          # Seite „Personen“ (ADR-0037): PersonsViewModel, TileListBox, PersonPickerDialog, PersonDetailsDialog (ADR-0045), WicFaceCropRenderer
    Places/                           # Seite „Orte“ (ADR-0051): MapControl (eigene Kachelkarte), MapViewport, Tiles (OSM, Platten-Cache), „Ohne Ort“, MapPickerWindow; Suggestions/ (Bereich „Ungenau“, Überlagerung der Karte) und TrackSettings/ (Einstellungen › Orte, ADR-0062)
    Settings/                         # Einstellungen, ThemeService, FaceData („Gesichtsdaten löschen…“), PersonRelations („Personen & Beziehungen“, ADR-0045)
    Shell/                            # Navigation, DialogService, StatusBar, Converters, Smoke/ (Rauchtest)
    Slideshow/                        # Diashow (ADR-0059): ISlideshowService, SlideshowWindow/-ViewModel/-Overlay, KenBurnsPlanner, Item-Loader, Einstellungen slideshow.*
    Stories/                          # Story-Seite (ADR-0060): StoriesPage/-ViewModel (Übersicht), StoryView/-ViewModel (Story), StoryLayoutEngine (Collage-Blöcke, rein), StoryDaySections, Media-Loader, Bild-Lieferung, StoryGalleryBridge (Galerie-Menü), Einstellungen › Storys, Zähler „n neu“
    Sorter/                           # Sortieren-Seite (File Sorter, Flatten & Move)
    Thumbnails/                       # WicImageThumbnailRenderer (WIC lebt im Wpf-Projekt, Interface in Services)
tests/
  Lumora.Tests/                   # xUnit: Services + Data
  Lumora.Wpf.Tests/               # xUnit: WPF-gebundener Code (WIC, GalleryQuery, Converter)
  Lumora.PerfTests/               # Benchmark/Console-Harness der Spikes
```

Noch nicht angelegt (Plan bleibt): `Services/Storage/` (StorageProfileService, ADR-0008), `Services/Settings/`,
`Domain/ValueObjects/`, `Domain/Abstractions/`, `Data/Repositories/`, `Lumora.Tools/` — Settings laufen heute
über die `Settings`-Tabelle direkt im jeweiligen Service, Interfaces liegen in `Services`.

**Referenz-Regeln:**
- Domain: keine Projekt-Abhängigkeiten
- Data → Domain
- Services → Domain + Data
- Wpf → Domain + Data + Services
- Keine WPF-Referenz in Domain/Data/Services

---

## 4. Datenbank-Schema

### V0.1-Tabellen (7 Stück)

```sql
Libraries          (Id, Name, RootPath, CreatedAt, LastOpenedAt)
Folders            (Id, LibraryId, Path, ParentId, Name, ScannedAt)
Files              (Id, LibraryId, FolderId, Name, Extension, Size,
                    CreatedAtUtc, ModifiedAtUtc, MediaType,
                    Width, Height, DurationMs, Codec,
                    IsFavorite, Rating, Missing)
Thumbnails         (FileId, Size, Kind, Format, CachePath,
                    SourceModifiedAtUtc, GeneratedAtUtc, ByteSize,
                    Status, AttemptCount, LastAttemptAtUtc,
                    ErrorCode, ErrorMessage)
                   -- Status: missing | pending | generated | failed | stale
                   -- Kind: still | hover | poster
OperationBatches   (Id, OperationType, Status, CreatedAtUtc, CompletedAtUtc,
                    PlanId, Description)
                   -- Status: pending | applying | applied | undoing | undone | failed
FileOperations     (Id, BatchId, FileId, Action, SourcePath, DestPath,
                    Status, Error, ExecutedAtUtc, UndoneAtUtc)
                   -- Action: move | copy | recycle | rename | mkdir | renamefolder (0.2.5) | adoptfolder | setcapturedate (0.2.48) | setgeolocation (0.2.53)
Settings           (Key, Value)
FilesFts           -- FTS5 virtual table (Files.Name, Extension)
```

### V0.2+ Tabellen
- LibraryRoots (Id, LibraryId, Path, Role, Name, SortOrder) + `Folders.RootId` — 0.2.5, Migration `AddLibraryRoots`,
  ADR-0020: Wurzelordner je Bibliothek, Role 0 = Eingang (beliebig viele), 1 = Archiv (genau eine; `Libraries.RootPath`
  zeigt auf sie). Indizes `UX_LibraryRoots_Library_Path`, `IX_LibraryRoots_Library_Role`, `IX_Folders_Root`.
- Tags / FileTags — umgesetzt in 0.2.6 (Migration `AddTags`, ADR-0021):
  `Tags (Id, Name UNIQUE COLLATE NOCASE, Color "#RRGGBB", SortOrder, CreatedAtUtc)`,
  `FileTags (FileId → Files ON DELETE CASCADE, TagId → Tags ON DELETE CASCADE, PK (FileId, TagId), IX_FileTags_Tag)`.
  Tags sind global (nicht je Library); Suche nach Tag-Namen per Join, nicht über `FilesFts`.
- Categories / CategoryValues / FileCategoryValues — umgesetzt in 0.2.20 (Migration `AddCategories`, ADR-0027):
  `Categories (Id, Name UNIQUE COLLATE NOCASE, SortOrder, CreatedAtUtc)`,
  `CategoryValues (Id, CategoryId → Categories ON DELETE CASCADE, Name COLLATE NOCASE, SortOrder, UNIQUE (CategoryId, Name))`,
  `FileCategoryValues (FileId → Files, CategoryId → Categories, ValueId → CategoryValues — alle ON DELETE CASCADE,
  PK (FileId, CategoryId) = ein Wert je Kategorie und Datei, IX_FileCategoryValues_Value, IX_FileCategoryValues_Category)`.
  Seit 0.2.30 (Migration `AddCategorySortIndex`, ADR-0031) ersetzt der abdeckende Index
  `IX_FileCategoryValues_Category_Value_File (CategoryId, ValueId, FileId)` den Index `IX_FileCategoryValues_Category`
  (segmentweise Kategorie-Sortierung, Kategorie-Filter, Suche nach Kategorie-Namen lesen nur den Index).
  Global wie Tags; „Neu aufbauen“ trägt die Werte über `UserDataCarryOver` (ADR-0023) mit — seit 0.2.24 in derselben
  Transaktion, die die alten Zeilen ersetzt (ADR-0028, keine Schema-Änderung).
- Alben und Ordner-Verknüpfungen — umgesetzt in 0.3.2 (Migration `AddAlbums`, ADR-0054; nur neue Tabellen):
  `Albums (Id AUTOINCREMENT, GroupId, Name COLLATE NOCASE, Kind 0 eigenes | 1 intelligent, FilterJson NULL (gespeicherter
  Galerie-Filter, Begriffe über ihre Namen), CoverFileId NULL → Files ON DELETE SET NULL, SortOrder, CreatedAtUtc,
  UpdatedAtUtc; UX_Albums_Group_Name (GroupId, Name) eindeutig, IX_Albums_CoverFile)` — je Bibliotheks-Gruppe wie Tags
  (`GroupId` ohne Fremdschlüssel, in `LibraryGroupStore.TermTables`).
  `AlbumItems (AlbumId → Albums, FileId → Files — beide ON DELETE CASCADE, PK (AlbumId, FileId), AddedAtUtc;
  IX_AlbumItems_File)` — Fotos eigener Alben (verknüpft, nicht kopiert).
  `FolderLinks (FolderId → Folders, FileId → Files — beide ON DELETE CASCADE, PK (FolderId, FileId), AddedAtUtc;
  IX_FolderLinks_File)` — Foto zusätzlich in einem weiteren Ordner derselben Bibliothek (keine Windows-Hardlinks).
  `FileLinkUndo (Id, FileOperationId → FileOperations ON DELETE CASCADE, Kind 0 Album | 1 Ordner, TargetId, AddedAtUtc,
  SourcePath, FileSize, FileModifiedAtUtc; IX_FileLinkUndo_Operation)` — Verknüpfungen einer Datei, deren Files-Zeile eine
  Datei-Operation entfernt hat (Papierkorb, aus der Bibliothek heraus); „Rückgängig“ bzw. Rückkehr derselben Datei (Pfad +
  Größe + Änderungszeit) stellt sie wieder her. „Neu aufbauen“ trägt AlbumItems, FolderLinks (über Ordnerpfad), Titelbilder
  und gesicherte Ordner-Ziele mit (`UserDataCarryOver.Albums.cs`).
- Plans / PlanFolders / PlanAssignments (V0.3)
- FileHashes (V0.3+ separat von Files)
- SorterProfiles / SorterTokens (Phase 8)
- ~~ExtractedArchives (Phase 8)~~ — entfällt: Archive sind Files-Zeilen mit `MediaType = 2`, der Papierkorb protokolliert über OperationBatches (ADR-0022)
- Persons / Faces / FaceRejections / FaceScans — Teil 1 umgesetzt in 0.2.34 (Migration `AddFaces`, ADR-0035):
  `Persons (Id, Name COLLATE NOCASE, CreatedAtUtc, CoverFaceId NULL ohne Fremdschlüssel, IsHidden)` — global wie Tags;
  `Faces (Id, FileId → Files ON DELETE CASCADE, BoxX/BoxY/BoxW/BoxH relativ 0..1, Landmarks BLOB 10 float32, DetScore,
  Size, Sharpness, Quality, Embedding BLOB 512 float32 = 2048 Bytes L2-normiert, ModelVersion, PersonId / SuggestedPersonId
  → Persons ON DELETE SET NULL, State 0 unbestätigt | 1 Vorschlag | 2 bestätigt | 3 ignoriert, SuggestionScore, CreatedAtUtc;
  IX_Faces_File, IX_Faces_Person, IX_Faces_State, IX_Faces_SuggestedPerson; seit 0.2.38 Migration `AddFacePersonIndex`
  zusätzlich abdeckend IX_Faces_Person_State_File (PersonId, State, FileId), IX_Faces_SuggestedPerson_State_File
  (SuggestedPersonId, State, FileId), IX_Faces_File_State_Person (FileId, State, PersonId, SuggestedPersonId) für
  Personen-Filter/-Suche/-Spalte, ADR-0038)`,
  `FaceRejections (FaceId → Faces, PersonId → Persons, PK beide, CASCADE)`,
  `FaceScans (FileId PK → Files CASCADE, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode)` — Merker des Hintergrund-Index.
  Seit 0.2.47 (Migration `AddPersonDetails`, ADR-0045): `Persons` + `FirstName`/`LastName` (NOT NULL DEFAULT '', NOCASE),
  `Nickname` (NULL, NOCASE), `RelationId` → `PersonRelations` ON DELETE SET NULL, `BirthYear`/`BirthMonth`/`BirthDay`
  (NULL, auch nur Jahr bzw. Jahr + Monat), `IsMe` (eindeutiger Teilindex `IX_Persons_IsMe WHERE IsMe = 1`),
  `IX_Persons_Relation`; `Name` bleibt der synchron gehaltene Anzeigename (Rufname, sonst „Vorname Nachname“), Bestand am
  ersten Leerzeichen geteilt. `PersonRelations (Id, Name NOCASE, IX_PersonRelations_Name eindeutig)` — selbst getippte
  Beziehungen, global wie Tags.
  „Neu aufbauen“ parkt Faces/FaceScans in seiner Transaktion und setzt sie über Ordnerpfad + Name auf die neuen FileIds
  (`UserDataCarryOver.Faces.cs`); Face-Ids bleiben.
- Aufnahmedatum — umgesetzt in 0.2.48 (Migration `AddCapturedAt`, ADR-0046): `Files.CapturedAt` (Unix-Sekunden UTC,
  NULL = unbekannt), `Files.CapturedAtSource` (NULL = noch nicht gelesen, 0 unbekannt, 1 EXIF, 2 Video, 3 Dateiname,
  4 manuell), `Files.CapturedAtPrecision` (0 genau, 1 Tag, 2 Monat, 3 Jahr — ungefähre Angaben in der Mitte des
  Zeitraums), `Files.CapturedAtOffset` (Minuten Ortszeit − UTC, Anzeige als Wanduhrzeit). Ausdrucks-Index
  `IX_Files_Library_Captured ON Files(LibraryId, COALESCE(CapturedAt, ModifiedAtUtc) DESC, Id DESC)` (Raw-SQL in der
  Migration, EF kennt ihn nicht — `DatabaseInitializer` legt ihn bei jedem Start idempotent an,
  `CaptureDateSortSql.CreateIndexSql`). `CaptureDateUndo (FileOperationId PK → FileOperations ON DELETE CASCADE,
  OldCapturedAt, OldSource, OldPrecision, OldOffset)` — alte Werte für „Rückgängig“ von „Datum setzen…“
  (`OperationBatches.OperationType = 7 SetCaptureDate`, je Datei eine FileOperation). Gefüllt vom Nachtragen im
  Hintergrund (nicht im Scan); manuelle Daten überleben „Neu aufbauen“ (`UserDataCarryOver`) und Inhaltsänderungen
  (Incremental-Scan setzt nur nicht-manuelle zurück).
- Orte — umgesetzt in 0.2.53 (Migration `AddGeoLocation`, ADR-0050, Vertrag mit der Karte): `Files.Latitude`/`Longitude`
  (REAL, WGS84-Dezimalgrad), `Files.GeoSource` (NULL = noch nicht gelesen, 0 keine Koordinaten, 1 EXIF/XMP, 2 Video,
  3 manuell gesetzt bzw. entfernt), `Files.PlaceId` (GeoNames-Id des nächsten Orts aus der eingebetteten Ressource, NULL
  ohne Ort im Umkreis von 100 km). Gefilterte Indizes `IX_Files_Library_Geo ON Files(LibraryId, Latitude, Longitude) WHERE
  Latitude IS NOT NULL` (Karte, Ausschnitt) und `IX_Files_Library_Place ON Files(LibraryId, PlaceId) WHERE PlaceId IS NOT
  NULL` (Ortsfilter, Ortszähler). `GeoTagUndo (FileOperationId PK → FileOperations ON DELETE CASCADE, OldLatitude,
  OldLongitude, OldGeoSource, OldPlaceId, NewLatitude, NewLongitude, WriteKind 0 Katalog | 1 JPEG-EXIF | 2 XMP-Begleitdatei,
  WrittenPath, OldBytes BLOB (altes APP1-Segment bzw. alte XMP-Datei), WrittenHash BLOB (SHA-256 des Geschriebenen),
  SegmentOffset, OldModifiedTicks (UTC-Ticks), OldSize)` — „Rückgängig“ von „Ort setzen/entfernen“ (`OperationBatches.OperationType = 8
  SetGeoLocation`, je Datei eine FileOperation). Gefüllt vom Nachtragen im Hintergrund; manuelle Orte überleben „Neu
  aufbauen“ und Inhaltsänderungen wie das Aufnahmedatum.
- Motive — umgesetzt in 0.2.55 (Migration `AddMotifs`, ADR-0052):
  `MotifTerms (Id, LibraryId → Libraries ON DELETE CASCADE, GroupId, Name COLLATE NOCASE, Prompt NULL (Beschreibung fürs
  Modell), Threshold NULL (= globale Schwelle), SortOrder, TextEmbedding BLOB NULL (768 float32), TextModelVersion NULL,
  CreatedAtUtc; UX_MotifTerms_Group_Name (GroupId, Name) eindeutig, IX_MotifTerms_Library (LibraryId))` — seit 0.2.56
  (Migration `MotifTermsByGroup`, ADR-0048 Nachtrag) Begriffe **je Bibliotheks-Gruppe** wie Tags: `GroupId` per
  `ADD COLUMN` ohne Fremdschlüssel; `LibraryId` ist nur noch der Anker des Fremdschlüssels aus `AddMotifs` (eine
  Bibliothek der Gruppe, beim Lösen/Entfernen nachgezogen) und wird für den Bereich nie gelesen. Standard-Liste
  (82 Begriffe) einmal je Gruppe (Merker `Settings.motifs.seeded.g{GroupId}`, vorher `motifs.seeded.{LibraryId}`).
  `FileEmbeddings (FileId PK → Files ON DELETE CASCADE, ModelVersion, ErrorCode NULL, CreatedAtUtc, Embedding BLOB NULL
  = 768 float16 = 1 536 Bytes L2-normiert, als letzte Spalte; IX_FileEmbeddings_File_Model (FileId, ModelVersion)
  abdeckend fürs Zählen)` — Bild-Vektor je Foto bzw. Video-Vorschaubild, bleibt bei Begriffs-Änderungen.
  `FileMotifs (FileId → Files, TermId → MotifTerms — beide ON DELETE CASCADE, PK (FileId, TermId), Score REAL = SigLIP-
  Wahrscheinlichkeit; IX_FileMotifs_Term_File (TermId, FileId))` — nur Treffer über der Schwelle.
  Settings: `motifs.enabled` (Standard an), `motifs.useGpu`, `motifs.threshold` (Standard 0,01). „Neu aufbauen“ parkt
  FileEmbeddings/FileMotifs wie die Gesichter (`UserDataCarryOver.Motifs.cs`); der Incremental-Scan verwirft Vektor und
  Treffer an Ort und Stelle geänderter Dateien.
- Bibliotheks-Gruppen — umgesetzt in 0.2.51 (Migration `AddLibraryScoping`, ADR-0048): `LibraryGroups (Id, Name NOCASE,
  CreatedAtUtc)`, `Libraries.GroupId` (`IX_Libraries_Group`), dazu `GroupId` an `Tags`, `Categories`, `Persons` und
  `PersonRelations` (seit 0.2.56 auch `MotifTerms`) — die Angaben „global“ oben gelten seitdem **je Gruppe**: `UX_Tags_Group_Name`,
  `UX_Categories_Group_Name`, `IX_PersonRelations_Group_Name` (eindeutig, NOCASE), `IX_Persons_Group_IsMe` (ersetzt
  `IX_Persons_IsMe`), `IX_Persons_Group_Name`; die globalen `UX_Tags_Name`, `UX_Categories_Name`, `IX_PersonRelations_Name`
  sind weg. `GroupId` ohne Fremdschlüssel (`ADD COLUMN … NOT NULL DEFAULT 0`, kein Tabellen-Neuaufbau); 0 = „ohne
  Bibliothek“, übernimmt die nächste Bibliothek. Jede Bibliothek hat ihre Gruppe (Standard: nur sie selbst); verknüpfte
  Bibliotheken teilen eine. Bestand: je benutzender Bibliothek eine Kopie der Begriffe (Zuordnungen umgehängt), ungenutzte
  in jeder Bibliothek. Ansicht „Alle verknüpften“ gemerkt in `Settings["Library.ViewAllLinkedGroup"]`.
- Doppelte — umgesetzt in 0.3.1 (Migration `AddDuplicateDetection`, ADR-0053, nur CREATE TABLE):
  `FileFingerprints (FileId PK → Files ON DELETE CASCADE, ContentHash BLOB (SHA-256, nur Dateien gleicher Größe),
  ContentSize, ContentModifiedAt, ImageHash INTEGER (64-Bit-pHash, vorzeichenbehaftet), ImageHashVersion, ImageModifiedAt,
  ImageLowDetail, ImageError)` mit `IX_FileFingerprints_ContentHash (ContentHash) WHERE ContentHash IS NOT NULL` — ein
  Fingerabdruck gilt nur, solange Größe/Änderungszeit der Files-Zeile gleich sind; `DuplicateCleanupUndo (Id, BatchId →
  OperationBatches ON DELETE CASCADE, FileId, KeptFileId, Snapshot (JSON: Files-Zeile + alle per Kaskade abhängigen Zeilen),
  Transfer (JSON: was auf die behaltene Datei übertragen wurde), CreatedAtUtc, UndoneAtUtc, IX_DuplicateCleanupUndo_Batch)`.
  Settings: `duplicates.similarThreshold` (Standard 10), `duplicates.transferMetadata` (Standard an).
  Seit 0.4.30 (ADR-0065, keine Migration) liegen dort auch die Abzüge für „Ordner in den Papierkorb“ und „Fehlende
  entfernen“: `KeptFileId = 0` (kein behaltenes Gegenstück; die Seite „Doppelte“ blendet sie aus), je entfernter Datei ein
  Abzug, `FileId = 0` = Abzug der entfernten `Folders`-Zeilen samt `FolderLinks` hinein. Neue `OperationType`-Werte
  `RecycleFolder` (9), `DeleteFolder` (10), `RemoveFromCatalog` (11) (als int gespeichert).
  Seit 0.4.33 (ADR-0066, keine Migration) auch für „Ausgeschlossene aus der Bibliothek entfernen“ (`ExcludeFromCatalog` (13));
  dazu `MoveFolder` (12) für „Ordner verschieben“ (ohne Abzüge, Pfade/ParentId/RootId der Folders-Zeilen werden nachgezogen).
- Ordner-Titelbild und Datei-Kopien — seit 0.4.44 (ADR-0068, Migration `AddFolderCover`, nur ADD COLUMN):
  `Folders.CoverFileId INTEGER NULL` (eigene Wahl, ohne FK; gilt nur, solange die Datei im Ordner liegt und nicht fehlt, sonst
  automatische Wahl; „Neu aufbauen“ trägt sie über den Ordnerpfad mit). Neue `OperationType`-Werte `Rotate` (14, EXIF-Ausrichtung,
  alte Bytes in `GeoTagUndo`); `Copy` (Kopieren nach…) wird jetzt protokolliert und ist rückgängig machbar (Kopie in den Papierkorb).
- Ausschlüsse beim Einlesen — seit 0.4.33 (ADR-0066, keine Migration, nur Settings): `scan.excludedNames` (JSON-Array von
  Ordnernamen für alle Bibliotheken des Katalogs; fehlt der Eintrag, gelten `.thumbs` und `.temp`),
  `scan.excludedFolders.{LibraryId}` (JSON-Array `{"root":LibraryRoots.Id,"path":"relativ zur Wurzel"}`, gehört der Bibliothek,
  geht beim Löschen der Bibliothek mit). Je Katalog — die versteckte Sitzung hat ihre eigene Liste.
- Foto-Güte — umgesetzt in 0.4.7 (Migration `AddFileQuality`, ADR-0057, nur CREATE TABLE):
  `FileQuality (FileId PK → Files ON DELETE CASCADE, Sharpness REAL, Exposure REAL, Aesthetic REAL NULL (Bildwirkung aus
  den Motiv-Vektoren), AestheticVersion NULL (Modell + Prompt-Satz), Version, SourceSize, SourceModifiedAt, ErrorCode NULL)`
  — ein Wert gilt nur, solange Größe/Änderungszeit der Files-Zeile gleich sind und `Version` passt; kein weiterer Index.
  Settings: `quality.aestheticVectors` (zwischengespeicherte Text-Vektoren der Bildwirkung). „Neu aufbauen“ rechnet die
  Werte neu (keine Übernahme).
- Storys — umgesetzt in 0.4.8 (Migration `AddStories`, ADR-0058, nur CREATE TABLE):
  `Stories (Id AUTOINCREMENT, GroupId (ohne Fremdschlüssel, in LibraryGroupStore.TermTables), Kind 0 Ereignis | 1 Reise |
  2 Rückblick | 3 Person | 4 Ort | 5 eigene, Signature (stabiler Schlüssel der Erkennung, z. B. E:<Start>, T:<Tag>-<Tag>,
  P:<Person>:<Jahr>-<Jahr>, O:<Ort>, R:D:yyyy-MM-dd, C:<Guid>), StartAt/EndAt (Unix-Sekunden UTC), FirstDay/LastDay
  (Kalendertag in Ortszeit, Tage seit 1970-01-01), DatePrecision (2/3 = Story aus ungefähren Daten), PlaceId NULL
  (GeoNames-Id), PersonId NULL → Persons ON DELETE CASCADE, PlaceIds NULL („id,id,…“ in Reihenfolge des Auftretens),
  VisitCount NULL, IsAway, CustomTitle NULL, CoverFileId NULL → Files ON DELETE SET NULL, CoverIsManual, IsPinned,
  PinnedAtUtc NULL, IsHidden, IsSeen, SeenAtUtc NULL, FileCount, VideoCount, CreatedAtUtc, UpdatedAtUtc;
  UX_Stories_Group_Signature (GroupId, Signature) eindeutig, IX_Stories_Group_Kind_Start, IX_Stories_Person,
  IX_Stories_CoverFile)`. `StoryItems (StoryId → Stories, FileId → Files — beide ON DELETE CASCADE, PK (StoryId, FileId),
  Day (Tagesabschnitt, Kalendertag Ortszeit), Origin 0 automatisch | 1 vom Benutzer hinzugefügt | −1 vom Benutzer
  entfernt (Ausschluss, zählt nicht), AddedAtUtc; IX_StoryItems_File)`. Überschriften werden nicht gespeichert.
  `FileLinkUndo.Kind` zusätzlich 2 = Story-Eintrag hinzugefügt, 3 = Story-Ausschluss (manuelle Einträge über
  Datei-Operationen hinweg). „Neu aufbauen“ trägt alle StoryItems und Titelbilder über Ordnerpfad + Name mit
  (`UserDataCarryOver.Stories.cs`). Settings: `stories.events|memories|persons|places` (an/aus, Standard an),
  `stories.pauseHours` (6), `stories.minFiles` (12), `stories.home` (auto | place:<id> | geo:<lat>,<lon>),
  `stories.minPersonFiles` (30), `stories.highlightShare` (0,15). Rückblicke werden je Tag berechnet und erst beim
  Öffnen als Story (Kind 2) gespeichert.
- Ungenaue Orte und GPS-Spuren — umgesetzt in 0.4.23 (Migration `AddGpsTracks`, ADR-0062, nur ADD COLUMN/CREATE TABLE):
  an `Files` `GeoAccuracy` (NULL = noch nicht bewertet, 0 nicht bewertbar, 1 genau, 2 geschätzt), `GeoAccuracyReason`
  (Bitmaske 1 ohne Höhe/Zeit, 2 Netzwerk-Verfahren, 4 Fehlerradius > 500 m, 8 unmöglicher Sprung), `GpsTimeUtc`
  (GPS-Zeitstempel, Unix-Sekunden), `ExifTimeOffset` (OffsetTimeOriginal in Minuten, NULL ohne), `CameraModel`
  („Hersteller Modell“), `GeoSuggestionState` (NOT NULL DEFAULT 0, 1 = Vorschlag abgelehnt); gefilterter Index
  `IX_Files_Library_GeoEstimated ON Files(LibraryId, GeoAccuracy) WHERE GeoAccuracy = 2`. `Files.GeoSource` zusätzlich
  4 = aus GPS-Spur übernommen, 5 = aus Nachbarfotos übernommen (wie 3: überleben Inhaltsänderungen und „Neu aufbauen“).
  `GpsTracks (Id AUTOINCREMENT, GroupId (ohne Fremdschlüssel, in LibraryGroupStore.TermTables), StartUtc/EndUtc
  (Unix-Sekunden), MinLatitude/MinLongitude/MaxLatitude/MaxLongitude, PointCount, DistanceMeters, Sport NULL, Device
  NULL, Name NULL, Source 1 FIT | 2 GPX | 3 TCX, Checksum BLOB (SHA-256 über Zeit + Mikrograd), Points BLOB
  (TrackCodec v1: ZigZag-Varint-Differenzen Zeit/1e-6°/dm, Deflate), ImportedAtUtc; UX_GpsTracks_Group_Checksum
  (GroupId, Checksum) eindeutig, IX_GpsTracks_Group_Time (GroupId, StartUtc, EndUtc))` — eine Zeile je Aktivität.
  Settings: `places.tracks.timeZone` (leer = Windows), `places.tracks.toleranceSeconds` (120),
  `places.tracks.cameraOffsets` (JSON Kamera → Sekunden), `places.tracks.watchFolder`, `places.tracks.usb` (Standard an),
  `places.suggestions.saveToFile` (Standard aus), Merker `places.tracks.watchLastScan`, `places.tracks.usbLastImport`.
- Story-Assistent — umgesetzt in 0.4.24 (Migration `AddStoryAssistant`, ADR-0063, nur CREATE TABLE):
  `AssistantHistory (Id AUTOINCREMENT, GroupId (ohne Fremdschlüssel, in LibraryGroupStore.TermTables), Sentence, IntentJson
  (Absicht der Sprach-KI, ohne Fotos/Pfade/Koordinaten), Action 0 Story | 1 Collage | 2 Album | 3 Diashow | 4 Galerie,
  ResultCount, CreatedAtUtc, LastUsedAtUtc; IX_AssistantHistory_Group_Used)` — je Gruppe höchstens 50, gleicher Satz nur
  aufgefrischt. `AssistantAliases (Id AUTOINCREMENT, GroupId, Term (normalisiert: klein, ohne Akzente), PersonId → Persons
  ON DELETE CASCADE, CreatedAtUtc; UX_AssistantAliases_Group_Term_Person eindeutig, IX_AssistantAliases_Person)` — gemerkte
  Antworten auf Rückfragen („oma“ → Person(en)). Verknüpfen/Lösen/leere Gruppe über `AssistantGroupStore`. Settings:
  `assistant.enabled` (an), `assistant.model` (`qwen2.5:3b`), `assistant.useGpu` (an), `assistant.history` (an) — je Katalog.

### Pflicht-Indizes V0.1
```sql
CREATE INDEX IX_Files_Library_Folder ON Files(LibraryId, FolderId);
CREATE INDEX IX_Files_Folder_Name ON Files(FolderId, Name);
CREATE INDEX IX_Files_Library_MediaType ON Files(LibraryId, MediaType);
CREATE INDEX IX_Files_Library_Modified ON Files(LibraryId, ModifiedAtUtc DESC);
CREATE INDEX IX_Files_Library_Favorite ON Files(LibraryId, IsFavorite);
CREATE INDEX IX_Thumbnails_File_Size_Kind ON Thumbnails(FileId, Size, Kind);
CREATE INDEX IX_FileOperations_Batch_Status ON FileOperations(BatchId, Status);
CREATE UNIQUE INDEX UX_Folders_Library_Path ON Folders(LibraryId, Path);
CREATE UNIQUE INDEX UX_Files_Folder_Name_Size_Modified ON Files(FolderId, Name, Size, ModifiedAtUtc);
```

### PRAGMAs
```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
PRAGMA temp_store=MEMORY;
```

### Kataloge und Katalog-Sitzungen (seit 0.2.52, ADR-0049)
- **Normaler Katalog**: `library.db` + `thumbs\` im Datenordner (ADR-0026), Protokolle unter `%LocalAppData%\Lumora\logs`.
- **Versteckte Bibliothek**: je Bibliothek ein eigener Katalog in `<Bibliotheksordner>\.lumora\` im verschlüsselten
  Container (`library.db`, `thumbs\`, `logs\`, `cache\`, `temp\`, `hidden-library.json` mit Name, Kennwort-Prüfwert,
  Zusperr-/Aushänge-Einstellung). Gleiches Schema, gleiche Migrationen; nur mit Startschalter `_hidden` sichtbar.
- **Katalog-Sitzung** = ein DI-Container mit `IAppDataPaths` + `ICatalogContext` (`IsHidden`, Pfade, `CacheRoot`,
  `TempRoot`) und eigenem Serilog-Logger. Wechsel normal ↔ versteckt zur Laufzeit: Fenster schließen, Host entsorgen
  (Dienste, Verbindungen, Caches), Messenger zurücksetzen, neuen Host starten (Migration), neues Hauptfenster. Dateien
  außerhalb der DB legen Module nur unter `ICatalogStoragePaths.CacheRoot/TempRoot` ab. `.lumora` wird nie gescannt.

---

## 5. 15 finale ADRs

| ADR | Titel | Inhalt |
|---|---|---|
| **ADR-001** | Runtime + UI-Stack | .NET 10 LTS, WPF, WPF-UI als isolierter UI-Layer, CommunityToolkit.Mvvm |
| **ADR-002** | Solution-Architektur | Multi-Project ab Tag 1 (Domain/Data/Services/Wpf) |
| **ADR-003** | Catalog-Modell | Eine aktive Library, zentrale DB `%LocalAppData%\Lumora\library.db`, LibraryId-FKs vorbereitet |
| **ADR-004** | Datenzugriff | EF Core für CRUD, Raw `Microsoft.Data.Sqlite` für Bulk/FTS5/Performance-Pfade, WAL + Single-Writer |
| **ADR-005** | Migration-Disziplin | DB-Reset bis erste produktive Nutzung, ab V0.2 strict Migrations, WAL-Checkpoint + versioniertes Backup |
| **ADR-006** | Bild- & Video-Pipeline | WIC für JPG/PNG, FFmpeg für MP4-Standbilder, LibVLCSharp für MP4-Lightbox (V0.1 Cutline: bei rotem Spike "Extern öffnen") |
| **ADR-007** | Thumbnail-Scheduling | Multi-Channel Queue (Critical/Viewport/CurrentFolder/Background) + Dedupe + Versioning + Status + `.tmp` Writes |
| **ADR-008** | Storage-Profile | StorageProfileService (LocalSsd/LocalHddOrUsb/NetworkShare/Unknown), Offline-State separat von Profil |
| **ADR-009** | Cache-Strategie | Thumbs lokal `%LocalAppData%\Lumora\thumbs\{libraryId}\` auch bei NAS |
| **ADR-010** | UI-Datenmodell | Paged/virtualisiertes Grid via Query-DTOs, keine 500k ObservableCollection, SelectionStore nach FileId |
| **ADR-011** | Suche/Filter V0.1 | FTS5 minimal Pflicht (Files.Name+Extension), Filter Alle/Bilder/Videos/Favoriten Pflicht, Favorit ja, Rating später |
| **ADR-012** | Operation-Logging | OperationBatches/FileOperations ab V0.1 für Delete-to-Recycle, Move/Rename V0.2+ |
| **ADR-013** | Status-UI | 4 Indikatoren Pflicht: Scan-Fortschritt, Thumb-Queue, Background-Aktivität, Recovery-Hinweise |
| **ADR-014** | Animated Hover | NICHT V0.1, später default-off, on-demand, LRU-limitiert |
| **ADR-015** | Face Recognition | NICHT im V0.1-Schema, eigenes Phase-9-Epic mit eigener Migration |

> **Vollständige ADRs** (Kontext / Entscheidung / Begründung / Konsequenzen / Alternativen) liegen unter [`docs/adr/`](docs/adr/) — jeder ADR ist eine eigene Markdown-Datei nach klassischem Michael-Nygard-Template. Siehe [docs/adr/README.md](docs/adr/README.md) für Index + Lifecycle-Regeln.

---

## 6. V0.1-Scope

Status (Stand 0.2.0): ✅ umgesetzt · 🟡 teilweise · ⬜ offen

### Pflicht (nicht streichbar)
| Status | Punkt |
|---|---|
| ✅ | Multi-Project Solution |
| ✅ | SQLite-Bulk-Scan (Raw SQLite Prepared INSERT in Transaktion) |
| ✅ | FTS5 minimal (Files.Name + Extension) — mit `FtsQueryBuilder` für sichere Benutzereingaben |
| ✅ | Paged virtualisiertes Grid (200–1000 Items pro Page, keine 500k ObservableCollection) |
| ✅ | WIC-Thumbnails JPG/PNG mit `.tmp`-Write + atomic Move — plus EXIF-Orientierung, weitere Formate (ADR-0016) |
| 🟡 | MP4-statisches Thumbnail via FFmpeg + Codec-Badge — Thumbnail ✅, Codec-Badge nur im Lightbox-Info-Panel |
| ✅ | Suche + Filter (Alle/Bilder/Videos/Favoriten) + Favorit |
| ✅ | Offline-vs-Missing-Guard mit 20%-Threshold (ADR-0019) |
| ✅ | Statusbar mit 4 Indikatoren (technisch-look OK, Hauptsache zuverlässig) |
| ✅ | Image-Lightbox mit Zoom + Pfeiltasten + Buttons |
| ✅ | Delete-to-Recycle mit Confirm + OperationBatches-Eintrag |
| ✅ | Container-State (Subfolder-Kacheln wenn keine direkten Medien) |

### V0.1 nur wenn Spike grün (Cutline ~5 PT)
| Status | Punkt |
|---|---|
| ✅ | **LibVLCSharp Video-Lightbox** — Spike grün, produktiv in `Wpf/Lightbox` |
| ⬜ | **FPS-basiertes Adaptive Throttling** — fällt zurück auf StorageProfile-feste Budgets (beides offen, feste Worker-Zahl) |

### Aus V0.1 raus
| Status | Punkt |
|---|---|
| ⬜ | Animated Hover-Thumbs |
| ✅ | Move / Rename (0.2.0 über `IFileOperationService`, ADR-0018) |
| ⬜ | Plan-Ordner |
| ⬜ | Tags / Albums |
| ✅ | Rating (0.2.0: 1–5 Sterne in Galerie und Lightbox) |
| ⬜ | Face Recognition |
| 🟡 | Full-Recovery-Repair-UI — Hinweis in der Statusleiste + Undo, kein Reparatur-Dialog |
| ✅ | File-Sorter (0.2.0 aus V2 portiert, Pattern-Engine + Multi-Level; ab 0.2.16 über alle Wurzeln: Eingang → Archiv, Ziele nur unter den Wurzeln, ADR-0024) |
| ✅ | Ordner in die Bibliothek übernehmen (Struktur erhalten, leere Zwischenordner zusammenziehen, ein `AdoptFolder`-Batch mit Rückgängig) + Ordner bewerten/kategorisieren (0.2.32, ADR-0034; keine Schema-Änderung, `OperationType.AdoptFolder = 6`). In der Oberfläche heißt die Archiv-Wurzel seitdem „Bibliotheksordner“ |
| ✅ | Archive: im Katalog, Inhaltsansicht, manuelles Entpacken ✅ (0.2.8, ADR-0022); Auto-Extract nach dem Scan für neue Archive in Eingängen ✅ (0.2.25, ADR-0032, Einstellung, Standard aus; je Bibliothek + vorhandene Archive nachholen ✅ 0.2.31, ADR-0033) |
| ⬜ | pHash Duplikat-Erkennung |

---

## 7. Phasen-Roadmap

| Status | Phase | Inhalt | Aufwand |
|---|---|---|---|
| ✅ | **Spikes (vor V0.1)** | 6 Pflicht-Spikes (3 blockierend) — alle grün, siehe `docs/spikes/` | 10–18 PT |
| ✅ | **V0.1** | Produktiver Viewer + Indexer (siehe Pflicht-Liste) | 60–90 PT |
| 🟡 | **V0.2** | LibVLC-Lightbox produktiv ✅, Move/Rename ✅, OperationBatches voll ✅ (ADR-0018), Recovery-UI 🟡 (Statusleisten-Hinweis + Undo), Thumbnail-Priorities komplett ⬜ (zwei Worker-Pools, keine 4-Prio-Queue); dazu Incremental-Scan ✅ (ADR-0019), Bibliotheksverwaltung ✅, Ordnerbaum ✅, Einstellungen ✅ | 25–40 PT |
| ⬜ | **V0.3** | Plan-Ordner: Plans/PlanFolders/PlanAssignments + Drag&Drop + Preflight + Apply Move-only + Block-Undo | 25–40 PT |
| 🟡 | **V0.4 / Phase 8** | FTS5 erweitert (Tags) ⬜, Tags/Albums-UI ⬜, Rating ✅ (vorgezogen), Animated Hover ⬜, File-Sorter ✅ (vorgezogen: Regex-Profile, Multi-Level, Import/Export; Drag&Drop-Editor ⬜), Archive im Katalog + Entpacken ✅ (vorgezogen, ADR-0022; Auto-Extract nach dem Scan ✅, ADR-0032), pHash Duplikate ⬜, Flatten & Move ✅ (vorgezogen) | 40–60 PT |
| 🟡 | **V0.5 / Phase 9** | Face Recognition: Teil 1 ✅ (0.2.34, ADR-0035: InsightFace buffalo_l = SCRFD + ArcFace via ONNX Runtime/DirectML, Modell per Benutzer-Download, Schema `AddFaces`, Hintergrund-Index mit Statusleiste, Einstellungen), Lern-/Gruppierlogik ✅ (0.2.35, ADR-0036), Zuordnungsdienst + Personen-Seite ✅ (0.2.37, ADR-0037: Benennen, Vorschläge, Gruppen mit abgewählten Ausreißern), Filter/Suche/Liste/Kachel/Lightbox-Rahmen nach Personen + „Gesichtsdaten löschen“ ✅ (0.2.38, ADR-0038) | 30–50 PT |

**Gesamt-V0.1 brutto:** 70–108 PT = **8–10 Wochen** für 1 erfahrenen Entwickler bei 5 PT/Woche.

### Geplante Vorhaben (Stand 0.4.21, 2026-10-22)

| Status | Vorhaben | Inhalt | Grundlage |
|---|---|---|---|
| ✅ | **Ungenaue Orte + GPS-Spuren** | geschätzte Handy-Positionen erkennen, Vorschläge aus Garmin-Spuren (Export-ZIP/FIT/GPX/USB) und Nachbarfotos, Einstellungen › Orte | Mockup 10, ADR-0062, umgesetzt in 0.4.23 |
| ✅ | **Welle 6b Story-Assistent** | Sätze → lokale Sprach-KI (Ollama) → Filter + Aktion, Verständnis zur Bestätigung, Tags/Kategorien/Orte/Motive/Personen, Collage-Bild | Mockup 12, ADR-0063, umgesetzt in 0.4.24 |
| ⬜ | **App-Verbindung (Gipfelbuch)** | lokale Schnittstelle 127.0.0.1, Kopplung, Rechte, Hintergrundstart, Reiter „Verbundene Apps“ | Mockup 11, Vertrag `herbertschrotter-blip/app-verbindung` v0.1 (Entwurf) |
| ⬜ | **Nutzungs-Signale** | lokal messen, was Herbert mag: Öffnen, Ansehdauer (aktiv, 1 s–2 min, kein Durchblättern), Zoom, Wiederkehr an anderen Tagen, Verwendung in Album/Story/Collage/Titelbild, Drucken/Teilen/Exportieren über Lumora, später Handy (Welle 7). Nicht zählen: Sortieren, Doppelte, Suche nach einem Foto. Nur Tagessummen je Foto (kein Ereignisprotokoll), je Katalog, versteckt nur im Container, abschaltbar + „Nutzungsdaten löschen“, nichts im Protokoll. Nutzung: kleiner Zuschlag in der Highlight-Auswahl (ADR-0057) mit Grund („oft angesehen“, „3× geteilt“), gedämpft gegen Selbstverstärkung (Ansehen in Storys zählt weniger), fehlende Nutzung ist kein Abzug; neue Story-Art „Vergessene Schätze“ | Herbert 2026-10-22; Mockup folgt |
| ✅ | **Sicherheits-Check (versteckte Bibliothek)** | prüft Windows-Spuren (BitLocker C:, Ruhezustand, Schnellstart, Auslagerungsdatei, Verlauf, Miniaturbilder, Indizierung, VeraCrypt-Verlauf), öffnet die Windows-Einstellung, ändert selbst nichts; Option „nach dem Zusperren beenden“ | Mockup 14, ADR-0064, umgesetzt in 0.4.28 |
| ✅ | **Aktivitäts-Anzeige** | alle Hintergrund-Vorgänge unten rechts mit Fortschritt, Liste, Pause/Abbrechen, Rückgängig | Mockup 15 (abgenommen 2026-10-25), ADR-0067, umgesetzt in 0.4.36 |
| ✅ | **Ausschlüsse beim Einlesen** | allgemeine Liste (.thumbs, .temp …) + je Bibliothek, Rechtsklick „Ordner ausschließen“, Bestehende nach Rückfrage aus dem Katalog entfernen | Herbert 2026-10-25, ADR-0066, umgesetzt in 0.4.33 |
| ✅ | **Ordner verschieben** | Reiter Ordner: „Verschieben nach…“ und Ziehen, mit Rückgängig | Herbert 2026-10-25, ADR-0066, umgesetzt in 0.4.33 |
| ✅ | **Tag- und Kategorie-Symbole** | kleine Marken auf Galerie-Kacheln, in der Lightbox und im Ordnerbaum, Hover mit Namen, Schalter je Ort | Mockup 16 (abgenommen 2026-10-25), umgesetzt 0.4.35 |
| ✅ | **Personen-Kacheln, Titelbild, Rechtsklick-Menü** | Chips bestätigt/Vorschläge, „Als Titelbild für“ überall mit automatischer Wahl, Kontextmenü mit Untermenüs | Mockup 17 (abgenommen 2026-10-25), Issue #15, ADR-0068, umgesetzt in 0.4.44 |
| ✅ | **Galerie mit Ordnerleiste** | Ordnerbaum als Leiste in der Galerie, Ordner als Filter-Chip, Menüpunkt „Ordner“ entfällt | Mockup 18 (abgenommen 2026-10-25), Issue #24, ADR-0068, umgesetzt in 0.4.44 |
| ✅ | **Fotos in andere Programme kopieren** | Ziehen nach außen und Strg+C als Datei-Kopie, nie verschieben, versteckte Bibliothek gesperrt | Herbert 2026-10-25, Issue #25, ADR-0068, umgesetzt in 0.4.44 |
| ⬜ | **Welle 7 Android-Begleit-App** | PC stellt Katalog + Vorschaubilder im Heimnetz bereit, Handy zeigt und bewertet | Memory/Plan |
| ⬜ | **Welle 8 Foto verbessern** | hochskalieren, Gesichter restaurieren, einfärben, entfernen, Zuschneiden/Licht; Ergebnis als neue Datei; Augen-offen/Lächeln als Gütemerkmal | Mockup 13 (abgenommen 2026-10-23) |
| 🟡 | **Produktivbetrieb** | Katalog-Sicherung vor Migrationen (konsistent, versteckt nur im Container, ohne Sicherung keine Migration), von Hand + Wiederherstellen beim nächsten Start, portabler Modus (`lumora.portable`/`Daten\` neben der EXE, `publish.ps1 -Portable`) für eine getrennte Release-Installation mit frischem Katalog, Datenordner in Info; offen: Sicherung der Fotos (Herberts Entscheidung, README „Produktivbetrieb“) | Issue #6, ADR-0069, umgesetzt in 0.4.40 |
| ✅ | **Update-Manager „Modelle & Daten“** | alle Modelle und Datenpakete mit Zweck, Größe, Version, Quelle, Lizenz und Zustand; Laden nur nach Bestätigung mit SHA-256 und Fortschritt in der Aktivitäts-Anzeige, Prüfen, Entfernen, „Update suchen“ nur auf Klick; Register für die Modelle von „Foto verbessern“ | Mockup 19 (Entwurf, Abnahme offen), ADR-0070, Issue #19, umgesetzt in 0.4.42 |

---

## 8. 6 Pflicht-Spikes

### Reihenfolge: blockierend zuerst

| # | Spike | PT | Blockiert | Messziele |
|---|---|---|---|---|
| 1 | **SQLite Bulk + FTS5 500k** | 2–3 | Schema, Scanner, Search-UI | Bulk-Insert <5min SSD/<15min HDD; Folder-Query 500 Items <100ms; FTS-Name-Query <200ms |
| 2 | **Virtual Grid 500k** | 2–4 | Gallery-UI, Selection | Start <1s; Page-Switch <150ms; Scroll flüssig; Memory stabil über 10min |
| 3 | **LibVLCSharp WPF Lightbox** | 3–5 | MP4 in V0.1 (sonst Cut auf "Extern öffnen") | Player-Lifecycle, Fullscreen, Focus, Multi-Monitor, Dispose ohne Leaks |
| 4 | WIC Benchmark + File-Lock | 1–2 | Thumb-Pipeline | 1k echte JPGs parallel, kein File-Lock-Leak |
| 5 | FFmpeg Cancel/Timeout | 1–2 | Video-Thumbs | Process-Kill bei kaputten MP4s, Timeout 3s/8s |
| 6 | Storage Detection | 1–2 | StorageProfileService | SSD/HDD/USB/NAS-Erkennung, UNC-Timeout, Offline-Verhalten |

### Gesamt-Spike-Aufwand: 10–18 PT (2–4 Wochen wenn parallel zur V0.1-Arbeit unmöglich)

---

## 9. Tag-1–3-Implementation-Plan

### Tag 1 — Fundament

**Ziel:** Solution startbar, DI steht, leeres MainWindow läuft.

1. Solution + Projekte anlegen (Domain, Data, Services, Wpf, Tests)
2. Target Framework: `net10.0-windows` für Wpf, `net10.0` für Rest
3. Central Package Management (`Directory.Packages.props`)
4. DI/HostBuilder in WPF App integrieren
5. Serilog Logging einrichten (Rolling File)
6. `AppDataPaths`-Service:
   - DB: `%LocalAppData%\Lumora\library.db`
   - Thumbs: `%LocalAppData%\Lumora\thumbs\`
   - Logs: `%LocalAppData%\Lumora\logs\`
7. Leeres MainWindow mit WPF-UI NavigationView
8. Smoke-Run

**Definition-of-Done:**
- App startet
- MainWindow sichtbar
- Logfile geschrieben
- DI funktioniert
- DB-Pfad geloggt
- Keine fachliche Funktionalität nötig

### Tag 2 — SQLite/FTS/Bulk Spike (vor UI weitermachen!)

**Ziel:** 500k synthetische FileRows einfügen, Bulk-Performance messen.

1. 500k synthetische Files generieren (80% jpg/png, 20% mp4, 10k Folders)
2. Realistische Namen: `IMG_20260510_123456.jpg`, `Urlaub_Italien_001.mp4`
3. Bulk-Insert via `Microsoft.Data.Sqlite` Prepared Command in Transaction
4. Indizes anlegen
5. FTS5 befüllen
6. Queries messen (Folder-Page, FTS-Name, Filter)

**Messziele:**
- Bulk-Insert 500k: <5 min auf SSD, <15 min HDD
- Folder-Query 500 Items: <100ms
- FTS-Name-Query: <200ms
- Filter MediaType-Page: <100ms

**Wenn Werte nicht erreichbar** → Data-Design anpassen, bevor UI gebaut wird.

### Tag 3 — Virtual Grid Spike

**Ziel:** WPF Shell + paged GalleryView.

1. WPF Shell zeigt GalleryView
2. QueryService liefert DTO-Pages (200–1000 Items)
3. Grid rendert Pages mit Platzhalter-Thumbs (noch keine echten)
4. Scroll/Filter/Search-Simulation
5. SelectionStore per FileId

**Messziele:**
- Start Gallery <1s nach DB bereit
- Page-Switch/Load <150ms
- Scroll sichtbar flüssig
- Memory stabil nach 10min Scrollen
- Keine 500k ObservableCollection

**Wenn Standard-Panel nicht reicht** → Custom/Third-Party prüfen, alternative UI-Strategie.

### Tag 4+ — danach in dieser Reihenfolge
4. LibVLCSharp Spike (3–5 PT) — entscheidet V0.1-Cutline
5. WIC + FFmpeg Thumbnail-Spikes
6. Storage-Detection-Spike
7. **Erst dann:** echte V0.1-Implementation in vertikalen Slices

---

## 10. NuGet-Pakete

### Directory.Packages.props (Central Package Management)

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- EF Core + SQLite -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.7" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.7" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.7" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.7" />

    <!-- Hosting + DI -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.7" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.7" />

    <!-- MVVM + WPF -->
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="WPF-UI" Version="4.0.0" />

    <!-- Video -->
    <PackageVersion Include="LibVLCSharp.WPF" Version="3.9.7.1" />
    <PackageVersion Include="VideoLAN.LibVLC.Windows" Version="3.0.21" />

    <!-- Logging -->
    <PackageVersion Include="Serilog.Extensions.Hosting" Version="9.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="6.0.0" />

    <!-- Tests -->
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="FluentAssertions" Version="8.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
  </ItemGroup>
</Project>
```

### Hinweise
- **Kein FFMpegCore** — eigener `ProcessStartInfo`-Wrapper für volle CancelAfter/Kill-Kontrolle
- **WpfAnimatedGif** erst in Phase 8 (nicht V0.1)
- **SharpCompress** erst in Phase 8 (nicht V0.1)
- **Microsoft.ML.OnnxRuntime** erst in Phase 9

---

## 11. Storage-Profile

### Konzept-Trennung
```
StorageAvailability = Library-Root erreichbar? (Online/Offline)
StorageProfile      = Wie aggressiv arbeiten? (SSD/HDD/NAS)
```

### 3 konkrete Profile

#### LocalSsd
```
ScanBatchSize:                  5000
DbInsertBatchSize:              5000
MaxImageWorkers:                max(2, CPU-2)
MaxVideoWorkers:                2 (CPU≥8) / 1
MaxConcurrentDiskReads:         6
BackgroundBatchDelayMs:         0–10
FtsBatchSize:                   5000
ThumbnailQueueBatchSize:        2000
AggressiveBackgroundThumbs:     true
```

#### LocalHddOrUsb (auch Default bei Unsicherheit)
```
ScanBatchSize:                  1000–2000
DbInsertBatchSize:              1000–2000
MaxImageWorkers:                2
MaxVideoWorkers:                1
MaxConcurrentDiskReads:         2
BackgroundBatchDelayMs:         50–150
FtsBatchSize:                   2000
ThumbnailQueueBatchSize:        500–1000
AggressiveBackgroundThumbs:     true (limitiert)
```

#### NetworkShare (NAS / UNC)
```
ScanBatchSize:                  500–1000
DbInsertBatchSize:              1000
MaxImageWorkers:                1–2
MaxVideoWorkers:                0 (on-demand only)
MaxConcurrentDiskReads:         1–2
BackgroundBatchDelayMs:         150–500
FtsBatchSize:                   1000
ThumbnailQueueBatchSize:        250–500
RootAvailabilityCheckTimeoutMs: 1500–3000
AggressiveBackgroundThumbs:     false  ← Bilder ja, Videos nur on-demand
```

### Detection-Strategie
- `GetDriveType` für Remote/Removable/Fixed
- UNC-Erkennung: `path.StartsWith("\\\\")`
- SSD/HDD optional via WMI `MSFT_PhysicalDisk.MediaType`
- **Bei Unsicherheit konservativ als HDD/USB** (Performance verlieren, nicht Vertrauen)
- **DeviceIoControl-Komplexität** erst später, nicht im MVP

---

## 12. Offline-vs-Missing-Guard

### Grundregel (kritisch!)
> Nur wenn LibraryRoot erreichbar ist, dürfen einzelne Files als Missing markiert werden.
> Wenn LibraryRoot nicht erreichbar: Library offline, KEIN File-Missing ändern.

### Algorithmus
- `CheckLibraryAvailabilityAsync` mit Timeout (UNC 3s, lokal 1s)
- Vor jedem Scan-Batch: Root-Recheck
- Bei IOException während Scan: Root-Recheck → wenn offline, Mark Library Offline, return
- **Mass-Missing-Threshold:** bei `>10.000` ODER `>20%` der Library NICHT auto-committen → UI-Dialog
- **Mehrere Wurzeln (ADR-0020, ab 0.2.5):** Erreichbarkeit, Root-Recheck und Mass-Missing-Guard gelten je Wurzel.
  Fresh/ClearAndRescan starten nur, wenn alle Wurzeln erreichbar sind; der Incremental-Scan überspringt eine
  Offline-Wurzel (keine Missing-Markierung dort), gleicht die übrigen ab und meldet `LibraryOffline` mit ihrem Pfad.
- **Neu aufbauen (ADR-0028, ab 0.2.24):** der bisherige Katalog bleibt während der Enumeration unangetastet; Sichern
  der Benutzerdaten, Löschen der alten Zeilen, Einfügen, Zurückschreiben und Suchindex laufen in einer Transaktion —
  Abbruch, Offline-Fehler, Ausnahme oder Absturz davor lassen Katalog und Benutzerdaten unverändert.

### UI bei Storage offline
```
Bibliothek offline: E:\Fotos ist nicht erreichbar.
Bitte Laufwerk anschließen oder Netzwerkverbindung prüfen.
[Erneut prüfen] [Andere Bibliothek wählen]
```

### UI bei Threshold-Überschreitung
```
Ungewöhnlich viele Dateien fehlen
Beim Scan wurden 142.381 fehlende Dateien erkannt.
Das kann passieren, wenn ein Laufwerk nicht vollständig verfügbar ist.
[Erneut prüfen] [Als fehlend markieren] [Scan abbrechen]
```

---

## 13. Thumbnail-Pipeline

### Speicherort
`%LocalAppData%\Lumora\thumbs\{libraryId}\{fileId}_{size}_{kind}.jpg` — bzw. `{Datenordner}\thumbs\…`, wenn der
Katalog in den Einstellungen umgezogen ist: der Cache liegt immer neben `library.db` und zieht mit (ADR-0026).

- **Naming**: `42_400_still.jpg` für File-ID 42, statisches Thumb mit kurzer Kante 400 px (`Thumbnails.Size` = 400)
- **Animierte (Phase 8)**: `42_400_hover.gif`
- **Größe (ADR-0043, ersetzt „160 / 320 / 640“)**: eine Größe — kurze Kante 400 px, lange höchstens 1200 px, nie
  hochskalieren, EXIF-Drehung, JPEG-Qualität 82 (`ThumbnailSizing`; WIC und FFmpeg gleich). Spike #11: ≈ 5,5 GB je
  100 000 Fotos (≈ 27 GB bei 500 000), Erzeugung ≈ 28–34 min je 100 000 mit 4 Workern.
- **Laden in der Galerie**: Dekodierfläche = Bildbereich der Kachel × Bildschirm-Skalierung; bei deutlich größerer Fläche
  (> 15 %) werden nur die zu kleinen Kacheln im Hintergrund neu dekodiert.
- **Übergang alter Größen** (320 px): zeigen, bis das neue fertig ist, dann Zeile (gleiche Transaktion) und Datei löschen;
  Pflege im Hintergrund ersetzt den Rest in 32er-Stapeln bei leerer Warteschlange (nicht bei Pause, Scan-Sperre,
  fehlender Quelle, Cloud-Platzhalter), Fortschritt in `PendingCount`.
- **Bildmaße**: beim Erzeugen `Files.Width/Height` = Original nach EXIF-Drehung (gleiche Transaktion wie „Generated“;
  Videos aus der FFmpeg-Eingabe); fehlende trägt die Pflege aus dem Dateikopf nach (0 × 0 = nicht lesbar).
- Validation via DB: `Files.ModifiedAtUtc != Thumbnails.SourceModifiedAtUtc` → Re-generate

### Multi-Channel Queue (4 Prios)

| Prio | Trigger |
|---|---|
| 🔴 Critical | Lightbox-Bild offen (640px + Prefetch ±2) |
| 🟠 Viewport | sichtbare Items im Grid (debounced 100ms) |
| 🟡 Current Folder | geöffneter Ordner, außerhalb Viewport |
| 🟢 Background | Erst-Indexierung, andere Ordner |

### Mechanik
- 4 separate `Channel<ThumbJob>` (System.Threading.Channels)
- `ConcurrentDictionary<ThumbKey, ThumbJobState>` für Dedupe + Versioning
- Stale-Detection beim Dequeue
- 1 Job = 1 Datei → Pause-Punkt zwischen Jobs (kein Hard-Cancel mitten in WIC)
- FFmpeg-Jobs: `CancellationTokenSource.CancelAfter(3s/8s)` für Timeout
- Bilder: fertiglaufen lassen
- Worker-Pool: Image-Worker `max(1, CPU-2)`, FFmpeg-Worker max 1–2
- Disk-Concurrency: `SemaphoreSlim` limitiert Parallel-Reads
- **Background batched 1000–5000**, nicht alle 500k auf einmal enqueuen

### Status-Tabelle
```sql
Thumbnails.Status: missing | pending | generated | failed | stale
Thumbnails.AttemptCount, LastAttemptAtUtc, ErrorCode, ErrorMessage
```
Crash-Recovery: beim App-Start `UPDATE Thumbnails SET Status='stale' WHERE Status='pending';`

### Atomic Write
1. Generate to `{file_id}_{size}_{kind}.jpg.tmp`
2. `File.Move(tempPath, finalPath, overwrite: true)`
3. Halbfertige Files niemals als final sichtbar

### WIC-Encoder (V0.1)
- `BitmapDecoder.Create` mit `BitmapCacheOption.OnLoad` (Stream-Lock vermeiden)
- `FileShare.ReadWrite | FileShare.Delete` (Cloud-Sync-tolerant)
- `BitmapSource.Freeze()` vor Übergabe
- Niemals `ImageSource` im Service speichern → nur Pfad/ID

---

## 14. Migration-Disziplin

### Grenze
**Erste produktive Nutzung** — nicht "letzter Tag der Entwicklung".

- Bis dahin: `EnsureDeleted/EnsureCreated` OK, DB-Reset frei
- Ab dann: nur `Database.MigrateAsync()`
- Spätestens ab V0.2

### Auto-Migration beim App-Start

```
App-Start
  → DB-Version prüfen
  → Wenn Migration nötig:
    → Dialog "Datenbank wird aktualisiert"
    → WAL Checkpoint (`PRAGMA wal_checkpoint(TRUNCATE)`)
    → Versioniertes Backup: library.db.backup-YYYYMMDD-HHMMSS-vX.Y.Z-to-vX.Y.Z+1
    → Migration ausführen (in Transaktion soweit möglich)
    → Schema-Version erst am Ende setzen
    → Bei Erfolg: normal weiter
    → Bei Fehler: Restore-Dialog mit Backup-Pfad + Log
```

### Umsetzung seit 0.4.40 (ADR-0069)
- `DatabaseInitializer` → vorgemerkte Wiederherstellung zuerst, dann bei `pending > 0 && applied > 0` Sicherung per
  SQLite-Online-Backup-API nach `<App-Wurzel>\backups\library-<Version>-<yyyyMMdd-HHmmss-fff>.db` (versteckt
  `.lumora\backups`), erst danach `MigrateAsync`. Kein Checkpoint/keine Dateikopie nötig (die Backup-API nimmt den WAL mit).
- Sicherung gescheitert → **keine Migration**, Startabbruch mit Meldung, Katalog unverändert.
- Aufbewahrung: 5 vor Migration, 10 von Hand, 3 vor Wiederherstellung. Wiederherstellen nur aus den Einstellungen als
  Vormerkung für den nächsten Start (alter Katalog als `…-vor-wiederherstellung.db`); ein Restore-Dialog direkt nach einem
  Migrationsfehler ist nicht gebaut — die Sicherung liegt vor, Wiederherstellen geht über eine ältere Version bzw. von Hand.

### Bei Migrationsfehler
```
Datenbank-Aktualisierung fehlgeschlagen
Backup wurde erstellt: ...\library.db.backup-...
Fehler: <Message>
[Backup wiederherstellen] [App schließen] [Log öffnen]
```
**Nie mit halb migrierter DB normal starten.**

---

## 15. Risiken & Mitigation

| Risiko | Mitigation |
|---|---|
| **Virtual Grid 500k zu langsam** | Spike #2 vor Implementation, Fallback auf Custom-Panel oder Third-Party |
| **LibVLCSharp WPF-Interop instabil** | Spike #3 mit Cap auf 5 PT, Cutline auf "Extern öffnen" |
| **NAS bei Scan offline → False-Missing** | Offline-Guard mit 20%-Threshold (Pflicht-Architektur) |
| **EF Core Initial-Scan zu langsam** | Raw `Microsoft.Data.Sqlite` Prepared INSERT (60–80% schneller) |
| **Thumbnail-Queue Race-Conditions** | Versioning + Dedupe-Dictionary + Stale-Detection beim Dequeue |
| **Memory-Leaks bei langer Laufzeit** | Niemals `ImageSource` in Services, `BitmapCacheOption.OnLoad`, `Freeze()`, EF ChangeTracker.Clear() |
| **Concurrent SQLite-Writes** | WAL + Single-Writer-Pattern via Channel oder `SemaphoreSlim` |
| **FFmpeg hängt bei kaputten Videos** | `CancellationTokenSource.CancelAfter(3s/8s)` + Process.Kill |
| **Katalog-Umzug verliert Daten** | Kopie per Backup-API, `integrity_check` + Zeilenzahlen, Umschalten erst danach; Scans/Batches/Überwachung/Thumbnails ruhig, Factory gesperrt (auch für ältere Kontexte), Schreibsperre der alten DB gehalten; Neustart statt Laufzeit-Umschaltung (ADR-0026) |
| **Versteckte Bibliothek hinterlässt Spuren** | Eigener Katalog + Logger je Sitzung im Container, Container-Wechsel statt Umschalten einzelner Dienste, Shell-Aufrufe (Explorer, Standard-App) gesperrt, `AddToRecent = false`, Spurenprüfung in Unit- und Rauchtest (ADR-0049); Grenzen: Dateidialog-MRU, VeraCrypt-Verlauf, Auslagerungsdatei |
| **Background-Worker blockiert UI** | WorkBudgetService mit StorageProfile-Limits + UI-Activity-Delay |

---

## 16. UI/UX

### Layout
```
┌─────────────────────────────────────────────────────────┐
│ Custom Title Bar (Mica)                       — □ ×    │
├──────────┬──────────────────────────────────────────────┤
│ NavView  │ CommandBar                                   │
│          ├──────────────────────────────────────────────┤
│ Logo     │ Breadcrumb / Page-Header                     │
│ Search   │ Filter-Pills (Alle | Bilder | Videos | ⭐)   │
│          │ InfoBar (optional)                           │
│ Library  ├──────────────────────────────────────────────┤
│ Folders  │                                              │
│          │   Paged virtualisiertes Thumbnail-Grid       │
│          │   (200–1000 Items pro Page)                  │
│          │                                              │
│ Settings │                                              │
├──────────┴──────────────────────────────────────────────┤
│ Statusbar: Scan / Thumbs / Background / Recovery        │
└─────────────────────────────────────────────────────────┘
        Floating Action Bar (bei Multi-Selection)
```

### Stil
- Fluent / WinUI 3 (Mica-Hintergrund, Akzent-Schimmer)
- Akzent: `#5B5FC7` hell / `#818CF8` dunkel
- Schrift: Segoe UI Variable
- Icons: Segoe Fluent Icons (kein Emoji-Mix)
- Light + Dark Theme

### Lightbox (eigenes Window)
- Rahmenlos / immersive
- Bild-Zoom (Mausrad, +/-, F=Vollbild, 1=100%, 0=Reset, Drag=Pan)
- Navigation: ← → (Pfeiltasten **und** Buttons), Bild↑↓ (±10), Pos1/Ende
- Filmstrip unten
- EXIF-Info-Panel rechts (toggle via I)
- LibVLCSharp für MP4 (V0.1 wenn Spike grün)
- Esc / Doppelklick = schließen
- Multi-Monitor unterstützt durch eigenes Window

### Mockups
- [mockups/04-fluent-light.html](mockups/04-fluent-light.html) — favorisiert hell
- [mockups/05-fluent-dark.html](mockups/05-fluent-dark.html) — favorisiert dunkel
- [mockups/03-lightbox.html](mockups/03-lightbox.html) — Lightbox-Layout

### Container-State (wichtig!)
**Klick auf Ordner ohne direkte Medien:** zeigt **Subfolder-Kacheln** statt leeres Grid (vermeidet "kaputt"-Eindruck).

### Plan-Ordner-UI (V0.3)
- Eigene NavView-Section "Sortierpläne"
- Two-Panel-Layout: Quelle (Galerie) links, Plan-Tree (Drop-Zonen) rechts
- Drag&Drop + Kontextmenü-Zuweisung
- Plan-Ordner-Badge: `[42 / 3 Konflikte]`
- Visuelle Unterscheidung physisch vs. virtuell (gestrichelte Kontur, "Virtuell"-Banner)
- Apply mit Preflight-Dialog

---

## Anhang: Review-Quellen

3 ChatGPT-Review-Runden mit GPT-5 (alle archiviert in `chatgpt-review/`):

- Runde 1: Architektur-Stack, DB-Schema, UI/UX, Migrations-Strategie, erweiterte Features
- Runde 2: WIC-Tiefe, Multi-Channel-Queue komplett, OperationBatches-Recovery, Animated-Hover-Lifecycle, MVP-Schnitt für 50k–500k, WPF-VirtualPanel-Limits, WorkBudgetService
- Runde 3: Aufwand-Reality-Check (8–10 Wochen), Storage-Profile-Detection, Migration-Disziplin, finale 15 ADRs, Tag-1–3-Plan, Offline-vs-Missing-Guard

---

**Plan-Status:** ✅ Final, bereit für Implementation
**Nächster Schritt:** Solution-Skeleton anlegen (Tag 1) oder erste blockierende Spike starten (Tag 2)
