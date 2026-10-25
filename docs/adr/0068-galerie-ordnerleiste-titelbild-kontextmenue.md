# ADR-0068: Galerie mit Ordnerleiste, Titelbild überall, neues Rechtsklick-Menü, Kopieren nach außen

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Herbert Schrotter (Abnahme Mockup 17 und Mockup 18 am 2026-10-25, Issues #15, #23, #24, #25), Claude
  (Umsetzung, Detailentscheidungen ohne Rückfrage)
- **Bezogen auf:** [ADR-0012](0012-operation-logging.md) (Protokoll, Rückgängig), [ADR-0037](0037-gesichter-zuordnen-personen-seite.md)
  (Personen), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich), [ADR-0049](0049-versteckte-bibliotheken.md)
  (versteckte Bibliothek), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (Ziehen: verknüpfen/verschieben),
  [ADR-0057](0057-foto-guete-und-highlights.md) (Foto-Güte, Highlight-Auswahl), [ADR-0065](0065-ordner-loeschen-und-fehlende-entfernen.md),
  [ADR-0066](0066-ausschluesse-und-ordner-verschieben.md) (Ordner-Aktionen), [ADR-0067](0067-aktivitaets-anzeige.md)
  (Aktivitäts-Anzeige), Mockups `mockups/17-personen-kontextmenue.html` und `mockups/18-galerie-ordnerleiste.html`, PLAN.md Kap. 7

## Kontext

Bis 0.4.37 gab es eine eigene Seite „Ordner“ (Baum links, Galerie rechts) neben der Seite „Galerie“; wer nach Ordnern
filtern wollte, musste die Seite wechseln. Das Rechtsklick-Menü der Kacheln war über die Jahre zu einer langen, flachen
Liste gewachsen, Titelbilder ließen sich nur für Alben und Storys an je eigener Stelle setzen, Personen nur über ein Gesicht in
der Personen-Ansicht, Ordner gar nicht. Personen-Kacheln schnitten Namen ab. Fotos ließen sich nicht in andere Programme
ziehen oder per Strg+C kopieren (#25). Im Ordnerbaum waren zwei Auswahl-Markierungen zu sehen (#23).

## Entscheidung

### A) Galerie mit Ordnerleiste (Mockup 18, #24, #23)

- Der Ordnerbaum lebt als **ein-/ausklappbare Leiste links in der Galerie** (`Folders/FolderPane`, `Gallery/Views/GalleryPage`);
  **Strg+B** oder die Taste im Kopf klappt sie zu/auf, der Zustand wird in der Settings-Tabelle gemerkt
  (`ui.gallery.folderPane`). Der Menüpunkt „Ordner“ entfällt; Sprünge auf die alte Seite (`NavigationTags.Folders`) öffnen
  die Galerie mit offener Leiste.
- **Nichts geht verloren:** Baum mit allen Aktionen aus 0.4.30–0.4.35, Suche, „Unterordner einschließen“, Eingang hinzufügen,
  Ausschlüsse-Hinweis mit „verwalten“, Ergebnis-/Rückgängig-Feld, Tag-/Kategorie-Zahlen und die Alben ziehen in die Leiste um.
  „Gesamte Bibliothek“ heißt jetzt „Alle Fotos der Bibliothek“.
- Ein Ordner wirkt als **Filter-Chip „Ordner: …“**, kombinierbar mit allen übrigen Filtern; darüber ein **Pfad (Breadcrumb)**,
  Klick auf ein Glied wählt den Elternordner und zieht die Leiste nach; das Kreuz am Chip hebt den Ordner auf (Nachricht
  `FolderSelectedMessage` mit „kein Ordner“ an die Leiste).
- Ordner-Menü neu: „Nur diesen Ordner zeigen“, „Diashow“, „Neuer Unterordner…“, „Titelbild des Ordners ▸“, ein Untermenü
  „Alle Fotos taggen / kategorisieren“ (Tags, Kategorie, Bewertung, Favorit, Datum für alle), „Neu einlesen“ nur für diesen
  Ordner samt Unterordnern (`ScanOptions.SubtreePath`, Incremental-Lauf nur im Teilbaum, der Rest der Bibliothek gilt nicht als
  fehlend). Ziehen bleibt wie in ADR-0054: ohne Taste verknüpfen, Umschalt verschieben.
- **#23:** Nur eine Auswahl-Markierung — der eigene Akzent-Balken bleibt, die schmale WPF-UI-Marke (Template-Teil
  `ActiveRectangle`) blendet die angehängte Eigenschaft `FolderTreeItemChrome.HideSelectionIndicator` aus.

