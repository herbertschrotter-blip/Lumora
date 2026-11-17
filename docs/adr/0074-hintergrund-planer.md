# ADR-0074: Hintergrund-Planer — Datei-Spur mit Vorrang, Dateikopf einmal lesen, Lesen passend zum Laufwerk, Pause je Aufgabe

- **Status:** Accepted
- **Datum:** 2026-10-27
- **Entscheider:** Herbert (Freigabe des Vorschlags „Reihenfolge, Datei nur einmal lesen, Pause beim Einlesen, Kerne passend
  zur Platte, Pause für alle, bessere Restzeit“), umgesetzt von Claude
- **Bezogen auf:** [ADR-0067](0067-aktivitaets-anzeige.md) (Aktivitäts-Anzeige), [ADR-0052](0052-motive-siglip2-lokal.md)
  (`IGpuGate`), [ADR-0046](0046-aufnahmedatum.md) (Aufnahmedatum), [ADR-0050](0050-orte.md) (Orte),
  [ADR-0057](0057-foto-guete-und-highlights.md) (Foto-Güte)

## Kontext

Nach dem Start bzw. nach dem Einlesen liefen bis zu neun Hintergrund-Dienste gleichzeitig: Aufnahmedatum, Orte nachtragen,
Orte prüfen, Doppelte (Prüfsummen, Bild-Fingerabdrücke), Foto-Güte, Gesichter, Motive, Storys, Vorschaubilder. Jeder hatte
seine eigene Schleife ohne gemeinsame Planung:

- Die Scan-Sperre wurde nur abgefragt (alle 2 s), Gesichter prüften sie gar nicht.
- Die Grafikkarten-Sperre (`IGpuGate`) galt nur für Gesichter und Motive.
- Pausieren ließen sich nur Gesichter und Motive.
- Drei Dienste öffneten dieselbe Datei nacheinander für Datum, Koordinate und GPS-Angaben.
- Alle lasen eine Datei nach der anderen. Auf einer Festplatte konkurrierten sie um den Lesekopf, auf einer SSD blieben
  Kerne ungenutzt.
- Die Restzeit war der Durchschnitt seit dem Start. Wartezeit zählte mit, die Schätzung kam nur alle 200 Dateien.

Herbert sah „7 laufend“ und fragte, ob man das nicht besser aufteilen könne und ob die Dienste alle Kerne nutzen.

## Entscheidung

1. **`IBackgroundPlanner`** (`Services/Background`, Singleton) verteilt eine **Datei-Spur**:
   - Wer Dateien liest (Aufnahmedatum, Orte nachtragen, Orte prüfen, Doppelte, Foto-Güte), holt sie je Stapel mit
     `AcquireAsync(job)` und gibt sie nach dem Schreiben zurück.
   - Vorrang: Datum, Orte und Genauigkeit gleich (0), dann Doppelte (1), dann Foto-Güte (2). Bei gleichem Vorrang kommt
     der zuerst Wartende dran, die drei wechseln sich also Stapel um Stapel ab.
   - Gesichter, Motive und Storys brauchen keine Spur. Sie holen nur die **Erlaubnis** (`WaitForPermissionAsync` bzw.
     `CanRun`), weil sie auf eigene Weise rechnen: Grafikkarte, Katalog.
2. **Globale Sperren** prüft der Planer bei jeder Vergabe und alle 500 ms für Wartende:
   - „alle pausiert“ (`SetAllPaused`)
   - Katalog-Umzug (`IDatabaseAccessGate.IsSuspended`)
   - Einlesen (`IScanGate.IsAnyBusy`, neu)

   Jede Aufgabe lässt sich einzeln pausieren (`SetPaused`). Der Zustand je Aufgabe (`BackgroundJobState`: Grund des Wartens)
   geht als `BackgroundJobStateMessage` und als Ereignis `Changed` hinaus.
