# ADR-0066: Ausschlüsse beim Einlesen und Ordner verschieben

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Herbert Schrotter (Wunsch und Grundentscheidungen: allgemeine Liste UND je Bibliothek, Standard `.thumbs`/`.temp`,
  Bestehende nach Rückfrage aus dem Katalog entfernen mit „Nur künftig ausschließen“ als Alternative, Rückgängig wie bei
  „Fehlende entfernen“; Ordner verschieben per Menü und Ziehen mit Rückgängig), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage)
- **Bezogen auf:** [ADR-0012](0012-operation-logging.md) (Protokoll), [ADR-0019](0019-incremental-scan.md) (Incremental-Scan, Offline-Guard),
  [ADR-0020](0020-bibliothek-mehrere-wurzeln-rollen.md) (Wurzeln), [ADR-0034](0034-ordner-uebernahme-mit-leerketten-zusammenzug.md) (Übernehmen),
  [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich, „Alle verknüpften“), [ADR-0049](0049-versteckte-bibliotheken.md)
  (eigener Katalog je Sitzung), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (Verknüpfungen),
  [ADR-0065](0065-ordner-loeschen-und-fehlende-entfernen.md) (Katalog-Entfernen mit Abzügen), PLAN.md Kap. 4, 7, 12

## Kontext

Herberts Fotoordner enthalten aus den alten PowerShell-Viewern V1/V2 noch `.thumbs`- und `.temp`-Ordner mit Vorschaubildern.
Lumora übersprang bisher nur `.lumora` sowie System-/Reparse-Ordner und las diese Bilder mit ein — das verfälscht Galerie,
Doppelte und Storys. Außerdem gab es im Reiter „Ordner“ kein Verschieben ganzer Ordner (nur Umbenennen und „In die Bibliothek
übernehmen“ aus dem Eingang).

## Entscheidung

### A) Ausschlüsse beim Einlesen

**Regeln** (`Services/Scanning/Exclusions/ScanExclusionSet`, unveränderlich, gemeinsam für alle Leser):
- **Allgemeine Liste** — Ordnernamen, die in jeder Tiefe unterhalb einer Wurzel gelten (Groß/Klein egal). Standard `.thumbs`
  und `.temp`.
- **Je Bibliothek** — bestimmte Ordner samt Unterordnern.
- Wurzeln sind nie ausgeschlossen, auch wenn ein Teil ihres Pfads wie ein ausgeschlossener Name heißt (Namen zählen nur für die
  Pfadteile unterhalb der Wurzel).

**Ablage** (`ScanExclusionStore`) in der **Settings-Tabelle des jeweiligen Katalogs** — eine versteckte Sitzung hat ihren eigenen
Katalog und damit ihre eigene Liste, nichts liegt außerhalb des Containers:
- `scan.excludedNames` = JSON-Array der Namen. Fehlt der Eintrag (Neuinstallation, bestehender Katalog vor 0.4.33), gelten
  `.thumbs` und `.temp`; eine bewusst geleerte Liste bleibt leer.
- `scan.excludedFolders.{LibraryId}` = JSON-Array `{"root":Id,"path":"relativ"}` — **relativ zur Wurzel** (überlebt einen
  Wurzel-Umzug), gehört der **Bibliothek, nicht der Gruppe** (ADR-0048); Einträge einer ausgetragenen Wurzel gelten nicht mehr,
  der Eintrag geht beim Löschen der Bibliothek mit. Ausschlüsse darunter bleiben beim Ausschließen eines Elternordners stehen (hebt man ihn wieder
  auf, gelten sie weiter). Umbenennen und
  Verschieben eines Ordners ziehen die Ausschlüsse darunter mit.
- Jede Änderung sendet `ScanExclusionsChangedMessage`.
- **Keine Migration** — es sind nur Settings-Zeilen.

**Wirkung überall, wo eingelesen wird:**
- **Voller Scan / Neu aufbauen:** `FolderEnumerator` betritt ausgeschlossene Ordner nicht (Folders-Zeilen entstehen gar nicht).
  „Neu aufbauen“ nimmt damit auch die Einträge darin heraus (wie jeder andere nicht mehr gefundene Ordner).