### B) Titelbild überall (Mockup 17, #15)

- `Services/Covers/ICoverService`: Ziele **Person, Album, Story, Ordner** für ein Foto (`GetTargetsAsync`), setzen, wieder
  automatisch. „Als Titelbild für ▸“ steht im Kachel-, Listen- und Lightbox-Menü und zeigt nur Ziele, zu denen das Foto gehört
  (Personen im Foto, Alben mit dem Foto, Storys mit dem Foto, sein Ordner), mit Häkchen am aktuellen Titelbild.
- **Person:** setzt `CoverFaceId` auf **das Gesicht dieser Person im Foto**. Automatisch (ohne eigene Wahl) gewinnt das beste
  Gesicht nach `FaceCoverScoring`: 0,75 × Gesichtsgüte × (0,6 + 0,4 × Frontalität aus den Landmarken) + 0,25 × Foto-Güte
  (ADR-0057, ohne Messung 0,5) — als SQLite-Funktion `lumora_face_cover_score` direkt in der Personen-Abfrage.
- **Album/Story/Ordner:** automatisch über die Highlight-Auswahl (`IHighlightSelector.PickCoverAsync`, bevorzugt Fotos mit
  Personen) unter den 400 neuesten Fotos; intelligente Alben bleiben beim neuesten Foto. **Eigene Wahl gewinnt immer.**
- **Ordner-Titelbild** als Spalte `Folders.CoverFileId` (Migration `AddFolderCover`, nur `ADD COLUMN`, **ohne FK**, damit
  SQLite die Tabelle nicht neu aufbaut). Gültig nur, solange die Datei noch im Ordner liegt und nicht fehlt — sonst gilt die
  automatische Wahl (Prüfung beim Lesen statt Aufräumen bei jeder Datei-Operation). „Neu aufbauen“ trägt es über den Ordnerpfad
  mit (`UserDataCarryOver`).
- **Personen-Kacheln:** Name zweizeilig ohne Abschneiden, Beziehung, Chips „✓ n“ (grün, bestätigt) und „n Vorschläge“ (gelb,
  öffnet den Reiter Vorschläge), Symbol für automatisches Titelbild, Tooltip; in den Details Umschalter „Automatisch / Selbst
  gewählt“.

### C) Neues Rechtsklick-Menü (Mockup 17)

- Ein gemeinsamer Aufbau `PhotoMenu/PhotoContextMenuBuilder` mit `PhotoMenuHost` je Ort (Galerie-Kachel, Liste, Lightbox,
  Story), Aktionen in `PhotoMenu/PhotoActions`. Gliederung: Öffnen/Diashow (Archiv-Einträge oben) · Bewerten/Favorit/Tags/
  Kategorie · Zu Album ▸ / Als Story · Als Titelbild für ▸ · Datum/Ort · Drehen · Suchen ▸ (gleicher Tag, gleicher Ort,
  Doppelte, gleiche Personen, ähnliche Motive) · Datei ▸ (Kopieren, Kopieren nach…, Exportieren (verkleinert)…, Im Explorer,
  Verschieben, Umbenennen) · Papierkorb · Eigenschaften (Alt+Enter). Untermenüs werden erst beim Öffnen gefüllt.
- **Mehrfachauswahl:** Aktionen für eines (Titelbild, Eigenschaften, Gesichter zeigen) nur bei genau einem Foto; die übrigen
  wirken auf alle; Drehen nur, wenn alle drehbar sind.
- **Drehen:** nur JPEG; schreibt die EXIF-Orientierung in die Datei (`JpegExifOrientationWriter` über die GPS-Schreib-
  Infrastruktur `GeoFileWriter`, alte Bytes als `GeoTagUndo`, Rückgängig prüft die Prüfsumme), setzt den Änderungszeitpunkt
  ≥ 1 s weiter, tauscht Breite/Höhe, dreht Gesichtsrahmen und Landmarken im Katalog, verwirft Vorschaubilder und
  Gesichtsausschnitte; Batch-Typ `Rotate`, Rückgängig über die Aktivitäts-Anzeige und die Galerie-Taste. Andere Formate:
  Eintrag deaktiviert mit Tooltip.
