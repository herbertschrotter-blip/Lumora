# ADR-0046: Aufnahmedatum im Katalog — gelesen im Hintergrund, Sortierung danach, selbst setzbar

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0004](0004-datenzugriff-ef-core-raw-sqlite.md) (Raw-SQLite für Bulk), [ADR-0012](0012-operation-logging.md)
  (Protokoll), [ADR-0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md) (Benutzerdaten über „Neu aufbauen“),
  [ADR-0025](0025-scan-sperre-fuer-datei-operationen.md) (Scan-Sperre), [ADR-0026](0026-speicherort-katalog-waehlbar.md)
  (Umzug), [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md) (Sortierung Feld + Richtung),
  [ADR-0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md) (Pflege im Hintergrund), [Spike #12](../spikes/12-aufnahmedatum.md)

## Kontext

Die Galerie sortierte nach dem Änderungsdatum der Datei (`Files.ModifiedAtUtc`). Bei kopierten, aus der Cloud geladenen
oder alten Fotos ist das oft der Tag des Kopierens. Herbert will das echte Aufnahmedatum — als Grundlage für Zeiträume
(„Kindheit bis 20“) und später Orte/Storys. Alte Scans haben kein EXIF-Datum; dort soll man das Datum selbst setzen
können, auch ungefähr („ca. 1982“). Kataloge haben 50 000–500 000 Dateien; der Scan darf höchstens ≈ 10 % langsamer
werden. Domain/Data/Services dürfen kein WPF kennen (WIC lebt im Wpf-Projekt).

## Entscheidung

1. **Schema** (Migration `AddCapturedAt`): `Files.CapturedAt` (Unix-Sekunden **UTC**, wie `ModifiedAtUtc`),
   `CapturedAtSource` (NULL = noch nicht gelesen; 0 unbekannt, 1 EXIF, 2 Video, 3 Dateiname, 4 manuell),
   `CapturedAtPrecision` (0 genau, 1 Tag, 2 Monat, 3 Jahr), `CapturedAtOffset` (Minuten Ortszeit − UTC). Angezeigt wird
   die **Wanduhrzeit der Aufnahme** (UTC + Abstand): aus EXIF `OffsetTimeOriginal` bzw. Apple-`creationdate`, sonst die
   Zeitzone dieses PCs zum Aufnahmedatum. Ungefähre Angaben liegen für die Sortierung in der Mitte des Zeitraums (Tag
   12:00, Monat → 15., Jahr → 1. Juli). Sortier-Index **`IX_Files_Library_Captured ON Files(LibraryId,
   COALESCE(CapturedAt, ModifiedAtUtc) DESC, Id DESC)`** — ein Ausdrucks-Index per Raw-SQL in der Migration (EF bildet
   ihn nicht ab; `DatabaseInitializer` legt ihn bei jedem Start idempotent an, falls eine spätere Migration Files neu aufbaut; `CapturedAtPrecision` hat den DB-Default 0, damit Raw-INSERTs sie weglassen dürfen). Id gehört dazu, sonst sortiert SQLite
   gleiche Zeitpunkte nach („USE TEMP B-TREE FOR LAST TERM“).
2. **Lesen: verwalteter Parser statt WIC** (`Services/Metadata`, keine NuGet-Abhängigkeit, kein MetadataExtractor):
   erkannt am Dateikopf — JPEG (APP1 „Exif“), TIFF und TIFF-basierte Rohformate (DNG, CR2, NEF, ARW, ORF, RW2), Fuji
   RAF, PNG (`eXIf`), WebP (`EXIF`), HEIC/HEIF/AVIF (ISO-BMFF `meta/iinf` + `iloc` → Element „Exif“), MP4/MOV/3GP
   (`moov/meta` Apple `com.apple.quicktime.creationdate` mit Zone vor `moov/mvhd` als UTC). Gelesen werden nur Kopf und
   Verzeichnisse per Seek (bei Videos nie der Medieninhalt). Nur `DateTimeOriginal` zählt — `DateTimeDigitized` und
   IFD0-`DateTime` sind bei Scans das Scan- bzw. Bearbeitungsdatum. Rückfall: **Dateiname** (`IMG_20240712_153012`,
   `PXL_…`, `20240712_153012`, `Screenshot 2026-09-30 105535`, `2024-07-12 15.30.12`, `PHOTO-2024-07-12-15-30-12`,
   WhatsApp `IMG-20240712-WA0001` = nur Tag), Ziffernfolge nicht Teil einer längeren Zahl, keine Zukunft. Ohne alles:
   Herkunft „unbekannt“, Sortierung über das Änderungsdatum (Tooltip „unbekannt (sortiert nach Änderungsdatum)“).
   *Begründung gegen WIC:* WIC bräuchte eine Implementierung im Wpf-Projekt hinter einem Services-Interface, liest HEIC
   nur mit installierter HEIF-Erweiterung und Videos gar nicht; der verwaltete Leser ist laut Spike #12 schneller und
   liefert für alle verglichenen JPEGs dasselbe Datum wie die WIC-Metadatenabfrage.
3. **Wann gelesen: im Hintergrund direkt nach dem Scan, nicht im Scan selbst.** Der Scan schreibt neue Zeilen mit
   `CapturedAtSource = NULL`; ein Durchgang des Nachtragens (`CaptureDateService.Backfill`) startet nach jedem Scan
   (`ScanCompletedMessage`), bei neuen Dateien (`FilesChangedMessage` „Added“) und 15 s nach dem App-Start — für
   bestehende Kataloge einmalig über alle Dateien. Stapel zu 200 nach FileId, **eine Transaktion je Stapel**
   (Single-Writer, Wiederholung bei gesperrter DB), geschrieben nur für die gelesene Dateiversion (`ModifiedAtUtc`) und
   nur, solange die Zeile noch offen ist — ein inzwischen manuell gesetztes Datum bleibt. Ruht, solange die Scan-Sperre
   die Bibliothek belegt (Scan, Datei-Batch, Umzug — der Umzug belegt alle Bibliotheken) oder die Factory gesperrt ist;
   **Cloud-Platzhalter, fehlende Dateien und Dateien nicht erreichbarer Wurzeln (Prüfung mit Timeout wie beim Scan) werden übersprungen** (kein OneDrive-Download, kein Hängen am Netzwerk) und bleiben offen.
   Fortschritt in der Statusleiste („Aufnahmedatum: 1.234 / 19.556“), Ergebnis als `CaptureDatesChangedMessage`
   (Galerie lädt bei Sortierung nach Aufnahmedatum bzw. betroffenen Kacheln neu — Zwischenstände höchstens alle 30 s).
   *Begründung:* Lesen im Scan kostete laut Spike #12 ein Vielfaches der Scan-Zeit (jede Datei öffnen statt nur
   aufzählen) — weit über der Grenze von ≈ 10 %; der Scan selbst wird nur durch die Pflege des neuen Index langsamer.