3. **Dateikopf einmal lesen:** `IFileMetadataProbe` öffnet eine Datei einmal und liest daraus:
   - Aufnahmedatum (`MediaDateReader`)
   - Koordinate (`MediaGpsReader`)
   - GPS-Angaben (`ExifGpsDetailsReader`)

   Das Ergebnis merkt er sich für höchstens 4096 Dateien, gültig solange Größe und Änderungszeit gleich sind.
   `CaptureDateReader`, `GeoLocationReader` und `GeoAccuracyService` nutzen ihn; ohne ihn (Tests) lesen sie wie bisher.
4. **Lesen passend zum Laufwerk:** `IStorageKindProbe` bestimmt die Laufwerksart über die Storage-Verwaltung von Windows,
   nur lesend und ohne Administratorrechte: Partition → `MSFT_PhysicalDisk.MediaType`. UNC und Netzlaufwerk gelten als Netz.
   Daraus folgt, wie viele Dateien gleichzeitig gelesen werden (`ReadParallelism`):

   | Laufwerk | gleichzeitig |
   |---|---|
   | SSD | Hälfte der Kerne, 2 bis 6 |
   | Netz | 2 |
   | Festplatte oder unbekannt | 1 |

   Gleichzeitig gelesen wird nur bei Datum, Orten, Genauigkeit und Prüfsummen. Bild-Fingerabdrücke und Foto-Güte dekodieren
   Bilder (Prozessor) und bleiben einzeln mit niedriger Thread-Priorität. Ortsnamen werden nach dem Lesen der Reihe nach
   gesucht.
5. **Aktivitäts-Anzeige:**
   - Jede Aufgabe hat Pause/Fortsetzen über den Planer, auch während sie wartet.
   - Eine pausierte Aufgabe bleibt sichtbar, auch ohne offene Arbeit, damit „Fortsetzen“ erreichbar bleibt.
   - „wartet auf das Einlesen“, „wartet auf den Katalog-Umzug“ und „alle Hintergrund-Aufgaben pausiert“ erscheinen als
     Wartegrund.
   - Das kurze Warten auf die Reihe steht nur in der zweiten Zeile („wartet — zuerst Aufnahmedatum“). Es gehört zur
     normalen Arbeit.
   - Im Kopf der Liste steht **„Alle pausieren“ / „Alle fortsetzen“**. Einlesen und Vorschaubilder laufen davon unabhängig
     weiter.
6. **Restzeit:** gleitende Geschwindigkeit (Dateien je Sekunde, jüngste Messung mit 30 % Gewicht, Messungen frühestens alle
   0,5 s). Warten und Pause halten die Uhr an, die Geschwindigkeit bleibt dabei erhalten. Restzeit = offene Dateien durch
   Geschwindigkeit, frühestens 3 s nach Beginn.

## Begründung

- **Eine Spur statt Gleichzeitigkeit:**
  - Auf einer Festplatte ist abwechselndes Lesen langsamer als nacheinander. Die Spur macht die Reihenfolge sichtbar
    („zuerst Aufnahmedatum“) und vermeidet, dass fünf Dienste gleichzeitig dieselben Ordner durchgehen.
  - Datum, Orte und Genauigkeit gehen vor, weil Galerie, Zeitachse und Karte sie brauchen. Doppelte und Güte sind Komfort.
  - Das Abwechseln Stapel um Stapel lässt die drei über dieselben Dateien laufen, sodass der Lesedienst die zweite und
    dritte Abfrage aus dem Gedächtnis bedient.
- **Erlaubnis statt Spur für Gesichter, Motive und Storys:** Sie lesen wenige Dateien und rechnen lange (Grafikkarte, Modell).
  Eine gemeinsame Spur würde Datum und Orte stundenlang blockieren. Die Grafikkarte teilen sie weiterhin über `IGpuGate`.
- **Laufwerksart über die Storage-Verwaltung:** Sie ist ohne Administratorrechte lesbar und ändert nichts an Windows. Wenn
  etwas schiefgeht, gilt das Laufwerk als „unbekannt“ und es wird einzeln gelesen (sichere Seite).
