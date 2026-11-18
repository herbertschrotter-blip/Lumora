# ADR-0077: OneDrive-Bibliothek — Platzhalter sichtbar und ordnbar, ohne Download

- **Status:** Accepted (Herbert, 2026-11-18: so abgenommen, wie in 0.5.41 gebaut; Mockup 27 abgenommen)
- **Datum:** 2026-11-17
- **Entscheider:** Herbert (Auftrag „OneDrive-Bibliothek“ nach Spike 19), umgesetzt von Claude
- **Bezogen auf:** [Spike 19](../spikes/19-onedrive-platzhalter.md) (Vorschaubilder und Aufnahmedatum ohne Download),
  [ADR-0007](0007-thumbnail-scheduling.md) (Vorschaubild-Pipeline), [ADR-0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md) (Größe 400 px),
  [ADR-0074](0074-hintergrund-planer.md) (Hintergrund-Dienste, Vorrat 0.5.40), [ADR-0046](0046-aufnahmedatum.md)
  (Aufnahmedatum), [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliothek), PLAN Kap. 12 (Offline-vs-Missing)
  und 13 (Vorschaubilder), Mockup `mockups/27-onedrive-bibliothek.html`

## Kontext

Mit „Dateien bei Bedarf“ liegt ein Foto in einem OneDrive-Ordner oft nur als **Platzhalter** auf der Platte: Name, Größe
und Datum sind da, der Inhalt nicht. Wer den Inhalt liest, löst einen Download aus. Herbert will einen solchen Ordner als
normale Bibliothek führen — alle Fotos sehen und ordnen, ohne dass Lumora sie herunterlädt.

Spike 19 hat gezeigt: Die Windows-Shell liefert für Platzhalter Vorschaubilder (256 und 1024 px, erster Abruf 0,35–2,8 s,
8 gleichzeitig ≈ 4 Bilder/s, danach aus dem Cache von Windows 5–10 ms) und über den Eigenschaftsspeicher das Aufnahmedatum,
GPS nicht — jeweils ohne Download. Shell-COM braucht STA-Threads; im App-Container der Claude-App liefert die Shell für
Platzhalter nichts, nativ schon.

Bei der Umsetzung fiel auf: OneDrive markiert jede Datei und jeden Ordner als **Reparse-Punkt**. Der Scanner übersprang
Reparse-Punkte (gegen Verknüpfungs-Schleifen) und fand deshalb in einem OneDrive-Ordner **nichts** (nativ gemessen: 0 Dateien
und 0 Ordner in der Wurzel). Außerdem dekodierte der Vorschaubild-Dienst Platzhalter wie lokale Dateien — jede angezeigte
Kachel hätte ihr Original heruntergeladen.

## Entscheidung

1. **Zentrale Platzhalter-Erkennung** `Services/Cloud/CloudFiles` (nur Attribute, nie der Inhalt):
   - nur online: `RecallOnDataAccess` (0x00400000), `RecallOnOpen` (0x00040000) oder `Offline`;
   - „Immer auf diesem Gerät behalten“: `Pinned` (0x00080000), „Speicherplatz freigeben“: `Unpinned` (0x00100000);
   - `ThumbnailService.IsLocallyAvailable` leitet dorthin weiter — alle bisherigen Prüfungen der Hintergrund-Dienste nutzen
     so dieselbe Stelle.
   - `ICloudFileService` liefert den Zustand je Datei (Attribute plus bekannte Sync-Wurzeln: OneDrive-Umgebungsvariablen
     und `SyncRootManager` der Registry).
2. **Scanner:** Reparse-Punkte werden nur noch übersprungen, wenn sie eine **Verknüpfung** sind (Linkziel vorhanden;
   Junction, Symlink). Cloud-Dateien und -Ordner haben kein Linkziel und werden eingelesen
   (`CloudFiles.IsLink`, liest nur den Reparse-Punkt).
