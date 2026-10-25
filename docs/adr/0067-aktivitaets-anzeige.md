# ADR-0067: Aktivitäts-Anzeige unten rechts

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Herbert Schrotter (Abnahme Mockup 15 am 2026-10-25: eine Anzeige ganz rechts für alle Hintergrund-Vorgänge,
  Liste mit Läuft/Wartet/Zuletzt fertig, kurze Meldung, ersetzt die Einzelanzeigen), Claude (Umsetzung, Detailentscheidungen
  ohne Rückfrage)
- **Bezogen auf:** [ADR-0012](0012-operation-logging.md) (Protokoll, Undo), [ADR-0013](0013-status-ui.md) (Statusleiste),
  [ADR-0025](0025-scan-sperre-fuer-datei-operationen.md) (Wartezustände der Scan-Sperre), [ADR-0030](0030-schreiber-warten-auf-gesperrten-katalog.md)
  (Katalog-Schreibsperre), [ADR-0032](0032-archive-automatisch-entpacken.md) (Entpacken), [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md)
  und [ADR-0052](0052-motive-siglip2-lokal.md) (Indizes, GPU-Sperre), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich),
  [ADR-0049](0049-versteckte-bibliotheken.md) (eigener Katalog je Sitzung), [ADR-0065](0065-ordner-loeschen-und-fehlende-entfernen.md)
  und [ADR-0066](0066-ausschluesse-und-ordner-verschieben.md) (Datei-Vorgänge mit Rückgängig), Mockup `mockups/15-aktivitaeten.html`,
  PLAN.md Kap. 7

## Kontext

Bis 0.4.33 hatte jede Hintergrund-Quelle ihre eigene Anzeige in der Statusleiste: Scan-Fortschritt und -Ergebnis,
Vorschaubild-Warteschlange, „Wartet auf laufenden Scan…“, „Änderung wartet auf den Katalog…“, Ordnerüberwachung, Entpacken,
Gesichter (mit Pause/Abbrechen), Motive (mit Pause/Abbrechen), Aufnahmedatum, Orte, GPS-Spuren, Doppelte, Foto-Güte, Storys
und „Hintergrund: …/Bereit“. Liefen mehrere gleichzeitig, wurde die Leiste unlesbar; Datei-Vorgänge (Verschieben, Papierkorb,
Übernehmen …) zeigten gar keinen Fortschritt, ihr Rückgängig lag verstreut (Galerie-Taste, Ergebnisfeld im Ordnerbaum,
Einstellungs-Karten). Mockup 15 bündelt alles an einer Stelle.

## Entscheidung

### A) Zentrale `IActivityCenter` (Services, WPF-frei)

`Services/Activity/ActivityCenter` — **eine Instanz je DI-Container**, also je Katalog-Sitzung: Der Wechsel in eine versteckte
Bibliothek und das Zusperren bauen den Container neu (ADR-0049), die Liste beginnt dann leer; nichts wird gespeichert.
- Quellen starten Vorgänge mit `ActivityStartInfo` (Art, Titel, Ziel, vom Benutzer gestartet?, unter „Zuletzt fertig“ behalten?,
  optional Schlüssel für Hintergrund-Indizes, Abbrechen/Pause/Fortsetzen als Delegates der Quelle) und melden über
  `IActivityHandle`: Fortschritt (Total 0 = unbestimmt), Wartet mit Grund (optional eigener Abbruch-Weg fürs Warten), Pause,
  Fertig mit Ergebnis/Aktionen/Details, Fehler, Abgebrochen oder still beendet (`Dismiss`, kein Eintrag).
- **Läuft** ist nach Wichtigkeit geordnet: vom Benutzer gestartet vor Hintergrund, laufend vor angehalten, dann nach Art
  (Datei-Vorgänge, Übernehmen, Sortieren, Einlesen, Entpacken, GPS-Spuren, Vorschaubilder, Doppelte, Gesichter, Motive, …), bei
  Gleichstand der jüngste. Die Statusleiste zeigt den ersten (ohne Laufendes den ersten Wartenden) und „+n“ für den Rest.