- **Gleitende Restzeit:** Große Videos oder RAW-Dateien verlangsamen mitten im Lauf. Der Mittelwert seit dem Start würde
  dann viel zu optimistisch schätzen, der gleitende Wert folgt in wenigen Messungen.

## Konsequenzen

### Positiv
- **Weniger gleichzeitig:** Höchstens ein Dateidienst liest zur Zeit, die Anzeige nennt den Grund des Wartens.
- **Weniger Lesezugriffe:** Datum, Orte und Genauigkeit öffnen jede Datei einmal statt dreimal.
- **Besser genutzte Hardware:** Auf einer SSD werden Kerne genutzt, eine Festplatte wird nicht mehr zerklüftet gelesen.
- **Pause und Einlesen gelten überall:** Jede Aufgabe ist einzeln pausierbar, „Alle pausieren“ hält alles an. Beim
  Einlesen ruhen jetzt auch Gesichter, die bisher weiterliefen.

### Negativ
- **Kurze Wartezeiten:** Eine Aufgabe mit niedrigem Vorrang (Foto-Güte) läuft erst, wenn die wichtigeren nichts mehr zu tun
  haben.
- **Einlesen hält alle Bibliotheken an:** Einlesen einer Bibliothek hält jetzt alle Hintergrund-Aufgaben an (`IsAnyBusy`
  statt je Bibliothek). Einfacher, und das Einlesen soll schnell fertig werden.
- **Pausieren wirkt erst am Stapelende:** Gesichter, Motive und Dateidienste beenden ihren laufenden Stapel, bevor sie
  anhalten.

### Neutral
- **Bisheriger Weg als Rückfall:** Die Dienste behalten ihren bisherigen Weg (Abfrage der Scan-Sperre), wenn kein Planer
  übergeben wird. Das betrifft Tests und Rückfall.
- **Pause nur für die Sitzung:** Pause-Zustände werden nicht gespeichert; nach einem Neustart läuft alles wieder.

## Nachtrag 0.5.12 — Vorrang für Arbeit, auf die der Benutzer wartet (Herbert, 2026-10-27)

**Anlass:** Herbert: „wenn ich grafikkarte bei zb verbesserung brauche dann sollen andere hintergrundaufgaben kurz pausiert
werden. ziel soll es sein alles was ich momentan mache und brauche so schnell wie möglich zu machen.“ Bisher teilten sich
Verbessern, Gesichter und Motive die Grafikkarte Foto um Foto (`IGpuGate`, FIFO) — das Verbessern wartete zwischen seinen
Schritten jeweils auf ein Hintergrund-Foto, und die Dateidienste rechneten auf dem Prozessor weiter.

**Entscheidung:**
- `IForegroundWork` (Singleton, `AddBackgroundPlanner`): Arbeit, auf die der Benutzer wartet, meldet sich mit
  `Begin(ForegroundNeed, Name)` an (Dispose = ab; mehrere zugleich erlaubt). `ForegroundNeed.Gpu` → Gesichter und Motive
  warten; `ForegroundNeed.Cpu` → alle Aufgaben warten. Der Planer prüft das in `Block(job)` nach Alle-Pause, Katalog-Umzug
  und Einlesen; neuer Wartegrund `BackgroundWait.Foreground` mit Text „wartet — Vorrang für „Name““ (jüngste Arbeit). An-
  und Abmelden lösen sofort eine neue Vergabe aus (kein Warten auf den Takt).
- **Foto verbessern** meldet `Gpu | Cpu` an (auch mit Grafikkarte braucht es den Prozessor für Laden, Kacheln, Speichern):
  je Vorschau und für den ganzen Stapel mehrerer Fotos, damit zwischen zwei Fotos nichts dazwischenrutscht.