3. **Vorschaubilder über die Shell:** Liegt das Original nur online, gibt der Bild- bzw. Video-Worker den Auftrag an eine
   eigene **Cloud-Warteschlange** weiter, statt WIC oder FFmpeg zu starten. Acht Cloud-Worker holen das Bild über
   `ICloudThumbnailRenderer` (Wpf: `ShellThumbnailExtractor` mit `IShellItemImageFactory`, `SIIGBF_THUMBNAILONLY`,
   Wiederholen bei `WTS_E_EXTRACTIONPENDING` bis 30 s, 8 STA-Threads; `ShellCloudThumbnailRenderer` verkleinert die
   1024-px-Vorschau auf die normale Vorschaubild-Größe und schreibt sie atomar in dieselbe Cache-Datei
   `{fileId}_400_still.jpg` mit demselben DB-Status).
   - Rang: zuletzt angeforderte Kacheln zuerst, innerhalb einer Anforderung deren Reihenfolge; höchstens 96 warten, die
     am längsten nicht mehr angeforderten verfallen und werden beim nächsten Sichtbarwerden neu angefordert — Aufträge der
     jüngsten Anforderung erst über der harten Obergrenze von 512 (Nachbesserung 0.5.41).
   - Fehlschlag (keine Verbindung, Zeitgrenze, kein Shell-Weg) ist eine äußere Ursache (`cloud_unavailable`): kein Versuch
     verbraucht, nach 5 min erneut. Auch ein abgebrochener Abruf (App beendet) verbraucht keinen; nur ein echter Fehler der
     Datei (Schreiben scheitert) zählt.
   - Die Lightbox holt ihre Vorschau auf eigenen STA-Threads (2), nie hinter den 8 der Galerie.
   - Bildmaße bleiben leer, bis die Datei lokal ist (die Galerie nimmt das Seitenverhältnis des Vorschaubilds).
4. **Nur bei Bedarf:** Die Galerie fordert für Platzhalter nur die **sichtbaren Kacheln plus 12 davor und danach** an und
   beim Scrollen nach 150 ms Ruhe die neu sichtbaren; lokale Dateien wie bisher die ganze Seite. Für die übrigen Platzhalter
   der Seite meldet sie Vorschaubilder, die schon im Cache liegen („nur Cache“,
   `IThumbnailService.ReportCachedThumbnailsAsync`, nichts eingereiht). Der **Vorrat** (0.5.40) lässt Platzhalter aus.
5. **Aufnahmedatum ohne Download:** Das Nachtragen fragt für Platzhalter den Eigenschaftsspeicher
   (`SHGetPropertyStoreFromParsingName`, `GPS_DEFAULT`, `System.Photo.DateTaken`) über `ICloudPropertyReader` — Herkunft
   **5 „OneDrive, vorläufig“** (`CaptureDateSource.Cloud`, Nachbesserung 0.5.41; Windows übergeht `OffsetTimeOriginal`),
   Zeitzone dieses PCs. Sobald die Datei lokal ist, liest das Nachtragen das Datum aus der Datei (ADR-0046 Nachtrag); nennt
   die Datei selbst keins, bleibt das vorläufige. Liefert der Eigenschaftsspeicher nichts, bleibt die Datei offen und wird
   in der Sitzung nicht erneut gefragt, bis sie lokal ist. GPS bleibt leer, bis die Datei lokal ist.
6. **Analysen lassen Platzhalter aus** und holen sie nach, sobald sie lokal sind:
   - Gesichter laden das Original nicht und vermerken die Datei nicht; Gesichts-Ausschnitte entstehen für freigegebene
     Dateien erst wieder, wenn sie lokal sind.
   - Motive rechnen mit einem vorhandenen Vorschaubild (auch dem der Shell), nie mit dem Original.
   - Datum, Orte, Genauigkeit, Doppelte, Prüfsummen, Foto-Güte und Vorrat übersprangen Platzhalter schon.
   - Neu: `CloudFilesChangedMessage(BecameLocal)` stößt Gesichter, Motive, Datum, Orte, Doppelte, Prüfsummen und Güte an —
     gesendet von der Lightbox nach dem Laden und von der Beobachtung nach „Immer behalten“. Dateien, die außerhalb von
     Lumora lokal werden, holt der nächste Durchgang nach (Einlesen, Start).
7. **Lightbox:** Bei einem Platzhalter erst die 1024-px-Vorschau der Shell mit „Wird von OneDrive geladen …“ und
   Fortschritt, dann das Original (`ICloudFileHydrator` liest die Datei in 1-MB-Blöcken, Windows lädt sie dabei) und danach
   die normale Anzeige. „Abbrechen“ schließt die Datei sofort, die Vorschau bleibt, „Erneut laden“ versucht es noch einmal.
   Ohne Internet eine Meldung („OneDrive ist gerade nicht erreichbar …“) — nie „fehlt“. Nachbarn, die nur online liegen,
   werden nie vorab geladen. Attribute liest die Lightbox (UI-Thread) nur unter bekannten Cloud-Wurzeln oder auf lokalen
   Laufwerken (Laufwerksart ohne Zugriff), nie auf Netzlaufwerken.
