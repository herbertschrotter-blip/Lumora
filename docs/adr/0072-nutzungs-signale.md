# ADR-0072: Nutzungs-Signale — Tagessummen je Foto, gedämpfter Zuschlag, „Vergessene Schätze“, „Lieblinge“

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Claude (autonom, Issue #16; Abnahme von Mockup 20 durch Herbert ausstehend)
- **Bezogen auf:** [ADR-0057](0057-foto-guete-und-highlights.md) (Foto-Güte und Highlights),
  [ADR-0058](0058-story-erkennung.md)/[ADR-0060](0060-story-seite.md) (Storys, Rückblicke),
  [ADR-0063](0063-story-assistent.md) („die schönsten“), [ADR-0068](0068-galerie-ordnerleiste-titelbild-kontextmenue.md)
  (Titelbild, Kopieren nach außen, Exportieren), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md)/[ADR-0049](0049-versteckte-bibliotheken.md)
  (Trennung, versteckte Bibliothek), [ADR-0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md) („Neu aufbauen“)

## Kontext

„Die schönsten“ (Story-Highlights, Titelbilder, Story-Assistent) wählt Lumora bisher nur nach Technik (Schärfe, Belichtung,
Bildwirkung), Personen, Favorit und Sternen. Herbert will, dass Lumora lernt, was ihm gefällt: Öffnen, Ansehdauer (nur aktiv,
1 s bis 2 min je Ansicht), Zoom, Wiederkehr an anderen Tagen, Verwendung (Album, Story, Collage, Titelbild) und Weitergabe
(Drucken, Teilen, Exportieren, Kopieren nach außen). Nicht zählen sollen Sortieren, die Seite „Doppelte“ und Durchblättern im
Schnelllauf; Diashow nur gedämpft. Datenschutz: nur Tagessummen je Foto, je Katalog, versteckt nur im Container, nichts im
Protokoll; abschaltbar und löschbar. Der Zuschlag soll klein sein, einen Grund nennen und fehlende Nutzung nie bestrafen —
sonst verstärken sich Highlights selbst (was in einer Story oben steht, wird öfter angesehen).

## Entscheidung

1. **Speicherung:** neue Tabelle `FileUsageDays (FileId → Files ON DELETE CASCADE, Day, Opens, Seconds, Zooms, SoftOpens,
   SoftSeconds, Uses, Shares; PK (FileId, Day), IX_FileUsageDays_Day (Day, FileId))` — Migration `AddUsageSignals`, nur
   CREATE TABLE. `Day` = Kalendertag in Ortszeit (Tage seit 1970-01-01); keine Zeitpunkte einzelner Ansichten. Je Katalog —
   die versteckte Sitzung schreibt in ihren Katalog im Container. Settings `usage.enabled` (Standard an), `usage.forgotten`
   (Standard an), `usage.since` (erster Tag, nur Anzeige).
2. **Messung** (`Services/Usage`, WPF-frei): `UsageViewTracker` zählt je gezeigter Datei nur Zeit, in der das Fenster vorne
   und nicht minimiert ist (Lightbox und Diashow melden Aktivieren/Deaktivieren/Minimieren), Zoom über `IsZoomFit → false`.
   `UsageSignalService` (`IUsageSignals` + `IUsageService`) verwirft Ansichten unter 1 s, kappt je Ansicht bei 2 min, summiert
   je (Datei, Tag) im Speicher und schreibt nach 3 s in **einer** Transaktion per UPSERT; ob „Nutzung lernen“ an ist, liest der
   Schreibvorgang selbst (aus → verworfen, auch bereits Gesammeltes). Gelöschte Dateien fallen still heraus. Uhr über
   `TimeProvider` (Tests, Rauchtest).