- **Assistent** (Ollama) meldet je Anfrage `Gpu` an, ohne Grafikkarte `Cpu`.
- **Gesichter und Motive** fragen den Planer jetzt **vor jedem Foto** (bisher nur je Lesestapel) — der Vorrang wirkt nach dem
  laufenden Foto, ebenso Alle-Pause und Einlesen. Die Dateidienste halten weiterhin am Ende ihres Stapels.
- Die GPU-Sperre bleibt unverändert (Schutz vor gleichzeitigem Rechnen); ein schon an der Sperre wartendes
  Hintergrund-Foto wird noch fertig.

**Folgen:** Längere Stapel „Foto verbessern“ halten alle Hintergrund-Aufgaben für ihre Dauer an — gewollt (Herbert wartet
auf das Ergebnis); sie laufen danach von selbst weiter. Vorschaubilder und Einlesen sind nicht betroffen.

## Nachtrag 0.5.15 — Vorrang für Lightbox und Galerie, Kern-Obergrenze (Herbert, 2026-10-27)

**Anlass:** Herbert: Lumora braucht „gefühlt ewig“, um die Lightbox zu öffnen. Messung an der laufenden App (versteckte
Sitzung, 12 Kerne): 50–100 % Prozessorlast; Stapel-Abzüge zeigten Gesichter (Vorbereitung, Erkennung, Laden), Motive,
Vorschaubilder (Bildmaße), Datum/Orte (Dateilesen) und die Gesichter-Vorschläge nach dem Start (`Parallel.For` über alle
Kerne) gleichzeitig. Zusammen deutlich mehr Rechen-Threads als Kerne; das Lightbox-Bild (`Task.Run`) wartete im
gemeinsamen Thread-Pool.

**Entscheidung:**
- `ForegroundHold`: hält für kurze, oft wiederholte Vordergrund-Arbeit EINE Anmeldung bei `IForegroundWork`, solange
  eine läuft und 2 s danach (kein Flackern beim Blättern; parallele Arbeiten melden nur einmal an).
- **Lightbox** (`LightboxImageCache`): Dekodieren per `TaskCreationOptions.LongRunning` auf eigenem Thread, währenddessen
  Vorrang „Lightbox“ (`Cpu`).
- **Vorschaubilder** (`ThumbnailService`): Vorrang „Vorschaubilder“ (`Cpu`), solange Aufträge der Galerie (nicht der
  Pflege) eingereiht oder in Arbeit sind.
- `BackgroundCpu`: parallele Hintergrund-Schleifen höchstens halbe Kerne (`MaxThreads`) — Gesichter-Vorschläge
  (`FaceRecognitionService`, `FaceModel.SuggestMany`), Training (`FaceRecognizer`), ähnliche Fotos (`SimilarGrouping`);
  ONNX-Sitzungen von Gesichtern und Motiven mit DirectML `IntraOpNumThreads` = Kerne/4 (1–4), auf dem Prozessor halbe
  Kerne (höchstens 8). Gesichter gruppieren (Personen-Seite, der Benutzer wartet) und Foto verbessern bleiben ohne Grenze.

**Folgen:** Hintergrund-Aufgaben pausieren beim Blättern in Lightbox und Galerie (Gesichter/Motive vor dem nächsten Foto,
Dateidienste am Stapelende) und laufen danach weiter; ohne Grafikkarte erkennen Gesichter und Motive etwas langsamer.
Offen: die Gesichter-Vorschläge rechnen nach jedem Start komplett neu.

## Nachtrag 0.5.18 — Prüfsummen im Leerlauf

Neue Aufgabe der Datei-Spur `BackgroundJob.Checksums` („Prüfsummen“) mit **Vorrang 3**, nach Foto-Güte: SHA-256 aller Fotos
und Videos für die App-Verbindung (ADR-0073 Nachtrag 0.5.18). Sie liest nur, wenn keine andere Dateiaufgabe wartet, und
nimmt höchstens 256 MB je Stapel.

## Alternativen