- **Zuletzt fertig**: höchstens 10, neuester zuerst. Hintergrund-Indizes landen dort nur bei Fehlern, Hinweisen oder Abbruch.
- **Restzeit** erst nach 3 s Fortschritt (lineare Schätzung ab Beginn bzw. nach Mengenwechsel/Pause neu).
- **Drosselung:** `Changed` meldet die erste Änderung nach einer Ruhe sofort, alles Weitere innerhalb von 150 ms gebündelt am
  Intervallende (Timer über `TimeProvider`, testbar). Die Oberfläche liest dann den jüngsten Stand; `Finished` kommt sofort
  (für die kurze Meldung). So bekommt die UI-Automation höchstens alle 150 ms neue Werte, egal wie oft Quellen melden.
- **Rückgängig** ist eine Aktion des fertigen Eintrags mit dem Weg der Quelle; danach entfällt sie und das Ergebnis bekommt
  „· rückgängig gemacht“. Wird derselbe Batch auf anderem Weg zurückgenommen (Galerie-Taste), zieht `MarkUndone` den Eintrag nach.
- Protokolliert werden nur Art und Id (Debug), nie Titel — Titel können Ordnernamen enthalten.

### B) Quellen anschließen, ohne sie umzubauen

1. **Datei-Vorgänge:** `ActivityTrackingFileOperationService` ist seit 0.4.36 das registrierte `IFileOperationService` und
   dekoriert den unveränderten `FileOperationService` (eigene Singleton-Registrierung). Jeder Aufruf (Verschieben, Umbenennen,
   Papierkorb, Ordner umbenennen/verschieben/in den Papierkorb, Übernehmen, Fehlende/Ausgeschlossene entfernen, Rückgängig,
   Zurückholen aus dem Papierkorb) wird ein vom Benutzer gestarteter Vorgang:
   - **Fortschritt je Datei** über `ActivityScope` (AsyncLocal): der Dekorierer setzt seinen Vorgang, `RunBatchAsync` meldet nach
     jedem Item `ActivityScope.ReportItems` (eine Zeile im Dienst); „Übernehmen“ über den vorhandenen `IProgress<int>`.
   - **Abbrechen** über ein mit dem Token des Aufrufers verknüpftes `CancellationTokenSource` — der Batch bricht wie bisher
     zwischen zwei Dateien ab, erledigte bleiben (Protokoll, Rückgängig).
   - **Rückgängig** über `UndoBatchAsync(batchId)` — kein zweiter Weg; nur wo der Batch rückgängig machbar ist (Papierkorb für
     Dateien nicht: dessen Rückgängig hat nur die Seite „Doppelte“ mit eigener Katalog-Rückschreibung).
   - Ordner anlegen (sofort fertig) und Abfragen laufen ohne Eintrag durch.
   - **Verschachtelung:** Läuft ein Aufruf schon in einem Vorgang (`ActivityScope.IsOwned`), entsteht kein eigener Eintrag — ein
     Sortier-Lauf mit vielen Batches ist ein Vorgang; `ActivityScope.Suppress()` unterdrückt Einträge für den Papierkorb des
     Archivs beim Entpacken (Teil des Entpackens, kein eigener Vorgang).
   - Texte nennen Zielordner nur mit Namen („nach „Urlaub 2024““), nie mit Pfad (Bibliotheks-Trennung, versteckte Sitzung).
2. **Sortierer:** `SorterMoveExecutor` startet (mit Zentrale) einen Vorgang „Sortieren“/„Flatten“ mit dem vorhandenen
   `SorterProgress`, Abbrechen per verknüpftem Token und Rückgängig über `UndoLastRunAsync` (wie „Rückgängig (letzter Lauf)“).
3. **Messenger-Brücke** `ActivityBridge` (Services, gestartet beim Sitzungsstart vor dem ersten Scan): Scan-Fortschritt/-Ende/
   -Abbruch (Ergebnis mit „Fehlende zeigen“ bei neu fehlenden Dateien), Wartezustände der Scan-Sperre (eine wartende
   Datei-Operation mit eigenem Vorgang macht diesen Eintrag „wartet“ — die Nachricht kommt im Ablauf des Wartenden —, sonst ein
   eigener Eintrag; Abbrechen über `IScanGate.CancelWait`), Katalog-Schreibsperre, Ordnerüberwachung (anstehend/angehalten als
   „Wartet“), automatisches Entpacken (mit Details der gemeldeten Archive), Gesichter und Motive (Pause/Fortsetzen/Abbrechen über
   `Pause`/`Resume`/`CancelRun`; derselbe Fehler nur einmal), Doppelte, Aufnahmedatum, Orte, Genauigkeit, Foto-Güte (Abbrechen
   über `CancelPass`), Storys, GPS-Spuren (Abbrechen über `Cancel`, Ergebnis unter „Zuletzt fertig“) und die
   Vorschaubild-Warteschlange (alle 500 ms `PendingCount`, Fortschritt = höchster Stand minus Rest).
