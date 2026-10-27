# Lumora

**Lumora** — aus *Lumen* (Licht) und *Memoria* (Erinnerung) — ist eine native Windows-Desktop-App (C# / .NET 10 / WPF mit WPF-UI 4, SQLite) zum Ansehen, Bewerten, Sortieren und Verschieben
großer Foto- und Video-Bibliotheken (50 000–500 000 Dateien). Nachfolger einer PowerShell-Web-App (V2), die bis zur
Ablösung im Alltag weiterläuft. Stand: **0.4.5** (siehe [CHANGELOG.md](CHANGELOG.md)).

Programmdatei `Lumora.exe` (Schriftzug und App-Symbol: [Mockup 07](mockups/07-lumora-schriftzug.html),
[ADR-0047](docs/adr/0047-produktname-lumora.md)), Projekte und Namespaces `Lumora.*` (`Lumora.slnx`), Datenordner
`%LocalAppData%\Lumora`, Umgebungsvariablen `LUMORA_*`, Protokolle `lumora-*.log`. Einen Datenordner einer älteren
Version übernimmt der erste Start automatisch; läuft dabei noch die ältere Version, geschieht das beim nächsten Start
([ADR-0056](docs/adr/0056-technische-namen-lumora.md)).

## Features (0.2.0)

- **Bibliothek**: Foto-Ordner als Bibliothek anlegen, Katalog in SQLite aufbauen (Fresh-Scan), später „Aktualisieren“
  (Incremental-Scan mit stabilen Datei-Ids — Favoriten, Bewertungen und Vorschaubilder bleiben) oder „Neu aufbauen“.
  Fehlende Dateien werden markiert, nicht gelöscht; ist das Laufwerk nicht erreichbar, wird nichts markiert
  (Offline-Guard), bei ungewöhnlich vielen fehlenden Dateien fragt die App nach (Mass-Missing-Guard).
- **Galerie**: seitenweises, virtualisiertes Kachel-Grid (200/500/1000 pro Seite) mit echten Vorschaubildern (WIC,
  EXIF-Orientierung; Videos und Formate ohne Windows-Codec über FFmpeg; scharf passend zu Vorschaugröße und
  Bildschirm-Skalierung, Bildmaße im Katalog), Sortierung nach Aufnahmedatum (EXIF/Video/Dateiname, selbst setzbar —
  auch „ca. 1982“)/Änderungsdatum/Name/Größe, Volltextsuche
  über Dateiname und Endung (FTS5), frei kombinierbare Filter (Dateiart, Favoriten, Bewertung, Ablage, Tags, Personen,
  Kategorien, Ort — alle mit UND, „Filter zurücksetzen“), Orte aus GPS samt Ortsnamen offline und „Ort setzen…“ (EXIF/XMP,
  rückgängig machbar), Ordnerfilter, Unterordner-Kacheln für Ordner
  ohne direkte Dateien, Favorit und 1–5 Sterne je Kachel, Mehrfachauswahl mit Aktionsleiste (Favorit, Bewertung,
  Tags, Verschieben nach…, Papierkorb) und Rückgängig. Umschaltbar auf eine **Collage** (echte Seitenverhältnisse,
  randvolle Reihen) und eine **Listenansicht** mit Spalten.
- **Tags**: frei definierbare, farbige Schlagworte (Einstellungen › Tags), Zuweisen für die Auswahl (Aktionsleiste,
  Kontextmenü, Strg+T), Tag- und Kategorie-Marken auf der Kachel, in der Lightbox und im Ordnerbaum (seit 0.4.35),
  Filter nach Tags (ODER/UND/ohne Tag), Suche findet Tag-Namen.
- **Kategorien**: eigene Kategorien mit festen Werten (Einstellungen › Kategorien), ein Wert je Datei, Zuweisen per
  Kontextmenü oder Strg+K (auch in der Lightbox), Filter, Suche nach Werten, je Kategorie eine Listenspalte, die die
  ganze Trefferliste sortiert.
- **Lightbox**: eigenes Fenster (auch auf einem zweiten Monitor) für Bilder mit Zoom/Pan und Info-Panel sowie Videos
  über LibVLC mit Transportleiste; Favorit, Bewertung, Papierkorb, „Im Explorer anzeigen“, „Mit Standardprogramm
  öffnen“.
- **Archive** (0.2.8): ZIP, RAR, 7z, TAR, GZ/TGZ, BZ2 und XZ erscheinen als Kacheln im Katalog (Icon, Endung, Größe;
  Filter „Archive“); die Lightbox zeigt ihren Inhalt, „Entpacken…“ legt die Dateien neben das Archiv und nimmt sie
  sofort in den Katalog auf (siehe [Archive](#archive)).
- **Ordner**: Ordnerbaum der Bibliothek mit Dateizählern als ein-/ausklappbare Leiste in der Galerie (seit 0.4.44, Strg+B),
  der gewählte Ordner wirkt als Filter-Chip (mit oder ohne Unterordner); ganze Ordner bewerten, als Favorit markieren,
  kategorisieren oder taggen.
- **Eingang und Bibliotheksordner** (0.2.32): unsortierte Quellordner als Eingang aufnehmen, ansehen und bewerten, dann
  per **In die Bibliothek übernehmen…** samt Unterordner-Struktur in den Bibliotheksordner verschieben — leere
  Zwischenordner werden zusammengezogen, Bewertungen wandern mit, alles mit einem Rückgängig umkehrbar.
- **Sortieren**: Pattern-basierter File Sorter (Regex-Profile, Multi-Level, Vorschau, Import/Export der V2-Profile)
  und Flatten & Move — jede Verschiebung läuft protokolliert über die Datei-Operationen und ist rückgängig machbar.
- **Einstellungen**: Speicherorte (Katalog samt Vorschaubildern umziehbar), Thumbnail-Cache, FFmpeg-Status, unterstützte Formate, Farbschema (Hell/Dunkel/System),
  Version.
- **Statusleiste**: Scan-Fortschritt und -Ergebnis, Thumbnail-Warteschlange, Hintergrund-Aktivität, Hinweise
  (Bibliothek offline, unvollständige Dateivorgänge, Datei-Operation wartet auf laufenden Scan — mit **Abbrechen**).
- **Sicherheit der Daten**: Löschen geht immer in den Windows-Papierkorb (nie still hart löschen); Verschieben,
  Umbenennen und Löschen werden in `OperationBatches`/`FileOperations` protokolliert; Move/Rename und Ordner-Übernahmen sind
  rückgängig machbar.

## Voraussetzungen

| | |
|---|---|
| Betriebssystem | Windows 11 (Mica-Hintergrund, WPF-UI 4) |
| Entwicklung | [.NET 10 SDK](https://dotnet.microsoft.com/download) (Solution-Format `.slnx`); Visual Studio mit .NET-10-Workload oder die `dotnet`-CLI |
| Video-Vorschaubilder | FFmpeg im `PATH`: `winget install Gyan.FFmpeg` (danach App neu starten). Ohne FFmpeg laufen Bilder normal, Videos bekommen keine Vorschaubilder; die Seite „Einstellungen“ zeigt den Status. |
| Video-Wiedergabe | LibVLC kommt als NuGet-Paket (`VideoLAN.LibVLC.Windows`) mit — keine Installation nötig. |
| Speicherplatz | Vorschaubilder (kurze Kante 400 px, ab 0.2.45) brauchen etwa **5,5 GB je 100 000 Fotos** (≈ 27 GB bei 500 000) im Ordner neben dem Katalog (Einstellungen → Speicherorte). Vorschaubilder älterer Versionen (320 px) werden nach dem Update im Hintergrund ersetzt — die Statusleiste zeigt „Vorschaubilder: N in Warteschlange“. |
| Story-Assistent (optional) | [Ollama](https://ollama.com) mit einem kleinen Sprachmodell — Installation und Download aus Einstellungen › Assistent, jeweils erst nach Bestätigung (ADR-0063). Ohne Ollama verstehen nur einfache Sätze. |
| Optional | Windows-Codec-Pakete aus dem Microsoft Store für HEIC/HEIF („HEIF-Bilderweiterungen“ + „HEVC-Videoerweiterungen“), WebP („Webp-Bilderweiterungen“) und Kamera-RAW („Raw-Bilderweiterung“). Ohne Codec erzeugt FFmpeg die Vorschaubilder, die Lightbox kann solche Bilder aber nicht anzeigen. |

## Build & Start

```powershell
cd "D:\Code\Lumora\repo"
dotnet build Lumora.slnx -c Debug --nologo
dotnet run --project src/Lumora.Wpf -c Debug
```

Oder in Visual Studio: `Lumora.slnx` öffnen, `Lumora.Wpf` als Startprojekt, **F5**.
Die Programmdatei liegt danach unter `src\Lumora.Wpf\bin\Debug\net10.0-windows\Lumora.exe` (bis 0.2.48
`Lumora.exe` — der Build räumt die alte EXE aus dem Ausgabeordner, Verknüpfungen neu auf `Lumora.exe` setzen).
Beim ersten Start legt die App die Datenbank an (EF-Core-Migration) und öffnet Einstellungen › Bibliotheken &
Eingänge („Neue Bibliothek…“).

Die App-Version (Einstellungen › Info, Dateiversion der EXE) kommt aus dem obersten Eintrag in
[CHANGELOG.md](CHANGELOG.md); `Lumora.Wpf.csproj` liest sie beim Build aus.

Das App-Symbol `src\Lumora.Wpf\Shell\Branding\Lumora.ico` (16–256 px) ist eingecheckt und wird aus der
Vektor-Quelle `LumoraIcon.xaml` erzeugt: nach einer Änderung am Symbol `pwsh -File build\make-icon.ps1` ausführen und
die neue `.ico` mit einchecken (der Test `LumoraIconFileTests` meldet, wenn Datei und Vektor-Quelle auseinanderlaufen).

## Portable Version bauen

```powershell
pwsh -File build\publish.ps1
```

Erzeugt per `dotnet publish` (Release, `win-x64`, **self-contained**, kein Single-File, kein Trimming) den Ordner
`artifacts\Lumora-<Version>-win-x64\` und daneben dieselbe Version als ZIP. Ordner bzw. ZIP an einen beliebigen
Ort kopieren und `Lumora.exe` starten — ein installiertes .NET ist nicht nötig. Das Skript bricht ab, wenn die
Produktversion der EXE nicht zum CHANGELOG passt, der Produktname nicht „Lumora“ ist, SQLite (`e_sqlite3.dll`) bzw. LibVLC (`libvlc\win-x64\`) fehlen oder
Werkzeuge für `dotnet ef` (`BuildHost-*`, `Microsoft.EntityFrameworkCore.Design.dll`) im Paket liegen (seit 0.2.31 nicht
mehr: 734 Dateien, ~253 MB, ZIP ~112 MB).
`artifacts\` ist per `.gitignore` ausgenommen; mit `-OutputRoot <Ordner>` landet das Paket außerhalb des Repos (z. B.
außerhalb von OneDrive), `-NoZip` lässt das ZIP weg, `-FrameworkDependent` baut ein kleineres Paket, das die
„.NET 10 Desktop Runtime“ (x64) voraussetzt.

- **Daten** liegen auch bei der portablen Version unter `%LocalAppData%\Lumora` (bzw. im Ordner aus
  `database-location.json`), nicht im Paketordner — ein neues Paket übernimmt Katalog und Vorschaubilder. Die
  Formatregister sind fest im Code, es gibt keine Konfigurationsdateien neben der EXE.
- **`-Portable`** (seit 0.4.40, ADR-0069) legt die Markierungsdatei `lumora.portable` ins Paket
  (`artifacts\Lumora-<Version>-win-x64-portable\`): Lumora speichert dann alles im Ordner `Daten\` neben `Lumora.exe` —
  eine eigene Installation mit eigenem Katalog, siehe [Produktivbetrieb](#produktivbetrieb).
- **FFmpeg** ist nicht im Paket; es wird wie beim Debug-Build im `PATH` bzw. unter den winget-Links gesucht
  (`winget install Gyan.FFmpeg`).
- **Pfadlänge:** Die LibVLC-Plugins liegen bis zu ~120 Zeichen tief im Paket (`Lumora-…\libvlc\win-x64\…`);
  Paket bzw. `-OutputRoot` deshalb nicht tiefer als ~130 Zeichen ablegen, sonst scheitern Kopieren/Entpacken am
  260-Zeichen-Limit von Windows.
- **Rauchtest gegen das Paket** (Testbibliothek siehe [Tests](#tests)):
  `pwsh -File build\run-smoke.ps1 -Exe artifacts\Lumora-<Version>-win-x64\Lumora.exe -TestLib <Testbibliothek>`

## Produktivbetrieb

Ab der Release-Version gilt der Katalog als echte Arbeit (Bewertungen, Tags, Alben, Personen, Orte). Drei Dinge gehören
dazu (seit 0.4.40, ADR-0069):

### 1. Fotos sichern — das macht Lumora nicht
Lumora verwaltet die Fotos an Ort und Stelle und sichert sie **nicht**. Die Sicherung der Fotos und Videos selbst ist eine
eigene Aufgabe, am besten nach der **3-2-1-Regel**: **3** Kopien (Original + 2 Sicherungen), auf **2** verschiedenen
Datenträgern (z. B. interne Platte + externe Platte/NAS), davon **1** an einem anderen Ort (außer Haus oder Cloud).
Eine Sicherung erst als Sicherung zählen, wenn das Zurückholen einmal ausprobiert wurde. Synchronisierung (OneDrive o. Ä.)
ist keine Sicherung — ein Löschen wird mitsynchronisiert.

### 2. Katalog-Sicherung
- **Automatisch vor jeder Aktualisierung:** Muss eine neue Lumora-Version den bestehenden Katalog aktualisieren
  (EF-Migration), legt sie vorher eine Sicherung an: `<Datenordner>\backups\library-<Version>-<Zeitstempel>.db`. Die
  letzten **5** bleiben, ältere werden gelöscht. Geht die Sicherung nicht (z. B. Platte voll), **aktualisiert Lumora
  nicht** und startet mit einer Meldung nicht — der Katalog bleibt unverändert; Platz schaffen und neu starten.
- **Von Hand:** Einstellungen › Speicher & Wartung › **Katalog-Sicherung** › **Katalog jetzt sichern** (die letzten 10
  bleiben). Die Liste zeigt Datum, Version, Anlass und Größe; **Ordner öffnen** zeigt den Sicherungsordner.
- **Wiederherstellen…** an einer Sicherung fragt nach, merkt sie vor und **beendet Lumora**. Beim nächsten Start ersetzt
  Lumora den Katalog durch die Sicherung; der bisherige bleibt als `…-vor-wiederherstellung.db` daneben liegen. Änderungen
  seit der Sicherung fehlen danach, die Fotos bleiben unberührt; die Vorschaubilder entstehen neu. Eine Vormerkung lässt
  sich bis dahin abbrechen. Fehlt die Sicherung beim Start oder ist sie beschädigt, startet Lumora mit dem bisherigen
  Katalog und nennt den Grund in der Karte. Sicherungen einer **neueren** Lumora-Version lassen sich nicht wiederherstellen.
- **Nicht gesichert:** Vorschaubilder (Lumora erzeugt sie bei Bedarf neu) und die Fotos selbst (siehe oben).
- Sicherungen sind einzelne SQLite-Dateien und lassen sich zusätzlich mit den Fotos mitsichern.
- In einer **versteckten Bibliothek** liegen ihre Sicherungen nur im Container (`.lumora\backups`), nie außerhalb.

### 3. Getrennte Release-Installation (portabel)
So läuft die Release-Version mit **frischem Katalog** neben der Test-Installation, ohne sie zu berühren:
1. Paket bauen: `pwsh -File build\publish.ps1 -Portable` → `artifacts\Lumora-<Version>-win-x64-portable.zip`.
2. Einen eigenen Ordner anlegen, z. B. `D:\Lumora-Release` (lokales Laufwerk, beschreibbar — nicht unter
   `C:\Program Files`, nicht in OneDrive, nicht in einem Fotoordner; Pfad kurz halten, siehe „Pfadlänge“ oben).
3. Das ZIP dort entpacken. Neben `Lumora.exe` liegt `lumora.portable` — sie schaltet den portablen Modus ein.
4. `Lumora.exe` starten. Lumora legt `Daten\` neben der EXE an (Katalog, Vorschaubilder, Protokolle, Sicherungen, Cache,
   Modelle). **Einstellungen › Info › Dieses System › Datenordner** zeigt „Portabel“.
5. Unter **Einstellungen › Bibliotheken & Eingänge** die Bibliothek neu anlegen und einlesen. Gesichtserkennung und Motive
   brauchen ihre Modelle erneut (Download in den Einstellungen; sie landen unter `Daten\models\`).
6. Für ein Update der Release-Version: Lumora beenden, das neue Paket über den Ordner entpacken (`Daten\` bleibt) — vor der
   Katalog-Aktualisierung sichert Lumora automatisch.

Vorrang der Datenordner: Umgebungsvariable `LUMORA_APPDATA` > portabel (`lumora.portable` oder Ordner `Daten\` neben
der EXE) > Standard `%LocalAppData%\Lumora`. Die Test-Installation (Debug-Build, Pakete ohne `-Portable`) bleibt beim
Standard; der einmalige Umzug aus `PhotoViewerV3` (ADR-0056) läuft nur dort.

## Bedienung

### Navigation (seit 0.2.44, ADR-0042)
Die linke Leiste zeigt ganz oben das App-Symbol mit dem Schriftzug „lumora“, darunter den **Bibliotheks-Umschalter** (Name der aktiven Bibliothek; Klick → andere Bibliothek
wählen, verknüpfte Bibliotheken als **Alle verknüpften (Name)** gemeinsam ansehen oder **Bibliotheken verwalten…**), darunter **Galerie, Alben, Storys, Personen, Orte, Doppelte, Sortieren** (den Punkt „Ordner“ gibt es seit 0.4.44 nicht mehr — der Ordnerbaum ist die Leiste links in der Galerie) und unten
**Einstellungen**. Bibliothek, Tags und Kategorien richtet man selten ein — sie sind Reiter der Einstellungen:
**Bibliotheken & Eingänge · Tags · Kategorien · Gesichter · Motive · Archive · Darstellung · Storys · Speicher & Wartung · Info**.
Tags und Kategorien **vergeben** geht wie bisher in der Galerie (Rechtsklick, Strg+T, Strg+K, Lightbox); „Tags
verwalten…“ bzw. „Kategorien verwalten…“ im Galerie-Menü öffnen den passenden Reiter. Ohne Bibliothek startet die App
in Einstellungen › Bibliotheken & Eingänge.

### Bibliotheken & Eingänge (Einstellungen)
- **Neue Bibliothek…** → Foto-Ordner wählen, Namen vergeben, auf Nachfrage sofort scannen.
- Je Bibliothek: **Scannen** (leerer Katalog) bzw. **Aktualisieren** (Incremental: neue, geänderte, fehlende und wieder
  vorhandene Dateien; Ids, Favoriten, Bewertungen, Vorschaubilder bleiben), **Neu aufbauen** (Katalog komplett neu mit
  neuen Ids und Vorschaubildern; Favoriten, Bewertungen und Tags werden für Dateien übernommen, die noch am selben Ort
  liegen — gelöschte, verschobene oder umbenannte Dateien verlieren sie; liegen noch Einträge in ausgeschlossenen Ordnern
  in der Bibliothek, nennt die Rückfrage ihre Zahl samt Sternen/Personen/Alben/Tags und bietet **Einträge behalten** oder
  **Einträge herausnehmen**, seit 0.4.39), **Aktiv setzen**, Umbenennen, Entfernen (löscht keine Dateien auf der
  Platte).
- Während eines Scans: Fortschrittspanel (Ordner, Dateien, Größe, Pfad, Dauer) mit **Abbrechen**; danach eine
  Zusammenfassung. Läuft gerade ein anderer Scan derselben Bibliothek (z. B. die automatische Aktualisierung) oder eine
  Datei-Operation (Verschieben, Sortierer …), zeigt das Panel bis zum Start „Wartet auf laufenden Scan…“ bzw. „Wartet auf
  laufende Datei-Operation…“; **Abbrechen** beendet das Warten, der Katalog bleibt unverändert. Bei nicht erreichbarem Ordner: Dialog „Bibliothek offline“ ([Erneut prüfen] [Andere Bibliothek
  wählen]); bei ungewöhnlich vielen fehlenden Dateien (> 10 000 oder > 20 %): Dialog ([Erneut prüfen]
  [Als fehlend markieren] [Abbrechen]).
- Je Bibliothek die Liste ihrer Ordner mit Rolle (siehe „Eingang und Bibliotheksordner“): **Eingang hinzufügen…**,
  je Eingang **In die Bibliothek übernehmen…**, Archiv-Symbol = diesen Ordner zum Bibliotheksordner machen, Umbenennen
  (Anzeigename), Entfernen (nur Eingänge, nur aus dem Katalog). Scannen/Aktualisieren läuft über alle Ordner. Ein
  Kasten „So funktioniert's“ erklärt den Ablauf; die Datenbank enthält nur Verweise und Bewertungen, keine Bilder.
- Unter der Liste: **Ordnerüberwachung** (Schalter, Ruhezeit; siehe „Ordnerüberwachung“).
- Darunter (seit 0.4.33, ADR-0066): **Ausschlüsse beim Einlesen** — Ordner, die Lumora nicht einliest (ihre Dateien tauchen
  in Galerie, Doppelten und Storys nicht auf; auf der Platte bleibt alles). **Ordnernamen** gelten in jeder Tiefe und für alle
  Bibliotheken (Standard `.thumbs` und `.temp` — die Vorschauordner der alten PowerShell-Viewer); Name eingeben →
  **Hinzufügen**, ✕ nimmt ihn heraus (die Bibliothek wird danach neu eingelesen). **Ordner dieser Bibliothek** kommen aus dem
  Reiter „Ordner“ (Rechtsklick › „Ordner ausschließen…“); **Ausschluss aufheben** liest den Ordner wieder ein. Sind in
  ausgeschlossenen Ordnern noch Dateien in der Bibliothek (z. B. `.thumbs` von vor 0.4.33), zeigt die Karte die Zahl mit
  **Aus der Bibliothek entfernen…** (Rückfrage mit Sternen/Personen/Alben/Tags, **Rückgängig** bringt die Einträge zurück).
  Beim Hinzufügen fragt Lumora, wenn schon Dateien betroffen sind: „Aus der Bibliothek entfernen“ oder „Nur künftig
  ausschließen“. Die versteckte Bibliothek hat ihre eigene Liste.

### Bibliotheken getrennt oder verknüpft (seit 0.2.51, ADR-0048)
Jede Bibliothek ist für sich: eine geöffnete Bibliothek zeigt **nichts** aus einer anderen — keine Fotos, keine Tags,
Kategorien, Personen oder Beziehungen, keine Motiv-Begriffe, keine Orte auf der Karte oder im Ort-Filter, keine Treffer
in der Suche oder der Bildinhalt-Suche, keine Personen-Vorschläge (Motive und Orte seit 0.2.56). Zwei Bibliotheken dürfen
gleich benannte Tags oder Personen haben; es sind verschiedene.
- **Verknüpfen**: Einstellungen › Bibliotheken & Eingänge, im Kasten unter einer Bibliothek **Verknüpfen mit…** → die
  andere Bibliothek wählen. Der Dialog zeigt, was zusammengeführt wird (gleichnamige Tags, Kategorien samt Werten und
  Beziehungen werden eins, die übrigen kommen hinzu; ebenso die Motiv-Begriffe — fehlende Treffer rechnet Lumora danach
  aus den gespeicherten Bild-Vektoren nach, kein Foto wird neu analysiert) und listet gleichnamige Personen: Häkchen = zusammenführen
  (Standard), ohne Häkchen bleiben sie getrennt und bekommen den Bibliotheksnamen als Rufnamen-Zusatz. Verknüpfte
  Bibliotheken teilen danach Tags, Kategorien, Personen und Beziehungen; die Gruppe heißt wie die Bibliothek, von der aus
  verknüpft wurde (**Umbenennen**-Symbol daneben).
- **Alle verknüpften** ansehen: im Bibliotheks-Umschalter oben links erscheint für verknüpfte Bibliotheken
  „Alle verknüpften (Name)“. Galerie, Suche, Filter (auch Motive und Ort), Personen, die Karte **Orte** und der
  Ordnerbaum (je Bibliothek ein Knoten) zeigen dann alle zusammen, die Zähler sind die Summe; Fotos lassen sich auch in
  dieser Ansicht verorten. Verschieben…, „In die Bibliothek“ und Entpacken aus der Galerie sowie „Eingang
  hinzufügen…“ auf der Ordner-Seite sind in dieser Ansicht gesperrt (das Ziel wäre nicht eindeutig) — dafür eine einzelne
  Bibliothek wählen. Favoriten, Bewertung, Tags, Kategorien, Personen, Datum und Papierkorb gehen weiter; „In die
  Bibliothek übernehmen…“ im Ordnerbaum wirkt in der Bibliothek des Ordners.
- **Verknüpfung lösen**: die Bibliothek wird wieder einzeln geführt und bekommt eine **Kopie** aller gemeinsamen Begriffe
  (auch der Motiv-Begriffe) und Personen — nichts geht verloren; Tags, Kategorien, Motiv-Treffer und Gesichter ihrer
  Fotos zeigen auf die Kopien.
- Beim Update auf 0.2.51 bekommt jede Bibliothek die Begriffe, die sie benutzt; nicht benutzte Tags, Kategorien,
  Personen und Beziehungen findet man in jeder Bibliothek wieder (überzählige dort löschen).

### Eingang und Bibliotheksordner
Eine Bibliothek hat genau einen **Bibliotheksordner** (der beim Anlegen gewählte Ordner — dein sortiertes Fotoarchiv;
im Code „Archiv-Wurzel“) und beliebig viele **Eingänge** (unsortierte Quellordner: Kamera-Import, Handy-Upload,
Downloads, alte Ordnersammlungen). Die Datenbank (Katalog) enthält nur Verweise auf die Dateien samt Bewertungen,
Favoriten, Tags und Kategorien — keine Bilder.

**Ablauf:** Eingang hinzufügen → ansehen, bewerten, kategorisieren (einzelne Dateien oder ganze Ordner) → Ordner in die
Bibliothek übernehmen.
- **Bibliothek → Eingang hinzufügen…**: Ordner wählen (darf nicht in oder über einem anderen Ordner der Bibliothek
  liegen), danach „Jetzt aktualisieren“ nimmt die Dateien in den Katalog auf.
- **Galerie**: Filter **Ablage** mit **Noch im Eingang** (alles in Eingängen) und **In der Bibliothek** (kombinierbar mit allen anderen Filtern); Kacheln im
  Eingang tragen das Badge „Eingang“, im Bibliotheksordner „Bibliothek“. Der Ordnerbaum zeigt die Wurzeln als oberste
  Knoten mit Rolle.
- **Ganze Ordner bewerten**: Rechtsklick auf einen Ordner im Ordnerbaum → **Bewertung für alle Dateien** (1–5 Sterne
  oder entfernen), **Favorit für alle**, **Kategorie für alle…** (Kategorie-Auswahl wie Strg+K), **Tags für alle…**
  (Tag-Auswahl wie Strg+T). Wirkt auf alle Dateien darunter (mit Unterordnern), nach einer Bestätigung mit Anzahl.
- **In die Bibliothek übernehmen…** (Rechtsklick auf einen Ordner unter einem Eingang oder auf den Eingang selbst; auf
  Einstellungen › Bibliotheken & Eingänge je Eingang): Dialog mit dem Ordnerbaum des Bibliotheksordners (Suche, **Neuer Ordner**,
  zuletzt benutzter Zielordner vorgewählt) und einer **Vorschau** des Ergebnisbaums (alt → neu, Dateien je Ordner,
  Hinweise auf zusammengezogene, zusammengeführte und umbenannte Einträge). Der Ordner landet samt Unterordner-Struktur
  im gewählten Zielordner. Optionen:
  - **Leere Zwischenordner zusammenziehen** (Standard an): ein Ordner ohne Dateien mit genau einem Unterordner entfällt,
    der nächste rückt nach, bis zum ersten Ordner mit Dateien oder mehreren Unterordnern — dessen Name bleibt.
    Beispiel `Fotos\leer\leer\leer\inhalt` → `Fotos\inhalt`; auch der übernommene Ordner selbst (`Import\leer\inhalt` →
    `<Ziel>\inhalt`). Verzweigungen bleiben vollständig erhalten; leere Ordner werden nicht angelegt.
  - **Quellordner danach in den Papierkorb** (Standard an): nur, wenn danach keine Datei mehr darin liegt — sonst bleibt
    er und ein Bericht nennt die Reste. Systemordner wie `.thumbs`, `.cache`, `@eaDir` und `Thumbs.db` werden nicht
    übernommen und gehen mit in den Papierkorb.
  - **Eingang danach austragen** (nur bei einem ganzen Eingang, Standard an; nur aus dem Katalog). Ohne Austragen
    bleibt der Eingangsordner leer bestehen, nur sein Inhalt kommt in den Papierkorb.
  Vorhandene Zielordner werden zusammengeführt, gleichnamige Dateien bekommen „_1“, „_2“ …. Übernommen werden alle
  Dateien, auch .xmp oder .txt. Bewertungen, Favoriten, Tags, Kategorien und Vorschaubilder wandern mit (gleiche
  FileId). **Rückgängig** (Galerie) holt alles zurück: Dateien an die alten Pfade, die Ordner entstehen neu, leere
  Zielordner verschwinden, ein ausgetragener Eingang wird wieder eingetragen (Name = Ordnername).
- **In die Bibliothek…** für einzelne Dateien (Aktionsleiste, **Strg+M**, Kontextmenü der Kachel, in der Lightbox
  **Strg+M**): Dialog mit dem Ordnerbaum des Bibliotheksordners (auch Ordner, die nur auf der Platte existieren),
  Suchfeld, **Neuer Ordner…**, **Umbenennen…**, Vorschlag „Jahr\Jahr-Monat“ aus Aufnahme- bzw. Dateidatum; der zuletzt
  benutzte Zielordner ist vorgewählt. Die Dateien landen flach im gewählten Ordner. Verschoben wird protokolliert
  (Rückgängig wie bei „Verschieben nach…“); Favoriten, Bewertungen und Vorschaubilder bleiben. Die Ansicht bleibt beim
  Filter und zeigt die restlichen Dateien.
- Einen Eingang zu entfernen löscht nur seine Katalogdaten — die Dateien auf der Platte bleiben.
- Nicht verwechseln: **Archive** (ZIP/RAR/7z — Filter „Archive“, „Entpacken…“, automatisches Entpacken) sind Dateien,
  der Bibliotheksordner ist ein Ordner.

### Galerie
Kopfzeile (0.2.43, ADR-0041):
- **Titel und Suche**: links „Galerie“ mit der Trefferzeile darunter („342 von
  1.248 Dateien · Ordner: alle“; ein Ordnerfilter erscheint dort als Chip „Ordner: …“ mit dem Pfad daneben — Klick auf ein
  Glied wählt diesen Elternordner, ✕ hebt den Ordner auf und kombiniert sich mit allen übrigen Filtern). Rechts das Suchfeld
  (Dateiname, Endung, Tag, Person, Kategorie-Wert oder Kategorie-Name; Wörter werden als Präfix gesucht, Sonderzeichen
  sind erlaubt): **Strg+F** springt hinein, **Enter** sucht sofort, sonst sucht es nach einer kurzen Tipp-Pause; **Esc**
  oder ✕ leert die Suche.
- **Filterkarte**, Zeile **Datei**: Dateiart (Alle / Bilder / Videos / Archive), **Nur Favoriten**, **Bewertung**
  (egal / ab 1–4 Sternen / nur 5 Sterne), **Ablage** (Überall / Noch im Eingang / In der Bibliothek / **Nur fehlende** — seit 0.4.30) und rechts
  **Filter zurücksetzen** mit der Zahl der aktiven Filter. Zeile **Inhalt**: **Tags**, **Personen** (sobald es Personen
  oder Gesichter gibt), **Kategorien** (sobald es Kategorien gibt) und **Doppelte** (nur Dateien, die es im Bereich
  byte-gleich ein zweites Mal gibt — seit 0.3.1); wirkende Filter erscheinen in Akzentfarbe mit
  ihrem Wert („Tags: Urlaub“). **Alle Filter gelten gleichzeitig (UND)** — z. B. nur Bilder UND Favoriten UND Tag
  „Urlaub“ UND ohne benannte Person UND noch im Eingang. Bei aktiven Filtern sagt die Zeile **„Es gilt: Bilder UND
  Favoriten UND Tag „Urlaub“ … → 342 Treffer“**, was gerade wirkt; ✕ an einer Bedingung entfernt genau diese.
  **Filter zurücksetzen** hebt alle Filter und die Suche auf, die Ordnerauswahl bleibt. Seit 0.4.1 gibt es **einen**
  Filterzustand für Galerie, Ordner und Orte (dieselbe Filterkarte auf allen drei Seiten, ADR-0055): Filter bleiben
  beim Seitenwechsel erhalten, „Filter zurücksetzen“ wirkt überall; Ordnerauswahl, Sortierung und Ansicht gehören der
  jeweiligen Seite.
- **Sortieren** [Feld ▾] [Richtung]: **Aufnahmedatum** (Standard seit 0.2.48), Änderungsdatum, Name, Größe oder eine
  Kategorie; der Knopf daneben kehrt die
  Richtung um („Neueste zuerst“ ↔ „Älteste zuerst“, „A → Z“ ↔ „Z → A“ …). Ein neues Feld beginnt mit seiner üblichen
  Richtung. · **Ansicht** Kacheln | Collage | Liste (wird gemerkt) und **Vorschaugröße** (Regler bei Kacheln und
  Collage: 120–320 px in 20er-Stufen, wird gemerkt) · rechts **Alle auswählen**, **Rückgängig**, Aktualisieren (ist die
  Leiste zu schmal, zeigen die beiden nur ihr Symbol).
- **Unter der Galerie**: Blättern („‹ Seite 1 von 7 ›“, dazu erste/letzte Seite) und **Pro Seite** (200/500/1000).
- Bei 1280 Pixel Fensterbreite bleiben Titelzeile, Zeile „Datei“ und Sortierleiste einzeilig; schmaler (bzw. rechts auf
  der Ordner-Seite) brechen sie sauber um, die Suche rutscht unter den Titel.

Kacheln: Herz = Favorit, Sterne = Bewertung, Badge „fehlt“ bei Dateien, die
beim letzten Scan nicht mehr auf der Platte lagen. Ordner ohne direkte Dateien zeigen ihre Unterordner als Kacheln.
**Alle auswählen** (Knopf rechts in der Sortierleiste neben „Rückgängig“, wie Strg+A) wählt alle Dateien der aktuellen
Seite. Bei einer Auswahl erscheint unten die Aktionsleiste (Favorit setzen/entfernen, Bewertung, Verschieben nach…,
In die Bibliothek…, Papierkorb, Datum…, Ort…, Alle auswählen, Auswahl aufheben); **Rückgängig** nimmt die letzte Verschiebung/Umbenennung (auch eine
Ordner-Umbenennung oder eine Ordner-Übernahme) bzw. das zuletzt gesetzte Aufnahmedatum oder den zuletzt gesetzten Ort zurück.

| Taste | Wirkung |
|---|---|
| Klick, Strg+Klick, Umschalt+Klick | Auswahl, Mehrfachauswahl |
| Enter, Doppelklick | Lightbox öffnen |
| Strg+A | Alles auf der Seite auswählen |
| Strg+Mausrad, Strg+Plus / Strg+Minus | Vorschaugröße größer / kleiner (Kacheln, Collage-Reihenhöhe) |
| Esc | Auswahl aufheben (im Suchfeld: Suche leeren) |
| Entf | Auswahl in den Papierkorb (mit Bestätigung) |
| Strg+← / Strg+→ | Vorherige / nächste Seite |
| Strg+Pos1 / Strg+Ende | Erste / letzte Seite |
| F5 | Aktualisieren |
| Strg+M | Auswahl in die Bibliothek… |
| Strg+T | Tags der Auswahl bearbeiten (Tag-Auswahl) |
| Strg+K | Kategorie-Werte der Auswahl zuweisen (Kategorie-Auswahl) |
| Rechtsklick | Tag-Menü der Auswahl (nicht ausgewählte Kachel/Zeile wird zuerst gewählt) |
| Strg+F | Suchfeld der Kopfzeile |
| Enter im Suchfeld | Suchen (sonst sucht es nach einer kurzen Tipp-Pause) |

#### Aufnahmedatum (seit 0.2.48, ADR-0046)
Die Galerie sortiert standardmäßig nach dem **Aufnahmedatum** statt nach dem Änderungsdatum der Datei (das bei
kopierten oder alten Fotos oft falsch ist). Es kommt aus dem EXIF-Feld `DateTimeOriginal` (JPEG, TIFF/RAW, HEIC/AVIF,
PNG, WebP — auch ohne HEIF-Erweiterung von Windows), bei Videos aus den Container-Metadaten (MP4/MOV), sonst aus dem
Dateinamen (`IMG_20240712_153012`, `PXL_…`, `Screenshot 2026-09-30 105535`, WhatsApp `IMG-20240712-WA0001` = nur Tag).
Gelesen wird **im Hintergrund nach jedem Scan** — bei bestehenden Katalogen einmalig für alle Dateien; die Statusleiste
zeigt „Aufnahmedatum: 1.234 / 19.556“. Es ruht während Scans, Datei-Operationen und eines Katalog-Umzugs und liest nur
Dateien, die lokal vorliegen (OneDrive-Platzhalter werden **nicht** heruntergeladen, sie folgen später). Dateien ohne
jedes Datum sortieren nach ihrem Änderungsdatum („unbekannt“ im Tooltip).

Anzeige: Kachel-Tooltip „Aufgenommen: 12.07.2024 15:30 (EXIF)“, Listenspalte **Aufgenommen** (Klick sortiert die ganze
Trefferliste; bei schon gespeicherten Spalten per Rechtsklick auf die Kopfzeile einblenden), in der Lightbox die Zeile
„Aufgenommen“ im Info-Bereich.

**Datum setzen…** (Aktionsleiste „Datum…“, Rechtsklick auf Kachel/Zeile, im Ordnerbaum Rechtsklick auf einen Ordner =
alle Dateien darin, wahlweise mit Unterordnern): **genau** (Datum + Uhrzeit), **ungefähr** („ca. 1982“ bzw. „ca. Juli
1982“ — sortiert in der Mitte des Zeitraums), **verschieben** um ± Tage/Stunden/Minuten (falsch gestellte Kamera) oder
**zurücksetzen** (wieder aus der Datei lesen). Geändert wird nur der Katalog, nie die Datei. **Rückgängig** in der
Galerie nimmt die letzte Änderung zurück (gemeinsam mit Verschieben/Umbenennen: der jüngste Eintrag zuerst). Manuell
gesetzte Daten bleiben bei „Aktualisieren“ und „Neu aufbauen“ immer erhalten; gelesene liest „Neu aufbauen“ neu.

#### Orte (seit 0.2.53, ADR-0050)
Fotos und Videos mit Koordinaten bekommen einen **Ort** („Wien, Österreich“). Die Koordinaten kommen aus dem EXIF-GPS
(JPEG, TIFF/RAW, HEIC, PNG, WebP), aus einer **XMP-Begleitdatei** (`name.ext.xmp` bzw. `name.xmp`, z. B. von Lightroom
oder digiKam — sie gewinnt vor dem EXIF) und bei Videos aus den Container-Metadaten (Android `©xyz`, iPhone
„ISO6709“). Gelesen wird wie beim Aufnahmedatum **im Hintergrund nach jedem Scan** (Statusleiste „Orte: 1.234 /
19.556“, nur lokale Dateien, nichts wird heruntergeladen). Der Ortsname kommt **offline** aus eingebetteten
GeoNames-Daten (rund 65 000 Orte ab 5 000 Einwohnern, Länder und Regionen mit deutschen Namen) — es geht nichts ins Netz.

Anzeige: Lightbox-Info „Ort: Wien, Österreich (48,2082° N, 16,3738° O)“, Listenspalte **Ort** (Koordinate im Tooltip).
Filter **Ort** in der Zeile „Inhalt“: Länder → Regionen → Orte mit Anzahl (auf- und zuklappbar, mit Suche), dazu
„ohne Ort“ — ein Land steht für alle seine Orte; mehrere Häkchen gelten mit ODER, zusammen mit den übrigen Filtern UND.
Die Karte kann die Galerie auf einen **Kartenausschnitt** einschränken (Bedingung „Kartenausschnitt (n Dateien)“).

**Ort setzen…** (Aktionsleiste „Ort…“, Rechtsklick): Ort **suchen** (offline, z. B. „Wien“, „Rom“ oder „Paris,
Frankreich“), **Koordinaten** eingeben (48,2082 und 16,3738 — auch „48.2082, 16.3738“ in einem Feld oder
48°12'29"N), **Ort von anderem Foto übernehmen** (zuletzt gesetzte und häufige Orte der Bibliothek) oder **Ort
entfernen**. „Änderungsdatum beibehalten“ ist voreingestellt. Geschrieben wird **in die Datei**: JPEG verlustfrei ins
EXIF (nur der EXIF-Block ändert sich, die Bilddaten bleiben Byte für Byte), RAW/HEIC/TIFF/PNG/WebP in eine
XMP-Begleitdatei (die Originaldatei bleibt unangetastet), Videos nur im Katalog. Schreibgeschützte, gesperrte oder nur in
der Cloud liegende Dateien bleiben unverändert und werden mit Grund gemeldet. **Rückgängig** in der Galerie stellt die
Dateien Byte für Byte wieder her (samt Änderungsdatum) — aber nur, wenn sie seither niemand verändert hat.

#### Tags
Tags legt man auf der Seite **Tags** an (Name, Farbe aus einer Palette mit zwölf Farben, Reihenfolge, Umbenennen,
Löschen mit Bestätigung und Anzahl der betroffenen Dateien) oder direkt in der Tag-Auswahl („Neues Tag…“). Zuweisen:
Knopf **Tags** in der Aktionsleiste oder Rechtsklick — Häkchen = alle ausgewählten Dateien haben das Tag, „(teilweise)“
= nur ein Teil; Klick setzt das Tag bei allen bzw. entfernt es. **Strg+T** öffnet die Tag-Auswahl mit dreistufigen
Häkchen für eine gemischte Auswahl. Die Kachel zeigt die Tags als Marke oben links (siehe „Tag- und Kategorie-Symbole“). Der Filter **Tags**
in der Zeile „Inhalt“ filtert nach einem oder mehreren Tags — „Mindestens eines der gewählten“ (ODER), „Alle gewählten
zusammen“ (UND) oder „Ohne Tag“; das Suchfeld im Flyout grenzt lange Tag-Listen ein. Die Suche findet neben Dateinamen auch Dateien, deren Tag-Name den Suchtext enthält. Tags hängen am
Katalog-Eintrag: „Aktualisieren“ und „Neu aufbauen“ (über den Pfad) behalten sie, der Papierkorb entfernt die Zuweisungen.

#### Kategorien
Eigene Kategorien mit festen Werten (z. B. „Projekt“: Nord, Süd) legt man auf der Seite **Kategorien** an (ADR-0027).
Jede Datei hat je Kategorie höchstens einen Wert. Zuweisen: Rechtsklick auf Kachel oder Zeile → Untermenü der Kategorie
→ Wert (Häkchen = alle ausgewählten Dateien haben ihn, „(teilweise)“ = nur ein Teil; **Kein Wert** entfernt ihn). Der
Filter **Kategorien** in der Zeile „Inhalt“ (erscheint, sobald es Kategorien gibt) filtert je Kategorie nach einem
oder mehreren Werten und/oder „ohne Wert“ — innerhalb einer Kategorie ODER, zwischen Kategorien UND. In der
Listenansicht ist jede Kategorie eine eigene Spalte. Werte hängen wie Tags am Katalog-Eintrag („Neu aufbauen“ behält
sie, der Papierkorb entfernt sie).

**Strg+K** (oder Rechtsklick → „Kategorien zuweisen…“) öffnet die Kategorie-Auswahl für die ausgewählten Dateien: je
Kategorie eine Auswahlliste mit den Werten und „Kein Wert“; haben die Dateien unterschiedliche Werte, steht dort
„(gemischt — nicht ändern)“, und die Kategorie bleibt unverändert, solange man nichts anderes wählt. Ohne Maus: Tab
wechselt die Kategorie, ↑/↓ den Wert, Enter übernimmt, Esc bricht ab. Die **Suche** findet auch Dateien, deren
Kategorie-Wert den Suchtext enthält (wie bei Tag-Namen, ohne Groß-/Kleinschreibung); trifft der Suchtext den Namen
einer Kategorie (z. B. „Projekt“), findet sie alle Dateien, die in dieser Kategorie irgendeinen Wert haben.

#### Tag- und Kategorie-Symbole (seit 0.4.35, Mockup 16)
Kleine Marken zeigen, wo Fotos Tags oder Kategorie-Werte haben — nur wenn welche gesetzt sind:
- **Kacheln:** oben links eine **Tag-Marke** (Tag-Symbol, bis zu drei Farbpunkte in den Tag-Farben, ab zwei Tags die
  Anzahl) und eine **Kategorie-Marke** (Symbol wie die Kategorien-Auswahl, ab zwei Kategorien die Anzahl). Darüberfahren
  nennt die Namen (Tags mit Farbpunkt, Kategorien als „Kategorie: Wert“). Ist die Kachel ausgewählt, rückt die Marke neben
  das Häkchen; bei „fehlt“/„Eingang“/„verknüpft“ steht sie darunter. Die farbigen Tag-Punkte unter der Kachel (bis 0.4.34)
  sind durch die Marke ersetzt. In der **Collage** erscheinen die Marken nur beim Darüberfahren (wie Herz und Sterne).
- **Lightbox:** Leiste unten mit den Tags (Farbpunkt + Name), einem Trenner, den gesetzten Kategorien und **+ Tag /
  Kategorie** (Menü: Tags bearbeiten… = Strg+T, Kategorien zuweisen… = Strg+K). Im Vollbild ist die Leiste aus.
- **Ordnerbaum:** rechts neben dem Ordnernamen Tag-/Kategorie-Symbol mit der Zahl der Fotos, die Tags bzw. Kategorie-Werte
  haben — nur Fotos **direkt im Ordner**, nicht in Unterordnern. Darüberfahren zeigt „n von m Fotos“ und die häufigsten
  Namen. Die Zahlen aktualisieren sich nach jedem Zuweisen.
- **Einstellungen › Darstellung › Symbole für Tags und Kategorien:** je ein Schalter für Kacheln, Lightbox und Ordnerbaum
  (Standard an; gespeichert im Katalog — eine versteckte Bibliothek hat eigene Schalter). Ist die Lightbox-Leiste aus,
  zeigt die Titelleiste die Tags und Kategorien wie früher.

#### Collage (0.2.46)
„Ansicht“ → **Collage** zeigt Fotos und Videos im echten Seitenverhältnis (aus den Bildmaßen, sonst aus dem
Vorschaubild; Archive 4:3) in gleich hohen, randvollen Reihen wie bei Google Fotos; die letzte Reihe wird nicht
gestreckt. Die Reihenhöhe folgt dem Vorschaugrößen-Regler (120–320 px, auch Strg+Mausrad, Strg+Plus/Minus). Unter dem
Bild steht kein Name — er erscheint beim Darüberfahren unten im Bild und als Tooltip. Herz, Sterne, Video-Symbol,
Personen und „fehlt“/„Eingang“ liegen wie bei den Kacheln auf dem Bild; Auswahl, Rechtsklick-Menü, Doppelklick/Enter
(Lightbox), Pfeiltasten (über die Reihen), Strg+A und Entf funktionieren wie gewohnt. Sehr hohe (schmaler als 0,4 : 1)
und sehr breite Bilder (breiter als 6 : 1) werden in ihrer Zelle mittig beschnitten.

#### Listenansicht
„Ansicht“ in der Sortierleiste wechselt zwischen Kacheln, Collage und Liste (die Wahl bleibt gespeichert). Die
Liste zeigt dieselbe Seite mit den Spalten Name (mit Vorschaubild), Ordner, Aufgenommen, Geändert, Größe, Typ, Bewertung (Sterne
klickbar), Favorit (Herz klickbar), Tags, Status und je Kategorie eine Spalte (neue Kategorien erscheinen am Ende).
Rechtsklick auf eine Spaltenüberschrift blendet Spalten ein/aus,
Ziehen ändert die Reihenfolge — beides bleibt gespeichert. Klick auf Name, Aufgenommen, Geändert oder Größe sortiert die ganze
Trefferliste (wie „Sortieren“ in der Kopfzeile). Klick auf eine Kategorie-Spalte sortiert ebenfalls die ganze Trefferliste:
Werte in ihrer Reihenfolge von der Seite **Kategorien**, erneuter Klick kehrt um; Dateien ohne Wert stehen immer am
Ende, gleiche Werte nach Datum neu → alt. „Sortieren“ zeigt dann „Kategorie: …“ als Feld (dort auch direkt wählbar);
ein anderes Feld hebt es wieder auf. Die übrigen Spalten sortieren nur die geladene Seite. Auswahl, Aktionsleiste,
Doppelklick/Enter (Lightbox) und Tastenkürzel wie im Grid.

### Lightbox
Rahmenloses Fenster, Toolbar und Titelleiste blenden sich nach kurzer Zeit aus (Mausbewegung oder Taste blendet sie
ein). Bilder: Mausrad = Zoom am Cursor, Ziehen = Verschieben, Doppelklick = Einpassen ↔ 100 %. Videos: Transportleiste
mit Position, Wiedergabe/Pause, Lautstärke, Stumm. Das Info-Panel zeigt Datei- und Medieninfos (Auflösung, Dauer,
Codec) und, sobald es Kategorien gibt, je Kategorie den Wert der Datei („—“ ohne Wert) mit dem Knopf **Zuweisen…**.
Tags und gesetzte Kategorie-Werte stehen in der Leiste unten (seit 0.4.35, „+ Tag / Kategorie“ öffnet die Zuordnung);
ist sie unter Einstellungen › Darstellung abgeschaltet, stehen sie wie früher in der Titelleiste (z. B. „Projekt: Nord · XX: N“).

| Taste | Wirkung |
|---|---|
| ← / → | Vorheriges / nächstes Medium |
| Pos1 / Ende | Erstes / letztes Medium |
| Bild↑ / Bild↓ | 10 Medien zurück / vor |
| F | Gesichtsrahmen ein/aus (Foto; siehe „Personen in Galerie und Lightbox“) |
| F11 | Vollbild ein/aus |
| Esc | Vollbild verlassen, sonst Lightbox schließen |
| I | Info-Panel ein/aus |
| E | Leiste „Verbessern“ ein/aus (Foto; Esc schließt sie zuerst) |
| + / − | Vergrößern / verkleinern (Bild) |
| 1 | 100 % (Bild) |
| 0 | Einpassen (Bild) |
| Leertaste | Wiedergabe / Pause (Video) |
| M | Stumm (Video) |
| Strg+D | Favorit umschalten |
| Strg+1 … Strg+5 | Bewertung 1–5 Sterne |
| Strg+0 | Bewertung löschen |
| Strg+M | In die Bibliothek… |
| Strg+T | Tags der Datei bearbeiten (die Leiste unten zeigt die Tags) |
| Strg+K | Kategorie-Werte der Datei zuweisen (dieselbe Auswahl wie in der Galerie) |
| Entf | In den Papierkorb (mit Bestätigung) |
| S | Diashow ab hier (siehe „Diashow“) |

### Foto verbessern (seit 0.4.46, ADR-0071)
Hochskalieren, Gesichter restaurieren, einfärben, Störendes entfernen, zuschneiden, drehen und Licht — lokal auf diesem PC
(Grafikkarte, sonst Prozessor). **Das Original bleibt immer unverändert**: jedes Ergebnis wird eine neue Datei daneben
(`<Name>_verbessert.jpg`), im Katalog mit dem Original verknüpft und über die Aktivitäten rückgängig machbar (die neue Datei
kommt in den Papierkorb).

- **Ein Foto:** in der Lightbox **Verbessern** (Taste **E**, Knopf „Verbessern“ in der Werkzeugleiste oder Rechtsklick „Bearbeiten ▸ Foto
  verbessern…“). Die Leiste rechts zeigt je Werkzeug, ob sein Modell bereit ist; fehlt es, lädt **Laden…** es nach
  Bestätigung (Name, Quelle, Größe, Lizenz; siehe Einstellungen › Modelle & Daten). Werkzeuge wählen — **Automatisch**
  (Lumora entscheidet je Foto: Einfärben + Kratzer bei Fotos ohne Farbe, Gesichter bei kleinen Gesichtern, Hochskalieren
  bei kleinen Bildern) oder einzeln **Hochskalieren** 2×/4×, **Gesichter** mit Stärke, **Einfärben**, **Entfernen** (mit
  dem Pinsel über das Bild malen, „Strich zurück“, „Kratzer automatisch“) und **Zuschneiden, drehen, Licht** —, dann
  **Vorschau berechnen**. Auf dem Bild zeigt ein Regler Vorher (links) und Nachher (rechts); ziehen oder klicken verschiebt
  ihn. **Als neue Datei speichern** legt die Fassung an, **Verwerfen** löscht die Vorschau. Andere Optionen oder Blättern
  verwerfen eine Vorschau ebenfalls.
- **Natürlichkeit** (seit 0.5.6, beim Hochskalieren und bei „Automatisch“): Das Modell bügelt Haut und feine Strukturen
  gern glatt. **Natürlichkeit** mischt einen Anteil des Originals zurück (Standard 35 %, bis 80 %), **Körnung** legt feines
  Korn darüber — nur in der Helligkeit, vor allem in den Mitteltönen, kaum in Tiefen und Lichtern. Beide Werte merkt sich
  Lumora beim Berechnen (Einstellungen › Verbessern) und nimmt sie auch für mehrere Fotos.
- **Modell beim Hochskalieren** (seit 0.5.7): **Scharf** (Real-ESRGAN x4plus, kräftig geschärft) oder **Natürlich**
  (Real-ESRGAN general x4v3, erhält Hautstruktur und Korn; ≈ 5 MB, „Modell ‚Natürlich' laden…“ direkt in der Leiste). Die
  Wahl merkt sich Lumora; „Automatisch“ nimmt das gewählte Modell und weicht auf das andere aus, wenn nur dieses geladen ist.
- **Mehrere Fotos:** in der Galerie auswählen, Rechtsklick „Bearbeiten ▸ n Fotos verbessern…“: Werkzeuge wählen, die
  Zeitschätzung lesen, **Verbessern**. Der Lauf erscheint unter den Aktivitäten (Fortschritt, Abbrechen); bereits fertige
  werden gespeichert, „Rückgängig“ am Vorgang nimmt alle Fassungen zurück. Die Grafikkarte wechselt sich mit Gesichts- und
  Motiv-Erkennung ab.
- **Galerie:** Original und Fassung tragen die Marke **„2 Fassungen“**; ein Klick darauf öffnet beide in der Lightbox.
  Mit „In der Galerie nur die beste Fassung zeigen“ verschwindet das Original (im Ordner nur, wenn die Fassung im selben
  Ordner liegt) und bleibt über die Marke erreichbar.
- **Einstellungen › Verbessern:** Modelle mit Quelle, Größe, Lizenz und Zustand; Speicherort (neben dem Original oder in
  einem Unterordner), Dateiname-Muster (`{name}` = Name des Originals), Aufnahmedatum/Ort übernehmen (EXIF bei JPEG und
  Katalog), Sterne/Favorit/Tags übernehmen, beste Fassung zeigen, Grafikkarte.
- **Modelle** (je Download nach Bestätigung, SHA-256 geprüft): Real-ESRGAN x4plus (BSD-3, ≈ 70 MB), Real-ESRGAN general
  x4v3 (BSD-3, ≈ 5 MB, ONNX-Umwandlung der Community, fester Commit), GFPGAN v1.4
  (Apache-2.0, ≈ 340 MB; braucht zum Finden der Gesichter die Gesichtserkennung), DeOldify artistic (MIT, ≈ 255 MB),
  LaMa (Apache-2.0, ≈ 210 MB). In einer versteckten Bibliothek bleiben Zwischenstände und Ergebnisse im Container.

### Diashow (seit 0.4.9, ADR-0059)
Vollbild-Diashow ohne Musik auf dem Bildschirm des Hauptfensters, schwarz: Fotos blenden ineinander über und zoomen
langsam (Ken Burns) auf die erkannten Gesichter, ohne Gesichter auf die Bildmitte — nie mit schwarzen Rändern. Unten links
stehen Datum („Samstag, 12. Juli 2025“; bei ungefährem Datum nur „Juli 1982“ bzw. „1982“) und Ort. Videos laufen mit Ton,
höchstens 30 Sekunden (einstellbar), danach geht es weiter; fehlende oder kaputte Dateien werden übersprungen. Nach dem
letzten Bild beginnt sie von vorn.

- **Starten:** Galerie-Leiste **Diashow** — ab zwei ausgewählten Dateien nur die Auswahl, sonst die ganze aktuelle Ansicht
  (Filter, Suche, Ordner, Sortierung; bei einer ausgewählten Datei ab dieser). Rechtsklick auf eine Kachel → **Diashow ab
  hier**. Seite **Alben**: Rechtsklick auf ein Album → **Diashow**. **Lightbox**: Knopf ▶ oder Taste **S** („Diashow ab
  hier“, die Lightbox bleibt offen).
- **Bedienleiste** unten mittig (blendet sich mit dem Mauszeiger nach 2 s ohne Bewegung aus, Bewegung holt sie zurück):
  Zurück, Pause/Weiter, Weiter, Position „12 / 64“, Anzeigedauer 3 s / 5 s / 8 s, **Zoom** an/aus, **Datum & Ort** an/aus,
  Beenden. Der gelbe Balken am unteren Rand zeigt die verbleibende Zeit des Fotos.
- **Einstellungen › Storys › Diashow** (bis 0.4.11 unter „Darstellung“): Anzeigedauer, Reihenfolge (wie die Ansicht oder zufällig, das Startbild zuerst),
  langsamer Zoom, Videos abspielen und Höchstdauer, Datum und Ort einblenden. Änderungen in der Leiste werden ebenfalls
  gemerkt.

| Taste | Wirkung |
|---|---|
| ← / → | Vorheriges / nächstes Foto oder Video |
| Leertaste | Pause / Weiter |
| Pos1 / Ende | Erstes / letztes |
| Esc | Diashow beenden |

### Storys (seit 0.4.15, ADR-0058/0060)
Lumora fasst Fotos und Videos im Hintergrund selbst zu **Storys** zusammen — Ereignisse und Reisen (nach Zeit und Ort,
mehrere Tage weg von zu Hause = Reise), Rückblicke („Heute vor 3 Jahren“, „Oktober in früheren Jahren“), Personen-Storys
(„Anna · 2019“) und Orte-Storys („Grado · 2014–2025“). Überschriften sind nur sachlich (Datum, Orte, Namen); einen eigenen
Titel gibt man selbst. Der Eintrag **Storys** in der Leiste zeigt, wie viele seit dem letzten Besuch neu sind („3 neu“); die
Statusleiste meldet „Storys aktuell“ bzw. „Storys werden berechnet …“.

- **Übersicht:** oben das Banner **Heute vor N Jahren** (nur wenn es an diesem Kalendertag in früheren Jahren genug Fotos
  gibt; **Öffnen** oder **Diashow**), darunter **Angeheftet**, **Neu**, **Reisen & Ereignisse** mit Jahres-Chips,
  **Personen**, **Orte** und **Eigene Storys**. Filter nach Art (Alle, Ereignisse & Reisen, Rückblicke, Personen, Orte), Suche
  nach Ort, Person oder Jahr. Karte anklicken öffnet die Story; überfahren zeigt **Diashow**; Rechtsklick: Öffnen, Diashow,
  Anheften/Lösen, Ausblenden. **Ausgeblendet (n)** zeigt die ausgeblendeten mit **Wieder zeigen**.
- **Story:** großes Titelbild mit Art, Überschrift, Orten, Zählern und Personen; **Titelbild** (dann ein Foto anklicken,
  „Automatisch“ setzt zurück) und **Anheften**. Die Leiste bleibt beim Scrollen oben: **Highlights / Alle Fotos** (mit
  Zählern), Tages-Chips (springen zum Tag, eingeklappte Tage klappen auf), **Diashow** (startet mit den Highlights bzw. im
  Modus „Alle Fotos“ mit allen). Je Tag eine Collage der Highlights — Panoramen über die volle Breite, Hochformate
  nebeneinander, Videos mit ▶ und Dauer, „+4 ähnliche“ bei Serien, beim Überfahren **warum ausgewählt**, ★ für Favoriten;
  **Alle n Fotos dieses Abschnitts** klappt den Tag dicht auf (Fotos, die keine Highlights sind, blass). Lange Reisen zeigen
  die ersten drei Tage, der Rest steht unter „n weitere Tage … **Anzeigen**“. Eine Story über einen einzigen Tag (z. B. ein
  Rückblick) ist nach Tageszeit geteilt (Vormittag, Nachmittag, Abend). **Doppelklick** auf ein Foto öffnet die Lightbox
  mit den Fotos der Story; Rechtsklick: Als Titelbild, Aus Story entfernen. Unten: wie viele Highlights und wie viele
  ähnliche weggelassen, **Als Album speichern**, **Diashow**. Öffnen markiert die Story als gesehen.
- **Menü „…“** am Titelbild: Titel ändern (nur eigener Text; leer = sachliche Überschrift), Titelbild wählen, Fotos
  hinzufügen (in der Galerie auswählen → Rechtsklick **Zur Story „…“ hinzufügen**), Fotos entfernen (anklicken →
  **Entfernen**; sie bleiben in der Galerie), Anheften, Als Album speichern (verknüpft, keine Kopien), In der Galerie zeigen,
  Ausblenden, Neu berechnen, Story löschen (nur eigene Storys und gespeicherte Rückblicke), **Als Collage-Bild
  speichern…** (seit 0.4.24, Dialog wie beim Story-Assistenten mit den Highlights der Story).
- **Galerie:** Rechtsklick auf eine Auswahl → **Als Story…** legt eine eigene Story an (eigener Titel optional) und öffnet
  sie; **In Storys zeigen** listet die Storys, in denen das Foto steht.
- **Einstellungen › Storys:** Ereignisse & Reisen, Rückblicke, Personen- und Orte-Storys an/aus; **Zu Hause** (automatisch =
  häufigster Ort, oder **Ort wählen…** über die Ortssuche); neues Ereignis nach einer Pause von 3/6/12/24 Stunden;
  Mindestgröße 8/12/20/40 Fotos; Highlights knapp (~8 %), automatisch (~15 %) oder großzügig (~30 %); dazu die Karte
  **Diashow**. Jede Änderung gilt sofort, die Storys werden im Hintergrund neu berechnet; angeheftete, ausgeblendete und
  eigene Storys bleiben.

### Nutzung lernen (seit 0.4.45, ADR-0072)
Lumora merkt sich **lokal**, welche Fotos Sie mögen, und wählt „die schönsten“ dann ein wenig mehr nach Ihrem Geschmack
(Story-Highlights, Titelbilder, Collagen, „die schönsten“ im Story-Assistenten).

- **Was zählt:** ein Foto in der Lightbox ansehen (nur solange das Fenster vorne ist, je Ansicht ab 1 s, höchstens 2 min),
  hineinzoomen, an mehreren Tagen wiederkommen, verwenden (zu einem Album oder einer Story hinzufügen, als Titelbild wählen)
  und weitergeben (Strg+C, in ein anderes Programm ziehen, „Kopieren nach…“ außerhalb der Bibliothek, Exportieren).
  **Gedämpft:** Lightbox aus einer Story, Diashow. **Zählt nicht:** Sortieren, Seite „Doppelte“, Durchblättern unter 1 s.
- **Gespeichert** werden nur Tagessummen je Foto im Katalog (eine versteckte Bibliothek nur in ihrem Container) — keine
  Zeitpunkte einzelner Ansichten, nichts im Protokoll, nichts verlässt den PC.
- **Wirkung:** ein kleiner Zuschlag (weniger als ein Favorit) in der Highlight-Auswahl; beim Überfahren eines Highlights
  steht der Grund, z. B. „scharf · oft angesehen“ oder „2× geteilt“. Fotos ohne Nutzung bekommen nie einen Abzug.
- **Vergessene Schätze:** unter den Rückblicken (und unter „Alle“) erscheinen jeden Monat bis zu 24 gute Fotos (Favorit,
  ab 4 Sternen, scharf und gut belichtet oder früher oft angesehen), die vor über einem Jahr aufgenommen und seit einem Jahr
  nicht geöffnet wurden — öffnen, anheften, Diashow wie jeder Rückblick.
- **Galerie › Inhalt › Lieblinge:** nur Fotos, bei denen Lumora eine Vorliebe gelernt hat (z. B. an drei Tagen geöffnet);
  wirkt mit allen anderen Filtern zusammen.
- **Lightbox › Info:** Zeile **Nutzung**, z. B. „9× angesehen an 6 Tagen · 4 min · 1× verwendet“.
- **Einstellungen › Storys › Nutzung lernen:** an/aus (Standard an; aus = nichts mehr messen und das Gelernte nicht
  verwenden), **„Vergessene Schätze“ anbieten**, Zahlen (Fotos, Tage, Lieblinge, seit) und **Gelernte Nutzung löschen…**
  (mit Rückfrage; Favoriten, Sterne, Alben und Storys bleiben).

### Story-Assistent (seit 0.4.24, ADR-0063)
Über der Story-Übersicht steht eine Eingabeleiste: einen Satz in eigenen Worten schreiben, z. B. „Collage mit meiner
Schwester bis 20 Jahre“, „Story von allen Sonnenuntergängen am Meer“, „Diashow: Papa und ich, die besten 50“, „Weihnachten
bei Oma, alle Jahre“ oder „Erstelle mir ein Album mit den schönsten Urlaubsfotos der letzten fünf Jahre mit Nicole und
Nina“ → **Los** (Enter). Eine **lokale Sprach-KI** (Ollama, läuft nur auf diesem PC) übersetzt den Satz in Filter — sie
bekommt nur den Satz, das Datum, die Namen/Rufnamen/Beziehungen der Personen und die Namen von Motiven, Tags und
Kategorien, **nie Fotos, Pfade, Orte oder Geburtsdaten**. Die Fotos sucht Lumora selbst.

- **So habe ich es verstanden — stimmt das?** zeigt jeden Punkt: Personen (mit Beziehung; bei mehreren „alle zusammen auf
  dem Foto“ oder „mindestens eine“), weitere Personen, Zeitraum als konkrete Daten (z. B. „Geburt bis 20 Jahre (aus dem
  Geburtsdatum von Anna)“, „letzte 5 Jahre“, „jedes Jahr 24.–26. Dezember“), Urlaub (Reise-Erkennung der Storys und
  passende Tags/Kategorien wie „Reisen“), Begriffe mit ihren Treffern („Barcelona: Tag (212) · Ort (180)“, Standard: beide;
  „nicht gefunden“ ist markiert), Medien, Auswahl („die besten 50“ — bei „die schönsten“ ohne Zahl schlägt Lumora eine
  Anzahl vor) und was entsteht. **Jeder Punkt lässt sich anklicken und ändern** — Lumora zählt dann neu. Dazu die Trefferzahl
  und ein Vorschau-Streifen.
- **Erstellen** legt erst jetzt etwas an: eigene Story (sachliche Überschrift wie „Anna · 1985–2005“, änderbar), Album
  („Nicole und Nina · Urlaub 2023–2025“), Diashow, Collage-Bild (Dialog, wahlweise zusätzlich als Story) oder nur die
  Galerie. **Nur Fotos zeigen (Galerie)**, **Satz ändern**, **Abbrechen**.
- **Rückfrage**, wenn etwas mehrdeutig ist („Wer ist „Oma“?“ mit Gesichtern und Fotozahlen, „Beide“) oder fehlt (Geburtsdatum,
  Zuhause für Urlaub). Antworten auf „Wer ist …?“ merkt sich Lumora; unter Einstellungen › Assistent lassen sie sich
  vergessen.
- **Zuletzt gefragt**: die letzten Sätze mit **Öffnen** bzw. **Wieder abspielen** (ohne die Sprach-KI erneut zu fragen),
  einzeln löschbar.
- **Als Collage-Bild speichern**: Format 16:9 / A4 quer / A4 hoch / Quadrat, Größe Full HD / 4K / Druck 300 dpi, 12/24/36/48
  Fotos, Anordnung Großes Titelbild / Raster / Zeitstrahl, Überschrift (sachlich, änderbar) und Datum je Foto an/aus,
  Speicherort (Standard `Bilder\Lumora\Collagen`, in einer versteckten Bibliothek nur im Container unter
  `<Bibliothek>\Collagen`). **Neu mischen** wählt andere gute Fotos, ein Klick auf ein Foto tauscht es. Gerendert wird aus
  den Originalen in voller Qualität (JPEG oder PNG).
- **Einstellungen › Assistent**: Assistent an/aus, Zustand von **Ollama** mit **Ollama installieren…** (über winget, erst
  nach Bestätigung mit Name, Quelle, Größe und Lizenz) bzw. **Ollama starten**, **Sprachmodell** (Standard `qwen2.5:3b`,
  ca. 1,9 GB; Alternative `qwen2.5:7b`, ca. 4,7 GB; eigener Name möglich) mit **Herunterladen…** (genaue Größe wird vorher
  abgefragt, Fortschritt, Abbrechen), **Grafikkarte nutzen** (Gesichts- und Motiv-Erkennung warten kurz, solange die
  Sprach-KI rechnet), **Zuletzt gefragte Sätze merken** + **Verlauf löschen**, gemerkte Antworten, **Ausprobieren** (zeigt nur,
  wie ein Satz verstanden wird). Ohne Ollama verstehen einfache Sätze („Anna 2019“, „Album mit allen Fotos vom Hund 2022“)
  trotzdem.

### Archive
Archive (`.zip .rar .7z .tar .gz .tgz .bz2 .xz`) nimmt der Scan wie Fotos in den Katalog auf; sie bekommen kein
Vorschaubild, die Kachel zeigt ein Archiv-Symbol mit Endung und Größe. Die Dateiart **Archive** in der Zeile „Datei“ zeigt nur sie.
- **Inhalt ansehen:** Enter/Doppelklick (oder Kontextmenü „Inhalt anzeigen“) öffnet das Archiv in der Lightbox als Liste
  der Einträge mit Größe, komprimierter Größe und Datum; Fotos/Videos und Archive im Archiv sind markiert. Knöpfe
  **Entpacken…** und **Im Explorer anzeigen**.
- **Entpacken…** (Aktionsleiste für alle ausgewählten Archive, Kontextmenü der Kachel oder Lightbox) öffnet einen Dialog:
  Ziel (Standard: Unterordner mit dem Archivnamen neben dem Archiv, oder ein eigener Ordner), vorhandene Dateien
  überspringen / überschreiben / umbenennen („Name (2).jpg“), nur Fotos und Videos (Standard) oder alles, Archive im
  Archiv mitentpacken (bis 3 Ebenen, die Zwischen-Archive werden danach entfernt), Archiv danach in den Papierkorb
  (nur wenn fehlerfrei; protokolliert wie jedes Löschen). Fortschritt mit Dateien und Bytes, **Abbrechen** jederzeit;
  am Ende Zusammenfassung und Fehlerliste. Danach aktualisiert die App die Bibliothek (Incremental-Scan), die entpackten
  Dateien erscheinen sofort in der Galerie.
- Passwortgeschützte Archive werden angezeigt, aber mit klarer Meldung nicht entpackt (kein Passwort-Dialog); Einträge
  mit Pfaden außerhalb des Zielordners („Zip-Slip“, `..\..\`) werden übersprungen und als Fehler gemeldet.
- Entpackt wird in der App über SharpCompress (keine Installation von 7-Zip/WinRAR nötig, ADR-0022).
- **Automatisch entpacken** (0.2.25, ADR-0032; **je Bibliothek** seit 0.2.31, ADR-0033; Standard aus): Einstellungen ›
  **Archive** → „Neue Archive in Eingangsordnern automatisch entpacken“, optional „Archiv nach fehlerfreiem Entpacken in
  den Papierkorb verschieben“ — beide Schalter gelten für die **aktive Bibliothek** (der Abschnitt nennt sie); für eine
  andere Bibliothek diese zuerst aktivieren (Umschalter oben links). Eine bis 0.2.30 global eingeschaltete Einstellung
  übernehmen beim ersten Start alle bestehenden Bibliotheken; neue Bibliotheken starten mit „aus“.
  Findet „Aktualisieren“ oder die Ordnerüberwachung ein **neues** Archiv in einem **Eingangsordner**, entpackt die App es
  nach dem Scan vollständig (alle Dateien, verschachtelte Archive bis 3 Ebenen) in einen Ordner mit dem Archivnamen
  daneben und aktualisiert die Bibliothek erneut. Unberührt bleiben: Archive im Archivordner, Archive, die schon vor dem
  Einschalten im Katalog standen, Archive in bereits entpackten Ordnern und Archive, deren Zielordner schon existiert.
  Passwortgeschützte oder beschädigte Archive bleiben liegen; die Statusleiste zeigt Fortschritt und Ergebnis, gemeldete
  Archive mit Grund im Tooltip.
- **Vorhandene Archive in Eingängen jetzt entpacken…** (Knopf im selben Abschnitt, nur bei eingeschaltetem Schalter):
  holt das Entpacken für Archive nach, die schon vor dem Einschalten im Katalog standen — nennt die Anzahl, fragt nach
  und entpackt wie die Automatik (Zielordner neben dem Archiv, Papierkorb nach Einstellung, danach ein Aktualisieren).
  Archive, deren Zielordner schon existiert (z. B. früher manuell entpackt), zählen nicht.

### Ordner (Ordnerleiste der Galerie)
Seit 0.4.44 (ADR-0068, Mockup 18) gibt es keine eigene Seite „Ordner“ mehr: Der Ordnerbaum der aktiven Bibliothek
(Dateizähler direkt / gesamt) ist die **Leiste links in der Galerie**; **Strg+B** oder die Taste im Kopf der Leiste klappt sie
zu und wieder auf (Lumora merkt sich das). Darin stehen Suche, Umschalter „mit Unterordnern“, „Eingang hinzufügen…“, der
Ausschlüsse-Hinweis mit „verwalten“, das Ergebnisfeld und darunter die Alben. Klick auf einen Ordner setzt den Chip
„Ordner: …“ der Galerie; „Alle Fotos der Bibliothek“ (bis 0.4.37 „Gesamte Bibliothek“) hebt den Ordnerfilter auf. Fotos auf
einen Ordner ziehen: ohne Taste verknüpfen, mit Umschalt verschieben (ADR-0054). Im Kontextmenü neu: **Nur diesen Ordner
zeigen** (ohne Unterordner), **Diashow**, **Neuer Unterordner…**, **Titelbild des Ordners ▸** (eigene Wahl oder wieder
automatisch), **Alle Fotos taggen / kategorisieren ▸** (Tags, Kategorie, Bewertung, Favorit, Datum für alle) und **Neu
einlesen** (nur dieser Ordner samt Unterordnern). Nach Scans und Datei-Operationen lädt der Baum neu und
behält Aufklapp-Zustand und Auswahl. Oberste Knoten sind die Wurzeln mit Rolle (Bibliotheksordner, Eingang). Kontextmenü:
**Neuer Ordner…** (Ordner auf der Platte + Katalogzeile), **Umbenennen…** (nicht für Wurzeln; Ordner mit Dateien
werden protokolliert umbenannt und lassen sich mit Rückgängig zurücknehmen), **In die Bibliothek übernehmen…** (nur
unter einem Eingang, siehe „Eingang und Bibliotheksordner“) sowie **Bewertung für alle Dateien** (1–5 Sterne oder
entfernen), **Favorit für alle**, **Kategorie für alle…** und **Tags für alle…** — wirken auf alle Dateien des Ordners
samt Unterordnern, nach einer Bestätigung mit Anzahl. Das Ergebnis steht über dem Baum (✕ blendet es aus).
Seit 0.4.30 (ADR-0065) außerdem: **In den Papierkorb…** — der Ordner samt Inhalt (auch Nicht-Medien und Unterordner)
kommt in den Windows-Papierkorb; die Bestätigung nennt Dateien auf der Platte, davon in der Bibliothek, Unterordner und
was an Benutzerdaten dranhängt (Sterne/Favorit, benannte Personen, Alben, Tags/Kategorien). **Rückgängig** im Ergebnisfeld
(oder in der Galerie) holt Ordner und Dateien zurück und bringt die Katalogeinträge mit denselben Ids und allen Angaben
wieder. Nicht möglich für die Wurzel selbst und Ordner, in denen eine Wurzel liegt (Hinweis auf Einstellungen ›
Bibliotheken & Eingänge), in „Alle verknüpften“ und bei nicht erreichbarem Laufwerk. Hat das Laufwerk **keinen
Papierkorb** (Netz, USB-/VeraCrypt-Laufwerk), fragt Lumora eigens „Dieses Laufwerk hat keinen Papierkorb — endgültig
löschen?“ mit eigenem Knopf; sonst passiert nichts (endgültig gelöscht lässt sich nicht zurücknehmen). **Fehlende
entfernen (n)** erscheint, wenn der Ordner (samt Unterordnern) n Dateien hat, die es auf der Platte nicht mehr gibt: Lumora
vergisst diese Einträge (keine Datei wird gelöscht) und nimmt danach leere, nicht mehr vorhandene Ordner aus dem Baum.
Seit 0.4.33 (ADR-0066): **Verschieben nach…** — Zielordner derselben Bibliothek im Windows-Ordnerdialog wählen; oder einen
Ordner im Baum **auf einen anderen ziehen** (das Ziel bekommt einen Rahmen, Esc bricht ab, Ablegen fragt nach). Alle Angaben,
Alben und Verknüpfungen bleiben (dieselben Einträge, nur der Pfad ändert sich); **Rückgängig** im Ergebnisfeld oder in der
Galerie verschiebt zurück. Nicht möglich: Wurzel, in sich selbst, gleichnamiger Ordner im Ziel (kein Zusammenführen), andere
Bibliothek, anderes Laufwerk (dafür „In die Bibliothek übernehmen…“), „Alle verknüpften“. **Ordner ausschließen…** — der
Ordner samt Unterordnern wird künftig nicht mehr eingelesen und verschwindet aus dem Baum (oben steht dezent „n Ordner
ausgeschlossen“); sind darin schon Dateien in der Bibliothek, fragt Lumora mit Zahlen: „Aus der Bibliothek entfernen“ (keine
Datei wird gelöscht; Rückgängig bringt die Einträge zurück und hebt den Ausschluss wieder auf) oder „Nur künftig ausschließen“.
Aufheben unter Einstellungen › Bibliotheken & Eingänge › Ausschlüsse beim Einlesen.
**Eingang hinzufügen…** oben neben „Ordner“ nimmt einen unsortierten Quellordner als Eingang der aktiven Bibliothek auf
(gleicher Ablauf wie in den Einstellungen, danach Angebot „Jetzt aktualisieren“; die Seite bleibt offen).

### Rechtsklick-Menü, Titelbild und Fotos nach außen (seit 0.4.44, ADR-0068)
Kachel, Listenzeile, Lightbox und Story-Fotos haben **dasselbe Menü**, gegliedert wie in Mockup 17: Öffnen und Diashow (bei
Archiven oben „Inhalt zeigen“, „Entpacken…“, „In die Bibliothek…“) · Favorit, Bewertung, Tags, Kategorie · **Zu Album ▸**,
Als Story · **Als Titelbild für ▸** · Datum setzen, Ort setzen · **Drehen** · **Suchen ▸** (gleicher Tag, gleicher Ort,
Doppelte, gleiche Personen, ähnliche Motive — wirkt als Filter zusammen mit den übrigen) · **Datei ▸** (Kopieren Strg+C,
Kopieren nach…, Exportieren (verkleinert)…, Im Explorer anzeigen, Verschieben…, Umbenennen…) · In den Papierkorb ·
**Eigenschaften** (Alt+Enter). Bei mehreren ausgewählten Fotos wirken die Aktionen auf alle; Titelbild, Eigenschaften und
Gesichter zeigen gibt es nur für ein Foto.
- **Als Titelbild für ▸** zeigt die Personen im Foto, die Alben und Storys mit dem Foto und seinen Ordner; ein Häkchen
  markiert das aktuelle Titelbild, „wieder automatisch“ nimmt die eigene Wahl zurück. Bei einer Person wird **ihr Gesicht in
  diesem Foto** das Titelbild. Ohne eigene Wahl wählt Lumora selbst: bei Personen das beste Gesicht (Gesichtsgüte, frontal,
  Foto-Güte), bei Alben, Storys und Ordnern ein gutes Foto mit Personen. Die eigene Wahl gewinnt immer.
- **Drehen** (nur JPEG) schreibt die Ausrichtung verlustfrei in die Datei; Rückgängig in der Aktivitäts-Anzeige oder in der
  Galerie stellt die Datei Byte für Byte wieder her.
- **Kopieren nach…** legt Kopien in einem gewählten Ordner an (bei gleichem Namen „Name (2).jpg“); Bewertung, Tags und
  Personen werden **nicht** mitkopiert. Rückgängig legt die Kopien in den Papierkorb.
- **Exportieren (verkleinert)…** speichert JPEGs mit wählbarer langer Kante (1024–3840 px) in einen Ordner außerhalb der
  Bibliothek, z. B. zum Verschicken; Videos werden übersprungen.
- **In andere Programme:** Fotos aus Galerie oder Lightbox in den Explorer, eine Mail oder ein Office-Dokument ziehen oder
  mit **Strg+C** kopieren und dort einfügen — es wird immer **kopiert**, nie verschoben. Fehlende Dateien und Archiv-Einträge
  werden übersprungen (mit Zahl). In der **versteckten Bibliothek** ist das gesperrt: Fotos verlassen den Container nicht.

### Alben und verknüpfte Fotos (seit 0.3.2, ADR-0054)
Ein Foto liegt immer nur **einmal** auf der Platte. Alben und Ordner-Verknüpfungen zeigen es zusätzlich an anderer Stelle —
nichts wird kopiert, nichts verschoben; „Verknüpfung entfernen“ und „Album löschen“ löschen nie ein Foto.
- **In einen anderen Ordner verknüpfen**: Kacheln aus der Galerie auf einen Ordner im Ordnerbaum ziehen (ohne Taste, mit
  Alt oder Strg = **verknüpfen**; mit **Umschalt = verschieben**, nach Rückfrage, rückgängig machbar) oder Rechtsklick
  **Hier verknüpfen…** (Ordner wählen). Nur innerhalb einer Bibliothek. Der Ordner zeigt dann seinen echten Inhalt plus
  die verknüpften Fotos mit dem Kennzeichen **„verknüpft“** (Ketten-Symbol; in der Liste vor dem Namen). Unter dem Titel:
  **mit Verknüpfungen** / **nur echte Dateien** und der Zähler „12 Dateien · 5 verknüpft“. Rechtsklick auf eine verknüpfte
  Kachel: **Verknüpfung entfernen**.
- **Eigene Alben**: Seite **Alben** → **Neues Album…**; Fotos hinzufügen in der Galerie per Rechtsklick **Zu Album
  hinzufügen**, Aktionsleiste **Album**, **Strg+L** (Albumname eingeben — vorhandenes oder neues Album) oder per Ziehen
  auf ein Album der Liste „Alben“ unter dem Ordnerbaum. Klick auf eine Album-Kachel öffnet die Galerie gefiltert auf das
  Album (Chip **Album** in der Zeile „Inhalt“, Bedingung „Album …“ in „Es gilt“ — mit allen übrigen Filtern und
  Ansichten kombinierbar). In der Album-Ansicht: **Aus Album entfernen**, **Als Titelbild des Albums**. Rechtsklick auf
  eine Album-Kachel: Umbenennen, Titelbild zurücksetzen, **Album exportieren…** (echte Kopien in einen gewählten Ordner,
  mit Fortschritt und Abbrechen; in einen Bibliotheksordner nur nach Rückfrage; in einer versteckten Bibliothek gesperrt),
  Album löschen.
- **Intelligente Alben**: in der Galerie Filter setzen (Dateiart, Favoriten, Bewertung, Ablage, Ort, Tags, Personen,
  Beziehung, Kategorien, Motive, Suche, Ordner …) und in der Zeile „Es gilt“ **Als Album speichern…** — das Album füllt
  sich selbst. Öffnen lädt den Filter in die Galerie; ändern und unter demselben Namen speichern ersetzt ihn (Rückfrage).
- **Sicherheit**: Verschieben, Umbenennen, Sortieren und „Ordner übernehmen“ behalten alle Verknüpfungen. Papierkorb
  entfernt die Verknüpfungen mit; kommt dieselbe Datei an ihren Ort zurück (Windows „Wiederherstellen“ + Aktualisieren)
  bzw. macht man ein Verschieben aus der Bibliothek rückgängig, sind sie wieder da. „Neu aufbauen“ behält Alben und
  Verknüpfungen. Alben gehören wie Tags der Bibliotheks-Gruppe (Verknüpfen führt gleichnamige eigene Alben zusammen,
  Lösen kopiert sie).

### Sortieren
- **Quelle und Ziel** (beide Bereiche): je eine Auswahl der Wurzeln der aktiven Bibliothek mit Rolle („Eingang“ /
  „Bibliotheksordner“) und Pfad, darunter ein frei wählbarer Ordner. Vorbelegt ist der erste erreichbare Eingang als Quelle und
  den Bibliotheksordner als Ziel — der Normalfall „aus dem Eingang in die Bibliothek sortieren“. Als Ziel geht auch
  „Quellordner“ (die Ordner entstehen an Ort und Stelle). Offline-Wurzeln sind ausgegraut; Ziele außerhalb aller
  Wurzeln sind gesperrt, weil die Dateien sonst aus dem Katalog fielen und Bewertung, Favorit und Tags verlören
  (ADR-0024). Die Auswahl folgt Bibliothekswechseln und geänderten Wurzeln. Verschobene Dateien behalten ihre FileId
  und wechseln im Katalog zur Zielwurzel (Galerie-Filter „Noch im Eingang“ / „In der Bibliothek“).
- **Sortieren**: Quellordner unter einer Wurzel der Bibliothek wählen, Patterns (Regex auf den Dateinamen ohne Endung, nach
  Priorität) und optional Sub-Level-Patterns (auf den vollen Dateinamen) pflegen — Profile liegen in der Datenbank,
  Import/Export im V2-JSON-Format. **Analysieren** zeigt die Gruppen (Zielordner änderbar, Gruppen zusammenführbar),
  **Vorschau** die Quelle → Ziel-Liste, **Sortieren** führt aus; Dateien ohne Treffer landen in `_unsorted`.
  **Rückgängig (letzter Lauf)** nimmt alle Verschiebungen des letzten Laufs zurück. Die eingebaute Hilfe erklärt die
  Regex-Grundlagen.
- **Flatten & Move**: Mediendateien aus Unterordnern beliebiger Tiefe nach `Ziel\Ebenenname\Datei` verschieben
  (Ebenenname aus den Pfad-Ebenen, Duplikate entfernt, Separator wählbar; Dateien in der Quellwurzel → `_root`;
  Namenskonflikte → `_1`, `_2`, …), optional direkt in die Zielwurzel und leere Quellordner löschen. Vorschau, Ausführen,
  Rückgängig.

### Tags (Einstellungen)
Verwaltung der Tags: neues Tag anlegen (Enter), je Tag Farbe aus der Palette, nach oben/unten (Reihenfolge in Menüs,
Chips und Filter), Umbenennen, Löschen (fragt nach und nennt die Zahl der Dateien; die Dateien selbst bleiben
unverändert). Neben dem Namen steht, wie vielen Dateien das Tag zugewiesen ist.

### Kategorien (Einstellungen)
Verwaltung der Kategorien (ADR-0027): neue Kategorie mit Namen und optional gleich ihren Werten („N, NN“) anlegen
(Enter); je Kategorie eine Karte mit Umbenennen, Löschen (fragt nach und nennt die Zahl der Dateien) und Reihenfolge
(Filter, Spalten, Kontextmenü). Werte: hinzufügen (mehrere durch Komma), umbenennen, nach oben/unten, löschen (betroffene
Dateien haben danach keinen Wert in dieser Kategorie). Neben jedem Wert steht, wie vielen Dateien er zugeordnet ist.

### Einstellungen
Reiter oben (ADR-0042):
- **Bibliotheken & Eingänge**, **Tags**, **Kategorien** — siehe die Abschnitte oben.
- **Gesichter** — Gesichtserkennung, Grafikkarte, Mindestgröße, Modell, Index neu aufbauen, Gesichtsdaten löschen;
  Karte **Personen & Beziehungen** (Beziehungen umbenennen, zusammenführen, löschen).
- **Archive** — automatisch entpacken (je Bibliothek), vorhandene Archive nachholen.
- **Darstellung** — Farbschema (Hell/Dunkel/System folgt Windows live); Karte **Karte (Seite „Orte“)**: Kartenstil
  und die API-Schlüssel für CARTO und MapTiler (seit 0.4.1, siehe „Kartenstile: Schlüssel anfordern“).
- **Storys** (seit 0.4.15) — Story-Arten an/aus, Zu Hause, Pause, Mindestgröße, Highlights-Anteil und die Karte
  **Diashow** (siehe „Storys“).
- **Modelle & Daten** (seit 0.4.42, ADR-0070, Mockup 19) — alle Bausteine, die Lumora zusätzlich braucht, in drei Gruppen:
  **Modelle** (Gesichtserkennung InsightFace buffalo_l, Motiv-Erkennung SigLIP 2; später die Modelle von „Foto
  verbessern“), **Daten** (Ortsnamen GeoNames — eingebaut; Kartenkacheln — bei Bedarf, mit Größe des Zwischenspeichers)
  und **Extern** (Sprachmodell über Ollama — nur Status, Verwaltung in Einstellungen › Assistent). Je Eintrag Zweck,
  Größe, Version, Lizenz (eingeschränkte wie „nur nicht-kommerziell“ hervorgehoben), Quelle mit Website, Speicherort,
  Zustand (nicht geladen / geladen / Update verfügbar / defekt) und die Aktionen **Laden…** (erst nach Bestätigung mit
  Name, Quelle, Größe, Lizenz und Ziel; Fortschritt in der Zeile und unten rechts in den Aktivitäten, abbrechbar —
  angefangene Dateien werden verworfen; danach SHA-256-Prüfung), **Prüfen** (SHA-256 aller Dateien; falsche → „defekt“,
  dann **Neu laden…**), **Update suchen** (nur auf Klick, fragt beim Hersteller nur die Dateiliste ab; ohne Quelle
  „Version x, keine automatische Prüfung“) und **Entfernen…** (löscht nur die Dateien des Modells, was schon erkannt ist,
  bleibt). Lumora lädt nie von selbst. Die versteckte Bibliothek nutzt dieselben Modelle aus dem normalen Modellordner.
- **Speicher & Wartung** — Speicherorte mit „Ordner öffnen“ und **Ändern…** (siehe „Speicherort des Katalogs“),
  Thumbnail-Cache (Belegung, **Cache leeren** — Vorschaubilder werden bei Bedarf neu erzeugt), Karte **Fehlende Dateien**
  mit **Alle fehlenden entfernen (n)** für die aktuelle Bibliothek (seit 0.4.30, siehe „Fehlende Dateien entfernen“),
  Karte **Katalog-Sicherung** (seit 0.4.40: jetzt sichern, Liste, Ordner öffnen, Wiederherstellen…, siehe
  [Produktivbetrieb](#produktivbetrieb)), FFmpeg-Status mit kopierbarem winget-Befehl, unterstützte Bild- und Videoformate.
- **Info** — Kopf mit App-Symbol und Schriftzug, Version (= oberster Eintrag der CHANGELOG), Build-Datum, Debug/Release; Entwicklung, dieses System (.NET,
  Windows, Grafikkarte, Gesichtsmodell, Katalogzahlen, **Datenordner** Standard/Portabel/Variable ohne Pfad), Speicherorte zum Öffnen, verwendete Bausteine mit Versionen und
  **Alle Lizenzen anzeigen…**; **Versionsinfo kopieren** (für Fehlermeldungen, ohne Pfade) und **Änderungsprotokoll**
  (öffnet die CHANGELOG.md neben der App).

#### Fehlende Dateien entfernen (seit 0.4.30, ADR-0065)
Findet „Aktualisieren“ eine Datei nicht mehr, bleibt ihr Eintrag mit Sternen, Personen, Alben usw. als **„fehlt“** stehen —
sie könnte ja zurückkommen (Laufwerk wieder da, Datei zurückverschoben). Wer sie wirklich los ist, lässt Lumora die Einträge
**vergessen** — es wird **keine Datei gelöscht**. Drei Wege: Reiter **Ordner** › Rechtsklick › **Fehlende entfernen (n)**
(Ordner samt Unterordnern), **Galerie** › Filter **Ablage: Nur fehlende**, Auswahl › **Aus der Bibliothek entfernen** (nur
wenn alle gewählten fehlen; bei gemischter Auswahl kommt ein Hinweis) und **Einstellungen › Speicher & Wartung** › **Alle
fehlenden entfernen (n)**. Die Bestätigung nennt die Zahl der Dateien und davon die mit Sternen/Favorit, benannten Personen,
in Alben und mit Tags/Kategorien — diese Angaben gehen verloren; **Rückgängig** bringt sie zurück (Galerie bzw. das
Ergebnisfeld), seit 0.4.39 auch mit dem bisherigen Vorschaubild (30 Tage aufbewahrt; danach zeigt die Kachel den Platzhalter
mit „fehlt“). Dasselbe gilt für „Ausgeschlossene entfernen“ und „Ordner in den Papierkorb“. Sicherheit: Ist das Laufwerk einer Wurzel **nicht erreichbar**, entfernt Lumora dort nichts („Laufwerk
nicht erreichbar — Dateien gelten als offline, nicht als fehlend“); vor dem Entfernen prüft es jede Datei noch einmal —
ist sie wieder da, bleibt sie und „fehlt“ verschwindet. Gilt nur im aktuellen Bereich; in „Alle verknüpften“ gesperrt;
in einer versteckten Bibliothek nur in ihrem eigenen Katalog.

#### Speicherort des Katalogs
**Einstellungen › Speicher & Wartung → Speicherorte → Datenbank → Ändern…** zieht Katalog (`library.db`) und Vorschaubilder (`thumbs\`) in
einen anderen Ordner (ADR-0026); **Standardort** holt sie nach `%LocalAppData%\Lumora` zurück.
- Der Ordner muss auf einem lokalen Laufwerk liegen (interne Platte, SSD, USB) — Netzlaufwerke und `\\Server\…` werden
  abgelehnt, ebenso Ordner in einer Bibliothekswurzel (die Vorschaubilder würden sonst als Fotos eingelesen). Er muss
  beschreibbar sein und genug Platz bieten (Katalog + Vorschaubilder + 10 % + 50 MB). OneDrive-Ordner gehen, aber mit
  Warnung: die Synchronisierung kann den laufenden Katalog beschädigen.
- Liegt im Ziel schon ein Katalog: **Übernehmen** (Lumora arbeitet danach mit diesem; der bisherige bleibt
  unverändert liegen) oder **Ersetzen** (der vorhandene wird beiseitegelegt und beim nächsten Start gelöscht).
- Ablauf: laufende Scans und Datei-Operationen werden abgewartet (höchstens 30 s), Ordnerüberwachung und Vorschaubilder
  angehalten, der Katalog per SQLite-Backup kopiert und geprüft (Integrität, Zeilenzahl jeder Tabelle), dann die
  Vorschaubilder kopiert. Erst danach gilt der neue Ort; Lumora startet neu und löscht beim Start die alten Dateien.
  Während des Umzugs ist das Fenster gesperrt.
- Geht etwas schief (Platte voll, Prüfung fehlgeschlagen, Scan läuft noch), wird das Ziel aufgeräumt und Lumora
  arbeitet mit dem bisherigen Ort weiter; ein Dialog nennt den Grund.
- Ist der gewählte Ordner beim Start nicht erreichbar (USB-Laufwerk abgezogen), startet Lumora nicht und nennt die
  Datei `database-location.json`, deren Löschen zum Standardort zurückführt.

### Gesichtserkennung
Erkennt im Hintergrund Gesichter in den Fotos der aktiven Bibliothek und speichert je Gesicht Position, Qualität und
einen Fingerabdruck (512 Werte) im Katalog (ADR-0035) — vollständig auf diesem PC. Benennen und Vorschläge: Abschnitt
„Personen“; Filter, Suche und Lightbox-Rahmen: „Personen in Galerie und Lightbox“.
- **Einstellungen › Gesichter**: „Gesichter im Hintergrund erkennen“ (Standard **aus**), „Grafikkarte
  verwenden“ (Standard an; DirectML, bei Problemen automatisch Prozessor), Modellstatus, **Index neu aufbauen**
  (analysiert alle Fotos erneut; bestätigte Zuordnungen bleiben).
- **Kleine Gesichter automatisch ignorieren** (Standard **kleiner als 24 Pixel**; Aus, 16 … 64 Pixel): sehr kleine
  Gesichter (meist Menschen im Hintergrund) gelten als ignoriert — keine Vorschläge, keine Gruppen, nicht in Galerie-
  Filtern. Gemessen wird die Gesichtsgröße im auf 1280 Pixel verkleinerten Foto. Ein anderer Wert oder „Aus“ holt die
  automatisch ignorierten im Hintergrund zurück; selbst ignorierte und bestätigte Gesichter fasst die Einstellung nie an
  (ADR-0040).
- **Modell**: InsightFace *buffalo_l* (Erkennung SCRFD `det_10g.onnx`, Fingerabdruck ArcFace `w600k_r50.onnx`). Fehlt
  es, steht dort **„Modell herunterladen… (275 MB, InsightFace buffalo_l, nur nicht-kommerzielle Nutzung)“**: nach
  Bestätigung lädt Lumora das Paket von GitHub (InsightFace Release v0.7), prüft die SHA-256-Prüfsumme und entpackt
  nur die zwei benötigten Dateien nach `%LocalAppData%\Lumora\models\buffalo_l\` (Fortschritt, abbrechbar; seit 0.4.42 über
  Einstellungen › Modelle & Daten mit SHA-256 der entpackten Dateien und Eintrag in den Aktivitäten). Ohne
  Klick wird nichts geladen. Die Modelle sind nur für die nicht-kommerzielle Nutzung freigegeben und liegen nie im Repo
  oder im portablen Paket.
- **Aktivitäten** (unten rechts, seit 0.4.36): „Gesichter erkennen: 1.234 von 19.556“ mit **Pause**/**Fortsetzen** und **Abbrechen** (bis zum
  nächsten Scan oder Neustart). Der Index startet 10 s nach dem App-Start, nach jedem Scan und bei neuen Dateien,
  arbeitet die neuesten Fotos zuerst ab und läuft mit niedriger Priorität.
- Videos werden noch nicht analysiert; Fotos, die WIC nicht dekodieren kann (z. B. HEIC ohne Codec), werden als
  „nicht dekodierbar“ vermerkt. „Neu aufbauen“ der Bibliothek behält alle Gesichter (kein erneutes Analysieren).

### Motive (seit 0.2.55, ADR-0052)
Erkennt, was auf den Fotos zu sehen ist — Sonnenuntergang, Meer, Hund, Torte, Quittung … — vollständig auf diesem PC.
Je Foto (Videos: Vorschaubild) entsteht im Hintergrund ein Bild-Vektor im Katalog; die Begriffe werden damit verglichen.
- **Einstellungen › Motive**: „Motive im Hintergrund erkennen“ (Standard an — läuft erst mit Modell), „Grafikkarte
  verwenden“, Modellstatus, Index mit **Neu analysieren**, **Schwelle** für alle Begriffe (Standard 1 %; kleiner = mehr
  Treffer). Darunter die **Begriffe** der aktiven Bibliothek (verknüpfte Bibliotheken teilen sie) mit Anzahl Treffern: **anlegen** (Begriff + optionale
  Beschreibung, englisch trennt am besten, z. B. „a photo of a lighthouse“), **umbenennen**, **Beschreibung ändern**,
  **eigene Schwelle**, **löschen**. Neue oder geänderte Begriffe wirken sofort aus den gespeicherten Vektoren — kein Foto
  wird dafür neu analysiert. Beim ersten Start legt Lumora je Bibliothek (bzw. Verknüpfungsgruppe) ≈ 80 Begriffe an.
- **Modell**: Fehlt es, steht dort **„Motiv-Modell herunterladen… (≈ 690 MB, SigLIP 2 von Google, Apache-2.0)“**: nach
  Bestätigung (Dateien, Quelle, Größen, Lizenz) lädt Lumora SigLIP 2 base als ONNX von Hugging Face
  (onnx-community/siglip2-base-patch16-224-ONNX), prüft jede Datei per SHA-256 und legt sie unter
  `%LocalAppData%\Lumora\models\siglip2-base-patch16-224\` ab (Fortschritt, abbrechbar; seit 0.4.42 über Einstellungen ›
  Modelle & Daten, dort auch „Update suchen“). Ohne Klick wird nichts
  geladen, ohne Modell ist die Motiv-Erkennung aus. Das Modell liegt nie im Repo oder im portablen Paket.
- **Galerie**: Chip **„Motive“** in der Zeile „Inhalt“ (sobald es Treffer gibt): Begriffe mit Anzahl, „eines der
  gewählten“ oder „alle zusammen“; oben im Flyout **Bildinhalt beschreiben** („Hund am Strand“ → die 200 ähnlichsten
  Fotos, auch ohne passenden Begriff). Die Bedingung steht in „Es gilt“. Die **Suche** findet Begriffe mit
  (`strand` → Fotos mit dem Motiv „Strand“), **„Motiv: Hund“** sucht nur in den Motiven. Listenspalte **„Motive“**.
- **Lightbox** (Info-Panel, Taste I): Abschnitt „Motive: Strand, Meer, Sonnenuntergang“ (wahrscheinlichste zuerst).
- **Aktivitäten** (unten rechts, seit 0.4.36): „Motive erkennen: 1.234 von 19.556“ mit Pause/Fortsetzen/Abbrechen. Startet 15 s nach dem
  App-Start (bestehende Kataloge werden nachgeholt), nach jedem Scan und bei neuen Dateien; ruht während eines Scans und
  rechnet nie gleichzeitig mit der Gesichtserkennung auf der Grafikkarte.

### Personen
Gefundene Gesichter benennen; Lumora lernt daraus und schlägt die Personen auf weiteren Fotos vor (ADR-0036/0037).
Gelernt wird **nur aus dem, was du bestätigst** — eigene Vorschläge lernen nie mit.
1. **Einschalten:** Einstellungen › Gesichter → „Gesichter im Hintergrund erkennen“ (einmalig das Modell
   herunterladen). Die Statusleiste zeigt den Fortschritt; die Seite **Personen** zeigt oben, wie viele Fotos schon
   analysiert sind, und weist darauf hin, wenn die Erkennung aus ist („Einstellungen öffnen“).
2. **Gruppen benennen:** Unter „Unbenannte Gruppen“ stehen Gesichter, die vermutlich dieselbe Person zeigen (größte
   zuerst). Eine Gruppe öffnen: die Gesichter sind nach Ähnlichkeit sortiert; **zweifelhafte stehen am Ende, sind rot
   markiert („Ausreißer?“) und abgewählt**. Klick wählt ein Gesicht ab oder an, Doppelklick öffnet das Foto.
   - **Als neue Person benennen…** — Vorname, Nachname, optional Rufname, Beziehung, Geburtsdatum und „Das bin ich“
     eingeben (siehe „Personen-Details“); nur die ausgewählten Gesichter werden bestätigt. Gibt es den Namen schon,
     fragt Lumora, ob die Gesichter zu dieser Person sollen.
   - **Zu Person hinzufügen…** — Person aus der Liste wählen (ähnelt die Gruppe einer bekannten Person, ist sie
     vorgewählt).
   - **Kein Gesicht / ignorieren** — die ausgewählten Gesichter (Fehlerkennungen); sie erscheinen nirgends mehr.
   - **Ganze Gruppe ignorieren…** — alle Gesichter der Gruppe, auch die abgewählten Ausreißer (Rückfrage mit Anzahl),
     z. B. für Fremde im Hintergrund; die Gruppe verschwindet. Dasselbe per **Rechtsklick auf eine Gruppenkachel**.
   Passt ein ausgewähltes Gesicht nicht gut zur Person (bzw. ähnelt es einer anderen bekannten Person), fragt
   Lumora nach: **Ohne diese bestätigen** / **Trotzdem alle bestätigen** / **Abbrechen**.
3. **Vorschläge prüfen:** Nach dem Benennen lernt Lumora im Hintergrund (wenige Sekunden) und schlägt die Person
   auf weiteren Fotos vor. Person öffnen → Reiter **Vorschläge**: je Gesicht ✓ (bestätigen) oder ✗ (ablehnen), oder
   mehrere auswählen bzw. **Sichtbare bestätigen/ablehnen** (nur die gerade auf dem Bildschirm stehen) oder **Alle
   Vorschläge bestätigen…/ablehnen…** (die ganze Liste, mit Rückfrage samt Anzahl; beim Bestätigen danach die
   Ausreißer-Rückfrage). Die Zeile über der Liste zeigt „Zeigt 1–14 von 30 Vorschlägen — nach unten scrollen für
   weitere“, solange nicht alle auf den Bildschirm passen. Abgelehnt
   heißt „das ist nicht diese Person“ — sie wird für dieses Gesicht nie wieder vorgeschlagen, der nächstbeste Kandidat
   rückt sofort nach.
4. **Korrigieren:** Reiter **Bestätigt** → falsche Gesichter auswählen → **Aus Person entfernen** (danach wird ohne sie
   neu gelernt), **Als Titelbild**. Oben: **Bearbeiten…** (alle Details), **Zusammenführen mit…** (z. B. zwei Einträge derselben
   Person), **Ausblenden** (aus der Übersicht; „Ausgeblendete zeigen“ holt sie zurück), **Löschen…** (die Gesichter
   bleiben und sind wieder unbenannt).
   **Titelbild** (seit 0.4.44, ADR-0068): oben in den Details **Automatisch / Selbst gewählt** — automatisch nimmt Lumora das
   beste Gesicht (Gesichtsgüte, frontal, Foto-Güte); auch aus Galerie und Lightbox über „Als Titelbild für ▸“ setzbar.
   **Personen-Kacheln** zeigen den Namen ohne Abschneiden (zwei Zeilen), die Beziehung und zwei Chips: **✓ n** (grün,
   bestätigt) und **n Vorschläge** (gelb, öffnet direkt den Reiter Vorschläge); ein kleines Symbol zeigt ein automatisch
   gewähltes Titelbild.
5. **Ignorierte Gesichter:** Übersicht → **Ignorierte Gesichter (N)** zeigt alle ignorierten Gesichter der aktiven
   Bibliothek (neueste Fotos zuerst, wird beim Scrollen nachgeladen); automatisch wegen der Mindestgröße ignorierte sind
   mit **„zu klein“** und ihrer Größe in Pixeln markiert. Auswählen → **Zurückholen** (wieder unbenannt; Vorschläge und
   Gruppen werden neu berechnet), Doppelklick öffnet das Foto. Ein zurückgeholtes kleines Gesicht bleibt, bis die
   Mindestgröße geändert wird.
6. **Übersicht ordnen:** über den Personen **Person suchen** (Vor-/Nachname, Rufname, Beziehung), **Sortieren** nach
   Anzeigename, Nachname oder Anzahl Fotos und **Nach Beziehung gruppieren** (Abschnitte je Beziehung, „Ohne Beziehung“
   am Ende). Die Kachel zeigt die Beziehung vor der Fotozahl.
- Personen gelten für alle Bibliotheken, Zähler und Vorschläge für die aktive. Neue Gesichter aus dem laufenden Index
  erscheinen in den Gruppen nach **Aktualisieren**.
- Die Gesichts-Ausschnitte (128 × 128) liegen im Vorschaubild-Ordner (`thumbs\<Bibliothek>\faces\`) und werden nur für
  sichtbare Kacheln erzeugt.

### Personen-Details und Beziehungen
Eine Person hat **Vorname**, **Nachname** und optional einen **Rufnamen** („Opa Franz“). Angezeigt wird überall der
Rufname, sonst „Vorname Nachname“ — dieser Anzeigename ist eindeutig (zwei gleichnamige Personen unterscheidet ein
Rufname). Personen aus älteren Versionen wurden am ersten Leerzeichen geteilt (erstes Wort = Vorname, Rest = Nachname);
Bearbeiten korrigiert das (ADR-0045).
- **Beziehung** aus deiner Sicht tippst du selbst („Schwester“, „Cousin mütterlicherseits“, „Taufpate“); schon
  verwendete werden beim Tippen vorgeschlagen, Groß-/Kleinschreibung zählt nicht (es bleibt die Schreibweise der ersten
  Verwendung). Verwalten unter **Einstellungen › Gesichter › Personen & Beziehungen**: **Umbenennen…** (wirkt bei allen
  Personen, in Suche und Filter; ein schon vorhandener Name bietet das Zusammenführen an), **Zusammenführen mit**
  (z. B. Tippfehler), **Löschen…** (die Personen behalten eine leere Beziehung).
- **Geburtsdatum** optional und auch ungefähr: nur Jahr, Monat und Jahr oder vollständig. Die Detailansicht zeigt das
  Alter heute („geboren März 1975 (51 Jahre)“, bei nur Jahr „etwa 45–46 Jahre“).
- **Das bin ich** markiert höchstens eine Person (eine neue Markierung nimmt sie der bisherigen, der Dialog sagt, wem).
  Vorbereitung für spätere Fragen wie „Fotos mit meiner Schwester aus meiner Kindheit“.
- Die Lightbox („Neue Person…“) fragt weiter nur einen Namen; Details danach auf der Personen-Seite mit **Bearbeiten…**.

### Personen in Galerie und Lightbox
Sobald Gesichter erkannt oder Personen benannt sind (ADR-0038). Als Person zählt nur ein **bestätigtes** Gesicht;
Vorschläge der Erkennung werden getrennt gezeigt.
- **Filter „Personen“** (Galerie-Kopfzeile, neben „Tags“): Häkchen je Person mit Anzahl Fotos dieser Bibliothek,
  Verknüpfung **ODER** / **UND**, **alle Fotos mit Gesichtern** (jedes erkannte Gesicht — benannt, vorgeschlagen oder
  unbenannt), **nur Fotos ohne Person**, **nur Fotos mit unbenannten Gesichtern** und die Option **auch Vorschläge**
  (Standard aus). Als „Kein Gesicht“ ignorierte Erkennungen zählen in keinem Filter. Ausgeblendete Personen stehen nicht
  in der Liste.
- **Suche** findet auch Personennamen (Teil des Namens genügt, ohne Groß-/Kleinschreibung) — wie Tags und Kategorien;
  seit 0.2.47 auch Vorname, Nachname, Rufname und Beziehung („schwester“ zeigt alle Fotos deiner Schwestern).
- **Beziehung** im Personen-Filter: Abschnitt „Beziehung“ mit Häkchen je Beziehung (mit Fotozahl); mehrere gelten mit
  ODER, zusammen mit gewählten Personen und allen anderen Filtern mit UND. Unter „Es gilt“ steht z. B. „Beziehung
  „Schwester““ mit eigenem ×; die Suche im Flyout findet Personen auch über Nachname, Rufname und Beziehung.
- **Listenansicht**: Spalte **Personen** (Kopfzeile rechts klicken → einblenden): bestätigte Namen, Vorschläge kursiv und
  grau mit „?“; Klick auf die Kopfzeile sortiert innerhalb der Seite.
- **Kachel**: kleines Personen-Symbol mit Anzahl, wenn bestätigte Personen auf dem Foto sind (Tooltip: Namen), daneben
  ein graues Gesichts-Symbol mit der Zahl der noch **unbenannten** Gesichter (unbestätigt oder nur vorgeschlagen;
  Tooltip z. B. „2 Gesichter noch nicht benannt“ und die Vorschläge). Ignorierte Gesichter bekommen kein Symbol.
- **Lightbox**: Taste **F** (oder Knopf „Gesichter“) zeigt Rahmen über den erkannten Gesichtern — grün mit Namen,
  gelb „Vorschlag: X?“, weiß „Unbekannt“; die Rahmen zoomen und schwenken mit. Klick auf einen Rahmen: **Ist X**
  (Vorschlag bestätigen), **Nicht X** (Vorschlag ablehnen), **Ist …** (Person wählen oder Namen eingeben — ein
  vorhandener Name wird zugeordnet, sonst entsteht eine neue Person), **Kein Gesicht** (Rahmen verschwindet). Vollbild
  liegt jetzt nur noch auf **F11**. Die Rahmen bleiben beim Weiterblättern an, und Lumora merkt sich die Taste F
  dauerhaft: wer sie eingeschaltet lässt, sieht die Rahmen beim nächsten Öffnen der Lightbox (auch nach Neustart) sofort.
  Ignorierte Gesichter zeigen keinen Rahmen.
- **Einstellungen › Gesichter → Gesichtsdaten löschen…**: nach Bestätigung werden für die aktive Bibliothek
  alle Gesichter mit Fingerabdrücken, Zuordnungen, abgelehnte Vorschläge, der Gesichter-Cache und Personen, die danach
  in keiner Bibliothek mehr vorkommen, gelöscht; die Gesichtserkennung wird ausgeschaltet. Fotos, Bewertungen, Tags und
  Kategorien bleiben. Alles bleibt auf diesem PC.

### Orte (seit 0.2.54, ADR-0051)
Weltkarte mit den verorteten Fotos der aktiven Bibliothek (nur dieser Bereich — eine andere Bibliothek zeigt nur ihre
Fotos). Oben die Trefferzeile („1.234 verortete Fotos · 56 ohne Ort“) und **Ort suchen …** (Ortsnamen offline; Enter
springt zum ersten Treffer).
- **Start:** der Bereich, in dem die Fotos gemacht wurden, mit etwas Rand; ohne verortete Fotos die ganze Welt.
  Mausrad bzw. **+/−** zoomt um den Mauszeiger, Ziehen verschiebt, Doppelklick ins Leere zoomt hinein, Pfeiltasten
  verschieben. Knöpfe oben rechts: hinein, hinaus, **alle verorteten Fotos**, **ganze Welt**.
- **Gruppen:** runde Marker mit dem Vorschaubild des besten Fotos (höchste Bewertung, sonst neuestes) und der Anzahl;
  je näher gezoomt, desto feiner, bis zu einzelnen Fotos (kleines Vorschaubild). **Klick auf eine Gruppe** zoomt
  hinein, bis sie zerfällt (Fotos am exakt selben Ort öffnen sich in der Lightbox); **Doppelklick** oder Rechtsklick
  › **In der Galerie zeigen** zeigt genau diese Fotos in der Galerie. **Klick auf ein Einzelfoto** öffnet die Lightbox
  (mit den Fotos im Ausschnitt). Oben links: **Im Ausschnitt: n Fotos** → alle sichtbaren in der Galerie.
- **Verorten:** rechts **Ohne Ort** — Fotos ohne Koordinaten, neueste zuerst (Klick wählt, Strg+Klick ergänzt,
  Umschalt+Klick wählt einen Bereich, Doppelklick öffnet). Ausgewählte Fotos **auf die Karte ziehen** oder
  Rechtsklick auf die Karte › **Hier verorten (n Fotos)** → Bestätigung mit dem nächsten Ort und
  **Änderungsdatum der Dateien beibehalten** (Standard an) → Koordinaten in Katalog und Datei.
- **Gemeinsamer Filter (seit 0.4.1, ADR-0055):** über der Karte stehen **dieselbe Suche und dieselbe Filterkarte** wie
  in Galerie und Ordner (Datei: Art, Favoriten, Bewertung, Ablage; Inhalt: Tags, Personen, Beziehung, Motive, Album,
  Kategorien, Ort, Doppelte; „Es gilt … UND …“, **Filter zurücksetzen**, „Als Album speichern…“). Gruppen, Zähler,
  **Im Ausschnitt**, **Ohne Ort** und der Start-Ausschnitt zeigen nur die Treffer (Trefferzeile „… · gefiltert“); nur
  die Bedingung „Kartenausschnitt“ (aus „In der Galerie zeigen“) gilt auf der Karte selbst nicht. Es
  gibt **einen** Filterzustand für Galerie, Ordner und Orte: ein Filter bleibt beim Seitenwechsel erhalten, „Filter
  zurücksetzen“ wirkt überall. Je Seite eigen bleiben nur die **Ordnerauswahl** der Ordner-Seite, **Sortierung** und
  **Ansicht** (Kacheln/Collage/Liste) von Galerie und Ordner sowie der **Kartenausschnitt**. Ein Bibliothekswechsel
  behält den Filter; Tags, Personen, Kategorien, Motive oder ein Album, die es im neuen Bereich nicht gibt, fallen
  heraus, Ort, Kartenausschnitt und Bildinhalt-Suche immer.
- **Kartenstil (seit 0.4.1):** Knopf mit dem Ebenen-Symbol oben rechts (oder **Einstellungen › Darstellung › Karte**):
  **Automatisch** (Standard — hell CARTO **Positron**, dunkel CARTO **Dark Matter**, folgt dem Farbschema live),
  **Voyager**, **Positron**, **Dark Matter**, **Standard (OpenStreetMap)** und **Satellit** (MapTiler). CARTO- und
  Satellitenkarten brauchen einen **eigenen API-Schlüssel** (siehe „Kartenstile: Schlüssel anfordern“ unten); ohne
  Schlüssel zeigt die Karte OpenStreetMap und den Hinweis „API-Schlüssel nötig – in den Einstellungen eintragen“
  („Automatisch“ ohne Schlüssel bleibt still bei OpenStreetMap). Die Namensnennung des Stils steht unten rechts.
- **Kartenkacheln** werden je Stil getrennt unter `%LocalAppData%\Lumora\cache\maptiles\<stil>`
  zwischengespeichert (zusammen höchstens 500 MB, am längsten unbenutzte zuerst gelöscht; CARTO- und MapTiler-Kacheln
  höchstens 30 Tage); nur der sichtbare Ausschnitt wird geladen, höchstens zwei Kacheln gleichzeitig, nie vorab. Ohne
  Netz bleibt die Karte grau bzw. zeigt den Cache („Offline …“), ein abgelehnter Schlüssel meldet sich unten links; die
  Gruppen funktionieren weiter. Bei einer versteckten Bibliothek weist eine Leiste darauf hin, dass der Kartenserver den
  betrachteten Ausschnitt sieht. Abschalten: `LUMORA_MAP_TILES=off`.

#### Ungenaue Orte verbessern und Garmin-Spuren (seit 0.4.23, ADR-0062)
Viele Handy-Fotos haben nur einen **geschätzten Ort** (Mobilfunk/WLAN, oft Kilometer daneben): erkennbar daran, dass das
Handy nur Breite/Länge gespeichert hat — ohne GPS-Höhe und -Zeit —, an einer Ortung „NETWORK/WLAN“, einem großen
Fehlerradius oder einem unmöglichen Sprung zu zeitnahen Fotos mit echtem GPS. Lumora prüft das im Hintergrund
(Statusleiste „Orte prüfen: n / m“); oben steht dann „… · **n mit geschätzter Position**“.
- **Seite Orte › Ungenau** (Umschalter rechts „Ohne Ort n | Ungenau m“): Erklärung mit Zählern, Filter **Alle,
  Garmin-Spur, Sicher, Wahrscheinlich, Ohne Vorschlag**, Liste nach Tagen bzw. Ausflügen. Vorschläge in dieser
  Reihenfolge: **Garmin-Spur** (Ihre Uhr war zur Aufnahmezeit dort), **sicher** (zwischen zwei Fotos mit echtem GPS
  höchstens 30 min auseinander), **wahrscheinlich** (ein Foto mit echtem GPS wenige Minuten entfernt bzw. Spur mit
  Lücke). Auf der Karte: gespeicherter Punkt grau, Vorschlag farbig, gestrichelte Korrektur, die Spur der Aktivität;
  den Vorschlag der ausgewählten Zeile kann man **ziehen**. Je Zeile ✓ **Übernehmen** bzw. ✕ **Ablehnen** (bleibt, wird
  nicht mehr vorgeschlagen), ohne Vorschlag **Ort setzen…**; unten **Garmin + sichere übernehmen (n)** und **Ausflug (n)**.
  **Speichern:** „nur im Katalog“ (Standard, die Datei bleibt unberührt) oder „auch ins Foto schreiben (EXIF)“. Nach dem
  Übernehmen: Hinweis mit **Rückgängig** (auch die Galerie-Rückgängig-Taste nimmt es zurück). Von Hand gesetzte Orte
  werden nie überschrieben.
- Auch **Fotos ohne Ort**, die in einer Aktivität bzw. nahe an Fotos mit GPS liegen, bekommen Vorschläge: unter „Ohne
  Ort“ → **Vorschläge zeigen**.
- **Lightbox (Taste I):** „Position geschätzt“ mit Begründung und Vorschlag („Laut Ihrer GPS-Spur … waren Sie um 14:56
  ≈ 4,8 km westlich …“), **Vorschlag übernehmen**, **Auf der Karte zeigen**, **Ort setzen…**.
- **Garmin-Spuren einlesen (Einstellungen › Orte)** — Garmin vergibt für Privatpersonen **keinen API-Schlüssel**, Lumora
  braucht auch keinen und sendet nichts an Garmin:
  1. **Garmin-Datenexport öffnen** → im Garmin-Konto unter Datenverwaltung › „Daten exportieren“ anfordern; Garmin
     schickt nach einiger Zeit einen Download-Link per E-Mail.
  2. **Export-ZIP einlesen…** → die heruntergeladene ZIP-Datei wählen; Lumora liest die FIT-Dateien daraus (auch aus den
     inneren ZIPs) im Hintergrund mit Fortschritt und **Abbrechen**; schon eingelesene Aktivitäten werden übersprungen.
     Einzelne **FIT/GPX/TCX-Dateien** gehen ebenso.
  3. **Laufend ergänzen:** einen **Ordner beobachten** (z. B. den Ablageordner von Garmin Express) oder die Uhr per
     **USB** anschließen — Lumora fragt vorher. Das geht nur bei Uhren, die als Laufwerk mit `GARMIN\Activity`
     erscheinen; neuere Uhren (z. B. Fenix 6/7) melden sich per MTP und sind kein Laufwerk → Export bzw. Garmin Express.
  Darunter der Stand („1 284 Aktivitäten · 2014–2026 · Fenix 5, Fenix 7 · zuletzt eingelesen …“), **Aktivitäten
  anzeigen** und **Spuren entfernen…**. Spuren gehören der Bibliothek bzw. Gruppe (Verknüpfen führt zusammen, Lösen
  kopiert); eine versteckte Bibliothek hat ihre eigenen im Container.
- **Zeitabgleich (Einstellungen › Orte):** die Aufnahmezeit wird so in UTC umgerechnet: Zeitzonen-Angabe der Datei ›
  GPS-Zeit des Fotos › **Zeitzone der Fotos** (Standard: Windows-Zeitzone mit Sommerzeit je Datum). **Toleranz**
  (Standard 2 min) vor/nach einer Aktivität. **Kamera-Abweichung** je Kamera (z. B. „+3:12“, wenn die Kamera-Uhr
  3 min 12 s vorgeht) — bei Kameras mit eigenem GPS-Zeitstempel schlägt Lumora den Wert vor („übernehmen“).

#### Kartenstile: Schlüssel anfordern (0.4.1)
- **CARTO** (Voyager, Positron, Dark Matter, „Automatisch“): unter **carto.com/basemaps** den kostenlosen Schlüssel
  per E-Mail anfordern (kommt nach etwa einer Minute, kein Konto nötig). Der Schlüssel muss dein eigener sein; frei
  für nicht-kommerzielle Nutzung bis 5 Mio. Kachelabrufe im Monat. Ohne Schlüssel ruft Lumora CARTO nie ab (sonst
  Wasserzeichen).
- **MapTiler** (Satellit): auf **maptiler.com** ein kostenloses Konto anlegen und im Bereich „API keys“ den Schlüssel
  kopieren (Free-Plan nur nicht-kommerziell).
- Eintragen unter **Einstellungen › Darstellung › Karte**: dort steht je Dienst schon eine Karte **Kartendienst CARTO**
  bzw. **Kartendienst MapTiler** (Status „Kein Schlüssel – noch nicht eingerichtet“, Knopf **API-Schlüssel
  anfordern…** öffnet die Anforderungsseite im Browser, darunter die drei Schritte). Schlüssel ins Feld **API-Schlüssel**
  (Passwortfeld) einfügen → **Schlüssel prüfen** (genau ein Kachelabruf; gültig → gespeichert, Status **Eingerichtet**,
  die Stile des Dienstes sind freigeschaltet). Dazu optional **Website** („Website öffnen“), **Registriert mit**
  (E-Mail, nur zur Erinnerung), **Konto / Verbrauch** („Konto öffnen“, vorbelegt mit dem Konto des Dienstes) und
  **Notiz**; **Kartendienste speichern** übernimmt Änderungen. Unten zeigt jede Karte Lumoras eigene Zählung „Diesen
  Monat von Lumora abgerufen: n Kacheln“ (je Kalendermonat; verbindlich ist nur die Zählung im Konto → „Verbrauch im
  Konto ansehen“). Links öffnen nur auf Klick (nur http/https); Lumora registriert nichts und sendet selbst keine
  Daten. Schlüssel, E-Mail und Notiz stehen unverschlüsselt in der Datenbank des Katalogs (nie im Protokoll); eine
  versteckte Bibliothek hat eigene Einstellungen.

### Doppelte (seit 0.3.1, ADR-0053)
Findet doppelte Fotos und Videos der aktiven Bibliothek (bzw. aller Bibliotheken in „Alle verknüpften“) und räumt sie auf.
Lumora berechnet dafür im Hintergrund — nach jedem Scan, bei neuen Dateien und einmal für bestehende Kataloge — zwei
Fingerabdrücke (Statusleiste „Doppelte: Prüfsummen n / m“; nur lokale Dateien, nichts wird heruntergeladen): eine
**Prüfsumme** (SHA-256) für Dateien, deren Größe noch einmal vorkommt, und einen **Bild-Fingerabdruck** aus dem
Vorschaubild. Solange das noch läuft, nennt die Seite „Noch n … offen — das Ergebnis ist noch unvollständig“.
- Oben umschalten zwischen **Exakt gleich** (Byte für Byte gleich) und **Ähnlich** (verkleinert, neu gespeichert,
  leicht beschnitten, WhatsApp-Kopie, Serienaufnahme). Bei „Ähnlich“ stellt der Regler **Ähnlichkeit** ein, wie streng
  verglichen wird (Abstand 1–12, Standard 10; kleiner = nur fast gleiche). Fast einfarbige Bilder zählen nie als ähnlich.
- **Eingang schon in der Bibliothek** zeigt nur Dateien im Eingang, die es im Bibliotheksordner schon gibt.
- Jede Gruppe zeigt ihre Dateien nebeneinander (Vorschaubild, Auflösung, Größe, Aufnahmedatum, Ordner, Bewertung/Favorit,
  Bibliothek/Eingang, bei „Ähnlich“ der Abstand). Eine Datei ist als **Behalten** vorgeschlagen — nach der Regel über der
  Liste: höhere Auflösung → Aufnahmedatum aus der Datei → liegt schon in der Bibliothek → Bewertung, Favorit, Tags,
  Kategorien, Personen → größere Datei → länger im Katalog; die Gruppe nennt den Grund. Mit dem Knopf an einer anderen
  Datei behält man diese; **Aufräumen** abhaken nimmt eine Gruppe heraus. Doppelklick aufs Bild öffnet die Lightbox.
- **Übrige in den Papierkorb (n · Größe)** fragt nach und legt alle nicht behaltenen Dateien der angehakten Gruppen in den
  Windows-Papierkorb (wie „Papierkorb“ in der Galerie, mit Protokoll). Mit **Angaben übertragen** (Standard an) bekommt
  die behaltene Datei vorher Favorit, die höhere Bewertung, fehlende Tags und Kategorie-Werte sowie die Personen der
  gelöschten (auf das passende Gesicht; geht das nicht, meldet die Ergebniszeile „Personen nicht übertragbar“).
- **Rückgängig** holt die zuletzt aufgeräumten Dateien aus dem Papierkorb zurück — mit derselben Katalog-Nummer und allen
  Angaben (Tags, Personen, Bewertung …); was übertragen wurde, verschwindet wieder von der behaltenen Datei.
- Liegen nach einem Scan neue Dateien im Eingang schon in der Bibliothek, meldet die Statusleiste „N Dateien im Eingang
  sind schon in der Bibliothek“ — **Anzeigen** öffnet die Seite mit genau diesen Gruppen.

### Ordnerüberwachung
Die Bibliotheksordner der aktiven Bibliothek werden im Hintergrund überwacht (`FileSystemWatcher` je Wurzel, rekursiv).
Neue, gelöschte, umbenannte oder geänderte Fotos und Videos (nur Formate aus dem Formatregister; `.tmp`/`.part`,
`~`-Dateien, `Thumbs.db`, `.thumbnails`, `@eaDir`, der eigene Thumbnail-Cache und — seit 0.4.33 — ausgeschlossene Ordner
wie `.thumbs`/`.temp` werden ignoriert) starten eine
**Ruhezeit** (Standard 3 s, einstellbar 1–60 s). Erst wenn in dieser Zeit nichts mehr passiert, läuft automatisch
„Aktualisieren“ (Incremental-Scan, stabile FileIds) — die Statusleiste zeigt vorher „Änderungen erkannt —
Aktualisierung in n s“, danach „Automatisch aktualisiert: …“. Regeln:

- Nie zwei Scans derselben Bibliothek gleichzeitig — egal ob „Aktualisieren“, „Neu aufbauen“, Entpacken oder Automatik:
  der spätere wartet, bis der laufende fertig ist. Änderungen, die vor dem Start eines manuellen Scans passiert sind,
  erledigt dieser gleich mit (kein zweiter automatischer Scan danach); was während des Scans passiert, prüft die Automatik
  danach erneut. Wird der manuelle Scan abgebrochen, gelten die gesammelten Änderungen wieder.
- Auch Ordner zählen (angelegt, hinein-/hinausverschoben, umbenannt, gelöscht), mit Punkt im Namen wie `2024.06 Urlaub`.
  Einzige Lücke: ein *gelöschter* Ordner, dessen Name wie eine Dateiendung endet (`Fotos.alt`), ist von einer gelöschten
  Nicht-Mediendatei nicht zu unterscheiden und löst keinen Scan aus.
- Wechselt die aktive Bibliothek während eines automatischen Scans, wird dessen Ergebnis (auch eine Sperre) verworfen und
  nicht der neuen Bibliothek zugeordnet.
- Eigene Datei-Vorgänge der App (Verschieben, „In die Bibliothek…“, Ordner übernehmen, Papierkorb) buchen den Katalog selbst; die Überwachung sieht
  ihre Dateiereignisse trotzdem und aktualisiert danach einmal automatisch (Ergebnis „keine Änderungen“). Entpacken
  aktualisiert am Ende selbst — die Ereignisse davor sind damit erledigt, ein zweiter automatischer Scan entfällt in der
  Regel.
- Meldet der automatische Scan **viele fehlende Dateien** (Mass-Missing-Guard) oder eine **Bibliothek offline**, wird nicht
  automatisch wiederholt; die Statusleiste zeigt „Automatische Aktualisierung angehalten: …“, bis ein manuelles
  „Aktualisieren“ in Einstellungen › Bibliotheken & Eingänge erfolgreich war (oder die Bibliothek gewechselt bzw. die Überwachung
  umgeschaltet wird).
- Nicht erreichbare Wurzeln (USB/NAS) werden alle 30 s erneut versucht; kommt die Wurzel zurück, folgt ein Scan nach der
  Ruhezeit. Ein Pufferüberlauf des Watchers startet ihn neu und plant ebenfalls einen Scan ein.
- Einstellungen „Bibliotheksordner überwachen“ (Standard: an) und „Ruhezeit“ liegen in der Settings-Tabelle
  (`watch.enabled`, `watch.quietSeconds`) und wirken sofort; schnelle Änderungen (Pfeile der Ruhezeit) werden der Reihe
  nach gespeichert, der zuletzt eingestellte Wert gilt.
- Beim Kopieren Tausender Dateien verschiebt jedes Ereignis den Countdown; die Statusleiste bekommt davon höchstens zwei
  Meldungen je Sekunde (gebündelt, Background-Priorität).

### Statusleiste
Links: aktive Bibliothek. Rechts: Hinweise, die keine Vorgänge sind (unvollständige oder fehlgeschlagene Dateivorgänge mit
Details im Tooltip; Bibliothek offline; „N Dateien im Eingang sind schon in der Bibliothek — Anzeigen“; „Orte: n geschätzt ·
m Vorschläge · k Garmin-Aktivitäten“; nur in einer geöffneten versteckten Bibliothek „Sicherheits-Check: x von y erfüllt“).

### Aktivitäten (seit 0.4.36, ADR-0067)
Ganz rechts in der Statusleiste steht **eine** Anzeige für alles, was im Hintergrund läuft: Verschieben, Umbenennen, In den
Papierkorb (auch Ordner), Fehlende/Ausgeschlossene entfernen, Ordner umbenennen/verschieben, In die Bibliothek übernehmen,
Sortieren/Flatten, Rückgängig, Einlesen (voll/inkrementell), automatisches Entpacken, Vorschaubilder, Doppelte, Gesichter,
Motive, Aufnahmedatum, Orte (Nachtragen, Genauigkeit), Foto-Güte, Storys, GPS-Spuren, die anstehende bzw. angehaltene
automatische Aktualisierung und Änderungen, die auf den Katalog warten.
- **Statusleiste:** der wichtigste Vorgang (vom Benutzer gestartete vor Hintergrund-Indizes) mit Drehsymbol, Text
  („Verschieben: 120 von 340“) und kleinem Balken, dazu „+n“ für weitere. Ohne Vorgang kurz „Alles erledigt“, danach
  „Keine Aktivität“.
- **Klick klappt die Liste auf** (Esc, Klick daneben oder Fensterwechsel schließt sie): **Läuft** (Titel mit Zahlen, Ziel nur
  mit Ordnernamen, Balken, Restzeit, sobald schätzbar, **Pause/Fortsetzen** bei allen Hintergrund-Aufgaben, **Abbrechen** —
  bereits Erledigtes bleibt, Rückgängig ist möglich), **Wartet** (mit Grund, z. B. „wartet auf laufendes Einlesen derselben
  Bibliothek“, „wartet auf den Katalog“; wartende Datei-Vorgänge lassen sich abbrechen, bevor sie etwas ändern) und
  **Zuletzt fertig** (höchstens 10, Ergebnis und Zeitpunkt; **Rückgängig** wo es ein Rückgängig gibt — derselbe Weg wie die
  Galerie-Taste —, **Fehlende zeigen** nach einem Einlesen mit neu fehlenden Dateien, **Details** bei Fehlern und
  Übersprungenen; „Liste leeren“). Die Liste gilt nur für die laufende Sitzung, nichts wird gespeichert; beim Wechsel in eine
  versteckte Bibliothek und beim Zusperren beginnt sie leer.
- **Kurze Meldung** über der Statusleiste, wenn ein selbst gestarteter Vorgang fertig ist (z. B. „340 Dateien verschoben —
  nach „Urlaub 2024““ mit **Rückgängig**); Hintergrund-Indizes und Einlesen melden sich nicht.
- Gesichter und Motive teilen sich die Grafikkarte Foto um Foto; die Liste zeigt dann „(abwechselnd mit „Gesichter““).
- **Hintergrund-Planer (seit 0.5.10, ADR-0074):** Aufnahmedatum, Orte, Genauigkeit, Doppelte und Foto-Güte lesen nicht
  gleichzeitig, sondern Stapel um Stapel nach Wichtigkeit (zweite Zeile z. B. „wartet — zuerst Aufnahmedatum“); jede
  Datei wird für Datum, Ort und Genauigkeit nur einmal gelesen, auf einer SSD mehrere zugleich, auf einer Festplatte eine
  nach der anderen. Während des Einlesens ruhen alle Hintergrund-Aufgaben. Jede hat **Pause/Fortsetzen**; im Kopf der
  Liste hält **Alle pausieren** alles an (Einlesen und Vorschaubilder laufen weiter) — die Pause gilt bis
  **Alle fortsetzen** bzw. bis zum nächsten Start. Die Restzeit folgt der jüngsten Geschwindigkeit; Warten zählt nicht mit.
  Das manuelle Entpacken im Entpacken-Dialog, „Neu aufbauen“ und „Speicherort ändern“ zeigen ihren Fortschritt weiter in
  ihren Dialogen bzw. auf der Bibliotheksseite.

### Versteckte Bibliotheken
Bibliotheken, deren Fotos in einem **verschlüsselten Container** liegen (VeraCrypt-Datei oder BitLocker-VHDX, den Sie
selbst einhängen) und von denen Lumora im Alltag **nichts zeigt** (ADR-0049).
- **Nur mit Startschalter:** `Lumora.exe _hidden` (auch `--hidden` oder `/hidden`). Ohne Schalter gibt es keinen Eintrag,
  keinen Reiter, keine Suche. Den Schalter **nicht in eine sichtbare Verknüpfung** legen. Gut geht eine Verknüpfung
  *im Container selbst* (nur sichtbar, wenn er eingehängt ist) oder die Eingabeaufforderung `cmd` (ohne gespeicherten
  Verlauf); nicht über Win+R (Windows merkt sich die Eingabe) und nicht in PowerShell (Befehlsverlauf).
- **Container nie in OneDrive** oder einem anderen Cloud-Ordner ablegen. Lumora verschlüsselt selbst nichts: der
  Container schützt Originale, Katalog, Vorschaubilder, Gesichter und Protokolle.
- **Container-Assistent (seit 0.4.18, ADR-0061):** „Versteckte Bibliothek anlegen…“ fragt zuerst:
  - **Container ist schon eingehängt** → weiter wie unten.
  - **Neu mit BitLocker** (Windows Pro/Enterprise/Education, ohne Zusatzprogramm): Speicherort der Containerdatei
    (Vorschlag z. B. `D:\Daten\sicherung.vhdx`, neutraler Name, nur auf einem NTFS-Laufwerk; nie in OneDrive/Cloud —
    Lumora lehnt das ab; Systemlaufwerk geht, mit Hinweis), Größe (Vorschlag aus dem freien Platz; die Datei wächst erst
    mit dem Inhalt), freier Laufwerksbuchstabe und Bezeichnung. **Container erstellen** → Windows fragt einmal nach
    Administratorrechten — für **Lumora selbst** mit dem lesbaren Auftrag `--container-create-vhdx --path "…" --size-mb …
    --letter X --label "…"` (unter „Details anzeigen“). Lumora legt die VHDX dann über Windows-Funktionen an (seit 0.4.26
    kein PowerShell und kein Skript mehr), hängt sie ein und formatiert sie (NTFS). Blockiert ein Virenschutz den Schritt,
    sagt Lumora das und schlägt VeraCrypt bzw. „Container ist schon eingehängt“ vor. Dann **BitLocker-Assistent
    öffnen** (der von Windows): Kennwort wählen, Wiederherstellungsschlüssel **in Datei speichern oder drucken — nicht im
    Microsoft-Konto**, „Automatisch entsperren“ **nicht** einschalten, Verschlüsselung starten → **Verschlüsselung
    prüfen** → **Bibliothek auf X: anlegen**. Später hängt ein Doppelklick auf die `.vhdx` im Explorer sie wieder ein
    (Windows fragt das BitLocker-Kennwort). Windows Home hat kein BitLocker — Lumora schlägt dann VeraCrypt vor.
  - **Neu mit VeraCrypt:** Fehlt VeraCrypt, zeigt Lumora Paket (`IDRIX.VeraCrypt`), Quelle (winget), Größe und Lizenz;
    erst **Zustimmen und installieren** startet `winget install` (das Installationsprogramm fragt nach
    Administratorrechten). Ohne winget gibt es den Link zur offiziellen Seite veracrypt.fr und **Erneut prüfen**. Dann
    **VeraCrypt-Assistent starten** und der Anleitung daneben folgen — Standard ist ein Container **mit verstecktem
    Bereich** (äußeres Volume mit harmlosen Fotos, inneres Volume mit einem ANDEREN Kennwort; die Bibliothek gehört in den
    INNEREN Bereich; den äußeren Bereich nur mit „Verstecktes Volume schützen“ einhängen, sonst kann Schreiben den
    inneren zerstören), wählbar **normaler Container** (ein Kennwort). **Weiter: Container einhängen** → Containerdatei
    und Buchstabe wählen (nicht aus einem Cloud-Ordner), VeraCrypt fragt das Kennwort (bei verstecktem Bereich das innere) → **Bibliothek auf X:
    anlegen**.
  - Container-Kennwörter geben Sie nur in den Fenstern von Windows bzw. VeraCrypt ein; Lumora sieht und speichert sie nie
    und protokolliert nichts vom Assistenten. **Abbrechen** geht auf jeder Seite; ein schon angelegter, noch nicht
    verschlüsselter BitLocker-Container bleibt liegen (Lumora sagt, was zu tun ist).
- **Anlegen:** Bibliotheks-Umschalter (oben links) → **Versteckte Bibliothek
  anlegen…** → (Container-Assistent) → Laufwerk des eingehängten Containers wählen (nach dem Assistenten ist das neue
  Laufwerk vorgewählt), optional einen Ordnernamen direkt darauf (z. B. `X:\Fotos`;
  tiefer geht nicht, sonst fände Lumora ihn nicht wieder), Name, Lumora-Kennwort zweimal (mindestens 6 Zeichen). Lumora
  legt im Ordner den versteckten Katalogordner `.lumora` an, wechselt in die neue Bibliothek und scannt sie. Der Ort
  wird nicht gemerkt.
- **Öffnen:** Mit `_hidden` sucht Lumora auf allen eingehängten lokalen Laufwerken (Wurzel und eine Ebene darunter;
  Netzlaufwerke, CD/DVD und Wechselmedien nicht) nach versteckten Bibliotheken und zeigt sie im Umschalter. Klick →
  Lumora-Kennwort → Lumora schließt die normale Bibliothek vollständig und öffnet die versteckte mit eigenem Katalog.
  Sie wird nie zusammen mit einer anderen Bibliothek angezeigt.
- **Zusperren:** Umschalter → **Zusperren (zurück zur normalen Bibliothek)**, **Einstellungen › Versteckte Bibliotheken →
  Jetzt zusperren**, automatisch nach einstellbarer Untätigkeit (Standard 10 Minuten ohne Maus/Tastatur in Lumora; ein
  laufender Scan oder Datei-Vorgang wird erst fertig) und beim Beenden. Danach ist der versteckte Katalog vollständig
  geschlossen (Fenster, Lightbox, Verbindungen, Bilder im Speicher).
  **Zusperren beendet den versteckten Modus bis zum nächsten Start:** danach zeigt Lumora nichts mehr davon — kein
  Eintrag im Umschalter, kein Reiter —, wie ein normaler Start. Wieder öffnen nur nach einem Neustart mit `_hidden`.
- **Einstellungen › Versteckte Bibliotheken** (Kennwort, Zusperr-Zeit, Aushängen) gibt es nur, solange eine versteckte
  Bibliothek geöffnet ist; in der normalen Bibliothek nie.
- **Einstellungen › Sicherheits-Check (seit 0.4.28, ADR-0064)** — ebenfalls nur in der geöffneten versteckten Bibliothek:
  zeigt, wo **Windows** sich etwas merkt, und öffnet die passende Windows-Einstellung. **Lumora liest nur und ändert
  selbst keine Windows-Einstellung** (kein Registry-Schreiben, keine Administratorrechte, kein PowerShell).
  - Punkte: BitLocker auf C: (wichtigster Punkt), Ruhezustand, Schnellstart, Auslagerungsdatei beim Herunterfahren
    leeren, zuletzt geöffnete Elemente/Sprunglisten, Aktivitätsverlauf, Explorer-Miniaturbilder, Windows-Suche auf dem
    Container-Laufwerk, VeraCrypt „Verlauf nie speichern“ (ohne VeraCrypt „nicht zutreffend“) und die Lumora-Option
    **„Lumora nach dem Zusperren ganz beenden“** (Standard aus; beim Zusperren von Hand und nach Untätigkeit beendet sich
    Lumora dann ganz, statt zur normalen Bibliothek zu wechseln — Container wird wie beim Beenden ausgehängt). Dazu der
    Hinweis „Nicht abschaltbar: Programm- und Laufwerksprotokolle“.
  - Je Punkt: grün erfüllt, rot/gelb offen, grau Zusatz bzw. „unbekannt“ (Windows lieferte den Wert nicht oder er ist
    nicht dokumentiert, z. B. der Schalter des Aktivitätsverlaufs ohne Gruppenrichtlinie) — Lumora rät nie. **Einstellung
    öffnen** startet nur die Windows-Oberfläche (Systemsteuerung, Einstellungen-App, Lokale Sicherheitsrichtlinie, Ordner-
    und Indizierungsoptionen) bzw. VeraCrypt; fehlt etwas (z. B. die Lokale Sicherheitsrichtlinie bei Windows Home), zeigt
    **ⓘ** den Weg.
  - **Wovor möchten Sie sich schützen?** Ausgeschalteter PC (nur BitLocker zählt), Mitbenutzer (Standard, alle
    Windows-Punkte) oder fachmännische Untersuchung (auch die Lumora-Option). Oben „x von y erfüllt“ mit Ring,
    **Erneut prüfen**, **Erfüllte ausblenden**; in der Statusleiste „Sicherheits-Check: x von y erfüllt“ (nur in der
    versteckten Sitzung). Stufe und Option stehen im Katalog der versteckten Bibliothek (im Container).
  - Geänderte Windows-Einstellungen (Schnellstart, Auslagerungsdatei) wirken erst nach einem Neustart; der Check zeigt die
    Einstellung, nicht den laufenden Zustand.
- **Container aushängen** (seit 0.4.38 Standard **aus**, in den Einstellungen der geöffneten versteckten Bibliothek einschaltbar; ist es an, muss der Container vor dem nächsten Öffnen wieder eingehängt werden — VHDX per Doppelklick im Explorer): beim
  Zusperren und Beenden hängt Lumora ein VeraCrypt-Volume über die VeraCrypt-Befehlszeile bzw. eine eingehängte VHDX
  wie Explorer › „Auswerfen“ aus (ohne Administratorrechte, seit 0.4.31); sind noch Dateien offen oder ist es kein erkennbarer Container, erscheint ein Hinweis.
  **Container öffnen…** (Einstellungen › Versteckte Bibliotheken): eine **BitLocker-VHDX** hängt Windows ein wie ein
  Doppelklick im Explorer und fragt das BitLocker-Kennwort in seinem eigenen Fenster „Laufwerk entsperren“ (seit 0.5.4,
  kein VeraCrypt nötig, Windows wählt den Laufwerksbuchstaben); eine **VeraCrypt-Datei** hängt VeraCrypt unter dem
  gewählten Buchstaben ein und fragt selbst. Lumora sieht kein Container-Kennwort. VeraCrypt wird nicht mitgeliefert.
- **Kennwort vergessen:** Container einhängen und im Ordner `.lumora` der Bibliothek eine Datei `reset-password.txt`
  anlegen (Inhalt egal; `.lumora` ist versteckt — im Explorer „Ausgeblendete Elemente“ einblenden). Beim nächsten
  „Öffnen“ bietet Lumora **Kennwort zurücksetzen** an und löscht die Datei danach.
- **Was Lumora außerhalb nicht hinterlässt:** keine Namen/Pfade in den normalen Einstellungen, im normalen Protokoll
  oder unter `%LocalAppData%\Lumora`; Protokolle der versteckten Sitzung nur in `.lumora\logs`; keine Einträge
  unter „Zuletzt verwendet“; in der versteckten Sitzung sind **Im Explorer zeigen**, **Ordner öffnen** und **Mit
  Standard-App öffnen** gesperrt (Windows würde sich die Pfade merken); der Katalog kann nicht umziehen.
- **Grenzen:** Windows merkt sich im Dateidialog („Container öffnen… → Durchsuchen“) ggf. den zuletzt besuchten Ordner,
  VeraCrypt kann eine eigene Verlaufsliste führen, Auslagerungsdatei und Ruhezustand können Speicherinhalte enthalten,
  in der Prozessliste steht `_hidden`. Welche dieser Stellen auf Ihrem PC offen sind, zeigt der **Sicherheits-Check**
  (oben); Prefetch, eingehängte Laufwerke und Ordneransichten des Explorers lassen sich nicht abschalten (mit BitLocker
  auf C: liegen sie verschlüsselt). Ein VeraCrypt-Volume, das „als Wechselmedium“ eingehängt ist, wird nicht gefunden.

### Verbundene Apps (App-Verbindung, im Aufbau seit 0.5.5, ADR-0073)

Lumora setzt den Vertrag **App-Verbindung 1.1** um (Repo `herbertschrotter-blip/app-verbindung`) — zuerst für das
Gipfelbuch (Touren, Spuren, Gipfel; Fotos nur als Verweis). Die Verbindung läuft nur auf diesem PC über `127.0.0.1`.

- **Eine Lumora je Benutzer:** Ein zweiter Start (Verknüpfung, EXE, portable Version) holt das Fenster der laufenden
  Lumora nach vorn und beendet sich. Ausnahme: Ein Start mit `_hidden` läuft wie bisher für sich und nimmt an der
  App-Verbindung gar nicht teil.
- **Anmeldedatei** `%LocalAppData%\AppVerbindung\apps\lumora.json`: Wegweiser für andere Apps (Port, Version, Rechte),
  ohne Geheimnis. Beim Beenden steht dort `running: false`.
- **Koppeln** (seit 0.5.8): Fragt eine andere App (z. B. das Gipfelbuch unter Einstellungen › Verbundene Apps) eine
  Verbindung an, erscheint in Lumora **„<App> möchte sich verbinden“** mit einem sechsstelligen Code — derselbe muss in der
  anderen App stehen. Je Zeile an/aus: Fotos suchen/Vorschaubilder/Originale öffnen, Personen sehen (Standard aus), Touren,
  GPS-Spuren und Gipfel an Lumora geben, Hintergrundstart (Standard aus). **Verbinden** oder **Ablehnen**; nach 5 Minuten
  verfällt die Anfrage. Das gemeinsame Geheimnis liegt nur verschlüsselt (Windows-DPAPI) unter `Lumora\appconnect`.
- **Lumora fragt selbst** (seit 0.5.9, noch ohne Knopf): Lumora kann andere Apps über ihre Anmeldedatei finden, sie bei
  Bedarf normal starten, eine Kopplung mit Code anfragen, Rechte nachfragen und trennen. Bedienbar wird das mit dem Reiter
  „Verbundene Apps“.
- **Noch offen:** Reiter „Verbundene Apps“ (Mockup 11), Fotos für das Gipfelbuch und Touren in Lumora folgen in den nächsten
  Stufen; bis dahin trennt nur die andere App.

## Datenablage

Alles liegt unter `%LocalAppData%\Lumora` (Fotos und Videos bleiben an Ort und Stelle; der Ordnername bleibt
trotz des Produktnamens Lumora, ADR-0047) — im portablen Modus unter `Daten\` neben `Lumora.exe` (ADR-0069). Katalog und
Vorschaubilder können in den Einstellungen in einen anderen Ordner umziehen; dann liegen `library.db` und `thumbs\`
dort, Protokolle und `database-location.json` bleiben hier:

| Pfad | Inhalt |
|---|---|
| `database-location.json` | Nur nach einem Umzug: gewählter Ordner für Katalog und Vorschaubilder, alte Dateien zum Löschen beim nächsten Start |
| `library.db` (+ `-wal`, `-shm`) | SQLite-Katalog: Bibliotheken, Ordner, Dateien, Thumbnails-Status, OperationBatches/FileOperations, Settings, FTS5-Index |
| `thumbs\{libraryId}\{fileId}_320_still.jpg` | Vorschaubilder (320 px, FileId-basiert, atomar geschrieben) |
| `backups\library-<Version>-<Zeitstempel>[-manuell\|-vor-wiederherstellung].db` | Katalog-Sicherungen (vor Aktualisierung: letzte 5, von Hand: 10, vor Wiederherstellung: 3); `restore-request.json` = für den nächsten Start vorgemerkte Wiederherstellung |
| `logs\lumora-YYYYMMDD.log` | Serilog-Protokoll, täglich rollierend, 14 Tage |
| `cache\maptiles\<stil>\z\x\y.png` | Kartenkacheln der Seite „Orte“ je Stil (`osm`, `carto-voyager`, `carto-positron`, `carto-darkmatter`, `maptiler-satellite`), zusammen höchstens 500 MB, am längsten unbenutzte zuerst gelöscht; CARTO/MapTiler höchstens 30 Tage |
| `models\buffalo_l\` | Nur nach dem Download in den Einstellungen: Modelle der Gesichtserkennung (`det_10g.onnx`, `w600k_r50.onnx`); bleibt beim Umzug des Katalogs hier |
| `models\siglip2-base-patch16-224\` | Nur nach dem Download in den Einstellungen: Motiv-Modell SigLIP 2 (`onnx\vision_model.onnx`, `onnx\text_model_int8.onnx`, `tokenizer.json` + Konfiguration); bleibt beim Umzug des Katalogs hier |

Die Datenbank wird beim Start per EF-Core-Migration auf den aktuellen Stand gebracht (ein bestehender Katalog wird
vorher nach `backups\` gesichert, ADR-0069); unterbrochene Thumbnail-Jobs
werden dabei zurückgesetzt und unfertige Dateivorgänge gemeldet.

## Umgebungsvariablen

| Variable | Wirkung |
|---|---|
| `LUMORA_APPDATA` | Anderer AppData-Root (DB, Thumbnails, Logs, `database-location.json`, Sicherungen) — z. B. für Tests mit frischer Datenbank, ohne den eigenen Katalog anzufassen; hat Vorrang vor dem portablen Modus (ADR-0069) |
| `LUMORA_SMOKE_ROOT` | Startet nach dem Laden des Hauptfensters den Rauchtest gegen diesen Bibliotheksordner. **Dateien darin werden verschoben und in den Papierkorb gelegt** — nur mit einer Kopie verwenden. |
| `LUMORA_SMOKE_SHOTS` | Ordner für PNG-Screenshots der Hauptansichten (optional) |
| `LUMORA_SMOKE_EXIT` | `1` → App nach dem Rauchtest beenden; ExitCode 0 = alle Schritte OK, 2 = mindestens ein Schritt FAIL |
| `LUMORA_SMOKE_CLEANUP` | `1` → die Testbibliothek am Ende wieder aus dem Katalog entfernen |
| `LUMORA_SMOKE_HIDDEN` | Nur im Rauchtest: simuliert den Startschalter `_hidden` für den Schritt „Versteckte Bibliothek“ (Standard an; `0` = aus). Container = Temp-Ordner neben der Testbibliothek, Aushängen nur simuliert |
| `LUMORA_FACE_MODELS` | Ordner mit `det_10g.onnx` und `w600k_r50.onnx` statt `models\buffalo_l\` (Entwicklung, Tests; dann kein Download). Tests mit echten Modellen laufen nur mit dieser Variable |
| `LUMORA_MAP_TILES` | `off` → keine Kartenkacheln laden (graue Karte, kein Netzzugriff); im Rauchtest immer aus |
| `LUMORA_FACE_SAMPLE_DIR` | Nur für die lokale Messung (`FaceRecognitionMeasurementTests`): Ordner mit Fotos, aus dem eine Stichprobe gelesen wird; Ausgabe nur zusammengefasste Zahlen |
| `LUMORA_MOTIF_MODEL` | Ordner mit dem Motiv-Modell (SigLIP 2) statt `models\siglip2-base-patch16-224\` (Entwicklung, Tests; dann kein Download). Tests mit echtem Modell laufen nur mit dieser Variable |
| `LUMORA_SECURITYCHECK_REAL` | Nur für die lokale Messung (`SecurityCheckRealWindowsTests`): `1` → die Leser des Sicherheits-Checks lesen die echte Registry/Shell dieses PCs (nur lesend); Ausgabe nur Punkt + Status |
| `LUMORA_MOTIF_SAMPLE_DIR` | Nur für die lokale Messung (`MotifMeasurementTests`, Spike 14): Fotoordner für die Stichprobe (`LUMORA_MOTIF_SAMPLE` = Anzahl, Standard 200); Ausgabe nur zusammengefasste Zahlen |

Das Ergebnis des Rauchtests steht in `<AppData>\smoke-result.txt` und im Log (Zeilen „Rauchtest …“).

## Tests

```powershell
dotnet test tests/Lumora.Tests --nologo        # Services + Data (xUnit, FluentAssertions; echte SQLite-Migration in Temp)
dotnet test tests/Lumora.Wpf.Tests --nologo    # WPF-gebundener Code (WIC-Renderer, GalleryQuery, Converter, Tags, Listenansicht)
```

Integrations-Rauchtest (steuert die ganze App über ihre ViewModels, prüft Dateisystem und Datenbank, macht Screenshots):

```powershell
$lib = "$env:TEMP\lumora-smoke-lib"; Copy-Item -Recurse <Testbibliothek> $lib
$env:LUMORA_APPDATA     = "$env:TEMP\lumora-smoke-appdata"
$env:LUMORA_SMOKE_ROOT  = $lib
$env:LUMORA_SMOKE_SHOTS = "$env:TEMP\lumora-smoke-shots"
$env:LUMORA_SMOKE_EXIT  = '1'
dotnet run --project src/Lumora.Wpf -c Debug
Get-Content "$env:LUMORA_APPDATA\smoke-result.txt"   # Erwartung: 16 OK, 0 FAIL
```

Dasselbe als Skript gegen eine beliebige EXE (Debug-Build oder portables Paket; kopiert die Testbibliothek nach
`%TEMP%\Lumora-smoke-<Tag>\lib`, eigenes AppData daneben, ExitCode 0 = alle Schritte OK):

```powershell
pwsh -File build\run-smoke.ps1 -Exe src\Lumora.Wpf\bin\Debug\net10.0-windows\Lumora.exe -TestLib <Testbibliothek>
```

Die Testbibliothek sollte Bilder (JPG/PNG), Videos (MP4/MOV), einen Ordner ohne direkte Dateien mit zwei
Unterordnern („Leer\Sub A“, „Leer\Sub B“), einen Ordner „Unsortiert“ mit mehreren JPGs sowie die Ordner
„2024\Familie“ und „2025\Screenshots“ enthalten (das Szenario prüft diese Namen) — insgesamt mindestens sechs
Mediendateien, damit der Mass-Missing-Guard beim Incremental-Schritt nicht greift. Der Schritt legt
`Unsortiert\smoke-neu.jpg` an (bleibt liegen, wird beim nächsten Lauf vorab entfernt); der Archiv-Schritt legt
`Unsortiert\smoke-archiv.zip` an, entpackt es nach `Unsortiert\smoke-archiv\` und verschiebt das ZIP in den
Windows-Papierkorb (der Ordner wird beim nächsten Lauf vorab entfernt). Die Ordnerüberwachung ist während des Laufs
ausgeschaltet und wird erst im letzten Schritt (Ruhezeit 1 s, `Unsortiert\smoke-watch.jpg` kopieren und löschen)
eingeschaltet. Der Schritt „Auto-Entpacken“ davor legt den Geschwisterordner `<Bibliothek>-Auto-Eingang` als Eingang an,
schaltet das automatische Entpacken für die Bibliothek ein und am Ende wieder aus und entfernt Eingang und Ordner wieder.
Der Schritt „Eingang → Archiv“
legt den Geschwisterordner `<Bibliothek>-Eingang` als Eingangs-Wurzel an (wird am Ende wieder entfernt) und hinterlässt
in der Bibliothek die leeren Ordner `2026\Smoke-Archiv` und `2026\Smoke-Umbenannt` (beim nächsten Lauf vorab entfernt).
Der Schritt „Ordner übernehmen“ legt den Geschwisterordner `<Bibliothek>-Uebernahme-Eingang` als Eingang an, übernimmt
ihn nach `2026\Smoke-Uebernahme` (der Quellordner geht dabei in den Windows-Papierkorb), macht das mit Rückgängig wieder
rückgängig und entfernt Eingang und Ordner am Ende — deshalb immer gegen eine Kopie laufen lassen.
Der Schritt „Versteckte Bibliothek“ (vor der Ordnerüberwachung) simuliert `_hidden`, kopiert drei Fotos in den
Geschwisterordner `<Bibliothek>-Container\Tresor…`, legt dort eine versteckte Bibliothek an, sperrt zu, öffnet wieder
und prüft, dass im AppData-Testordner weder Name noch Pfad noch Dateinamen vorkommen; der Ordner wird danach gelöscht.
Danach ist die Oberfläche neu aufgebaut — die Ordnerüberwachung läuft mit den Diensten der neuen normalen Sitzung.

Spike- und Benchmark-Harness: `dotnet run --project tests/Lumora.PerfTests -c Release -- <spike>` (siehe
[docs/spikes/](docs/spikes/)).

## Projektstruktur

```
Lumora.slnx                 # Solution (.slnx)
Directory.Packages.props           # Central Package Management inkl. transitiver Security-Pins
build/                             # publish.ps1 (portable Version), run-smoke.ps1 (Rauchtest gegen eine beliebige EXE),
                                   # make-icon.ps1 (Lumora.ico aus LumoraIcon.xaml)
src/
  Lumora.Domain/            # Entities, Enums — keine Abhängigkeiten
  Lumora.Data/              # LibraryContext (EF Core), EntitiesMapping, Migrations,
                                   # RawSql (BulkScanWriter, FtsIndexer, FtsQueryBuilder)
  Lumora.Services/          # WPF-frei: Catalog, Database, Infrastructure (AppDataPaths), Library, Media
                                   # (Formatregister), Messaging, Operations, Scanning, Sorter, Tags, Thumbnails, Archive;
                                   # AddLumoraServices() + AddXyz() je Modul
  Lumora.Wpf/               # App.xaml.cs (Host + DI), MainWindow (NavigationView);
                                   # Module Folders, Gallery, Library, Lightbox, Settings, Shell (+ Branding, Smoke), Sorter,
                                   # Thumbnails (WIC-Renderer)
tests/
  Lumora.Tests/             # xUnit: Services + Data (256 Tests)
  Lumora.Wpf.Tests/         # xUnit: WPF-gebundener Code (97 Tests)
  Lumora.PerfTests/         # Konsolen-Harness der Spikes (SQLite-Bulk, WIC, FFmpeg, Storage, V0.1-Scan)
docs/adr/                          # Architekturentscheidungen ADR-0001 … ADR-0021
docs/spikes/                       # Messungen der sechs Spikes und der Real-Welt-Scan-Validation
mockups/                           # HTML-Design-Vorlagen (Fluent hell/dunkel, Lightbox)
chatgpt-review/                    # Review-Runden zum Plan
PLAN.md · CHANGELOG.md · CLAUDE.md # Implementierungsplan, Änderungsprotokoll, Projektprofil für Claude
```

Schichten: Domain ← Data ← Services ← Wpf; keine WPF-Referenz in Domain/Data/Services. Module sprechen über
Services-Interfaces und Nachrichten (`WeakReferenceMessenger`), nie über fremde ViewModels (ADR-0017).

## Weiterführende Dokumentation

- [PLAN.md](PLAN.md) — Vision, Stack, Schema, Scope und Roadmap (mit Status 0.2.0)
- [docs/adr/README.md](docs/adr/README.md) — Architekturentscheidungen (ADR-0001 … ADR-0019)
- [docs/spikes/](docs/spikes/) — Spike-Ergebnisse (SQLite-Bulk/FTS5, Virtual Grid, LibVLC, WIC, FFmpeg, Storage)
- [CHANGELOG.md](CHANGELOG.md) — Änderungen je Version und offene Punkte
- [CLAUDE.md](CLAUDE.md) — Projektprofil und Repo-Regeln für die Arbeit mit Claude

## Lizenz

Privates Projekt von Herbert Schrotter — nicht zur Weitergabe bestimmt, alle Rechte vorbehalten. Verwendete
Open-Source-Komponenten (WPF-UI, CommunityToolkit.Mvvm, EF Core, LibVLCSharp/libVLC, Serilog, xUnit, FluentAssertions)
unterliegen ihren jeweiligen Lizenzen; FFmpeg wird nicht mitgeliefert, sondern lokal installiert.