- **Gemeinsamer Arbeitsplan mit einer Warteschlange je Datei** (ein Leser, der alle Dienste füttert) → verworfen. Großer
  Umbau aller Dienste; die Datei-Spur mit gemerktem Dateikopf erreicht fast denselben Effekt.
- **Feste Zahl gleichzeitiger Dienste (z. B. 2)** → verworfen. Auf einer Festplatte bleibt das Konkurrieren um den
  Lesekopf, und es nennt keinen Grund.
- **Laufwerksart über die Lesegeschwindigkeit messen** → verworfen. Unzuverlässig (Zwischenspeicher, Last) und es kostet
  Lesezugriffe.

## Referenzen

- `src/Lumora.Services/Background/BackgroundPlanner.cs`, `StorageKindProbe.cs`, `ForegroundWork.cs` (0.5.12), `ForegroundHold.cs`, `BackgroundCpu.cs` (0.5.15)
- `src/Lumora.Services/Metadata/FileMetadataProbe.cs`
- `src/Lumora.Services/Activity/ActivityBridge.Indexes.cs`, `ActivityCenter.cs`
- `src/Lumora.Wpf/Shell/Activity/ActivityStatusViewModel.cs` („Alle pausieren“)
- Tests: `tests/Lumora.Tests/Background/BackgroundPlannerTests.cs`, `Metadata/FileMetadataProbeTests.cs`,
  `Activity/ActivityCenterTests.cs`, `Activity/ActivityBridgeTests.cs`, `Enhance/EnhanceForegroundTests.cs`, `Background/ForegroundHoldTests.cs` (0.5.15),
  `Faces/FaceIndexServiceTests.cs` (Vorrang, 0.5.12)

## Nachtrag 0.5.31 — Gesichter gruppieren unter der Kern-Obergrenze (Herbert, 2026-10-30)

Das Gruppieren unbekannter Gesichter auf der Seite Personen galt in 0.5.15 als Arbeit, auf die der Benutzer wartet, und
durfte alle Kerne nutzen. In einer großen versteckten Bibliothek (rund 450 000 Dateien) rechnete es minutenlang auf allen
Kernen; die Galerie bekam so lange keine Vorschaubilder. Seit 0.5.31 nutzt `FaceClusterer` deshalb
`BackgroundCpu.Options` (halbe Kerne). Das Gruppieren dauert dadurch etwas länger, die Bedienung bleibt flüssig.

## Nachtrag 0.5.40 — Vorschaubilder auf Vorrat (2026-11-17)

**Anlass:** Bisher entstanden Vorschaubilder nur für Angezeigtes (Galerie-Seite, Storys, Orte, Doppelte, Alben,
App-Verbindung). Wer zum ersten Mal in einen großen Ordner blättert, wartet; die Galerie meldet dabei Vordergrund-Vorrang an
und hält alle Hintergrund-Aufgaben an.

**Entscheidung:**
- Neue Aufgabe der Datei-Spur **`BackgroundJob.ThumbnailStock`** („Vorschaubilder auf Vorrat“) mit **Vorrang 4**, nach den
  Prüfsummen: `ThumbnailStockService` (`Services/Thumbnails`) erstellt still nach und nach die Vorschaubilder aller lokalen
  Fotos und Videos. Fotos zuerst, dann Videos, je nach FileId; Archive nie. Offen ist eine Datei, die nicht „fehlt“ und kein
  gültiges Vorschaubild der aktuellen Größe hat (keine Zeile, „Generated“ für eine andere Dateiversion, Pending/Stale/Failed
  außer endgültig gescheitert wie bei der Galerie). Ohne FFmpeg zählen Videos nicht als offen.
