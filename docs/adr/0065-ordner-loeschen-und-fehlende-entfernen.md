# ADR-0065: Ordner in den Papierkorb und fehlende Dateien bewusst aus der Bibliothek entfernen

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Herbert Schrotter (Wunsch und Grundentscheidungen: Papierkorb statt Löschen, Rückfrage ohne Papierkorb,
  Fehlende an drei Stellen, Offline-Guard, Zahlen in der Bestätigung), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage)
- **Bezogen auf:** [ADR-0012](0012-operation-logging.md) (Protokoll), [ADR-0018](0018-datei-operationen-nur-ueber-service.md),
  [ADR-0019](0019-incremental-scan.md) („Missing statt Löschen“, Offline-Guard), [ADR-0020](0020-bibliothek-mehrere-wurzeln-rollen.md),
  [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich, „Alle verknüpften“), [ADR-0049](0049-versteckte-bibliotheken.md),
  [ADR-0053](0053-doppelte-erkennen.md) (`CatalogRowSnapshot`, Zurückholen aus dem Papierkorb), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md)
  (`FileLinkUndoStore`), PLAN.md Kap. 12

## Kontext

Bisher ließen sich nur einzelne Dateien in den Papierkorb legen; ein ganzer Ordner musste im Explorer gelöscht werden, danach
standen seine Dateien als „fehlt“ im Katalog. „Fehlt“ ist absichtlich (ADR-0019: Laufwerk kann zurückkommen, Benutzerdaten
bleiben) — es gab aber keinen bewussten Weg, wirklich verschwundene Dateien zu vergessen. Herbert wünscht (1) „In den
Papierkorb…“ im Kontextmenü des Reiters „Ordner“ und (2) „Fehlende aus der Bibliothek entfernen“ am Ordner, in der Galerie und
in Einstellungen › Speicher & Wartung — jeweils sicher gegen Offline-Laufwerke, mit Zahlen in der Bestätigung und, wo
machbar, Rückgängig.

## Entscheidung

**Ordner in den Papierkorb** (`IFileOperationService.RecycleFolderAsync`, `FileOperationService.RecycleFolder.cs`):

- Ein **RecycleFolder-Batch** nach ADR-0012: Ordner-Operation (FileId null) + eine Recycle-Operation je katalogisierter Datei,
  dazu je Datei ein **Abzug der Katalogzeile** (`CatalogRowSnapshot`, ADR-0053) und ein Abzug der `Folders`-Zeilen samt
  `FolderLinks` hinein — alles in EINER Transaktion vor dem Dateisystem-Schritt.
- Der Ordner geht **in einem Schritt** in den Windows-Papierkorb (`IRecycleBin.MoveDirectoryToRecycleBin`, dieselben
  Schutzregeln wie bisher: nie hart löschen, Laufwerkswurzel abgelehnt). Danach je Datei, die wirklich weg ist:
  `FileLinkUndoStore.CaptureAsync` (ADR-0054), FTS-`delete` mit alten Werten, Thumbnails-Zeilen, Files-Zeile (Kaskaden),
  Cache-Datei; `Folders`-Zeilen nicht mehr vorhandener Ordner (tiefste zuerst). Gesperrte Dateien bleiben im Katalog
  (Item Failed), der Batch ist dann Applied mit Teilerfolg.
- **Abgelehnt ohne Änderung** (`FolderRemovalRules`, gemeinsam für Vorschau und Ausführung): Wurzel selbst und Ordner, in
  denen eine Wurzel liegt (Hinweis auf Einstellungen › Bibliotheken & Eingänge), Ordner unter keiner Wurzel, Laufwerk nicht
  erreichbar (`RootAvailabilityProbe`), Ordner nicht vorhanden (Hinweis auf „Fehlende entfernen“). „Alle verknüpften“ sperrt
  die Oberfläche (wie die übrigen Datei-Aktionen, ADR-0048).
- **Kein Papierkorb** (`IRecycleBin.GetUnavailableReasonFor`: Netz/UNC, Wechsel- und VeraCrypt-Laufwerke, `SHQueryRecycleBin`
  scheitert): ohne Zustimmung passiert nichts (`NeedsPermanentConsent`). Die Oberfläche fragt eigens „Dieses Laufwerk hat
  keinen Papierkorb — „…“ endgültig löschen?“ mit eigenem Knopf „Endgültig löschen“; erst dann **DeleteFolder-Batch**
  (`Directory.Delete`), ohne Abzüge, nicht rückgängig zu machen.