4. **Inhalt geändert** (Incremental-Scan, Größe oder Änderungszeit anders): nicht-manuelle Daten werden auf „noch nicht
   gelesen“ zurückgesetzt (gleiche SQL-Anweisung wie das Update), manuelle bleiben. **„Neu aufbauen“** trägt manuelle
   Daten über `UserDataCarryOver` (wie Bewertungen, ADR-0023, gleiche Transaktion) — alle übrigen liest das Nachtragen
   neu (kein Mittragen von 500 000 gelesenen Werten durch die TEMP-Tabellen).
5. **Sortierung:** `GallerySort.CapturedDesc/CapturedAsc` über `COALESCE(f.CapturedAt, f.ModifiedAtUtc)`, Feld
   „Aufnahmedatum“ vorn in der Sortierleiste und **Standard** (die Sortierung wurde bisher nicht gespeichert; Start und
   Bibliothekswechsel beginnen damit nun mit „Aufnahmedatum, neueste zuerst“ — die einmalige Umstellung bestehender
   Einstellungen entfällt). Kombinierbar mit allen Filtern, Suche, Ordner; Pläne ohne Vollscan (Test), 500 000 Dateien
   gemessen (Spike #12). Die Kategorie-Sortierung ordnet gleiche Werte weiter nach Änderungsdatum.
6. **Datum setzen…** (Aktionsleiste, Rechtsklick, Ordnerbaum mit „Unterordner einschließen“): genau, ungefähr (Jahr bzw.
   Monat + Jahr), verschieben (± Tage/Stunden/Minuten; Basis ist das bisherige Datum — noch nicht gelesene Dateien werden dafür zuerst aus der Datei gelesen, ohne Datum das Änderungsdatum —, Genauigkeit
   bleibt) oder zurücksetzen (wieder lesen). Schreibt **nur den Katalog** (EXIF-Schreiben folgt mit „Orte“). Protokoll:
   `OperationBatch` vom Typ `SetCaptureDate` (7) mit einer `FileOperation` je geänderter Datei und den alten Werten in
   der neuen Tabelle `CaptureDateUndo` (1:1 zur Operation, Kaskade). **Rückgängig** in der Galerie nimmt den jüngsten
   rückgängig machbaren Eintrag zurück — Datei-Operation oder „Datum setzen“ (Vergleich über die Batch-Id); nach
   „Neu aufbauen“ findet es die Dateien über Ordnerpfad + Name. Der `FileOperationService` bleibt unverändert (sein
   Undo kennt Typ 7 nicht und meldet ihn als nicht rückgängig machbar).
7. **Anzeige:** Lightbox-Info „Aufgenommen: 12.07.2024 15:30 (EXIF)“ bzw. „ca. 1982 (manuell)“, Kachel-Tooltip,
   Listenspalte „Aufgenommen“ (Kopfklick sortiert die ganze Trefferliste).

## Konsequenzen

- + Echte Chronologie für alle Fotos und Videos mit Metadaten oder sprechendem Dateinamen; alte Scans lassen sich
  ungefähr datieren und sortieren mit.
- + Scan bleibt schnell; das einmalige Nachtragen läuft im Hintergrund und lässt Scans, Datei-Operationen und den
  Umzug vor.
- − Bis das Nachtragen fertig ist, sortieren neue bzw. noch nicht gelesene Dateien nach ihrem Änderungsdatum (bei
  kleinen Scans Sekunden, beim ersten Lauf über 500 000 Dateien einige Minuten).
- − Der Ausdrucks-Index ist EF unbekannt — Migrationen, die Files neu aufbauen, verlieren ihn bis zum nächsten Start (dort wieder angelegt).
- − Video-Container ohne Zone (`mvhd`) gelten als UTC; Kameras, die dort Ortszeit schreiben, liegen um den Zonenabstand
  daneben. AVI, MKV, MTS, WMV, Canon CR3 und Dateien nur in der Cloud bekommen vorerst nur das Dateinamen-Datum bzw.
  bleiben offen.
- − Manuelle Daten stehen nur im Katalog, nicht in der Datei (bewusst bis „Orte“).

## Nachtrag 0.5.41 — Herkunft 5 „OneDrive, vorläufig“ (ADR-0077)

`CapturedAtSource` bekommt den Wert **5 (`CaptureDateSource.Cloud`)**: das Aufnahmedatum eines OneDrive-Platzhalters aus dem
Eigenschaftsspeicher von Windows (ohne Download, [ADR-0077](0077-onedrive-bibliothek.md)). Windows liest
`DateTimeOriginal` als Ortszeit dieses PCs und übergeht `OffsetTimeOriginal` (nativ gemessen an 280 lokalen Fotos:
Wanduhrzeit 280 von 280 gleich, UTC bei den 14 Fotos mit abweichendem Zeitversatz 0 von 14). Der Wert gilt deshalb nur
vorläufig: Sobald die Datei lokal ist (Meldung `CloudFilesChangedMessage`, sonst einmal je Sitzung über die Attribute
geprüft), setzt das Nachtragen die Herkunft auf NULL — das Datum bleibt bis dahin stehen — und liest es aus der Datei;
nennt die Datei selbst keins (oder nur eins aus dem Dateinamen), bleibt das vorläufige.
Vorläufige Daten zählen beim Nachtragen nicht als offen. Anzeige „(OneDrive, vorläufig)“; Doppelte werten sie wie EXIF.
Keine Schema-Änderung (die Spalte ist eine Zahl).