8. **Kachel und Rechtsklick:** Wolken-Marke („nur online“) neben der Endung bzw. in der Collage unten links. Im Menü
   „Datei ▸“ für Dateien in OneDrive „Immer auf diesem Gerät behalten“ (+P −U) und „Speicherplatz freigeben“ (+U −P) über
   `SetFileAttributes` — wie im Explorer, auch in der Lightbox — mit Statusmeldung. Der Dienst beobachtet die Dateien, bis
   Windows fertig ist (behalten bis 30 min, freigeben bis 5 min), und meldet es; die Marke folgt.
9. **Offline-vs-Missing:** Ein Platzhalter gilt nie als fehlend. Liefert ein Ordner beim Abgleich einen IO-Fehler, obwohl
   die Wurzel erreichbar ist (etwa OneDrive ohne Verbindung), markiert der Abgleich darin nichts als fehlend
   (`UnreadablePaths`). Der volle Scan bricht bei einem Fehler mitten im Auflisten ab wie vor 0.5.41; „Neu aufbauen“
   bricht bei jedem nicht lesbaren, aber vorhandenen Ordner ab, der Katalog bleibt unverändert (ADR-0028) — eine einzelne
   nicht lesbare Datei überspringt es wie bisher.
10. **Versteckte Bibliothek:** keine Änderung — Cloud-Ordner bleiben dort gesperrt; Shell-Vorschau und Eigenschaftsspeicher
    sind in der versteckten Sitzung zusätzlich abgeschaltet (keine Spuren im Vorschaubild-Cache von Windows).

## Nachbesserung nach der Prüfung (0.5.41)

| Befund | Entscheidung |
|--------|--------------|
| Eine Anforderung mit mehr als 96 Platzhaltern (großer Bildschirm, kleine Kacheln) ließ auch sichtbare Kacheln verfallen; sie blieben leer, bis gescrollt wurde | `CloudJobQueue` kürzt die jüngste Anforderung in der Warteschlange erst über 512 (Rang-Block je Anforderung, harte Obergrenze für Alben, Orte, Storys, App-Verbindung); die nächste lässt ältere von hinten auf 96 schrumpfen |
| Ein IO-Fehler mitten im Auflisten wurde auch im vollen Scan geschluckt — „Neu aufbauen“ schrieb einen Katalog ohne den Rest des Ordners | Ohne Hook wirft `FolderEnumerator` weiter (wie vor 0.5.41); „Neu aufbauen“ übergibt einen Hook, der bei einem vorhandenen, nicht lesbaren Ordner abbricht; einzelne Dateien und inzwischen gelöschte Pfade überspringt er wie bisher |
| Das Shell-Datum wurde als EXIF gespeichert und nie korrigiert | Eigene Herkunft 5 „OneDrive, vorläufig“; neu gelesen, sobald die Datei lokal ist, nennt die Datei keins, bleibt es. Nativ geprüft (300 lokale Fotos, 280 mit Datum): Wanduhrzeit 280 von 280 gleich, UTC 266 von 280 — die 14 mit abweichendem Zeitversatz lagen genau um den Zonenabstand daneben |
| „Pending“ zählte für Platzhalter vor dem Abruf einen Versuch; drei abgebrochene Abrufe sperrten die Datei für immer | Platzhalter: „Pending“ ohne Versuch, nur ein echter Fehler zählt; Zeilen mit `cloud_unavailable` sperrt `MaxAttempts` nie (Galerie und Vorrat) |
| Lightbox-Vorschau wartete hinter den 8 Galerie-Abrufen (offline bis 30 s) | Eigene Spur `ShellThumbnailExtractor.Lightbox` mit 2 STA-Threads |
| Lightbox las Attribute auf dem UI-Thread auch auf Netzlaufwerken mit Buchstaben | nur unter bekannten Cloud-Wurzeln (`ICloudFileService.MayBeCloud`) oder auf lokalen Laufwerken (`GetDriveType`, ohne Zugriff auf das Laufwerk) |
| Gecachte Vorschaubilder von Platzhaltern außerhalb von sichtbar ± 12 erschienen erst nach dem Scrollen | sichtbare zuerst anfordern, die übrigen Platzhalter der Seite „nur Cache“ |