3. **Herkunft:** Lightbox aus Galerie/Ordnern/Personen/Orten/Alben zählt voll (`Opens`, `Seconds`), aus einer Story
   gedämpft (`SoftOpens`, `SoftSeconds`), Diashow nur als Zeit (`SoftSeconds`), aus „Doppelte“ gar nicht
   (`ILightboxService.Open(…, UsageViewContext)`; Standard-Methode im Interface, Ersatz-Implementierungen bleiben gültig).
   Verwendung/Weitergabe melden Aktionen mit einer Zeile `UsageReport.Used/Shared(fileIds)` (Messenger-Nachricht
   `UsageSignalMessage`, die der Dienst der Sitzung empfängt): Zu Album hinzufügen, Story-Einträge, selbst gewähltes
   Titelbild (`ICoverService.SetAsync`, Album-Ansicht, Story-Ansicht), Strg+C/Kopieren in die Zwischenablage, Ziehen in ein
   anderes Programm (Effekt Kopieren), „Kopieren nach…“ nur außerhalb der Bibliothek, Exportieren. Je Datei und Meldung einmal.
4. **Wert:** Rohwert = Öffnen (≤ 20) + 0,5 × gedämpftes Öffnen (≤ 20) + Minuten (≤ 30) × 0,5 + gedämpfte Minuten × 0,125 +
   0,5 × Zoom (≤ 10) + 2 × jeder weitere Tag (≤ 10) + 2 × Verwendung (≤ 5) + 3 × Weitergabe (≤ 5); Vorliebe = 1 − e^(−Roh/8).
   Derselbe Rohwert in C# (`UsageScoring.Raw`) und SQL (`UsageScoring.RawSql`, Tests vergleichen).
5. **Wirkung:** `QualityHighlightSelector` liest die Summen (nur mit „Nutzung lernen“) und `HighlightPlanner` addiert
   **0,12 × Vorliebe** (kleiner als ein Favorit mit 0,15; ohne Nutzung 0, nie ein Abzug) — wirkt in Story-Highlights,
   Titelbild-Wahl, Collage und „die schönsten“ des Story-Assistenten. Ab Vorliebe 0,25 steht der Grund im Tooltip („oft
   angesehen“, „2× geteilt“, „weitergegeben“, „verwendet“).
6. **„Vergessene Schätze“** = neue Rückblick-Art (`StoryMemoryKind.ForgottenTreasures`, Schlüssel `R:F:yyyy-MM`): Kandidaten
   sind Fotos des Bereichs, vor über einem Jahr aufgenommen, seit einem Jahr nicht geöffnet und gut (Favorit, ab 4 Sternen,
   scharf + gut belichtet + Bildwirkung ≥ 0,55 oder früher ein Liebling); je Monat eine gleichmäßige andere Stichprobe von
   höchstens 2 000, davon bis zu 24 über die Highlight-Auswahl, mindestens 6. Wie jeder Rückblick erst beim Öffnen als
   Story (Art Rückblick) gespeichert; danach bleibt die geöffnete Auswahl. Unter „Alle“ neben dem Banner „Heute vor …“.
7. **„Lieblinge“** = Galerie-Filter (Chip in der Zeile „Inhalt“ neben „Doppelte“, UND mit allen Filtern) statt eines
   Sortierschlüssels: `f.Id IN (Lieblinge)` mit Rohwert ≥ 6, leer bei „Nutzung lernen“ aus.
8. **Oberfläche:** Einstellungen › Storys, Karte „Nutzung lernen“ (Schalter, „Vergessene Schätze anbieten“, Zahlen,
   „Gelernte Nutzung löschen…“ mit Rückfrage); Lightbox-Info-Zeile „Nutzung“ („9× angesehen an 6 Tagen · 4 min …“).
9. **„Neu aufbauen“:** `UserDataCarryOver.Usage.cs` parkt die Tageszeilen (negative FileId) und setzt sie auf die neuen Ids.

## Begründung