- **Shell löscht doch endgültig** (Ordner zu groß für den Papierkorb, im Shell-Dialog von `FOF_WANTNUKEWARNING`
  bestätigt): nach dem Schritt prüft `IRecycleBin.ContainsDirectory`, ob der Ordner wirklich im Papierkorb liegt; sonst
  wird der Batch als DeleteFolder gebucht (keine Abzüge, keine `FileLinkUndo`-Sicherung, nicht rückgängig).
- **Teilerfolg:** als „weg“ zählt nur, was vor dem Schritt da war und danach fehlt (bzw. alles, wenn der Ordner weg ist) —
  schon fehlende Dateien eines Ordners, der nicht weg ging, bleiben im Katalog.
- **Ordnerüberwachung:** der ganze Baum gilt während und kurz nach dem Schritt als eigen (`IOwnChangeScope.AddTree`) — kein
  automatischer Diff-Scan, keine „fehlt“-Flut; der Katalog ist ja schon gebucht.
- **Rückgängig** (`UndoBatchAsync`/`UndoLastAsync`, also auch die Galerie-Taste und „Rückgängig“ im Ergebnisfeld des
  Ordnerbaums): Ordner per `IRecycleBin.RestoreDirectoryFromRecycleBin` zurück (das `$R`-Gegenstück eines gelöschten Ordners
  ist ein Ordner; `WindowsRecycleBin` sucht es wie bei Dateien über den `$I`-Kopf und verschiebt es in einem Schritt zurück),
  dann `Folders`-Zeilen mit alten Ids, je Datei die Files-Zeile mit **derselben FileId** und allen abhängigen Zeilen (Tags,
  Kategorien, Gesichter/Personen, Motive, Alben, Ordner-Verknüpfungen, Story-Einträge, Fingerabdrücke …) + FTS, zuletzt die
  Verknüpfungen fremder Dateien in die Ordner; die `FileLinkUndo`-Sicherung wird verworfen. Scheitert der Katalog-Teil, nachdem der
  Ordner schon zurück ist, wird die Ordner-Operation als zurückgenommen festgehalten — ein erneutes Rückgängig holt dann nur
  den Katalog nach. Jede Datei wird in eigener Transaktion zurückgeschrieben; scheitert sie, bleibt ihre Operation offen.

**Fehlende aus der Bibliothek entfernen** (`IFileOperationService.RemoveMissingAsync`, `FileOperationService.RemoveMissing.cs`;
Vorschau/Zahlen `IRemovalService`):

- Nur Katalog, **keine Datei wird angefasst**. Je Datei: nur wenn `Missing = 1`; **Offline-vs-Missing-Guard** je Wurzel —
  unter einer nicht erreichbaren Wurzel bleibt alles („Laufwerk nicht erreichbar — Dateien gelten als offline, nicht als
  fehlend“); unmittelbar vorher **erneut auf Existenz geprüft** — wieder da → nicht entfernen, `Missing` zurücksetzen
  (Metadata-Nachricht).
- **RemoveFromCatalog-Batch**: Batch, eine Operation je Datei und je Datei ein Abzug in EINER Transaktion, danach je Datei in einer
  Item-Transaktion `FileLinkUndoStore.CaptureAsync`, FTS mit alten Werten, Thumbnails-Zeilen, Files-Zeile (Kaskaden),
  Cache-Datei (scheitert die Buchung einer Datei, ist ihre Operation Failed und ihr Abzug verworfen). Danach **leere,
  nicht mehr vorhandene Ordner** aus dem Baum — Kandidaten: Ordner der entfernten Dateien samt
  Elternordnern, bei „am Ordner“ der Ordner samt Unterordnern; nur ohne Dateien, Unterordner-Zeilen und Verknüpfungen, keine
  Wurzel, unter erreichbarer Wurzel, nicht vorhanden; mit Abzug.
- **Rückgängig** wie oben ohne Dateisystem: Zeilen kommen mit derselben FileId zurück, „fehlt“ je nachdem, ob die Datei
  inzwischen da ist.