- **Incremental-Scan:** ausgeschlossene Ordner werden nicht betreten, und ihre **schon katalogisierten Dateien gleicht der Scan
  nicht ab** — weder „fehlt“ noch Aktualisierung. So bleibt „Nur künftig ausschließen“ stabil, und ein ausgeschlossener Ordner
  erzeugt nie eine „fehlt“-Flut.
- **Ordnerüberwachung:** Ereignisse in ausgeschlossenen Ordnern zählen nicht (ein Renamed zählt, wenn eine Seite nicht
  ausgeschlossen ist); die Regeln werden beim Bibliothekswechsel und nach `ScanExclusionsChangedMessage` neu geladen.
- **Übernehmen aus dem Eingang** (ADR-0034): ausgeschlossene Ordner der Quelle werden nicht übernommen und bleiben liegen —
  sie zählen als Rest, der Quellordner wandert dann nicht in den Papierkorb (eigene Inhalte des Benutzers gehen nie unbemerkt
  mit). Die eingebauten Systemordner (`.thumbs`, `.temp`, `.cache` …) verhalten sich wie bisher.
- **Ordnerbaum:** ausgeschlossene Ordner (und alles darunter) erscheinen nicht. Ein **dezenter Hinweis** „n Ordner
  ausgeschlossen“ unter dem Seitenkopf (Tooltip: wo man sie verwaltet) zählt die obersten ausgeschlossenen Ordner im Katalog
  plus ausgeschlossene Ordner der Bibliothek ohne Zeile — sonst wäre ein ausgeblendeter Ordner schwer wiederzufinden.

**Bestehende Einträge** (Herbert: nach Rückfrage aus dem Katalog, Dateien bleiben auf der Platte):
- Rückfrage beim Hinzufügen (Ordner oder Name), wenn darin schon Dateien katalogisiert sind: Zahl + Benutzerdaten
  (Sterne/Favorit, benannte Personen, Alben, Tags/Kategorien — derselbe Zähler wie ADR-0065, jetzt `UserDataCounter`), Knöpfe
  „Aus der Bibliothek entfernen“ / „Nur künftig ausschließen“ / Abbrechen (Abbrechen speichert nichts).
- Für den Standard-Fall (`.thumbs` war vor 0.4.33 schon eingelesen) zeigt die Einstellungs-Karte „n Dateien in ausgeschlossenen
  Ordnern sind noch in der Bibliothek“ mit „Aus der Bibliothek entfernen…“.
- **Eigener `OperationType.ExcludeFromCatalog` (13)** statt eines Grundes am RemoveFromCatalog-Batch: die Vorbedingung ist eine
  andere (Datei vorhanden und Ordner ausgeschlossen statt „fehlt“ unter erreichbarer Wurzel), es gibt keinen Offline-Guard (die
  Platte wird nicht befragt), und Protokoll bzw. die spätere Aktivitäts-Anzeige (Mockup 15) sollen beide Fälle unterscheiden,
  ohne die Beschreibung zu parsen. Der Ablauf ist derselbe Code (`RemoveCatalogRowsWithUndoAsync`, aus „Fehlende entfernen“
  herausgelöst): Batch, Operation und Abzug je Datei in einer Transaktion, danach je Datei die Item-Transaktion (Verknüpfungen
  gesichert, FTS mit alten Werten, Thumbnails-Zeilen, Files-Zeile, Cache-Datei), dann leere ausgeschlossene Ordnerzeilen mit
  Abzug aus dem Baum. Der Dienst prüft selbst, dass der Ordner jeder Datei nach den **gespeicherten** Ausschlüssen ihrer
  Bibliothek ausgeschlossen ist (die Oberfläche speichert den Ausschluss also vorher).
- **Rückgängig** über denselben Zweig wie ADR-0065 (`UndoRemovalAsync`): dieselben FileIds mit allen Angaben. Im Ergebnisfeld
  des Ordnerbaums bzw. der Einstellungs-Karte nimmt Rückgängig zusätzlich den **gerade gesetzten** Ausschluss wieder weg (Ordner
  bzw. Name) — sonst stünden die Einträge in einem ausgeschlossenen Ordner. Über die Galerie-Taste (UndoLastAsync) kommen nur
  die Einträge zurück; der Ausschluss bleibt (Zustand wie „Nur künftig ausschließen“).