4. **GPU-Sperre:** Gesichter und Motive wechseln sich Foto um Foto ab (`IGpuGate`, ADR-0052) — keiner wartet dabei auf den
   anderen. Statt eines erfundenen „wartet“ sagt die zweite Zeile „Grafikkarte (abwechselnd mit „Gesichter““). Wartet ein Index
   wirklich (Katalog, Scan), kommt der Grund aus seinem Zustand.

### C) Oberfläche (Wpf)

- `Shell/Activity/ActivityStatusViewModel` liest die Zentrale nach `Changed` (höchstens ein Auftrag mit Background-Priorität
  steht aus), Zeilen (`ActivityItemViewModel`) werden **in place** aktualisiert (gleiche Id → gleiche Zeile).
- **Statusleiste** (`StatusBar.xaml`): links nur die Bibliothek, rechts nur Hinweise, die keine Vorgänge sind (Recovery,
  offline, Doppelte im Eingang, Orte-Zahlen, Sicherheits-Check), ganz rechts `ActivityIndicator`: Drehsymbol bzw. Uhr/Häkchen,
  Text, Balken 90 px, „+n“; ohne Vorgang 8 s „Alles erledigt“, dann „Keine Aktivität“. Die alten Text-Eigenschaften des
  `StatusBarViewModel` bleiben als Zustand (Tests, Rauchtest), werden aber nicht mehr gezeigt.
- **Liste und kurze Meldung** liegen nicht in einem Popup, sondern in einem Canvas der Größe 0 am rechten Rand, der über die
  Statusleiste nach oben ragt: bleibt im Fenster (kein Topmost über fremden Fenstern), folgt dem Theme und ist im Screenshot
  (RenderTargetBitmap) sichtbar. Schließen bei Klick daneben, Esc oder Fensterwechsel (Code-Behind).
- **Kurze Meldung** 6 s nur für vom Benutzer gestartete Vorgänge (nicht bei „Abgebrochen“), mit Rückgängig, wenn der Eintrag es
  hat; nicht gleichzeitig mit der aufgeklappten Liste.
- „Fehlende zeigen“ öffnet die Galerie mit „Ablage: Nur fehlende“ (`IActivityNavigator`); „Details“ zeigt den Text im Dialog.

## Alternativen

- **Jede Quelle ruft die Zentrale direkt auf** — verworfen: viele Dienste hätten umgebaut werden müssen; die Messenger-Records
  und der Dekorierer reichen, die Quellen bleiben unverändert (bis auf eine Zeile Fortschritt je Datei und zwei
  `Suppress`-Zeilen).
- **Popup für Liste/Meldung** — verworfen: Popups sind eigene Topmost-Fenster (Meldung über fremden Anwendungen) und fehlen im
  Fenster-Screenshot des Rauchtests.
- **Alte `StatusBarViewModel`-Teile entfernen** — vorerst nicht: viele Tests und Rauchtest-Schritte prüfen deren Texte; sie kosten
  wenig. Kann später entfallen.
- **Liste speichern** — verworfen (Auftrag): nur die laufende Sitzung.

## Folgen

- Keine Migration, keine neuen Pakete.
- Nicht angeschlossen (Fortschritt weiter in ihren Dialogen bzw. Seiten): manuelles Entpacken im Entpacken-Dialog, „Neu aufbauen“
  (ClearAndRescan zeigt sich aber als Einlesen), „Speicherort ändern“, Gesichtsdaten löschen, Story-Assistent/Collage,
  Foto verbessern (noch nicht gebaut). Kopieren gibt es in Lumora (noch) nicht. Einlesen ist nicht aus der Liste abbrechbar
  (der Scan-Dienst hat keinen Abbruch von außen; die Bibliotheksseite bricht ihn ab).
- Einlesen meldet sich ohne kurze Meldung (Ordnerüberwachung löst es oft aus); das Ergebnis steht unter „Zuletzt fertig“.
- Neue Hintergrund-Quellen hängen sich über eine Messenger-Nachricht in `ActivityBridge` oder direkt über `IActivityCenter` ein.