- **Kein zweiter Renderer:** `IThumbnailService.CreateStockAsync` erzeugt das Vorschaubild sofort auf dem Thread des
  Aufrufers — gleicher Renderer, gleiche Cache-Datei `{fileId}_400_still.jpg`, gleicher DB-Status, gleiches
  `ThumbnailReady`. Er geht nicht durch die Warteschlange, zählt nicht in `PendingCount` und nicht als Vordergrund-Arbeit.
  Der gemeinsame In-Memory-Zustand verhindert Doppelarbeit mit Galerie und Pflege; vor dem Rendern prüft ein Zugriff über
  den Primärschlüssel, ob die Zeile inzwischen gültig ist.
- **Spur je Datei abgeben:** Vor jeder Datei prüft der Vorrat, ob er weiter darf. Er gibt ab, sobald
  - Galerie oder Pflege Vorschaubilder eingereiht haben (`PendingCount` > 0; Wartegrund „wartet — zuerst angeforderte
    Vorschaubilder“),
  - der Planer ihn anhält (Pause, „Alle pausieren“, Einlesen, Katalog-Umzug, `IForegroundWork` wie Lightbox, Galerie,
    Verbessern),
  - oder eine wichtigere Dateiaufgabe auf die Spur wartet — neu `IBackgroundPlanner.HasWaitingAhead(job)`.

  Die übrigen Dateidienste geben die Spur weiterhin am Stapelende ab.
- **Nie ein Download:** Fehlende Dateien, Dateien unter nicht erreichbaren Wurzeln und Cloud-Platzhalter
  (`ThumbnailService.IsLocallyAvailable`) werden ohne Dateizugriff übersprungen. Es entsteht dabei auch keine DB-Zeile.
  Als nicht lokal erkannte Dateiversionen merkt sich der Dienst 30 Minuten. Folgende Durchgänge nach einem Einlesen
  fragen so nicht jedes Mal alle Platzhalter neu ab.
- **Lesen passend zum Laufwerk:** Fotos gleichzeitig nach `ReadParallelism`, höchstens `BackgroundCpu.MaxThreads`, mit
  niedriger Thread-Priorität wie bisher; Videos einzeln (FFmpeg, ADR-0007). Stapel zu 32 Dateien.
- **Auslöser:** erster Durchgang 60 s nach dem Start, danach nach jedem Einlesen, wenn Dateien dazukommen und nach „Cache
  leeren“ (`ThumbnailCacheClearedMessage`). Ist der Vorschaubild-Dienst angehalten (Katalog-Umzug), stellt er die Datei
  zurück; sie kommt danach noch einmal dran.
- **Grenze:** Eine außerhalb von Lumora gelöschte Cache-Datei mit gültiger Zeile erkennt nur die Galerie beim Anzeigen. Der
  Vorrat prüft keine Dateien im Cache, damit ein Durchgang über 500 000 Zeilen keinen Dateizugriff je Zeile braucht.
- **Schalter je Katalog:** Einstellungen › Speicher & Wartung › Vorschaubilder › „Vorschaubilder auf Vorrat erstellen“,
  Standard an, gespeichert in der Settings-Tabelle (`thumbnails.stock.enabled`). „Aus“ beendet einen laufenden Durchgang nach
  den Dateien, die gerade entstehen. Die versteckte Bibliothek hat ihren eigenen Schalter. Sie schreibt wie die Galerie nur
  in ihren eigenen Vorschaubild-Cache im Container.
- **Aktivitäts-Anzeige:** eigener Eintrag „Vorschaubilder auf Vorrat“ mit Fortschritt, Restzeit und Pause/Fortsetzen über den
  Planer, wie die übrigen Dateidienste. Im Protokoll stehen nur Zahlen.