Nicht umgesetzt: Shell-Abrufe der Galerie beim Pausieren (Katalog-Umzug, „Cache leeren“) abbrechen und neu einreihen —
ein Abruf dauert nativ 0,35–4,3 s, nur ohne Verbindung bis 30 s; die Pause des Umzugs scheitert dann mit ihrer Zeitgrenze
und lässt den Dienst laufen (0.5.40).

## Begründung

- Die Shell ist der Weg, den auch der Explorer geht: OneDrive liefert die Vorschau aus der Cloud, die Datei bleibt online.
  Ein eigener Download- oder Web-Zugang zu OneDrive wäre mehr Code, Anmeldung und Datenschutz-Fragen für dasselbe Ergebnis.
- Gleiche Cache-Datei und gleicher DB-Status heißen: Galerie, Storys, Orte, Alben und App-Verbindung zeigen Platzhalter
  ohne eigene Pfade; wird die Datei lokal, bleibt das Vorschaubild gültig (gleiche Änderungszeit).
- Bei ≈ 4 Bildern/s wären 46 000 Platzhalter auf Vorrat über drei Stunden Abruf — deshalb nur Sichtbares, neueste zuerst,
  begrenzter Rückstau.
- Die Unterscheidung Verknüpfung ↔ Cloud-Ordner über das Linkziel schützt weiter vor Schleifen (Junctions) und braucht
  keinen Zugriff auf den Inhalt.

## Konsequenzen

### Positiv
- Ein OneDrive-Ordner ist eine normale Bibliothek: einlesen, sehen, sortieren, bewerten, taggen, verschieben — ohne Download.
- Nativ gemessen (8 Platzhalter, Lumoras Shell-Weg): 8 von 8 Vorschaubilder in 4,3 s (Median 2,0 s je Bild), Aufnahmedatum
  8 von 8, danach alle weiterhin nur online; der Scanner findet im OneDrive-Ordner 881 von 881 Mediendateien (vorher 0).
- Behalten/Freigeben direkt aus Lumora, Analysen holen geladene Dateien nach.

### Negativ
- Gesichter, Orte (GPS), Doppelte, Prüfsummen und Güte gibt es für Platzhalter erst, wenn die Datei einmal lokal war.
- Ohne Internet bleiben neue Platzhalter-Kacheln ohne Bild (Wolken-Marke, Symbol) — sie werden nach 5 min erneut versucht.
- Der Scanner fragt bei lokalen OneDrive-Dateien ohne Cloud-Attribut einmal je Datei den Reparse-Punkt ab (kein Inhalt).

### Neutral
- Im App-Container der Claude-App liefert die Shell für Platzhalter nichts; Tests laufen mit Fakes, die echte Prüfung nativ
  (`OneDriveNativeProbeTests`, nur mit `LUMORA_ONEDRIVE_PROBE`).
- Videos, die nur online liegen: Vorschaubild über die Shell; Abspielen in der Lightbox lädt wie bisher über LibVLC.
- Diashow, Eigenschaften, Export, Verbessern und Drehen lesen das Original weiterhin auf ausdrücklichen Wunsch (Download
  durch Windows).

## Alternativen

- **Platzhalter überspringen wie bisher:** einfach, aber der OneDrive-Ordner bliebe leer bzw. ohne Bilder → verworfen.
- **Vorschaubilder auf Vorrat auch für Platzhalter:** Stunden an Abrufen, Last für OneDrive → verworfen (Spike 19).
- **OneDrive-Web-API (Graph):** Anmeldung, Netz-Code, Datenschutz-Fragen; die Shell liefert dasselbe lokal → verworfen.
- **Original kurz laden und wieder freigeben:** echter Download je Kachel → verworfen.

## Referenzen

- [Spike 19](../spikes/19-onedrive-platzhalter.md)
- Code: `src/Lumora.Services/Cloud`, `src/Lumora.Services/Thumbnails/ThumbnailService.Cloud.cs`,
  `src/Lumora.Wpf/Thumbnails/Cloud`, `src/Lumora.Wpf/Gallery/ViewModels/GalleryViewModel.Cloud.cs`,
  `src/Lumora.Wpf/Lightbox/LightboxViewModel.Cloud.cs`, `src/Lumora.Services/Scanning/FolderEnumerator.cs`
- Mockup: `mockups/27-onedrive-bibliothek.html` (abgenommen 2026-11-18)