- **Drei Stellen:** Reiter „Ordner“ › „Fehlende entfernen (n)“ (nur sichtbar bei n > 0, Zähler samt Unterordnern beim Laden
  des Baums), Galerie › Ablage **„Nur fehlende“** (`GalleryLocation.Missing`, `f.Missing = 1`) und „Aus der Bibliothek
  entfernen“ für die Auswahl (nur wenn alle gewählten fehlen, sonst Hinweis), Einstellungen › Speicher & Wartung › Karte
  „Fehlende Dateien“ mit „Alle fehlenden entfernen (n)“ für die aktuelle Bibliothek (in „Alle verknüpften“ gesperrt).
- **Bestätigung mit Zahlen** (`RemovalActions`): n Dateien, davon x mit Sternen/Favorit, y mit benannten Personen, z in
  Alben, w mit Tags/Kategorien — „diese Angaben gehen verloren“, dazu wie viele offline bleiben. Beim Ordner: Dateien auf der
  Platte, davon in der Bibliothek, Unterordner, dieselben Benutzerdaten.
- **Bereich:** Ordner, Auswahl bzw. `scope.LibraryId` des aktuellen Bereichs; versteckte Bibliothek arbeitet ohnehin nur im
  eigenen Katalog der Sitzung, Cache-Dateien über die Katalog-Pfade der Sitzung (`IAppDataPaths`). Protokoll auf Info-Level
  nur mit Zahlen (keine Dateinamen, keine Pfade).

**Rückgängig-Daten ohne neue Tabelle:** Die Abzüge liegen in `DuplicateCleanupUndo` (ADR-0053) mit `KeptFileId = 0`
(`RemovalUndoStore`); die Seite „Doppelte“ filtert `KeptFileId <> 0`. Neue `OperationType`-Werte `RecycleFolder` (9),
`DeleteFolder` (10), `RemoveFromCatalog` (11) — als int gespeichert, kein Schema-Eingriff.

## Begründung

- Der vorhandene Weg (Protokoll, Abzug, Zurückholen aus dem Papierkorb aus ADR-0053) bringt Dateien **mit denselben FileIds
  und allen Angaben** zurück — genau das, was Herbert für Rückgängig erwartet. Ein eigener Ordner-Schritt (statt jede Datei
  einzeln in den Papierkorb) ist schneller, landet als **ein** Eintrag im Papierkorb (im Explorer wiederherstellbar) und
  nimmt Nicht-Medien und leere Unterordner mit.
- `DuplicateCleanupUndo` hat bereits genau die nötige Form (Batch, FileId, Abzug, erledigt-Zeitpunkt, Kaskade am Batch).
  Eine neue Tabelle hätte eine Migration gebraucht, die diesem Auftrag nicht zugeteilt war, und parallel arbeitende Agenten
  hätten am ModelSnapshot kollidiert. Das Kennzeichen `KeptFileId = 0` ist eindeutig (FileIds beginnen bei 1).
- `FileLinkUndoStore` zusätzlich zum Abzug: wird der Ordner im Explorer wiederhergestellt (statt in Lumora) und eingelesen,
  bekommen die Dateien ihre Album-/Ordner-/Story-Verknüpfungen wie bei jedem anderen Papierkorb-Batch zurück.
- Offline-Guard und erneute Existenzprüfung folgen PLAN Kap. 12: ein abgestecktes Laufwerk darf nie dazu führen, dass Lumora
  Benutzerdaten vergisst.
- Der Ordner-Schritt prüft die Regeln selbst (unter Schreib- und Scan-Sperre), nicht nur die Oberfläche — so kann auch ein
  späterer Aufrufer (Rauchtest, Assistent) keine Wurzel löschen.

## Konsequenzen

### Positiv
- Ordner verschwinden in einem Schritt aus Platte und Katalog, ohne „fehlt“-Rest; Rückgängig stellt alles wieder her.
- Verschwundene Dateien lassen sich an drei Stellen bewusst vergessen, mit klaren Zahlen und Rückgängig.
- Keine Migration, kein neuer Dienst außerhalb der Datei-Operationen für Schreibzugriffe.

### Negativ
- **Vorschaubilder** kommen bei Rückgängig nicht mit zurück: die Cache-Datei wird beim Entfernen gelöscht (Repo-Regel). Beim
  Ordner entstehen sie aus den zurückgeholten Dateien neu; bei „Fehlende entfernen“ bleibt eine zurückgeholte Zeile ohne
  Vorschaubild, bis die Datei wieder auftaucht.