**Nachbesserung nach der Prüfung (0.5.40):**
- **„Cache leeren“** (`IThumbnailCacheCleaner`, registriert in `AddThumbnailStock`) löscht zuerst die Dateien unter
  `ThumbnailsRoot`. Das dauert bei 500 000 Vorschaubildern Minuten; Galerie und Vorrat arbeiten dabei weiter. Dann ruht der
  Vorschaubild-Dienst kurz: `PauseAsync` wartet auf laufende Aufträge von Galerie, Pflege und Vorrat. Gelöscht werden alle
  Zeilen und die inzwischen entstandenen Dateien, danach wird der In-Memory-Zustand verworfen. Es folgen `Resume` und erst
  dann `ThumbnailCacheClearedMessage`. Ohne die Pause schrieben Galerie oder Vorrat neue Zeilen „Generated“, deren Datei das
  Löschen gleich wieder entfernte. Die oben genannte Grenze gilt damit wieder nur für Löschen außerhalb von Lumora. Auf
  laufende Aufträge wird höchstens 30 s gewartet; danach werden die Zeilen ohne Pause gelöscht (Warnung). Die Ablage
  `_undo` wird wie bisher mit geleert (ADR-0065 Nachtrag 0.4.39).
- **Pausen schachteln sich:** Jede erfolgreiche `PauseAsync` braucht ihr eigenes `Resume`; erst das letzte setzt fort.
  Bricht das Warten ab (Zeitlimit des Umzugs), nimmt `PauseAsync` die eigene Pause selbst zurück.
- **Offen** zählt ohne Dateizugriff weder gemerkte Platzhalter noch, falls eine Wurzel offline ist, Dateien darunter
  (SQL-Funktionen auf der Leseverbindung). Eintrag in der Aktivitäts-Anzeige, Wartegrund und Info-Zeile erscheinen erst nach
  dem ersten wirklich gerenderten Vorschaubild. Fortschritt und Gesamt zählen nur Dateien, die dem Vorschaubild-Dienst
  übergeben werden; in diesem Durchgang neu erkannte Platzhalter senken das Gesamt. Ein Durchgang ohne Rendern endet still
  (nur Debug). Rendert die Galerie während eines Durchgangs viele offene Dateien selbst, endet der Fortschritt unter 100 %;
  das ist hingenommen, der Eintrag verschwindet am Ende ohnehin.
- **Höchstens ein FFmpeg:** Video-Worker der Galerie und Vorrat teilen eine Sperre; sie wird vor dem Status „Pending“
  genommen, damit ein wartender Auftrag keinen Versuch verbraucht. Bei Fotos bleibt die kurze Überschneidung hingenommen:
  Der Vorrat nimmt keine neue Datei, sobald die Galerie etwas eingereiht hat; höchstens die laufenden Vorrat-Dateien (bis
  `BackgroundCpu.MaxThreads`) überlappen mit den Galerie-Workern.

**Folgen:** Galerie, Storys und Orte zeigen nach einiger Zeit überall sofort Bilder. Der Cache wächst auf die volle Größe
(≈ 5,5 GB je 100 000 Fotos, PLAN Kap. 13), statt nur auf das bisher Angesehene. Auf einer Festplatte liest der Vorrat
eine Datei nach der anderen, nur wenn nichts anderes ansteht. Die Pflege (ältere Größen ersetzen) geht vor, weil sie
`PendingCount` belegt.

**Referenzen:** `src/Lumora.Services/Thumbnails/ThumbnailStockService.cs`, `ThumbnailService.Stock.cs`,
`ThumbnailStockSettingsStore.cs`, `ThumbnailStockServiceCollectionExtensions.cs`, `ThumbnailCacheCleaner.cs`;
`src/Lumora.Wpf/Settings/ThumbnailStock/ThumbnailStockSettingsViewModel.cs`. Tests:
`tests/Lumora.Tests/Thumbnails/ThumbnailStockTests.cs`, `tests/Lumora.Tests/Thumbnails/ThumbnailServicePauseTests.cs`,
`tests/Lumora.Tests/Activity/ThumbnailStockActivityTests.cs`, `tests/Lumora.Wpf.Tests/Settings/ThumbnailStockSettingsTests.cs`,
`tests/Lumora.Wpf.Tests/Settings/ThumbnailCacheClearSettingsTests.cs`;
Rauchtest-Schritt „Vorschaubilder auf Vorrat“.