- **Ausschluss aufheben** (Einstellungen › Bibliotheken & Eingänge, „Ausschluss aufheben“ bzw. Name entfernen) liest die aktuelle
  Bibliothek danach im Hintergrund neu ein (Incremental). Andere Bibliotheken folgen beim nächsten „Aktualisieren“.

**Oberfläche:**
- Reiter „Ordner“ › Rechtsklick › **„Ordner ausschließen…“** (nicht an der Wurzel, nicht an Kopfknoten).
- Einstellungen › **Bibliotheken & Eingänge** › Karte **„Ausschlüsse beim Einlesen“** unter der Ordnerüberwachung (dort, wo
  Wurzeln und Einlesen schon verwaltet werden): allgemeine Liste mit Hinzufügen/Entfernen, Ordner der aktuellen Bibliothek mit
  „Ausschluss aufheben“, „bereits katalogisiert“ mit Entfernen und Rückgängig.
- **„Alle verknüpften“**: „Ordner ausschließen…“ ist gesperrt (Hinweis wie bei den übrigen Datei-Aktionen); die Karte zeigt die
  Ordner der aktiven Bibliothek mit ihrem Namen.
- Info-Logs nur mit Zahlen (Anzahl Namen/Ordner, entfernte Dateien), keine Pfade.

### B) Ordner verschieben

- Reiter „Ordner“ › Rechtsklick › **„Verschieben nach…“** — Zielauswahl über den **Windows-Ordnerdialog**, der in der Wurzel
  des Ordners beginnt; geprüft wird im Dienst. Die Auswahl ist die Bestätigung (keine zweite Rückfrage).
- **Ziehen** eines Ordners auf einen anderen im Baum: schnelle Vorprüfung im Speicher (gleiche Bibliothek, nicht in sich selbst,
  nicht dorthin, wo er liegt, keine Wurzel), **Drop-Markierung** (Rahmen in Akzentfarbe) nur auf erlaubten Zielen, **Esc**
  bricht ab (Standard von `DoDragDrop`), Ablegen fragt nach („… samt Inhalt nach … verschieben?“).
- **Protokoll** (ADR-0012): `OperationType.MoveFolder` (12) — Batch, Ordner-Operation (FileId null, Quelle → Ziel) und eine
  Move-Operation je katalogisierter Datei, alle Pending in einer Transaktion vor dem Dateisystem-Schritt.
- **Katalog ohne neue FileIds:** `Directory.Move` in einem Schritt, danach Pfade des Ordners und aller Unterordner, **ParentId**
  (Zielordner, bei Bedarf samt Kette angelegt) und **RootId** (z. B. Eingang → Bibliotheksordner auf demselben Laufwerk).
  Geprüft, was sonst Pfade speichert: `Files` (nur FolderId + Name), `Thumbnails` (FileId-basiert), `FilesFts` (Name/Endung),
  `AlbumItems`/`FolderLinks`/Story-Einträge (Ids) — nichts weiter zu tun; `FileOperations`/`FileLinkUndo` sind Protokoll und
  bleiben; die Ausschlüsse der Bibliothek ziehen mit.
- **Ordnerüberwachung:** Quelle und Ziel (Pfad und ganzer Baum) gelten als eigen — kein Diff-Scan, keine „fehlt“-Flut. Scheitert
  nur die Katalog-Buchung, werden die Pfade zurückgenommen, die Überwachung liest neu ein.
- **Abgelehnt ohne Änderung** (`FolderMoveRules` + Plattenprüfung unter Schreib- und Scan-Sperre): Wurzel selbst, Ordner mit
  einer Wurzel darin, Ziel = Ordner selbst oder darin, Ziel = jetziger Elternordner, gleichnamiger Ordner im Ziel (Platte oder
  Katalog — **kein Zusammenführen**), Ziel außerhalb der Bibliothek bzw. in einer anderen Bibliothek, Ziel ausgeschlossen,
  versteckte Bibliothek (`.lumora`) darin, Laufwerk nicht erreichbar, Ordner oder Ziel nicht vorhanden, anderes Laufwerk;
  „Alle verknüpften“ sperrt die Oberfläche.