- Rückgängig scheitert für eine Datei, wenn ihre FileId oder ihr Platz (Ordner + Name) inzwischen belegt ist (z. B. nach
  „Neu aufbauen“ oder einem Scan, der die zurückgeholte Datei schon neu aufgenommen hat) — sie bleibt dann als neue Zeile
  ohne die alten Angaben bzw. bleibt weg; die Meldung sagt es.
- Endgültig gelöschte Ordner (Laufwerk ohne Papierkorb) sind nicht rückgängig zu machen — deshalb die eigene Rückfrage.
- Der Abzug je Datei kostet bei sehr großen Ordnern einige Sekunden (mehrere Abfragen je Datei); bei Herberts Ordnergrößen
  vertretbar, sonst später bündeln.
- `DuplicateCleanupUndo` trägt jetzt zwei Bedeutungen; eine spätere Migration kann sie in eine allgemeine `CatalogRowUndo`
  umbenennen.

### Neutral
- Der Rauchtest nutzt im eigenen Schritt einen **Test-Papierkorb** (`SmokeRecycleBin.TestMode`: Ablage im Rauchtest-AppData,
  danach geleert) — so bleibt im echten Windows-Papierkorb nie etwas aus diesem Schritt liegen, auch nicht nach einem
  Abbruch. Die echte Windows-Seite (`RestoreDirectoryFromRecycleBin`) ist über ein nachgebautes `$Recycle.Bin`-Verzeichnis
  getestet.
- Abzüge bleiben nach erfolgreichem Rückgängig mit `UndoneAtUtc` stehen und gehen mit dem Batch (Kaskade).

## Alternativen

- **Jede Datei einzeln in den Papierkorb, dann den leeren Ordner** → verworfen: viele Shell-Aufrufe, viele Papierkorb-Einträge,
  Nicht-Medien bräuchten eigene Behandlung, Teilzustände bei Abbruch.
- **Eigene Tabelle `CatalogRowUndo` mit Migration** → verworfen (keine Migration zugeteilt, Konfliktgefahr), siehe Negativ.
- **Ohne Papierkorb stillschweigend löschen** (wie die Shell mit `FOF_NOCONFIRMATION`) → verworfen: Repo-Regel „Papierkorb
  statt Hard-Delete“; nur mit ausdrücklicher Zustimmung.
- **Fehlende ohne Rückgängig, nur Bestätigung** → verworfen: der vorhandene Abzug macht Rückgängig billig und sicher.
- **Fehlende automatisch nach N Tagen entfernen** → verworfen: widerspricht ADR-0019 („fehlt“ ist reversibel).

## Referenzen

- [src/Lumora.Services/Operations/FileOperationService.RecycleFolder.cs](../../src/Lumora.Services/Operations/FileOperationService.RecycleFolder.cs),
  [FileOperationService.RemoveMissing.cs](../../src/Lumora.Services/Operations/FileOperationService.RemoveMissing.cs),
  [RemovalUndoStore.cs](../../src/Lumora.Services/Operations/RemovalUndoStore.cs),
  [FolderRemovalRules.cs](../../src/Lumora.Services/Operations/FolderRemovalRules.cs),
  [WindowsRecycleBin.Restore.cs](../../src/Lumora.Services/Operations/WindowsRecycleBin.Restore.cs)
- [src/Lumora.Services/Removal/](../../src/Lumora.Services/Removal/) (`IRemovalService`, Modelle)
- [src/Lumora.Wpf/Removal/](../../src/Lumora.Wpf/Removal/) (`RemovalActions`, `MissingFilesSettingsViewModel`),
  [FolderTreeViewModel.Removal.cs](../../src/Lumora.Wpf/Folders/FolderTreeViewModel.Removal.cs),
  [GalleryViewModel.MissingFiles.cs](../../src/Lumora.Wpf/Gallery/ViewModels/GalleryViewModel.MissingFiles.cs)
- Tests: `tests/Lumora.Tests/Operations/FolderRecycleTests.cs`, `MissingRemovalTests.cs`, `WindowsRecycleBinFolderTests.cs`;
  `tests/Lumora.Wpf.Tests/Removal/*`; Rauchtest `SmokeScenario.FolderRemoval.cs`
