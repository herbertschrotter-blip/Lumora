# Changelog

Alle nennenswerten Änderungen an Lumora (bis 0.2.48 „PhotoViewer V3“). Format: `[Version] — Datum`, oberster Eintrag = aktuelle Version
(Versionsquelle für das Commit-Profil in `CLAUDE.md`).

## [0.4.37] — 2026-10-25

### Docs — Mockup 17 „Personen, Titelbild, Rechtsklick-Menü“ und Mockup 18 „Galerie mit Ordnerleiste“, abgenommen
- `mockups/17-personen-kontextmenue.html`: Personen-Kacheln ohne abgeschnittenen Text mit Chips, „Als Titelbild für ▸“
  überall mit automatischer Wahl, neu gegliedertes Rechtsklick-Menü; dazu Fotos per Ziehen/Strg+C in andere Programme
  kopieren (nur Kopie, in der versteckten Bibliothek gesperrt).
- `mockups/18-galerie-ordnerleiste.html`: Galerie mit ein-/ausklappbarer Ordnerleiste, Ordner als Filter-Chip, Menüpunkt
  „Ordner“ entfällt.
- PLAN.md Kap. 7: Vorhaben „Personen-Kacheln, Titelbild, Rechtsklick-Menü“, „Galerie mit Ordnerleiste“,
  „Fotos in andere Programme kopieren“. (closes #3)

## [0.4.36] — 2026-10-25

### Feature (ADR-0067) — Aktivitäts-Anzeige unten rechts (Mockup 15)
- **Eine Anzeige ganz rechts in der Statusleiste für alle Hintergrund-Vorgänge**: wichtigster Vorgang (vom Benutzer gestartete
  vor Hintergrund-Indizes) mit Drehsymbol, Text („Verschieben: 120 von 340“) und kleinem Balken, Zähler „+n“; ohne Vorgang
  8 s „Alles erledigt“, danach „Keine Aktivität“. **Ersetzt die Einzelanzeigen** (Scan, Vorschaubilder, „Wartet auf laufenden
  Scan…“, „Änderung wartet auf den Katalog…“, Ordnerüberwachung, Entpacken, Gesichter, Motive, Aufnahmedatum, Orte, GPS-Spuren,
  Doppelte, Foto-Güte, Storys, „Hintergrund …/Bereit“). Links bleibt die Bibliothek, rechts bleiben Hinweise ohne Vorgang
  (Recovery, offline, Doppelte im Eingang, Orte-Zahlen, Sicherheits-Check).
- **Klick klappt die Liste auf** (hell/dunkel; Esc, Klick daneben, Fensterwechsel schließen): **Läuft** (Titel mit Zahlen, Ziel nur
  mit Ordnernamen, Balken, Restzeit nach 3 s, Pause/Fortsetzen bei Gesichtern und Motiven, Abbrechen), **Wartet** (mit Grund,
  wartende Datei-Vorgänge abbrechbar), **Zuletzt fertig** (≤ 10, Ergebnis + Zeitpunkt, **Rückgängig** wo es eines gibt,
  **Fehlende zeigen** nach einem Einlesen mit neu fehlenden Dateien → Galerie „Nur fehlende“, **Details** bei Fehlern und
  Übersprungenen, „Liste leeren“). Nur die laufende Sitzung, nichts gespeichert; versteckte Sitzung und Zusperren beginnen leer
  (eine Zentrale je DI-Container).
- **Kurze Meldung** über der Statusleiste beim Fertigwerden eines selbst gestarteten Vorgangs (6 s, mit Rückgängig); Ergebnis von
  „Rückgängig“ ebenso. Hintergrund-Indizes und Einlesen ohne Meldung.
- **Angeschlossen:** Verschieben, Umbenennen, Papierkorb (Dateien und Ordner), Fehlende/Ausgeschlossene entfernen, Ordner
  umbenennen/verschieben, In die Bibliothek übernehmen, Rückgängig, Zurückholen aus dem Papierkorb, Sortieren/Flatten (ein Lauf =
  ein Vorgang), Einlesen voll/inkrementell, automatisches Entpacken, Vorschaubilder, Doppelte, Gesichter, Motive, Aufnahmedatum,
  Orte nachtragen/prüfen, Foto-Güte, Storys, GPS-Spuren, Ordnerüberwachung (anstehend/angehalten), Scan-Sperre und
  Katalog-Schreibsperre (Wartet). **Nicht angeschlossen:** manuelles Entpacken im Dialog, „Speicherort ändern“, Gesichtsdaten
  löschen, Story-Assistent/Collage (Fortschritt weiter dort); Kopieren gibt es nicht; Einlesen ist aus der Liste nicht abbrechbar.
- **Technik:** `Services/Activity` — `IActivityCenter`/`ActivityCenter` (Ordnung nach Wichtigkeit, Begrenzung, Drosselung der
  Benachrichtigung auf höchstens alle 150 ms über `TimeProvider`, `Finished` sofort), `ActivityScope` (AsyncLocal: Besitzer,
  Datei-Fortschritt, Unterdrücken), `ActivityBridge` (Messenger-Records + Vorschaubild-Warteschlange → Zentrale; Abbrechen/Pause
  über die Wege der Dienste), `ActivityTrackingFileOperationService` als registriertes `IFileOperationService` um den
  unveränderten `FileOperationService` (Abbrechen per verknüpftem Token mit `CancelAsync`, Rückgängig über `UndoBatchAsync`, zieht
  Einträge nach, die über die Galerie-Taste zurückgenommen werden); `SorterMoveExecutor` meldet den Lauf (Rückgängig nur am
  letzten Lauf). Eingriffe in bestehende Dienste: je eine Zeile `ActivityScope.ReportItems` in `RunBatchAsync` und
  `ActivityScope.Suppress()` beim Papierkorb des Archivs (manuell/automatisch). Wpf `Shell/Activity` (`ActivityStatusViewModel`,
  `ActivityItemViewModel` — Zeilen in place, `ActivityIndicator` — Liste und Meldung als Überlagerung im Fenster statt Popup),
  `StatusBar.xaml` umgebaut, `StatusBarViewModel.Activities` (alte Texte bleiben als Zustand, Vorschaubild-Timer dort aus).
  Logs nur mit Art/Id. Keine Migration, keine neuen Pakete.
- **Tests:** Services 1359 → 1388 (3 übersprungen wie vorher) — Zentrale (Wichtigkeit, Schlüssel, Wartet mit eigenem Abbrechen,
  Fertig/„Zuletzt fertig“ nur für Benutzer-Vorgänge bzw. Fehler, Begrenzung 10, Drosselung, Abbrechen einmal und nach Entsorgen,
  Pause, Rückgängig inkl. Fehler bleibt erneut versuchbar, Restzeit, Entsorgen leert), Datei-Vorgänge mit echtem
  `FileOperationService` (Fortschritt je Datei, Rückgängig aus der Liste ohne zweiten Eintrag, Galerie-Taste zieht nach,
  verschachtelt/unterdrückt ohne Eintrag, Abbrechen, wartende Scan-Sperre am selben Eintrag, Ordnername), Brücke (Einlesen mit
  „Fehlende zeigen“, Abbruch, Gesichter Pause/Abbrechen über den Dienst, GPU-Hinweis, Index-Fehler einmal, einfache Indizes,
  Scan-Sperre, Katalog-Sperre, Ordnerüberwachung, Entpacken mit Details, Vorschaubild-Warteschlange). Wpf 1224 → 1229 (7
  übersprungen wie vorher) — Statusleiste („+n“, „Alles erledigt“ → „Keine Aktivität“), Liste (drei Abschnitte, in place),
  kurze Meldung (nur Benutzer-Vorgänge, Rückgängig, nicht bei Abbruch), Pause/Abbrechen/Details, Darstellung hell/dunkel ohne
  Binding-Fehler.
- **Rauchtest:** neuer Schritt „Aktivitäten“ vor „Versteckte Bibliothek“ (Verschieben wartet auf die gehaltene Scan-Sperre →
  Statusleiste und Liste „Wartet“, Screenshot `25-aktivitaeten-hell`; fertig → „Zuletzt fertig“ mit Rückgängig und kurze Meldung,
  `25-aktivitaeten-dunkel`; Rückgängig über die Liste, dieselbe FileId; aufgeräumt). Angepasst statt gelöscht: „Wartezustände“
  (Abbrechen über die Liste), „Gesichter“ (Pause/Fortsetzen über die Liste), „Auto-Entpacken“ (Ergebnis unter „Zuletzt fertig“).

## [0.4.35] — 2026-10-25

### Feature (Mockup 16) — Tag- und Kategorie-Symbole auf Kacheln, Lightbox und Ordnerbaum
- **Galerie-Kacheln:** oben links eine **Tag-Marke** (Symbol `Tag24`, bis zu 3 Farbpunkte in den Tag-Farben, Anzahl ab 2) und
  eine **Kategorie-Marke** (Symbol `Group24` wie die Kategorien-Auswahl, Anzahl ab 2) — nur wenn gesetzt, halbtransparent
  dunkel auf dem Bild. Darüberfahren nennt die Namen (Tags mit Farbpunkt, Kategorien als „Kategorie: Wert“); der Tooltip
  entsteht erst beim Öffnen (kein vorgebauter Baum je Kachel). Kollisionen: bei Auswahl rückt die Marke neben das Häkchen,
  bei „fehlt“/„Eingang“/„verknüpft“ (oben mittig) steht sie darunter; Herz, Endung und Sterne bleiben, wo sie sind.
  **Entscheidung:** Die farbigen Tag-Punkte unter der Kachel (`TagChipsControl` im Fuß) entfallen — sie zeigten dasselbe wie
  die neue Tag-Marke (das Mockup zeigt nur die Marke), der Fuß bleibt für Größe und Personen; wer den Schalter ausschaltet,
  will keine Marken.
- **Collage:** dieselben Marken, nur beim Darüberfahren (wie Herz und Sterne).
- **Lightbox:** Leiste unten mit Tag-Chips (Farbpunkt + Name), Trenner, gesetzten Kategorien und **„+ Tag / Kategorie“**
  (Menü mit den vorhandenen Dialogen Strg+T bzw. Strg+K). Im Vollbild aus. Ist die Leiste abgeschaltet, zeigt die
  Titelleiste Tags und Kategorien wie bis 0.4.34.
- **Ordnerbaum:** rechts neben dem Namen Tag-/Kategorie-Symbol mit der Zahl der Fotos mit Tags bzw. Kategorie-Werten
  **direkt im Ordner** (nicht rekursiv); Tooltip „n von m Fotos“ mit den 3 häufigsten Namen. Gezählt in vier gruppierten
  Abfragen für den ganzen Bereich (kein N+1, nur die Bibliotheken von `ILibraryScope`, ADR-0048) im Hintergrund nach dem
  Aufbau des Baums; neu gezählt (400 ms gebündelt) nach Zuweisungen (`FilesChangedMessage` Metadata) und Stammdaten-Änderungen
  (`TagsChangedMessage`/`CategoriesChangedMessage`, dann auch die Namen). Auswahl-Optik (0.4.30) und Ausschluss-Logik (0.4.33)
  unverändert.
- **Einstellungen › Darstellung › „Symbole für Tags und Kategorien“:** je ein Schalter für Kacheln, Lightbox und Ordnerbaum,
  Standard an, gespeichert in der Settings-Tabelle des Katalogs (`ui.marks.tiles`, `ui.marks.lightbox`, `ui.marks.folderTree`;
  versteckte Sitzung = eigener Katalog). Ordnerbaum aus → keine Abfrage.
- **Leistung:** Tags und Kategorie-Werte kamen schon je Galerie-Seite gebündelt (je eine Abfrage für die 500 Kacheln,
  `ApplyTagsToItemsAsync`/`ApplyCategoriesToItemsAsync`) — die Marken nutzen genau diese Daten; neu ist nur die Beschriftung
  „Kategorie: Wert“ in `GalleryItemCategories.Labels`. Kacheln bleiben Automation-Blätter.
- **Technik:** Services `Marks/` (`MarkSettings` + `IMarkSettingsStore`, `IFolderMarkCounter`/`FolderMarkCounter`,
  `AddMarks`); Wpf `Marks/` (`MarkSettingsState` je Sitzung, `TileMarksControl`, `FolderMarksControl`, `FolderMarkSummary`,
  `MarkToolTips`, `SettingsMarksSection`, `AddMarksUi` aus `AddGallery`), `FolderTreeViewModel.Marks.cs`,
  `GalleryViewModel.Marks.cs`, `LightboxViewModel.Marks.cs`, `LightboxMarksBar`. **Keine ADR** (keine neue
  Architekturentscheidung: kein Schema, keine neue Schicht oder Abhängigkeit; Settings-Tabelle, Messenger-Records, gruppierte
  Abfrage und Seiten-Bündel folgen bestehenden Mustern), **keine Migration**.
- **Tests:** Services 1359 → 1365 (3 übersprungen wie vorher) — Ordner-Zählung nur direkt im Ordner mit den häufigsten
  zuerst, Begrenzung, Bereichs-Trennung (andere Bibliothek nur im gemeinsamen Bereich), Kategorie-Werte einer Seite in einem
  Aufruf, Einstellungen (Standard an, speichern ohne Doppelzeilen, unlesbare Werte → an). Wpf 1224 → 1238 (7 übersprungen wie
  vorher) — Kachel-Marke (nur wenn gesetzt, Anzahl ab 2, höchstens 3 Punkte, Schalter aus → unsichtbar, Tooltip erst bei
  Bedarf mit Namen), Collage mit der echten Vorlage (nur beim Darüberfahren, Schalter), Galerie-ViewModel folgt dem Schalter,
  Lightbox-Leiste (Namen, Trenner, Menü mit Strg+T/Strg+K-Befehlen, Schalter, Vollbild, Bindung im Fenster), Ordnerbaum
  (Zahlen direkt im Ordner, Tooltip, Neuzählung nach Zuweisung ohne Neuaufbau, Schalter aus/an, ohne Dienst wie bisher),
  Ordner-Marke, Einstellungs-Karte speichert, Laden überschreibt keinen frühen Klick. Wpf-Suite dreimal hintereinander grün.
- **Rauchtest:** neuer Schritt „Tag- und Kategorie-Symbole“ vor „Versteckte Bibliothek“ (2 Fotos mit Tag, 1 mit Kategorie →
  Marken auf den Kacheln, Zahl im Ordnerbaum, Lightbox-Leiste mit Namen, Schalter aus → weg und `false` in der
  Settings-Tabelle, wieder an, aufgeräumt; Screenshots `26-symbole-hell`, `26-symbole-dunkel`, `26-symbole-lightbox`).
  Erster Lauf 48/49: der neue Schritt wies zu, bevor die Galerie die eben angelegte Kategorie kannte, und hielt veraltete
  Kachel-Instanzen (Schritt korrigiert: wartet auf die Stammdaten, sucht Kacheln über die Id), dazu 5 Binding-Fehler im
  bekannten Wackler „Alben“ (intelligentes Album). Zweiter Lauf **49/49 OK, 0 Binding-Fehler**, kein ERR/FTL.

### Offen
- Die Ordner-Zahlen werden auch nach Favorit/Bewertung neu gezählt (die Metadata-Nachricht unterscheidet nicht nach Art);
  gebündelt und im Hintergrund, aber bei sehr großen Bibliotheken unnötige Arbeit — ggf. eigene Nachricht für Zuordnungen.
- „n von m Fotos“ im Ordner-Tooltip zählt wie die Ordnerzahl alle Dateien des Ordners (auch Videos und fehlende).
- Schon vorher so (Listenspalten): wird ein Kategorie-Wert zugewiesen, bevor die Galerie die eben angelegte Kategorie geladen
  hat, fehlt er auf der Kachel bis zum nächsten Neuladen der Seite (im Alltag kaum erreichbar, im Rauchtest gesehen).

## [0.4.34] — 2026-10-25

### Docs — Offene Aufgaben als GitHub Issues
- 23 offene Aufgaben als Issues #1–#23 im Repo angelegt (Labels „in Arbeit“, „Entscheidung“, „Test bei Herbert“,
  „geplant“, `bug`, `enhancement`); Texte neutral, weil das Repo öffentlich ist.
- CLAUDE.md Code-Profil: Aufgabenquelle = GitHub Issues (plus Auftrag im Chat), Commits schließen Issues mit „(closes #n)“.

### Offen
- Projekt-Board: braucht die GitHub-Berechtigung `project` (`gh auth refresh -s project`, nur Herbert).

## [0.4.33] — 2026-10-25

### Feature (ADR-0066) — Ausschlüsse beim Einlesen und Ordner verschieben
- **Ausschlüsse beim Einlesen:** Ordner, die Lumora nicht einliest — **allgemeine Liste** (Ordnernamen in jeder Tiefe, Groß/Klein
  egal, für alle Bibliotheken des Katalogs; Standard `.thumbs` und `.temp` aus den alten PowerShell-Viewern, auch für bestehende
  Kataloge ohne Eintrag) und **Ordner je Bibliothek** (relativ zur Wurzel gespeichert, gehört der Bibliothek, nicht der Gruppe).
  Ablage in der Settings-Tabelle des jeweiligen Katalogs (`scan.excludedNames`, `scan.excludedFolders.{Id}`) — die versteckte
  Sitzung hat ihre eigene Liste; **keine Migration**. Wirkt im vollen Scan, im Incremental-Scan (schon katalogisierte Dateien
  in ausgeschlossenen Ordnern werden nicht abgeglichen — kein „fehlt“), in der Ordnerüberwachung (Ereignisse dort zählen nicht),
  beim Übernehmen aus dem Eingang (ausgeschlossene Ordner bleiben liegen) und im Ordnerbaum (ausgeblendet, dezent „n Ordner
  ausgeschlossen“).
- **Reiter „Ordner“ › Rechtsklick › „Ordner ausschließen…“** (nicht an der Wurzel, gesperrt in „Alle verknüpften“): sind darin
  schon Dateien in der Bibliothek, Rückfrage mit Zahlen (Sterne/Favorit, Personen, Alben, Tags/Kategorien) — **„Aus der
  Bibliothek entfernen“** (keine Datei wird gelöscht) oder **„Nur künftig ausschließen“**. Entfernen als eigener
  **ExcludeFromCatalog-Batch** (OperationType 13) über den Entfernen-Weg aus 0.4.30 (Abzüge, FTS, Thumbnails, Cache, leere
  ausgeschlossene Ordnerzeilen); **Rückgängig** bringt die Einträge mit denselben FileIds zurück und hebt den gerade gesetzten
  Ausschluss wieder auf.
- **Einstellungen › Bibliotheken & Eingänge › „Ausschlüsse beim Einlesen“** (unter der Ordnerüberwachung): Namen hinzufügen
  (gleiche Rückfrage) / entfernen, Ordner der Bibliothek mit **„Ausschluss aufheben“** (liest danach neu ein), Zahl „noch in der
  Bibliothek“ mit **„Aus der Bibliothek entfernen…“** und Rückgängig (für `.thumbs`, die vor 0.4.33 eingelesen wurden).
- **Ordner verschieben** (Reiter „Ordner“): Rechtsklick › **„Verschieben nach…“** (Windows-Ordnerdialog, beginnt in der Wurzel)
  und **Ziehen** eines Ordners auf einen anderen (Drop-Rahmen nur auf erlaubten Zielen, Esc bricht ab, Ablegen fragt nach).
  **MoveFolder-Batch** (OperationType 12): `Directory.Move` in einem Schritt, Katalog **ohne neue FileIds** (Pfade, ParentId,
  RootId der Folders-Zeilen; Ausschlüsse ziehen mit), Quelle und Ziel für die Überwachung eigen, **Rückgängig** im Ergebnisfeld
  und über die Galerie-Taste. Abgelehnt mit klarer Meldung: Wurzel bzw. Wurzel darin, in sich selbst, schon dort, gleichnamiger
  Ordner im Ziel (kein Zusammenführen), außerhalb der Bibliothek/andere Bibliothek, ausgeschlossenes Ziel, **anderes Laufwerk**
  (Hinweis auf „In die Bibliothek übernehmen…“), offline/fehlend, „Alle verknüpften“. Umbenennen zieht die Ausschlüsse ebenfalls mit.
- **Technik:** `Services/Scanning/Exclusions` (`ScanExclusionSet`, `ScanExclusionStore`, `ScanExclusionService`,
  `ScanExclusionsChangedMessage`), `FolderEnumerator`/`IncrementalScanRunner`/`ScanService`/`LibraryWatchService`/
  `FolderAdoptionSourceReader` mit Ausschlüssen, `IFileOperationService.RemoveExcludedAsync`/`MoveFolderAsync`,
  `FolderMoveRules`, `UserDataCounter` (aus `RemovalService` herausgelöst), „Fehlende entfernen“ nutzt jetzt denselben Helfer
  `RemoveCatalogRowsWithUndoAsync`; Wpf `Exclusions/` (`ScanExclusionActions`, `ScanExclusionsSettingsViewModel`,
  `IFolderEditDialogs`), `FolderMoveActions`, `FolderTreeViewModel.Exclusions/.Move`. Info-Logs nur mit Zahlen.
- **Tests:** Services 1327 → 1359 (3 übersprungen wie vorher) — Regeln (jede Tiefe, Wurzel nie), Ablage (Standard, leer,
  ungültig, relativ, Wurzel/außerhalb abgelehnt, Elternordner, Verschieben zieht mit, Bibliothek löschen, eigener Katalog),
  voller/Incremental-Scan ignorieren `.thumbs` in jeder Tiefe, „Nur künftig“ ohne „fehlt“ und Aufheben liest ein, je
  Bibliothek nur dort, Übernehmen lässt liegen, Entfernen nach Bestätigung + Rückgängig (FileIds, Tags, Album, FTS, Ordnerzeilen),
  Galerie-Taste, ohne Ausschluss nichts; Verschieben (FileIds, Angaben, Ordnerzeilen, Rückgängig, Ablehnungen, andere
  Bibliothek, Eingang → Archiv mit neuer Wurzel, eigen für die Überwachung, Ausschlüsse ziehen mit); Überwachung ignoriert
  `.thumbs` und neue Ordner-Ausschlüsse, eigenes Verschieben ohne Scan. `FolderAdoptionExecutionTests` angepasst (das
  `.thumbs`-Bild wird nicht mehr eingelesen). Wpf 1212 → 1224 (7 übersprungen wie vorher, dreimal grün) — Ordnerbaum (ausgeblendet + Hinweis,
  ausschließen mit Entfernen/Rückgängig, nur künftig, Wurzel/„Alle verknüpften“ gesperrt, Verschieben über Zielauswahl +
  Rückgängig, Ziehen mit Vorprüfung/Rückfrage/Ablehnung), Einstellungs-Karte (Standardliste, bereits katalogisierte entfernen,
  Namen hinzufügen + Rückgängig, ungültig/doppelt, Aufheben liest ein, Rückgängig ohne Rückkehr lässt die Regel).
- **Rauchtest:** neuer Schritt „Ausschlüsse und Ordner verschieben“ vor „Versteckte Bibliothek“ (Bild in `.thumbs` nicht
  eingelesen, Ordner ausschließen + aus dem Katalog, im Baum ausgeblendet, in den Einstellungen aufheben → wieder eingelesen,
  Ordner verschieben + Rückgängig mit derselben FileId, Screenshot `24-ordner-ausgeschlossen`, Aufräumen über den
  Test-Papierkorb). Drei Läufe: 48/48 OK (vor dem Selbst-Review), 43/48 (bekannter Wackler „Bibliotheks-Trennung“ — „Ort-Flyout
  in A wieder ohne den Ort aus B“ — mit Folgefehlern in Motive/Orte/Doppelte/Alben), Wiederholung 48/48 OK; jeweils 0
  Binding-Fehler, kein ERR/FTL außer den Wackler-Meldungen. Wpf-Suite dreimal grün.

### Offen
- Nach „Nur künftig ausschließen“ nimmt „Neu aufbauen“ die Einträge in ausgeschlossenen Ordnern ohne eigene Rückfrage heraus
  (wie jeden nicht mehr gefundenen Ordner; „Neu aufbauen“ hat seine eigene Bestätigung).
- Entfernen eines Namens aus der allgemeinen Liste liest nur die aktuelle Bibliothek sofort neu ein.
- Verschieben auf ein anderes Laufwerk derselben Bibliothek ist abgelehnt (ADR-0066).

## [0.4.32] — 2026-10-25

### Docs — Mockup 16 „Tag- und Kategorie-Symbole“, abgenommen
- `mockups/16-tag-kategorie-symbole.html`: kleine Tag- und Kategorie-Marken auf Galerie-Kacheln (oben links; Collage
  nur beim Darüberfahren), Leiste in der Lightbox mit Namen, Zahl der Fotos mit Tags/Kategorien im Ordnerbaum; Hover
  nennt die Namen; Schalter je Ort in Einstellungen › Darstellung.
- PLAN.md Kap. 7: neues Vorhaben „Tag- und Kategorie-Symbole“.


## [0.4.31] — 2026-10-25

### Fix (ADR-0049, Nachtrag 0.4.31) — BitLocker-Container beim Beenden wieder aushängen
- Beim Beenden bzw. Zusperren mit eingehängter BitLocker-VHDX kam immer „Der Container wurde nicht ausgehängt: Laufwerk X:
  konnte nicht ausgehängt werden“ (Herbert). Ursache: PowerShell `Dismount-DiskImage` braucht Administratorrechte, Lumora
  läuft ohne.
- Jetzt wie Explorer › „Auswerfen“: Erkennen über die Storage-Verwaltung (`MSFT_Partition`/`MSFT_Disk`, lesend), Auswerfen
  über das Shell-Verb „Eject“ (`ShellVirtualDiskEjector` hinter `IVirtualDiskEjector`); kein PowerShell mehr in
  `ContainerTools`. Höchstens 20 s Wartezeit; sind noch Dateien offen, bleibt der Hinweis auf „Auswerfen“.
- Tests: `ContainerToolsTests` mit Fake-Auswerfer (ausgeworfen, normales Laufwerk nie, hängt noch → Hinweis, Quelltext
  ohne PowerShell).

### Offen
- Echter Lauf auf Herberts PC: Lumora mit `_hidden` beenden, X: muss danach weg sein.

## [0.4.30] — 2026-10-25

### Feature (ADR-0065) — Ordner in den Papierkorb und fehlende Dateien bewusst aus der Bibliothek entfernen
- **Reiter „Ordner“ › Rechtsklick › „In den Papierkorb…“:** Ordner samt Inhalt (auch Nicht-Medien, Unterordner) in einem
  Schritt in den Windows-Papierkorb. Bestätigung mit Zahlen (Dateien auf der Platte, davon in der Bibliothek, Unterordner,
  Sterne/Favorit, benannte Personen, Alben, Tags/Kategorien). Protokolliert als RecycleFolder-Batch (ADR-0012) mit Abzug je
  Katalogzeile vorher; danach Verknüpfungen gesichert (ADR-0054), FTS mit alten Werten, Thumbnails-Zeilen, Files-Zeilen,
  Cache-Dateien, Folders-Zeilen. **Rückgängig** (im Ergebnisfeld des Ordnerbaums oder über die Galerie-Taste) holt den
  Ordner aus dem Papierkorb und schreibt alle Zeilen mit **denselben FileIds und Angaben** zurück.
  Abgelehnt: Wurzel selbst bzw. Ordner mit einer Wurzel darin (Hinweis auf die Einstellungen), „Alle verknüpften“,
  Laufwerk nicht erreichbar, Ordner nicht mehr vorhanden. **Laufwerk ohne Papierkorb** (Netz, USB, VeraCrypt): eigene
  Rückfrage „Dieses Laufwerk hat keinen Papierkorb — endgültig löschen?“ mit Knopf „Endgültig löschen“, sonst passiert
  nichts (DeleteFolder-Batch, nicht rückgängig). Die Ordnerüberwachung sieht den Baum als eigen (kein Diff-Scan, keine
  „fehlt“-Flut).
- **Fehlende entfernen** (Lumora vergisst Dateien, die es nicht mehr gibt — keine Datei wird gelöscht), an drei Stellen:
  Ordner-Kontextmenü **„Fehlende entfernen (n)“** (samt Unterordnern, nur bei n > 0; leere, nicht mehr vorhandene Ordner
  gehen mit aus dem Baum), Galerie Ablage **„Nur fehlende“** + **„Aus der Bibliothek entfernen“** für die Auswahl (nur wenn
  alle fehlen, sonst Hinweis), **Einstellungen › Speicher & Wartung › „Alle fehlenden entfernen (n)“** für die aktuelle
  Bibliothek. Offline-vs-Missing-Guard je Wurzel („Laufwerk nicht erreichbar — Dateien gelten als offline, nicht als
  fehlend“), erneute Existenzprüfung („wieder da“ → „fehlt“ zurückgesetzt), Bestätigung „… — diese Angaben gehen
  verloren“ mit Zahlen, RemoveFromCatalog-Batch mit Rückgängig. Nur im aktuellen Bereich, in „Alle verknüpften“ gesperrt,
  versteckte Bibliothek nur im eigenen Katalog; Protokoll auf Info-Level nur mit Zahlen.
- **Technik:** `IFileOperationService.RecycleFolderAsync`/`RemoveMissingAsync` (+ Undo-Zweig), `FolderRemovalRules`,
  `RemovalUndoStore` (Abzüge in `DuplicateCleanupUndo` mit `KeptFileId = 0` — **keine Migration**; die Seite „Doppelte“
  blendet sie aus), `IRecycleBin.GetUnavailableReasonFor`/`RestoreDirectoryFromRecycleBin` (Windows: `$R`-Ordner über den
  `$I`-Kopf), `CatalogRowSnapshot.RestoreFileRowAsCaptured`, `FileLinkUndoStore.DiscardAsync`, neue `OperationType`-Werte
  9–11, `Services/Removal` (`IRemovalService`: Vorschau, Zahlen, Guard), Wpf `Removal/RemovalActions` (Abläufe mit
  Bestätigung über `IRemovalDialogs`), `MissingFilesSettingsViewModel`, `GalleryLocation.Missing`.
- **Reiter „Ordner“ — gewählter Ordner deutlich erkennbar** (Nachtrag Herbert): Akzent-Balken links (3 px), Akzent-Tönung
  als Hintergrund, Ordnersymbol in Akzentfarbe, Name halbfett — nur DynamicResource, hell und dunkel; Knoten-Vorlage jetzt in
  `Folders/FolderTreeStyles.xaml`. Fokus und Aufklappen bleiben beim TreeViewItem-Stil.
- **Tests:** Services 1251 → 1271 (2 übersprungen wie vorher; dazu 1× `HiddenLibraryLocatorTests.Folder_rules_…` rot schon
  im Ausgangsstand — umgebungsabhängig: am geprüften Ort liegt auf diesem Rechner bereits eine versteckte Bibliothek), Wpf 1087 → 1202
  (7 übersprungen wie vorher; Suite dreimal grün; +96 durch „Nur fehlende“ in `GalleryFilterCombinationTests`) — u. a.
  Ordner in den Papierkorb + Rückgängig mit denselben FileIds/Tags/Album, Wurzel/Wurzel darin/offline/fehlend abgelehnt,
  ohne Papierkorb erst Rückfrage dann endgültig (nicht rückgängig), Shell löscht doch endgültig → DeleteFolder,
  Papierkorb-Fehler ändert nichts, Rückgängig nach Katalogfehler holt beim zweiten Versuch nur den Katalog nach, Baum für die
  Ordnerüberwachung eigen, Seite „Doppelte“ ignoriert die Abzüge; Fehlende am Ordner/Auswahl/Bibliothek mit Zahlen, Katalog
  sauber (Thumbnails, Cache-Datei, FTS, Kaskaden, `FileLinkUndo`), leere verschwundene Ordner weg (nicht bei Verknüpfung),
  „wieder da“, Offline-Guard (auch im Dienst selbst), andere Bibliothek unberührt, Rückgängig als „fehlt“; `$R`-Ordner im
  nachgebauten Papierkorb; ViewModels Ordnerbaum (Zähler, Menü nur bei n > 0, Rückgängig, „Alle verknüpften“ gesperrt),
  Galerie (Filter, Knopf, gemischte Auswahl), Einstellungs-Karte, Bestätigungstexte; Ordner-Auswahl im dunklen Design.
- **Rauchtest:** neuer Schritt „Ordner löschen und Fehlende entfernen“ vor „Versteckte Bibliothek“ (Unterordner mit 2 Fotos,
  in den Test-Papierkorb + Rückgängig, Datei löschen → „fehlt“, Zähler, Galerie „Nur fehlende“, Einstellungen-Zahl,
  „Fehlende entfernen (1)“, Screenshots `21`–`23` hell/dunkel; der Rauchtest-Papierkorb legt im Schritt nur im
  Rauchtest-AppData ab). Drei Läufe: 47/47 OK (4 Binding-Fehler im Schritt „Alben“ — bekannter Wackler beim Öffnen des
  intelligenten Albums), 46/47 (Wackler „Lightbox: Kachel 41 realisiert und fokussiert“ — `Focus()` ohne aktives Fenster,
  0 Binding-Fehler), 47/47 OK (wieder die 4 bekannten Binding-Fehler in „Alben“); kein ERR/FTL.

### Offen
- Vorschaubilder kommen bei „Fehlende entfernen“ → Rückgängig nicht zurück (Cache-Datei ist nach Repo-Regel gelöscht).
- Ein echter Durchlauf von „In den Papierkorb“ + Rückgängig gegen den Windows-Papierkorb (statt Test-Papierkorb) bei Herbert
  steht aus; die Windows-Seite ist über ein nachgebautes `$Recycle.Bin`-Verzeichnis getestet.

## [0.4.29] — 2026-10-25

### Docs — Mockup 15 „Aktivitäten unten rechts“, abgenommen
- `mockups/15-aktivitaeten.html`: eine Aktivitäts-Anzeige ganz rechts in der Statusleiste für alle Hintergrund-Vorgänge
  (Verschieben, Kopieren, Papierkorb, In die Bibliothek übernehmen, Einlesen, Entpacken, Vorschaubilder, Gesichter,
  Motive, Orte); Aufklapp-Liste „Läuft / Wartet / Zuletzt fertig“ mit Fortschritt, Ziel, Pause/Abbrechen, Rückgängig;
  kurze Meldung beim Fertigwerden; ersetzt die Einzelanzeigen rechts.
- PLAN.md Kap. 7: neue Vorhaben Aktivitäts-Anzeige, Ordner verschieben, Ausschlüsse beim Einlesen.

## [0.4.28] — 2026-10-24

### Feature (ADR-0064) — Sicherheits-Check der versteckten Bibliothek (Mockup 14)
- **Reiter Einstellungen › Sicherheits-Check** hinter „Versteckte Bibliotheken“ — nur in der geöffneten versteckten
  Sitzung (`{ IsHiddenModeEnabled: true, IsHiddenSessionActive: true }`); alle Dienste nur im DI-Container der
  versteckten Sitzung. Im normalen Modus keine Spur: kein Reiter, kein Dienst, keine Einstellung, keine Protokollzeile.
- **Prüfungen (nur lesend):** BitLocker auf C: (Shell-Eigenschaft `System.Volume.BitLockerProtection` direkt aus C# über
  `PSGetPropertyKeyFromName`/`SHGetPropertyStoreFromParsingName` — kein PowerShell), Ruhezustand (`HibernateEnabled`),
  Schnellstart (`HiberbootEnabled` + Richtlinie, ohne Ruhezustand erfüllt), Auslagerungsdatei leeren
  (`ClearPageFileAtShutdown`, ohne Auslagerungsdatei erfüllt), Zuletzt-Liste/Sprunglisten (`Start_TrackDocs` +
  `NoRecentDocsHistory`), Aktivitätsverlauf (nur die dokumentierten Richtlinien, sonst „Unbekannt“), Miniaturbilder
  (`IconsOnly` + `DisableThumbnails`/`NoThumbnailCache`), Windows-Suche auf dem Container-Laufwerk (Dienst `WSearch` bzw.
  Einschluss-Regeln des Crawl-Scope-Managers), VeraCrypt „Verlauf nie speichern“ (`SaveVolumeHistory`, alte
  `History.xml`; ohne VeraCrypt „nicht zutreffend“). Je Punkt Erfüllt / Nicht erfüllt / Unbekannt; ein Leser-Fehler wird
  „Unbekannt“ mit Hinweis, nie ein Absturz. Registry-Orte, Wertbedeutungen und Quellen in ADR-0064.
- **Gefahren-Stufe** (Ausgeschalteter PC / Mitbenutzer / Fachmännische Untersuchung, Standard Mitbenutzer) bestimmt, was
  zählt (1 / 9 / 10 Punkte, Tabelle im ADR); Ring „x/y“, **Erneut prüfen**, **Erfüllte ausblenden**; je Punkt Etikett
  (wichtigster Punkt / empfohlen / Lumora-Einstellung / Zusatz), ein Satz, **Einstellung öffnen** und ⓘ-Anleitung; Info
  „Nicht abschaltbar: Programm- und Laufwerksprotokolle“.
- **„Einstellung öffnen“** nur über einen Starter mit fester Positivliste: `control.exe` (BitLocker, Energieoptionen,
  Ordneroptionen, Indizierungsoptionen), `secpol.msc` (nur wenn vorhanden), `ms-settings:personalization-start`,
  `ms-settings:privacy-activityhistory`, `ms-settings:deviceencryption` (Home), VeraCrypt ohne Argumente. Keine freien
  Befehlszeilen; fehlt ein Ziel, ist der Knopf aus und die Anleitung gilt.
- **„Lumora nach dem Zusperren ganz beenden“** (Standard aus): beim Zusperren von Hand und nach Untätigkeit beendet sich
  Lumora ganz (`ICatalogHostSwitcher.ShutdownAfterLock` → `App.OnExit` schließt die Sitzung wie beim Beenden, Aushängen
  nach Einstellung) statt die normale Bibliothek aufzubauen.
- Gefahren-Stufe und Option in der **Settings-Tabelle des versteckten Katalogs** (`securitycheck.level`,
  `securitycheck.exitAfterLock`), keine Migration.
- **Statusleiste:** „Sicherheits-Check: x von y erfüllt“ nur in der versteckten Sitzung (erste Prüfung 2 s nach dem Öffnen).
- **Protokoll:** nur Punkt + Fehlertyp, nie Pfade, Laufwerksbuchstaben oder Ausnahme-Objekte.
- **Tests:** Services 1251 → 1304 (3 übersprungen: Modell, Messung, neu die Messung `SecurityCheckRealWindowsTests`); Wpf 1087 → 1097 (7 übersprungen wie vorher; Suite dreimal: 2× grün, 1× Wackler `TraceScanLockedRangeTests.Kurz_ganz_gesperrte_Datei…` „library.db-wal (nicht lesbar)“ unter Last) — je Prüfung Fake-Leser (Wert da/fehlt/0/1/Richtlinie
  überschreibt/Fehler), Wertung je Stufe, Starter-Positivliste inkl. Befehlszeilen-Injektion, Einstellungen im Katalog,
  Protokoll ohne Pfade, Quelltext-Wächter (keine Registry-Schreibzugriffe, kein PowerShell, kein Programmstart außer über
  den Starter), Reiter nur in der versteckten Sitzung (normal/versteckt/nach Zusperren), „ganz beenden“ von Hand und nach
  Untätigkeit, Registrierung nur im versteckten Container, Statusleiste. Messung `SecurityCheckRealWindowsTests` nur mit
  `LUMORA_SECURITYCHECK_REAL=1` (auf Herberts PC: alle Leser liefern ohne Administratorrechte Werte).
- **Rauchtest:** Schritt „Versteckte Bibliothek“ prüft zusätzlich den Sicherheits-Check mit Fake-Lesern (Statusleiste,
  Reiter, Wertung, Stufe, Ausblenden, Starter-Fake, Anleitung, Screenshots `70-sicherheits-check-hell/-dunkel`) und danach
  im normalen Modus: kein Reiter, kein Dienst, kein Statusleisten-Text, keine Einstellung. Ergebnis: 46 Schritte OK, 4 Binding-Fehler (die bekannten `GalleryListBoxItem`-Bindungen wie im Ausgangsstand), kein ERR/FTL; in zwei Vorläufen je ein bekannter Wackler („Bibliotheks-Trennung: A wieder allein“ mit Folgefehler „Motive“, „Story-Seite: Story unter Angeheftet“), der neue Teil war in allen drei Läufen grün.

### Offen
- **Echter Durchlauf mit Herbert:** Reiter in der versteckten Sitzung öffnen, Werte mit den Windows-Einstellungen
  vergleichen (v. a. Windows-Suche, Aktivitätsverlauf, VeraCrypt-Verlauf), jeden „Einstellung öffnen“-Knopf einmal
  drücken, „ganz beenden“ von Hand und nach Untätigkeit ausprobieren (Container wird ausgehängt?).
- `HiddenLibraryLocatorTests.Folder_rules_allow_drive_root_or_one_level…` hängt von der Umgebung ab: Ist auf dem PC gerade
  eine echte versteckte Bibliothek unter `X:\Fotos` eingehängt, meldet `Validate(@"X:\Fotos")` „bereits eine versteckte
  Bibliothek“ (beim Bau von 0.4.28 so aufgetreten, Code unverändert). Test sollte einen Laufwerksbuchstaben nutzen, den es
  sicher nicht gibt, oder das Dateisystem ersetzen.
- `BitLockerPlatform` (Container-Assistent) könnte die neue `ShellVolumePropertyReader` nutzen — dann gäbe es im
  Container-Ordner gar kein PowerShell mehr.

## [0.4.27] — 2026-10-24

### Docs — Mockup 14 „Sicherheits-Check“, abgenommen
- `mockups/14-sicherheits-check.html`: Einstellungs-Reiter nur in der versteckten Sitzung, der prüft, wo Windows sich
  etwas merkt (BitLocker auf C:, Ruhezustand, Schnellstart, Auslagerungsdatei leeren, Zuletzt-Liste/Sprunglisten,
  Aktivitätsverlauf, Explorer-Miniaturbilder, Windows-Suche auf dem Container-Laufwerk, VeraCrypt-Verlauf), je Punkt
  die passende Windows-Einstellung öffnet und eine Anleitung zeigt; Gefahren-Stufe bestimmt, was zählt; Lumora-Option
  „nach dem Zusperren ganz beenden“. Lumora prüft nur und ändert keine Windows-Einstellung.

## [0.4.26] — 2026-10-23

### Fix (ADR-0061, Nachtrag) — BitLocker-Container ohne PowerShell und diskpart: Lumora legt die VHDX selbst über Windows-Funktionen an
- **Anlass:** Beim ersten echten Durchlauf startete „Container erstellen“ per `runas` ein `powershell.exe -EncodedCommand …`,
  das ein diskpart-Skript nach `%SystemRoot%\Temp` schrieb und `diskpart /s` aufrief. Kaspersky (Verhaltensanalyse) sperrte
  die UAC-Erhöhung, beendete Lumora als „PDM:Trojan.Win32.Generic“ und stellte Lumora.exe unter Quarantäne — das Muster
  „Admin + PowerShell mit Base64 + diskpart“ ist typisch für Erpresser-Software.
- **Neu:** Der erhöhte Schritt ist **Lumora selbst** mit lesbarem Auftrag
  `Lumora.exe --container-create-vhdx --path "<Datei>" --size-mb <n> --letter <X> --label "<Text>"` (so auch in der
  UAC-Rückfrage unter „Details“). Eigener Einsprung `Program.Main` (`StartupObject`) erkennt das Verb **vor allem anderen** —
  kein Fenster, kein Katalog, kein Datenordner-Umzug, keine Hintergrunddienste, kein Protokoll, kein `_hidden`-Zwang;
  Ergebnis nur als Exit-Code.
- **Im erhöhten Lauf** (`VhdxElevatedJob`): strenge Prüfung der Befehlszeile (genau vier Optionen, je einmal), des Pfads
  (voller lokaler Pfad, nur `X:\…\Datei.vhdx`, keine `..`/`.`/leeren Teile, kein UNC/`\\?\`/`\\.\`, kein `:`-Datenstrom,
  keine Punkte/Leerzeichen am Rand, keine Verknüpfung im Pfad, kein Cloud-Ordner, NTFS/ReFS, Datei neu), der Größe
  (1 GB–64 TB), des Buchstabens (D–Z, frei) und der Bezeichnung. Dann `CreateVirtualDisk` (VHDX, dynamisch),
  `AttachVirtualDisk` (dauerhaft wie bisher), `GetVirtualDiskPhysicalPath` über **virtdisk.dll** und die
  **Storage-Verwaltung** (WMI `MSFT_Disk.Initialize` GPT, `CreatePartition` über den ganzen Platz, `MSFT_Volume.Format`
  NTFS, Buchstabe erst danach per `AddAccessPath`). Jeder Fehler räumt auf (abhängen + löschen, nur was der Lauf selbst
  angelegt hat; nicht durch eine inzwischen entstandene Verknüpfung hindurch).
- **Meldungen:** Exit-Codes 7101–7120 je Prüfung/Schritt in verständlichen Texten („… nicht formatieren (NTFS). (Fehler
  7116) …“). Fremder Code, Lauf nicht gestartet oder 0 ohne Laufwerk und Datei → **„Ein Sicherheitsprogramm hat den Schritt
  vermutlich blockiert …“** mit den Auswegen „Neu mit VeraCrypt“ bzw. Container selbst anlegen und „Container ist schon
  eingehängt“. UAC abgelehnt, Zeitüberschreitung (5 min) wie bisher.
- **Entfernt:** `VhdxScript` (diskpart-Skript, PowerShell-Befehl, Base64) samt Tests; Regeln heißen jetzt `VhdxRules`.
  Pfade dürfen jetzt Umlaute enthalten (der ASCII-Zwang kam nur von diskpart). Unverändert: BitLocker-Assistent
  `BitLockerWizardElev.exe X:\ T`, Statusprüfung (PowerShell **ohne** Adminrechte, Klartext), VeraCrypt-Weg, Oberfläche
  (nur der Hinweistext „Was jetzt passiert“).
- **Paket:** `System.Management` (Microsoft, war schon zentral für die Speicher-Messung) jetzt auch in `Lumora.Services`.
- **Tests:** Services 1198 → 1251 (2 übersprungen wie vorher), Wpf 1080 → 1087 (7 übersprungen wie vorher; Suite dreimal grün) — u. a.
  Befehlszeile lesbar und über `CommandLineToArgvW` verlustfrei, Ablehnung kaputter Befehlszeilen und Pfad-Tricks, Ablauf
  mit Fake je Schritt inkl. Aufräumen, Exit-Code → Meldung, P/Invoke-Strukturgrößen nach `virtdisk.h`, Quelltext ohne
  `EncodedCommand`/diskpart und `runas` nur in `ContainerProcesses.cs`, Einsprung erkennt das Verb ohne App-Start.
  Kein echter VHDX-/Admin-Lauf in Tests oder Rauchtest. Rauchtest (startet Lumora jetzt über `Program.Main`): 46 Schritte
  OK, 0 Binding-Fehler, kein ERR/FTL; im ersten Lauf 1× der bekannte Wackler „Story-Seite: Story unter Angeheftet“.

### Offen
- **Echter Test mit Herbert (Kaspersky aktiv) steht aus:** „Neu mit BitLocker“ → „Container erstellen“ einmal echt
  durchlaufen (UAC-Rückfrage zeigt „Lumora“ + Auftrag; Laufwerk erscheint; BitLocker-Assistent; Fehlerfall prüfen). Die
  Windows-Seite (`WindowsVirtualDiskApi`) ist bisher nur über Fakes und Strukturgrößen geprüft. Greift Kaspersky trotzdem
  ein, ggf. Lumora.exe signieren bzw. als vertrauenswürdig eintragen.
- Die BitLocker-Statusprüfung nutzt weiter eine (nicht erhöhte) PowerShell mit Klartext-Befehl; falls auch sie auffällt,
  auf die Shell-Eigenschaft direkt aus C# umstellen.

## [0.4.25] — 2026-10-23

### Fix — Container-Assistent im dunklen Design lesbar
- Die drei Wahlmöglichkeiten („Container ist schon eingehängt“ / BitLocker / VeraCrypt) hatten einen eigenen Stil ohne
  `BasedOn` und fielen dadurch auf den WPF-Standard-Knopf zurück: schwarze Überschriften auf dunklem Grund (Herberts
  Screenshot beim ersten echten Durchlauf). Stil baut jetzt auf dem WPF-UI-Knopf auf, Schrift `TextFillColorPrimaryBrush`;
  Test `Auswahl_im_dunklen_Design_lesbar`.

## [0.4.24] — 2026-10-23

### Feature (ADR-0063) — Story-Assistent: Sätze in eigenen Worten, lokale Sprach-KI nur für die Absicht, Collage-Bild
- **Seite Storys:** Eingabeleiste mit Beispielen und Hinweis „läuft nur auf diesem PC“ (Mockup 12). Ein Satz („Collage mit
  meiner Schwester bis 20 Jahre“, „Weihnachten bei Oma, alle Jahre“, „Erstelle mir ein Album mit den schönsten
  Urlaubsfotos der letzten fünf Jahre mit Nicole und Nina“) geht an eine **lokale Sprach-KI über Ollama**
  (`http://127.0.0.1:11434`, sonst nichts) — sie sieht nur den Satz, das Datum, Namen/Rufnamen/Beziehungen der Personen und
  die Namen von Motiven, Tags und Kategorien, **nie Fotos, Pfade, Koordinaten oder Geburtsdaten**, und liefert nur eine
  Absicht (`StoryIntent`, JSON-Schema über Ollamas `format`, Temperatur 0, `keep_alive` 60 s) — keine Titel, keine Texte.
  Strikte Prüfung (unbekannte Felder, Typen, Werte, Grenzen, abgeschnittene Antwort).
- **Auflösung in Lumora:** Personen über Namen/Beziehungen mit Synonymen und „ich“; mehrdeutig/unbekannt → **Rückfrage**
  mit Gesicht und Fotozahl („Beide“), die Antwort wird als Alias gemerkt (Einstellungen › Assistent: vergessen). Zeitraum
  absolut, **„letzte N Jahre“ relativ zu heute** (als konkrete Daten), **Alter aus dem Geburtsdatum** (fehlt es → Rückfrage),
  **jedes Jahr** (auch über den Jahreswechsel). **Begriffe** gegen Tags, Kategorien (Name/Wert), Motive, Personen, Länder/
  Regionen und die Offline-Ortssuche — mehrere Treffer **vereinigt und mit Zahlen** („Barcelona: Tag (212) · Ort (180)“), auf
  einen umschaltbar; kein Treffer → **„nicht gefunden“** markiert. **Urlaub/Reise** über die Reise-Erkennung der Storys plus
  passende Tags/Kategorien (z. B. Kategorie „Reisen“, Standard vereinigt); ohne eingestelltes Zuhause **Rückfrage** („Zuhause
  ist Graz“ speichert es, „In Einstellungen › Storys festlegen“, „Ohne Reise-Erkennung“). Mehrere Personen Standard **alle
  zusammen auf dem Foto**, umschaltbar auf „mindestens eine“. **„Die besten N“** über die Highlight-Auswahl; „die schönsten“
  ohne Zahl → Vorschlag (bis 24 alle, sonst ≈ 10 %, 24–100), in der Karte 24/50/100/200/alle. Alles im aktuellen Bereich.
- **Karte „So habe ich es verstanden — stimmt das?“:** jeder Punkt per Klick änderbar (neu gezählt ohne Sprach-KI),
  Trefferzahl, Vorschau-Streifen; **Erstellen** (erst jetzt: eigene Story bzw. Album mit sachlicher Überschrift wie „Nicole
  und Nina · Urlaub 2023–2025“, Diashow, Galerie, Collage-Bild ggf. + Story), **Nur Fotos zeigen (Galerie)**, **Satz ändern**,
  **Abbrechen**; Fortschritt mit Abbrechen; **Zuletzt gefragt** (Öffnen/Wieder abspielen ohne erneute KI-Anfrage, löschbar).
- **Ohne Ollama:** Hinweis mit Weg in Einstellungen › Assistent; **regelbasierter Rückfall** für klare Sätze (Name/Beziehung/
  Begriff + Jahr, „die besten N“, „letzte N Jahre“, Urlaub, Aktionswort) — sonst „dafür braucht es die Sprach-KI“.
- **Als Collage-Bild speichern** (Dialog wie Mockup): Format 16:9 / A4 quer / A4 hoch / Quadrat, Full HD / 4K / Druck
  300 dpi, 12/24/36/48 Fotos, Großes Titelbild / Raster / Zeitstrahl (lückenlos, ohne Überlappung), sachliche Überschrift
  (änderbar) und Datum je Foto, Neu mischen, Foto per Klick tauschen; gerendert aus den **Originalen** (WIC, EXIF-Ausrichtung,
  eigener STA-Thread, Temp-Datei im Zielordner), JPEG/PNG; gespeichert nur über den Dateidialog (ohne „Zuletzt verwendet“).
  Der Menüpunkt der Story-Ansicht ist jetzt aktiv. Versteckte Bibliothek: nur im Container (`<Bibliothek>\Collagen`).
- **Einstellungen › Assistent** (neuer Reiter nach „Storys“): an/aus, Ollama-Status + **Ollama installieren…** (winget
  `Ollama.Ollama`, erst nach Bestätigung mit Name, Quelle, Größe, Lizenz) bzw. **Ollama starten**, Sprachmodell (Standard
  `qwen2.5:3b`, Alternative `qwen2.5:7b`, frei wählbar) + **Herunterladen…** (genaue Größe vorher aus dem Manifest der
  Ollama-Registry, Bestätigung, Fortschritt, Abbrechen), **Grafikkarte nutzen** (während einer Anfrage hält der Assistent
  `IGpuGate` — Gesichts-/Motiv-Index warten zwischen zwei Fotos; aus = nur CPU), Verlauf merken + löschen, gemerkte Antworten,
  **Ausprobieren** (zeigt nur das Verständnis).
- **Daten:** Migration `AddStoryAssistant` (nur CREATE TABLE): `AssistantHistory`, `AssistantAliases` je Bibliotheks-Gruppe
  (Verknüpfen/Lösen/leere Gruppe über `AssistantGroupStore`, in `LibraryGroupStore.TermTables`); Einstellungen
  `assistant.*` je Katalog. Versteckte Bibliothek: Verlauf, Antworten und Wortschatz nur aus dem versteckten Katalog.
  Protokoll ohne Satz und Namen („Assistent: Anfrage, n Treffer“).
- **Rauchtest:** neuer Schritt „Story-Assistent“ vor „Versteckte Bibliothek“ mit Attrappe statt Ollama (kein Netzwerk):
  Satz → Karte → eigene Story → Collage aus den Originalen in Temp (1920 × 1080) → Verlauf, aufgeräumt; Screenshots 67–68.
- **Tests:** Services 1062 → 1147 (2 übersprungen wie vorher), Wpf 1058 → 1068 (7 übersprungen wie vorher); Wpf-Suite
  dreimal: 2× grün, 1× der zeitabhängige Wackel-Test `TraceScanLockedRangeTests.Kurz_ganz_gesperrte_Datei_wird_nach_
  erneutem_Lesen_durchsucht` („library.db-wal (nicht lesbar)“, unverändert, nicht angefasst); Rauchtest 45 Schritte OK,
  0 Binding-Fehler (im ersten Lauf 12 im Schritt „Alben“ — bekannt wackelig unter Last).
- **Selbst-Review** (`code-review` medium, 10 Befunde): behoben — Collage überschrieb still den Vorschlag (jetzt nur über den
  Dateidialog), veraltete Ergebnisse nach Bibliothekswechsel/neuem Satz (Generationszähler), Videos verkleinerten den
  Collage-Vorrat, Story vor fehlendem Collage-Dialog angelegt, Wortschatz bei jeder Antwort neu gezählt (2 min
  Zwischenspeicher, Antwort setzt zurück), Urlaubs-Treffer mit 45 Zählabfragen (jetzt je Art eine), Dispatcher des
  Render-Threads, toter Code. CHANGELOG/Temp-Datei: erledigt.

### Offen
- Rauchtest „Bibliotheks-Trennung: gelöst: A wieder ohne den Ort aus B“ beim Einspielen 1 von 3 Läufen rot (mit dem Galerie-Fix aus 0.4.23 lädt die Galerie nur noch A). Die Prüfung ist jetzt in „Karte“ und „Ort-Flyout“ (mit dem Galerie-Bereich in der Meldung) aufgeteilt, damit der nächste rote Lauf die Stelle zeigt.
- **Echter Test mit Herbert**: Ollama über Einstellungen › Assistent installieren und `qwen2.5:3b` herunterladen (beides
  nur nach Bestätigung), dann Herberts Sätze ausprobieren — Prompt und Beispiele ggf. nachschärfen; Antwortzeit auf der
  GTX 1080 Ti messen; ob `qwen2.5:7b` als Standard besser passt.
- Gemerkte Antworten unter **Personen** ändern (Mockup) — bisher nur in Einstellungen › Assistent vergessen.
- „Nur Fotos zeigen (Galerie)“ nutzt den Datei-Filter der Galerie (Chip „Kartenausschnitt (n Dateien)“, wie ADR-0060).
- Personen zählen nur über bestätigte Gesichter; Orte als Rechteck ± 20 km.

## [0.4.23] — 2026-10-23

### Feature (ADR-0062) — Ungenaue Orte verbessern und Garmin-Spuren (Mockup 10)
- **„Position geschätzt“ erkennen:** im Hintergrund (Statusleiste „Orte prüfen: n / m“, Scan-Sperre und Katalog-Umzug
  beachtet) liest Lumora GPS-Höhe, GPS-Zeit, Verfahren und Fehlerradius: nur Breite/Länge ohne Höhe und Zeit,
  NETWORK/CELLID/WLAN/WIFI oder > 500 m → geschätzt; dazu ein **unmöglicher Sprung** (> 150 km/h, > 2 km) zu genauen
  Fotos ≤ 30 min. Neue Spalten `Files.GeoAccuracy/-Reason/GpsTimeUtc/ExifTimeOffset/CameraModel/GeoSuggestionState`
  (Migration `AddGpsTracks`, nur ADD COLUMN). Kopf der Seite Orte: „… · **n mit geschätzter Position**“.
- **GPS-Spuren** je Bibliotheks-Gruppe in `GpsTracks` — eine Zeile je Aktivität, Punkte delta-kodiert und komprimiert
  (5 000 verrauschte Punkte ≈ 13 KB), Prüfsumme gegen Doppel-Import; Verknüpfen führt zusammen, Lösen kopiert, leere Gruppe
  löscht. **Einlesen:** Garmin-Datenexport-ZIP (innere `UploadedFiles_*.zip` gestreamt, Temp nur im Katalog bzw.
  Container), einzelne **FIT/GPX/TCX**-Dateien, **beobachteter Ordner**, **Uhr per USB** (nur als Laufwerk mit
  `GARMIN\Activity`, vorher fragen) — im Hintergrund mit Fortschritt und Abbrechen, Doppelte übersprungen. Eigener,
  kleiner **FIT-Leser** statt Garmin FIT SDK (Lizenz verbietet die Weitergabe mit der portablen Version). Kein
  API-Schlüssel, kein Login — nichts wird an Garmin gesendet.
- **Zeitabgleich:** OffsetTimeOriginal › GPS-Zeit des Fotos › Zeitzone der Fotos (Standard Windows mit Sommerzeit je
  Datum), dann **Kamera-Abweichung je Kamera** (Vorschlag „aus GPS-Zeit ermittelt“ für Kameras mit GPS-Zeitstempel);
  Toleranz vor/nach einer Aktivität (Standard 2 min).
- **Vorschläge mit Stufen** (rein, getestet): **Garmin-Spur** (Interpolation zwischen Spurpunkten mit kurzer Lücke bzw.
  in einer Pause) › **sicher** (zwischen zwei genauen Fotos ≤ 30 min, plausible Geschwindigkeit) › **wahrscheinlich**
  (Nachbarfoto ≤ 15 min bzw. Spur mit langer Lücke) › kein Vorschlag — für „Ungenau“ und für „Ohne Ort“.
- **Seite Orte:** Umschalter **„Ohne Ort n | Ungenau m“**, Erklärung, Filter-Chips, Liste nach Tagen/Ausflügen mit
  Plaketten, ✓ Übernehmen / ✕ Ablehnen / Ort setzen…, **„Garmin + sichere übernehmen (n)“**, **„Ausflug (n)“**,
  Speichern **nur im Katalog** oder **auch ins Foto schreiben (EXIF)**, Hinweis mit **Rückgängig**; Karte mit Spur
  (korallenrot), gespeichertem Punkt (grau), Vorschlag (Akzent, **ziehbar**), gestrichelter Korrektur und Legende.
  Übernommene Orte tragen die neue Herkunft **„aus GPS-Spur“** bzw. **„aus Nachbarfotos“** (`GeoSource` 4/5), überleben
  Inhaltsänderungen und „Neu aufbauen“ und werden nie automatisch überschrieben. Unter „Ohne Ort“: „n Fotos … mit
  Vorschlag — **Vorschläge zeigen**“.
- **Lightbox:** Hinweis „Position geschätzt“ mit Begründung, Vorschlag samt Quelle und Entfernung, **Vorschlag
  übernehmen**, **Auf der Karte zeigen**, **Ort setzen…**.
- **Einstellungen › Orte (neuer Reiter):** Garmin-Aktivitäten (Erklärung, **Garmin-Datenexport öffnen**, **Export-ZIP
  einlesen…**, FIT/GPX-Dateien, Ordner beobachten, USB-Erkennung, Stand, **Aktivitäten anzeigen**, **Spuren entfernen…**)
  und Zeitabgleich (Zeitzone, Toleranz, Kamera-Abweichungen, Vorrang). Statusleiste: „GPS-Spuren: n / m Dateien“ bzw.
  „Orte: n geschätzt · m Vorschläge · k Garmin-Aktivitäten“.
- Rauchtest-Schritt **„Ungenaue Orte“** vor „Versteckte Bibliothek“ (synthetische GPX-Spur im Lauf, kein Netz).

### Fix beim Einspielen — Galerie: schnelle Bereichswechsel
- Zwei `ActiveLibraryChangedMessage` kurz hintereinander (z. B. „Lösen“ meldet verzögert die alte aktive Bibliothek,
  gleich danach der Wechsel) liefen in der Galerie parallel; der zuletzt **fertige** Ladevorgang gewann, sodass die
  Galerie (Ort-Flyout, Bereich) auf der falschen Bibliothek landen konnte — im Rauchtest „Bibliotheks-Trennung: gelöst: A
  wieder ohne den Ort aus B“ mit vier Folgefehlern (in 2 von 6 Läufen). Jetzt gewinnt der zuletzt **angestoßene**
  (Generationszähler in `GalleryViewModel.ReloadLibraryAsync`/`LoadLibraryAsync`); Test
  `Schnelle_Bereichswechsel_der_zuletzt_angestossene_gewinnt`.

### Offen
- Rauchtest „Story-Seite“: einmal (1 von 2 Läufen beim Einspielen) „Übersicht: Story unter „Angeheftet““ rot, direkt nach
  Diashow + Zurück — vermutlich Neuladen der Übersicht parallel zum Rauchtest-`LoadAsync`; Story-Seite unverändert seit 0.4.15.
- **MTP-Uhren** (Fenix 6/7 u. a. melden sich wie ein Handy, nicht als Laufwerk) werden nicht direkt gelesen — Weg über
  den Garmin-Datenexport bzw. den Ablageordner von Garmin Express.
- **Zeitzone aus dem Ort** statt aus der Einstellung/Windows (Urlaubsfotos von Kameras ohne Zonenangabe).
- **Google-Standortverlauf** (Timeline) als weitere Spur-Quelle importieren.
- Von Lumora ins EXIF geschriebene Orte enthalten nur Breite/Länge — in einem fremden Katalog gälten sie als „geschätzt“.

## [0.4.22] — 2026-10-23

### Docs — Mockup 13 „Foto verbessern“ (Welle 8), abgenommen
- `mockups/13-foto-verbessern.html`: Lightbox-Leiste „Verbessern“ (Automatisch, Hochskalieren und Schärfen, Gesichter
  restaurieren, Einfärben, Entfernen mit Pinsel bzw. Kratzer automatisch, Zuschneiden/Drehen/Licht) mit Vorschau und
  Vorher/Nachher-Regler; Ergebnis immer als neue Datei neben dem Original, verknüpft; mehrere Fotos aus der Galerie
  im Hintergrund; Galerie-Paare „2 Fassungen“; Einstellungen › Verbessern mit Modellen (Real-ESRGAN, GFPGAN,
  DeOldify, LaMa) nur nach Bestätigung mit Name, Quelle, Größe, Lizenz.

## [0.4.21] — 2026-10-22

### Docs — Geplante Vorhaben in PLAN.md, neu: Nutzungs-Signale
- PLAN.md Kap. 7 „Geplante Vorhaben“: Stand der laufenden und geplanten Arbeiten (Ungenaue Orte + GPS-Spuren, Story-
  Assistent, App-Verbindung, Android, Foto verbessern, Update-Manager).
- Neu eingeplant (Herbert): **Nutzungs-Signale** — lokal messen, wie oft und wie lange Fotos angesehen, gezoomt,
  verwendet, gedruckt oder geteilt werden; nur Tagessummen, abschaltbar und löschbar; fließt gedämpft in die Auswahl
  „die schönsten“ ein und ermöglicht die Story-Art „Vergessene Schätze“.

## [0.4.20] — 2026-10-22

### Docs — Mockup 12 „Story-Assistent“ (Welle 6b), abgenommen
- `mockups/12-story-assistent.html`: Sätze in eigenen Worten auf der Seite Storys („Collage mit meiner Schwester bis
  20 Jahre“) → lokale Sprach-KI (Ollama, kleines Modell) übersetzt nur in Filter und Aktion, sieht keine Fotos;
  Lumora zeigt das Verständnis (Person mit Beziehung, Zeitraum aus Geburtsdatum, Medien, Story/Collage/Album/
  Diashow) mit Treffern zur Bestätigung, fragt bei Mehrdeutigkeit nach und merkt sich die Antwort; Verlauf.
- Dialog **„Als Collage-Bild speichern“** (Format, Größe, Anzahl, Anordnung, sachliche Überschrift, Neu mischen,
  Fotos tauschen) und Einstellungen › **Assistent** (Ollama und Modell nur nach Bestätigung mit Name, Quelle, Größe;
  Grafikkarte teilen; Verlauf; Ausprobieren ohne Erstellen).

## [0.4.19] — 2026-10-22

### Docs — Mockup 11 „Verbundene Apps“, abgenommen
- `mockups/11-verbundene-apps.html`: neuer Reiter Einstellungen › **Verbundene Apps**, allgemein für künftige Apps
  (erste: Gipfelbuch). Verbindungen erlauben, Hintergrundstart durch andere Apps (ohne Fenster, schlank, beendet sich
  selbst), Symbol im Infobereich wahlweise; Karte je App mit Status (läuft / im Hintergrund / beendet), Rechten je
  Richtung (Fotos suchen, Vorschaubilder, Originale öffnen, Personen; Touren mit GPS-Spur, Gipfel/Tourdaten für
  Storys), freigegebenen Bibliotheken, Zugriffszahlen ohne Dateinamen, Trennen; Kopplungs-Dialog mit Code. Versteckte
  Bibliotheken sind für verbundene Apps nie sichtbar.
- Die Schnittstelle selbst steht im eigenen Vertrags-Repo **app-verbindung** (`VERTRAG.md`, Version 0.1 Entwurf);
  Lumora schreibt sie nicht ab, sondern verweist darauf.

## [0.4.18] — 2026-10-22

### Feature (ADR-0061) — Versteckte Bibliotheken: Container-Assistent (BitLocker oder VeraCrypt)
- „Versteckte Bibliothek anlegen…“ fragt zuerst: **Container ist schon eingehängt** (bisheriger Weg), **Neuen
  verschlüsselten Container erstellen — BitLocker** oder **— VeraCrypt**. Am Ende beider neuen Wege folgt der bisherige
  Anlegen-Dialog mit dem neuen Laufwerk vorgewählt.
- **BitLocker:** Speicherort (Vorschlag `<NTFS-Laufwerk>\Daten\sicherung.vhdx`, Cloud-Ordner und Nicht-NTFS gesperrt,
  Hinweis beim Systemlaufwerk), Größe aus dem freien Platz (dynamisch erweiterbar), freier Buchstabe, Bezeichnung
  „Daten“. Ein erhöhter PowerShell-Lauf (`Verb=runas`, eine UAC-Rückfrage, Befehl als `-EncodedCommand`) schreibt das
  diskpart-Skript (ASCII, ohne Kennwort) erst selbst nach `%SystemRoot%\Temp`, führt es aus, räumt bei einem Fehler die
  halbe VHDX ab und löscht das Skript immer — so kann kein anderer Prozess es zwischen Schreiben und UAC-Zustimmung
  austauschen, und unter `%LocalAppData%\Lumora` entsteht keine Datei (Abweichung vom Plan „Skript im `TempRoot`“, aus
  dem Selbst-Review); danach der Windows-BitLocker-Assistent (`BitLockerWizardElev.exe X:\ T`) mit Hinweisen
  (Wiederherstellungsschlüssel nicht ins Microsoft-Konto, kein automatisches Entsperren); „Verschlüsselung prüfen“ liest
  `System.Volume.BitLockerProtection` ohne Adminrechte. Windows Home → Hinweis und VeraCrypt; UAC abgelehnt → nichts
  bleibt; Buchstabe belegt → Meldung.
- **VeraCrypt:** fehlt es, Bestätigungsseite (Paket `IDRIX.VeraCrypt`, Quelle winget, ca. 40 MB, Adminrechte, Lizenz)
  und erst auf **Zustimmen und installieren** `winget install --id IDRIX.VeraCrypt -e --accept-package-agreements
  --accept-source-agreements` (Ausgabe sichtbar, Fehler verständlich); ohne winget Link zu veracrypt.fr. Dann
  `VeraCrypt Format.exe` mit Schritt-Anleitung — Standard **verstecktes Volume** mit Warnungen, wählbar normaler
  Container — und Einhängen über das vorhandene „Container öffnen…“ (VeraCrypt fragt das Kennwort; nicht aus einem
  Cloud-Ordner). „Erneut prüfen“ erkennt ein von Hand installiertes VeraCrypt ohne zweites winget.
- Abbrechen auf jeder Seite ohne Seiteneffekte (mit Hinweis, wenn schon ein Container angelegt bzw. eingehängt ist);
  nichts davon im Protokoll; Dateidialoge ohne „Zuletzt verwendet“.
- Selbst-Review (`code-review` medium, 9 Befunde): behoben — Skript-Austausch vor der UAC-Zustimmung, Zeitüberschreitung
  räumte zu früh auf, halbe VHDX blieb eingehängt, VeraCrypt-Container aus Cloud-Ordnern, Nicht-NTFS-Laufwerke,
  Abbrechen auf „bereit“ ohne Hinweis, kein „Erneut prüfen“ neben winget, Laufwerksliste bei jedem Tastendruck
  (jetzt 10 s zwischengespeichert). Begründet offen: eigene `IProcessOutputRunner`/`IDriveSpaceSource` neben
  `IProcessRunner`/`IDriveRootSource` (andere Aufgabe: Ausgabe mitlesen bzw. freier Platz + Dateisystem; die alten
  Schnittstellen bleiben für bestehende Fakes unverändert).
- Neue Schnittstellen (Services): `IContainerCreation`, `IElevatedRunner`, `IProcessOutputRunner`, `IShellLauncher`,
  `IBitLockerPlatform`, `IVeraCryptInstaller`, `IDriveSpaceSource`; Oberfläche: `ContainerAssistantWindow` +
  `ContainerAssistantViewModel`, `IContainerAssistant`, `IContainerFilePicker`. Kein neuer Rauchtest-Schritt (der
  Rauchtest legt wie bisher direkt an).
- Tests nur mit Fakes (nie echt diskpart/BitLocker/winget/VeraCrypt/UAC): `ContainerCreationTests` (Skript-Befehle,
  Maskierung, kein Kennwort, Skript gelöscht, UAC abgelehnt ohne Rest, belegter Buchstabe/Cloud/Größe, BitLocker-
  Assistent und -Zustand, Windows Home, winget-Befehl und -Meldungen, VeraCrypt-Assistent, Anleitung),
  `ContainerAssistantTests` (Zustandsmaschine, Abbrechen auf jeder Seite, Übergabe an den Anlegen-Dialog, nichts im
  Protokoll, Fenster).

### Offen — erster echter Durchlauf mit Herbert (Checkliste)
- BitLocker: UAC-Rückfrage erscheint genau einmal; diskpart legt VHDX an (dynamisch), GPT, NTFS, Bezeichnung, Buchstabe;
  keine Explorer-Meldung „Datenträger formatieren“; Skript in `%SystemRoot%\Temp` danach weg, PowerShell-Here-Strings und`n  `$LASTEXITCODE` von diskpart wie erwartet, Aufräumen nach absichtlichem Fehler (z. B. belegter Buchstabe); Assistent öffnet
  sich für das richtige Laufwerk; Statuswerte von `System.Volume.BitLockerProtection` (1/3 nach dem Assistenten, 6 nach
  dem Sperren) stimmen; Doppelklick auf die VHDX nach Neustart fragt das Kennwort; Aushängen beim Zusperren.
- VeraCrypt: winget-Rückgabewerte (abgelehnte UAC des Installers, schon installiert), Ausgabe-Zeilen lesbar (Umlaute),
  `VeraCrypt Format.exe` startet (ggf. eigene UAC), Bezeichnungen der Anleitung passen zur installierten VeraCrypt-Version
  (deutsch/englisch), Dateisystem-Empfehlung für das äußere Volume, Einhängen des inneren Bereichs mit `/volume … /letter`.
- Allgemein: Fensterhöhe des Assistenten auf Herberts Bildschirm, hell/dunkel.
- Rauchtest 43/43 OK; die bekannten 4 Binding-Fehler beim Öffnen des intelligenten Albums (0.4.10 „Offen“, Galerie
  `GalleryListBoxItem`/`HeartButton`/`RatingStars`) traten in 2 von 2 Läufen auf — Alben/Galerie sind hier unverändert.
- Tests: Lumora.Tests 1062 grün (2 übersprungen), Lumora.Wpf.Tests 1036 grün (7 übersprungen), dreimal hintereinander.

## [0.4.17] — 2026-10-22

### Docs — Mockup 10 um Garmin-Spuren erweitert, abgenommen
- Beste Quelle für ungenaue Orte und Fotos ohne Ort sind die **GPS-Spuren der Garmin-Uhr**: Stufe „Garmin-Spur“ über
  „sicher“, Spur der Aktivität auf der Karte, Begründung im Lightbox-Hinweis; Fotos vor Start bzw. nach Ende einer
  Aktivität fallen auf die bisherigen Vorschläge zurück.
- **Einstellungen › Orte:** Link zum Garmin-Datenexport, „Export-ZIP einlesen…“ (FIT-Dateien), Ordner mit FIT/GPX
  beobachten, Uhr per USB erkennen (vorher fragen), Stand der Aktivitäten, Spuren entfernen; Zeitabgleich mit Zeitzone
  automatisch, Toleranz, Kamera-Abweichung aus der Spur ermittelt; Vorrang Garmin-Spur › Foto mit GPS › Handy-Schätzung,
  von Hand gesetzte Orte nie überschreiben. Nichts wird an Garmin gesendet.
- Kein API-Schlüssel: Garmin vergibt die Connect-API nur an Firmen und Einrichtungen, das Programm ist derzeit pausiert.

## [0.4.16] — 2026-10-22

### Docs — Mockup 10 „Ungenaue Orte verbessern“, abgenommen
- `mockups/10-ungenaue-orte.html`: Seite Orte mit Bereich **„Ungenau“** neben „Ohne Ort“ für Fotos mit geschätzter
  Position (Handy hat nur Breite/Länge ohne GPS-Höhe/-Zeit gespeichert, oder unmöglicher Sprung zu zeitnahen Fotos).
  Vorschläge aus zeitnahen Fotos mit echtem GPS (zwischen zwei Nachbarn gemittelt bzw. ein naher Nachbar), Stufen
  sicher/wahrscheinlich/kein Vorschlag; Karte mit altem Punkt, Vorschlag und Korrektur-Linie; Übernehmen je Foto, je
  Ausflug oder alle sicheren, von Hand ziehen, Speichern nur im Katalog oder auch ins Foto, Rückgängig; Lightbox-Hinweis
  „Position geschätzt“. Hell/dunkel, Stil aus Mockup 08/09.

## [0.4.15] — 2026-10-22

### Feature (ADR-0060) — Story-Seite (Welle 6, Phase 2, Mockup 09)
- **Navigation „Storys“** nach „Alben“ mit Zähler **„n neu“** (ungesehene Storys, aktualisiert nach jeder Änderung und
  beim Bibliothekswechsel); Statusleiste **„Storys aktuell“ / „Storys werden berechnet …“**.
- **Übersicht** als virtualisierte Liste: Kopf mit Zählern und Suche (Ort, Person, Jahr), Art-Filter mit Zählern (Alle,
  Ereignisse & Reisen, Rückblicke, Personen, Orte), Banner **„Heute vor N Jahren“** (nur wenn es heute einen gibt; Öffnen,
  Diashow), Angeheftet, Neu, Reisen & Ereignisse mit Jahres-Chips, Personen (Karte mit Gesicht), Orte, eigene Storys;
  Karten mit Titelbild-Collage, Art-Abzeichen, Anheft-Symbol, Zählern, Personen-Rundbildern und „Diashow“ beim Überfahren;
  **Ausgeblendet (n)** mit „Wieder zeigen“; „Story aus Auswahl…“ als Hinweis. Bilder und Personen erst für sichtbare Reihen.
- **Story-Ansicht:** Brotkrumen, großes Titelbild (scharf aus dem Original) mit sachlicher Überschrift, Orten, Zählern und
  Personen, Knöpfe Titelbild/Anheften und Menü „…“ (Titel ändern = nur eigener Text, Titelbild wählen, Fotos
  hinzufügen/entfernen, Anheften, Als Album speichern, In der Galerie zeigen, Ausblenden, Neu berechnen, Löschen für eigene
  Storys/Rückblicke, **„Als Collage-Bild speichern…“ deaktiviert — Welle 6b**). Fixierte Leiste Highlights/Alle Fotos mit
  Zählern, Tages-Chips (Sprung, klappt eingeklappte Tage auf), Diashow (Highlights bzw. alle, ab Anfang). Tagesabschnitte
  mit **Collage-Blöcken** aus der neuen, reinen `StoryLayoutEngine` (groß+2, 3er, 4er, 2+groß im Wechsel, Panoramen über die
  volle Breite, Hochformate nebeneinander, lückenlos auf ganze DIP), Videos mit ▶ und Dauer, „+n ähnliche“, „warum
  ausgewählt“ beim Überfahren, Favoriten-Stern; „Alle n Fotos dieses Abschnitts“ klappt dicht auf (Nicht-Highlights blass);
  ab fünf Tagen die ersten drei offen, Rest „n weitere Tage … Anzeigen“; Ende-Kasten mit Zählern, Album, Diashow.
  Doppelklick → Lightbox mit den Fotos der Story. Öffnen markiert als gesehen. Eine Story über einen Tag (Rückblick) ist nach
  Tageszeit geteilt (Vormittag/Nachmittag/Abend).
- **Galerie-Kontextmenü:** **Als Story…** (Auswahl → eigene Story, danach geöffnet), **In Storys zeigen**, bei „Fotos
  hinzufügen…“ **Zur Story „…“ hinzufügen**.
- **Einstellungen › Storys** (neuer Reiter nach „Darstellung“): Story-Arten an/aus, Zu Hause (automatisch mit erkanntem Ort
  oder Ort über die Offline-Ortssuche), Pause, Mindestgröße, Highlights-Anteil; der Abschnitt **Diashow** ist aus
  „Darstellung“ hierher gezogen. „Unscharfe/dunkle nie als Titelbild“ ist kein Schalter (der Dienst kennt keine solche
  Einstellung; die Titelbild-Wahl tut es immer) — als Hinweis in der Karte „Highlights“.
- Kacheln und Karten sind UI-Automation-Blätter (`StoryLeafButton`), Bilder zeichnet `StoryImage` (keine gebundenen
  `ImageBrush` in virtualisierten Vorlagen). Keine Migration, keine neue Abhängigkeit.
- Rauchtest: neuer Schritt **„Story-Seite“** nach „Diashow“, vor „Versteckte Bibliothek“ (Übersicht, Story, Highlights ↔
  Alle, Anheften, Diashow + Esc, zurück; Screenshots 62–66 hell/dunkel). Neue Wpf-Tests unter `tests/Lumora.Wpf.Tests/Stories`.

### Offen
- „In der Galerie zeigen“ einer Story nutzt den Datei-Filter der Karte; die Bedingung heißt in der Galerie deshalb
  „Kartenausschnitt (n Dateien)“ — ein eigener Galerie-Filter „Story“ wäre ein Folgeschritt.
- Im Rauchtest scheiterte der Schritt „Motive“ („eigener Begriff gelöscht“: die Liste in Einstellungen › Motive zeigte den
  gelöschten Begriff weiter), solange ein schon beim Start erzeugtes Story-ViewModel bei jedem Bibliothekswechsel die
  Übersicht lud. Behoben, indem die Seite erst nach dem ersten Anzeigen lädt; die Ursache (vermutlich eine Wettlaufsituation
  im Neuladen der Motiv-Begriffe, `MotifSettingsViewModel.LoadTermsAsync` ohne Reihenfolge-Schutz) ist nicht abschließend
  geklärt.

## [0.4.14] — 2026-10-22

### Change (ADR-0049, Nachtrag) — Versteckte Bibliotheken: nach dem Zusperren keine Spur mehr
- Herbert: Mit `_hidden` gestartet und zurück zur normalen Bibliothek gewechselt, stand in den Einstellungen weiter der
  Reiter „Versteckte Bibliotheken“ — „das darf auf keinen Fall sein“.
- **Zusperren beendet den versteckten Modus für den Rest der Laufzeit** (von Hand, nach Untätigkeit, über den
  Umschalter): danach kein Reiter, keine Einträge im Bibliotheks-Umschalter, kein Suchen auf den Laufwerken — wie ein
  normaler Start. Wieder öffnen nur nach einem Neustart mit `_hidden`.
- **Reiter „Versteckte Bibliotheken“ nur in einer geöffneten versteckten Bibliothek**; in der normalen Bibliothek mit
  `_hidden` laufen Anlegen, Öffnen und „Container öffnen…“ über den Bibliotheks-Umschalter.
- Tests: neuer `Locking_ends_hidden_mode_until_restart`, Reiter-Test erweitert (normal: nie, versteckt: ja, nach dem
  Zusperren: nie), Umschalter nach dem Zusperren leer; Wieder-Öffnen-Tests simulieren den Neustart mit einem neuen
  Sitzungsverwalter. Rauchtest prüft das Ende des Modus und simuliert dann den Neustart (`RestartHiddenModeForSmoke`).
- README (Versteckte Bibliotheken) und ADR-0049 (Nachtrag) angepasst.

## [0.4.13] — 2026-10-22

### Fix — Dialoge ohne leeren Raum unter den Knöpfen
- Herbert: „die ganzen Entfernen-Dialoge sehen unschön aus“ — unter der Knopfleiste blieb eine große leere Fläche.
  Ursache: Der FluentWindow-Stil von WPF-UI gibt jedem Fenster eine Mindesthöhe von 320 px; der Entfernen-Dialog
  braucht nur ≈ 222 px.
- `AppFluentWindow` hebt diese Mindesthöhe für Fenster mit Höhe aus dem Inhalt (`SizeToContent`) auf; eine eigene
  Mindesthöhe aus XAML/Code bleibt. Betrifft alle 11 Dialoge: Bestätigungen/Meldungen (`AppDialogWindow`), Tags,
  Kategorien, Personen (Auswahl, Details), Aufnahmedatum, Ort setzen, Verorten, Entpacken, Bibliotheken verknüpfen,
  versteckte Bibliothek. Lightbox und Hauptfenster unverändert.
- Neue Tests `ContentSizedWindowHeightTests` (Dialoghöhe = Inhaltshöhe, eigene Mindesthöhe bleibt, Fenster mit fester
  Höhe behalten die Mindesthöhe).

## [0.4.11] — 2026-10-22

### Docs — Projektordner `D:\Code\Lumora` mit `repo\` und `werkzeuge\`
- Herbert: ein Hauptordner pro Projekt. Das Git-Repo liegt jetzt in `D:\Code\Lumora\repo` (vorher direkt
  `D:\Code\Lumora`), die Werkzeuge in `D:\Code\Lumora\werkzeuge` (vorher `D:\Code\Lumora-Werkzeuge`).
- Pfade nachgezogen: CLAUDE.md, README (Build & Start), Spikes, ADR-0056, Rauchtest-Skript und Agenten-Regeln in den
  Werkzeugen. Debug-Build jetzt unter `repo\src\Lumora.Wpf\bin\Debug\net10.0-windows\Lumora.exe`.
- Geprüft am neuen Ort: Build 0 Warnungen/0 Fehler, Rauchtest 43/43. Datenordner `%LocalAppData%\Lumora` unverändert.

## [0.4.10] — 2026-10-22

### Fix — Rauchtest: Spurenprüfung der versteckten Bibliothek wackelte unter Last
- Der Schritt „Versteckte Bibliothek … keine Spuren im AppData“ meldete zeitweise `library.db-wal`/`library.db-shm` des
  normalen Katalogs als „nicht lesbar“ und damit als mögliche Spur (beobachtet in den Läufen der Agenten von Welle 6, auch
  auf unverändertem 0.4.6): direkt nach dem Sitzungswechsel sperrt SQLite diese Dateien kurz ganz.
- Vor der Prüfung schreibt der Nachfolger den WAL zurück (`PRAGMA wal_checkpoint(TRUNCATE)`); eine ganz gesperrte Datei
  wird bis zu 20-mal im Abstand von 100 ms erneut gelesen. Bleibt sie gesperrt, zählt sie weiter als Treffer — die
  Prüfung wird nicht weicher. Zwei neue Tests (`TraceScanLockedRangeTests`).

### Offen
- Unter Last gelegentlich rot, einzeln grün (von den Agenten mit Namen festgehalten): `FaceCropServiceTests.
  Fordert_an_rendert_je_Datei_einmal_und_meldet_fertige_Ausschnitte`, `FaceIndexServiceTests.
  Abbrechen_beendet_den_Durchgang_der_naechste_Anstoss_setzt_fort`, `MotifIndexServiceTests.
  Pause_haelt_an_und_Fortsetzen_macht_weiter`, `JustifiedTilePanelTests.Pfeiltasten_wandern_ueber_Reihen_…`
  (IndexOutOfRange), `CatalogWriteRetryTests.Nach_der_Hoechstdauer_kommt_der_Fehler_beim_Aufrufer_an_statt_still_zu_
  verschwinden` („Expected waits.Counts to be equal to {1, 0}, but found empty collection“ — keine Warte-Meldung, im
  Zusammenbau von Welle 6 einmal von drei Läufen).
- Rauchtest: Alben-Schritt meldete in einzelnen Läufen Binding-Fehler beim Öffnen des intelligenten Albums (Galerie lädt
  zweimal kurz hintereinander, Hauptfenster minimiert) — nicht reproduzierbar.

## [0.4.9] — 2026-10-22

### Feature (ADR-0059) — Diashow für Galerie, Alben und Lightbox (Welle 6, ohne Musik)
- **Diashow** als eigenes, rahmenloses Vollbild-Fenster auf dem Bildschirm des Hauptfensters (Mockup 09): Überblendung
  zwischen zwei Bild-Ebenen, **langsamer Zoom (Ken Burns)** auf den Mittelpunkt aller erkannten Gesichter, sonst auf die
  Bildmitte — abwechselnd hinein/heraus, immer im Bild (keine schwarzen Ränder). Unten links **Datum und Ort**
  („Samstag, 12. Juli 2025“, ungefähre Daten nur „Juli 1982“ / „1982“; „Grado, Italien“), unten mittig die **Bedienleiste**
  (Zurück, Pause/Weiter, Weiter, „12 / 64“, 3/5/8 s, Zoom, Datum & Ort, Beenden), die sich mit dem Mauszeiger nach 2 s ohne
  Bewegung ausblendet; Fortschrittsbalken unten. Tasten ←/→, Leertaste, Pos1/Ende, Esc; F/F11 ohne Wirkung. Nach dem
  letzten Bild geht es vorn weiter. Bildschirmschoner ist während der Diashow aus.
- **Videos** über den LibVLC-Player der Lightbox mit Ton, höchstens 30 s (einstellbar), danach weiter; fehlende,
  kaputte und nicht abspielbare Dateien und Archive werden in Blätterrichtung übersprungen.
- **Einstiege:** Galerie-Leiste **Diashow** (ab zwei ausgewählten die Auswahl, sonst die ganze aktuelle Ansicht — Ids
  seitenweise, nie alle auf einmal), Kachel-Kontextmenü **Diashow ab hier**, Seite **Alben** (Rechtsklick → Diashow, Titel =
  Albumname), Lightbox-Knopf ▶ und Taste **S** („Diashow ab hier“).
- **Einstellungen › Darstellung › Diashow** (`slideshow.*`): Anzeigedauer, Reihenfolge (wie die Ansicht / zufällig, Startbild
  zuerst), langsamer Zoom, Videos abspielen + Höchstdauer, Datum und Ort. Die Leiste merkt Dauer, Zoom und Datum & Ort,
  ohne inzwischen geänderte Einstellungen zu überschreiben. Die Story-Seite verschiebt den Abschnitt später nach „Storys“.
- **Schnittstelle für Storys:** `ISlideshowService.Start(fileIds, startIndex, title)` bzw. mit `ISlideshowSequence`;
  Datei-Infos (Pfad, Aufnahmedatum mit Präzision/Abstand, Ort, Gesichtsrahmen) lädt `SlideshowItemLoader` blockweise.
- **Speicher:** dekodiert wird auf Bildschirm × Skalierung × 1,12, das nächste Bild vorgeladen, höchstens drei Bilder im
  Speicher. Messung (Wpf-Test, 200 echt dekodierte 3000 × 2000-JPEGs): ≈ 20 ms je Bild, verwaltet 15,6 → 15,6 MB, privat
  +8 MB.
- Rauchtest: neuer Schritt „Diashow“ vor „Versteckte Bibliothek“ (Galerie → zwei weiter → Video läuft, wird übersprungen →
  Esc → Galerie; „Diashow ab hier“ aus der Lightbox). 53 neue Wpf-Tests (`tests/Lumora.Wpf.Tests/Slideshow`).

### Offen
- Zum und vom Video gibt es keine Überblendung (HwndHost/Airspace); ein nicht abspielbares Video zeigt kurz Schwarz, bevor
  es übersprungen wird.
- Wackel-Test (CHANGELOG 0.4.4 „Offen“) erfasst: `Lumora.Tests.Faces.Assignment.FaceCropServiceTests.Fordert_an_rendert_je_Datei_einmal_und_meldet_fertige_Ausschnitte`
  schlug in einem Lauf fehl (943/945, ohne Änderung an Services), einzeln und im nächsten Lauf grün.

## [0.4.8] — 2026-10-22

### Feature (ADR-0058) — Storys: automatische Erkennung im Katalog (Welle 6, ohne Oberfläche)
- **Schema** (Migration `AddStories`, nur neue Tabellen): `Stories` je Bibliotheks-Gruppe mit stabiler `Signature`,
  Zeitraum, Orten/Person/Ort, Benutzer-Zustand (eigener Titel, Titelbild + „manuell“, angeheftet, ausgeblendet, gesehen)
  und Zählern; `StoryItems` mit Tagesabschnitt und `Origin` (automatisch / hinzugefügt / Ausschluss).
- **Erkennung** im Hintergrund (`StoryIndexService`): **Ereignisse** (Pause > 6 h oder Ortswechsel > 30 km trennt,
  Mindestgröße 12), **Reisen** (aufeinanderfolgende Tage weg von zu Hause, Zuhause automatisch = häufigster Ort),
  Ausflüge (ein Tag weg), Gruppen ungefährer Daten („Juli 2025“), **Personen-Storys** (Jahre bzw. Spannen mit ≥ 30
  bestätigten Fotos, ausgeblendete Personen nicht), **Orte-Storys** (Besuche in ≥ 3 Jahren). Nach Start, Scan,
  Aufnahmedatum-, Orts- und Gesichts-Änderungen; dateibezogen nur die betroffenen Zeiträume, Orte und Personen; ruht
  während Aufnahmedaten/Orte nachgetragen werden. Eine Neuberechnung findet Storys über Signatur bzw. gemeinsame
  Dateien wieder und behält angeheftet/ausgeblendet/Titel/Titelbild/manuelle Zu- und Abgänge.
- **Rückblicke** „Heute vor N Jahren“ und „<Monat> in früheren Jahren“ dynamisch je Tag, gespeichert erst beim Öffnen.
- **Überschriften** nur sachlich, beim Lesen gebaut: „Samstag, 6. September 2025“, „12.–19. Juli 2025“,
  „28. Juli – 9. August 2024“, „Grado · Triest · Aquileia — Italien“, „Grado, Triest und 2 weitere“, „Anna · 2019“,
  „Grado · 2014–2025“; ungefähre Daten nur Monat/Jahr.
- **Dienst `IStoryService`** für die kommende Story-Seite: Übersicht (Rückblicke, Angeheftet, Neu, Ereignisse & Reisen
  mit Jahren, Personen, Orte, eigene, Ausgeblendete), Details mit Tagesabschnitten, Highlights, Aktionen (anheften,
  ausblenden, gesehen, Titel, Titelbild, Dateien hinzufügen/entfernen, „Story aus Auswahl“, „Als Album speichern“,
  neu berechnen); Einstellungen `stories.*`; Nachrichten `StoriesChangedMessage`, `StoryIndexStatusMessage`.
- **Vertrag `IHighlightSelector`** (`Services/Quality/`) mit einfacher Ersatz-Auswahl `BasicHighlightSelector`
  (Favorit, Bewertung, Bildgröße, über die Zeit verteilt) — die Foto-Güte (ADR-0057) ersetzt sie.
- **Bibliotheks-Trennung**: Storys je Gruppe (`TermTables`, `DeleteGroupIfEmpty`), Verknüpfen führt gleiche Signaturen
  zusammen, Lösen kopiert samt Zustand. **„Neu aufbauen“** trägt Einträge und Titelbilder mit; **Datei-Operationen**
  sichern manuelle Einträge in `FileLinkUndo` (neue Arten 2/3), „Rückgängig“ stellt sie wieder her.
- Messung (Spike 17, 500 000 Dateien): volle Erkennung ohne Änderung 2,0–2,3 s, allererste 4,6–5,0 s (+ 2 s Titelbilder),
  inkrementell ≈ 0,1 s, Übersicht 52–56 ms.
- Rauchtest: neuer Schritt „Storys“ vor „Versteckte Bibliothek“ (42 Schritte).

### Offen
- Die Story-Seite, Diashow und Einstellungen › Storys (Mockup 09) baut Agent U gegen `IStoryService`.
- Entfernte Dateien: Zähler/Zeitraum der Storys folgen erst mit dem nächsten Hintergrund-Durchgang (Sekunden); jede
  Entfernung rechnet die Gruppe ganz neu (≈ 2 s bei 500 000 Dateien, gesammelt über 2 s).
- Personen auf der Personen-Seite zusammenführen löscht die Personen-Storys der aufgelösten Person samt Zustand
  (Kaskade); beim Verknüpfen ziehen sie mit (`StoryGroupStore.MovePersonStories`) — dieselbe Zeile gehört später in
  `MergePersonsAsync`.
- Lösen: gesicherte manuelle Story-Einträge entfernter Dateien (`FileLinkUndo` Art 2/3) zeigen weiter auf die Story der
  alten Gruppe (wie bei den Alben).

## [0.4.7] — 2026-10-22

### Feature (ADR-0057) — Foto-Güte und Highlights (Welle 6, Grundlage für Storys)
- **Foto-Güte im Hintergrund**: jedes Foto (und Video über sein Vorschaubild) bekommt Schärfe (Laplace-Varianz der
  schärfsten Kacheln, Porträts mit unscharfem Hintergrund zählen nach dem Gesicht) und Belichtung (Helligkeit,
  ausgefressene/abgesoffene Flächen) aus dem Vorschaubild — nach jedem Scan, bei neuen Dateien, beim Bereichswechsel,
  nur im aktuellen Bereich, ruht bei Scan-Sperre und Katalog-Umzug, liest keine Cloud-Platzhalter. Statusleiste
  „Foto-Güte: x von y bewertet“. Geänderte Dateien werden neu bewertet (Gültigkeit über Größe/Änderungszeit).
- **Bildwirkung ohne neues Modell**: aus den vorhandenen Motiv-Vektoren (SigLIP 2) gegen feste deutsch/englische
  Beschreibungen („schönes, scharfes, gut belichtetes Foto“ gegen „verschwommenes, dunkles, missglücktes Foto“); ohne
  Motiv-Modell bleibt sie leer, alles andere funktioniert.
- **Highlights für Storys** (`IHighlightSelector`): wählt die besten Fotos einer Liste nach Schärfe, Belichtung,
  Bildwirkung, bestätigten Personen, Favorit und Bewertung; Serien (ähnlich und ≤ 60 s) und Doppelte nur einmal mit
  „+N ähnliche“, Verteilung über die Aufnahmestunden, Grund „warum ausgewählt“ (z. B. „scharf · 2 Personen · Favorit“),
  Titelbild nie das unschärfste oder dunkelste. 5 000 Dateien in ≈ 90 ms.
- Schema: neue Tabelle `FileQuality` (Migration `AddFileQuality`, nur CREATE TABLE). Messung in
  `docs/spikes/16-foto-guete.md`: 1 000 Vorschaubilder ≈ 7 s, 500 000 ≈ 1 h einmalig im Hintergrund.

### Offen
- Gewichte und Schwellen an echten Storys nachjustieren (sobald die Story-Seite steht).
- Werte über „Neu aufbauen“ mitnehmen (bewusst vertagt, ADR-0057 Punkt 8).

## [0.4.6] — 2026-10-21

### Docs — Mockup 09 „Storys“ (Welle 6), abgenommen
- `mockups/09-storys.html`: Übersicht (Rückblick „Heute vor … Jahren“, Angeheftet, Neu, Reisen & Ereignisse nach Jahr,
  Personen, Orte), Story-Ansicht (Titelbild, sachliche Überschrift aus Datum/Ort/Personen, Highlights/Alle Fotos,
  Tagesabschnitte mit Collage-Blöcken, Videos, Serien zusammengefasst), Diashow (Vollbild, Überblendung, langsamer Zoom
  auf Gesichter, Datum/Ort, ohne Musik), Einstellungen › Storys. Hell/dunkel, Stil aus Mockup 08.
- Entscheidungen Herbert: alle vier Story-Arten automatisch, kein erfundener Text, keine Musik, „Als Collage-Bild
  speichern“ erst mit Welle 6b.

## [0.4.5] — 2026-10-21

### Docs — README ohne alten Namen
- README nennt nur noch Lumora: Titel ohne „vormals …“, V2 ohne alten Ordnerpfad, Absatz zur Umbenennung durch die
  heutigen Namen ersetzt (Datenordner einer älteren Version übernimmt der erste Start automatisch). Stand 0.4.5.
- ADR-0056 heißt jetzt `docs/adr/0056-technische-namen-lumora.md` (Links in README, PLAN und ADR-Übersicht nachgezogen).

## [0.4.4] — 2026-10-21

### Docs — Auch die Doku und die Kurzform „pv3“ heißen Lumora (Nachtrag zu ADR-0056)
- ADRs 0001–0055, ADR-Übersicht und Spikes: alle Erwähnungen von PhotoViewer/PhotoViewerV3, `PHOTOVIEWERV3_*`,
  `photoviewer-*.log`, `PV3_*` und des alten OneDrive-Repo-Pfads nennen jetzt Lumora bzw. `D:\Code\Lumora` (Herbert:
  „alles ersetzen“) — Befehle und Links der Spikes funktionieren wieder. Ausnahmen: ADR-0047 und ADR-0056 (sie
  beschreiben die Umbenennung selbst) samt ihren Übersichtszeilen; „PhotoViewer V2“ bleibt (anderes Programm).
- Code: Teildateien beim Entpacken heißen `.lumorapart` statt `.pv3part`; Temp-Ordner der Tests `lumora-*` statt `pv3-*`.
  Alte, bei einem Abbruch liegengebliebene `.pv3part`-Dateien bleiben wie bisher liegen.
- README: „Stand“ auf 0.4.4.

### Offen
- Ein Services-Test schlug in 2 von 9 Läufen fehl (nicht reproduzierbar, Testname nicht erfasst; danach 7 Läufe
  grün, auch unter Last parallel zu den WPF-Tests). Beim nächsten roten Lauf den Namen festhalten.

## [0.4.3] — 2026-10-21

### Change (ADR-0056) — Auch technische Namen heißen Lumora
- **Projekte, Ordner und Namespaces** `PhotoViewerV3.*` → `Lumora.*` (`src/Lumora.Domain|Data|Services|Wpf`,
  `tests/Lumora.Tests|Wpf.Tests|PerfTests`), Solution `Lumora.slnx`, `AddPhotoViewerServices` → `AddLumoraServices`.
  Der Debug-Build liegt jetzt unter `src\Lumora.Wpf\bin\Debug\net10.0-windows\Lumora.exe`; der alte Ordner
  `src\PhotoViewerV3.Wpf` enthält nur noch Build-Reste und kann gelöscht werden.
- **Datenordner** `%LocalAppData%\PhotoViewerV3` → `%LocalAppData%\Lumora`: der erste Start benennt den alten Ordner um
  (Katalog, Vorschaubilder, Modelle, Karten-Cache, Protokolle) und schreibt `database-location.json` sowie die
  Vorschaubild-Pfade im Katalog um. Läuft dabei noch eine ältere Lumora, bleibt der alte Ordner für diesen Start in
  Gebrauch (Warnung im Protokoll) und der nächste Start versucht es erneut. Gibt es beide Ordner, gilt der neue.
- **Umgebungsvariablen** `PHOTOVIEWERV3_*` und `PV3_*` → `LUMORA_*` (z. B. `LUMORA_APPDATA`, `LUMORA_FACE_MODELS`,
  `LUMORA_MOTIF_MODEL`, `LUMORA_MAP_TILES`, `LUMORA_SMOKE_*`); die Rauchtest-Skripte setzen die alten Rauchtest-Variablen
  zusätzlich, damit ein älteres Paket nie den echten Katalog nimmt.
- **Protokolle** `logs\lumora-*.log` statt `photoviewer-*.log`; Rauchtest-Arbeitsordner `%TEMP%\Lumora-smoke-*`.
- Das Build-Ziel, das veraltete `PhotoViewerV3.exe`-Reste aus dem Ausgabeordner löschte, entfällt (neuer Ausgabeordner).
- Doku: CLAUDE.md, README, PLAN (Kap. 3), ADR-Übersicht und Texte („Lumora lernt …“ statt „PhotoViewer lernt …“); die
  Historie (Einträge bis 0.4.2, ADRs bis 0055, Spikes, Reviews) bleibt unverändert. ADR-0047 Punkt 3 ist durch ADR-0056
  ersetzt. GitHub-Repo heißt `herbertschrotter-blip/Lumora` (alte URL leitet weiter).

### Offen
- Eigene, außerhalb gesetzte `PHOTOVIEWERV3_*`-Variablen wirken nicht mehr — auf `LUMORA_*` umstellen.
- Verknüpfungen auf die alte Debug-EXE `src\PhotoViewerV3.Wpf\bin\…` umstellen.

## [0.4.2] — 2026-10-21

### Docs — Repo zieht aus OneDrive nach `D:\Code\Lumora`
- Das Repo liegt jetzt lokal unter `D:\Code\Lumora` (frischer Klon, Remote GitHub `herbertschrotter-blip/03_Foto-Viewer-V3`);
  Builds und Tests laufen ohne OneDrive-Synchronisation, die Sync-Konflikte in `.git/objects` beim Push entfallen.
  GitHub ist Sicherung und Abgleich zwischen Geräten — am Ende einer Sitzung pushen.
- Werkzeuge außerhalb des Repos nach `D:\Code\Lumora-Werkzeuge` (Rauchtest-Testbibliothek, `run-smoke-v.ps1`,
  Konflikt-Helfer, gemeinsame Agenten-Regeln, Test-Modelle, GeoNames-Rohdaten) — vorher im temporären Sitzungsordner.
- CLAUDE.md: Pfad zu V2 absolut, Repo- und Werkzeug-Ort, Push-Policy ohne OneDrive-Begründung; README: Build-Pfad.
- Der alte Ordner `…\05 Vorlagen - Scripte\03_Foto-Viewer-V3` (OneDrive) bleibt als Archiv liegen, bis Herbert ihn löscht.

## [0.4.1] — 2026-10-21

### Feature (ADR-0055) — Orte: umschaltbare moderne Kartenstile und gemeinsamer Filter für Galerie, Ordner und Orte
- **Kartenstile**: **Automatisch** (Standard — hell CARTO **Positron**, dunkel CARTO **Dark Matter**, folgt dem
  Farbschema von Lumora live), **Voyager**, **Positron**, **Dark Matter**, **Standard (OpenStreetMap)**, **Satellit**
  (MapTiler). Umschalten über das **Stil-Menü** auf der Karte (Ebenen-Knopf oben rechts) oder **Einstellungen ›
  Darstellung › Karte**. Positron statt Voyager für „hell“, weil die farbigen Foto-Marker auf der zurückhaltenden Karte
  besser lesbar sind.
- **Nur mit eigenem Schlüssel**: CARTO (`…/rastertiles/{voyager|light_all|dark_all}/{z}/{x}/{y}.png?key=…`) und
  MapTiler (`…/maps/satellite/256/{z}/{x}/{y}.jpg?key=…`) werden nie ohne Schlüssel abgerufen; ohne Schlüssel zeigt die
  Karte OpenStreetMap und „API-Schlüssel nötig – in den Einstellungen eintragen“ (Automatisch ohne Schlüssel still OSM).
- **Karten „Kartendienst“** (Einstellungen › Darstellung › Karte) je Dienst, auch ohne Schlüssel vorgeschlagen:
  Beschreibung, Status („Kein Schlüssel – noch nicht eingerichtet“ … „Eingerichtet“), **API-Schlüssel anfordern…**
  (CARTO `carto.com/basemaps/apikey/`, MapTiler `cloud.maptiler.com/account/keys/` — nur Browser, Lumora sendet nichts),
  Anleitung in drei Schritten, Schlüssel (Passwortfeld) mit **Schlüssel prüfen** (ein Kachelabruf; gültig → gespeichert
  und eingerichtet), Website, E-Mail der Registrierung, Konto-/Verbrauchs-Link (vorbelegt), Notiz, Lumoras eigene
  Zählung des Monats („Diesen Monat von Lumora abgerufen: n Kacheln“, CARTO „Gratis bis 5 Mio./Monat“) und „Verbrauch
  im Konto ansehen“. Keine Verbrauchsabfrage beim Anbieter (keine schlüsselbasierte API dokumentiert). Links nur
  http/https. Ablage in der Settings-Tabelle (`ui.map.style`, `ui.map.cartoKey`, `ui.map.maptilerKey`,
  `ui.map.<dienst>.*`), nie im Protokoll; im versteckten Katalog eigene Werte.
- **Namensnennung je Stil** unten rechts („© OpenStreetMap-Mitwirkende, © CARTO“ bzw. „© MapTiler © OpenStreetMap-
  Mitwirkende · Satellitenbilder: © MapTiler, Maxar, Copernicus u. a.“) mit Link auf die Lizenzseite; abgelehnter
  Schlüssel (401/403) → Pause und Hinweis unten links.
- **Cache je Stil** `<CacheRoot>\maptiles\<stil>` (OSM-Kacheln aus `maptiles\z\…` werden einmalig übernommen), LRU 500 MB
  über alle Stile, CARTO/MapTiler höchstens **30 Tage** (beim Lesen verworfen, Ablaufzeit gekappt, Aufräumlauf beim Start
  auch für nicht benutzte Stile); weiter nur sichtbare Kacheln, höchstens zwei gleichzeitig — auch über einen Stilwechsel.
  In Tests und im Rauchtest weiter keine Abrufe.
- **Gemeinsamer Filter**: Filterkarte (`GalleryFilterCard`) und Suche (`GallerySearchBox`) sind eigene Steuerelemente;
  Galerie, Ordner und **Orte** zeigen dieselben. **Ein** Filterzustand für alle drei Seiten (`ISharedGalleryFilter`):
  Filter bleiben beim Seitenwechsel erhalten, „Filter zurücksetzen“ wirkt überall, „Als Album speichern…“ auch auf der
  Karte. Die Karte liest ihre Medien mit derselben Galerie-SQL (`GalleryPageSource.CreateFilteredCommand`): Gruppen,
  Zähler, „Im Ausschnitt“, „Ohne Ort“ und Start-Ausschnitt zeigen nur Treffer („· gefiltert“); der „Kartenausschnitt“
  aus „In der Galerie zeigen“ gilt nur in Galerie/Ordner.
- **Seitengebunden** bleiben Ordnerauswahl, Sortierung und Ansicht (Kacheln/Collage/Liste, Ordner-Seite neu unter
  `ui.folders.viewMode`) von Galerie und Ordner sowie der Kartenausschnitt.
- **Bibliothekswechsel/„Alle verknüpften“** behält den Filter: Tags, Personen, Kategorien, Motive und Album, die es im
  neuen Bereich nicht gibt, fallen heraus (Tags/Kategorien jetzt vor der ersten Seite gelesen); Ort, Kartenausschnitt und
  Bildinhalt-Suche (bereichsbezogen) immer. Bis 0.4.0 setzte ein Wechsel alle Filter zurück.
- Info › Bausteine/Lizenzen um CARTO Basemaps und MapTiler Satellite ergänzt; README (Kartenstile, Schlüssel anfordern,
  gemeinsamer Filter).

### Tests
- `tests/PhotoViewerV3.Tests`: 935 (1 übersprungen ohne Modell, unverändert).
- `tests/PhotoViewerV3.Wpf.Tests`: 911 (+26, 5 übersprungen ohne Modelle; dreimal hintereinander grün) — `Places/MapStyleTests` (Automatisch folgt Hell/Dunkel, URL mit/ohne
  Schlüssel, kein CARTO-Abruf ohne Schlüssel, Rückfall OSM mit Hinweis, Cache je Stil, 30-Tage-Grenze, Aufräumlauf,
  Übernahme alter OSM-Kacheln, Namensnennung je Stil, 401 → KeyRejected, umschaltbare Quelle, abgeschaltet ohne Abruf,
  Provider lädt nach Stilwechsel neu, „Schlüssel prüfen“, Zählung nur echter Abrufe), `Places/MapStyleSettingsTests`
  (Stil/Schlüssel und Kartendienst-Angaben speichern/laden samt Vorbelegung, Zählung je Kalendermonat UTC,
  Link-Validierung nur http/https, Karten vorgeschlagen → prüfen → eingerichtet/abgelehnt), `Gallery/SharedGalleryFilterTests` (Galerie setzt → Ordner/Orte sehen ihn, Zurücksetzen überall,
  Ordnerauswahl und Sortierung seitengebunden, Bibliothekswechsel, Karte zählt genau die Galerie-Treffer mit
  Koordinaten, Kartenausschnitt nicht auf der Karte); `GalleryToolbarLayoutTests` auf die neuen Steuerelemente umgestellt.
- Rauchtest-Schritt „Orte-Karte“ erweitert (`SmokeScenario.PlacesShared.cs`): Filter „Ort“ auf der Karte, Seitenwechsel
  Orte → Ordner → Orte behält ihn, Stil umschalten ohne Abruf (Hinweis, CARTO-Namensnennung mit Rauchtest-Schlüssel,
  Automatisch → Dark Matter), Karten „Kartendienst“ vorgeschlagen und „Schlüssel prüfen“ ohne Abruf, Zurücksetzen auf
  der Karte wirkt überall; Screenshots `40b`–`40f`. Rauchtest 41/41 OK, 0 Binding-Fehler.

## [0.4.0] — 2026-10-21

### Release — Abschluss der Welle „Doppelte und Alben“ (0.3.1–0.3.2)
Nach der Versionsregel des Projektprofils (MINOR beim Abschluss einer Welle). Enthalten:
- **0.3.1 Doppelte erkennen** (ADR-0053): exakt gleiche Dateien per SHA-256 (Vorfilter Dateigröße), ähnliche Bilder per
  pHash aus dem Vorschaubild, Seite „Doppelte“ mit Behalten-Vorschlag, Aufräumen in den Papierkorb mit Übertragen der
  Angaben und Rückgängig (gleiche FileId), Eingang-Hinweis, Galerie-Filter „Doppelte“.
- **0.3.2 Alben** (ADR-0054): Fotos in Ordner und Alben verknüpfen statt kopieren (Ketten-Kennzeichen, Schalter „mit
  Verknüpfungen“/„nur echte Dateien“), Seite „Alben“, intelligente Alben (gespeicherter Filter), Album-Filter,
  „Album exportieren…“.

### Zusammenbau
- Navigation: Galerie, Ordner, Alben, Personen, Orte, Doppelte, Sortieren (Rauchtest-Prüfung der Leiste und README
  entsprechend zusammengeführt); beide Rauchtest-Schritte vor „Versteckte Bibliothek“.
- ModelSnapshot aus dem vollständigen Modell neu erzeugt (`AddDuplicateDetection` + `AddAlbums`), `dotnet ef migrations
  has-pending-model-changes`: keine Änderungen. Der Aufräum-Abzug der Doppelten nimmt Tabellen mit ON DELETE CASCADE
  auf `Files.Id` automatisch mit — Album-Zuordnungen kommen beim Rückgängig mit zurück.

## [0.3.2] — 2026-10-21

### Feature (ADR-0054) — Alben: Fotos in Ordner und Alben verknüpfen statt kopieren, eigene und intelligente Alben
- **Ein Foto liegt nur einmal**: Verknüpfungen statt Kopien oder Windows-Hardlinks. Migration **`AddAlbums`** (nur neue
  Tabellen): `Albums` (je Bibliotheks-Gruppe, eigenes/intelligent, gespeicherter Filter, Titelbild), `AlbumItems`,
  `FolderLinks`, `FileLinkUndo`. Dienste `IAlbumService` (auch für spätere Storys: Alben lesen, Dateien eines Albums im
  Bereich) und `IFolderLinkService` (Services/Albums).
- **Ordneransicht**: Fotos per Ziehen auf einen Ordner im Ordnerbaum (ohne Taste/Alt/Strg = **verknüpfen**, **Umschalt =
  verschieben** mit Rückfrage) oder Rechtsklick **Hier verknüpfen…** in einen anderen Ordner derselben Bibliothek. Der
  Ordner zeigt echten Inhalt + verknüpfte Fotos mit Kennzeichen „verknüpft“ (Kachel, Collage, Liste), Schalter **mit
  Verknüpfungen** / **nur echte Dateien** und Zähler „12 Dateien · 5 verknüpft“ (nur in Ordnern mit Verknüpfungen);
  **Verknüpfung entfernen** im Kontextmenü. Ohne Verknüpfungen bleibt die Galerie-Abfrage unverändert.
- **Eigene Alben**: neue Seite **Alben** (Kacheln mit Titelbild und Anzahl; anlegen, umbenennen, löschen, Titelbild
  zurücksetzen, exportieren). Hinzufügen per Kontextmenü **Zu Album hinzufügen**, Aktionsleiste **Album**, **Strg+L**,
  Ziehen auf ein Album der Liste unter dem Ordnerbaum. Album öffnet die Galerie gefiltert: Chip **Album** in der Zeile
  „Inhalt“, Bedingung in „Es gilt“, kombinierbar mit allen Filtern und Ansichten; **Aus Album entfernen**, **Als Titelbild
  des Albums**.
- **Intelligente Alben**: **Als Album speichern…** in der „Es gilt“-Zeile speichert den Galerie-Filter (Dateiart,
  Favoriten, Bewertung, Ablage, Ort, Suche, Tags, Personen, Beziehung, Kategorien, Motive, Ordner, Album) — Begriffe über
  ihre Namen, damit Verknüpfen/Lösen/„Neu aufbauen“ sie nicht ungültig machen. Auswertung mit derselben SQL wie die
  Galerie (`GallerySmartAlbumEvaluator`); Öffnen lädt den Filter, neu speichern ersetzt ihn.
- **Sicherheit**: Verschieben, Umbenennen, Ordner umbenennen, Sortierer und „Ordner übernehmen“ erhalten alle
  Verknüpfungen (Übernehmen hängt Verknüpfungen in Quellordner auf die Zielordner um). Papierkorb und Verschieben aus der
  Bibliothek sichern die Verknüpfungen zur Operation (`FileLinkUndo`); **Rückgängig** stellt sie her, ebenso die Rückkehr
  derselben Datei (Pfad + Größe + Änderungszeit, z. B. Windows „Wiederherstellen“) — ein neuer Import gleichen Namens erbt
  nichts. „Neu aufbauen“ trägt Album-Einträge, Ordner-Verknüpfungen, Titelbilder und gesicherte Ziele mit.
- **Album exportieren…**: echte Kopien in einen gewählten Ordner, Fortschritt, Abbrechen, gleichnamige mit „ (2)“; in
  einen Bibliotheksordner nur nach Rückfrage; in einer versteckten Bibliothek gesperrt.
- **Bibliotheks-Trennung**: Alben je Gruppe (`TermTables`, `DeleteGroupIfEmpty`), zeigen/zählen nur den Bereich.
  Verknüpfen führt gleichnamige eigene Alben zusammen, intelligente bekommen bei gleichem Namen den Bibliotheksnamen als
  Zusatz; Lösen kopiert alle Alben (neue Ids über dem AUTOINCREMENT-Zähler). Ordner-Verknüpfungen nur innerhalb einer
  Bibliothek.
- `CLAUDE.md`: Regel zur Files-Löschung um Alben/Ordner-Verknüpfungen ergänzt.

### Tests
- `tests/PhotoViewerV3.Tests`: 895 (+10, 1 übersprungen ohne Modell) — `Albums/AlbumServiceTests` (verknüpfen/entfernen
  ohne Fotoverlust, Ordner-Verknüpfung inkl. Unterordner, Verschieben/Umbenennen/Ordner umbenennen erhalten
  Verknüpfungen, Papierkorb + Rückkehr, neuer Import erbt nichts, Verschieben nach außen + Rückgängig, „Neu aufbauen“,
  intelligentes Album speichern/ersetzen/JSON, Export inkl. Abbrechen und versteckt gesperrt), `Albums/AlbumScopingTests`
  (Trennung, Verknüpfen mit Zusammenführen und Zusatz, „Alle verknüpften“, Lösen kopiert, Bibliothek entfernen).
- `tests/PhotoViewerV3.Wpf.Tests`: 874 (+9, 5 übersprungen ohne Modell/Stichprobe), dreimal hintereinander grün (ein
  einzelner Ausreißer davor in `EarlyCloseWindowTests.Lightbox_oeffnen_und_sofort_schliessen_ohne_Ausnahme`, nicht
  reproduzierbar) — `Albums/GalleryAlbumQueryTests` (echt + verknüpft, Zähler, Kennzeichen, „nur echte Dateien“,
  Abfrageplan unverändert ohne Verknüpfungen, Filter „Album“, intelligentes Album = Galerie-Filter im Bereich),
  `Albums/BoolOptionConverterTests`.
- Rauchtest 40/40 OK, 0 Binding-Fehler — neuer Schritt **„Alben“** vor „Versteckte Bibliothek“
  (`Shell/Smoke/SmokeScenario.Albums.cs`); Navigation-Prüfung um „Alben“ ergänzt. Screenshots `50-alben-galerie-gefiltert`,
  `51-alben-seite`, `52-ordner-verknuepft`, `53-ordner-verknuepft-dunkel`, `54-alben-seite-dunkel`,
  `55-alben-intelligent-geoeffnet`.

### Offen
- Papierkorb hat weiterhin kein Lumora-Rückgängig; Verknüpfungen kommen mit der Datei an ihrem alten Ort zurück.
- „Ordner übernehmen“: Verknüpfungen in einen Quellordner wandern nur, wenn der Zielordner eine Katalogzeile bekommt;
  Rückgängig der Übernahme hängt sie nicht zurück.
- Die Seite „Alben“ wertet intelligente Alben einzeln aus (bei vielen auf großen Bibliotheken spürbar); der Zähler
  „n verknüpft“ wird je Seitenwechsel neu gezählt. Nicht an 500 000 Dateien gemessen.
- Der Bestätigungsdialog „Verknüpfen“ der Bibliotheken nennt die Alben noch nicht.

## [0.3.1] — 2026-10-21

### Feature (ADR-0053) — Doppelte erkennen: gleiche und ähnliche Fotos finden und aufräumen
- **Exakt gleich** = SHA-256 über alle Bytes, nur für Dateien, deren Größe im Katalog noch einmal vorkommt (Vorfilter;
  in Herberts Handy-Katalog 13 % der Dateien). **Ähnlich** = 64-Bit-pHash (DCT) aus dem Vorschaubild (kurze Kante
  400 px, auf 128 px geladen ≈ 1 ms; ohne Vorschaubild aus dem lokalen Original), Abstand = Hamming, **Standard-Schwelle
  10** (einstellbar 1–12); fast einfarbige Bilder zählen nie als ähnlich. Gruppen per Mehrfach-Index-Hashing (vier
  16-Bit-Bänder, Ergebnis = Vollvergleich) und Stern-Verfahren (alle Mitglieder nah am ersten, keine Ketten).
- **Schema — Migration `AddDuplicateDetection`** (nur CREATE TABLE): `FileFingerprints` (Prüfsumme + Bild-Fingerabdruck
  je Datei, gültig nur für die gelesene Dateiversion; `IX_FileFingerprints_ContentHash`) und `DuplicateCleanupUndo`
  (Rückgängig-Daten). Settings `duplicates.similarThreshold`, `duplicates.transferMetadata`.
- **Hintergrund-Index** `IDuplicateIndexService` im Muster des Aufnahmedatums: nach jedem Scan, bei neuen Dateien und
  15 s nach dem Start; Stapel mit einer Transaktion, ruht bei Scan-Sperre und Katalog-Umzug, liest keine
  OneDrive-Platzhalter und keine Dateien nicht erreichbarer Wurzeln; Statusleiste „Doppelte: Prüfsummen n / m“.
- **Seite „Doppelte“** (Navigation zwischen „Orte“ und „Sortieren“): Umschalter **Exakt gleich | Ähnlich** mit
  Schwellen-Regler, Filter **Eingang schon in der Bibliothek**, Gruppen als Karten (Vorschaubild, Auflösung, Größe,
  Aufnahmedatum, Ordner, Bewertung/Favorit, Bibliothek/Eingang, Abstand), virtualisiert, Doppelklick → Lightbox.
  **Vorschlag „behalten“** nach sichtbarer Regel (höhere Auflösung → Aufnahmedatum aus der Datei → liegt in der
  Bibliothek → eigene Angaben → größere Datei → länger im Katalog) mit Begründung je Gruppe; Wahl per Radio-Knopf
  änderbar, Gruppen abwählbar.
- **„Übrige in den Papierkorb“** nur nach Bestätigung, über `IFileOperationService.RecycleAsync` (Recycle-Batch,
  Protokoll, Papierkorb). Vorher je gelöschter Datei ein Abzug ihrer Files-Zeile und aller per Kaskade abhängigen Zeilen
  (aus dem Schema gelesen) und — **„Angaben übertragen“, Standard an** — Favorit, höhere Bewertung, fehlende Tags und
  Kategorie-Werte, Personen (Gesicht mit bester Überdeckung bzw. Gesichter kopiert bei byte-gleicher, noch nicht
  analysierter Datei) auf die behaltene Datei. Bleibt eine Datei (gesperrt), wird ihre Übertragung sofort zurückgenommen.
- **„Rückgängig“** auf der Seite: neue Methode `IFileOperationService.RestoreRecycledAsync` holt die Dateien aus dem
  Windows-Papierkorb (eigene Auswertung der `$I`/`$R`-Paare, Verzeichnis einmal je Lauf gelesen) mit **derselben FileId**
  und allen Angaben zurück; die Übertragung an der behaltenen Datei wird zurückgenommen; teilweise Zurückgeholtes lässt sich
  erneut versuchen; Gesichter, deren Person inzwischen gelöscht ist, kommen ohne Person zurück.
- **Eingang-Hinweis**: liegen neu gehashte Dateien im Eingang byte-gleich schon in der Bibliothek, zeigt die Statusleiste
  „N Dateien im Eingang sind schon in der Bibliothek — Anzeigen“ (nur für den angezeigten Bereich; öffnet die Seite
  gefiltert). Für die Handy-Übertragung: `IDuplicateService.FindByContentAsync` („gibt es diese Prüfsumme schon?“).
- **Galerie-Filter „Doppelte“** als Chip in der Zeile „Inhalt“ (in „Datei“ bräche die Zeile bei 1280 DIP um): nur Dateien
  mit byte-gleichem Gegenstück im Bereich, UND mit allen Filtern, „Es gilt“ mit ✕.
- **Bereich**: alles nur in den Bibliotheken des Bereichs (`ILibraryScope`), in „Alle verknüpften“ über die Gruppe;
  versteckte Bibliotheken nur im eigenen Katalog.

### Messung (Spike 15, `docs/spikes/15-doppelte.md`)
- Handy-Katalog (24 217 Dateien, nur Zahlen): 13,1 % teilen ihre Größe; SHA-256 353 MB/s (plattengebunden), Hochrechnung
  500 000 Dateien ≈ 80 min Lesen beim ersten Mal. pHash an 400 JPEGs: verkleinert + Q70 p95 4 (max 8), 3 % beschnitten
  Median 6 / p95 10, heller p95 2, fremde Fotos Median 32; aufeinanderfolgende Fotos ≤ 10: 17 % (Serien).
- Gruppieren: 100 000 Fingerabdrücke r = 10 ≈ 0,2 s, 500 000 ≈ 4–5 s (r = 12 ≈ 20 s). SQL bei 500 000 Dateien: Seite
  exakt gleich 1,3 s, ähnlich (r = 10) 6,7 s, Galerie-Filter „nur Doppelte“ 1,1 s, Eingang-Zähler 1,6 s (Hintergrund),
  Größen-Vorfilter 0,4 s je Durchgang. Ein korreliertes `EXISTS` (erster Entwurf) war für den Eingang-Zähler unbrauchbar.

### Tests
- `tests/PhotoViewerV3.Tests`: 926 (+41, 1 übersprungen ohne Modell) — `Duplicates/FingerprintTests` (SHA-256 gleich/ungleich über mehrere Puffer,
  geteiltes Lesen; pHash nah bei verkleinert/neu gespeichert/beschnitten/heller, fern bei anderem Motiv, einfarbig),
  `SimilarGroupingTests` (Bucket-Suche = Vollvergleich für r = 0–12, Stern-Gruppen, Behalten-Regel), 
  `DuplicateIndexAndQueryTests` (Vorfilter, veraltete Prüfsumme, Eingang-Hinweis, Bereich je Bibliothek/„Alle
  verknüpften“, Prüfsummen-Abfrage, Scan-Sperre, Bild-Fingerabdrücke, ähnliche Gruppen, Videos nur mit Vorschaubild),
  `DuplicateCleanupTests` (Übertragen + Papierkorb + Rückgängig mit derselben FileId, ohne Übertragen, Gesichter kopiert,
  nicht übertragbar, gesperrte Datei, teilweise zurückgeholt, gelöschte Person, Platz belegt),
  `Operations/WindowsRecycleBinRestoreTests` (`$I` v1/v2, jüngster Eintrag, echter Papierkorb hin und zurück).
- `tests/PhotoViewerV3.Wpf.Tests`: 881 (+11, 5 übersprungen ohne Modell/Stichprobe), dreimal hintereinander grün — `Duplicates/DuplicatesViewModelTests`, `DuplicatesPageUiTests` (echte Seite,
  virtualisiert, Radio-Knopf, keine Binding-Fehler, Navigation/Symbole), `GalleryDuplicateFilterTests` (SQL mit echter DB).
- Rauchtest: neuer Schritt **„Doppelte“** vor „Versteckte Bibliothek“ (`SmokeScenario.Duplicates.cs`): Foto + Kopie +
  verkleinerte Kopie, Prüfsumme gleich, pHash nah, Galerie-Filter, Seite exakt/ähnlich (Screenshots
  `48-doppelte-exakt-hell`, `49-doppelte-aehnlich-dunkel`), Übrige in den Papierkorb mit Übertragung, Rückgängig mit
  derselben FileId; Navigationsprüfung um „Doppelte“ ergänzt.

### Offen
- Fingerabdrücke über „Neu aufbauen“ mitnehmen; Rückgängig nur für die jüngste Aufräumaktion auf der Seite (nicht über die
  Galerie-Taste); manuelles Datum/manueller Ort der gelöschten Datei werden nicht übertragen.
- „Ähnlich“ über 500 000 Fotos braucht beim Öffnen einige Sekunden; der Größen-Vorfilter liest bei jedem Durchgang die
  ganze Files-Tabelle (≈ 0,4 s bei 500 000).
- Schwelle und Vorschlag an Herberts echtem Katalog nachjustieren. Der pHash sieht nur Helligkeit: fast einfarbige Bilder
  mit wenig Text (z. B. die synthetischen Bilder der Testbibliothek) landen trotz anderer Farbe in einer „ähnlich“-Gruppe.
- Kommt eine Datei beim Rückgängig ohne ihren Abzug zurück (Bibliothek inzwischen entfernt), bleiben die übertragenen
  Angaben an der behaltenen Datei (nichts geht verloren, steht aber nur dort).

## [0.3.0] — 2026-10-21

### Release — Abschluss der Runde „Trennung, Versteckt, Orte, Motive“ (0.2.51–0.2.56)
Nach der Versionsregel des Projektprofils (MINOR beim Abschluss einer Welle) schließt 0.3.0 die Runde ab. Enthalten:
- **0.2.51 Bibliotheks-Trennung** (ADR-0048): Tags, Kategorien, Personen, Beziehungen je Bibliotheks-Gruppe;
  „Bibliotheken verknüpfen“ als Einstellung; Ansicht „Alle verknüpften“.
- **0.2.52 Versteckte Bibliotheken** (ADR-0049): eigener Katalog in `.lumora` im verschlüsselten Container, nur mit
  `_hidden` und Lumora-Kennwort, Zusperren nach Untätigkeit, Aushängen.
- **0.2.53 Orte-Daten** (ADR-0050): GPS lesen, „Ort setzen“ verlustfrei in EXIF/XMP mit Rückgängig, Ortsnamen offline
  (GeoNames), Filter „Ort“.
- **0.2.54 Orte-Karte** (ADR-0051): Weltkarte mit Foto-Gruppen, die beim Zoomen feiner werden; Verorten per Ziehen.
- **0.2.55 Motive** (ADR-0052): SigLIP 2 lokal, deutsche Begriffe, Filter „Motive“, Bildinhalt-Suche.
- **0.2.56 Zusammenbau**: Trennung gilt auch für Orte, Karte und Motive.

### Fix — Rauchtest-Spurenprüfung der versteckten Bibliothek
- `SmokeScenario.FindTraces` liest Dateien blockweise (4 KB) und überspringt gesperrte Bereiche. SQLite sperrt in der
  offenen DB samt `-wal`/`-shm` einzelne Byte-Bereiche (LockFileEx); vorher zählte die ganze Datei als „nicht lesbar“
  und damit als Spur (zeitabhängiger FAIL „library.db-shm/-wal (nicht lesbar)“ im Schritt „Versteckte Bibliothek“).
  Nur eine Datei, aus der gar nichts lesbar ist, bleibt eine mögliche Spur. Tests `TraceScanLockedRangeTests`
  (gesperrter Bereich ohne Treffer, Treffer außerhalb, ganz gesperrte Datei).
- Verworfen auf Herberts Wunsch (Testphase, Daten sind Kopien): eine automatische Sicherung vor Migrationen — wird vor
  dem Produktivbetrieb wieder angeboten.

## [0.2.56] — 2026-10-21

### Fix (ADR-0048 Nachtrag) — Zusammenbau Welle 10: Bibliotheks-Trennung gilt auch für Orte, Karte und Motive
- **Zusammenbau 0.2.50 + 0.2.52 + 0.2.54 + 0.2.53 + 0.2.55 + 0.2.51** (Branch `integration-w10`): Platzhalter zwischen
  Orte-Daten und Karte entfernt, `IMapPicker` an die Schnittstelle der Orte-Daten angepasst, DI der Karte heißt
  `AddPlacesMap()`, der doppelte `Services.Library.ICatalogContext` ist weg — die Bibliotheks-Trennung nutzt
  `Services.Infrastructure.ICatalogContext` (IsHidden je Katalog-Sitzung, Tests mit `TestCatalogContext`), ModelSnapshot
  geprüft. README: doppelte Navigationszeile aus dem Zusammenführen bereinigt.
- **Motiv-Begriffe gehören der Bibliotheks-Gruppe** wie Tags: Migration **`MotifTermsByGroup`** — `MotifTerms.GroupId`
  per `ADD COLUMN` (kein Tabellen-Neuaufbau, Treffer hängen per Kaskade daran), befüllt aus `Libraries.GroupId`,
  eindeutig je (GroupId, Name); gleichnamige Begriffe schon verknüpfter Bibliotheken werden eins (Treffer umgehängt),
  Merker der Start-Liste je Gruppe (`motifs.seeded.g{GroupId}`). `LibraryId` bleibt nur als Anker des Fremdschlüssels
  und wandert beim Entfernen einer Bibliothek bzw. beim Lösen an eine andere Bibliothek der Gruppe — die Kaskade nimmt
  gemeinsame Begriffe nie mit.
- **Verknüpfen** führt gleichnamige Motiv-Begriffe zusammen; wo ein Begriff Fotos dazubekommt, die er nie gesehen hat,
  rechnet der Index Text-Vektor und Treffer beim nächsten Start aus den gespeicherten Bild-Vektoren nach (kein Foto wird
  neu analysiert). **Lösen** kopiert alle Motiv-Begriffe samt Text-Vektor in die herausgelöste Bibliothek, ihre Treffer
  zeigen auf die Kopien. Protokoll nennt die Zahl der Motiv-Begriffe.
- **Motive im Bereich**: Galerie-Flyout „Motive“, Suche nach Begriffen und „Motiv: …“ lesen die Begriffe der Gruppe;
  Zähler (Galerie und Einstellungen › Motive) und Bildinhalt-Suche zählen bzw. suchen nur in den Bibliotheken des
  Bereichs — eine verknüpfte Bibliothek einzeln nur ihre eigenen Fotos, „Alle verknüpften“ die Summe. Begriffsänderungen
  rechnen die Treffer aller Bibliotheken der Gruppe neu.
- **Orte und Karte im Bereich**: die Seite **Orte** (Karte, „Ohne Ort“, Ortssuche, Kartenfenster „Ort wählen“) nimmt den
  Bereich aus `ILibraryScope` (`LibraryScopePlacesScope`) und zeigt in „Alle verknüpften“ alle Bibliotheken der Gruppe;
  Galerie-Filter „Ort“, Ortszähler und „zuletzt verwendete Orte“ ebenso. Verorten in „Alle verknüpften“ geht (jede Datei
  in ihrer Bibliothek).
- **Versteckte Bibliothek**: bekommt ihre Gruppe schon beim Anlegen; Verknüpfen und „Alle verknüpften“ bleiben gesperrt,
  Motiv-Begriffe und Orte liegen nur im Katalog des Containers (geprüft).

### Tests
- `tests/PhotoViewerV3.Tests`: 886 (+4, 1 übersprungen ohne Modell) — `Libraries/MotifPlacesScopingTests` (zwei
  Bibliotheken mit eigenen Begriffen, Bild-Vektoren und Orten: nichts aus A in B bei Begriffen, Zählern, Treffern,
  Bildinhalt-Suche, Kartenpunkten, Ortszählern; verknüpft → Start-Listen eins, fehlender Treffer nachgerechnet, B einzeln
  zählt nur B; „Alle verknüpften“ = Summe; gelöst → Begriffe mit Vektor in beiden, gelöschter Standard-Begriff kommt nicht
  wieder; Bibliothek entfernen behält gemeinsame Begriffe (Anker); Migration mit Duplikaten und Merkern),
  `HiddenCatalogTests` (Gruppe beim Anlegen, Verknüpfen/„Alle verknüpften“ gesperrt, Begriffe nur im Container).
- `tests/PhotoViewerV3.Wpf.Tests`: 867 (+2, 5 übersprungen ohne Modell/Stichprobe), dreimal hintereinander grün —
  `GalleryPageSourceMotifTests.Begriffe_je_Gruppe_Zaehler_und_Suche_je_Bereich`, `Places/PlacesScopeTests`.
- Rauchtest 39/39 OK, 0 Binding-Fehler: Schritt **„Bibliotheks-Trennung“** um Motiv-Begriff in A (in B weder in den
  Einstellungen noch im Flyout) und Ort an einem Foto aus B (in A weder auf der Karte noch im Ort-Flyout) ergänzt;
  verknüpft bzw. „Alle verknüpften“ sichtbar, gelöst Begriff in beiden, Ort vor dem Entfernen zurückgenommen.

### Offen
- „Rückgängig“ von „Ort setzen“ und „Datum setzen“ nimmt den jüngsten Eintrag des Katalogs, nicht des Bereichs (bestand
  schon vorher; betrifft nur mehrere Bibliotheken im selben Katalog).
- Motiv-Index, Gesichter-Index und Ortsnachtrag laufen weiter nur auf der aktiven Bibliothek (wie in ADR-0048
  beschrieben); der Bestätigungsdialog „Verknüpfen“ nennt die Motiv-Begriffe noch nicht.

## [0.2.55] — 2026-10-21

### Feature (ADR-0052) — Motive: Fotos lokal nach Inhalt erkennen (Sonnenuntergang, Meer, Hund …) und filtern
- **Modell SigLIP 2 base** (google/siglip2-base-patch16-224, Apache-2.0, mehrsprachig) als ONNX über die vorhandene ONNX
  Runtime DirectML: Bildmodell fp32 auf der Grafikkarte (Rückfall CPU), Sprachmodell int8 auf dem Prozessor (nur für
  Begriffe und Freitext-Suche, nach 2 min Ruhe freigegeben). **Einstellungen › Motive › „Motiv-Modell herunterladen…“**
  nur nach Bestätigung (Dateien, Quelle, Größen, Lizenz, ≈ 690 MB), SHA-256 je Datei fest im Code, Fortschritt,
  Abbrechen, Ablage `<AppData>\models\siglip2-base-patch16-224\` (nie Repo/Paket); `PHOTOVIEWERV3_MOTIF_MODEL` für
  Entwicklung/Tests. Ohne Modell ist die Funktion aus (Hinweis im Reiter).
- **Vorverarbeitung exakt nach `preprocessor_config.json`** (`Services/Motifs/Siglip/SiglipPreprocessor`): 224 × 224,
  Pillow-BILINEAR nachgebildet (Dreiecksfilter, 22-Bit-Festkomma, zwei Durchgänge), (x/255 − 0,5)/0,5, NCHW. Eingang:
  fertiges Vorschaubild (≈ 1 ms) bzw. Original per WIC (längste Kante 448); Videos über ihr Vorschaubild.
  **Tokenizer selbst** (`GemmaTokenizer`, BPE aus `tokenizer.json`, Byte-Fallback, `<eos>`, 64 Token) — kein neues
  NuGet-Paket (`Microsoft.ML.Tokenizers` liest Gemma nur aus dem SentencePiece-Modell).
- **Wahrscheinlichkeit wie SigLIP**: σ(cos · e^logit_scale + logit_bias) mit den Modellwerten (4,724453 / −16,77172);
  Standard-Schwelle **1 %**, je Begriff einstellbar. Begriff = Mittel aus deutschem Wort und Beschreibung (Standard:
  englische Foto-Vorlage — trennte an Referenzbildern am besten).
- **Schema — Migration `AddMotifs`**: `MotifTerms` (je `LibraryId`, NOCASE-eindeutig, Text-Vektor + Version),
  `FileEmbeddings` (Bild-Vektor float16, 1 536 Bytes), `FileMotifs` (Treffer, `IX_FileMotifs_Term_File`). Start-Liste mit
  82 deutschen Begriffen einmal je Bibliothek. Begriffe anlegen/umbenennen/Beschreibung/Schwelle/löschen rechnet nur die
  Treffer aus den gespeicherten Vektoren neu — kein Foto wird erneut analysiert.
- **Hintergrund-Index** `IMotifIndexService` (Muster Gesichtserkennung): aktive Bibliothek, neueste zuerst, 3 Bilder
  vorab, stapelweise schreiben, Pause/Fortsetzen/Abbrechen in der Statusleiste („Motive: 1.234 / 19.556 · 26 ms/Foto“),
  startet 15 s nach dem App-Start (bestehende Kataloge werden nachgeholt), nach Scans, bei neuen Dateien und nach dem
  Download; **ruht während eines Scans** (`IScanGate`). Neue **gemeinsame GPU-Sperre** `IGpuGate`: Gesichts- und
  Motiv-Index rechnen nie gleichzeitig auf der Grafikkarte (Sperre je Foto, nur mit DirectML; `FaceIndexService` nimmt sie
  optional entgegen).
- **Galerie**: Chip **„Motive“** in der Zeile „Inhalt“ (Begriffe mit Anzahl, „eines der gewählten“/„alle zusammen“,
  Suche im Flyout), Bedingung in „Es gilt“, ✕ entfernt sie, „Filter zurücksetzen“ hebt sie auf; **Suche** findet Begriffe
  (UNION-Zweig, nur Begriffe des Bereichs), **„Motiv: Hund“** sucht nur Motive; **Bildinhalt beschreiben** (Bonus:
  Freitext „Hund am Strand“ → die 200 ähnlichsten Fotos mit Kosinus ≥ 0,05 als Bedingung); Listenspalte **„Motive“**;
  **Lightbox** Info-Panel „MOTIVE“.
- **Einstellungen › Motive** (neuer Reiter nach „Gesichter“, `NavigationTags.SettingsMotifs`): Schalter (Standard an),
  Grafikkarte, Modell, Index + „Neu analysieren“, globale Schwelle, Begriffsliste mit Treffern.
- **Info › Bausteine/Lizenzen**: SigLIP 2 (Apache-2.0, Download nach Bestätigung).
- „Neu aufbauen“ trägt Vektoren und Treffer mit (`UserDataCarryOver.Motifs.cs`); der Incremental-Scan verwirft Vektor und
  Treffer an Ort und Stelle geänderter Dateien.
- Bibliotheks-Trennung: Begriffe, Treffer, Zähler, Suche und Bildinhalt-Suche nur im aktiven Bereich (`LibraryId`);
  Vektoren liegen im Katalog, außerhalb wird nichts abgelegt.

### Messung (Spike 14, `docs/spikes/14-motive.md`)
- 200 Handyfotos (nur Zahlen): Bildmodell DirectML **≈ 26 ms/Foto**, CPU 125–200 ms; Laden Original (≤ 448 px) 45–58 ms,
  aus dem Vorschaubild ≈ 1 ms; Durchsatz mit 3 Originalen vorab **39,7 ms/Foto**. Hochrechnung DirectML: 50 000 Fotos
  ≈ 23–33 min, 500 000 ≈ 3,8–5,5 h; CPU 500 000 ≈ 17–28 h. Katalog +1,5 KB je Datei (500 000 ≈ 0,8 GB).
- Qualität: bei 1 % bekommen **85 von 200 Fotos** mindestens ein Motiv (Ø 0,56); Gegenprobe mit der Gesichtserkennung:
  Fotos ohne Gesicht nur 3 von 126 mit Personen-Motiv, Fotos mit ≥ 3 Gesichtern 15 von 25. Referenzbilder (Hund, Katze,
  Dokument, Bildschirm) jeweils auf Platz 1. Vorlagen-Vergleich: Wort + englische Vorlage trennt am besten (Ø +0,041).

### Tests
- `tests/PhotoViewerV3.Tests`: 763 (+28, 1 übersprungen ohne Modell): `Motifs/MotifMathTests` (Normierung, Pillow-
  BILINEAR von Hand nachgerechnet 4 → 2 Pixel und 448 → 224 Gewichte, einfarbiges Bild NCHW, Seitenverhältnis ohne
  Zuschnitt, float32/float16, SigLIP-Wahrscheinlichkeit und Umkehrung, Treffer je Schwelle, BPE an einer kleinen
  tokenizer.json mit Rangfolge/Byte-Fallback/eos/Auffüllen/Kürzen), `MotifIndexServiceTests` (Vektoren und Treffer,
  Videos nur mit Vorschaubild, Pause/Fortsetzen, ruht während eines Scans, **GPU-Sperre: Gesichter und Motive nie
  gleichzeitig**, ohne Modell/aus, neue Dateien und Neu analysieren, globale Schwelle ohne neue Analyse, „Neu aufbauen“
  trägt Vektoren mit, geänderte Datei verliert Vektor, Bibliotheks-Trennung), `MotifTermServiceTests` (Start-Liste einmal,
  Prüfungen, neuer Begriff/Beschreibung/eigene Schwelle wirken auf gespeicherte Vektoren, ohne Modell später, Freitext-
  Suche nur im Bereich), `MotifModelProviderTests` (Fake-HTTP: SHA-256 je Datei, Fortschritt, falsche Prüfsumme, Abbruch,
  vorhandene Dateien, HTTP-Fehler, feste Liste).
- `tests/PhotoViewerV3.Wpf.Tests`: 793 (+13, 5 übersprungen ohne Modell/Stichprobe), dreimal hintereinander grün:
  `Motifs/GalleryPageSourceMotifTests` (ODER/UND, mit Suche und Datei-Filter, Suche nach Begriffen nur im Bereich,
  „Motiv: …“, Bildinhalt-Ids, Zähler, Motive je Datei, Abfrageplan über `IX_FileMotifs_Term_File`),
  `MotifSettingsViewModelTests` (Laden, Download nur nach Bestätigung mit Dateien/Größen/Quelle/Lizenz, Abbruch/Fehler,
  Begriffe über den Dienst, Schwellen, Neu analysieren, Texte von Index und Statusleiste); mit echtem Modell
  (`PHOTOVIEWERV3_MOTIF_MODEL`): `MotifModelTests` (Gemma-Token-Ids, Referenzbilder Hund/Katze/Dokument/Bildschirm/
  Sonnenuntergang über alle 82 Begriffe, CPU = GPU), Messung `MotifMeasurementTests`; Reiter-Reihenfolge um „Motive“
  ergänzt.
- Rauchtest 35/35 OK, 0 Binding-Fehler — neuer Schritt **„Motive“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.Motifs.cs`, `SmokeMotifEncoderFactory` ohne Modell): Standard-Begriffe, Einschalten im
  Reiter, Statusleiste, Pause/Fortsetzen, Vektoren (float16) und Treffer = DB, Filter mit Zähler und „Es gilt“,
  Bildinhalt-Suche, „Motiv: …“ und Begriffssuche, Listenspalte, Lightbox-Info, eigener Begriff ohne neue Analyse.
  Screenshots `45-motive-einstellungen`, `46-motive-galerie-filter`, `47-motive-galerie-liste`. Die Erkennung ist in den
  übrigen Schritten aus.

### Offen
- `MotifTerms.LibraryId` stellt die Bibliotheks-Trennung beim Zusammenbau auf ihre Gruppen um.
- Standard-Schwelle an 200 unmarkierten Fotos und Referenzbildern gewählt — Feinabstimmung je Begriff mit Herberts
  Rückmeldung. Bildinhalt-Suche sortiert nach der Galerie-Sortierung, nicht nach Ähnlichkeit.

## [0.2.54] — 2026-10-21

### Feature (ADR-0051) — Orte: Weltkarte mit Foto-Gruppen, die beim Zoomen feiner werden
- **Neue Seite „Orte“** (Navigation zwischen Personen und Sortieren, Symbol Karte): Kopfzeile im Stil der Galerie mit
  „1.234 verortete Fotos · 56 ohne Ort“ und **Ort suchen …** (Karte springt hin), Karte füllt die Seite, rechts die
  Seitenleiste **Ohne Ort**.
- **Start-Ausschnitt** = Bereich, in dem die Fotos des aktuellen Bereichs gemacht wurden, plus Rand (10 %, mind. 80 px,
  höchstens Zoom 16); ohne verortete Fotos die ganze Welt. Hinauszoomen bis zur Welt; Knöpfe hinein/hinaus/alle
  Fotos/Welt, Mausrad und +/− um den Mauszeiger, Ziehen, Doppelklick, Pfeiltasten.
- **Gruppen:** runde Marker mit Vorschaubild des besten Fotos (höchste Bewertung, sonst neuestes) und Anzahl-Plakette,
  Einzelfoto als kleines Vorschaubild. Klick auf eine Gruppe zoomt hinein, bis sie zerfällt (Fotos am selben Ort →
  Lightbox); Doppelklick bzw. Rechtsklick › **In der Galerie zeigen** sendet `ShowFilesOnMapSelectionMessage` (Vertrag
  mit Agent V) und wechselt zur Galerie; Klick auf ein Einzelfoto öffnet die Lightbox; **Im Ausschnitt: n Fotos** zeigt
  alle sichtbaren in der Galerie.
- **Clustering** (`Services/Places/Clustering/GeoClusterIndex`): hierarchisches Raster über den Morton-Code
  (Zellen 64 px je Zoomstufe, Präfixsummen + Segmentbaum, nahe Marker < 56 px zusammengelegt). Gemessen im Debug-Build
  mit 500 000 Fotos: Aufbau **275 ms** (gehäuft) bzw. **223 ms** (gleichmäßig), Abfrage je Ausschnitt Ø **0,14 ms**,
  max 6,9 ms. Ein erster Supercluster-Entwurf brauchte 5,0 s und wurde ersetzt.
- **Verorten:** Fotos aus „Ohne Ort“ (virtualisiert, Mehrfachauswahl mit Strg/Umschalt) auf die Karte ziehen oder
  Rechtsklick auf die Karte › **Hier verorten (n Fotos)** → Dialog mit Ortsname (`IReverseGeocoder.Nearest`) und
  **Änderungsdatum der Dateien beibehalten** → `IGeoTagService.SetLocationAsync`. Dazu `IMapPicker` als Kartenfenster
  **Ort wählen** (Klick setzt die Nadel, Ortssuche) für den Dialog „Ort setzen…“.
- **Kartenkacheln:** eigene Kachelkarte (`MapControl`, kein Mapsui/WebView2, keine neuen Pakete) mit
  OpenStreetMap-Kacheln nach den Nutzungsregeln: User-Agent „Lumora/0.2.54 (private use)“, höchstens zwei
  gleichzeitige Abrufe, nur der sichtbare Ausschnitt, Platten-Cache `%LocalAppData%\PhotoViewerV3\cache\maptiles`
  (Ablaufzeit aus den HTTP-Kopfzeilen, mind. 7 Tage; 500 MB, LRU) über das neue `ICatalogStoragePaths`, Zuordnung
  „© OpenStreetMap-Mitwirkende“ unten rechts. Offline: graue Karte bzw. Cache mit Hinweis, Gruppen funktionieren
  weiter. `PHOTOVIEWERV3_MAP_TILES=off` schaltet die Kacheln ab (Rauchtest und Tests immer ohne Netz).
- **Bibliotheks-Trennung:** nur die Fotos des aktuellen Bereichs (`IPlacesScope`, heute aktive Bibliothek);
  Bibliothekswechsel lädt die Karte neu. Hinweis-Leiste im versteckten Modus („der Kartenserver sieht den betrachteten
  Ausschnitt“, `IMapPrivacyContext`).
- Info › Verwendete Bausteine: Zeile „Karte“; Lizenzen: OpenStreetMap (ODbL, Kachel-Nutzungsbedingungen).
- **Bis zum Zusammenbau** (Agent V): Vertragstypen als Platzhalter `Services/Places/PlacesContract.Placeholder.cs`,
  Dienste als Sitzungsspeicher `Services/Places/Placeholders` (TryAdd) — beim Zusammenbau löschen.

### Tests
- `tests/PhotoViewerV3.Tests`: 744 (+9, 1 übersprungen ohne Modell) (`Places/GeoClusterIndexTests`): Gruppen zerfallen beim Hineinzoomen, Summen je
  Stufe, Zerfalls-Zoom, Ausschnitt, bestes Foto, gleicher Ort, Begrenzungsrahmen, Mercator, Messung 500 000 Punkte.
- `tests/PhotoViewerV3.Wpf.Tests`: 805 (+25, 1 übersprungen), dreimal hintereinander grün (`Places/`): Start-Ausschnitt = Ausdehnung + Rand, Welt ohne Fotos, Zoom um den
  Mauspunkt; ViewModel mit Fake-Daten (Trefferzeile, Trennung, Bibliothekswechsel, Hineinzoomen, Lightbox, Galerie-
  Nachricht, Marker behält seinen Index nach dem Neuladen, Änderungen anderer Bibliotheken laden nicht neu, Verorten
  mit den richtigen Ids, Abbruch, fremde Ids, Ortssuche, Hinweis versteckt); Kachelquelle nur mit
  Fake-Handler (User-Agent, ≤ 2 parallel, Cache, offline, LRU); echte Seite ohne Netz (Marker gezeichnet, Karte ein
  Blatt im Automation-Baum, „Ohne Ort“ virtualisiert, Kontextmenü), Dialog und Kartenfenster.
- Rauchtest: neuer Schritt **„Orte-Karte“** vor „Ordnerüberwachung“ (`SmokeScenario.Places.cs`, Kachelquelle aus):
  3 Fotos per „Hier verorten“, 2 per Ablegen verortet, Weltansicht eine Gruppe, Klick zerfällt sie, „In der Galerie
  zeigen“ (Nachricht + Galerie; Trefferzahl wird geprüft, sobald `GalleryGeoFilter` von Agent V da ist); Screenshots
  `39-orte-karte-hell`, `40-orte-karte-dunkel`. Navigationsprüfung um „Orte“ ergänzt.

## [0.2.53] — 2026-10-21

### Neu (ADR-0050) — Orte: GPS aus Fotos lesen, selbst verorten mit EXIF-Schreiben, Ortsnamen offline, Filter „Ort“
- **Katalog** (Migration `AddGeoLocation`, Vertrag mit der Karte): `Files.Latitude`/`Longitude` (WGS84), `GeoSource`
  (NULL = noch nicht gelesen, keine, EXIF/XMP, Video, manuell), `PlaceId` (GeoNames-Id); gefilterte Indizes
  `IX_Files_Library_Geo` und `IX_Files_Library_Place`; Tabelle `GeoTagUndo`; `OperationType.SetGeoLocation = 8`.
- **Lesen** (`Services/Metadata`, verwaltet, keine neuen Pakete): EXIF-GPS aus JPEG, TIFF/RAW, RAF, PNG, WebP, HEIC
  (der Container-Durchgang des Aufnahmedatums ist jetzt generisch), Videos über `©xyz` bzw. Apple ISO 6709, eine
  **XMP-Begleitdatei** (`name.ext.xmp`, sonst `name.xmp`) gewinnt. **Nachtragen im Hintergrund** nach jedem Scan und
  einmalig für bestehende Kataloge (eigene Schleife wie beim Aufnahmedatum: Stapel, Single-Writer, Scan-Sperre, keine
  Cloud-Platzhalter), Statusleiste „Orte: n / m“; Incremental-Scan liest geänderte Dateien neu, „Neu aufbauen“ behält
  manuelle Orte.
- **Ortsnamen offline** (`Services/Places/ReverseGeocoder`): GeoNames `cities5000` ohne Stadtteile, verdichtet von
  `build/geonames/Build-GeoNames.ps1` zu einer eingebetteten Ressource (1,5 MB, 64 584 Orte); deutsche Länder-,
  Regions- und Städtenamen aus kleinen kuratierten Tabellen (`build/geonames/*-de.tsv`). Nächster Ort über einen
  Raster-Index (1,8–8 µs je Aufruf, Grenze 100 km), Suche auch „Muenchen“/„Paris, Frankreich“. Namensnennung „GeoNames,
  CC BY 4.0“ in Info › Verwendete Bausteine und Alle Lizenzen.
- **Ort setzen…** (Aktionsleiste „Ort…“, Rechtsklick): Ortssuche, Koordinaten (auch „48.2082, 16.3738“ oder Grad/Minuten/
  Sekunden), „Ort von anderem Foto übernehmen“ (zuletzt gesetzte und häufige Orte der Bibliothek), „Ort entfernen“,
  „Änderungsdatum beibehalten“ (Standard an); Haken `IMapPicker` für die Kartenauswahl der Karte. Geschrieben wird
  **JPEG verlustfrei ins EXIF** (nur das APP1-Segment ändert sich, Bilddaten Byte für Byte gleich), RAW/HEIC/TIFF/PNG/
  WebP in eine **XMP-Begleitdatei**, Videos nur in den Katalog — über eine Temp-Datei, geprüft und ersetzt (nie halbe
  Dateien), unter der Scan-Sperre und als eigener Schreibvorgang für die Ordnerüberwachung. Schreibgeschützte, gesperrte
  oder nur in der Cloud liegende Dateien bleiben unverändert und erscheinen mit Grund in einer Fehlerliste.
- **Rückgängig** in der Galerie (gemeinsam mit Datei-Operationen und „Datum setzen“, jüngster Eintrag zuerst) stellt die
  Dateien Byte für Byte wieder her (altes EXIF-Segment, alte Änderungszeit; angelegte XMP-Dateien in den Papierkorb) —
  nur, wenn sie seither unverändert sind.
- **Galerie:** Filter **Ort** in der Zeile „Inhalt“ (Länder → Regionen → Orte mit Anzahl, auf-/zuklappbar, Suche, „ohne
  Ort“, „ohne Ortsnamen“), Filter **Kartenausschnitt** (`GalleryGeoFilter`, Empfänger von
  `ShowFilesOnMapSelectionMessage`), beide in „Es gilt“ und im Zähler; Listenspalte **Ort**. **Lightbox:** Zeile „Ort:
  Wien, Österreich (48,2082° N, 16,3738° O)“. Karten-Abfragen `IGeoQueryService` (Punkte, Ausschnitt auch über die
  Datumsgrenze, Ausdehnung) — alle nur für die übergebenen Bibliotheken.

### Tests
- `tests/PhotoViewerV3.Tests`: +68 — `Places/MediaGpsReaderTests` (je Format, Byte-Reihenfolgen, Süd/West, Höhe, 0/0,
  Videos, ISO 6709, XMP-Vorrang, JPEG ignoriert die Lightroom-XMP des RAW-Paars), `Places/GeoWriterTests` (JPEG ohne/mit EXIF/GPS in II und MM: nur das Segment ändert
  sich, SOS-Daten gleich; Entfernen, Änderungsdatum, schreibgeschützt/gesperrt/kaputt ohne Temp-Reste, 64-KB-Grenze,
  HEIC → XMP, vorhandene Lightroom-XMP ergänzt und entfernt, Begleitdatei ohne GPS bleibt unangetastet, Änderungszeit
  mit Bruchteilen zurück), `Places/ReverseGeocoderTests` (deutsche Namen,
  Datumsgrenze, Meer, Suche, Nearest < 0,1 ms), `Places/GeoLocationServiceTests` (Nachtragen, Ort setzen + Protokoll +
  Rückgängig byte-genau, veränderte Datei, HEIC/Video, Entfernen + „Neu aufbauen“, Fehler, Incremental, Karte und Zähler
  nur im Bereich, zuletzt verwendete Orte, mehrfaches Dispose).
- `tests/PhotoViewerV3.Wpf.Tests`: +18 — `Gallery/GalleryPlaceFilterTests` (Orte/ohne Ort/Kartengruppe/Ausschnitt,
  UND mit Dateiart, andere Bibliothek), `Gallery/PlacesUiTests` (Flyout-Baum, Sichtbarkeit, Koordinaten-Schreibweisen,
  Dialog-Logik), `Gallery/PlacesWicTests` (WIC-GPS gelesen, Lumoras EXIF von WIC gelesen, Dialogfenster).
- Rauchtest-Schritt „Orte: GPS lesen und setzen“ (vor „Ordnerüberwachung“): Foto mit WIC-GPS → „Wien, Österreich“,
  Ort setzen (Graz) → GPS im EXIF, SOS-Daten und Änderungsdatum gleich, Filter „Ort: Graz“ über das Flyout, Rückgängig →
  Datei Byte für Byte wie vorher; Screenshot `40-galerie-ort`.
- Spike #13 (`docs/spikes/13-orte.md`, Harness `spike13-places`): GPS je Datei warm +0,16 ms, WIC-Übereinstimmung
  1 619 / 1 619, Nachtragen 790 Dateien/s, Schreiben 4-MB-JPEG 38 ms, Rückgängig byte-gleich 50 / 50.

### Offen
- Rückgängig-Speicher alter EXIF-Segmente wird nicht aufgeräumt (≈ 10–60 KB je verortetem JPEG).
- Verschieben/Umbenennen nimmt XMP-Begleitdateien noch nicht mit.
- Ortsfilter und Karte nutzen bis zum Zusammenbau die aktive Bibliothek als Bereich (Agent T stellt auf die
  Verknüpfungsgruppe um).

## [0.2.52] — 2026-10-21

### Feature (ADR-0049) — Versteckte Bibliotheken im verschlüsselten Container
- **Nur mit Startschalter `_hidden`** (`--hidden`, `/hidden`): ohne ihn zeigt Lumora keinerlei Spur. Mit ihm sucht
  Lumora auf allen eingehängten lokalen Laufwerken (Wurzel + eine Ebene; nicht Netz, CD/DVD, Wechselmedien) nach
  `.lumora`-Ordnern und bietet die gefundenen versteckten Bibliotheken im **Bibliotheks-Umschalter** und im neuen
  Reiter **Einstellungen › Versteckte Bibliotheken** an. Gemerkt wird nichts.
- **Anlegen**: „Versteckte Bibliothek anlegen…“ → Laufwerk des eingehängten Containers + optional ein Ordner direkt
  darauf, Name, Lumora-Kennwort zweimal; Hinweise „Container nicht in OneDrive“ und „`_hidden` nicht in eine sichtbare
  Verknüpfung“. Lumora legt `<Ordner>\.lumora\` an (versteckt, System, nicht indiziert) mit eigenem Katalog
  (`library.db`), Vorschaubildern/Gesichtsausschnitten, Protokollen (`logs\lumora-*.log`), `cache\`, `temp\` und der
  Kenndatei `hidden-library.json`, wechselt hinein und scannt. Lumora verschlüsselt selbst nichts.
- **Öffnen** mit Lumora-Kennwort (Prüfwert PBKDF2-HMAC-SHA-256, 600 000 Iterationen, nie das Kennwort).
  **Kennwort vergessen**: im eingehängten Container `.lumora\reset-password.txt` anlegen → „Kennwort zurücksetzen“.
  Ist der Container unter einem anderen Laufwerksbuchstaben eingehängt als beim Anlegen, nennt Lumora den alten.
- **Katalogwechsel zur Laufzeit ohne Neustart**: jede Katalog-Sitzung hat ihren eigenen DI-Container
  (`ICatalogHostSwitcher` in `App`): Lightbox/Dialoge schließen, Host entsorgen (Hintergrunddienste, DB-Verbindungen,
  Caches), Messenger zurücksetzen, neuen Host mit Migration starten, Oberfläche neu aufbauen — in beide Richtungen.
  Neu: `ICatalogContext`/`ICatalogStoragePaths` (`IsHidden`, Katalog-/Thumbnail-/Log-/Cache-/Temp-Pfade) für alle
  Module; Agent T blendet „Verknüpfen“ bei `IsHidden` aus.
- **Zusperren**: von Hand (Umschalter, Reiter), automatisch nach Untätigkeit (Standard 10 min Maus/Tastatur in Lumora,
  einstellbar; wartet auf laufende Scans/Datei-Vorgänge) und beim Beenden. **Container aushängen** (Standard an):
  VeraCrypt per Befehlszeile (`/dismount X /quit /silent`, sonst `/unmount`), BitLocker-VHDX per `Dismount-DiskImage`,
  sonst Hinweis. **Container öffnen…** startet VeraCrypt, das sein Kennwort selbst abfragt.
- **Spurenfreiheit**: Protokolle je Sitzung (versteckt nur im Container, Nachzügler verworfen, Absturzberichte danach
  ohne Details), Einstellungen der versteckten Bibliothek in ihrer Kenndatei, Dateidialoge ohne „Zuletzt verwendet“
  (`AddToRecent = false`, auch Sortierer/Ordnerauswahl), in der versteckten Sitzung gesperrt: „Im Explorer zeigen“,
  „Ordner öffnen“, „Mit Standard-App öffnen“; Katalog-Umzug gesperrt; Gesichtsmodelle aus dem normalen Modellordner.
- `.lumora` wird nie gescannt, geflattet, als Ziel angeboten oder per „Ordner übernehmen“ verschoben.

### Tests
- `tests/PhotoViewerV3.Tests`: +35 — `HiddenLibraries/HiddenLibraryStoreTests` (Prüfwert, Kenndatei, Attribute,
  Zurücksetzen nur mit Bestätigungsdatei, Einstellungen, Pfade in `.lumora`), `HiddenLibraryLocatorTests` (Suche Wurzel
  + eine Ebene, Ordnerregeln, Startschalter, Untätigkeit mit Fake-Uhr), `ContainerToolsTests` (VeraCrypt/VHDX mit Fakes,
  nie das Systemlaufwerk, kein Kennwort/Explorer beim Einhängen), `HiddenCatalogTests` (Sitzung in der Services-Schicht,
  Scan ohne `.lumora`, normaler Datenordner leer).
- `tests/PhotoViewerV3.Wpf.Tests`: +20 — `HiddenLibraries/CatalogSessionManagerTests` (Anlegen, falsches Kennwort,
  Zusperren nach Untätigkeit, Aushängen, Beenden, Laufwerksbuchstabe, ohne Schalter nichts), `CatalogLogRouterTests`,
  `HiddenLibrariesUiTests` (Reiter/Umschalter nur mit Schalter, Dialoge), `HiddenSessionTraceTests` (echte
  App-Zusammenstellung: 0 Spuren im AppData-Testordner).
- Rauchtest: neuer Schritt „Versteckte Bibliothek“ vor der Ordnerüberwachung (`PHOTOVIEWERV3_SMOKE_HIDDEN`, im Rauchtest
  Standard an): anlegen im Temp-Ordner, 3 Fotos + Vorschaubilder + Protokoll im Container, Zusperren nach Untätigkeit mit
  Fake-Aushängen, falsches Kennwort, wieder öffnen, normale Bibliothek unverändert, Spurenprüfung im AppData (0 Treffer);
  die Ordnerüberwachung läuft danach mit den Diensten der neu aufgebauten Sitzung.

### Offen
- Windows-eigene Spuren außerhalb von Lumora: Dateidialog-MRU („Container öffnen… → Durchsuchen“), Verlauf in
  VeraCrypt, Auslagerungsdatei/Ruhezustand, Befehlszeile in der Prozessliste.
- VeraCrypt-Befehlszeile nicht am echten VeraCrypt geprüft (auf Herberts PC nicht installiert) — nur mit Fakes.

## [0.2.51] — 2026-10-21

### Feature (ADR-0048) — Bibliotheken strikt getrennt, Verknüpfen als Einstellung, Ansicht „Alle verknüpften“
- **Eine Bibliothek sieht nichts aus einer anderen** (Herberts Vorgabe): Tags, Kategorien (mit Werten), Personen und
  Beziehungen gehören jetzt einer **Bibliotheks-Gruppe** statt global zu sein. Galerie (Zähler, Seiten, Suche nach
  Datei-/Tag-/Kategorie-/Personennamen, alle Filter, Kategorie-Sortierung), Tag-/Kategorie-/Personen-/Beziehungs-Flyouts,
  Einstellungs-Reiter (Tags, Kategorien, Personen & Beziehungen), Personen-Seite (Liste, Details, Gruppen-Vorschläge,
  ignorierte Gesichter), Lightbox-Personen, Gesichts-Lernen und Vorschläge (Trainingsdaten und Kandidaten nur aus dem
  Bereich), Ausreißer-Prüfung und Statusleiste lesen alle über den neuen **Bereichsdienst `ILibraryScope`**. Gleich
  benannte Tags/Personen in zwei Bibliotheken sind verschiedene. Schreibwege prüfen in der Datenbank, dass Tag, Kategorie
  bzw. Person zur Gruppe der Datei gehören — ein Begriff aus A landet nie an einer Datei aus B.
- **Migration `AddLibraryScoping`** (ohne Tabellen-Neuaufbau, `Down` wirft — Rückweg ist die Sicherung vor der
  Migration): `LibraryGroups`, `GroupId` an Libraries/Tags/Categories/Persons/PersonRelations, Eindeutigkeit je Gruppe
  (`UX_Tags_Group_Name`, `UX_Categories_Group_Name`, `IX_PersonRelations_Group_Name`, `IX_Persons_Group_IsMe`,
  `IX_Persons_Group_Name`, `IX_Libraries_Group`). Bestand verlustfrei: jede Bibliothek bekommt eine eigene Gruppe und die
  Begriffe, die sie benutzt (bei mehreren Bibliotheken eine Kopie je weiterer Bibliothek, Zuordnungen, Gesichter,
  Vorschläge, Ablehnungen und Titelbilder umgehängt); **nicht benutzte Begriffe bekommt jede Bibliothek**. Mit einer
  Bibliothek ändert sich nichts Sichtbares.
- **„Bibliotheken verknüpfen“** als eigene Einstellung (Einstellungen › Bibliotheken & Eingänge, Kasten je Bibliothek):
  **Verknüpfen mit…** (Dialog mit Partner-Auswahl, Zusammenfassung „wird eins / kommt hinzu“ und Liste gleichnamiger
  Personen — Häkchen = zusammenführen, Standard; getrennt gehaltene bekommen den Bibliotheksnamen als Rufnamen-Zusatz),
  **Verknüpfung lösen** (Bestätigung mit Zusammenfassung; die Bibliothek bekommt Kopien ALLER gemeinsamen Begriffe und
  Personen, ihre Zuordnungen zeigen auf die Kopien) und **Gruppe umbenennen…**. Alles in einer Transaktion unter der
  Scan-Sperre aller betroffenen Bibliotheken (`ILibraryLinkService`).
- **Ansicht „Alle verknüpften (Name)“** im Bibliotheks-Umschalter für Gruppen mit ≥ 2 Bibliotheken: Galerie, Suche,
  Filter, Personen und Ordnerbaum (je Bibliothek ein Kopfknoten) zeigen alle Bibliotheken der Gruppe, Zähler = Summe;
  Vorschaubilder und Gesichts-Ausschnitte liegen im Cache der eigenen Bibliothek jeder Datei. Gesperrt in dieser Ansicht:
  Verschieben…, „In die Bibliothek“ und Entpacken aus der Galerie, „Eingang hinzufügen…“ auf der Ordner-Seite (Ziel nicht
  eindeutig). Eine einzelne Bibliothek wählen beendet die Ansicht.
- **Versteckte Bibliotheken (Vorbereitung für Agent U):** `ICatalogContext.IsHidden` — in einem versteckten Katalog
  wirft das Verknüpfen (`LibraryLinkNotAllowedException`), die Einstellung ist ausgeblendet, „Alle verknüpften“ nie
  angeboten.
- **Bibliothek entfernen:** war sie die letzte ihrer Gruppe, gehen Gruppe und ihre Begriffe mit; Begriffe, die angelegt
  wurden, als es noch keine Bibliothek gab, übernimmt die erste Bibliothek.
- **Fix:** zwei Bibliothekswechsel in derselben Sekunde ließen die höhere Id statt der zuletzt gewählten aktiv
  (`LastOpenedAt` in ganzen Sekunden) — „jetzt geöffnet“ liegt jetzt mindestens eine Sekunde nach dem jüngsten anderen.
- **Tests:** `LibraryScopingTests` (Isolation inkl. Lernen/Vorschläge/Gruppen, Verknüpfen/Lösen verlustfrei, „Alle
  verknüpften“, versteckter Katalog, Übernahme ohne Bibliothek, Entfernen), `LibraryScopingMigrationTests`
  (Mehrbibliotheks-Bestand), `GalleryLibraryScopeTests` (Galerie-SQL, Umschalter, Einstellungen); Rauchtest-Schritt
  „Bibliotheks-Trennung“ vor „Ordnerüberwachung“.

## [0.2.50] — 2026-10-21

### Fix — Personen-Kacheln ohne Binding-Fehler, Design-Time-Datenbank nie mehr der echte Katalog
- **Personen-Seite:** Titelbilder der Personen- und Gruppen-Kacheln werden als `Image` mit runder bzw. abgerundeter
  `Clip`-Geometrie gezeichnet statt als `ImageBrush`-Füllung. Beim Recyceln eines Containers (DataContext kurz
  `DisconnectedItem`) meldete der Freezable-Pinsel einen Binding-Fehler (`Image` auf `NamedObject`), sichtbar als
  gelegentlich „2 WPF-Binding-Fehler“ im Rauchtest (2 von 5 Läufen). Ein FrameworkElement überspringt diesen Zustand still.
- **`DesignTimeLibraryContextFactory`** (nur für `dotnet ef`) zeigt auf eine Wegwerf-DB im Temp-Ordner
  (`%TEMP%\PhotoViewerV3-DesignTime\design-time.db`) statt auf `%LocalAppData%\PhotoViewerV3\library.db`. Ein
  `dotnet ef migrations remove --force` beim Bau von 0.2.48 hätte sonst den echten Katalog öffnen können (geprüft:
  Katalog unverändert, alle Migrationen vorhanden, `quick_check` ok).
- **Zusammenbau 0.2.46–0.2.49:** Collage-Vorlagen relativ eingebunden (`/PhotoViewerV3;component/…` zeigte nach der
  Umbenennung zu `Lumora.dll` ins Leere), Spalten von Collage (Bildmaße) und Aufnahmedatum gemeinsam in `FileColumns`;
  ModelSnapshot geprüft (`dotnet ef migrations has-pending-model-changes`: keine Änderungen).
- **Rauchtest „Collage-Ansicht“** sucht die SICHTBARE `GalleryView` (Galerie- und Ordner-Seite haben je eine Instanz);
  vorher konnte er je nach Schrittfolge die verborgene Ordner-Instanz erwischen und lief in den Timeout. Die
  Timeout-Meldung nennt jetzt Panel-Zustand und Seitengröße.

## [0.2.49] — 2026-10-21

### Feature (ADR-0047) — Neuer Produktname Lumora mit Schriftzug und App-Symbol
- **Die App heißt jetzt „Lumora“** (*Lumen* + *Memoria*; Name und Design von Herbert gewählt, Mockup 07
  „Gewählt · A + B + C“, jetzt `mockups/07-lumora-schriftzug.html`). Fenstertitel und Titelleiste des Hauptfensters
  „Lumora“ mit App-Symbol, Lightbox-Fenstertitel „Dateiname – Lumora“ (Taskleiste/Alt+Tab; die schlanke Titelleiste
  zeigt weiter nur den Dateinamen), Versionstexte „Lumora x.y.z“, Startfehler, Umzugs-/Download-/Lizenztexte und
  „kein Lumora-Katalog“.
- **Programmdatei heißt jetzt `Lumora.exe`** (`src\PhotoViewerV3.Wpf\bin\Debug\net10.0-windows\Lumora.exe`; Produkt,
  Titel und Dateibeschreibung „Lumora“). Verknüpfungen auf die alte `PhotoViewerV3.exe` bitte neu anlegen — der Build
  entfernt die alte EXE samt DLL aus dem Ausgabeordner (gesperrte Dateien einer noch laufenden alten App bleiben liegen).
  Portables Paket: `artifacts\Lumora-<Version>-win-x64` (+ ZIP); `build/publish.ps1` prüft zusätzlich den Produktnamen,
  `build/run-smoke.ps1` nimmt den Prozessnamen aus der übergebenen EXE.
- **Daten bleiben unter `%LocalAppData%\PhotoViewerV3`** (bzw. im Ordner aus `database-location.json`) — kein Umzug,
  der Katalog läuft unverändert weiter. Ebenfalls unverändert: Projekt-/Namespace-/Ordnernamen `PhotoViewerV3.*`, die
  Umgebungsvariablen `PHOTOVIEWERV3_*` und die Protokolle `logs\photoviewer-*.log`.
- **Schriftzug „lumora“** (`Shell/Branding/LumoraWordmark`): Segoe UI Variable Display im Verlauf Indigo → Violett →
  Koralle → Bernstein über das ganze Wort (dunkel: #818CF8 → #A78BFA → #FB8A7A → #FBBF24), das „o“ eine dünne Linse
  mit dem Funken in der Mitte, gemessen am echten „o“ der Schrift; bis 24 px normale Strichstärke und kräftige Linse.
  Oben in der linken Leiste über dem Bibliotheks-Umschalter (Symbol + Schriftzug 22 px) und groß im Info-Kopf (Symbol
  56 px + Schriftzug 30 px statt Bild-Symbol und „PhotoViewer V3“). Farben folgen Hell/Dunkel/System sofort
  (`LumoraBrandDictionary` in `App.xaml`, DynamicResource). Info › Vorgänger: „PhotoViewer V3 (bis 0.2.48) ·
  PhotoViewer V2 (PowerShell-Web-App)“.
- **App-Symbol**: Indigo-Kachel mit gefächertem Fotostapel und Funke (unter 32 px ein Foto, großer Funke) als Vektor
  (`Shell/Branding/LumoraIcon.xaml`) und als `Lumora.ico` mit 16/20/24/32/40/48/64/96/128/256 px — reproduzierbar
  erzeugt mit `pwsh -File build\make-icon.ps1` (WPF `RenderTargetBitmap`, ICO-Container selbst geschrieben, keine neuen
  Pakete). EXE-Symbol (`ApplicationIcon`), Fenster-Symbol aller Fenster (`AppFluentWindow`, Taskleiste), Titelleiste.
- Eigene XAML-Ressourcen werden nicht mehr über den Assembly-Namen eingebunden (`/PhotoViewerV3;component/…` →
  relativ; betraf die sechs Galerie-Filter-Verweise auf `GalleryFilterStyles.xaml`).

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: +16 — `Shell/LumoraBrandingTests` (Assembly/Produkt/Dateibeschreibung „Lumora“,
  sichtbare Titel, kein „PhotoViewer“ in sichtbaren Texten, keine Pack-URIs mit Assembly-Namen, Info-Kopf und
  Navigation mit Symbol + Schriftzug, Strichstärke je Größe, Pixel-Stichprobe Verlauf hell/dunkel, Pinsel folgen dem
  Farbschema, Linse sitzt wie ein „o“ bei 22/30/68 px), `Shell/LumoraIconFileTests` (alle Größen, eingecheckte Datei =
  Vektorquelle, kleine Größen vereinfacht, eingebettet + Fenster-Symbol). `InfoViewModelTests` auf „Lumora“.
- Rauchtest: Schritt „Einstellungen-Reiter und Info“ prüft zusätzlich Fenstertitel + Symbol, Programmdatei
  `Lumora.exe`, Schriftzug in der Leiste und im Info-Kopf, Vorgänger-Zeile, Schriftzug-Farben hell/dunkel,
  Versionsinfo „Lumora x.y.z“; Screenshots `30-einstellungen-info-hell`/`31-einstellungen-info-dunkel` zeigen Leiste und
  Info.

## [0.2.48] — 2026-10-21

### Neu (ADR-0046) — Aufnahmedatum: aus EXIF/Video/Dateiname, Sortierung danach, Datum selbst setzen
- **Katalog** (Migration `AddCapturedAt`): `Files.CapturedAt` (UTC), `CapturedAtSource` (NULL = noch nicht gelesen,
  unbekannt, EXIF, Video, Dateiname, manuell), `CapturedAtPrecision` (genau/Tag/Monat/Jahr), `CapturedAtOffset`
  (Ortszeit der Aufnahme); Ausdrucks-Index `IX_Files_Library_Captured (LibraryId, COALESCE(CapturedAt, ModifiedAtUtc)
  DESC, Id DESC)`; Tabelle `CaptureDateUndo` für Rückgängig.
- **Lesen** (`Services/Metadata`, verwaltet, ohne neue NuGet-Pakete): EXIF `DateTimeOriginal` (+ `OffsetTimeOriginal`)
  aus JPEG, TIFF/DNG/CR2/NEF/ARW/ORF/RW2, Fuji RAF, PNG `eXIf`, WebP `EXIF`, HEIC/HEIF/AVIF (auch ohne HEIF-Erweiterung);
  Videos MP4/MOV/3GP über Apple `creationdate` (mit Zone) bzw. `mvhd` (UTC); sonst der Dateiname (`IMG_20240712_153012`,
  `PXL_…`, `Screenshot 2026-09-30 105535`, WhatsApp `IMG-20240712-WA0001` = nur Tag …). `DateTimeDigitized`/IFD0-`DateTime`
  zählen nicht (bei Scans das Scan-Datum). Gemessen schneller als die WIC-Metadatenabfrage und für alle verglichenen
  JPEGs gleich (Spike #12).
- **Nachtragen im Hintergrund** statt im Scan: nach jedem Scan, bei neuen Dateien und 15 s nach dem Start (bestehende
  Kataloge einmalig ganz), Stapel zu 200 mit einer Transaktion, ruht bei Scan-Sperre/Umzug, liest keine
  Cloud-Platzhalter (kein Download) und keine Dateien nicht erreichbarer Wurzeln, Fortschritt in der Statusleiste
  („Aufnahmedatum: 1.234 / 19.556“). Der Scan wird nur durch den neuen Index langsamer (Spike #12: Fresh-Scan 24 217
  Dateien Median 4,36 → 4,74 s, **+8,8 %**); Lesen im Scan hätte ihn vervielfacht (Nachtragen derselben Dateien mit
  kaltem Cache 179,5 s = 135 Dateien/s — 500 000 ≈ 1 h einmalig im Hintergrund). Leser je Datei warm 0,12 ms gegen
  1,3 ms über WIC, Ergebnis für 1 712 von 1 712 EXIF-Bildern gleich. Der Index wird beim Start zusätzlich idempotent
  sichergestellt (`DatabaseInitializer`).
- **Sortierung „Aufnahmedatum“** (vorn in der Sortierleiste, **Standard**, Richtung wie bei Datum); Dateien ohne Datum
  stehen mit ihrem Änderungsdatum dazwischen. Kombinierbar mit allen Filtern, Suche und Ordner — Pläne ohne Vollscan;
  500 000 Dateien: Seite 1 8 ms, letzte Seite 3 ms, Mitte 0,2–1,3 s (Änderungsdatum zum Vergleich 2,3 s), Ordner 1,3 s
  (Änderungsdatum 4,4 s), Tag-Filter 0,2 s.
- **Anzeige**: Lightbox-Info „Aufgenommen: 12.07.2024 15:30 (EXIF)“ bzw. „ca. 1982 (manuell)“, Kachel-Tooltip,
  Listenspalte „Aufgenommen“ (Kopfklick sortiert die ganze Trefferliste).
- **Datum setzen…** (Aktionsleiste „Datum…“, Rechtsklick auf Kachel/Zeile, Rechtsklick auf Ordner im Ordnerbaum mit
  „Unterordner einschließen“): genau (Datum + Uhrzeit), ungefähr (Jahr bzw. Monat + Jahr, „ca. 1982“), verschieben um ±
  Tage/Stunden/Minuten (falsch gestellte Kamera; noch nicht gelesene Dateien werden dafür erst aus der Datei gelesen),
  zurücksetzen (wieder lesen). Nur im Katalog; Protokoll als
  OperationBatch `SetCaptureDate` mit FileOperation je Datei; **Rückgängig** in der Galerie (jüngster Eintrag aus
  Datei-Operationen und „Datum setzen“). Manuelle Daten bleiben bei Incremental-Scan und **„Neu aufbauen“** immer
  erhalten (`UserDataCarryOver`); bei geändertem Inhalt werden die übrigen neu gelesen.
- Dienste: `ICaptureDateService`/`CaptureDateService` (+ `.Backfill`), `ICaptureDateReader`, `MediaDateReader`,
  `ExifTiffReader`, `FileNameDateParser`, `CaptureDateText`; Wpf: `Gallery/CaptureDates` (Dialog
  `SetCaptureDateDialog` auf `AppFluentWindow`, `CaptureDateActions`, `AddCaptureDateUi()`), Partials für Galerie,
  Ordnerbaum, Lightbox, Statusleiste. Harness `spike12-capture-date`, Doku `docs/spikes/12-aufnahmedatum.md`.

### Tests
- `tests/PhotoViewerV3.Tests`: 722 (+75, 1 übersprungen ohne Modell) (`Metadata/`): Leser je Format mit synthetischen Dateien (JPEG mit/ohne Zone, TIFF
  big-endian, PNG hinter IDAT, WebP mit/ohne Präfix, HEIC infe v2/v3, MP4 v0/v1 und moov hinter 1-MB-mdat, MOV Apple
  creationdate, Abgeschnittenes ohne Ausnahme), Dateinamen-Muster (positiv/negativ, Zone), Texte, Dienst (Nachtragen
  EXIF/Dateiname/unbekannt, fehlende Dateien, nicht erreichbare Wurzel, Scan-Sperre, manuelles Datum während des Lesens,
  Verschieben einer noch nicht gelesenen Datei, genau/ungefähr/
  verschieben/zurücksetzen, Protokoll + Rückgängig, Neu aufbauen behält manuell + Rückgängig danach, Incremental bei
  geändertem Inhalt, Ordner mit/ohne Unterordner, ungültige Eingaben).
- `tests/PhotoViewerV3.Wpf.Tests`: 717 (+49, 1 übersprungen), dreimal hintereinander grün: Sortierung (beide Richtungen, Rückfall Änderungsdatum, Reverse-Trick, Filter,
  Pläne ohne Vollscan für 32 Kombinationen, Index ohne Nachsortieren, DTO/Tooltip/Listentext, Sortierfeld/-spalte),
  Dialog-Logik (Prüfung, gebaute Änderung, Ordner-Zähler, Vorgaben), echtes Dialogfenster, WIC-JPEG/TIFF als Quelle,
  Statusleiste, Messung `CaptureDateSortMeasurementTests` (`PV3_CAPTURESORT_FILES`).
- Rauchtest 32/32 OK, 0 Binding-Fehler: neuer Schritt **„Aufnahmedatum“** vor „Ordnerüberwachung“ (`SmokeScenario.CaptureDate.cs`): JPEG mit EXIF
  1975 → nachgetragen (Herkunft EXIF), Sortierung Aufnahme alt → neu stellt es vorn, Änderungsdatum nicht; „Datum
  setzen“ ca. 1982 (manuell, Jahr, Listentext „ca. 1982“) und Rückgängig (alter Wert, Batch zurückgenommen).
  Screenshot `26-galerie-aufnahmedatum`.

### Offen
- Video-Container ohne Zone (`mvhd`) gelten als UTC; Kameras, die Ortszeit schreiben, liegen um den Zonenabstand daneben.
- AVI, MKV, MTS, WMV und Canon CR3 nur mit Dateinamen-Datum; Dateien nur in der Cloud bleiben „noch nicht gelesen“ und
  werden in jedem Durchgang erneut geprüft (nur Attribut, kein Download).
- Die Sortierung wird weiterhin nicht gespeichert (Start immer mit „Aufnahmedatum, neueste zuerst“).
- Listenspalte „Aufgenommen“ erscheint bei schon gespeicherter Spaltenauswahl nicht von selbst (Rechtsklick auf die
  Kopfzeile).
- Galerie-Ordnerfilter mit Datums-Sortierung über 500 000 Dateien braucht 1–4 s für Seite 1 (Planer läuft den
  Datums-Index entlang; schon vorher so beim Änderungsdatum).

## [0.2.47] — 2026-10-21

### Feature (ADR-0045) — Personen mit Vor-/Nachname, Rufname, eigener Beziehung, Geburtsdatum und „Das bin ich“
- **Schema** (Migration `AddPersonDetails`): `Persons` + `FirstName`, `LastName`, `Nickname`, `RelationId` →
  neue Tabelle `PersonRelations (Id, Name NOCASE, eindeutig)` ON DELETE SET NULL, `BirthYear/BirthMonth/BirthDay`,
  `IsMe`; Indizes `IX_Persons_Relation`, `IX_PersonRelations_Name`, eindeutiger Teilindex `IX_Persons_IsMe WHERE IsMe = 1`.
  `RelationId` per `ALTER TABLE … ADD COLUMN … REFERENCES` (kein Neuaufbau von `Persons`, Zuordnungen und Ablehnungen
  bleiben); kein Rückweg (`Down` wirft, Sicherung vor der Migration). **Bestand**: Name am ersten Leerzeichen geteilt
  (erstes Wort = Vorname, exakter Rest = Nachname), `Name` bleibt → verlustfrei.
- **`Persons.Name` = Anzeigename** (Rufname, sonst „Vorname Nachname“, `Domain/Entities/PersonNames`), bei jedem
  Schreiben nachgezogen und weiter eindeutig — Galerie-Liste („Personen“), Kachel, Lightbox-Rahmen und Personen-Flyout
  zeigen ihn ohne Änderung. Alte Einzelnamen-Wege (Lightbox „Neue Person…“) teilen genauso; mehrfache Leerzeichen
  werden dort zu einem.
- **Dienst** `IPersonDetailsService` im `FaceRecognitionService` (partial `.Details.cs`, gleicher Single-Writer, gleiche
  `PersonsChangedMessage`): anlegen/bearbeiten mit Details, Beziehung als Freitext (vorhandene ohne Groß-/Kleinschreibung
  wiederverwendet, erste Schreibweise bleibt), Beziehungen umbenennen/zusammenführen/löschen, „Das bin ich“ eindeutig.
  Personen zusammenführen übernimmt fehlende Beziehung/Geburtsdatum und „Das bin ich“. `PersonSummary.Details`
  (init-Eigenschaft), `PersonBirthDate` (Jahr / Jahr + Monat / vollständig, Alter als Spanne).
- **Dialog „Person benennen/bearbeiten“** (`Persons/Dialogs/PersonDetailsDialog`, Logik `PersonDetailsEditor`):
  Vorname, Nachname, Rufname, Beziehung (editierbares Auswahlfeld mit Vorschlägen), Geburtsdatum (Tag/Monat optional,
  Prüfung mit Meldung), „Das bin ich“ (nennt die bisherige), Vorschau des Anzeigenamens, Hinweis auf vergebenen Namen.
  Gilt für „Als neue Person benennen…“ und **Bearbeiten…** (statt „Umbenennen…“; vergebener Name → zusammenführen,
  Eingaben bleiben). Vorhandene Person beim Benennen funktioniert wie bisher.
- **Personen-Seite**: Suche (Name, Vor-/Nachname, Rufname, Beziehung), Sortieren nach Anzeigename / Nachname / Anzahl
  Fotos, **Nach Beziehung gruppieren** (Abschnitte, „Ohne Beziehung“ am Ende, Kacheln bleiben Blatt-Peers), Kachel mit
  Beziehung, Detailzeile „Schwester · geboren März 1975 (51 Jahre) · Das bin ich“. Personenauswahl findet auch
  Nachname und Beziehung.
- **Galerie**: Suche findet Vorname, Nachname, Rufname und Beziehung; Personen-Flyout sucht über dieselben Felder und hat
  den Abschnitt **Beziehung** (ODER untereinander, UND mit Personen und allen Filtern, „auch Vorschläge“ gilt mit;
  `GalleryPersonFilter.RelationIds`, `GalleryPageSource.Relations.cs`); „Es gilt“ zeigt „Beziehung „Schwester““ mit
  eigenem ×, „Leeren“ im Flyout leert beides.
- **Einstellungen › Gesichter › Personen & Beziehungen** (`Settings/PersonRelations`, `SettingsRelationsSection`):
  umbenennen (vorhandener Name → Rückfrage zusammenführen), zusammenführen mit, löschen (Personen behalten leere
  Beziehung).
- Doku: ADR-0045, PLAN Kap. 3/4, README (Personen-Details, Filter „Beziehung“, Einstellungen).

### Tests
- `tests/PhotoViewerV3.Tests`: 660 (+14, 1 übersprungen ohne Modell): `Faces/PersonDetailsMigrationTests` (Bestand
  geteilt und verlustfrei, Zuordnungen/Ablehnungen bleiben, Indizes, Fremdschlüssel SET NULL, Beziehungsname eindeutig;
  Anzeigename-Regeln; Geburtsdatum ungefähr mit Altersspanne und Prüfungen), `Faces/Assignment/PersonDetailsServiceTests`
  (anlegen mit allen Feldern, Beziehung wiederverwendet mit erster Schreibweise, bearbeiten/umbenennen, „Das bin ich“
  eindeutig auch per Index, Beziehungen umbenennen/zusammenführen/löschen, Personen zusammenführen übernimmt Details).
- `tests/PhotoViewerV3.Wpf.Tests`: 679 (+12, 1 übersprungen), dreimal hintereinander grün:
  `Persons/PersonDetailsViewModelTests` (Dialog-Logik, benennen mit Details, bearbeiten, zusammenführen behält Eingaben,
  sortieren/suchen/gruppieren, Einstellungen umbenennen → zusammenführen, löschen), `Persons/GalleryPageSourceRelationTests`
  (Suche nach Nachname/Rufname/Beziehung, Filter Beziehung ODER/UND/mit Vorschlägen, Zähler, Anzeigename in der Spalte),
  `Persons/PersonDetailsUiTests` (Dialog im Fenster ohne Binding-Fehler, Seite gruppiert mit Blatt-Kacheln).
- Rauchtest 32/32 OK, 0 Binding-Fehler — neuer Schritt **„Personen-Details und Beziehungen“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.PersonDetails.cs`): benennen mit Vor-/Nachname/Beziehung/Geburtsdatum, Vorschlag beim
  zweiten Mal („schwester“ → „Schwester“), Rufname, gruppierte Übersicht, Detailzeile, „Das bin ich“ eindeutig, Filter
  „Beziehung“ (Es gilt, UND mit Person, × an der Person lässt die Beziehung stehen), Umbenennen in den Einstellungen wirkt
  in Galerie und Personen-Seite, Suche nach Beziehung. Screenshots `35-personen-nach-beziehung`,
  `36-personen-detail-beziehung`, `37-galerie-filter-beziehung`, `38-einstellungen-beziehungen`.

### Offen
- Lightbox „Neue Person…“ fragt weiter nur einen Namen (Details danach auf der Personen-Seite).
- Alter auf einem Foto (Aufnahmedatum − Geburtsdatum) und der Assistent folgen später.

## [0.2.46] — 2026-10-21

### Neu (ADR-0044) — Collage-Ansicht in der Galerie (Mockup 06, Ansicht „Collage“)
- **Dritte Ansicht** im Segment der Sortierleiste: **Kacheln | Collage | Liste** (Symbol „Board“, wird gemerkt wie
  bisher). Fotos und Videos im **echten Seitenverhältnis**, Reihen gleich hoch und **randvoll** (Justified Layout wie
  Google Fotos/Flickr), letzte Reihe bei Zielhöhe linksbündig (nicht gestreckt), Fugen 6 px. Zielhöhe = Vorschaugrößen-
  Regler (120–320 DIP, Strg+Mausrad, Strg+Plus/Minus); gefüllte Reihen werden nur verkleinert, nie gestreckt. Panoramen
  bekommen allein die volle Breite; Seitenverhältnisse sind auf 0,4 … 6 begrenzt (nur extremere Bilder werden mittig
  beschnitten).
- **Panel** `Gallery/Controls/JustifiedTilePanel` (UI-virtualisierend, `IScrollInfo` pixelweise, Mausrad, `MakeVisible`,
  Pfeiltasten über Reihen): Umbruch vorab für die ganze Seite (`JustifiedRowLayout`, O(n)), realisiert nur sichtbare
  Reihen + 1 Pufferreihe. Regeln wie beim Kachel-Panel seit 0.2.42: recycelte Container immer vorbereitet, Anordnen über
  den echten Item-Index, Fokus-Container bleibt an seinem Platz. Bei Regler-, Breiten- oder Seitenverhältnis-Änderung
  bleibt das oberste sichtbare Item oben. Gleiche `GalleryListBox` (Blatt-Automation-Peers, Auswahl-Abgleich); Panel,
  Vorlage und Container-Stil setzt der Code-Behind je Ansicht als lokale Werte (`GalleryView.ApplyGalleryViewMode`; ein
  Stil-Trigger erzeugte beim Entfernen von Items nach einem Seitenwechsel 8 Binding-Fehler im Rauchtest).
- **Collage-Kachel** (`Gallery/Views/GalleryCollageTemplates.xaml`): Bild füllt die Zelle, kein Namensfeld — Name als
  Tooltip und beim Darüberfahren unten eingeblendet; Herz, Sterne, Video-Symbol, Personen-Plaketten, „fehlt“/„Eingang“
  als Overlay; Auswahlrahmen über dem Bild; Doppelklick/Enter → Lightbox (gleicher Weg wie Kacheln, inkl. Id-Rückfall
  aus 0.2.42), Kontextmenü identisch. Archive sind 4:3-Zellen; Unterordner-Kacheln bleiben ein eigener Zustand.
- **Seitenverhältnis je Datei**: `Files.Width/Height` jetzt im Galerie-DTO (`GalleryFileDto.Width/Height`, über den
  Spaltennamen gelesen); fehlen sie oder sind 0 × 0 → aus dem geladenen Vorschaubild, sonst 4:3. Nachgetragene Maße bzw.
  das erste Vorschaubild ohne Maße lassen die Collage gebündelt neu umbrechen (`CollageLayoutVersion`).
- **Vorschaubilder** (`GalleryViewModel.ThumbnailDecode.cs`): eigener Pfad je Ansicht — die Collage dekodiert auf
  Reihenhöhe × Bildschirm-Skalierung (`ThumbnailDecodeBox.FromCollageRow`; Querformat: kurze Kante, Hochformat: Breite
  aus der Höhe), nie hochskaliert; die Kachel-Konstanten gelten dort nicht. Neu dekodiert wird nur, wenn das gezeigte
  Bild die neue Fläche nicht schon bedeckt (zurück zu Kacheln: nichts neu). `DpiChanged` ohne echte Änderung (neue
  Elemente melden es dutzendfach) wird übergangen.
- **Kopfzeile bei 1280 DIP weiter einzeilig** (ADR-0041): reicht die Sortierleiste mit dem dritten Segment-Eintrag nicht,
  zeigen „Alle auswählen“ und „Rückgängig“ nur ihr Symbol (Tooltip/Automation-Name bleiben, mit Hysterese).
- **Messung**: Umbruch 1000 Items im Median **0,046 ms** (max 0,18 ms; im Panel inkl. Lesen 0,11 ms), 1000 Items in
  900 × 600 realisieren 19 Container. Vorschaubild je Item bei gleichem Regler ≈ 2,1-fach der Kachel (Zelle größer).

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 704 (+37, 1 übersprungen), dreimal hintereinander grün: `Gallery/JustifiedRowLayoutTests`
  (randvoll, höchstens zielhoch, lückenlos in Seitenreihenfolge, Seitenverhältnis in der Zelle, letzte Reihe, Panorama,
  Begrenzung 0,4/6, sehr hohe Bilder, Messung 1000 Items, Seitenverhältnis aus Maßen/Vorschaubild/Archiv),
  `Gallery/JustifiedTilePanelTests` mit der echten Collage-Vorlage (Realisierung 1000 Items, Reihen im Fenster randvoll,
  Container-Zuordnung per Treffertest bei Fokus/Scrollen/Sprüngen/Pfeiltasten in Recycling und Standard, Reload mit
  neuem Seitenverhältnis, Regler hält das oberste Item, Kacheln ↔ Collage dreimal wechseln), `CollageThumbnailDecodeTests`.
  Fixture `CollageGridHost`. `tests/PhotoViewerV3.Tests`: 647 unverändert (1 übersprungen ohne Modell).
- Rauchtest 32/32 OK — neuer Schritt **„Collage-Ansicht“** vor „Ordnerüberwachung“ (`Shell/Smoke/SmokeScenario.Collage.cs`):
  Segment-Reihenfolge, Einstellung gespeichert, Reihen randvoll ±1 px, letzte nicht gestreckt, Zellen im Seitenverhältnis
  der Bildmaße (EXIF-6-Foto hochkant), Dekodierfläche = Reihe × Skalierung, Doppelklick über den echten Handler öffnet
  die getroffene Datei, Screenshots `26-galerie-collage`/`27-galerie-collage-dunkel`, zurück zu Kacheln. Binding-Fehler
  aus der Galerie: 0; in manchen Läufen 2 aus der Personen-Seite (`ImageBrush` mit `{Binding Image}`,
  `PersonsPage.xaml` Zeile 77/104, beim ersten Öffnen der Seite) — unabhängig von dieser Änderung.

### Offen
- Speicher: bei 1000er-Seiten und 150 % ≈ 390 MB Vorschaubilder in der Collage gegen ≈ 180 MB bei Kacheln (gleicher
  Regler). Stellschraube: Zielhöhe 0,75 × Regler (ADR-0044, Alternativen).
- Realisierungslogik liegt in `VirtualizingTilePanel` und `JustifiedTilePanel` doppelt vor (gemeinsame Basisklasse als
  reiner Umbau möglich).
- Personen-Seite: gelegentlich 2 Binding-Fehler der Gesichts-`ImageBrush` beim ersten Öffnen (siehe Tests).

## [0.2.45] — 2026-10-21

### Fix (ADR-0043) — Scharfe Vorschaubilder, Bildmaße im Katalog
- Befund („in der Kachelansicht ist schon alles verschwommen“): Vorschaubilder hatten die LANGE Kante 320 px (Querformat
  320 × 180…240, Qualität 80), die Galerie dekodierte sie fest auf 180 px Breite — eine Kachel (Regler 120–320) bei
  150 % Skalierung zog ein 180 × 120-Bild auf bis zu 480 px hoch. `Files.Width/Height` waren nie gefüllt.
- **Erzeugung**: kurze Kante **400 px**, lange höchstens 1200 px, nie hochskalieren, EXIF-Drehung, Qualität **82**
  (`Services/Thumbnails/ThumbnailSizing`); FFmpeg (Videos, Bild-Fallback) mit demselben Maß im `scale`-Filter. Datei
  `{fileId}_400_still.jpg`. Gemessen an 200 Handyfotos (Spike #11, `docs/spikes/11-vorschaubild-groesse.md`): alt
  14 KB je Bild (1,3 GB je 100 000), Vorgabe 480/Q85 87 KB (**8,3 GB** — über der Grenze von ≈ 6 GB), gewählt 400/Q82
  57 KB (**5,5 GB je 100 000, 27 GB je 500 000**); Erzeugung ≈ 54–61 → 67–82 ms je Bild (+20–30 %, ≈ 28–34 min je
  100 000 mit 4 Workern).
- **Laden in der Galerie**: Dekodierfläche = Bildbereich der Kachel (Regler minus Rand und Namenszeile) ×
  Bildschirm-Skalierung des Hauptfensters (`VisualTreeHelper.GetDpi`, `DpiChanged`), dekodiert so groß, dass der
  Bildbereich bedeckt ist (`UniformToFill` skaliert nie hoch), nie größer als das Vorschaubild; ein verspätet
  dekodiertes Bild alter Größe ersetzt nie ein schon gezeigtes neues. Regler größer bzw. höhere Skalierung: nur Kacheln mit > 15 % zu kleinem
  Bild werden entprellt im Hintergrund neu dekodiert (sichtbare zuerst, gebündelt mit Background-Priorität);
  kleiner ziehen dekodiert nichts neu (`Gallery/ViewModels/GalleryViewModel.ThumbnailDecode.cs`). Listenansicht nutzt
  dieselben Bilder.
- **Übergang**: Vorschaubilder der alten Größe (320) werden gezeigt, bis das neue fertig ist; danach löscht dieselbe
  Transaktion die alte Zeile, die Datei folgt nach dem Commit; scheitert das neue, bleibt das alte. **Pflege im
  Hintergrund** (`ThumbnailService.Maintenance.cs`, 20 s nach dem ersten Request, einmal je Sitzung): ersetzt die übrigen
  in 32er-Stapeln nach FileId, jeden erst bei leerer Warteschlange (angeforderte Kacheln gehen vor); ruht bei Pause
  (Umzug), Scan-Sperre der Bibliothek; überspringt fehlende Originale und Cloud-Platzhalter (kein OneDrive-Download).
  Beim Start wird nur gezählt — die noch nicht eingereihten zählen in der Statusleiste („Vorschaubilder: N in
  Warteschlange“) mit.
- **Bildmaße**: beim Erzeugen schreibt der ThumbnailService die Maße des Originals nach EXIF-Drehung nach
  `Files.Width/Height` (gleiche Transaktion wie „Generated“); Videos aus der FFmpeg-Eingabe-Beschreibung
  (`FfmpegStreamInfo`: Maße, Display-Matrix/`rotate`, HEIF-Kachelgitter; FFmpeg läuft dafür mit `-loglevel info -nostats`).
  Die Pflege trägt für Bilder mit neuem Vorschaubild, aber ohne Maße, die Maße aus dem Dateikopf nach
  (`IImageThumbnailRenderer.ReadDimensions`, `DelayCreation`), 200 je Transaktion; nicht lesbare (z. B. HEIC ohne Codec)
  bekommen 0 × 0 und werden nicht jede Sitzung neu gelesen. Maße werden nur für die gelesene Dateiversion geschrieben
  (`Files.ModifiedAtUtc`). Eingebettete Titelbilder in Videos (`attached pic`) zählen nicht. Nach „Neu aufbauen“ entstehen sie mit
  den Vorschaubildern neu. Keine Migration.
- Dienst: `ThumbnailService` partial (+ optionale `IScanGate`-Abhängigkeit, `MaintenanceStartDelay`,
  `RunMaintenancePassAsync`), `ThumbnailRenderResult.SourceWidth/SourceHeight`; Renderer-Parameter heißt jetzt
  `targetShortEdge`. ADR-0009 (Größen „160 / 320 / 640“) teilweise ersetzt; PLAN Kap. 13 angepasst; README
  (Speicherplatz).

### Tests
- `tests/PhotoViewerV3.Tests`: 647 (+45, 1 übersprungen ohne Modell): `Thumbnails/ThumbnailUpgradeTests` (Maße nach Files,
  alt zeigen → neu → alt gelöscht, Fehlschlag behält alt, andere Dateiversion, Pflege ersetzt/überspringt, Maße aus dem
  Dateikopf, nicht lesbare 0 × 0, Maße nur für die gelesene Version, Scan-Sperre, Pause, lokale Verfügbarkeit),
  `Thumbnails/ThumbnailSizingTests` (kurze Kante/Grenze/kein Hochskalieren, Drehung, FFmpeg-Eingabe parsen inkl.
  Titelbild, Fehlertext ohne Info-Zeilen, `scale`-Ausdruck), `FfmpegFallbackRendererTests` (+2 mit echtem
  FFmpeg: Panorama-Grenze und kleine Bilder, gedrehtes Video meldet 180 × 320).
- `tests/PhotoViewerV3.Wpf.Tests`: 327 (+22, 1 übersprungen), dreimal hintereinander grün: `WicImageThumbnailRendererTests`
  (kurze Kante, Panorama, Originalmaße bei EXIF 6, `ReadDimensions`), `Gallery/GalleryThumbnailDecodeTests`
  (Bildbereich × Skalierung, Dekodierbreite bedeckt ihn, Schwelle fürs Neu-Dekodieren, 150 %-Kachel scharf, alte
  Größe am Dateinamen).
- Perf-Harness `spike11-thumb-size` (`tests/PhotoViewerV3.PerfTests`).
- Rauchtest 29/29 OK, 0 Binding-Fehler — neuer Schritt **„Vorschaubild-Größe und Bildmaße“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.ThumbnailSize.cs`): EXIF-6-Foto 1200 × 600 → Vorschaubild 400 × 800, Files 600 × 1200;
  alle Bilder kurze Kante = min(400, Original), Videos mit Maßen aus FFmpeg, keine alte Größe; Kachelbild bedeckt
  Kachel × Skalierung, nach Regler auf 320 neu dekodiert. Screenshot `25-galerie-vorschaubilder-gross`.

### Offen
- Reglerstufen 300/320 bei 150 % Skalierung: Hochformate werden bis 1,14-fach vergrößert (400 statt 456 px, bewusst
  wegen Speicher).
- Gesichts-Ausschnitte der Personen-Seite sind 128 px groß und werden in Kreisen von 130 DIP gezeigt — bei 150 %
  ebenfalls ≈ 1,5-fach vergrößert (ADR-0037, nicht Teil dieses Fixes).
- Videos, deren Vorschaubild schon in 400 px vorliegt, bekommen Maße erst beim nächsten Erzeugen (kein Nachtragen
  ohne FFmpeg-Lauf).

## [0.2.44] — 2026-10-21

### Neu (ADR-0042) — Schlankere Navigation, Einstellungen mit Reitern, Info-Seite (Mockup 06, Reiter „Navigation & Einstellungen“)
- **Linke Leiste**: oben der **Bibliotheks-Umschalter** (aktive Bibliothek mit Namen; Klick → Menü mit allen Bibliotheken
  samt Dateizahl und „Bibliotheken verwalten…“; ohne Bibliothek führt der Klick direkt zur Einrichtung; während eines
  Scans der Bibliotheksverwaltung wird nicht gewechselt, Fehler kommen als Dialog; neu gelesen wird bei
  Bibliothekswechsel, nach Scans, beim Öffnen des Menüs und beim Verlassen der Einstellungen), darunter
  **Galerie, Ordner, Personen, Sortieren**, unten **Einstellungen**. Die Seiten **Bibliothek, Tags und Kategorien** sind
  aus der Leiste verschwunden — sie sind jetzt Reiter der Einstellungen. Vergeben von Tags und Kategorien bleibt
  unverändert in der Galerie (Rechtsklick, Strg+T, Strg+K, Lightbox).
- **Einstellungen mit Reitern oben** (Pivot im WPF-UI-Stil, gewählter Reiter fett mit Akzent-Strich): **Bibliotheken &
  Eingänge** (die bisherige Bibliotheksseite unverändert: Neue Bibliothek, Aktiv setzen, Aktualisieren, Neu aufbauen,
  Eingang hinzufügen, In die Bibliothek übernehmen …; darunter die **Ordnerüberwachung**), **Tags**, **Kategorien** (die
  bisherigen Seiten), **Gesichter** (Gesichtserkennung, Mindestgröße, Modell, Index neu aufbauen, Gesichtsdaten löschen),
  **Archive** (automatisch entpacken), **Darstellung** (Farbschema), **Speicher & Wartung** (Speicherorte samt Katalog-
  Umzug, Vorschaubild-Cache, FFmpeg, unterstützte Formate) und zuletzt **Info**. Kein bisheriger Abschnitt ging verloren;
  die alte Karte „Über“ ist in „Info“ aufgegangen.
- **Navigation von außen auf einen Reiter**: `NavigationTags.Library/Tags/Categories` und neue Reiter-Tags
  (`settings-faces`, `-archives`, `-appearance`, `-storage`, `-info`) öffnen über den `INavigationService` die
  Einstellungen auf dem Reiter (`SettingsSectionState`) — „Tags verwalten…“/„Kategorien verwalten…“ aus der Galerie
  funktionieren unverändert, „Einstellungen öffnen“ der Personen-Seite springt jetzt auf „Gesichter“.
- **Start**: mit Bibliothek wie bisher die Galerie; **ohne Bibliothek (Ersteinrichtung) direkt Einstellungen ›
  Bibliotheken & Eingänge**.
- **„Eingang hinzufügen…“ zusätzlich auf der Seite „Ordner“** (Kopf des Ordnerbaums): gleicher Ablauf wie in den
  Einstellungen für die aktive Bibliothek (Ordnerauswahl, Prüfung, Angebot „Jetzt aktualisieren“); Fehler beim
  Hinzufügen bzw. beim anschließenden Aktualisieren, ein laufender Scan oder eine fehlende Bibliothek als Dialog. Ein
  von dort gestarteter Scan bleibt auf der Seite „Ordner“ statt zur Galerie zu springen.
- **Info**: App-Name, **Version = oberster CHANGELOG-Eintrag** (las `PhotoViewerV3.Wpf.csproj` schon bisher; jetzt mit
  Test), Build-Datum, Debug/Release, Kurzbeschreibung; Karten **Entwicklung** (Programmierung: Herbert Schrotter;
  Mitarbeit: Claude (Anthropic), KI-Assistent; Vorgänger: PhotoViewer V2 — keine E-Mail-Adresse), **Dieses System**
  (.NET, Windows-Build, Architektur, Grafikkarte über `EnumDisplayDevices` mit Ausführung der Gesichtserkennung
  DirectML/CPU, Gesichtsmodell vorhanden/geladen, Katalog: Bibliotheken · Dateien · DB-Größe), **Speicherorte**
  (Katalog — markiert die Datei, fehlt sie noch, nur den Ordner —, Vorschaubilder, Protokolle, je „öffnen“ im Explorer) und **Verwendete Bausteine** (WPF-UI,
  CommunityToolkit.Mvvm, SQLite/EF Core, SharpCompress, ONNX Runtime DirectML, LibVLCSharp — Versionen aus den DLLs
  neben der App; InsightFace buffalo_l „nur nicht-kommerziell“) mit **„Alle Lizenzen anzeigen…“** (fester Text im
  App-Dialog, kein Netzzugriff). Knöpfe **„Versionsinfo kopieren“** (Version, Build, System, Bausteine — ohne Pfade und
  Bibliotheksnamen) und **„Änderungsprotokoll“** (öffnet die CHANGELOG.md neben der App; ohne sie ausgeblendet).
  `CHANGELOG.md` wird dafür ins Ausgabeverzeichnis und ins portable Paket kopiert.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 335 (+30, 1 übersprungen ohne Modell). Neu `Shell/NavigationAndSettingsTabsTests`
  (Reiter-Tags und Reihenfolge, Zustand lehnt Unbekanntes ab, Reiter-Tag öffnet Einstellungen auf dem Reiter auch vor
  dem Anbinden, Umschalter mit/ohne Bibliothek, Wechsel, „verwalten“, kein Wechsel während eines Scans, Fehler als
  Dialog, Neulesen beim Verlassen der Einstellungen, zusammengefasstes Neulesen, „Eingang hinzufügen…“ für die aktive
  Bibliothek bzw. abgebrochen/abgelehnt/ohne Bibliothek, Knopf auf der Ordner-Seite) und `Settings/InfoViewModelTests`
  (Version = oberster Eintrag der Repo-CHANGELOG, CHANGELOG neben der App, Lesen der obersten Version, Windows-/
  Kopfzeile, Karten, Grafikkarte/Gesichtsmodell-Texte, Bausteine mit Versionen, Versionsinfo ohne Pfade, Lizenzen,
  Knopf „Änderungsprotokoll“ nur mit Datei); `PersonsViewModelTests` erwartet den Reiter „Gesichter“.
  `tests/PhotoViewerV3.Tests` unverändert 602 (1 übersprungen).
- Rauchtest: Schritte über Bibliothek/Tags/Kategorien öffnen die Einstellungs-Reiter (`OpenSettingsSectionAsync`); der
  Startschritt prüft die Ersteinrichtung (Einstellungen › Bibliotheken & Eingänge, Umschalter „Bibliothek einrichten“);
  neuer Schritt **„Einstellungen-Reiter und Info“** vor der Ordnerüberwachung (Leiste ohne Bibliothek/Tags/Kategorien,
  Umschalter, alle Reiter öffnen, Info-Version = oberster CHANGELOG-Eintrag, „Tags verwalten…“ von außen, „Eingang
  hinzufügen…“ auf der Ordner-Seite, Screenshots hell/dunkel).

## [0.2.43] — 2026-10-21

### Neu (ADR-0041) — Galerie-Kopfzeile: Suche im Titel, Filter frei kombinierbar, Filter zurücksetzen
Mockup 06 („Galerie-Kopfzeile“, `mockups/06-galerie-kopfzeile.html`) von Herbert abgenommen am 2026-10-21.
- **Filter unabhängig kombinierbar**: Dateiart (Alle/Bilder/Videos/Archive), **Nur Favoriten**, **Ablage**
  (Überall/Noch im Eingang/In der Bibliothek) und neu **Bewertung** (egal/ab 1–4 Sternen/nur 5 Sterne) sind eigene
  Filter statt eines Einzel-Enums; zusammen mit Tags, Personen, Kategorien, Suche und Ordner gelten alle mit **UND** —
  z. B. „nur Bilder UND Favoriten UND Tag „Urlaub“ UND ohne benannte Person UND noch im Eingang“. Modell
  `GalleryFilterState` ersetzt `GalleryFilter` (`Gallery/Data/GalleryFilterState.cs`, SQL in
  `GalleryPageSource.FileFilter.cs`); die bisherigen Einzel-Filter erzeugen wortgleiches SQL (gleiche Abfragepläne).
- **Kopfzeile neu** (`Gallery/Controls/GalleryToolbar.xaml`): Titel „Galerie“ bzw. „Ordner“ mit Trefferzeile „342 von
  1.248 Dateien · Ordner: …“ (Ordner-Chip mit ✕ bleibt dort), Suchfeld rechtsbündig („Suchen: Name, Tag, Person oder
  Kategorie“, **Strg+F** fokussiert, Enter sucht sofort, sonst nach 450 ms Tipp-Pause, Esc leert; der Knopf „Suchen“
  entfällt). **Filterkarte** mit Zeile „Datei“ (Segment, Chips „Nur Favoriten“, „Bewertung“, „Ablage“, rechts **Filter
  zurücksetzen** mit Zähler, ohne Filter deaktiviert) und Zeile „Inhalt“ (Tags, Personen, Kategorien als Chips, aktiv in
  Akzentfarbe mit Wert). Fußzeile nur bei aktiven Filtern: **„Es gilt: Bilder UND Favoriten UND Tag „Urlaub“ … →
  342 Treffer“**, ✕ entfernt genau diese Bedingung. „Filter zurücksetzen“ hebt alle Filter und die Suche auf, die
  Ordnerauswahl bleibt.
- **Flyouts mit verständlichen Texten**: Tags „Mindestens eines der gewählten / Alle gewählten zusammen / Ohne Tag“,
  Personen „Eine der gewählten Personen / Alle gewählten Personen zusammen / Keine benannte Person / Unbenannte
  Gesichter / Irgendein Gesicht“, „Auch Vorschläge mitzählen“; Suchfeld über die Tag- bzw. Personenliste; „Leeren“ /
  „Fertig“. Alle Funktionen der bisherigen Dropdowns bleiben (Zähler, ausgeblendete Personen, „ohne Wert“ je Kategorie).
- **Sortier- und Ansichtsleiste**: „Sortieren [Feld ▾] [↓ Neueste zuerst]“ — Feld (Änderungsdatum, Name, Größe, jede
  Kategorie) und Richtung getrennt; Kategorie-Sortierung per Spaltenkopf bleibt und erscheint als Feld. „Ansicht
  [Kacheln | Liste]“ als Segment aus einer Liste (`ViewModeOptions` — „Collage“ in Welle 2 ist ein weiterer Eintrag),
  Vorschaugrößen-Regler, rechts Alle auswählen, Rückgängig, Aktualisieren.
- **Blättern und „Pro Seite“ unter der Galerie**: „‹ Seite 1 von 7 › · Pro Seite [200 ▾]“ (erste/letzte Seite bleiben).
- Bei 1280 DIP Fensterbreite einzeilig: Titelzeile, Zeile „Datei“ und Sortierleiste — auch mit allen Datei-Filtern
  aktiv (Chips zeigen dafür Kurzwerte „Ablage: Eingang“, „Bewertung: ab 3 ★“); schmaler brechen sie sauber um.
- Stile der Chips, Segmente und Flyouts zentral in `Gallery/Controls/GalleryFilterStyles.xaml` (nur Theme-Ressourcen,
  hell und dunkel). Galerie-Kacheln, Tastenkürzel und Automation-Namen unverändert; neu `GalleryResetFilters`,
  `GalleryFavoritesFilter`, `GalleryRatingFilterButton`, `GallerySortFieldCombo`, `GallerySortDirectionButton`,
  `GalleryViewModeSegment` (der alte `GallerySortCombo` entfällt).

### Messung (500 000 Dateien, `GallerySearchSortMeasurementTests`, Median aus 3, warmer Cache)
- Bilder: Zählen 21 ms, Seite 1 7 ms · Favoriten: 4 / 56 ms · Bilder + Favoriten + ab 3 Sternen (8 334 Treffer):
  144 / 231 ms · Bilder + ab 3 Sternen + Tag „Familie“ (41 666): 233 / 112 ms (Tag-Suche „familie“ zum Vergleich
  167 / 188 ms). Bewertung ohne eigenen Index (keine Migration) — siehe ADR-0041.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: `Gallery/GalleryFilterCombinationTests` (96 Kombinationen aus Dateiart × Favoriten ×
  Ablage × Bewertung gegen eine echte DB, jeweils auch mit Tag-Filter, „ohne Tag“, Suche und Ordnerfilter; Abfrageplan
  ohne Vollscan über Files in jeder Kombination; Einzel-Filter wortgleich wie vorher; Bezugsgröße der Trefferzeile;
  Leertexte), `GalleryToolbarLayoutTests` neu für die Kopfzeile (1280/1920/2560 einzeilig, 1280 mit allen Filtern hell
  und dunkel, 1024 und Ordner-Seite brechen ohne Überlauf um). Alle `GalleryFilter`-Aufrufe in Tests umgestellt.
- Rauchtest: Filter-Schritte auf `FilterState` umgestellt; neuer Schritt **„Kombinierte Filter“** vor der
  Ordnerüberwachung (Bilder + Favoriten + Tag + ab 3 Sternen gegen die DB-Zahl, „Es gilt“-Zeile, ✕ an „ab 3 Sternen“,
  Suche als Bedingung, Filter zurücksetzen mit erhaltener Ordnerauswahl, Gesamtzahl wieder da, Strg+F gebunden).

## [0.2.42] — 2026-10-21

### Behoben — Doppelklick auf eine Kachel öffnete manchmal ein anderes Foto
- **Fehlerbild:** Eine Kachel zeigte Datei A (Name und Vorschaubild passend zueinander), der Doppelklick öffnete die
  Lightbox mit Datei B an deren Position („73 / 256“ statt der angezeigten Kachel). Nur manche Kacheln, Galerie und
  Ordner-Seite (gleiche `GalleryView`), ebenso die Kachel-Listen der Personen-Seite (gleiches Panel).
- **Ursache:** `VirtualizingTilePanel` (Recycling-Modus) bereitete wiederverwendete Container nicht vor. Der
  `ItemContainerGenerator` liefert einen Container aus der Recycling-Warteschlange mit `newlyRealized == false` und setzt
  dabei nur `DataContext` auf das neue Item; das Panel rief `PrepareItemContainer` nur bei `newlyRealized == true` auf.
  `ListBoxItem.Content` blieb deshalb `DisconnectedItem`, und die Bindungen der Kachelvorlage behielten den Stand des
  Items, das der Container vorher zeigte — der Doppelklick-Handler liest aber `ListBoxItem.DataContext` (das neue Item).
  Sichtbar wurde es erst nach Scrollen über die Pufferzeile hinaus (dann erst recycelt das Panel); der vermutete
  Reihenfolge-Fehler beim behaltenen Fokus-Container trat in den Tests nicht auf.
- **Fix im Panel:** Ein vom Generator gelieferter Container, der noch nicht unter den Kindern des Panels liegt, kommt aus
  der Recycling-Warteschlange und wird jetzt vorbereitet (`PrepareItemContainer` → `Content`, Vorlage, Auswahl).
  Zusätzlich ordnen `ArrangeOverride`, `CleanUpOutside` und `MakeVisible` über den echten Item-Index des Containers an
  (`ItemContainerGenerator.IndexFromContainer` bzw. dessen Generator-Position) statt über die Position unter den
  Kindern: ein behaltener Fokus-Container liegt so immer an seinem eigenen Platz (außerhalb des Viewports), Recycle/Remove
  trifft immer genau den entfernten Container. `ReplaceChildRange` bereitete schon immer vor; `PlaceChild` unverändert.
- **Verteidigung in `GalleryViewModel.OpenInLightbox`** (neue partial `GalleryViewModel.Lightbox.cs`,
  `BuildLightboxSequence`): Start erst per Referenz, dann per FileId in der Page; steht das Item gar nicht in der Page,
  öffnet die Lightbox nur dieses eine Item (Warnung im Log) statt still bei Index 0 zu beginnen.

### Tests
- `TileContainerMappingTests` (WPF, 256 Items, 600 × 420, je Standard- und Recycling-Modus): für jeden sichtbaren
  Platz per Treffertest den Container holen und prüfen — `DataContext`, `Content`, `ContentPresenter.Content`,
  Vorlagen-DataContext und sichtbarer Dateiname = Item des Platzes. Szenarien: Item 72 fokussiert, in Mausrad-Rastungen
  (mit Hin-und-her) nach oben, Sprünge; Fokus per Pfeiltasten; Lightbox-Fenster öffnen/schließen; Seiten-Reload mit
  ersetzten/verschobenen/entfernten Items; reines Hin-und-her-Scrollen. Mit dem alten Panel scheiterten alle
  Recycling-Fälle (z. B. Platz 66 zeigte „IMG_0011“, DataContext = Item 66).
- `GalleryLightboxSequenceTests`: Start per Referenz, per FileId (andere Instanz), Item außerhalb der Page öffnet allein.
- Rauchtest „Lightbox Bild + Video“ um einen Block erweitert: große Kacheln, letzte Kachel fokussieren und öffnen,
  schließen, in Mausrad-Rastungen nach oben, jede sichtbare Kachel per Treffertest (angezeigter Name = DataContext =
  Item des Platzes), eine Kachel per Container-DataContext „doppelklicken“ → Lightbox zeigt dieselbe Datei. Mit dem alten
  Panel schlug der Block fehl (6 von 6 Kacheln zeigten eine andere Datei).

## [0.2.41] — 2026-10-21

### Tests
- Zwei unter Last wackelnde Gesichter-Tests stabilisiert (Befund aus 0.2.39/0.2.40):
  - `FaceCropServiceTests.Fordert_an_rendert_je_Datei_einmal_…`: der entprellte Abgleich nach `FacesChangedMessage` (50 ms)
    konnte erst nach dem Löschen des Gesichts laufen und den verwaisten Ausschnitt selbst entfernen — `SweepNow` fand dann 0.
    Geprüft wird jetzt, dass der Ausschnitt danach weg ist (`SweepNow` 0 oder 1).
  - `FaceRecognitionServiceTests.Gruppen_nur_aus_unbekannten_Gesichtern_…`: wartet nach dem Bestätigen von „Bert“ auf
    `WaitForIdleAsync`, bevor neue Gesichter entstehen — sonst bekamen sie vom verspäteten Vorschlagsdurchlauf schon einen
    Vorschlag und fehlten in den unbenannten Gruppen.
- Zusammenführung 0.2.39 + 0.2.40: Services 602 (8× volle Suite grün), WPF 305 (3×), beide Klassen 15× grün, Rauchtest 28/28.

## [0.2.40] — 2026-10-21

### Neu (ADR-0040) — Ignorierte Gesichter ansehen und zurückholen, ganze Gruppe ignorieren, Mindestgröße
- **Personen → „Ignorierte Gesichter (N)“** (Knopf in der Übersicht): alle ignorierten Gesichter der aktiven Bibliothek,
  neueste Fotos zuerst, seitenweise je 500 nachgeladen, Kacheln virtualisiert mit Ausschnitten wie die übrigen;
  automatisch ignorierte mit **„zu klein“** und Größe in Pixeln markiert. Auswahl → **Zurückholen** (Zustand 3 → 0,
  danach Neu-Lernen und Vorschläge, Gruppen neu), Doppelklick → Lightbox. Zeile „Zeigt 1–21 von 30 …“ über der Liste.
- **Ganze Gruppe ignorieren…** in der Gruppenansicht und per Rechtsklick auf eine Gruppenkachel: alle Mitglieder
  inklusive der abgewählten Ausreißer (Rückfrage mit Anzahl), nur noch offene (ein inzwischen bestätigtes bleibt); die
  Gruppe verschwindet.
- **Einstellungen → Gesichtserkennung → „Kleine Gesichter automatisch ignorieren“** (Aus, 16 … 64 Pixel, **Standard
  kleiner als 24 Pixel** — Spike 09: p10 ≈ 25 px, p50 ≈ 62 px bei 1280er-Kante; unter 24 px trägt die Größe nichts mehr
  zur Qualität bei). Automatisch ignorierte bleiben Zustand 3 (wirken auf Lernen, Gruppen, Vorschläge, Galerie wie
  manuell ignorierte) und tragen die Markierung `SuggestionScore = -1` — **keine Migration, kein neuer State-Wert**.
  Neue Gesichter ignoriert der Index gleich beim Einfügen (`FaceIndexStore`, gleiche Transaktion); auf den Bestand wendet
  der Zuordnungsdienst die Schwelle im Hintergrund einmal je Änderung an (beim Start, wenn sie von der zuletzt
  angewandten abweicht — beim ersten Start nach dem Update also einmal mit 24 px): offene unter der Schwelle → ignoriert,
  nicht mehr betroffene automatische → zurück. Bestätigte und manuell ignorierte fasst die Regel nie an.
- Dienst: neues Interface `IFaceIgnoreService` (`GetIgnoredFacesAsync`, `UnignoreFacesAsync`, `IgnoreGroupAsync`,
  `Get/SetMinFaceSizeAsync`), implementiert vom `FaceRecognitionService` (partial `FaceRecognitionService.Ignore.cs`),
  registriert in `AddFaceRecognition()`; `IFaceRecognitionService` und seine Fakes bleiben unverändert. Regel und
  Markierung in `Services/Faces/FaceAutoIgnore`.
- Galerie-Abfragen (`Wpf/Gallery/Data/**`) gelesen: sie prüfen nur `State` 0/1/2 — automatisch ignorierte (Zustand 3)
  fallen dort wie manuell ignorierte heraus, keine Anpassung nötig.

### Fix — Vorschläge einer Person „nicht alle zu sehen“ (Herberts Meldung)
- Ursache: Die Liste lädt und zeigt alle Vorschläge (nachgestellt im Test: echte Seite in der WPF-UI-NavigationView,
  60 Vorschläge im kleinen Fenster — alle in der Liste, Liste scrollbar), aber unter Kopf, Meldungen, Reitern und
  Knopfleiste bleiben bei 1280 × 800 nur etwa zwei Kachelreihen (≈ 14 Gesichter); die schmale Bildlaufleiste von WPF-UI
  blendet sich aus, nichts wies auf weitere hin. **„Alle sichtbaren bestätigen“ bestätigte nur diese ≈ 14** — danach
  rückten die übrigen nach, was wie „jetzt sind sie da“ aussah. Zusätzlich forderte ein Reiterwechsel keine Ausschnitte
  an, wenn das Panel denselben Bereich wie zuvor meldete (kein `VisibleRangeChanged`) — leere Kacheln möglich.
- Neu: **„Alle Vorschläge bestätigen…“ / „Alle Vorschläge ablehnen…“** (ganze Liste, Rückfrage mit Anzahl, beim
  Bestätigen danach die Ausreißer-Rückfrage wie beim Benennen); die bisherigen Knöpfe heißen **„Sichtbare
  bestätigen“/„Sichtbare ablehnen“**; Zeile **„Zeigt 1–14 von 30 Vorschlägen — nach unten scrollen für weitere.“** über
  der Liste; Reiterwechsel fordert die Ausschnitte des sichtbaren Reiters an.

### Tests
- `tests/PhotoViewerV3.Tests`: 602 (+7, 1 übersprungen ohne Modell): `Faces/Assignment/FaceIgnoreServiceTests`
  (Zurückholen → Vorschläge, ganze Gruppe inkl. Ausreißer und Gruppe weg, inzwischen bestätigte bleiben,
  Mindestgröße anwenden/senken/ausschalten mit bestätigten und manuell ignorierten unverändert, Standard einmal beim
  Start und zurückgeholte bleiben beim nächsten Start, seitenweises Lesen, Index fügt zu kleine ignoriert ein). Fake-
  Analysator in `FaceFakes` liefert jetzt 26-px-Gesichter (über dem Standard).
- `tests/PhotoViewerV3.Wpf.Tests`: 293 (+8, 1 übersprungen), dreimal hintereinander grün:
  `Persons/PersonsIgnoredViewModelTests` (Ansicht, „zu klein“, Zurückholen, Nachladen, ganze Gruppe per Knopf und
  Kontextmenü mit Rückfrage, alle Vorschläge bestätigen/ablehnen vs. sichtbare, Einstellung Mindestgröße),
  `Persons/PersonsSuggestionsListUiTests` (Reproduktion in der NavigationView: 60 Vorschläge im kleinen Fenster,
  Scrollen und Reiterwechsel fordern Ausschnitte an; 2 000 ignorierte virtualisiert als Blatt-Kacheln mit Nachladen).
- Rauchtest 28/28 OK, 0 Binding-Fehler — Block **„Ignorierte Gesichter“** im Schritt „Personen“
  (`Shell/Smoke/SmokeScenario.PersonsIgnored.cs`): manuell ignorieren → Ansicht, Mindestgröße 400 px → alle offenen
  automatisch ignoriert (bestätigte/manuelles bleiben), Schwelle zurück → zurückgeholt, „Zurückholen“ über die Ansicht.
  Screenshot `23b-personen-ignorierte`.

### Offen
- Laufzeit der Anwendung auf den Bestand bei 500 000 Gesichtern nicht gemessen (eine Transaktion über `IX_Faces_State`).
- Gruppenkacheln: zwei WPF-Binding-Fehler (`ImageBrush.ImageSource`, „Cannot find governing FrameworkElement“) traten
  im Rauchtest auf, solange zwischen Personen- und Gruppenliste ein zusätzliches `await` lag — zeitabhängig, mit der
  jetzigen Reihenfolge 0; die Vorlage (`ImageBrush` in `Border.Background`) bleibt empfindlich.

## [0.2.39] — 2026-10-21

### Neu — Gesichter sichtbar machen (Galerie und Lightbox)
- **Personen-Filter „alle Fotos mit Gesichtern“** (neuer `GalleryPersonMode.AnyFace`): Fotos mit mindestens einem
  erkannten Gesicht im Zustand unbestätigt, Vorschlag oder bestätigt — ignorierte zählen nicht, gewählte Personen und
  „auch Vorschläge“ spielen keine Rolle. Je Datei eine abdeckende Index-Suche in `IX_Faces_File_State_Person`
  (`GalleryPageSource.FaceMarks.cs`, EXPLAIN-QUERY-PLAN-Test). „Nur Fotos mit unbenannten Gesichtern“ zählt ignorierte
  weiterhin nicht (Test).
- **Kachel-Markierung für unbenannte Gesichter**: neben dem Personen-Symbol ein graues Gesichts-Symbol
  (`PersonQuestionMark16`) mit der Zahl der unbenannten Gesichter (unbestätigt, Vorschlag oder bestätigt ohne Person —
  wie der Filter), Tooltip „2 Gesichter noch nicht benannt“ plus Vorschläge. Nur ignorierte Gesichter → kein Symbol.
  Geladen in derselben Abfrage je Seite wie die Personen-Namen (`GetFaceMarksForFilesAsync`, 500 Dateien je Abfrage;
  `GetPersonsForFilesAsync` liefert unverändert die Namen). Die Kachel bleibt Automation-Blatt; ihr HelpText nennt
  Personen und unbenannte Gesichter.
- **Lightbox: Taste F gemerkt** — Settings-Schlüssel `ui.lightbox.showFaces` (über `IGalleryViewSettingsStore`), jede
  Taste F schreibt ihn, ein neues Lightbox-Fenster stellt ihn wieder her (`LightboxFacesViewModel.RestoreAsync`; drückt
  der Benutzer F vorher, gilt seine Taste). Beim Blättern bleiben die Rahmen an; ignorierte Gesichter zeigen keinen Rahmen.
- Tests: `GalleryFaceMarksTests` (migrierte DB mit Gesichtern aller vier Zustände: Filter, Zählung, Index-Plan,
  Kachel-Daten, Automation-Text), `LightboxFacesPersistenceTests` (Speichern, Wiederherstellen, Blättern, Reihenfolge,
  echte Settings-Tabelle). Rauchtest „Personen in Galerie/Lightbox“ prüft zusätzlich die Kachel-Markierung, den neuen
  Filter (Erwartung aus dem DB-Stand) und die gemerkte Taste F beim erneuten Öffnen.

## [0.2.38] — 2026-10-21

### Zusammenführung mit 0.2.37
- Provisorischer Vertrag `_P2Contract.cs`, Ersatzdienst `InterimFaceRecognitionService`, dessen Registrierung
  `AddLightboxFaces()` und die zugehörige Testklasse entfernt — die Lightbox nutzt den echten Zuordnungsdienst (ADR-0037).
  `FaceRecognitionForwarder` bleibt nur für Test-Fakes. `InvalidateAll()`/`IFaceCropService.DeleteAll()` passen ohne
  Anpassung. README: Abschnitt „Gesichtserkennung“ verweist auf „Personen“ und „Personen in Galerie und Lightbox“.
- Rauchtest „Personen in Galerie/Lightbox“: wartet vor den per SQL vorgeschriebenen Zuordnungen, bis der Zuordnungsdienst
  ruht (er rechnete nach dem Schritt „Personen“ noch Vorschläge nach und überschrieb Berts Vorschlag), berechnet „nur Fotos
  ohne Person“ aus dem DB-Stand und setzt den Personen-Filter auch nach einem Fehlschlag zurück (sonst scheiterte die
  Ordnerüberwachung mit). Rauchtest 28/28 (2×), Services 594, WPF 284 (3×), Gesichter mit echtem Modell 96/96.

### Neu (ADR-0038) — Gesichtserkennung Teil 4: Personen in Galerie, Suche, Liste und Lightbox
- **Galerie-Filter „Personen“** (Kopfzeile neben „Tags“, sichtbar sobald Personen oder Gesichter da sind): Häkchen je
  Person mit Anzahl Fotos der aktiven Bibliothek, **ODER**/**UND**, **nur Fotos ohne Person**, **nur Fotos mit
  unbenannten Gesichtern**, Option **auch Vorschläge** (Standard aus). Als Person zählt nur ein bestätigtes Gesicht
  (`Faces.State = 2`); ausgeblendete Personen stehen nicht in der Liste. Direkt in SQL (`GalleryPageSource.Persons.cs`),
  wirkt in Zählung, Seite und Kategorie-Sortierung.
- **Suche** findet Personennamen (Teilstring, ohne Groß-/Kleinschreibung) als weiterer UNION-Zweig wie Tags/Kategorien;
  Platzhalter „Name, Tag oder Person…“.
- **Listenansicht**: Spalte **Personen** (bestätigte Namen; Vorschläge kursiv/grau mit „?“), sortierbar innerhalb der
  Seite (bestätigt → nur Vorschläge → leer). Gespeicherte Spaltenauswahl bleibt; einblenden über die Kopfzeile.
- **Kachel**: Personen-Symbol mit Anzahl bestätigter Personen, Tooltip mit Namen (Kacheln bleiben Automation-Blätter).
- **Lightbox**: Taste **F** bzw. Knopf „Gesichter“ zeigt Rahmen über den Gesichtern (grün Name, gelb „Vorschlag: X?“,
  weiß „Unbekannt“), die mit Zoom/Schwenk mitwandern (`ZoomPanControl.Overlay` + `IZoomPanOverlay`, `FaceBoxesLayer`).
  Klick → Menü **Ist X** / **Nicht X** / **Ist …** (Person wählen oder Namen eingeben; vorhandener Name wird zugeordnet,
  sonst neue Person) / **Kein Gesicht**; danach Rahmen neu (auch auf `PersonsChangedMessage`/`FacesChangedMessage`).
  Logik in `LightboxFacesViewModel` über `IFaceRecognitionService`. **Vollbild nur noch F11** (F war Vollbild).
- **Einstellungen → Gesichtserkennung → „Gesichtsdaten löschen…“** (`IFaceDataEraser`, Bestätigung im AppDialogWindow):
  schaltet den Index aus und löscht für die aktive Bibliothek in einer Transaktion Gesichter samt Fingerabdrücken,
  FaceScans, Ablehnungen, den Gesichter-Cache `thumbs\{id}\faces\` und Personen, die danach nirgends mehr vorkommen
  (Personen ohne Gesicht in dieser Bibliothek bleiben); Nachkontrolle nach 1,5 s gegen einen späten Schreib-Stapel des
  Index. Fotos, Bewertungen, Tags, Kategorien bleiben. Hinweis „Alles bleibt auf diesem PC“.
- **Migration `AddFacePersonIndex`**: abdeckende Indizes `IX_Faces_Person_State_File`, `IX_Faces_SuggestedPerson_State_File`,
  `IX_Faces_File_State_Person` (State/PersonId liegen hinter dem 2-KB-Fingerabdruck). Bestehende Daten bleiben.
- Nach dem Löschen werden `IFaceRecognitionService.InvalidateAll()` und `IFaceCropService.DeleteAll()` (P1, optional
  injiziert) aufgerufen.
- **Übergang bis zum Merge mit P1**: provisorische Vertragstypen `Services/Faces/Assignment/_P2Contract.cs`
  (`IFaceRecognitionService` inkl. `InvalidateAll`, `FacePersonInfo`, `PersonSummary`, `PersonsChangedMessage`,
  `Services.Faces.Crops.IFaceCropService`) — die Integration löscht nur diese Datei. Der Ersatzdienst
  `Wpf/Lightbox/Faces/InterimFaceRecognitionService` implementiert das Interface nicht selbst, sondern wird über
  `FaceRecognitionForwarder` (DispatchProxy) angeboten und nur per `TryAdd` registriert (`AddLightboxFaces()`); er
  kompiliert damit auch gegen P1s vollständiges Interface, P1s Registrierung gewinnt. Ebenso die Test-Fakes.

### Messung (200 000 Dateien, 98 000 Gesichter, 200 Personen, ohne ANALYZE)
- Zählen: ODER 22 ms, ODER + Vorschläge 21 ms, UND 18 ms, ohne Person 72 ms (182 000 Treffer), unbenannte Gesichter
  59 ms, Suche nach Personennamen 8 ms; Namen für 500 Dateien 2–11 ms; Personen-Liste mit Zählern 93 ms. Alle Pläne über
  abdeckende Indizes (`SEARCH … USING COVERING INDEX`, kein `SCAN` über Faces).

### Tests
- `tests/PhotoViewerV3.Tests`: 580 (+2, 1 übersprungen ohne Modell): `Faces/FaceDataEraserTests` (nur die Bibliothek,
  Personen-Regeln, Titelbild, Cache, Index aus, Nachrichten; leere Bibliothek).
- `tests/PhotoViewerV3.Wpf.Tests`: 274 (+25, 1 übersprungen), dreimal hintereinander grün: `Persons/GalleryPageSourcePersonTests`
  (ODER/UND/ohne Person/unbenannt mit und ohne Vorschläge, Suche, Kategorie-Sortierung, Namen je Datei, Zähler,
  EXPLAIN QUERY PLAN), `Persons/LightboxFacesViewModelTests` + `FaceBoxesLayerTests` (Rahmen, Beschriftung, Menü
  bestätigen/ablehnen/ignorieren/neue Person, Nachrichten, Rahmen folgen dem Zoom), `Persons/GalleryPersonsColumnTests`,
  `Persons/FaceDataSettingsTests` (Bestätigung, Text, Fehler) und `InterimFaceRecognitionServiceTests`; Fake
  `FakeFaceRecognitionService`.
- Rauchtest 27/27 OK, 0 WPF-Binding-Fehler — neuer Schritt **„Personen in Galerie/Lightbox“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.PersonsGallery.cs`): Personen/Zuordnungen per SQL auf die Gesichter des Smoke-Fake-Analysators,
  Filter, Suche, Listenspalte, Kachel-Daten, Lightbox-Rahmen mit „Ist X“ über den registrierten Zuordnungsdienst,
  „Gesichtsdaten löschen“. Screenshots `22-galerie-personen-filter` … `25-einstellungen-gesichtsdaten-geloescht`.

### Offen
- Ort des Gesichter-Caches (`FaceDataCache.FolderName = "faces"`) mit P1 abstimmen.
- „Nur Fotos ohne Person“/„unbenannte Gesichter“ prüfen je Datei (bei 500 000 Fotos ≈ 0,2 s Zählung).
- Ordnerbaum-Kontextmenü „… für alle Dateien“: geprüft, keine Kollision; Personen dort bewusst nicht ergänzt.

## [0.2.37] — 2026-10-21

### Neu (ADR-0037) — Gesichtserkennung Teil 3: Personen-Seite, Benennen, Vorschläge bestätigen
- **Zuordnungsdienst** `IFaceRecognitionService` (`Services/Faces/Assignment`, `AddFaceRecognition()` registriert ihn mit
  `FaceRecognizer`/`FaceClusterer` als Singletons): Modell im Speicher je aktiver Bibliothek, gelernt nur aus
  bestätigten Gesichtern (häufigste Modell-Kennung, Ablehnungen aus `FaceRejections`); nach jeder Bestätigung/Korrektur
  entprellt neu gelernt und Vorschläge für alle offenen Gesichter geschrieben (Zustand 1 mit Person/Score bzw. zurück
  auf 0), nach jedem Index-Stapel (`FacesChangedMessage`) nur für dessen Dateien — nie auf dem UI-Thread, stapelweise,
  per bedingtem UPDATE (eine Bestätigung zwischen Lesen und Schreiben gewinnt immer), Warte-Logik ADR-0030.
  Operationen: Person anlegen/umbenennen (Name ohne Groß-/Kleinschreibung eindeutig)/zusammenführen/ausblenden/löschen,
  bestätigen (einzeln/mehrere), Vorschlag ablehnen (sofort der nächstbeste Kandidat), aus Person entfernen (+ Ablehnung),
  ignorieren, Titelbild; Ausreißer-Prüfung vor dem Bestätigen (`CheckOutliersAsync`, bei neuer Person: ähnelt einer
  bekannten). Gruppen-Vorschläge über unbekannte Gesichter (abschnittsweise je 50 000), zwischengespeichert, eigene
  Zuordnungen nehmen Gesichter sofort heraus. Nachricht `PersonsChangedMessage`. Vereinbarte Schnittstelle für Galerie/
  Lightbox: `GetFacesForFilesAsync`, `GetPersonsAsync`, `ConfirmAsync`, `ConfirmNewPersonAsync`, `RejectSuggestionAsync`,
  `IgnoreFaceAsync`.
- **Gesichts-Ausschnitte** 128 × 128 im Thumbnail-Ordner (`thumbs\{libraryId}\faces\{faceId}.jpg`, `IFaceCropService`),
  gerendert über WIC im Wpf-Projekt (`WicFaceCropRenderer`, je Datei ein Dekodieren in passender Größe), nur für
  sichtbare Kacheln, gebündelt mit Background-Priorität an den UI-Thread; bei neu analysierten Dateien verworfen,
  verwaiste (gelöschte Gesichter) und alte `.tmp` per Abgleich gelöscht, `DeleteAll()` für „Gesichtsdaten löschen“.
- **Seite „Personen“** (Navigation, `Wpf/Persons`): benannte Personen (Titelbild, Name, Fotos/Vorschläge, Ausgeblendete
  zeigen) und unbenannte Gruppen (größte zuerst, „ähnelt …?“); Detail einer Person mit Reitern Vorschläge/Bestätigt
  (✓/✗ je Gesicht, Auswahl und „alle sichtbaren“ bestätigen/ablehnen, aus Person entfernen, Titelbild, umbenennen,
  zusammenführen mit…, ausblenden, löschen, Doppelklick → Lightbox); Gruppenansicht nach Nähe sortiert, **Ausreißer am
  Ende rot markiert und abgewählt**, „Als neue Person benennen…“, „Zu Person hinzufügen…“ (Vorschlag aus dem
  Gruppenzentrum), „Kein Gesicht / ignorieren“; Ausreißer-Rückfrage (AppDialogWindow) „Ohne diese bestätigen / Trotzdem
  alle / Abbrechen“. Kopfzeile mit Indexstand und Hinweis „Gesichtserkennung ausgeschaltet → Einstellungen öffnen“.
  Kachel-Listen virtualisiert (`VirtualizingTilePanel`) mit Blatt-Automation-Peers (`TileListBox`).

### Tests
- `tests/PhotoViewerV3.Tests`: 592 (+14, 1 übersprungen ohne Modell): `Faces/Assignment/FaceRecognitionServiceTests`
  (Bestätigen → Training → Vorschläge, P2-Schnittstelle + Namen, Ablehnen → nächstbester und bleibt nach Neu-Lernen,
  Entfernen → Neu-Lernen, Zusammenführen, Löschen, Ausreißer-Prüfung, Gruppen ohne bestätigte/ignorierte mit Ausreißer
  am Ende und sofortiger Aktualisierung, Index-Meldung → neue Vorschläge, Ignorieren, Thread-Verhalten bei gesperrtem
  Katalog, Bestätigung gewinnt gegen Hintergrund-Vorschlag), `FaceCropServiceTests` (Cache-Pfad, ein Render je Datei,
  Verwerfen bei Index-Meldung, Abgleich, alles löschen, kein Endlos-Wiederholen); Hilfe `FaceAssignmentWorld`
  (synthetische Fingerabdrücke in migrierter Test-DB).
- `tests/PhotoViewerV3.Wpf.Tests`: 260 (+11, 1 übersprungen), dreimal hintereinander grün: `Persons/PersonsViewModelTests`
  (Ausreißer abgewählt, Benennen mit Rückfrage/Abbrechen/vorhandener Name, Hinzufügen mit Vorschlag, Ignorieren,
  Vorschläge einzeln/Auswahl/sichtbare, Zusammenführen, Kopfzeile), `Persons/PersonsPageUiTests` (echte Seite: 2 000
  Gesichter → < 120 Container, Blatt-Peers, Ausschnitte nur für sichtbaren Bereich; Personenauswahl).
- Rauchtest 27/27 OK, 0 Binding-Fehler — neuer Schritt **„Personen“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.Persons.cs`): reproduzierbare synthetische Fingerabdrücke (2 Personen + Ausreißer) in die
  Gesichter des Fake-Analysators, Gruppe benennen (Ausreißer abgewählt), Vorschlag bestätigen/ablehnen,
  Ausreißer-Prüfung, zweite Gruppe benennen und zusammenführen, Lightbox, Ausschnitte im Cache.

### Offen
- Schwellen weiter aus synthetischen Daten (ADR-0036); Kalibrierung an echten Fingerabdrücken.
- Modell je aktiver Bibliothek (Personen global, Lernbeispiele nur aus der aktiven Bibliothek).
- Neue Index-Gesichter erscheinen in den Gruppen erst nach „Aktualisieren“/erneutem Öffnen.

## [0.2.36] — 2026-10-21

### Doku
- CLAUDE.md: Regel „Keine externen Sample-Assets im Produktivpfad“ um die Ausnahme für Laufzeit-Modelle ergänzt, die der
  Benutzer nach Bestätigung herunterlädt (buffalo_l; nie ins Repo, nie in die portable Version; ADR-0035).
- Zusammenführung von 0.2.34 (Erkennen/Index) und 0.2.35 (Lernen/Gruppieren): 578 Services-Tests, 249 WPF-Tests (3×),
  Gesichter-Tests mit echtem Modell 80/80.

## [0.2.35] — 2026-10-21

### Neu (ADR-0036) — Gesichtserkennung Teil 2: Lernen & Gruppieren (nur Services, noch nicht verdrahtet)
- `Services/Faces/Recognition` (rein, WPF-/EF-frei, deterministisch, thread-sicher lesbar, SIMD über `Vector<T>`):
  - **`IFaceRecognizer.Train`** lernt nur aus **bestätigten** Gesichtern (`FaceSample.ConfirmedPersonId`, Qualität ≥ 0,4);
    eigene Vorschläge fließen nie ein. Training = vollständiger Neuaufbau (10 000 Beispiele / 200 Personen: 232 ms) — nach
    einer Korrektur lernt die Person nur aus den verbleibenden Beispielen.
  - **`IFaceModel.Suggest`**: Ensemble aus k-NN (Mittel der 3 ähnlichsten Beispiele je Person) und linearem Klassifikator
    je Person (One-vs-Rest, logistische Regression per SGD, Eigenimplementierung); „unbekannt“, wenn keine Person nah
    genug ist (k-NN < 0,45); Alternativen absteigend; abgelehnte Vorschläge (`FaceRejection`) werden je Gesicht
    übersprungen → nächstbester Kandidat; schlechte Qualität bekommt abgewertete Vorschläge. `SuggestMany` parallel.
  - **`IFaceRecognizer.CheckOutlier`**: „Rückfrage empfohlen“ beim Bestätigen, wenn das Gesicht kaum zu den Beispielen
    der Person passt, deutlich unter ihrem Üblichen liegt oder einer anderen Person deutlich ähnlicher ist.
  - **`IFaceClusterer.Cluster`**: strenge Gruppen-Vorschläge — k-NN-Graph + agglomerativ mit Average Linkage (Schwelle
    0,55, exakt über Summenvektoren), Ausschluss ferner Mitglieder, Mindestgröße 3; Mitglieder nach Nähe zum Zentrum
    sortiert, zweifelhafte mit `IsOutlier` am Ende; Rauschen und übersprungene (Qualität/ungültig) getrennt.
- Spike 10 (`docs/spikes/10-gesichter-lernen.md`): Bewertung auf synthetischen ArcFace-ähnlichen Daten — Gruppen in allen
  Welten rein (DBSCAN/Single Linkage: bis 53 gemischte Gruppen), Zuordnung 88/94/95 % bei 1/3/10 Beispielen ohne
  Fehlzuordnung bekannter Personen, ein falsches unter 20 Beispielen ändert nichts (k = 1: 100 % falsch).

### Tests
- `tests/PhotoViewerV3.Tests`: 539 (+40): `Faces/Recognition/FaceClustererTests` (13: reine Gruppen, Doppelgänger,
  Brücken, Sortierung + Ausreißer-Markierung, Rauschen, Qualitätsfilter/ungültige Vektoren, Mindestgröße, Determinismus,
  viele Personen, Abbruch, Zentrum), `FaceRecognizerTests` (21: 1/3/10 Beispiele, unbekannt, Ablehnung → nächstbester,
  ein falsches unter 20, Ausreißer-Prüfung und Neu-Lernen nach Entfernen, Qualitätsfilter, kein Lernen aus
  Unbestätigtem, Determinismus, eine Person, viele Personen, Parallelität, Alterung), `FaceLearningEvaluationTests`
  (6, Harness; `PV3_FACELEARN_SCALE=full` für die Spike-Tabellen); Generator `SyntheticFaces`.

### Offen
- Schwellen sind an synthetischen Daten gewählt; Nachjustieren an Herberts echten Fingerabdrücken in Welle 2.
- Noch keine DB, kein DI-Eintrag, keine Oberfläche (Welle 2). Gruppierung ist O(n²): > 50 000 unbekannte Gesichter
  abschnittsweise gruppieren. Ablehnungen wirken nur auf das abgelehnte Gesicht.

## [0.2.34] — 2026-10-21

### Neu (ADR-0035, PLAN Phase 9 Teil 1)
- **Gesichtserkennung im Hintergrund** mit InsightFace *buffalo_l*: SCRFD `det_10g` (Erkennung, 5 Landmarken) und
  ArcFace `w600k_r50` (Fingerabdruck 512 float, L2-normiert) über ONNX Runtime (`Microsoft.ML.OnnxRuntime.DirectML`
  1.24.4, nur im Services-Projekt). Vorverarbeitung wie InsightFace (Letterbox 640 oben links, RGB, (x − 127.5)/128;
  Anker-Dekodierung Stride 8/16/32 × 2 Anker, Score 0.5, NMS 0.4; Ähnlichkeitstransformation auf `arcface_dst` 112 × 112,
  (x − 127.5)/127.5). Ein-/Ausgaben der ONNX-Dateien geprüft; die Ausgaben werden über ihre Formen zugeordnet.
- **DirectML mit Rückfall auf CPU** (Einstellung „Grafikkarte verwenden“, Standard an; Probelauf beim Laden), Sitzungen
  einmal geladen, je Sitzung ein Lauf zur Zeit; ArcFace auf der CPU in Stapeln, mit DirectML einzeln.
- **Bild laden** über `IFaceImageLoader` (Services) mit `WicFaceImageLoader` (Wpf): skaliertes WIC-Dekodieren auf
  längste Kante 1280, EXIF-Orientierung, RGB24. Videos werden übersprungen.
- **Qualität je Gesicht**: Erkennungs-Score, Größe (px), Schärfe (Laplace-Varianz des 112er-Ausschnitts) und ein Wert
  0..1 (0.25 Score + 0.40 Größe + 0.35 Schärfe), Einzelwerte gespeichert.
- **Schema — Migration `AddFaces`**: `Persons`, `Faces`, `FaceRejections`, `FaceScans` (Kaskaden siehe PLAN Kap. 4;
  `Persons.CoverFaceId` ohne Fremdschlüssel). Bestehende Daten bleiben. `FaceEmbeddingCodec` (float[512] ↔ 2048 Bytes
  little-endian) als Schnittstelle zur Lern-/Gruppierlogik.
- **Hintergrund-Index** `IFaceIndexService`: aktive Bibliothek, neueste Fotos zuerst, drei Bilder vorab geladen, niedrige
  Priorität, Schreiben stapelweise in einer Transaktion mit stiller Warte-Logik (ADR-0030), nie zwei Durchgänge
  gleichzeitig; startet 10 s nach dem App-Start, nach jedem Scan und bei neuen Dateien; pausier-/fortsetzbar/abbrechbar.
  Erneute Analyse (neue Modell-Kennung, „Index neu aufbauen“) gleicht über die Box ab — bestätigte Zuordnungen,
  Ablehnungen und Titelbilder bleiben; nicht wiedergefundene bestätigte/ignorierte Gesichter bleiben stehen. Fehlende
  Dateien werden später erneut versucht, nicht dekodierbare (`decode_failed`) und nicht analysierbare (`analyze_failed`)
  vermerkt; drei Analysefehler hintereinander beenden den Durchgang mit Fehlermeldung. An Ort und Stelle geänderte
  Dateien (Incremental-Scan: Größe/Zeit anders) werden neu analysiert (`IncrementalCatalogWriter` löscht ihren FaceScans-Eintrag).
- **„Neu aufbauen“ behält die Gesichter**: Faces und FaceScans werden in der Neuaufbau-Transaktion geparkt (negative
  FileId, aufgeschobene Fremdschlüssel) und über Ordnerpfad + Name auf die neuen FileIds gesetzt
  (`UserDataCarryOver.Faces.cs`); Face-Ids bleiben, kein Foto muss neu analysiert werden.
- **Modell per Benutzer-Download** (`IFaceModelProvider`): Ordner `<AppData>\models\buffalo_l\`
  (`PHOTOVIEWERV3_FACE_MODELS` überschreibt); Knopf „Modell herunterladen… (275 MB, InsightFace buffalo_l, nur
  nicht-kommerzielle Nutzung)“ nur nach Bestätigung im Dialog, mit Fortschritt, SHA-256-Prüfung, abbrechbar; entpackt
  werden nur `det_10g.onnx` und `w600k_r50.onnx`.
- **Einstellungen → Gesichtserkennung**: an/aus (Standard aus), Grafikkarte, Modellstatus, Download, Indexstand,
  „Index neu aufbauen“. **Statusleiste**: „Gesichter: 1.234 / 19.556 · 37 ms/Foto“ mit Pause/Fortsetzen und Abbrechen.

### Messung (Spike 09, `docs/spikes/09-gesichtserkennung.md`)
- 1 000 Handyfotos (nur Zahlen): Laden 115 ms, Erkennen CPU 130 ms / DirectML 23 ms, Fingerabdruck je Gesicht CPU
  112 ms / DirectML 12 ms; Durchsatz mit 3 Bildern vorab **46 ms/Foto (DirectML)** bzw. 231 ms/Foto (CPU, 2 vorab).
  44,4 % der Fotos mit Gesichtern, Ø 1,12 Gesichter je Foto. Kosinus: Serienpaar (dieselbe Person) Ø 0,79, 94 % > 0,4;
  Gesichter im selben Foto Ø 0,09, 87,5 % < 0,2. Hochrechnung 500 000 Fotos: ≈ 6,4 h (DirectML) bzw. ≈ 32 h (CPU),
  DB ≈ +1,3 GB.

### Tests
- `tests/PhotoViewerV3.Tests`: 538 (+39, 1 übersprungen ohne Modelle): `Faces/FaceMathTests` (Codec, Letterbox und
  Normierung, Ausgaben-Zuordnung, Anker-Dekodierung, NMS/IoU, Ähnlichkeitstransformation mit bekannten Punkten,
  Ausschnitt, ArcFace-Tensor, Qualität), `Faces/FaceIndexServiceTests` (alle Fotos, Pause/Fortsetzen, neue Dateien,
  Modell-Wechsel mit Bestätigung/Ablehnung/Titelbild, kein Doppel-Lauf, aus/kein Modell, fehlende/kaputte Datei, neu
  aufbauen, Abbrechen, einzelner/gehäufte Analysefehler, geänderte Datei, nicht mehr dekodierbar), `Faces/FaceSchemaTests` (Migration auf Bestands-DB, Kaskaden, Abgleich, „Neu aufbauen“ trägt
  Gesichter samt Zuordnungen), `Faces/FaceModelProviderTests` (Prüfsumme, nur zwei Dateien, falsche Prüfsumme, Abbruch,
  HTTP über lokalen Handler — der echte Download läuft in Tests nie), `Faces/OnnxFaceAnalyzerModelTests` (nur mit
  `PHOTOVIEWERV3_FACE_MODELS`: CPU/DirectML, leeres Bild ohne Gesicht, normierte und gleiche Fingerabdrücke).
- `tests/PhotoViewerV3.Wpf.Tests`: 249 (+10, 1 übersprungen): `Faces/WicFaceImageLoaderTests`, `Faces/FaceUiTests`
  (Einstellungs-Abschnitt, Download nur nach Bestätigung, Abbruch/Fehler, neu aufbauen, Statusleiste),
  `Faces/FaceRecognitionMeasurementTests` (Messung, nur lokal mit Modellen und `PV3_FACE_SAMPLE_DIR`).
- Rauchtest 26/26 OK, 0 WPF-Binding-Fehler — neuer Schritt **„Gesichter“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.Faces.cs`, Fake-Analysator ohne Modelle über den echten WIC-Lader): Einschalten in den
  Einstellungen, Statusleiste, Pause hält an, Fortsetzen, FaceScans/Faces für alle Fotos, wieder aus.

### Offen
- Personen-Seite, Lernen/Vorschläge (Welle 2, `Services/Faces/Recognition`), Filter/Lightbox/Liste nach Personen.
- Videos werden nicht analysiert. Bestätigte Gesichter, die ein neues Modell nicht wiederfindet, behalten den
  Fingerabdruck der alten Kennung (Welle 2 muss `ModelVersion` beachten).
- `Persons.CoverFaceId` hat keinen Fremdschlüssel — Leser müssen ein gelöschtes Gesicht tolerieren.
- Vorschlag an Herbert: CLAUDE.md-Regel „Keine externen Sample-Assets im Produktivpfad“ um „außer vom Benutzer nach
  Bestätigung geladene Laufzeit-Modelle (ADR-0035)“ präzisieren.

## [0.2.33] — 2026-10-20

### Behoben
- Wackelnde Tests in `tests/PhotoViewerV3.Tests`: `ArchiveServiceTests.ExtractAsync_bricht_ab_und_laesst_keine_Teildateien_zurueck`
  brach unter Last gelegentlich nicht ab (498/499). Ursache: `Progress<T>` stellt Meldungen ohne SynchronizationContext
  asynchron über den Thread-Pool zu — der Abbruch aus der Meldung kam erst, als alle 20 Dateien schon entpackt waren.
  Dasselbe Muster prüfte in `ArchiveServiceTests` (Fortschritt) und `FileSorterServiceTests` (Ausführen) eine Liste,
  die nach dem `await` noch nicht vollständig sein musste. Neu: `Fixtures/InlineProgress<T>` meldet sofort auf dem
  meldenden Thread; die drei Tests nutzen es.

### Tests
- `tests/PhotoViewerV3.Tests`: 499, dreimal hintereinander grün; die beiden betroffenen Klassen 15× hintereinander grün.

### Offen
- Einmal (von über 15 Läufen) endete ein Lauf der Services-Tests mit „Testlauf abgebrochen“ statt mit einem Ergebnis
  (auch Agent O sah einmal nur 147 gezählte Tests). Mit `--blame-crash` und in allen weiteren Läufen nicht wieder
  aufgetreten; Ursache unbekannt.

## [0.2.32] — 2026-10-20

### Neu (ADR-0034)
- **„In die Bibliothek übernehmen…“**: Rechtsklick auf einen Ordner unter einem Eingang (auch auf den Eingang selbst)
  im Ordnerbaum bzw. Knopf je Eingang auf der Bibliotheksseite. Dialog (eigenes Fenster wie „In die Bibliothek…“) mit dem
  Ordnerbaum des Bibliotheksordners (Suche, Neuer Ordner, zuletzt benutztes Ziel vorgewählt), Vorschau des Ergebnisbaums
  „alt → neu“ mit Dateien je Ordner und Hinweisen (zusammengezogen, zusammengeführt, umbenannt) und den Optionen
  **Leere Zwischenordner zusammenziehen**, **Quellordner danach in den Papierkorb**, **Eingang danach austragen** (alle
  Standard an, Austragen nur bei ganzem Eingang).
- **Zusammenziehen**: ein Ordner ohne Dateien mit genau einem nicht leeren Unterordner entfällt, bis zum ersten Ordner
  mit Dateien oder mehreren Unterordnern (`Fotos\leer\leer\leer\inhalt` → `Fotos\inhalt`, auch der übernommene Ordner
  selbst); Verzweigungen bleiben, leere Teilbäume werden nicht angelegt; Geschwister, die danach gleich hießen, werden an
  dieser Stelle nicht zusammengezogen. Vorhandene Zielordner werden zusammengeführt, Namenskonflikte → „_1“, „_2“.
  Reine Plan-Logik WPF-frei in `Services/Operations/FolderAdoptionPlanner.cs`; Quellbaum über
  `FolderAdoptionSourceReader` (alle Dateien inkl. .xmp/.txt; Systemordner `.thumbs`, `.cache`, `.temp`, `.converted`,
  `.thumbnails`, `@eaDir` und `Thumbs.db`/`desktop.ini`/`.DS_Store` werden nicht übernommen).
- **Ausführung** in EINEM protokollierten Batch: `IFileOperationService.AdoptFolderAsync`, neuer
  `OperationType.AdoptFolder = 6` (keine Schema-Änderung) mit Ordner-Operation, MkDir je neuem Ordner, Move je Datei,
  Recycle des Quellordners; Scan-Sperre (ADR-0025), eigene Pfade für die Ordnerüberwachung, FileIds bleiben (Bewertung,
  Tags, Kategorien, Vorschaubilder wandern mit), umbenannte Dateien mit FTS-'delete'/Insert, nicht katalogisierte Medien
  werden aufgenommen, neue Ordner mit `Folders.RootId` des Bibliotheksordners. Der Quellordner kommt nur in den
  Papierkorb, wenn keine Datei mehr darin liegt (`IRecycleBin.MoveDirectoryToRecycleBin`, Shell fragt, statt zu
  große Ordner endgültig zu löschen); sonst Bericht mit den Resten. Bleibt der Eingang eingetragen, geht nur sein Inhalt
  in den Papierkorb. Eingang austragen über `ILibraryService.RemoveRootAsync`, nur ohne Rest. Gelingt keine Datei, werden
  angelegte Zielordner sofort wieder entfernt.
- **Rückgängig** (Galerie) für Übernahmen: Dateien zurück an die alten Pfade (Ordner neu angelegt), leere Zielordner samt
  Ordnerzeilen entfernt, ein ausgetragener Eingang wird wieder eingetragen (Name = Ordnername); überlappt er inzwischen
  eine andere Wurzel, verweigert das Rückgängig, statt den Katalog zu verbiegen.
- **Ordner bewerten / kategorisieren**: Kontextmenü im Ordnerbaum „Bewertung für alle Dateien“ (1–5, entfernen), „Favorit
  für alle“, „Kategorie für alle…“ (Kategorie-Auswahl), „Tags für alle…“ (Tag-Auswahl) — rekursiv, Bestätigung mit
  Anzahl, über die vorhandenen Dienste in Abschnitten zu 2 000 Dateien (`IFolderMetadataService`, Warte-Logik ADR-0030).
  Ergebnis als ausblendbarer Hinweis über dem Baum.
- `Wpf/Folders/FolderActions` (Ordner-Aktionen für Baum und Bibliotheksseite, Registrierung `AddFolderActions()`),
  `IFolderAdoptionPicker` (Dialog bzw. Rauchtest-Fake), `AddFolderAdoption()` in `AddPhotoViewerServices`.

### Geändert — klarere Begriffe (nur Oberfläche und README)
- Archiv-Wurzel heißt **„Bibliotheksordner“** (Rollen-Chip, Bibliotheksliste, Sortierer, Fehlermeldungen); „Ins
  Archiv…“ heißt **„In die Bibliothek…“** (Strg+M bleibt); Status-Filter **„Noch im Eingang“ / „In der Bibliothek“**,
  Kachel-Badge „Bibliothek“; „Eingang“ = unsortierter Quellordner. Archiv-DATEIEN (ZIP/RAR/7z, Filter „Archive“,
  „Entpacken…“, automatisches Entpacken) bleiben „Archive“. Keine Umbenennung von Code-Bezeichnern, Einstellungs-Schlüsseln
  oder Katalogdaten, keine Migration bestehender Ordner.
- Bibliotheksseite: Kasten **„So funktioniert's“** (Eingang hinzufügen → ansehen, bewerten, kategorisieren → Ordner in
  die Bibliothek übernehmen) und ein Satz, was die Datenbank ist (Verweise und Bewertungen, keine Bilder); Erklärtext bricht um.

### Tests
- `tests/PhotoViewerV3.Tests`: 499 (+21): `Operations/FolderAdoptionPlannerTests` (Beispielkette, Wurzel als
  Durchgangsordner, Verzweigung, Datei im Zwischenordner, leere Blätter, ohne Zusammenziehen, Namenskonflikte ohne
  Groß-/Kleinschreibung, Zusammenführen mit vorhandenem Ordner, gleichnamige Geschwister, Datei mit Ordnernamen, leerer
  Ordner / Ziel im Quellordner, Quellbaum mit Nicht-Medien und Systemordnern), `Operations/FolderAdoptionExecutionTests`
  (ganzer Eingang mit Papierkorb, Austragen, FileIds/Bewertung/Tag/FTS/RootId und Rückgängig samt Wieder-Eintragen,
  Unterordner, Eingang behalten, Rest-Dateien, nichts verschoben, gescheiterter Papierkorb, Namenskonflikt mit Undo,
  kein Kontext außerhalb eines Eingangs, Ordner bewerten/Favorit/Tags/Kategorie).
- `tests/PhotoViewerV3.Wpf.Tests`: 239 (+2): `ArchiveTarget/FolderAdoptionDialogTests` (Vorschau, Zusammenziehen an/aus,
  Ziel außerhalb gesperrt; echtes Fenster liefert den Plan). Bestehende Tests auf die neuen Texte angepasst.
- **Rauchtest 25/25 OK**, 0 WPF-Binding-Fehler — neuer Schritt **„Ordner übernehmen“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.FolderAdoption.cs`): Eingang `<Bibliothek>-Uebernahme-Eingang` mit `leer\leer2\inhalt`,
  `Reise\Tag1|Tag2` und notiz.txt, „Bewertung für alle Dateien“ (Reise → 5) und „Tags für alle…“ (inhalt) im Ordnerbaum,
  Übernahme des ganzen Eingangs nach `2026\Smoke-Uebernahme` (Vorschau, Struktur, Papierkorb, Austragen, FileIds,
  Bewertung, Tag, Batch-Typ), Rückgängig über die Galerie, Aufräumen. Screenshot `20-ordnerbaum-uebernommen`.

### Offen
- Kein Fortschrittsbalken während einer großen Übernahme (Ordnerbaum/Bibliotheksseite sind bis zum Ende gesperrt);
  Rückgängig sehr großer Übernahmen lädt alle Operationen auf einmal (wie bei „Verschieben“).
- Rückgängig holt Systemordner (`.thumbs` …) und deren Katalogzeilen nicht aus dem Papierkorb; ein wieder eingetragener
  Eingang heißt wie sein Ordner, nicht wie vorher.

## [0.2.31] — 2026-10-20

### Neu (ADR-0033, offene Punkte aus 0.2.25)
- **Automatisches Entpacken je Bibliothek** statt global: Schlüssel `archives.autoExtract.enabled.{LibraryId}` und
  `archives.autoExtract.recycle.{LibraryId}` in der Settings-Tabelle (keine Schema-Änderung), Standard aus. Beim ersten
  Start übernimmt jede bestehende Bibliothek ohne eigenen Eintrag die bisherige globale Einstellung (Entpacken und
  Papierkorb), danach werden die globalen Schlüssel entfernt — die Übernahme läuft genau einmal, neue Bibliotheken
  starten mit „aus“.
- Einstellungen → **Archive** zeigt die Schalter der **aktiven Bibliothek** („Gilt für die aktive Bibliothek „Name““);
  ohne aktive Bibliothek ist der Abschnitt deaktiviert. Die Bibliotheksseite bleibt unverändert.
- **„Vorhandene Archive in Eingängen jetzt entpacken…“** (nur bei eingeschaltetem Schalter): zählt Archive der aktiven
  Bibliothek unter Eingangs-Wurzeln, deren Zielordner noch fehlt, fragt mit Anzahl nach und reiht denselben Lauf wie
  nach einem Scan ein (Sperre je Archiv, Papierkorb nach Einstellung, ein Folge-Scan, Statusleiste). Früher entpackte
  Archive (Zielordner vorhanden) zählen nicht und werden nicht als „übersprungen“ gemeldet.
- `IArchiveAutoExtractService`: `GetSettings(libraryId)`, `SetEnabledAsync`/`SetRecycleAfterExtractAsync` mit LibraryId,
  `CountExistingAsync`, `ExtractExistingAsync`; `IArchiveAutoExtractSettingsStore`: je Bibliothek, `LoadAllAsync`,
  `MigrateGlobalDefaultAsync`. ADR-0032 mit Statusvermerk (Entscheidung „global“ abgelöst).

### Geändert (ADR-0029, Nachtrag)
- **Portable Version ohne `BuildHost-net472/-netcore`**: `Microsoft.EntityFrameworkCore.Design` im Data-Projekt ohne
  `contentfiles` (bleibt `PrivateAssets=all`); die Ordner kamen als Inhaltsdateien von
  `Microsoft.CodeAnalysis.Workspaces.MSBuild` in jede Ausgabe bis ins Paket. `build/publish.ps1` bricht ab, wenn
  `BuildHost-*` oder `Microsoft.EntityFrameworkCore.Design.dll` im Paket liegen.
- Paket gemessen (gleicher Code, nur diese Änderung): **vorher 780 Dateien / 256,8 MB / ZIP 114,1 MB, nachher 734 Dateien
  / 252,8 MB / ZIP 112,3 MB** — Unterschied genau die 46 BuildHost-Dateien (2,2 + 1,8 MB).
- `dotnet ef migrations add` im Data-Projekt geprüft (Probe-Migration erzeugt, leer, wieder entfernt; Snapshot unverändert);
  `dotnet ef migrations list` listet alle vier Migrationen.

### Tests
- `tests/PhotoViewerV3.Tests`: 466 (+7 `Archive/ArchiveAutoExtractPerLibraryTests`: Store je Bibliothek getrennt,
  einmalige Übernahme der globalen Vorgabe (eigener Eintrag hat Vorrang, zweiter Start ändert nichts, neue Bibliothek
  aus), ohne globale Einstellung alles aus, Start übernimmt und entpackt in beiden Bibliotheken, Entpacken nur in der
  Bibliothek mit „an“ (auch nach Umschalten), Papierkorb-Schalter je Bibliothek, vorhandene Archive nachholen (nur bei
  „an“, ohne schon entpackte, Archiv-Wurzel und andere Bibliothek); `ArchiveAutoExtractServiceTests` auf die neue API).
- `tests/PhotoViewerV3.Wpf.Tests`: 203 (+1): `Settings/SettingsAutoExtractTests` (aktive Bibliothek laden/wechseln,
  Speichern mit LibraryId, ohne Dienst oder ohne aktive Bibliothek deaktiviert, „Vorhandene Archive“ mit Rückfrage,
  Abbrechen, Papierkorb-Hinweis, nichts vorhanden). Volle Suite 3× hintereinander grün; Build ohne Warnungen.
- **Rauchtest 23/23 OK**, 0 WPF-Binding-Fehler — neuer Schritt **„Auto-Entpacken“** vor „Ordnerüberwachung“
  (`Shell/Smoke/SmokeScenario.AutoExtract.cs`): zweiter Eingang `<Bibliothek>-Auto-Eingang` mit einem ZIP vor dem
  Einschalten (bleibt unberührt), Schalter der aktiven Bibliothek auf der Einstellungsseite (DB-Schlüssel, kein globaler
  Schlüssel), ZIP in den Eingang kopieren → Aktualisieren → automatisch entpackt → Folge-Scan (Added 2) → Katalog,
  Galerie und Statusleiste, danach „Vorhandene Archive jetzt entpacken“ für das ältere ZIP; am Ende Schalter aus,
  Eingang entfernt. Screenshots `15-einstellungen-auto-entpacken`, `15-galerie-auto-entpackt`.
- Rauchtest zusätzlich mit der **veröffentlichten** EXE (Paket 0.2.31 ohne BuildHost,
  `build/run-smoke.ps1 -Exe <Paket>\PhotoViewerV3.exe -TestLib <Testbibliothek>`): 23/23 OK, 0 WPF-Binding-Fehler, kein
  ERR/FTL im Log.

### Offen
- Die Einstellung einer nicht aktiven Bibliothek ist nur nach Wechsel der aktiven Bibliothek änderbar (Schalter auf der
  Bibliotheksseite wäre über dieselbe Service-API nachrüstbar).
- „Vorhandene Archive nachholen“ erkennt Archive in Ordnern, die in einer **früheren** Sitzung aus einem Archiv
  entstanden, nicht (nur Reste an der Tiefengrenze 3 betroffen). Settings-Einträge gelöschter Bibliotheken bleiben liegen.
- Archive eines Fresh-Scans (neue Bibliothek) und von „Neu aufbauen“ werden weiterhin nicht automatisch entpackt — über
  „Vorhandene Archive jetzt entpacken…“ aber nachholbar.

## [0.2.30] — 2026-10-20

### Geändert (ADR-0031, offene Punkte aus 0.2.23)
- **Kategorie-Sortierung segmentweise**: statt einer Sortierung aller Dateien mit je einem Schlüssel-Lookup zählt die
  Galerie die Dateien je Wert und liest die Seite nur aus dem Wert-Segment, in das sie fällt (Reihenfolge des
  Datums-Index, `f.Id IN (Dateien des Werts)`, Reverse-Trick vom Segmentende, Spalten erst für die Zeilen der Seite).
  Kleine Wert-Segmente (≤ 4 000 Dateien, ohne Suche/Filter) lesen ihre Zuordnungen direkt. Zählung und Seite laufen in
  einer Lese-Transaktion (ein Snapshot); die Zählung bleibt beim Blättern gültig, bis eine andere Verbindung schreibt
  (`PRAGMA data_version`), die Gesamtzahl aus `GetTotalCountAsync` wird übernommen statt doppelt gezählt. Reihenfolge
  unverändert (ohne Wert zuletzt, Werte-Reihenfolge der Verwaltung, gleiche Werte nach Datum neu → alt, dann Id).
  500 000 Dateien: Seite 1 / Mitte / Seite 801 von 7,8–8,4 s auf 0,26–0,61 s beim ersten Laden nach einer Änderung und
  0,03–0,26 s beim Blättern (Spike #08, `docs/spikes/08-suche-sortierung.md`).
- **Neue Migration `AddCategorySortIndex`**: abdeckender Index `IX_FileCategoryValues_Category_Value_File (CategoryId,
  ValueId, FileId)` ersetzt `IX_FileCategoryValues_Category`; bestehende Daten bleiben unverändert.
- **Tag-Zweig der Suche** in derselben `IN`-Form wie die Kategorie-Werte: `FileTags.TagId IN (passende Tags)` über
  `IX_FileTags_Tag` statt Join über alle Zuordnungen (Plan vorher `SCAN ft`, nachher `SEARCH ft USING INDEX
  IX_FileTags_Tag (TagId=?)`). 500 000 Dateien mit 1,1 Mio. Tag-Zuweisungen: seltener Tag 196 → 42 ms (Zählen wie
  Seite); bei einem Tag mit 100 000 Treffern dominieren die Treffer selbst (≈ 0,15–0,35 s, vorher wie nachher).

### Neu
- **Suche findet Kategorie-Namen**: trifft der Suchtext den Namen einer Kategorie (Teilstring, ohne Groß-/Kleinschreibung),
  erscheinen alle Dateien mit irgendeinem Wert dieser Kategorie — gleiche Mechanik wie Tags/Werte (`CategoryId IN
  (passende Kategorien)` über den neuen Index, keine Index-Pflege, Umbenennen wirkt sofort).

### Tests
- `tests/PhotoViewerV3.Tests`: 460 (+1 `Categories/CategorySortIndexMigrationTests`) (DB auf Stand `AddCategories` mit
  Zuweisungen → migriert, neuer Index da, alter weg, alle Zuweisungen erhalten, `foreign_key_check` leer).
- `tests/PhotoViewerV3.Wpf.Tests`: 234 (+32): `Categories/GalleryCategorySortPagingTests` (neu: Blättern mit 8 Seitengrößen auf/ab,
  mit Filter und Suche gleich der Reihenfolge der alten Ein-Abfrage-Sortierung, zwei Bibliotheken, Zuweisung zwischen
  zwei Seiten macht die zwischengespeicherte Zählung ungültig — Gegenprobe ohne `data_version` rot, neue Datei nach
  dem Zählen erscheint ohne erneutes Zählen — Gegenprobe mit blind übernommener Gesamtzahl rot, leere Werte, Seite
  hinter dem Ende), `GalleryPageSourceCategoryTests` (Kategorie-Namen-Suche, Umbenennen, mit Filter/Sortierung, Plan
  über den neuen Index; Plan-Test der Sortierung auf die Segment-Form umgestellt), `GalleryPageSourceTagTests` (Plan
  `IX_FileTags_Tag` ohne `SCAN ft`, Umbenennen/Zuweisen), `Gallery/GallerySearchSortMeasurementTests` (Messung,
  Größe über `PV3_SEARCHSORT_FILES`, Standard 3 000). Wpf-Suite 3× hintereinander grün, Build ohne Warnungen.
- Rauchtest 22/22 OK, 0 WPF-Binding-Fehler, kein ERR/FTL im Log (Schritt „Kategorien“ deckt Suche und Sortierung ab;
  kein neuer Schritt).

### Offen
- Grundsortierungen Datum/Name/Größe lesen je übersprungener Zeile die ganze Files-Zeile; im synthetischen
  500k-Katalog mit quer zur Id verteiltem Datum kostet die mittlere Seite 2,4 s (Datum) bzw. 6,8 s (Name). Abhilfe wie
  hier: verzögerter Join (innen nur Id + Sortierspalten), für Name/Größe mit eigenem Index (Spike #08).
- Eine Suche, die sehr viele Dateien trifft (z. B. Kategorie-Name mit 250 000 Dateien), materialisiert alle Treffer
  (≈ 0,3–0,4 s bei 500k); ein kurzer Suchtext trifft als Teilstring jede Kategorie, deren Name ihn enthält.
- Der Platzhalter des Suchfelds („Name, Endung oder Tag…“) nennt Kategorien nicht (Gallery-View, nicht angefasst).

## [0.2.29] — 2026-10-20

### Behoben (ADR-0030, offener Punkt aus 0.2.24)
- **Keine verlorene Benutzeraktion während „Neu aufbauen“.** Die eine Schreibtransaktion am Ende (500 000 Dateien ≈ 30 s)
  ließ andere Schreiber scheitern — gemessen nach **34 s** (busy_timeout 5 s + interne Wiederholung von
  Microsoft.Data.Sqlite bis CommandTimeout 30 s), und weil Microsoft.Data.Sqlite auch in `*Async` synchron arbeitet, war
  das Fenster bei einer Bewertung so lange **eingefroren**. Jetzt wartet jeder Schreiber asynchron:
  - `Services/Database/SqliteBusyRetry` (zentral): Versuch auf dem Thread-Pool, nach SQLITE_BUSY/LOCKED asynchron
    warten und wiederholen bis `MaxWait` (5 min); danach kommt der Fehler wie bisher beim Aufrufer an.
  - `BusyRetryInterceptor` an jedem EF-Kontext der `LibraryContextFactory`: Autocommit-Schreibbefehle und
    Transaktionsbeginn laufen darüber — Einstellungen, Tag-/Kategorie-Stammdaten, Thumbnail-Status und
    Datei-Operationen ohne eigene Zeile.
  - Favorit/Bewertung, Tag- und Kategorie-Zuweisungen über `Services/Catalog/CatalogFileTargets`: Ordnerpfad + Name der
    Ziele vorher lesen, `BEGIN IMMEDIATE` mit Wiederholung, danach **fehlende FileIds auf die neuen Ids des Neuaufbaus
    umsetzen** — sonst landete die wartende Bewertung nach dem Commit auf einer Id, die es nicht mehr gibt (still
    verloren; im Test mit Wiederholung allein rot). `CatalogUpdateService` schreibt dafür Raw-SQL in einer Transaktion
    statt `ExecuteUpdateAsync` je Chunk; die Nachricht kommt nach dem Commit mit den tatsächlich geschriebenen Ids.
  - Statusleiste: „Änderung wartet auf den Katalog…“ (bzw. „n Änderungen warten…“, Uhr-Symbol, Tooltip) solange ein
    Schreiber wartet (`CatalogWriteWaitMessage`).
- **Bibliotheksseite erkennt ihr eigenes Warten an einer Kennung** (offener Punkt aus 0.2.22, ADR-0025 Nachtrag):
  `IScanGate.AcquireAsync(libraryId, callerId, ct)`, `ScanGateWait.CallerId`, `ScanOptions.GateCallerId`. Die Seite
  vergibt je Scan-Lauf eine Guid und zeigt nur dieses Warten; die Ordnerüberwachung scannt mit eigener Kennung. Vorher
  hielt die Seite das Warten eines gleichzeitig wartenden automatischen Scans für ihr eigenes.

### Geändert
- Nachrichten der Dienste nach Bewertung, Tags, Kategorien und EF-Schreibvorgängen kommen jetzt vom Thread-Pool (alle
  Empfänger dispatchen selbst); sie laufen also nach der Rückkehr des Aufrufs. Rauchtest-Schritt „Tags“ liest den
  Menüeintrag deshalb nach der Zuweisung neu (das Menü kann nach dem Anlegen zweier Tags noch einmal neu aufgebaut werden).
- `SqlitePragmaInterceptor(busyTimeoutMs)` und `LibraryContextFactory(busyRetry, messenger, defaultCommandTimeoutSeconds)`
  nehmen verkürzte Zeiten für Tests; produktiv unverändert (busy_timeout 5000, CommandTimeout 30 s).

### Tests
- `tests/PhotoViewerV3.Tests`: 470 (+11): `Database/CatalogWriteRetryTests` (9: Bewertung, Tag-Zuweisung,
  Kategorie-Wert und EF-Schreiber — Einzel-INSERT, Mehrfach-UPDATE in Transaktion, Einstellung — kommen nach einer 2,5 s
  gehaltenen Schreibsperre an, der Aufruf kehrt sofort zurück, Wartenachricht 1 → 0; Bewertung und Tag auf eine beim
  „Neuaufbau“ ersetzte FileId landen auf der neuen Id; Gegenprobe ohne Wiederholung scheitert mit SQLITE_BUSY; nach der
  Höchstdauer kommt der Fehler an; Abbruch schreibt nichts; `IsBusy`), busy_timeout 100 ms und CommandTimeout 1 s statt
  5 s/30 s. Gegen den alten Stand 7 von 9 rot (Gegenprobe und `IsBusy` grün); mit Interceptor, aber altem
  `CatalogUpdateService` bleibt der FileId-Test rot. `Watch/ScanGateCallerIdTests` (2: Kennungen gleichzeitiger
  Wartender in Nachrichten und `GetWaits`, ScanService reicht `ScanOptions.GateCallerId` durch).
- `tests/PhotoViewerV3.Wpf.Tests`: 204 (+2 `Shell/ScanGateCallerIdViewModelTests`: Watch-Scan wartet zuerst, die Seite
  zeigt nur ihr eigenes Warten und wartet weiter, als der Watch-Scan die Sperre bekommt — gegen das alte
  `LibraryViewModel` rot; Statusleisten-Hinweis 1/2/0 Wartende). Volle Suite 3× hintereinander grün. Die Hilfsklasse
  `GateScanService` in `ScanGateWaitViewModelTests` reicht die Kennung wie der echte ScanService durch.
- Rauchtest 23/23 OK, 0 WPF-Binding-Fehler; neuer Schritt „Katalog gesperrt“ (hält BEGIN IMMEDIATE, Bewertung über die
  Kachel kehrt sofort zurück, Statusleiste zeigt den Hinweis, Bewertung nach der Freigabe in der DB; Screenshot
  `19-statusleiste-katalog-wartet`; im Log „nach 5,7 s (2 Versuche) ausgeführt“).

### Offen
- Rohe Schreibpfade der Scans (`IncrementalCatalogWriter`, `LibraryCatalogCleanup`) und synchrone EF-Aufrufe nutzen die
  Wiederholung nicht (Hintergrund-Threads, warten wie bisher bis ≈ 35 s): ein automatischer Scan einer *anderen*
  Bibliothek kann während eines langen „Neu aufbauen“ weiterhin scheitern.
- Wird die App beendet, während eine Änderung wartet, ist sie verloren (Tooltip weist darauf hin).
- Eine Aktion auf eine alte FileId *nach* dem Commit, bevor die Galerie neu geladen hat, trifft weiter ins Leere (vorher
  ebenso; betrifft nur Klicks in diesem Augenblick).

## [0.2.28] — 2026-10-20

### Behoben
- **Wackelnde UI-Tests in der vollen Suite** (`tests/PhotoViewerV3.Wpf.Tests`): `AppDialogWindowTests` (Namensabfrage,
  gelegentlich auch Fehlerdialog) und `ArchiveUiTests.Dialog_Fenster_oeffnet_entpackt_und_schliesst` rissen ab und zu die
  10-s-Grenze (gemessen bis 12 s *im* Dialog), einzeln liefen sie stabil grün. Ursache: Alle UI-Testklassen teilen sich
  den einen `TestUiThread`, liefen als xUnit-Klassen aber parallel. `ShowDialog` und `Dispatcher.Invoke` (Pump) öffnen
  dort verschachtelte Message-Loops, in denen die Dispatcher-Aufträge fremder Testklassen (z. B. die Layout-Messungen der
  `VirtualizingTilePanelTests`) mitliefen — der Dialog kehrte erst danach zurück.
- Abhilfe: neue xUnit-Sammlung `Fixtures/UiTestCollection` (`DisableParallelization = true`); alle zehn Klassen, die den
  UI-Thread nutzen, stehen darin und laufen nacheinander. `TestUiThread` prüft beim Zugriff über den Aufrufstapel, dass
  die aufrufende Testklasse in der Sammlung steht, und wirft sonst sofort — eine vergessene Zuordnung fällt damit immer
  auf statt gelegentlich. `AppDialogWindowTests` misst wie der Archiv-Test nur noch die Zeit im Dialog (nicht den Start
  des UI-Threads); Zeitgrenzen und Timeouts bleiben unverändert.
- Zweites Fehlerbild der Namensabfrage (Eingabe leer statt „02 Fotos“): `ShowDialog` übergeht `ShowActivated = false`,
  der Testdialog wird auch außerhalb des Bildschirms Vordergrundfenster mit fokussiertem, markiertem Eingabefeld — eine
  Taste des Benutzers in seiner eigenen App (z. B. Rücktaste) landete im Test. `TestUiThread` verwirft deshalb echte
  Tastatur- und Maus-Eingaben (`InputManager.PreProcessInput`); kein Test nutzt sie, Fokuswechsel bleiben unberührt.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 169 (+1 `AppDialogWindowTests.Eingaben_ueber_den_InputManager_erreichen_den_Testdialog_nicht`:
  Rücktaste und Texteingabe über den InputManager ändern die Vorbelegung nicht; Gegenprobe ohne Filter rot). Volle Suite
  10× hintereinander grün (vorher 3 von 7 Läufen rot); Build ohne Warnungen.
- Beim Zusammenführen mit 0.2.21–0.2.27: `Categories/CategoryPickerTests` (aus 0.2.23) in die Sammlung `UiTestCollection`
  aufgenommen — der neue Wächter in `TestUiThread` verlangt das für jede Klasse, die den UI-Thread nutzt.
  `Shell/ScanGateWaitViewModelTests` (aus 0.2.22) nutzt statt ihrer eigenen Sammlung `ScanGateWaitUiCollection` jetzt
  ebenfalls `UiTestCollection` — zwei Sammlungen hätten wieder parallel auf dem einen UI-Thread laufen können.

## [0.2.27] — 2026-10-20

### Neu
- **Knopf „Alle auswählen“** in der Galerie: rechts in der Kopfzeile neben „Rückgängig“ (immer sichtbar) und in der
  Aktionsleiste neben „Auswahl aufheben“; wählt wie Strg+A alle Dateien der aktuellen Seite (`SelectAllCommand`).
  Damit die Ansichtszeile bei 1024 px Fensterbreite einzeilig bleibt, sind Sortier-Auswahl (150 px) und
  Vorschaugrößen-Regler (72 px, ohne Symbol davor) etwas schmaler.

### Tests
- Kopfzeilen-Layouttest (9 Breiten/Varianten) weiter grün mit dem neuen Knopf; Wpf-Tests 168, Rauchtest 21/21.

### Offen
- `AppDialogWindowTests` und `ArchiveUiTests` (echte Fenster) scheitern in der vollen Wpf-Suite gelegentlich (einzeln
  stabil grün, schon bei 0.2.20 beobachtet) — Fokus/Timing, nicht an diese Änderung gebunden.

## [0.2.26] — 2026-10-20

### Neu (ADR-0029)
- **Portable Version**: `pwsh -File build\publish.ps1` baut per `dotnet publish` ein self-contained Paket für
  `win-x64` (kein installiertes .NET nötig) als Ordner `artifacts\PhotoViewerV3-<Version>-win-x64\` plus ZIP — kein
  Single-File (LibVLC braucht seinen Plugin-Ordner), kein Trimming (WPF), nur `libvlc\win-x64`, nur deutsche
  Satelliten-Ressourcen. Das Skript prüft Produktversion = CHANGELOG sowie SQLite- und LibVLC-Bestandteile.
  Schalter `-OutputRoot`, `-NoZip`, `-FrameworkDependent`. `artifacts/` und `publish/` stehen in `.gitignore`.
- **Rauchtest gegen eine beliebige EXE**: `build\run-smoke.ps1 -Exe <PhotoViewerV3.exe> -TestLib <Testbibliothek>`
  kopiert die Testbibliothek in einen frischen Arbeitsordner (Standard `%TEMP%\PhotoViewerV3-smoke-<Tag>`), startet
  mit eigenem AppData und gibt Ergebnis, Log-Auszug und Screenshots aus (ExitCode 0 = alle Schritte OK).

### Geändert
- **App-Version aus dem CHANGELOG**: `PhotoViewerV3.Wpf.csproj` liest die Version beim Build aus dem obersten
  Eintrag `## [x.y.z]` (bisher fest `0.2.0`, Einstellungen → „Über“ zeigte deshalb seit 0.2.1 die falsche Version).
  Der Rauchtest-Schritt „Einstellungen“ erwartet jetzt die Assembly-Version statt fest „0.2.0“.

### Geprüft
- Pfade hängen nicht vom Repo-Layout ab: Daten unter `%LocalAppData%\PhotoViewerV3` bzw. `PHOTOVIEWERV3_APPDATA` und
  `database-location.json`; Formatregister sind Code; SQLite (`e_sqlite3.dll`) und LibVLC liegen im Paket; FFmpeg
  wird wie bisher im PATH bzw. unter den winget-Links gesucht.
- Paket 0.2.26: 780 Dateien, 256,6 MB entpackt, ZIP 114 MB. Rauchtest mit der **veröffentlichten** EXE: 21/21 OK,
  0 WPF-Binding-Fehler, Version „0.2.26“, LibVLC 3.0.21 und FFmpeg gefunden.

### Offen
- FFmpeg ist nicht im Paket (Lizenz, Größe) und wird nicht neben der EXE gesucht; kein Code-Signing, kein Installer.
- `BuildHost-net472/-netcore` (~4 MB aus `Microsoft.EntityFrameworkCore.Design`) liegen unnötig im Paket.

## [0.2.25] — 2026-10-20

### Neu (ADR-0032)
- **Archive automatisch entpacken** (Einstellungen → **Archive**, Standard **aus**, global): findet ein Incremental-Scan
  („Aktualisieren“ oder Ordnerüberwachung) ein **neues** Archiv unter einer **Eingangs-Wurzel**, entpackt der neue
  `IArchiveAutoExtractService` (Services/Archive) es **nach dem Scan** vollständig in den Ordner mit dem Archivnamen daneben
  (verschachtelte Archive über das bestehende Runden-System bis Tiefe 3) und startet danach einen Incremental-Scan.
  Optional (zweiter Schalter, ebenfalls aus) wandert das Archiv nach fehlerfreiem Entpacken über
  `IFileOperationService.RecycleAsync` protokolliert in den Papierkorb.
- Unberührt bleiben Archive unter der Archiv-Wurzel, Archive aus Fresh-Scan/„Neu aufbauen“, Archive in einem aus einem
  Archiv entstandenen Ordner (neue `ArchiveExtractedMessage` des `ArchiveService`, manuell wie automatisch — keine
  Schleife) und Archive, deren Zielordner schon existiert. Passwortgeschützte, beschädigte und leere Archive bleiben liegen.
- **Sperren**: entpackt wird je Archiv mit `IScanGate.AcquireForFileOperationAsync` (wartet auf das Ende des Scans),
  Papierkorb und Folge-Scan holen die Sperre danach selbst (nicht wiedereintrittsfähig, ADR-0025). Kein Eingriff in den
  ScanService.
- **Ordnerüberwachung**: neues `IOwnChangeScope.AddTree` (Ordner und alles darunter gilt als eigen); die Zielordner sind bis
  nach dem Folge-Scan angemeldet — kein zweiter automatischer Scan.
- **Statusleiste**: Fortschritt („Archiv 1/3 wird automatisch entpackt: …“), Ergebnis („Automatisch entpackt: n Archive ·
  m Dateien · k im Papierkorb“), gemeldete Archive als Warnung mit Name und Grund im Tooltip.

### Tests
- `tests/PhotoViewerV3.Tests`: 434 (+10): `Archive/ArchiveAutoExtractServiceTests` (9: Standard aus, aus = kein Entpacken,
  an = Entpacken neben dem Archiv + Folge-Scan + eigene Ordnerbäume, Papierkorb protokolliert, nur Eingang, verschlüsselt/
  beschädigt/Zielordner vorhanden bleiben liegen und werden gemeldet, wartet auf gehaltene Sperre und kein Deadlock mit
  gemeinsamem ScanGate, verschachtelte Reste ohne Schleife, Archive aus manuellem Entpacken unberührt),
  `Watch/OwnChangeRegistryTests` (+1: `AddTree`).
- `tests/PhotoViewerV3.Wpf.Tests`: 173 (+5): `Settings/SettingsAutoExtractTests` (Laden, Schalter speichern, Speicherfehler
  setzt zurück, ohne Dienst deaktiviert, Statusleisten-Text).
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler (Einstellung im Rauchtest aus; kein eigener Schritt).
- Hinweis: bei stark ausgelasteter Maschine (mehrere parallele Läufe) schlugen bestehende Fenster-Tests
  (`EarlyCloseWindowTests`, `AppDialogWindowTests`, `GalleryToolbarLayoutTests`, `ArchiveUiTests`-Fenster) gelegentlich mit
  Dispatcher-Timeout fehl; einzeln und im erneuten Gesamtlauf grün.

### Offen
- Einstellung nur global (nicht je Bibliothek); Archive, die vor dem Einschalten schon im Katalog standen, und Archive
  eines Fresh-Scans bleiben manuell. Kein eigener Rauchtest-Schritt für das automatische Entpacken.

## [0.2.24] — 2026-10-20

### Behoben (ADR-0028, offener Punkt aus 0.2.14)
- **„Neu aufbauen“ verliert bei Abbruch oder Absturz keine Benutzerdaten mehr.** Bisher wurde der Katalog vor der
  Enumeration geleert und committet; brach der Lauf danach ab (Abbrechen, Ausnahme, App-Absturz), waren Katalog,
  Favoriten, Bewertungen, Tags und Kategorie-Werte weg. Jetzt bleibt der bisherige Katalog während der Enumeration
  unangetastet und benutzbar; danach laufen Sichern der Benutzerdaten, Löschen der alten Zeilen, Bulk-Insert,
  Zurückschreiben und Suchindex in **einer** Schreibtransaktion (`Services/Scanning/FullScanCatalogWriter`). Abbruch
  oder Fehler an jeder Stelle rollen zurück, ein Absturz hinterlässt eine nie committete Transaktion, die SQLite beim
  nächsten Öffnen verwirft — keine Wiederaufnahme-Logik, keine Schema-Änderung. Der Thumbnail-Cache-Ordner wird erst
  nach dem Commit gelöscht (`ILibraryService.DeleteThumbnailCacheAsync`).
- Kategorie-Werte (0.2.20) wurden bereits übernommen; sie sind jetzt in allen Fehlerfällen mit abgedeckt. Bewertungen
  oder Tags, die während der Enumeration noch vergeben werden, gehen ebenfalls mit über.
- Fresh-Scan nutzt denselben Schreibpfad; der eigene Aufräum-Rollback (`RollbackPartialScan`) entfällt.
- Start-Ids werden unter der Schreibsperre erneut geprüft: Hatte ein Scan einer anderen Bibliothek während der
  Enumeration dieselben Ids vergeben, verwarf `INSERT OR IGNORE` bisher still Dateien und Ordner (im Test: 1 statt 3
  Dateien) — jetzt werden die Zeilen samt ParentId/FolderId nach hinten verschoben.
- `UserDataCarryOver` arbeitet in der Transaktion des Aufrufers statt mit eigener Verbindung; `BulkScanWriter`
  (`InsertFolders`/`InsertFiles`) und `FtsIndexer.PopulateAll` nehmen optional eine offene Transaktion entgegen.
- Bestätigungsdialog und Tooltip „Neu aufbauen“ nennen Kategorien und dass der bisherige Katalog bis zum Abschluss
  bestehen bleibt.
- ADR-0028 (neu), ADR-0023 mit Statusvermerk „Ablauf abgelöst durch ADR-0028“, PLAN Kap. 4 und 12 nachgezogen.

### Tests
- `tests/PhotoViewerV3.Tests`: 434 (+10 `Scanning/ClearAndRescanFailureTests`: Abbruch in jeder Phase — Enumeration,
  direkt nach dem Löschen der alten Zeilen, beim Einfügen, vor dem Suchindex —, Ausnahme in drei Phasen samt
  erfolgreichem Folgelauf, simulierter Absturz vor dem Commit (DB-Dateien in diesem Moment kopiert, geöffnet, alter
  Katalog samt Benutzerdaten unverändert, Folgelauf übernimmt sie), erfolgreicher Neuaufbau mit Kategorien und einer
  Bewertung aus der Enumeration, Scan einer zweiten Bibliothek während der Enumeration vergibt die Start-Ids; jeweils
  FileIds, Suchindex, Thumbnail-Zeilen, Tags und Kategorie-Werte verglichen).
  `Mengen_Messung_synthetischer_Katalog` misst jetzt den ganzen Transaktionsblock: 500 000 Dateien (jede mit Bewertung,
  jede zehnte mit zwei Tags) — Sichern 5,4 s, Zurückschreiben 3,3 s, Löschen + Einfügen + Zurückschreiben + Suchindex
  in einer Transaktion 27,8 s (Rechner parallel mit weiteren Builds belastet).
- `tests/PhotoViewerV3.Wpf.Tests`: 168 (unverändert; Fake von `ILibraryService` um `DeleteThumbnailCacheAsync` ergänzt).
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler (Schritt „Aktualisieren + Neu aufbauen“ prüft Favoriten/Bewertungen nach ClearAndRescan).

### Offen
- Die Schreibtransaktion am Ende von „Neu aufbauen“ ist bei sehr großen Bibliotheken lang (500k: ~30 s); andere
  Schreiber (Bewertung setzen, Tags, Thumbnail-Status) scheitern in dieser Zeit nach `busy_timeout` 5 s mit „database
  is locked“ — die einzelne Aktion schlägt fehl, der Katalog nicht. Denkbar: Bedienung während der Phase „Katalog wird
  geschrieben…“ sperren oder Schreiber mit Wiederholung.

## [0.2.23] — 2026-10-20

### Neu (ADR-0027, Nachtrag 0.2.23)
- **Strg+K — Kategorien zuweisen per Tastatur** in Galerie und Lightbox (auch Rechtsklick → „Kategorien zuweisen…“):
  Dialog mit je Kategorie einer Auswahlliste (Werte, „Kein Wert“, bei unterschiedlichen Werten der Auswahl
  „(gemischt — nicht ändern)“); Tab/↑/↓/Enter/Esc, nur geänderte Kategorien werden geschrieben. Ausgangswerte kommen aus
  der DB, zählen also auch ausgewählte Dateien außerhalb der Seite. Strg+K war in Galerie und Lightbox frei.
- **Lightbox zeigt Kategorie-Werte**: gesetzte Werte in der Titelleiste („XX: N · Projekt: Nord“), im Info-Panel (I)
  alle Kategorien mit Wert bzw. „—“ und Knopf „Zuweisen… (Strg+K)“; folgt Blättern, Zuweisungen und Änderungen auf der
  Seite „Kategorien“. Tastenhilfe nennt Strg+T und Strg+K.
- **Suche findet Kategorie-Werte** (Teilstring ohne Groß-/Kleinschreibung wie Tag-Namen) — per Join
  (`FileCategoryValues.ValueId IN (… CategoryValues.Name LIKE …)`) statt FTS: keine Index-Pflege, Umbenennen/Löschen
  wirkt sofort; der Weg läuft über `IX_FileCategoryValues_Value`. 500 000 Dateien: 33 ms (seltener Wert) bis 320 ms
  (50 000 Treffer).
- **Kategorie-Spalten sortieren die ganze Trefferliste serverseitig** (SQL mit Paging inkl. Reverse-Trick): Werte in der
  Reihenfolge der Verwaltung, erneuter Klick kehrt um, „ohne Wert“ immer am Ende, gleiche Werte nach Datum neu → alt.
  Die Sortier-Auswahl zeigt „XX (Kategorie) ↑/↓“, Sortierpfeil an der Spalte. 500 000 Dateien: Seite 1 ≈ 0,5 s, Mitte
  ≈ 1,25 s (wie „Name A → Z“).

### Tests
- `tests/PhotoViewerV3.Tests`: 425 (+1 `Categories/CategoryServiceTests`: Werte einer Auswahl über die Chunk-Grenze lesen
  und je Kategorie setzen/entfernen).
- `tests/PhotoViewerV3.Wpf.Tests`: 189 (+21): `Categories/GalleryPageSourceCategoryTests` (+13: Suche nach Werten, mit
  Dateiname und Filter, Umbenennen/Löschen ohne Index-Pflege, Abfrageplan ohne `SCAN f`/`SCAN fcv`; Sortierung auf/ab mit
  „ohne Wert“ am Ende, Werte-Reihenfolge der Verwaltung, Seiten inkl. Reverse-Trick, mit Filter/Suche, Primärschlüssel-
  Lookup im Plan), `Categories/CategoryPickerTests` (+8: Ausgangszustand gemeinsam/kein Wert/gemischt, nur Änderungen im
  Ergebnis, echtes Dialogfenster mit Fokus/Enter/Esc, Kopfzeilen-Logik, Strg+K belegt ohne Doppelbelegung in Galerie und
  Lightbox).
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler; Schritt „Kategorien“ erweitert um Strg+K (Galerie, gemischte Auswahl,
  Tastenbindung), Suche nach Wert, Sortierung auf/ab samt Sortierpfeil und Rückkehr zur Standard-Sortierung, Lightbox
  (Titelleiste, Info-Panel, Zuweisen/Entfernen).
- Bekannt, nicht durch diese Änderung: unter paralleler Testausführung und Last scheitern in `PhotoViewerV3.Wpf.Tests`
  sporadisch 2–3 UI-Tests mit Zeitüberschreitung (`AppDialogWindowTests`, `EarlyCloseWindowTests`, `ArchiveUiTests`;
  gemeinsamer UI-Thread). Gegengeprüft: Stand 0.2.20 zeigt dasselbe; seriell
  (`-- xUnit.ParallelizeTestCollections=false`) alle 189 grün.

### Offen
- Tiefe Seiten der Kategorie-Sortierung kosten bei 500 000 Dateien gut eine Sekunde (Top-N-Sortierung wie Name/Größe).
- Der Tag-Zweig der Suche liest alle `FileTags`-Zeilen (Plan `SCAN ft`); dieselbe `IN`-Form wie bei Kategorie-Werten
  wäre bei vielen Tag-Zuweisungen schneller.
- Die Suche findet Kategorie-Werte, nicht Kategorie-Namen.

## [0.2.22] — 2026-10-20

### Neu (offene Punkte aus 0.2.14 und 0.2.17, ADR-0025 Nachtrag)
- **Wartezustände der Scan-Sperre sichtbar**: `ScanGate` meldet über den Messenger, wenn ein Scan oder Datei-Batch auf
  eine belegte Bibliothek warten muss — `ScanGateWaitStartedMessage` / `ScanGateWaitEndedMessage`
  (`Services/Messaging/ScanGateMessages.cs`, `ScanGateWait` mit Wartendem und Belegung Scan/Datei-Batch). Wer die
  Sperre sofort bekommt, meldet nichts. Neu in `IScanGate`: `GetWaits()` (Momentaufnahme) und `CancelWait(id)` — der
  Wartende bekommt eine `ScanGateWaitCanceledException` (ein `OperationCanceledException`), bevor der Batch etwas
  protokolliert oder ändert; die Abbruch-Fortsetzungen laufen nicht auf dem UI-Thread.
- **Statusleiste**: solange eine Datei-Operation (Verschieben, Papierkorb, Ins Archiv, Umbenennen, Ordner
  anlegen/umbenennen, Rückgängig, Sortierer-Lauf) auf einen laufenden Scan derselben Bibliothek wartet, steht links
  „Wartet auf laufenden Scan…“ (Uhr-Symbol, Tooltip) mit **Abbrechen**; danach kurz „Wartende Datei-Operation
  abgebrochen“. Anzeige-Modell `Shell/ScanGateWaitIndicator` (Filter je Verwender, UI-Thread-Dispatch).
- **Bibliotheksseite**: ein manueller Scan, der auf einen anderen Scan oder einen Datei-Batch wartet, zeigt im
  Scan-Panel „Wartet auf laufenden Scan…“ bzw. „Wartet auf laufende Datei-Operation…“ statt „Ordner werden
  durchsucht…“, bis er die Sperre hat (`LibraryViewModel.IsWaitingForGate`). **Abbrechen** während des Wartens meldet
  „Der Scan wurde abgebrochen, bevor er begann; der Katalog ist unverändert.“
- `StatusBarViewModel` und `LibraryViewModel` nehmen optional einen `IMessenger` (Tests); `StatusBarViewModel` bekommt
  `IScanGate` per DI. Die Galerie bleibt unverändert (sie verwirft den Abbruch wie bisher still).

### Tests
- `tests/PhotoViewerV3.Tests`: 432 (+8 `Watch/ScanGateWaitTests`: keine Meldung ohne Warten, Beginn/Ende mit Wartendem
  und Belegung, Scan wartet auf Datei-Batch, `CancelWait` mit eigener Ausnahme ohne Belegung, Abbruch über das Token des
  Aufrufers zählt nicht als Benutzer-Abbruch, unbekannte Id, mehrere Bibliotheken, `MoveAsync` beim Warten abgebrochen —
  nichts verschoben, kein Batch protokolliert, danach normal).
- `tests/PhotoViewerV3.Wpf.Tests`: 175 (+7 `Shell/ScanGateWaitViewModelTests`: Indikator zeigt/abbricht, filtert,
  übernimmt ein schon laufendes Warten; Statusleiste mit Bestätigung; Bibliotheksseite wartet auf Datei-Batch, Abbruch
  während des Wartens, kein Hinweis ohne Konkurrenz) mit `Fixtures/NullService` (DispatchProxy-Leerdienste).
- Rauchtest: neuer Schritt „Wartezustände“ (belegt die Sperre selbst: Statusleiste + Abbrechen, Scan-Panel wartet auf
  Datei-Batch, Screenshots `12-statusleiste-wartet`, `12-bibliothek-wartet`). Rauchtest 22/22 OK, 0 WPF-Binding-Fehler
  (ein erster Lauf scheiterte einmal an „Thumbnails neu erzeugt: 43/42“ im Schritt „Neu aufbauen“ — Zeitverhalten der
  Vorschaubilder, nicht berührt; Wiederholung grün).
- Die neue Wpf-Testklasse läuft in einer eigenen, nicht parallelen Collection (teilt den Test-UI-Thread mit den
  Fenster-Tests). Unter hoher Rechnerlast (sechs Agenten parallel) schlagen die zeitkritischen Fenster-Tests
  (`EarlyCloseWindowTests`, `AppDialogWindowTests` „< 10 s“, `GalleryToolbarLayoutTests`) im parallelen Lauf
  sporadisch fehl — auch ohne die neuen Tests; seriell (`-- xUnit.ParallelizeTestCollections=false`) 175/175 grün.

### Offen
- Die Bibliotheksseite ordnet ein Warten ihrem Scan über Bibliothek und Zeitpunkt zu (erster wartender Scan vor dem
  ersten eigenen Fortschritt): wartet gleichzeitig ein automatischer Scan der Ordnerüberwachung, kann sie den falschen
  erwischen. Abhilfe wäre eine Kennung des Aufrufers in `IScanGate.AcquireAsync`.
- Die Ruhephase beim Umzug der Katalog-DB belegt die Sperre wie ein Datei-Batch; wartet sie, erscheint der Hinweis
  ebenfalls in der Statusleiste (der Umzug läuft ohnehin modal mit eigenem Zeitlimit).
- Eine Datei-Operation, die hinter einer anderen auf den internen Schreib-Lock des `FileOperationService` wartet, zeigt
  keinen Hinweis (nur das Warten auf die Scan-Sperre wird gemeldet).

## [0.2.21] — 2026-10-20

### Behoben (offene Punkte aus 0.2.18, ADR-0026 ergänzt)
- **Kein Schreiben mehr in die alte DB während und nach dem Umzug**: Kontexte der `LibraryContextFactory` tragen einen
  Gate-Interceptor, der bei gesetzter Sperre `SaveChanges`, neue Transaktionen und Non-Query-Befehle mit
  `DatabaseSuspendedException` abweist — auch bei Kontexten, die vor der Sperre erzeugt wurden (Favorit im selben
  Augenblick); Lesen bleibt für sie erlaubt. Zusätzlich hält der Umzug die `BEGIN IMMEDIATE`-Schreibsperre der alten Datei bis zur Rücknahme bzw. nach
  Erfolg bis zum Prozessende; damit scheitern auch rohe Verbindungen, die schon vor der Sperre offen waren, mit
  `SQLITE_BUSY`. Leser (Galerie, Backup-API) arbeiten im WAL-Modus weiter. Der Checkpoint vor der Kopie ist deshalb
  `PASSIVE` (die Backup-API kopiert den WAL-Inhalt ohnehin mit). `DatabaseRelocationService` ist jetzt `IDisposable`
  (gibt die gehaltene Sperre beim Beenden frei).
- **Vorschaubilder nach einer Rücknahme sofort**: während der Pause angeforderte Vorschaubilder werden je FileId
  zurückgestellt (neueste Anforderung gilt, Archive ausgenommen) und von `IThumbnailService.Resume` sofort nachgereicht,
  nicht erst beim nächsten Seitenwechsel.
- **Thumbnail-Ordner nur einmal durchlaufen** (`Relocation/ThumbnailInventory`): die Prüfung erstellt die Dateiliste,
  der Umzug übernimmt sie (ohne vorherige Prüfung: ein eigener Durchlauf) und kopiert danach statt erneut aufzuzählen —
  bisher drei Durchläufe (Prüfung, erneute Prüfung im Umzug, Kopie). Die Prüfung bleibt so streng wie bisher: nach der
  Pause der Thumbnail-Worker werden seither erzeugte Vorschaubilder aus der DB ergänzt (`GeneratedAtUtc` bzw.
  `LastAttemptAtUtc` ab Beginn des Durchlaufs − 2 s) und der Platz mit der endgültigen Liste und frisch gelesenem freiem
  Platz erneut geprüft. „Übernehmen“ durchläuft den Ordner gar nicht. `RelocationTargetCheck.ThumbnailFiles` zählt jetzt
  nur die zu kopierenden Dateien (ohne `.tmp`), wie dokumentiert; der Platzbedarf rechnet `.tmp` weiter mit.
- Annahmen: Vorschaubild-Dateien entstehen nur über den Thumbnail-Dienst (Datei vor DB-Zeile); eine fremde Datei, die
  nach der Prüfung ohne DB-Zeile in den Ordner gelegt wird, zieht nicht mit. Seit der Prüfung entfernte Dateien gelten
  beim Kopieren nicht als „übersprungen“.

### Tests
- `tests/PhotoViewerV3.Tests`: 430 (+6): `Database/DatabaseRelocationServiceTests` (+5: älterer Kontext — `SaveChanges`,
  Non-Query über offene Verbindung und rohe Verbindung — schreibt weder während noch nach dem Umzug in die alte DB;
  nach der Rücknahme speichert derselbe Kontext wieder normal; Kopie nach der Liste der Prüfung mit ergänztem neuem
  Vorschaubild und ohne nachträglich abgelegte fremde Datei; Platz wird nach dem Abgleich erneut geprüft und bricht mit
  Rücknahme ab; Umzug ohne vorherige Prüfung), `Thumbnails/ThumbnailServicePauseTests` (+1: zurückgestellte
  Anforderungen kommen nach `Resume` ohne neue Anforderung, je Datei einmal). Ohne die drei Korrekturen scheitern
  5 der 6 neuen Tests (gegengeprüft; der sechste deckt den Umzug ohne vorherige Prüfung ab). Bestehende Tests: „Neustart“ gibt vorher die gehaltene
  Sperre frei (`Service.Dispose` = Prozessende), `ThumbnailFiles` erwartet 2 statt 3 (ohne `.tmp`).
- `tests/PhotoViewerV3.Wpf.Tests`: 168 (unverändert), grün. Unter Last durch parallele Läufe scheiterte im Standardlauf
  wechselnd je 1 Fenster-Test (`Shell/EarlyCloseWindowTests`, `Shell/AppDialogWindowTests`, UI-Timeout) — ebenso auf
  dem unveränderten Stand 0.2.20 (dort 3); mit `xUnit.ParallelizeTestCollections=false` 168/168.
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler (kein eigener Umzugs-Schritt; Umzug ist durch die Unit-Tests abgedeckt).

## [0.2.20] — 2026-10-20

### Neu (ADR-0027)
- **Kategorien mit festen Werten**: neue Seite **Kategorien** (Navigation unter „Tags“) — Kategorien anlegen (optional
  gleich mit Werten „N, NN“), umbenennen, sortieren, löschen; Werte hinzufügen (mehrere durch Komma), umbenennen,
  sortieren, löschen; Zähler je Wert. Jede Datei hat je Kategorie höchstens einen Wert.
- **Zuweisen** per Rechtsklick auf Kachel oder Zeile: je Kategorie ein Untermenü mit den Werten (Häkchen / „teilweise“
  bezogen auf die Auswahl) und „Kein Wert“.
- **Filter** „Kategorien“ in der Galerie-Kopfzeile (sichtbar, sobald es Kategorien gibt): je Kategorie Werte und
  „ohne Wert“ — innerhalb einer Kategorie ODER, zwischen Kategorien UND; wirkt mit Suche, Tag- und Status-Filter zusammen.
- **Listenspalten**: je Kategorie eine Spalte (Sortierung innerhalb der Seite); ein-/ausblenden und Reihenfolge werden
  gespeichert (`ui.gallery.listColumns`: Token `Kategorie:{Id}` bzw. `!Kategorie:{Id}`), neue Kategorien erscheinen
  automatisch am Ende.
- **Schema** (Migration `AddCategories`): `Categories`, `CategoryValues`, `FileCategoryValues` (PK FileId+CategoryId,
  alle Fremdschlüssel ON DELETE CASCADE); `ICategoryService` (Services/Categories). „Neu aufbauen“ übernimmt die Werte
  wie Tags (`UserDataCarryOver`, ADR-0023).

### Tests
- `tests/PhotoViewerV3.Tests`: 424 (+7 `Categories/CategoryServiceTests`: Anlegen mit Werten, Duplikate/leere Namen,
  ein Wert je Kategorie setzen/ersetzen/entfernen, fremder Wert abgelehnt, Kaskaden beim Löschen von Wert/Kategorie/Datei,
  Umbenennen/Reihenfolge, „Neu aufbauen“ übernimmt die Werte).
- `tests/PhotoViewerV3.Wpf.Tests`: 168 (+13): `Categories/GalleryPageSourceCategoryTests` (6: Wert, ODER, „ohne Wert“,
  UND zwischen Kategorien, mit Suche — gegen echte, migrierte DB), `Categories/CategoryColumnSettingsTests` (7: Spalten-Token,
  Aus-/Einblenden, Reihenfolge, Indexer der Werte, Sortierung über den Indexer-Pfad, Werteingabe zerlegen).
- Rauchtest 21/21 OK (neuer Schritt „Kategorien“: Anlegen, Zuweisen über das Menü-Kommando inkl. Bedienbarkeit, Filter,
  Listenspalte, Verwaltung, Umbenennen, Löschen), 0 WPF-Binding-Fehler.

### Offen
- Kategorie-Werte lassen sich nur über das Rechtsklick-Menü zuweisen (kein Tastenkürzel, keine Anzeige in der Lightbox);
  die Suche findet Kategorie-Werte nicht; Kategorie-Spalten sortieren nur die geladene Seite.

## [0.2.19] — 2026-10-20

### Neu
- **Vorschaugröße in der Galerie einstellbar**: Regler in der Kopfzeile neben dem Umschalter Kachel/Liste (nur in der
  Kachelansicht), außerdem Strg+Mausrad und Strg+Plus/Minus im Kachel-Grid. Kachelbreite 120–320 px in 20er-Stufen
  (Standard 180; 320 = Auflösung der Vorschaubilder), die Höhe wächst mit dem Bildbereich, die Namenszeile bleibt gleich.
  Die Wahl steht in der Settings-Tabelle (`ui.gallery.tileSize`). Beim Umschalten bleibt die oberste sichtbare Kachel
  oben (`VirtualizingTilePanel` verankert den Scroll-Offset am ersten sichtbaren Item).

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 155 (+7): `Gallery/VirtualizingTilePanelTests` (Kachelgröße ändern hält die oberste
  Kachel, 2 Fälle; Begrenzen/Einrasten der Vorschaugröße, 5 Fälle); Kopfzeilen-Layouttest weiter grün (Regler passt bei
  1024 px in die Ansichtszeile).

## [0.2.18] — 2026-10-20

### Neu (ADR-0026)
- **Speicherort des Katalogs wählbar**: Einstellungen → Speicherorte → Datenbank → **Ändern…** zieht `library.db`
  samt Vorschaubildern (`thumbs\`, Herberts Entscheidung: zieht mit) in einen anderen Ordner; **Standardort** holt sie
  zurück. Der Ort steht in `database-location.json` in der App-Wurzel (`DatabaseLocationStore`), `AppDataPaths.Resolve`
  liest sie beim Start; Logs bleiben in der App-Wurzel. Beschädigte Datei oder gewählter Ordner ohne `library.db`
  (Laufwerk abgezogen) → Start bricht mit Meldung ab statt still einen leeren Katalog anzulegen.
- **Sicherer Umzug** (`Services/Database/Relocation/DatabaseRelocationService`): Ziel prüfen (lokal, kein UNC/
  Netzlaufwerk, nicht in einer Bibliothekswurzel oder im Thumbnail-/Log-Ordner, Schreibprobe, Platz inkl. Reserve,
  OneDrive nur mit Warnung) → Ordnerüberwachung stoppen, alle Bibliotheken über `IScanGate` belegen, Thumbnail-Worker
  pausieren (`IThumbnailService.PauseAsync/Resume`), höchstens 30 s warten → Context-Factory sperren
  (`IDatabaseAccessGate`, neue Verbindungen werfen `DatabaseSuspendedException`), `ClearAllPools`, `BEGIN IMMEDIATE` →
  `wal_checkpoint(TRUNCATE)` + SQLite-Backup-API → `integrity_check` und Zeilenzahl je Tabelle → `Thumbnails.CachePath`
  auf den neuen Ordner umschreiben → Vorschaubilder kopieren → neuen Ort atomar speichern. Erst dann gilt er.
- **Vorhandener Katalog im Ziel**: „Übernehmen“ (gültiger Katalog; nichts kopiert, der bisherige bleibt erhalten) oder
  „Ersetzen“ (vorhandene Dateien werden in `….ersetzt-{Zeit}` umbenannt und beim nächsten Start gelöscht).
- **Fehler/Abbruch**: Rücknahme-Stapel räumt das Ziel auf, benennt Beiseitegelegtes zurück, hebt Sperren auf; die App
  arbeitet mit dem alten Ort weiter, `IDialogService` nennt den Grund. Während des Umzugs ist das Fenster gesperrt.
- **Neustart statt Umschalten zur Laufzeit** (`Shell/AppRestartService`): nach Erfolg startet die App neu; der neue
  Prozess löscht alte DB-Dateien und den alten `thumbs\` im Hintergrund (bis zu 10 Versuche, nie etwas vom aktiven Ort).

### Tests
- `tests/PhotoViewerV3.Tests`: 417 (+23): `Database/DatabaseRelocationServiceTests` (14: Umzug mit Prüfung und
  umgeschriebenen Thumbnail-Pfaden, „Neustart“ mit neuem Ort und Aufräumen, zurück zum Standardort, Abbruch beim
  Kopieren und Fehler beim Speichern → vollständige Rücknahme, Scan über die Wartezeit, Warten auf einen Datei-Batch,
  vorhandener Katalog: erkennen/übernehmen/ersetzen/Rücknahme nach dem Beiseitelegen, kaputte Datei, ungeeignete Ziele,
  OneDrive), `Database/DatabaseLocationStoreTests` (8: Persistenz, beschädigte Datei, fehlendes Laufwerk, Aufräumen nie
  am aktiven Ort, gesperrte Datei bleibt vorgemerkt), `Thumbnails/ThumbnailServicePauseTests` (1). Ohne Rücknahme
  scheitern 3 der Relocation-Tests (gegengeprüft).
- `tests/PhotoViewerV3.Wpf.Tests`: 148 (+8 `Settings/SettingsRelocationTests`: Neustart nach Erfolg, Fehlerdialog mit
  weiter gültigem Ort, ungeeignetes Ziel, Übernehmen/Ersetzen/Abbrechen, fremde Datei, OneDrive-Warnung).

### Offen
- Ein EF-Kontext, der vor der Sperre erzeugt wurde und erst danach speichert (Favorit im selben Augenblick), schreibt
  noch in die alte DB; die `BEGIN IMMEDIATE`-Barriere fängt nur laufende Transaktionen ab.
- Während der Pause verworfene Vorschaubild-Anforderungen kommen nach einer Rücknahme erst mit dem nächsten Seitenwechsel.
- Der Thumbnail-Ordner wird für Prüfung und Umzug mehrfach durchlaufen (bei 500k Dateien spürbar).

## [0.2.17] — 2026-10-19

### Behoben (offene Punkte aus 0.2.14, ADR-0025)
- **Scan und Datei-Operation derselben Bibliothek schließen sich aus**: jeder Batch des `FileOperationService`
  (Verschieben, Papierkorb, Umbenennen, Ordner anlegen/umbenennen, Rückgängig) belegt über
  `IScanGate.AcquireForFileOperationAsync` die beteiligten Bibliotheken (Dateien und Move-Ziel; bei Undo die Wurzeln der
  protokollierten Pfade), bevor er den Katalog liest, und gibt sie vor der Rückkehr frei. Ein Scan wartet auf das
  Batch-Ende, ein Batch auf das Scan-Ende (abbrechbar); kein Scan sieht mehr eine verschobene Datei als „fehlt“ + „neu“.
  Die Datei-Variante löst `IScanGate.Acquired` nicht aus. Entpacken (Papierkorb, dann Scan), Sortierer und
  „Ins Archiv…“ halten die Sperre beim Scan-Aufruf nie — kein Deadlock.
- **Kein leerer automatischer Scan nach eigenen Operationen**: neu `Services/Watch/OwnChangeRegistry`
  (`IOwnChangeRegistry`, Singleton). Vor jedem Dateisystem-Schritt trägt der Batch Quelle, Ziel und neu angelegte
  Ordner ein — nur Pfade, deren Katalog er mitbucht (ein Ziel außerhalb der Bibliothek der Datei bleibt fremd). Die
  Ordnerüberwachung verwirft Ereignisse dieser Pfade (bei „Renamed“ nur, wenn beide Pfade eigen sind) bis 10 s nach
  Batch-Ende. Scheitert Dateisystem- oder Katalog-Schritt, wird der Pfad zurückgenommen und zählt nachträglich als
  Ereignis; externe Änderungen an anderen Pfaden zählen weiter, auch mitten im Batch. Der Sortierer trägt gelöschte
  leere Quellordner ebenfalls ein.

### Tests
- `tests/PhotoViewerV3.Tests`: 394 (+16): `Operations/FileOperationScanGateTests` (7: Move wartet auf laufenden Scan,
  Scan wartet auf Papierkorb-Batch und sieht sein Ergebnis, Abbruch beim Warten, Ordner-Operationen/Undo/Umbenennen
  warten, Eingang → Archiv neben parallelen Scans, Entpacken mit Papierkorb + Scan, Sortierer-Lauf + Undo + Scans —
  jeweils ohne Deadlock und mit konsistentem Katalog), `Watch/LibraryWatchServiceTests.OwnChanges` (4: kein Scan nach
  eigenem Verschieben in neue Ordner, nach Papierkorb/Umbenennen/Ordner-Operationen/Verschieben aus der Bibliothek/
  Undo; fremde Datei danach bzw. während des Batches wird erkannt; zurückgenommener Pfad zählt nur unter einer Wurzel),
  `Watch/OwnChangeRegistryTests` (5). Die beiden „kein Scan“-Tests scheitern ohne die Unterdrückung (gegengeprüft).
- `tests/PhotoViewerV3.Wpf.Tests`: 140 (+1 `ArchiveTarget/ArchiveMoveServiceGateTests`: „Ins Archiv…“ wartet auf den
  Scan und gibt die Bibliothek frei).
- Rauchtest 20/20 OK, 0 WPF-Binding-Fehler; die Ordnerüberwachung scannte im ganzen Lauf nur einmal (Schritt
  „Datei kopieren“), nicht mehr nach Verschieben, Papierkorb, „Ins Archiv…“ und Sortierer.

### Offen
- Ein Batch, der auf einen langen Scan wartet, zeigt in der Galerie keinen Hinweis aufs Warten (nur der laufende Scan
  in der Statusleiste; im Log „Datei-Operation wartet auf den laufenden Scan“).

## [0.2.16] — 2026-10-19

### Neu
- **Sortierer und Flatten & Move über alle Wurzeln** (ADR-0020, ADR-0024): Quelle ist jede Wurzel der Bibliothek
  (Eingang oder Archiv) bzw. ein Ordner darunter, Ziel standardmäßig die Archiv-Wurzel (bzw. ein Ordner darunter) oder
  „Quellordner“. Gemeinsame Auswahl `Sorter/Views/SorterLocationView` + `SorterRootSelection` für beide Bereiche:
  ComboBox je Quelle/Ziel mit Rolle „Eingang“/„Archiv“, Name und Pfad, Offline-Wurzeln ausgegraut und nicht wählbar,
  Vorbelegung erster erreichbarer Eingang → Archiv, Hinweis „unter Archiv „…““ bzw. „gesperrt“. Lädt bei
  `ActiveLibraryChangedMessage`/`LibraryRootsChangedMessage` neu, behält gültige Auswahlen und verwirft eine Analyse,
  deren Wurzeln sich geändert haben. Der Bestätigungsdialog nennt den Wechsel „von Eingang … nach Archiv …“.
- Services: `SorterCatalog.RequireLocationAsync` findet die Wurzel (über `LibraryRoots`, längster Pfad) statt
  `Libraries.RootPath`; Analyse und Plan tragen `SourceRoot`, `Roots`, `TargetLibraryRoot`, `CrossesRoots`.
  `IFileSorterService.SetTargetRoot` prüft „Ordner existiert bereits“ gegen die Zielwurzel statt gegen den Quellordner.
  Die Katalog-Nachführung läuft unverändert über `IFileOperationService` (Zielordner-Kette mit `Folders.RootId` der
  Zielwurzel, FileIds stabil, FTS gepflegt, OperationBatch/FileOperation, Rückgängig).

### Geändert
- **Ziele außerhalb aller Wurzeln sind gesperrt** (ADR-0024), ebenso Ziele unter einer Offline-Wurzel: alle
  Plan-Einträge „ungültiges Ziel“ mit Begründung (`SorterPlan.TargetError`), Fehlerleiste statt Warnung, „Sortieren“/
  „Ausführen“ verschiebt nichts. Bisher nur Warnung — die Dateien fielen aus dem Katalog, Rückgängig vergab neue FileIds.
- Beim Aufräumen leerer Quellordner werden alle Wurzeln geschützt (Eingang wie Archiv), nicht nur die Archiv-Wurzel.
- Knopf „Bibliotheksordner“ entfällt (ersetzt durch die Wurzel-Auswahl); `SorterLibraryLocator` liefert die Wurzeln.

### Tests
- `tests/PhotoViewerV3.Tests`: 351 (+7 `Sorter/SorterRootsTests` — Analyse im Eingang, Zielwurzel-Prüfung für
  „Ordner existiert“, Sortieren Eingang → Archiv mit RootId/Zwischenordnern/Status-Filter/FTS/Protokoll/Undo, Flatten
  im Eingang + Undo, Ziel außerhalb gesperrt, Offline-Wurzel gesperrt, `SorterTargetCheck`).
- `tests/PhotoViewerV3.Wpf.Tests`: 126 (+5 `Sorter/SorterRootSelectionTests` — Vorbelegung, ohne Eingang, ohne
  Bibliothek, Synchronisation Auswahl/Pfad mit Sperre, Neuladen über Nachrichten).
- Rauchtest: neuer Schritt „Sortierer: Eingang → Archiv“ vor „Ordnerüberwachung“ (eigener Eingang
  `<Bibliothek>-Sortierer-Eingang`, Vorbelegung, gesperrtes Ziel, Sortieren nach `Archiv\Smoke-Sortiert` mit DB-/FTS-/
  Filter-Prüfung, Rückgängig, Flatten im Eingang + Rückgängig, Eingang entfernen → Auswahl lädt neu); 20/20 OK,
  0 WPF-Binding-Fehler.

## [0.2.15] — 2026-10-19

### Geändert
- `MainWindow`, `ArchiveTargetDialog`, `ExtractArchiveDialog` und `TagPickerDialog` erben von `Shell/Dialogs/AppFluentWindow`
  (offener Punkt aus 0.2.13) — alle Fenster der App sind gegen das frühe Schließen vor dem ersten Zeichnen geschützt.
  CLAUDE.md: Regel für neue Fenster.
- Rauchtest, Schritt „Thumbnails“: wartet zuerst auf die geladene Galerieseite; unter Last war die Seite nach dem
  Fresh-Scan noch leer und der Schritt meldete „0/42“ (Befund aus den Läufen der Agenten zu 0.2.12/0.2.13).

## [0.2.14] — 2026-10-19

### Behoben (Ordnerüberwachung härten, Befunde aus dem Code-Review)
- **Nie zwei Scans derselben Bibliothek gleichzeitig** (Befund 1, bestätigt): `ScanService` hatte keine Sperre;
  „Aktualisieren“/„Neu aufbauen“, Entpacken und der automatische Scan konnten parallel laufen (die Überwachung erkannte
  fremde Scans erst an deren erster `ScanProgressMessage`, die Bibliotheksseite gar nichts). Neu `Services/Scanning/ScanGate`
  (`IScanGate`, ein `SemaphoreSlim` je Bibliothek, Singleton): `ScanService` belegt die Bibliothek vor dem ersten
  Dateisystem-Zugriff, der spätere Scan wartet (abbrechbar; auch ein beim Warten abgebrochener Aufruf sendet seine
  `ScanAbortedMessage`). Die Überwachung hört auf `IScanGate.Acquired`: ein fremder Scan gilt ab Belegung als laufend,
  und die bis dahin gesammelten Ereignisse sind durch ihn abgedeckt (zusammengelegt statt zweimal gescannt); endet er
  nicht erfolgreich, gelten sie wieder.
- **Bibliothekswechsel während eines automatischen Scans** (Befund 2, bestätigt): kam der Abbruch zu spät, wurde das
  Ergebnis der vorigen Bibliothek der neuen zugeordnet — Sperre „angehalten“ bzw. „Automatisch aktualisiert: …“ mit
  falscher Bibliothek. Jetzt Epoche je Bibliothekswechsel; Ergebnis und Sperre eines veralteten Scans werden verworfen,
  die neue Bibliothek zeigt nicht „läuft…“ für den fremden Scan.
- **Ordner mit Punkt im Namen** (Befund 3, bestätigt): `WatchPathFilter` hielt `2024.06 Urlaub` für eine Datei mit
  Endung „.06 Urlaub“ und verwarf Anlegen/Hineinverschieben/Umbenennen/Löschen. Jetzt zählt jeder existierende Ordner;
  gelöschte Pfade gelten als Ordner, wenn ihre „Endung“ keine Dateiendung sein kann (Leerzeichen, nur Ziffern, > 8
  Zeichen). Restlücke dokumentiert (gelöschter Ordner `Fotos.alt`).
- **Speicherreihenfolge der Einstellungen** (Befund 4, bestätigt): `SetQuietPeriodAsync`/`SetEnabledAsync` speicherten
  ohne Reihenfolge; die NumberBox „Ruhezeit“ (UpdateSourceTrigger=PropertyChanged, Aufruf ohne await) konnte einen
  älteren Wert zuletzt schreiben. Jetzt nacheinander (`SemaphoreSlim`), und jeder Speichervorgang schreibt den aktuellen
  Stand statt seines Aufrufwerts.
- **Flut von Statusnachrichten** (Befund 5, bestätigt): jedes relevante Dateiereignis schickte eine
  `WatchStatusChangedMessage` (beim Kopieren Tausender Dateien Tausende Dispatcher-Aufträge mit Normal-Priorität).
  Jetzt gehen reine Countdown-Verschiebungen höchstens alle 500 ms hinaus (die letzte nachlaufend), Zustandswechsel
  sofort; gesendet wird immer der jüngste Zustand, nie ein älterer nach einem jüngeren. Die Statusleiste bündelt
  zusätzlich (höchstens ein ausstehender Auftrag, Background-Priorität).

### Geprüft, nicht umgesetzt
- Eigene Datei-Operationen (Verschieben, „Ins Archiv…“, Papierkorb) lösen hinterher weiterhin einen (entprellten)
  automatischen Scan aus, der „keine Änderungen“ findet. Sauber unterdrücken ließe sich das nur in
  `FileOperationService` (Pfade der laufenden `OperationBatch` melden bzw. die Bibliothek für den Batch belegen); ein
  pauschales Ignorieren während eines Batches verlöre echte externe Änderungen und Teilfehler (Kopie am Ziel, Quelle
  nicht gelöscht). Entpacken profitiert bereits vom Zusammenlegen (es scannt am Ende selbst).

### Tests
- `tests/PhotoViewerV3.Tests`: 366 (+22): `Watch/ScanGateTests` (3, echter ScanService: zweiter Scan wartet, Abbruch
  beim Warten), `Watch/LibraryWatchServiceTests.Hardening` (8: fremder Scan vor seiner ersten Fortschrittsmeldung,
  Zusammenlegen, Abbruch gibt Ereignisse zurück, Bibliothekswechsel ×2, Ordner „2024.06 Urlaub“ hinein/hinaus,
  Speicherreihenfolge, Nachrichtenrate), `Watch/WatchPathFilterTests` (+11). Jeder Befund-Test scheitert mit dem alten
  Verhalten (gegengeprüft durch gezieltes Zurückbauen). `tests/PhotoViewerV3.Wpf.Tests`: 121.
- Rauchtest 19/19 OK, 0 WPF-Binding-Fehler.

### Offen
- Scan und Datei-Operation derselben Bibliothek sind nicht gegeneinander gesperrt (Single-Writer gilt nur je Dienst): ein
  Scan, der den Katalog liest, während ein Batch Dateien verschiebt, kann eine verschobene Datei als „fehlt“ + „neu“
  sehen. Kandidat: `FileOperationService` belegt für die Dauer eines Batches dasselbe `IScanGate`.
- Ein wartender manueller Scan zeigt auf der Bibliotheksseite bis zum Start „Ordner werden durchsucht…“ ohne Hinweis aufs
  Warten.

## [0.2.13] — 2026-10-19

### Geändert
- **Galerie-Kopfzeile** als eigenes Control `Gallery/Controls/GalleryToolbar` (vorher direkt in `GalleryView.xaml`),
  sauber von 1024 bis 2560 px Fensterbreite und in der schmalen rechten Hälfte der Ordner-Seite:
  - Typ-Filter als Segmentgruppe im Fluent-Stil (Alle | Bilder | Videos | Favoriten | Archive, mit Symbolen wie im
    Mockup), Status-Filter **Nicht archiviert / Archiviert** im neuen Dropdown **Status**
    (`GalleryStatusFilterDropDown`, aktiver Wert in Akzentfarbe). Bindungen und Commands unverändert (`Filter` über
    `EnumEquals`, Werte `GalleryFilter.*`).
  - Neues Layout-Panel `ToolbarFlowPanel`: bricht um statt überzulaufen; die Suche zählt für den Umbruch mit ihrer
    Mindestbreite (360 px) und wächst bis 640 px — passt sie nicht mehr neben die Filter, rutscht sie in eine eigene
    Zeile. Zweite Zeile: Sortierung, Seitengröße links; Kachel/Liste, Rückgängig, Aktualisieren rechtsbündig.
  - Suchfeld: Platzhalter „Name, Endung oder Tag…“ (die Suche findet seit 0.2.10 auch Tag-Namen), „Leeren“ als
    Symbolknopf ✕; der eingebaute Leeren-Knopf des Eingabefelds ist aus (er leerte nur den Text, ohne neu zu laden).
- Neue Basisklasse `Shell/Dialogs/AppFluentWindow` für Lightbox und `AppDialogWindow`.

### Behoben
- Erledigt (offener Punkt aus 0.2.11): Wurde ein Fenster mit WPF-UI-TitleBar nach `Loaded`, aber vor dem ersten
  Zeichnen geschlossen (Rauchtest: Lightbox öffnen und binnen ~100 ms schließen), warf
  `TitleBar.OnWindowContentRendered` „Hwnd mit dem Wert Null ist ungültig“ in den globalen Handler (FTL im Log).
  Ursache: WPF stellt `ContentRendered` mit Input-Priorität ein und bricht es beim Schließen nicht ab.
  `AppFluentWindow.OnContentRendered` verwirft das Ereignis eines geschlossenen Fensters — zentral für Lightbox und
  `AppDialogWindow`, ohne Verzögerung des Schließens und ohne Reflection.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 134 (+13) — `Shell/EarlyCloseWindowTests` (Kontrolle: reines FluentWindow mit
  TitleBar wirft weiterhin; AppFluentWindow, echte Lightbox und modaler AppDialogWindow schließen vor dem Zeichnen
  ohne Ausnahme), `Gallery/GalleryToolbarLayoutTests` (echtes `GalleryToolbar` bei 1024/1280/1920/2560 px, mit
  Ordner-Chip + aktivem Status, Ordner-Seite: nichts ragt über den Rand, Suche ≥ Mindestbreite, Platzhalter passt,
  Zeilenzahl; `ToolbarFlowPanel` direkt). `tests/PhotoViewerV3.Tests`: 344.
- Belege: `docs/spikes/07-galerie-kopfzeile.md` mit Bildern bei 1024, 1280 (auch mit Ordner-Chip) und 1920 px.
- Rauchtest: 19/19 OK, 0 WPF-Binding-Fehler, kein FTL/„Hwnd“ mehr im Log (Lightbox öffnen/schließen). Ein Lauf davor
  scheiterte einmalig an „Thumbnails 0/42“ (Thumbnail-Dienst startete erst nach der Prüfung, ohne Bezug zu dieser
  Änderung); Wiederholung grün.

### Offen
- Weitere FluentWindows mit TitleBar (`MainWindow`, `ArchiveTargetDialog`, `ExtractArchiveDialog`, `TagPickerDialog`)
  erben noch von `FluentWindow`; sie werden nur durch Benutzeraktion nach dem Zeichnen geschlossen. Umstellen = eine
  Zeile je XAML/Code-Behind auf `AppFluentWindow`.

## [0.2.12] — 2026-10-19

### Behoben
- **„Neu aufbauen“ übernimmt Favoriten, Bewertungen und Tags** (ADR-0023): `Services/Scanning/UserDataCarryOver`
  sichert vor dem Leeren alle Dateien mit Favorit, Bewertung oder Tag in TEMP-Tabellen einer eigenen Verbindung und
  schreibt sie nach dem Bulk-Insert (vor dem FTS-Aufbau, eine Schreibtransaktion) auf die neuen FileIds zurück —
  mengenbasiert per INSERT…SELECT / UPDATE…FROM. Schlüssel wie beim Incremental-Scan (ADR-0019): voller Ordnerpfad +
  Dateiname ohne Groß-/Kleinschreibung (Unicode), damit über alle Wurzeln eindeutig; keine Prüfung von
  Größe/Änderungszeit (eine nachbearbeitete Datei behält ihre Daten wie beim Aktualisieren). Dateien, die nach dem
  Neuscan nicht mehr am selben Pfad liegen, verlieren ihre Daten; die Tabelle `Tags` bleibt unberührt, Thumbnails und
  Suchindex werden wie bisher neu aufgebaut. Ein Fehler beim Zurückschreiben rollt den Scan wie jeder Persistenzfehler
  zurück. `ScanService` ruft die Klasse mit wenigen Zeilen im ClearAndRescan-Pfad auf und protokolliert beide Schritte.
- Bestätigungsdialog und Tooltip „Neu aufbauen“ (Bibliotheksseite) nennen jetzt, was erhalten bleibt.
- ADR-0019 (Punkt „Neu aufbauen“) und ADR-0021 (Konsequenz „Tags gehen verloren“) mit Statusvermerk auf ADR-0023.

### Messung
- Synthetischer Katalog, schlechtester Fall (jede Datei mit Bewertung, jede zweite Favorit, jede zehnte zwei Tags,
  jede hundertste in anderer Schreibweise zurück): **500 000 Dateien — Sichern 1,9 s, Zurückschreiben 2,6–3,9 s**;
  200 000 Dateien — 0,8 s / 1,0 s (`PV3_CARRYOVER_ROWS=500000 dotnet test … --filter Mengen_Messung`). Ein Filter auf
  `Files.LibraryId` in der Zuordnung lenkte den Planer auf `IX_Files_Library_Folder` (7,6 s bei 200k) — entfernt.

### Tests
- `tests/PhotoViewerV3.Tests`: +5 `Scanning/ClearAndRescanUserDataTests` (zwei Wurzeln + gleichnamige Dateien in
  verschiedenen Ordnern, entfernte Datei verliert ihre Daten und Tags bleiben, Umbenennung nur in
  Groß-/Kleinschreibung, FTS-/Thumbnail-Konsistenz, Mengen-Messung mit 20 000 Dateien).
- Rauchtest, Schritt „Aktualisieren + Neu aufbauen“: prüft, dass Favoriten und Bewertungen (Pfad + Werte) nach
  ClearAndRescan identisch sind (statt bisher „Favoriten verworfen“).

### Offen
- Abbruch während der Enumeration von „Neu aufbauen“ hinterlässt wie bisher einen leeren Katalog — jetzt samt
  verlorener Benutzerdaten (die Sicherung lebt nur in der Verbindung). Besser: erst nach der Enumeration sichern und
  leeren (Start-Ids vorher lesen); betrifft die Reihenfolge in `ScanService.RunFullScanAsync`.

## [0.2.11] — 2026-10-19

### Geändert
- Listenansicht, Spalte „Status“: zeigt jetzt „fehlt“, „Eingang“ oder „Archiv“ (Rolle der Wurzel, ADR-0020) statt nur
  „fehlt“; sortierbar. `GalleryItem.IsArchived`/`StatusText`, Platzhalter-Konverter entfernt.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 121 (+7 `ListView/GalleryItemStatusTests`); `tests/PhotoViewerV3.Tests`: 344.
- Rauchtest des integrierten Stands (0.2.7–0.2.10): 19/19 OK, 0 WPF-Binding-Fehler.

### Offen
- WPF-UI 4: Wird ein Fenster mit TitleBar geschlossen, bevor es einmal gezeichnet wurde (Rauchtest öffnet und schließt
  die Lightbox binnen ~100 ms), wirft `TitleBar.OnWindowContentRendered` „Hwnd mit dem Wert Null ist ungültig“. Der
  globale Handler fängt das ab, die App läuft weiter; Abhilfe wäre, das Schließen bis `ContentRendered` zu verzögern.
- Rauchtest-Startskript: robocopy kopierte im App-Container der Claude-Desktop-App teils 0 Dateien — dort Copy-Item nutzen.

## [0.2.10] — 2026-10-19

### Neu
- **Tags** (ADR-0021): frei konfigurierbare Schlagworte in eigenen Tabellen `Tags` (Name eindeutig ohne
  Groß-/Kleinschreibung, Farbe aus fester 12er-Palette, Reihenfolge) und `FileTags` (FileId + TagId, Index auf TagId,
  beide Fremdschlüssel ON DELETE CASCADE — Papierkorb, „Neu aufbauen“ und Tag löschen räumen die Zuweisungen in der DB
  mit auf). Migration `AddTags` (strikt, Bestandsdaten unverändert).
- `Services/Tags`: `ITagService` (anlegen, umbenennen, färben, sortieren, löschen; zuweisen/entfernen für Id-Listen in
  Chunks zu 500; Tags je Datei als Batch für eine Seite; Zähler je Tag; `RemoveForFilesAsync` als Hilfsmethode),
  `TagsChangedMessage` für Stammdaten, `FilesChangedMessage(Metadata)` nach Zuweisungen. Registrierung `AddTags()`.
- Galerie: Knopf **Tags** in der Aktionsleiste und Kontextmenü auf Kacheln/Zeilen (Häkchen = alle ausgewählten Dateien
  haben das Tag, „(teilweise)“ = ein Teil; Rechtsklick auf eine nicht ausgewählte Kachel wählt nur sie), **Strg+T**
  öffnet die Tag-Auswahl (`Gallery/Tags/TagPickerDialog`, dreistufige Häkchen, „Neues Tag…“), Tag-Punkte auf der Kachel
  (Hover = Namen), Dropdown **Tags** neben den Filter-Pills (ODER / UND / „nur Dateien ohne Tag“, mit Zählern). Die
  Suche findet zusätzlich Dateien, deren Tag-Name den Suchtext enthält (FTS-Treffer UNION Tag-Treffer, per
  `CROSS JOIN` an Files gebunden — die Treffer treiben die Abfrage wie bisher die FTS-Tabelle; keine FTS-Spalte).
- Lightbox: Tags der Datei in der Titelleiste, **Strg+T** bearbeitet sie (eigene Datei `LightboxViewModel.Tags.cs`).
- Seite **Tags** in der Navigation (`Settings/TagManagement`): Liste mit Zählern, Neu, Umbenennen, Farbe aus der
  Palette, Reihenfolge, Löschen mit Bestätigung und Zahl der betroffenen Dateien.
- **Listenansicht** (`Gallery/ListView`): Umschalter Kachel-Grid/Liste in der Kopfzeile (gespeichert als
  `ui.gallery.viewMode`), DataGrid über dieselbe Seite mit den Spalten Name (mit Vorschaubild), Ordner, Geändert,
  Größe, Typ, Bewertung, Favorit, Tags und Status (Platzhalter, zeigt bislang nur „fehlt“). Spalten über das
  Kopfzeilen-Kontextmenü ein-/ausblendbar und per Ziehen umsortierbar (`ui.gallery.listColumns`), Klick auf
  Name/Geändert/Größe sortiert die ganze Trefferliste über die Galerie-Sortierung, die übrigen Spalten nur die geladene
  Seite. Mehrfachauswahl, Doppelklick/Enter → Lightbox, Entf, Esc, Strg+A, Strg+T und Aktionsleiste wie im Grid;
  Zeilen-Virtualisierung mit Recycling; im UI-Automation-Baum sind nur realisierte Zeilen sichtbar, als Blätter
  (`GalleryDataGrid`/`GalleryDataGridRow`, eigene Peers statt `DataGridAutomationPeer`). Die Liste wird erst beim
  ersten Umschalten erzeugt — der Start im Kachel-Modus bleibt unverändert schnell.

### Integration
- Zusammengeführt mit 0.2.7–0.2.9. Migration `AddTags` neu erzeugt (`20261019105649_AddTags`, inhaltlich unverändert),
  damit sie nach `AddLibraryRoots` läuft; ModelSnapshot neu.
- Ein gemeinsames Kontextmenü für Kachel und Listenzeile (im Code-Behind gebaut): Archiv „Inhalt anzeigen“/„Entpacken…“,
  „Ins Archiv…“, „Im Explorer anzeigen“, danach das Tag-Menü. Behebt in 0.2.9 das doppelte ContextMenu-Setter-Paar,
  bei dem das Archiv-Menü „Ins Archiv…“ überdeckte.
- Rauchtest-Reihenfolge: … → Archiv (ZIP) → Eingang → Archiv → Tags + Listenansicht → Ordnerüberwachung.

### Tests
- `tests/PhotoViewerV3.Tests`: 256 (+15 `Tags/TagServiceTests` — Anlegen/Validierung, Duplikate ohne
  Groß-/Kleinschreibung, Nachrichten, Zuweisen > 500 in Chunks, Löschen, ON DELETE CASCADE beim Entfernen der Datei und
  beim Papierkorb, leere Listen, Empfänger-Ausnahmen, Palette).
- `tests/PhotoViewerV3.Wpf.Tests`: 97 (+46 — `ListView/GalleryListColumnsTests` Spalten-/Sortier-Logik und Setting,
  `ListView/GalleryDataGridAutomationTests` Blatt-Peers, `Tags/TagPickerLogicTests`, `Tags/GalleryPageSourceTagTests`
  Filter ODER/UND/ohne Tag und Tag-Suche gegen eine migrierte DB, `Tags/TagManagementViewModelTests`).
- Rauchtest-Szenario: neuer Schritt „Tags (Zuweisen, Filter, Suche, Verwaltung) + Listenansicht“ (Tag-Menü, Strg+T
  über einen Rauchtest-Ersatz des Dialogs, Filter, Suche, Listenansicht mit Setting/Spalte/Sortierung, Lightbox-Tags,
  Verwaltungsseite, Umbenennen, Löschen); 16/16 OK, 0 WPF-Binding-Fehler.

### Offen
- Spalte „Status“ der Listenansicht ist ein Platzhalter (TODO in `ListViewConverters.cs`/`GalleryListView.xaml`): den
  Eingangs-/Archiv-Status liefert das Bibliotheksmodul.
- Tag-Zuweisungen hängen an FileIds: „Neu aufbauen“ verliert sie wie Favoriten und Bewertungen.

## [0.2.9] — 2026-10-19

### Neu
- **Eingang und Archiv in einer Bibliothek** (ADR-0020): Tabelle `LibraryRoots` (Id, LibraryId, Path, Role, Name,
  SortOrder) mit genau einer Archiv-Wurzel und beliebig vielen Eingangs-Wurzeln, `Folders.RootId`. Migration
  `AddLibraryRoots` übernimmt jede `Libraries.RootPath` als Archiv-Wurzel und setzt `RootId` bestehender Ordner per
  Pfad-Präfix; `RootPath` bleibt als Anzeigefeld der Archiv-Wurzel. `LibraryInfo.Roots` (+ `ArchiveRoot`,
  `InboxRoots`, `HasOfflineRoot`), `ILibraryService.GetRootsAsync/AddRootAsync/RenameRootAsync/SetRootRoleAsync/
  RemoveRootAsync` (Entfernen nur aus dem Katalog), `LibraryRootsChangedMessage`.
- Scanner über alle Wurzeln: Fresh/ClearAndRescan nur, wenn alle Wurzeln erreichbar sind; Incremental je Wurzel mit
  eigenem Offline- und Mass-Missing-Guard (Offline-Wurzel wird übersprungen, Ergebnis `LibraryOffline` mit ihrem Pfad),
  Ordnerkette je Wurzel, `ScanResult.RootsScanned`.
- `FileOperationService`: „innerhalb der Bibliothek“ = unter irgendeiner Wurzel; Zielordner-Kette unter der passenden
  Wurzel, Undo entsprechend. Neu `CreateFolderAsync` (MkDir-Batch) und `RenameFolderAsync` (RenameFolder-Batch: Ordner-
  plus je Datei eine Operation, `Directory.Move`, Folders-Pfade samt Unterordnern, rückgängig machbar; Wurzeln nicht),
  `FoldersChangedMessage`.
- Bibliotheksseite: Wurzeln je Bibliothek mit Rolle und Erreichbarkeit, „Eingang hinzufügen…“ (mit Angebot zu
  aktualisieren), zur Archiv-Wurzel machen, Anzeigename ändern, Eingang entfernen (nur Katalog).
- Galerie: Filter-Pills „Nicht archiviert“ / „Archiviert“ (SQL über `Folders.RootId → LibraryRoots.Role`, Count-Query
  mitgezogen), Badge „Eingang“ auf der Kachel, Aktion **Ins Archiv…** (Aktionsleiste, Strg+M, Kontextmenü der Kachel,
  Lightbox Strg+M bzw. Overlay-Knopf über `IArchiveMoveService`). Dialog `Gallery/ArchiveTarget/ArchiveTargetDialog`: Ordnerbaum
  der Archiv-Wurzel (Katalog- und Dateisystem-Ordner), Suche, Neuer Ordner, Umbenennen, Vorschlag „Jahr\Jahr-Monat“ aus
  EXIF-Aufnahme- bzw. Dateidatum, zuletzt benutzter Zielordner vorgemerkt (Setting `archive.lastTarget.{libraryId}`).
  Die Ansicht bleibt nach dem Verschieben beim Filter.
- Ordnerbaum: Wurzeln als oberste Knoten mit Rolle (Archiv zuerst), Kontextmenü „Neuer Ordner…“ / „Umbenennen…“.
- Rauchtest: Schritt „Eingang → Archiv“ (zweite Wurzel, Incremental über 2 Wurzeln, Status-Filter + Badge, Ins Archiv mit
  Fake-Zieldialog, Rückgängig, Ordner anlegen/umbenennen, Eingang entfernen) — 16 OK.

### Integration
- Zusammengeführt mit Ordnerüberwachung (0.2.7) und Archiven (0.2.8): Der Dienst für „Ins Archiv…“ heißt
  `IArchiveMoveService`/`ArchiveMoveService` (Ordner `Wpf/Gallery/ArchiveTarget`), damit er nicht mit dem
  ZIP/RAR-Dienst `Services/Archive/IArchiveService` kollidiert; Filter-Pills Alle / Bilder / Videos / Favoriten /
  Archive / Nicht archiviert / Archiviert.
- Ordnerüberwachung beobachtet jetzt alle Wurzeln der Bibliothek (`LibraryWatchService.GetRootPaths` über
  `LibraryInfo.Roots`) und baut ihre Watcher bei `LibraryRootsChangedMessage` neu auf; Tests `WatchRootPathsTests`.
- Rauchtest-Reihenfolge: … → Archiv (ZIP) → Eingang → Archiv → Ordnerüberwachung (Überwachung bleibt bis zum
  letzten Schritt aus).

### Tests
- `PhotoViewerV3.Tests`: Migration auf einer Alt-DB, Wurzelverwaltung, Scanner über zwei Wurzeln (Fresh, Incremental,
  Offline-Wurzel), Move Eingang → Archiv mit Undo, Ordner anlegen/umbenennen (257 Tests).
- `PhotoViewerV3.Wpf.Tests`: Status-Filter im `GalleryPageSource` (auch mit Suche/Ordnerfilter), Archiv-Dialog-ViewModel
  (Baum, Vorauswahl, Suche, Vorschlag, Neuer Ordner, Umbenennen), Datumsvorschlag, echtes Dialogfenster (63 Tests).

### Offen
- Sortieren/Flatten arbeiten weiter nur unter der Archiv-Wurzel (`Libraries.RootPath`); Eingänge als Quelle folgen.
- Dateisystem-Ordner ohne Katalogzeile lassen sich im Archiv-Dialog anlegen/auswählen, aber nicht umbenennen.

## [0.2.8] — 2026-10-19

### Hinzugefügt
- **Archive im Katalog** (ADR-0022): `MediaType.Archive = 2`; das Formatregister führt `.zip .rar .7z .tar .gz .tgz .bz2 .xz`
  als dritte Liste, der Scanner nimmt Archive automatisch auf (Files-Zeile, Ordnerkette, FTS). Neu `IMediaFormatRegistry.IsMedia`
  (nur Foto/Video) — Sortierer und Flatten zählen Archive nicht als Mediendateien. Kein Thumbnail: `ThumbnailService`
  reiht Archive nicht ein; die Kachel zeigt Archiv-Symbol, Endung (Badge) und Größe. Filter-Pill „Archive“.
- **`IArchiveService`** (`Services/Archive`, `AddArchives()`): `ListAsync` (Einträge mit Pfad, Größe, komprimiert, Datum,
  Medien-/Archiv-Markierung, Verschlüsselung) und `ExtractAsync` mit Zielordner (Standard: Unterordner mit dem Archivnamen
  neben dem Archiv, `x.tar.gz` → `x`), vorhandene Dateien überspringen/überschreiben/umbenennen, nur Medien oder alles,
  verschachtelte Archive im Runden-System bis Tiefe 3 (V2-Vorbild, Zwischen-Archive werden danach entfernt),
  Zip-Slip-Schutz, Abbruch, Fortschritt (Dateien/Bytes), atomares Schreiben über `.pv3part`. Danach Incremental-Scan der
  Bibliothek und optional Archiv in den Papierkorb (`IFileOperationService.RecycleAsync`, nur ohne Fehler).
  Passwortgeschützte Archive: `ArchiveEncryptedException` mit klarer Meldung (kein Passwort-Dialog); beschädigte:
  `ArchiveFormatException`. Lesen über **SharpCompress 0.50.6** (neu, nur im Services-Projekt), ZIP-Fallback über
  System.IO.Compression.
- **Entpacken-Dialog** (`Wpf/Gallery/Archives`, Muster `AppDialogWindow`): Ziel, Optionen, Fortschritt mit Abbrechen,
  Ergebnis mit Fehlerliste; mehrere Archive nacheinander, Scan einmal nach dem letzten. Aufruf über die Aktionsleiste
  („Entpacken…“ für ausgewählte Archive), das neue Kachel-Kontextmenü („Inhalt anzeigen“, „Entpacken…“, „Im Explorer
  anzeigen“) und die Lightbox.
- **Lightbox-Inhaltsansicht** (`Lightbox/ArchiveContentView`): Enter/Doppelklick auf ein Archiv zeigt statt eines Bildes die
  Liste der Einträge (Größe, komprimiert, Datum, Symbol je Typ) mit „Entpacken…“ (über `ExtractArchiveRequestedMessage` an
  die Galerie) und „Im Explorer anzeigen“; Info-Panel „Typ: Archiv“ mit Datei-/Medienzählern.
- Rauchtest-Schritt „Archiv: ZIP → Kachel → Inhaltsansicht → Entpacken → Katalog“ (ZIP mit Unterordner, Textdatei und
  verschachteltem ZIP in „Unsortiert“; Screenshots 11–13).

### Behoben (während der Umsetzung gefunden)
- SharpCompress `ExtractAllEntries()` funktioniert nur für solide RAR und 7z; ZIP lief deshalb unbemerkt immer über den
  Fallback, TAR und nicht-solide RAR schlugen fehl. Jetzt Eintrag für Eintrag, `ExtractAllEntries` nur für solide Archive/7z.
  Der ZIP-Fallback greift nur noch, solange kein Eintrag verarbeitet wurde (keine doppelten Dateien).
- SharpCompress 1.0.0 ist auf NuGet zurückgezogen (unlisted) und lieferte PAX-Header in TAR als Dateien mit gekürzten Namen;
  umgestellt auf die neueste gelistete Version 0.50.6 (`OpenArchive`/`OpenReader`), TAR-Verwaltungseinträge werden
  zusätzlich übersprungen.

### Tests
- `tests/PhotoViewerV3.Tests`: 281 (+40, `Archive/`): Register-Klassifikation, Scanner nimmt Archive auf, ThumbnailService
  reiht sie nicht ein, `ListAsync`/`ExtractAsync` mit erzeugten ZIPs (Unterordner, Zip-Slip, verschachtelt bis Tiefe 3 und
  Tiefen-Grenze, Überspringen/Überschreiben/Umbenennen, nur Medien, Abbruch ohne Teildateien, verschlüsseltes und
  kaputtes ZIP), GZ, TAR, TAR.GZ, TAR mit 140 Zeichen langem PAX-Namen, Papierkorb + Incremental-Scan nach dem Entpacken.
  RAR (normal, solide, passwortgeschützt, verschlüsselte Köpfe) und 7z über eingecheckte Mini-Archive
  `Archive/TestData/` (erzeugt mit WinRAR bzw. Windows-bsdtar).
- `tests/PhotoViewerV3.Wpf.Tests`: 56 (+5, `Archive/`): Kachel-Daten, Inhaltszeile, Dialog-ViewModel (mehrere Archive,
  eigener Ordner, Fehlerliste), echtes Dialogfenster.
- Rauchtest-Szenario: 17/17 OK (nach dem Merge mit der Ordnerüberwachung 0.2.7), 0 WPF-Binding-Fehler.

### Offen
- „Archive: automatisch entpacken beim Scan“ (Einstellung, Standard aus) — der Service ist dafür vorbereitet.
- „Überschreiben“ ersetzt vorhandene Dateien direkt (kein Papierkorb, kein FileOperation-Eintrag).
- Mehrere Archive im Dialog: scheitert das letzte Archiv komplett, entfällt der abschließende Scan (Dateien erscheinen
  erst nach „Aktualisieren“).

## [0.2.7] — 2026-10-19

### Hinzugefügt
- **Ordnerüberwachung mit automatischem Diff-Scan** (`Services/Watch`: `ILibraryWatchService`/`LibraryWatchService`,
  `WatchPathFilter`, `WatchSettingsStore`, `AddWatch()`; ADR-0019 nannte den FileSystemWatcher bereits als möglichen
  Auslöser für „Aktualisieren“): ein `FileSystemWatcher` je Wurzel der aktiven Bibliothek (rekursiv, Created/Deleted/
  Renamed/Changed, 64 KB Puffer; die Wurzelliste ist in `GetRootPaths` gekapselt — für Bibliotheken mit mehreren
  Wurzeln nur dort erweitern). Relevant sind nur Medienformate aus dem Formatregister; `.tmp`/`.part`/`~`-Dateien,
  `Thumbs.db`, `.thumbnails`, `@eaDir`, Papierkorb und der eigene Thumbnail-Cache werden ignoriert, Ordner zählen bei
  Anlegen/Löschen/Umbenennen. Entprellung: Ereignisse werden gezählt, jedes startet die **Ruhezeit** (Standard 3 s)
  neu; erst danach läuft ein Incremental-Scan über `IScanService` — nie parallel zu einem laufenden Scan (eigener oder
  manueller, erkannt an den Scan-Nachrichten), gesammelte Ereignisse werden nach dessen Ende erneut geprüft. Endet der
  automatische Scan mit `MassMissingBlocked` oder `LibraryOffline`, wird nicht automatisch wiederholt (Zustand
  `Blocked` mit Hinweis), bis ein manueller Scan erfolgreich war, die Bibliothek wechselt oder die Einstellung
  umgeschaltet wird. Nicht erreichbare Wurzeln werden alle 30 s erneut versucht; Watcher-Fehler (Pufferüberlauf) →
  Neustart + Scan. Wechsel per `ActiveLibraryChangedMessage`; Start in `App.OnStartup`, Stop über Dispose des
  Singletons beim Beenden. Zustände gehen als `WatchStatusChangedMessage` an den Messenger.
- **Einstellungen**: Abschnitt „Ordnerüberwachung“ mit Schalter „Bibliotheksordner überwachen“ (Standard: an) und
  Ruhezeit in Sekunden (1–60, NumberBox); Settings-Tabelle `watch.enabled`/`watch.quietSeconds`, sofort wirksam.
- **Statusleiste**: Hinweis „Änderungen erkannt — Aktualisierung in n s“ (Countdown), „Automatische Aktualisierung
  läuft…“, „Automatisch aktualisiert: n neu · …“ (12 s) bzw. „Automatische Aktualisierung angehalten: …“ (Warnsymbol).
- README: Abschnitt „Ordnerüberwachung“.

### Tests
- `tests/PhotoViewerV3.Tests`: +30 (`Watch/LibraryWatchServiceTests` mit echtem FileSystemWatcher auf dem Temp-Ordner der
  `TempLibraryFixture` und Scan-Fake: Entprellung mehrerer Ereignisse → ein Scan, Anlegen/Löschen → je ein Scan, kein
  Doppelstart während des eigenen oder eines manuellen Scans, Temp-/Thumbnail-/Nicht-Medien-Dateien ignoriert, Stop,
  Schalter und Ruhezeit (Persistenz, Clamp), Mass-Missing-/Offline-Sperre und Aufhebung, nicht erreichbare Wurzel mit
  Wiederholung, Bibliothekswechsel; `Watch/WatchPathFilterTests`; Settings-Roundtrip).
- Rauchtest: neuer Schritt „Ordnerüberwachung: Datei kopieren → automatischer Diff-Scan → Galerie“ (Ruhezeit 1 s;
  Kopie → Countdown in der Statusleiste → Incremental Added 1 → Kachel sichtbar; Löschen → MarkedMissing 1 →
  „fehlt“-Badge). Die Überwachung ist in den übrigen Schritten ausgeschaltet, damit kein automatischer Scan parallel
  zu den manuellen Scans und Datei-Operationen läuft. 16/16 OK.

### Offen
- Datei-Operationen der App selbst (Verschieben, Papierkorb, Sortierer) lösen über den Watcher nach der Ruhezeit einen
  Incremental-Scan aus, der in der Regel „keine Änderungen“ findet — bei sehr großen Bibliotheken auf HDD ein
  vollständiger Baumlauf. Nächster Schritt wäre, eigene Operationen (OperationBatch) im Watcher auszublenden.
- Restrisiko Doppel-Scan: ein manueller Scan, der genau im Fenster zwischen Ablauf der Ruhezeit und seiner ersten
  `ScanProgressMessage` startet, wird nicht erkannt (SQLite-Single-Writer bleibt, der Katalog wäre doppelt
  verglichen).

## [0.2.5] — 2026-10-19

### Tests
- WPF-Testfenster (`AppDialogWindowTests`, `GalleryGridHost`) liegen bei −5000/−5000 und werden nicht aktiviert — sie
  erschienen sonst während der Testläufe auf dem Desktop (Dialog „Ungewöhnlich viele Dateien fehlen … 142.381“).

## [0.2.4] — 2026-10-19

### Geändert
- **Galerie-Grid UI-virtualisiert** (`Gallery/Controls/VirtualizingTilePanel`, Folgearbeit aus 0.2.1): eigenes
  `VirtualizingPanel` mit `IScrollInfo` — feste Kachelgröße 180×160, Spaltenzahl aus der Viewport-Breite,
  pixelbasiertes Scrollen (Mausrad 48 px je Rastung wie bisher, Bild↑/↓ = Viewport, Zeile 16 px), Container nur für die
  sichtbaren Zeilen plus eine Pufferzeile oben/unten über den `ItemContainerGenerator` (Recycling), `BringIndexIntoView`/
  `MakeVisible` für `ScrollIntoView` und Tastaturnavigation, der fokussierte Container bleibt realisiert. Kein
  horizontales Scrollen. `GalleryView.xaml`: `CanContentScroll=True`, `IsVirtualizing=True`, `VirtualizationMode=Recycling`.
- `GalleryListBox` gleicht die Auswahl zwischen Items (ViewModel, maßgeblich) und ListBox auch für Kacheln ohne Container ab:
  Umschalt-Bereich/Strg+A der ListBox erreichen alle Items (`OnSelectionChanged`); vom ViewModel aufgehobene Auswahl wird
  vor der Container-Erzeugung in die ListBox übernommen, damit `Selector.AdjustNewContainers` sie beim Zurückscrollen nicht
  wiederbelebt. Enter-Fallback in der View nimmt das erste ausgewählte Item statt `SelectedItem`.
- Thumbnails sichtbare Kacheln zuerst: das Panel meldet den sichtbaren Index-Bereich (`VisibleRangeChanged`, gebubbelt),
  `GalleryViewModel.ReportVisibleRange`; die Seitenanforderung wartet den Layoutdurchlauf ab und reiht nach Abstand zum
  sichtbaren Bereich ein (Queue des ThumbnailService bleibt FIFO).
- Diagnose-Logzeile „Kachel-Grid sichtbar: n Kacheln, m Container realisiert, x ms nach Seitenänderung“ (`GalleryView`).

### Messung (Debug-Build, i7-8700K)
- Harness `tests/PhotoViewerV3.Wpf.Tests` (ListBox 900×600, 1000 Kacheln, kachelähnliche Vorlage): WrapPanel 0.2.2 →
  Layout 3 267 ms, 1000 Container, 1001 UI-Automation-Elemente; VirtualizingTilePanel → Layout 50 ms, 20 Container,
  21 UI-Automation-Elemente.
- Echte App, Bibliothek mit 1242 Dateien, Seitengröße 1000: Grid nach Seitenänderung sichtbar nach 14–186 ms (20 Container,
  5 Spalten; erste Seite nach App-Start 420–470 ms inkl. erstmaliger Vorlagen), Seitenwechsel ≈ 200 ms (dominiert von den
  ~2000 Einzeländerungen an `CurrentPage` in `ApplyRows`, nicht vom Panel). UI-Automation-Elemente im Hauptfenster 89
  (0.2.1: 569 bei 500 Kacheln), Antwortzeit des UI-Threads mit lauschendem Automation-Client max 9–17 ms.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 50 (+14: `Gallery/VirtualizingTilePanelTests` — realisierte Container ≤ 60, Scrollen,
  ScrollIntoView, Reset, Spaltenumbruch, Mausrad/Seite, sichtbarer Bereich, Insert/Remove/Move/Replace, Auswahl in beide
  Richtungen inkl. Recycling, UI-Automation-Baum < 100, Messung WrapPanel gegen Panel, Thumbnail-Reihenfolge).
  Neue Fixture `Fixtures/GalleryGridHost` (echtes Fenster mit `GalleryListBox`).
- Rauchtest-Szenario: 15/15 OK, 0 WPF-Binding-Fehler.

### Offen
- Seitenwechsel bei 1000 Kacheln ≈ 200 ms durch Einzel-Remove/-Insert in `ApplyRows` (ObservableCollection); Bündelung wäre
  der nächste Schritt, falls spürbar.

## [0.2.3] — 2026-10-19

### Behoben
- **Lightbox zeigte von großen Fotos nur die linke obere Ecke in Fit-Größe:** Das Bildelement lag direkt im
  Bühnen-Grid; WPF schnitt ein Element, das größer als die Bühne ist, erst auf den Layout-Platz zu und
  verkleinerte es dann per RenderTransform. Das Bild liegt jetzt auf einem Canvas (keine Layout-Begrenzung),
  die Bühne schneidet selbst ab. Regressionstest mit 4000×2250-Bild und Treffertest am rechten Rand
  (`tests/PhotoViewerV3.Wpf.Tests/Lightbox/ZoomPanControlTests.cs`). In den Rauchtests waren alle Bilder
  kleiner als das Fenster, deshalb blieb es unentdeckt.
- Galerie-Kacheln melden im UI-Automation-Baum den Dateinamen statt des Typnamens.

## [0.2.2] — 2026-10-19

### Geändert
- `CLAUDE.md`: Regeln „keine Wpf.Ui MessageBox“ und „Galerie-Kacheln als Blatt-Peers“ aus den Befunden von 0.2.1.

## [0.2.1] — 2026-10-19

### Behoben
- **App hing beim Anlegen einer Bibliothek** (Namensabfrage ohne Knöpfe, CPU voll, Speicher wuchs): Die Wpf.Ui
  MessageBox 4.0.2 berechnet ihre Größe in `OnLoaded` selbst und gerät mit umbrechendem Text plus Eingabefeld in
  eine Layout-Schleife. Alle Dialoge (Shell, Galerie, Lightbox) laufen jetzt über ein eigenes `AppDialogWindow`
  (feste Breite, Höhe aus dem Inhalt, Primär/Sekundär/Schließen, Eingabefeld, Detailzeilen, Enter/Esc).
  Regressionstests öffnen den Dialog real (`tests/PhotoViewerV3.Wpf.Tests/Shell/AppDialogWindowTests.cs`).
- **UI-Thread blockierte dauerhaft, sobald ein UI-Automation-Client lauschte** (Visual Studio Live Visual Tree,
  Automationsskripte, Bildschirmleser): WPF aktualisiert dann nach jedem Layout den gesamten Peer-Baum
  (`AutomationPeer.UpdateSubtree`), bei 500 nicht virtualisierten Kacheln mit je rund zehn Elementen und einem
  Layout je eintreffendem Vorschaubild ohne Ende. Gegenmaßnahmen: Kacheln sind im Automation-Baum Blätter
  (`GalleryListBox`/`GalleryListBoxItem`, 569 statt mehrerer tausend Elemente), Vorschaubilder werden gebündelt mit
  Background-Priorität in die Galerie übernommen, Thumbnail-Decoder laufen mit höchstens sechs Threads in
  niedriger Priorität. Messung mit lauschendem Client: Antwortzeit des UI-Threads maximal 10 ms, Vorschaubilder
  einer Bibliothek mit 1558 Fotos nach rund 5 s fertig.
- EF-Core-SQL-Befehle werden nicht mehr auf Information-Niveau protokolliert (vorher tausende Zeilen je Sitzung).

### Offen
- Beim Start aus Visual Studio (Debugger) schienen keine Logzeilen in `%LocalAppData%\PhotoViewerV3\logs` zu
  entstehen — geklärt (0.2.6): die App loggte korrekt; der Prüfprozess lief in einem App-Container, dessen Sicht auf
  `%LocalAppData%` virtualisiert ist, und sah deshalb nur sein eigenes Verzeichnis.
- Galerie ohne UI-Virtualisierung: Erstaufbau von 500 Kacheln kostet im Debug-Build einige Sekunden; eine
  virtualisierende Kachelanordnung bleibt Folgearbeit.

## [0.2.0] — 2026-10-19

Release-Stand nach drei Wellen (Services → Lightbox/Galerie/Bibliothek/Shell → Sortierer → Aufräumen und Doku).
Konsolidiert die Vorabversionen `0.2.0-services` … `0.2.0-sorter`; siehe auch [README.md](README.md) und die neuen
ADRs [0016](docs/adr/0016-formatregister.md)–[0019](docs/adr/0019-incremental-scan.md).

### Hinzugefügt
- **Bibliothek** (Seite „Bibliothek“, `Wpf/Library`, `Services/Library`): Bibliothek per Ordnerdialog anlegen, Fresh-Scan,
  „Aktualisieren“ = Incremental-Scan mit stabilen FileIds (neu / geändert / fehlend / wieder vorhanden; Favoriten und
  Bewertungen bleiben), Missing-Markierung statt Löschen, Mass-Missing-Guard (> 10 000 oder > 20 % → Dialog
  [Erneut prüfen] [Als fehlend markieren] [Abbrechen]), Offline-Dialog ([Erneut prüfen] [Andere Bibliothek wählen]),
  „Neu aufbauen“ (ClearAndRescan nach Bestätigung), aktivieren, umbenennen, entfernen, Fortschrittspanel mit Abbruch
  und Zusammenfassung.
- **Galerie** (`Wpf/Gallery`): echte Vorschaubilder via WIC mit EXIF-Orientierung 1–8 und Alpha-Flattening, FFmpeg für
  Videos und als Fallback für WebP/HEIC/AVIF/RAW ohne Windows-Codec; sechs Sortierungen (Datum, Name, Größe je auf-
  und absteigend), Seitengröße 200/500/1000, FTS-Suche mit Query-Builder (keine Syntaxfehler bei `- : ( ) " *`),
  Filter Alle/Bilder/Videos/Favoriten, Ordnerfilter (direkt/rekursiv) mit Chip, Container-Kacheln für Ordner ohne
  direkte Dateien, Favorit-Herz und 5-Sterne-Bewertung je Kachel, Mehrfachauswahl, schwebende Aktionsleiste
  (Favorit, Bewertung, Verschieben nach…, Papierkorb, Auswahl aufheben) mit Rückgängig, Tastatur (Strg+←/→,
  Strg+Pos1/Ende, F5, Entf, Esc, Strg+A, Enter), Leerzustände, „fehlt“-Badge.
- **Lightbox** (`Wpf/Lightbox`): eigenes FluentWindow mit Auto-Hide-Toolbar; Bilder mit Zoom/Pan (Mausrad am Cursor,
  Ziehen, Doppelklick Fit ↔ 100 %), EXIF-Drehung, Decode im Hintergrund mit Prefetch ±1, Info-Panel; Videos via
  LibVLC mit Transportleiste, Lautstärke/Stumm, Codec/Dauer/Auflösung; Tastatur (←/→, Pos1/Ende, Bild↑/↓ = ±10,
  F/F11, I, Leertaste, M, +/−, 1, 0, Strg+D, Strg+0…5, Entf, Esc); Favorit/Bewertung/Papierkorb, „Im Explorer
  anzeigen“, „Mit Standardprogramm öffnen“.
- **Ordnerbaum-Seite** (`Wpf/Folders`): Baum der aktiven Bibliothek mit Dateizählern, Auswahl → Galerie rechts
  (mit/ohne Unterordner), „Gesamte Bibliothek“; lädt nach Scans und Datei-Operationen gebündelt neu und stellt
  Aufklapp-Zustand und Auswahl wieder her.
- **Einstellungen** (`Wpf/Settings`): Speicherorte (Datenbank, Protokolle, Thumbnail-Cache mit Größe und „Cache
  leeren“), FFmpeg-Status mit winget-Befehl, unterstützte Formate aus dem Formatregister, Farbschema
  Hell/Dunkel/System (Settings-Tabelle `ui.theme`), Version und Build-Datum.
- **Statusleiste** (ADR-0013, vier Indikatoren): Scan-Fortschritt und -Ergebnis (Incremental mit Diff-Zählern,
  „Bestätigung nötig“ beim Mass-Missing-Guard, „Bibliothek offline“), Thumbnail-Warteschlange, Hintergrund-Aktivität,
  Recovery-Hinweis für unvollständige/fehlgeschlagene OperationBatches.
- **Sortieren** (`Wpf/Sorter`, `Services/Sorter`, aus V2 portiert): File Sorter mit Pattern-Engine (Regex, Priorität,
  Capture-Gruppe 0/n, Kleinschreibung, `_unsorted`), Multi-Level (Sub-Level-Patterns auf dem vollen Dateinamen),
  Profile in der Settings-Tabelle (`sorter.patterns`, `sorter.sublevels`, V2-Defaults, Validierung), Import/Export im
  V2-JSON-Format, Gruppentabelle mit editierbaren Zielordnern und Zusammenführen, Vorschau, Ausführung über
  protokollierte Datei-Operationen, „Rückgängig (letzter Lauf)“; Flatten & Move nach V2-Regel (deduplizierte
  Pfad-Ebenen, `_root`, Separator, Dedup-Dateinamen `_1`/`_2` als protokolliertes Rename, optional direkt in die
  Zielwurzel, nur wirklich leere Quellordner löschen) mit Undo.
- **Services**: `FileOperationService` (Papierkorb via `SHFileOperation` ohne Shell-UI und mit Schutz vor stillem
  Hard-Delete auf Netz-/UNC-/Wechseldatenträgern, Move/Rename/Undo, Protokoll in `OperationBatches`/`FileOperations`,
  Folders-Kette, FTS- und Thumbnail-Bereinigung, `FilesChangedMessage`), `CatalogUpdateService` (Favorit/Bewertung
  set-basiert in 500er-Chunks), `LibraryService` (anlegen/aktivieren/umbenennen/leeren/entfernen, Root-Check),
  `IncrementalScanRunner` mit `RootAvailabilityProbe` und `MassMissingGuard`, `MediaFormatRegistry` (ADR-0016),
  `ThumbnailService`-Härtung (Zustand je Dateiversion, `.tmp`-Bereinigung, Retry-Cooldown bei äußeren Ursachen,
  `Invalidate`/`InvalidateAll`), FFmpeg-Wrapper-Fixes (ArgumentList-Quoting, stdout wird geleert, abbrechbares
  WaitForExit, `ffmpeg_timeout`, zweiter Lauf ohne `-ss` bei Clips unter 1 s), `FtsQueryBuilder`,
  Nachrichten-Records in `Services/Messaging` (ADR-0017).
- **Werkzeuge**: `CLAUDE.md` mit Skill- und Code-Profil; Rauchtest-Szenario (`Shell/Smoke`, 15 Schritte mit
  DB-/Dateisystem-Prüfungen und Screenshots) per Umgebungsvariablen `PHOTOVIEWERV3_SMOKE_ROOT/_SHOTS/_EXIT/_CLEANUP`;
  isoliertes AppData per `PHOTOVIEWERV3_APPDATA`; unbehandelte Ausnahmen und WPF-Binding-Fehler (Debug/Rauchtest) im
  Serilog-Log; neues Testprojekt `tests/PhotoViewerV3.Wpf.Tests`.
- **Doku**: `README.md`, ADR-0016 bis ADR-0019, `PLAN.md` auf Stand 0.2.0, Spike-Dokus 02/03 mit Hinweis auf den
  produktiven Code.

### Geändert
- Spike-Code aufgeräumt: `Wpf/Spike02` → `Wpf/Gallery` (Namespace `PhotoViewerV3.Gallery.*`, Registrierung in
  `AddGallery()`), `Wpf/Spike03` entfernt (produktiver Nachfolger `Wpf/Lightbox`).
- `IDialogService` um `ChooseAsync` (Drei-Knopf-Dialog, `DialogChoice`) erweitert; `ILibraryDialogService`,
  `LibraryDialogService` und `AddLibraryModule` entfallen.
- Galerie-Suche nutzt `FtsQueryBuilder.FromUserInput` aus `Data/RawSql` (eine Implementierung im Repo); Suchtext ohne
  suchbaren Inhalt (z. B. `---`) wird mit Hinweis in der Statuszeile ignoriert (Feld geleert) statt als leere Phrase
  gesucht.
- `GalleryViewModel` invalidiert den Thumbnail-Zustand bei `FilesChangedMessage` (Removed/Added: betroffene Ids) und
  `ScanCompletedMessage` (alles — neue Ids nach Neuaufbau, geänderte/wieder vorhandene Dateien auf jeder Page);
  „Cache leeren“ in den Einstellungen wirkt ohne Neustart (`InvalidateAll`, fehlgeschlagene Vorschaubilder werden
  erneut versucht), Dialogtext entsprechend.
- `ScanAbortedMessage.LibraryOffline`; Statusleiste unterscheidet „Scan abgebrochen“, „Bibliothek offline“ und Fehler.
- Rauchtest: der zweite Scan ist „Aktualisieren“ (Incremental ohne Änderung, mit neuer und fehlender Datei, wieder
  vorhanden), der Neuaufbau läuft über `LibraryViewModel.RunSmokeRebuildAsync`; weiterhin 15/15.
- NuGet: transitive Pins `SQLitePCLRaw.*` 2.1.14 und `System.Security.Cryptography.Xml`/`.Pkcs` 10.0.13 — Build ohne
  NU1902/NU1903.
- `ScanService`: fortlaufende Ids über alle Bibliotheken, vollständige Ordnerkette, gedrosselte
  Fortschrittsnachrichten, Rollback bei Abbruch; `ScanResult` mit `Mode`, `Outcome` und Diff-Zählern,
  `IScanService`-Overload mit `ScanOptions`.
- Shell: NavigationView mit `TargetPageType` + DI-PageProvider, `INavigationService`, Theme beim Start;
  `LightboxViewModel` per `ActivatorUtilities` (kein Container-Leak); Galerie-Thumbnail-Callback liest nur einen
  unveränderlichen Id-Schnappschuss; App-Start mit Fang, `OnExit` synchron.
- `CLAUDE.md`: Regel „Seiten-Namespaces dürfen keine Entitätsnamen verdecken“.

### Behoben
- FTS-Index wurde nie befüllt (`INSERT … NOT IN` las aus der External-Content-Tabelle) → `'rebuild'`; Zählung über
  `FilesFts_docsize`.
- Video-Thumbnails scheiterten am Muxer (die `.tmp`-Endung bestimmt kein Format) → `-f image2 -c:v mjpeg`.
- DI wählte für `IMediaFormatRegistry` den Konstruktor mit zwei leeren Listen → Factory-Registrierung; der Scanner fand
  in der App keine Dateien.
- Scanner-Ids kollidierten bei einer zweiten Bibliothek (Start bei 1) → `MAX(Id)`/`sqlite_sequence`.
- Ordnerkette war unvollständig (Zwischenordner fehlten) → Scanner legt die volle Kette an; verwaiste Ordnerzeilen aus
  Altkatalogen hängt `FileOperationService` an nachgetragene Elternordner.
- Namespace-Kollision: `PhotoViewerV3.Library` verdeckte die Entität `Library` → `PhotoViewerV3.Libraries`.
- WIC: Hochkant-Bilder (längere Seite = Zielgröße), EXIF-Orientierung, Alpha auf dunklem Hintergrund, kein
  Hochskalieren; WPF-UI-Icon außerhalb der BMP (U+F02B2) nicht mehr verwendet.
- `FileOperationService`: Katalog-Buchung nach dem Dateisystem-Schritt nicht mehr abbrechbar, Item-Status `Applying`
  vor `File.Move`, Undo findet die Zeile über den aktuellen Pfad (sicher bei Rescan/Id-Wiederverwendung), reservierte
  Gerätenamen abgelehnt, FTS-`delete` mit Rebuild-Fallback; `CatalogUpdateService` sendet Nachrichten auch nach
  Teilfehlern.

### Tests
- `tests/PhotoViewerV3.Tests`: 241 (Scanner inkl. Incremental und Guard, Datei-Operationen und Härtung, Katalog,
  Thumbnails, FFmpeg-Fallback, FTS-Builder gegen echtes FTS5, Sorter/Flatten, Profile).
- `tests/PhotoViewerV3.Wpf.Tests`: 33 (WIC-Renderer, `GalleryQuery`/FTS, Converter, Binding-Listener).
- Rauchtest-Szenario: 15/15 OK, 0 WPF-Binding-Fehler.

## [0.1.6] — 2026-10-18

### Hinzugefügt
- `IMediaFormatRegistry` / `MediaFormatRegistry` (Services/Media): gängige Foto- und Videoformate statt fester jpg/png/mp4-Liste; Scanner klassifiziert darüber.
- Schnittstellen-Verträge für die nächsten Slices: `ILibraryService`, `IFileOperationService`, `ICatalogUpdateService`, Nachrichten-Records in `Services/Messaging` (WeakReferenceMessenger), `ILightboxService` (Wpf).
- `ScanMode.ClearAndRescan` und `ScanMode.Incremental` (Implementierung folgt).
- `IThumbnailService.PendingCount` für die Statusleiste.
- `CLAUDE.md` mit Skill- und Code-Profil, dieses Changelog.

## [0.1.5] — 2026-10-18

### Hinzugefügt
- Thumbnail-Pipeline: `ThumbnailService` mit getrennten Worker-Pools (WIC für Bilder, FFmpeg für Videos), DB-Status-Lifecycle, atomic `.tmp`+Move; echte Vorschaubilder im Gallery-Grid.

## [0.1.4] — 2026-06-30
- Gallery arbeitet auf dem produktiven `LibraryContext` statt der Spike-DB.

## [0.1.3] — 2026-05-11
- Doku: Real-Welt-Scan-Validation (770 Dateien, 17 GB).

## [0.1.2] — 2026-05-11
- Fix: FTS-Count via SELECT; CLI-Test `spike-v01-scan`.

## [0.1.1] — 2026-05-11
- App-Start nutzt `AddPhotoViewerServices` + `DatabaseInitializer`.

## [0.1.0] — 2026-05-11
- Scanner-Slice: `FolderEnumerator` + `BulkScanWriter` + `FtsIndexer`.

## [0.0.8] — 2026-05-11
- Entities, `LibraryContext`, Migration `InitialV01` (V0.1-Schema).

## [0.0.2] – [0.0.7] — 2026-05-11
- Sechs Spikes grün: SQLite-Bulk/FTS5 (500k), Virtual Grid, LibVLCSharp-Lightbox, WIC + File-Locks, FFmpeg-Cancel, Storage-Detection.

## [0.0.1] — 2026-05-10
- Initial: Plan, 15 ADRs, Mockups, Tag-1-Skeleton.

## Offen
- SelectionStore über Seitengrenzen hinweg (ADR-0010: Auswahl nach FileId über Pages; heute nur innerhalb der aktuellen Page).
- Index für die Namenssortierung (`Files(LibraryId, Name COLLATE NOCASE)`) und COLLATE-NOCASE-Indizes für den Incremental-Abgleich (Ordnerpfad, Name) — bei 500k messen (Spike-Harness).
- Recovery-Dialog für unfertige OperationBatches beim Start (ADR-0012: Fortsetzen / Reparieren) — heute nur Hinweis in der Statusleiste und Undo.
- Thumbnail-Queue mit vier Prioritäten und Background-Batching für 500k (ADR-0007; heute zwei Worker-Pools mit Dedupe) sowie Storage-Profile (ADR-0008) produktiv anbinden.
- Tags/Alben (V0.4), Plan-Ordner (V0.3), Gesichtserkennung (V0.5, ADR-0015), Animated Hover (ADR-0014), Archive Auto-Extract, pHash-Duplikate.
- Portable Auslieferung per `dotnet publish` (PLAN Kap. 1) und Migration-Backup-Dialog (PLAN Kap. 14).
- Umbenannte/verschobene Dateien erkennt der Incremental-Scan als „neu + fehlend“ (keine Hash-Identität, ADR-0019).