- **Kopieren nach…:** `IFileOperationService.CopyAsync` als `OperationBatch`/`FileOperation` vom Typ `Copy` mit freiem Namen
  „Name (2).ext“; innerhalb einer Bibliothek entsteht eine neue Katalogzeile (nur Maße/Dauer/Codec übernommen, **keine**
  Bewertung/Tags/Personen), außerhalb keine. Rückgängig legt die Kopie in den Papierkorb und entfernt die Zeile.
- **Exportieren (verkleinert)…:** JPEG mit wählbarer langer Kante (1024–3840) in einen Ordner **außerhalb** der Bibliothek,
  Videos/fehlende Dateien/Archive übersprungen, als Vorgang in der Aktivitäts-Anzeige.
- **Eigenschaften:** eigenes Fenster (`AppFluentWindow`) mit Datei-, Aufnahme-, Kamera-, Ort- und Katalogangaben.

### D) Fotos in andere Programme (#25)

- Ziehen aus Galerie und Lightbox legt neben dem internen Format einen echten **FileDrop** (+ „Preferred DropEffect“ = Kopieren)
  ab und erlaubt dann **nur Kopieren** — fremde Ziele (Explorer, Mail, Office) können nicht verschieben oder verknüpfen;
  interne Ziele entscheiden weiter nach Taste (Umschalt = verschieben).
- **Strg+C** bzw. Datei ▸ Kopieren legt die Dateien in die Zwischenablage (Dateiliste + Kopieren).
- **Versteckte Bibliothek:** gesperrt mit Hinweis („Fotos verlassen den Container nicht“), ohne Datei-Pfade im Ziehen; fehlende
  Dateien und Archiv-Einträge werden mit Zahl übersprungen.

## Begründung

- Ordnerleiste statt eigener Seite: ein Ort für alle Filter, kein Seitenwechsel, der Ordner wird ein Filter wie jeder andere.
- Ein gemeinsamer Menü-Aufbau für alle Orte verhindert, dass Galerie, Liste, Lightbox und Story wieder auseinanderlaufen.
- Ordner-Titelbild als Spalte ist die kleinste Änderung; eine eigene Tabelle hätte nur Verwaltungsaufwand gebracht.
- EXIF-Orientierung statt Neu-Kodieren: verlustfrei, rückgängig machbar mit identischen Bytes, gleiche Technik wie GPS (ADR-0050).
- Nur Kopieren nach außen schützt vor versehentlichem Verschieben aus der Bibliothek durch fremde Programme.

## Konsequenzen

### Positiv
- Galerie und Ordner in einer Ansicht; alle Ordner-Aktionen bleiben erreichbar.
- Titelbilder für Personen, Alben, Storys und Ordner an einer Stelle mit sinnvoller automatischer Wahl.
- Drehen, Kopieren nach…, Exportieren und Eigenschaften neu, mit Rückgängig wo es Dateien ändert.

### Negativ
- Gedrehte Fotos brauchen neue Vorschaubilder und Gesichtsausschnitte (werden verworfen und neu erzeugt).
- Ordner-Titelbild ohne FK: ein ungültiger Verweis bleibt stehen, bis jemand ein neues Titelbild setzt (wird beim Lesen ignoriert).

### Neutral
- Nur JPEG ist drehbar; keine eigenen Tastenkürzel fürs Drehen.
- Intelligente Alben erscheinen nicht unter „Als Titelbild für“.
- Hover-Vorschau des Ordner-Titelbilds im Baum ist nicht umgesetzt.

## Alternativen

- **Ordner-Seite behalten und nur verlinken:** → verworfen, Mockup 18 will eine Ansicht.
- **Ordner-Titelbild in eigener Tabelle `FolderCovers`:** → verworfen, kein Mehrwert gegenüber einer Spalte.
- **Drehen durch Neu-Kodieren der Pixel:** → verworfen, verlustbehaftet und nicht bytegenau rückgängig.
- **Nach außen Kopieren und Verschieben erlauben:** → verworfen, ein fremdes Ziel könnte Dateien aus der Bibliothek nehmen.

## Referenzen

- Code: `src/Lumora.Services/Covers/`, `Services/Rotation/`, `Services/Export/`, `Services/Related/`,
  `Services/Operations/FileOperationService.Copy.cs`, `src/Lumora.Wpf/PhotoMenu/`, `Wpf/Folders/FolderPane.xaml`,
  `Wpf/Gallery/Views/GalleryPage.xaml`
- Migration `20261025072546_AddFolderCover`
- Issues #15, #23, #24, #25
