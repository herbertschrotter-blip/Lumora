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

## Alternativen

- **Gemeinsamer Arbeitsplan mit einer Warteschlange je Datei** (ein Leser, der alle Dienste füttert) → verworfen. Großer
  Umbau aller Dienste; die Datei-Spur mit gemerktem Dateikopf erreicht fast denselben Effekt.
- **Feste Zahl gleichzeitiger Dienste (z. B. 2)** → verworfen. Auf einer Festplatte bleibt das Konkurrieren um den
  Lesekopf, und es nennt keinen Grund.
- **Laufwerksart über die Lesegeschwindigkeit messen** → verworfen. Unzuverlässig (Zwischenspeicher, Last) und es kostet
  Lesezugriffe.

## Referenzen

- `src/Lumora.Services/Background/BackgroundPlanner.cs`, `StorageKindProbe.cs`, `ForegroundWork.cs` (0.5.12)
- `src/Lumora.Services/Metadata/FileMetadataProbe.cs`
- `src/Lumora.Services/Activity/ActivityBridge.Indexes.cs`, `ActivityCenter.cs`
- `src/Lumora.Wpf/Shell/Activity/ActivityStatusViewModel.cs` („Alle pausieren“)
- Tests: `tests/Lumora.Tests/Background/BackgroundPlannerTests.cs`, `Metadata/FileMetadataProbeTests.cs`,
  `Activity/ActivityCenterTests.cs`, `Activity/ActivityBridgeTests.cs`, `Enhance/EnhanceForegroundTests.cs`,
  `Faces/FaceIndexServiceTests.cs` (Vorrang, 0.5.12)