- Tagessummen beantworten alle gewünschten Fragen (wie oft, wie lange, an wie vielen Tagen, wann zuletzt grob) ohne ein
  Ereignisprotokoll, das ein Bewegungsprofil wäre; die Tabelle bleibt klein (nur angesehene Fotos).
- Der Zuschlag ist gesättigt und kleiner als ein Favorit: Herbert behält mit Herz und Sternen das letzte Wort, Technik bleibt
  wichtig, und Ansehen aus Story/Diashow zählt gedämpft — so verstärken sich Highlights nicht selbst.
- Ein Filter „Lieblinge“ ist eine kleine Menge über den Primärschlüssel — schnell auch bei 500 000 Dateien. Ein
  Sortierschlüssel „Meistgesehen“ müsste alle Dateien je Seite sortieren (oder wie die Kategorie-Sortierung segmentweise
  lesen, ADR-0031) — Aufwand ohne klaren Mehrwert, weil die meisten Fotos keine Nutzung haben.
- „Vergessene Schätze“ als Rückblick nutzt den vorhandenen Weg (dynamisch, erst beim Öffnen gespeichert, anheften,
  ausblenden, Diashow) statt einer neuen Story-Art mit eigener Erkennung.
- Melden über den Messenger hält die Aktionen anderer Module (Rechtsklick-Menü, „Foto verbessern“) frei von neuen
  Abhängigkeiten: eine Zeile genügt.

## Konsequenzen

### Positiv
- „Die schönsten“ treffen Herberts Geschmack besser, mit sichtbarem Grund.
- Abschalten und Löschen jederzeit; aus heißt auch „nicht verwenden“.
- Neue Wege (Drucken, Teilen, Handy-App Welle 7, „Verbessern“) melden mit einer Zeile.

### Negativ
- Die Messung hängt am Fenster-Aktivzustand: ein Foto, das neben einem anderen Programm sichtbar ist, zählt nicht.
- Tage nach Ortszeit des Rechners; eine Reise über Zeitzonen kann eine Ansicht dem Nachbartag zuordnen.
- Die geöffnete „Vergessene Schätze“-Story bleibt Rückblick-Art (Kindlabel „Rückblick“), Titel „Vergessene Schätze“.

### Neutral
- Die Daten gehören zum Katalog (`FileUsageDays`); „Doppelte aufräumen“ sichert sie wie alle abhängigen Zeilen
  (Abzug über die Fremdschlüssel), überträgt sie aber nicht auf die behaltene Datei.
- Smart-Alben speichern den Filter „Lieblinge“ nicht.

## Alternativen

- **Ereignisprotokoll je Ansicht** → verworfen: Zeitpunkte wären ein Nutzungsprofil, Datenschutz-Vorgabe „nur Tagessummen“.
- **Sortierschlüssel „Meistgesehen“** → verworfen (Leistung, siehe Begründung); der Filter zeigt dieselben Fotos.
- **Abzug für nie angesehene Fotos** → verworfen: neue oder lange nicht geöffnete Fotos wären benachteiligt („Vergessene
  Schätze“ zeigt sie gerade).
- **Eigene Story-Art (StoryKind 6)** → verworfen: alle Filter, Zähler und Überschriften müssten sie kennen; ein Rückblick
  reicht.

## Referenzen

- Mockup [`mockups/20-nutzungs-signale.html`](../../mockups/20-nutzungs-signale.html) (Entwurf, Abnahme offen)
- Code: `src/Lumora.Services/Usage/`, `src/Lumora.Services/Stories/StoryService.Treasures.cs`,
  `src/Lumora.Wpf/Lightbox/LightboxViewModel.Usage.cs`, `src/Lumora.Wpf/Usage/`, `src/Lumora.Wpf/Gallery/ViewModels/GalleryViewModel.Usage.cs`
- Tests: `tests/Lumora.Tests/Usage/UsageSignalTests.cs`, `tests/Lumora.Wpf.Tests/Usage/UsageUiTests.cs`, Rauchtest-Schritt „Nutzungs-Signale“