- **Anderes Laufwerk** (Wurzeln einer Bibliothek auf verschiedenen Laufwerken): **abgelehnt** mit Hinweis auf „In die Bibliothek
  übernehmen…“. Begründung: Kopieren + Quelle in den Papierkorb wäre ein langer, nicht atomarer Vorgang mit Teilzuständen
  (Abbruch mitten im Kopieren, volle Platte, gesperrte Dateien) und bräuchte ein eigenes Rückgängig über zwei Laufwerke; der
  häufige Fall (Eingang auf einem anderen Laufwerk → Bibliotheksordner) ist mit „In die Bibliothek übernehmen…“ (ADR-0034)
  bereits sicher und rückgängig machbar gelöst. Auf demselben Laufwerk ist `Directory.Move` sofort fertig.
- **Scan-Sperre:** der Schritt belegt die Bibliothek wie alle Datei-Operationen und wartet auf einen laufenden Scan (Hinweis in
  der Statusleiste wie bisher). Fortschritt: Ordner-Aktion läuft (wie die übrigen Ordner-Aktionen); die Aktivitäts-Anzeige
  (Mockup 15) folgt später.
- **Rückgängig** im Ergebnisfeld des Ordnerbaums und über die Galerie-Taste (`UndoLastAsync`): Ordner zurück, Pfade, ParentId
  und RootId wieder wie vorher, Ausschlüsse ziehen zurück. Abgelehnt, wenn der alte Platz belegt oder der Elternordner weg ist.

## Begründung

- Eine Regel-Klasse für alle Leser (Scan, Incremental, Überwachung, Übernehmen, Ordnerbaum) verhindert, dass ein Weg einen
  Ordner doch einliest.
- Settings-Tabelle statt neuer Tabelle: wenige, kleine Listen; keine Migration, kein Konflikt am ModelSnapshot; versteckte
  Sitzung automatisch getrennt.
- Relativ zur Wurzel gespeichert: Wurzel umziehen (Laufwerksbuchstabe) bricht die Ausschlüsse nicht.
- Katalogisierte Dateien in ausgeschlossenen Ordnern im Incremental-Scan einzufrieren (statt sie „fehlen“ zu lassen) hält die
  Entscheidung „Nur künftig“ ein und schützt vor einer „fehlt“-Flut samt Mass-Missing-Rückfrage.
- Der vorhandene Entfernen-Weg aus ADR-0065 bringt Rückgängig mit denselben FileIds ohne neue Mechanik.
- Ordner verschieben folgt dem bewährten Muster „Ordner umbenennen“ (ein `Directory.Move`, Folders-Zeilen nachziehen) — FileIds,
  Vorschaubilder und Verknüpfungen bleiben ohne weiteres Zutun erhalten.

## Konsequenzen

### Positiv
- `.thumbs`/`.temp` verschwinden ab sofort aus neuen Scans; die schon eingelesenen lassen sich mit einem Klick (mit Zahlen,
  Rückgängig) entfernen.
- Eigene Ausschlüsse je Bibliothek und für alle Bibliotheken, auch in der versteckten Sitzung (eigene Liste).
- Ordner lassen sich per Menü oder Ziehen verschieben, ohne Angaben zu verlieren, mit Rückgängig.

### Negativ
- Nach „Nur künftig ausschließen“ bleiben die alten Einträge sichtbar in der Galerie, ihr Ordner aber nicht im Baum (gewollt;
  die Einstellungs-Karte zeigt die Zahl und bietet das Entfernen an).
- Ausschluss aufheben liest nur die aktuelle Bibliothek sofort neu ein; andere Bibliotheken (nach Änderung der allgemeinen
  Liste) beim nächsten „Aktualisieren“.
- Vorschaubilder entfernter Einträge kommen bei Rückgängig nicht zurück (Cache-Datei gelöscht, wie ADR-0065); sie entstehen bei
  Bedarf neu, da die Dateien vorhanden sind.
- Verschieben auf ein anderes Laufwerk geht nicht (Hinweis auf „In die Bibliothek übernehmen…“).

### Neutral
- Neue `OperationType`-Werte `MoveFolder` (12) und `ExcludeFromCatalog` (13), als int gespeichert, kein Schema-Eingriff.
- Rauchtest-Schritt „Ausschlüsse und Ordner verschieben“ vor „Versteckte Bibliothek“ (nur in der Kopie der Testbibliothek,
  Aufräumen über den Test-Papierkorb aus ADR-0065).

## Alternativen

- **Eigene Tabelle `ScanExclusions` mit Migration** → verworfen: wenige Einträge, Settings genügt; keine Migration nötig.
- **Ausschluss je Gruppe** (wie Tags) → verworfen: Ordner gehören zu einer Bibliothek (Herbert: „gehören zur Bibliothek“).
- **Grund am RemoveFromCatalog-Batch statt eigenem Typ** → verworfen (siehe oben: andere Vorbedingung, kein Offline-Guard,
  unterscheidbar ohne Textauswertung).
- **Ausgeschlossene Dateien im Incremental-Scan „fehlen“ lassen** → verworfen: „fehlt“-Flut, Mass-Missing-Rückfrage, widerspricht
  „Nur künftig ausschließen“.
- **Eigener Zielauswahl-Dialog mit Ordnerbaum** → zurückgestellt: der Windows-Ordnerdialog mit Prüfung im Dienst reicht und ist
  vertraut; Ziehen im Baum deckt den schnellen Weg ab.
- **Laufwerkswechsel per Kopieren + Papierkorb** → verworfen (siehe oben).

## Referenzen

- [src/Lumora.Services/Scanning/Exclusions/](../../src/Lumora.Services/Scanning/Exclusions/) (`ScanExclusionSet`, `ScanExclusionStore`,
  `ScanExclusionService`), [FolderEnumerator.cs](../../src/Lumora.Services/Scanning/FolderEnumerator.cs),
  [IncrementalScanRunner.cs](../../src/Lumora.Services/Scanning/IncrementalScanRunner.cs),
  [LibraryWatchService.cs](../../src/Lumora.Services/Watch/LibraryWatchService.cs),
  [FolderAdoptionSourceReader.cs](../../src/Lumora.Services/Operations/FolderAdoptionSourceReader.cs)
- [FileOperationService.RemoveExcluded.cs](../../src/Lumora.Services/Operations/FileOperationService.RemoveExcluded.cs),
  [FileOperationService.MoveFolder.cs](../../src/Lumora.Services/Operations/FileOperationService.MoveFolder.cs),
  [FolderMoveRules.cs](../../src/Lumora.Services/Operations/FolderMoveRules.cs),
  [UserDataCounter.cs](../../src/Lumora.Services/Removal/UserDataCounter.cs)
- [src/Lumora.Wpf/Exclusions/](../../src/Lumora.Wpf/Exclusions/) (`ScanExclusionActions`, `ScanExclusionsSettingsViewModel`,
  `IFolderEditDialogs`), [FolderTreeViewModel.Exclusions.cs](../../src/Lumora.Wpf/Folders/FolderTreeViewModel.Exclusions.cs),
  [FolderTreeViewModel.Move.cs](../../src/Lumora.Wpf/Folders/FolderTreeViewModel.Move.cs),
  [FolderMoveActions.cs](../../src/Lumora.Wpf/Folders/FolderMoveActions.cs), Ziehen in
  [FolderTreePage.xaml.cs](../../src/Lumora.Wpf/Folders/FolderTreePage.xaml.cs)
- Tests: `tests/Lumora.Tests/Scanning/ScanExclusionTests.cs`, `Operations/ExcludedRemovalTests.cs`, `Operations/FolderMoveTests.cs`,
  `Watch/LibraryWatchServiceTests.Exclusions.cs`; `tests/Lumora.Wpf.Tests/Exclusions/*`; Rauchtest `SmokeScenario.ExclusionsMove.cs`
