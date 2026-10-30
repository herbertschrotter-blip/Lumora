# ADR-0062: Ungenaue Orte verbessern und GPS-Spuren (Garmin) als beste Quelle

- **Status:** Accepted
- **Datum:** 2026-10-22
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0050](0050-orte.md) (Orte, „Ort setzen“, Rückgängig), [ADR-0051](0051-orte-weltkarte-mit-foto-gruppen.md)
  (Seite „Orte“, MapControl), [ADR-0046](0046-aufnahmedatum.md) (Aufnahmedatum, Zeitzone), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md)
  (Gruppen, Verknüpfen/Lösen), [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken), Mockup
  `mockups/10-ungenaue-orte.html` (abgenommen inkl. Garmin-Erweiterung)

## Kontext

Viele Handy-Fotos haben einen Ort, der Kilometer daneben liegt: Steht in den EXIF-GPS-Tags nur Breite/Länge (Tags 1–4)
ohne GPS-Höhe (6) und ohne GPS-Zeit (7/29), hat das Handy die Position über Mobilfunk/WLAN geschätzt. In einer Stichprobe
aus Herberts Fotos traf das ~40 % der verorteten Handy-Fotos. Herbert zeichnet fast jede Tour mit einer Garmin Fenix auf —
das ist die genaueste Quelle. Garmin vergibt die Connect-API nur an Firmen (Programm derzeit pausiert): kein API-Schlüssel,
kein Login mit Passwort, keine inoffiziellen Bibliotheken. Daten kommen nur über Dateien herein, nichts verlässt den PC.
Rahmen: 50 000–500 000 Fotos, > 1 000 Aktivitäten mit mehreren tausend Punkten, Bibliotheks-Trennung, versteckte
Bibliotheken ohne Spuren außerhalb, Fotos nie beschädigen.

## Entscheidung

1. **Erkennen „Position geschätzt“** (neue Spalten an `Files`, Migration `AddGpsTracks`, nur ADD COLUMN):
   `GeoAccuracy` (NULL = noch nicht bewertet, 0 nicht bewertbar, 1 genau, 2 geschätzt) und `GeoAccuracyReason`
   (Bitmaske). Regeln je Datei (`GeoAccuracyRules`, rein): nur Breite/Länge ohne Höhe UND ohne GPS-Zeit → geschätzt;
   `GPSProcessingMethod` (27) mit NETWORK/CELLID/WLAN/WIFI → geschätzt; `GPSHPositioningError` (31) > 500 m → geschätzt.
   Über mehrere Dateien (`GeoJumpDetector`, rein): ein nach den Tags genaues Foto **ohne GPS-Zeit** gilt als geschätzt
   („unmöglicher Sprung“), wenn es zu zeitnahen genauen Fotos (≤ 30 min) nur mit > 150 km/h und > 2 km passt — sind
   Nachbarn mit GPS-Zeit da, entscheiden nur sie (Mehrheit), sonst die Mehrheit von mindestens zwei schwachen Nachbarn
   (zwei Fotos, die sich nur gegenseitig widersprechen, bleiben unmarkiert). Videos, XMP-Begleitdateien und in Lumora
   gesetzte Orte sind „nicht bewertbar“; die Liste „Ungenau“ zeigt nur `GeoSource = 1 AND GeoAccuracy = 2`.
   Gelesen werden die Angaben im Hintergrund (`GeoAccuracyService`, Muster des Orte-Nachtrags: Stapel zu 200, eine
   Transaktion je Stapel, Scan-Sperre, `IDatabaseAccessGate.IsSuspended`, Statusleiste „Orte prüfen: n / m“, nur die
   gelesene Dateiversion) mit einem eigenen kleinen EXIF-Leser (`ExifGpsDetailsReader`) — dabei auch `GpsTimeUtc`,
   `ExifTimeOffset` (OffsetTimeOriginal) und `CameraModel` (IFD0 Make/Model) für den Zeitabgleich. Ein Incremental-Scan
   mit geändertem Inhalt setzt `GeoAccuracy` zurück; gefilterter Index `IX_Files_Library_GeoEstimated` für den Zähler.
2. **GPS-Spuren je Bibliotheks-Gruppe** (`GpsTracks`, `GroupId` ohne Fremdschlüssel wie Tags/Alben, in
   `LibraryGroupStore.TermTables`, `DeleteGroupIfEmpty` und `LibraryLinkStore`: Verknüpfen führt zusammen — gleiche
   Prüfsumme nur einmal —, Lösen kopiert, leere Gruppe löscht). **Eine Zeile je Aktivität**, die Punkte kompakt als BLOB
   (`TrackCodec`: Version, Flags, Punktzahl, je Punkt ZigZag-Varint-Differenzen von Zeit (s), Breite/Länge (1e-6° ≈ 0,11 m)
   und Höhe (dm), Deflate) — 5 000 verrauschte Punkte im Sekundentakt ≈ 13 KB (gemessen) statt ~160 KB roh bzw. 5 000 Zeilen. Dazu Start/Ende (UTC),
   Begrenzungsrahmen, Punktzahl, Länge, Sportart, Gerät, Name, Format und `Checksum` (SHA-256 über Zeit + Mikrograd;
   `UX_GpsTracks_Group_Checksum` gegen Doppel-Import), `IX_GpsTracks_Group_Time (GroupId, StartUtc, EndUtc)`.
3. **Einlesen** (`GpsTrackImportService`): Garmin-Datenexport-ZIP mit verschachtelten ZIPs
   (`DI_CONNECT/…/UploadedFiles_*.zip`) — Einträge werden gestreamt, FIT/GPX/TCX höchstens 64 MB in den Speicher, innere
   ZIPs einzeln in `ICatalogStoragePaths.TempRoot` (in einer versteckten Bibliothek im Container) kopiert und sofort
   gelöscht, höchstens zwei Ebenen; einzelne Dateien und Ordner; im Hintergrund mit Fortschritt (Statusleiste,
   Einstellungen) und Abbruch, Doppelte übersprungen, Aktivitäten ohne GPS gezählt. Protokolle nennen nur Zahlen (Garmin-
   Dateinamen enthalten die E-Mail-Adresse). **Beobachteter Ordner** (FileSystemWatcher, entprellt; Merker = Zeitpunkt des letzten
   Durchsuchens, verglichen mit dem jüngeren von Änderungs- und Erstelldatum — auch kopierte ältere Aktivitäten zählen). **Uhr per USB** nur als Massenspeicher-Laufwerk mit `GARMIN\Activity` (Prüfung alle 10 s, nur
   Wechsellaufwerke) — vorher wird gefragt (`GarminDeviceDetectedMessage` → Dialog). Uhren mit **MTP** (Fenix 6/7 u. a.)
   erscheinen nicht als Laufwerk und werden bewusst nicht unterstützt; die Oberfläche erklärt den Weg über den Export bzw.
   den Ablageordner von Garmin Express.
4. **FIT ohne SDK:** eigener, minimaler Leser (`FitTrackReader`) nach der öffentlichen Protokollbeschreibung — Kopf,
   Definitions-/Datennachrichten beider Byte-Reihenfolgen, Entwicklerfelder überspringen, komprimierte Zeitstempel,
   verkettete Dateien; ausgewertet `record` (timestamp, position_lat/long in Semikreisen, altitude/enhanced_altitude),
   `session`/`sport` (Sportart), `file_id`/`device_info` (Gerät, Fenix-Produktnummern). GPX/TCX per `XmlReader` ohne DTD.
5. **Zeitabgleich** (`PhotoTimeResolver`, rein): Wanduhrzeit = `CapturedAt` + gespeicherter Abstand, dann in dieser
   Reihenfolge (1) `OffsetTimeOriginal` (EXIF 36881), (2) bei genauem GPS die Differenz GPS-Zeit ↔ Wanduhrzeit auf 15 min
   gerundet, (3) Zeitzone aus der Einstellung (Standard: Windows-Zeitzone mit Sommerzeit für das jeweilige Datum); danach
   die **Kamera-Abweichung je Kamera-Modell** (+ = Kamera geht vor). Videos tragen UTC aus dem Container; ungefähre
   Daten zählen nicht. Vorschlag „aus GPS-Zeit ermittelt“ nur für Kameras mit eigenem GPS-Zeitstempel (Median der
   Restabweichung Wanduhrzeit − GPS-Zeit − Zone über ≥ 3 Fotos; Zone aus `OffsetTimeOriginal`, sonst der Median auf volle
   Stunden gerundet — erkennt Abweichungen bis ±30 min; < 5 s = passt) — für Kameras ohne GPS lässt es sich nicht sauber berechnen (von Hand).
6. **Vorschläge mit Stufen** (`GeoSuggestionEngine`, rein): **Garmin-Spur** (Foto-UTC in einer Aktivität ± Toleranz,
   Interpolation zwischen den umgebenden Spurpunkten mit Lücke ≤ 120 s — oder einer längeren Lücke ≤ 15 min, wenn die
   Punkte davor/danach ≤ 150 m auseinander liegen: Pause am selben Ort) > **sicher** (Interpolation zwischen zwei genauen
   Fotos, Abstand ≤ 30 min, ≤ 150 km/h oder < 500 m) > **wahrscheinlich** (Spur mit Lücke ≤ 15 min bzw. ein genaues
   Nachbarfoto ≤ 15 min) > **kein Vorschlag**. Stützen sind genaue EXIF-Orte (UTC = GPS-Zeit, wenn vorhanden) und aus
   Spuren übernommene Orte im Bereich. Vorschläge gibt es für „Ungenau“ UND „Ohne Ort“. Von Hand gesetzte bzw.
   übernommene Orte (`GeoSource` 3–5) sind nie Kandidaten und werden nie automatisch überschrieben; „Ablehnen“ merkt
   `Files.GeoSuggestionState = 1` (überlebt „Neu aufbauen“).
7. **Übernehmen** über den Weg von „Ort setzen“ (`IGeoAssignService.AssignAsync`): je Datei eigene Koordinate und
   Herkunft in EINEM `OperationBatch` (Typ 8) mit `GeoTagUndo` — **neue Herkunft** `GeoSource.Track = 4` („aus GPS-Spur“)
   bzw. `GeoSource.Neighbors = 5` („aus Nachbarfotos“), von Hand auf der Karte verschoben = `Manual`. „Nur im Katalog“
   (Standard) fasst die Datei nicht an; „auch ins Foto schreiben (EXIF)“ nutzt `GeoFileWriter` (JPEG verlustfrei, sonst
   XMP, Videos nur Katalog). Rückgängig über den Hinweis bzw. die Galerie (`IGeoTagUndoService`, Byte für Byte).
   Incremental-Scan und „Neu aufbauen“ behandeln 4/5 wie 3 (bleiben erhalten).
8. **Oberfläche** (WPF-UI, DynamicResource, Mockup 10): Seite Orte mit Umschalter „Ohne Ort n | Ungenau m“, Kopf „n mit
   geschätzter Position“, Erklärung, Filter-Chips mit Zählern, virtualisierte Liste nach Tagen/Ausflügen mit Plaketten
   und Aktionen (✓/✕/„Ort setzen…“/Rückgängig), Fuß „Speichern“ + „Garmin + sichere übernehmen (n)“ + „Ausflug (n)“;
   unter „Ohne Ort“ der Hinweis „n Fotos ohne Ort … mit Vorschlag“. Karte: Überlagerung in `MapControl` (Spur korallenrot,
   gespeicherter Punkt grau, Vorschlag in der Akzentfarbe, gestrichelte Korrektur, Fotos mit echtem GPS grün,
   Tooltips, Vorschlag der ausgewählten Zeile ziehen); in der Liste blendet die Karte die Foto-Gruppen aus. Lightbox-Info:
   „Position geschätzt“ mit Begründung, Vorschlag samt Quelle, „Vorschlag übernehmen“, „Auf der Karte zeigen“, „Ort
   setzen…“. Einstellungen › **Orte**: Garmin-Aktivitäten (kein API-Schlüssel nötig/möglich, „Garmin-Datenexport öffnen“
   → Garmin-Konto im Standardbrowser, „Export-ZIP einlesen…“, FIT/GPX-Dateien, Ordner beobachten, USB-Erkennung, Stand,
   Aktivitäten anzeigen, Spuren entfernen…) und Zeitabgleich (Zeitzone, Toleranz, Kamera-Abweichungen, Vorrang).
   Einstellungen in der Settings-Tabelle des Katalogs (`places.tracks.*`, `places.suggestions.saveToFile`).

## Begründung

- **Tag-Regeln statt Online-Prüfung:** die Kombination „nur 1–4“ ist ein starkes, lokal prüfbares Signal; Netzwerk-
  Verfahren und Fehlerradius ergänzen es, die Sprung-Regel fängt Ausreißer mit vollständigen Tags. 150 km/h und 2 km
  trennen Auto/Bahn im Alltag sicher von Ortungsfehlern; Flüge liegen zeitlich weiter auseinander als 30 min.
- **BLOB je Aktivität:** 1 300 Aktivitäten × 5 000 Punkte wären 6,5 Mio. Zeilen samt Index; als BLOB bleibt die Tabelle
  klein (≈ 20–40 MB), die Zeitraum-Abfrage trifft wenige Zeilen, dekodiert wird nur, was ein Foto abdeckt.
- **Eigener FIT-Leser statt `Garmin.FIT.Sdk`:** das NuGet-Paket (21.218.0, netstandard2.0, baut unter net10.0) steht
  unter der *FIT Protocol License*: Nutzung nur für „internal business purposes“, Weitergabe der Licensed Technology an
  Dritte untersagt („rent, lease, lend, sell, sublicense, … distribute … or otherwise make available“), das SDK gilt als
  vertrauliche Information. Lumora wird als portables Paket weitergegeben und liegt auf GitHub — das verträgt sich nicht.
  Für Spuren genügt ein kleiner Teil des öffentlich beschriebenen Protokolls; Tests erzeugen FIT-Dateien selbst.
- **Konstanten:** 120 s Spurlücke (Garmin „Smart Recording“ speichert alle 1–10 s, zu Fuß ≤ ~170 m Fehler), 15 min für
  „wahrscheinlich“ (Auto-Pause, Tunnel), 150 m „Pause am selben Ort“ (Fotostopp mit Auto-Pause), 30 min Fotospanne für
  „sicher“, 15 min für ein einzelnes Nachbarfoto, Toleranz 2 min vor/nach einer Aktivität (Uhr startet nach dem ersten
  Foto bzw. stoppt davor). Alles in `GeoSuggestionEngine`/`GeoJumpDetector`/`GeoAccuracyRules` als `const` mit Tests.
- **Eigene Herkunft 4/5** statt `Manual`: Anzeige („aus GPS-Spur“), spätere Auswertungen und Rückgängig bleiben
  unterscheidbar; die bestehenden Regeln „manuell bleibt“ gelten für 3–5.

## Konsequenzen

### Positiv
- Geschätzte Orte werden sichtbar und mit einem Klick (oder je Ausflug) korrigierbar; Fotos ohne Ort bekommen Orte aus
  der Uhr. Alles offline, ohne Konto, ohne Schlüssel; Rückgängig wie bei „Ort setzen“.
- Die Logik ist rein und getestet (Regeln, Sprung, Zeitabgleich, Stufen, Kodierung, Leser).

### Negativ
- Zeitzone „aus dem Ort“ (z. B. Urlaubsfotos einer Kamera ohne Zonenangabe in einer anderen Zeitzone) fehlt — es gilt die
  eingestellte bzw. die Windows-Zeitzone je Datum. Kamera-Abweichungen für Kameras ohne GPS nur von Hand.
- MTP-Uhren werden nicht direkt gelesen; Google-Standortverlauf (Timeline) wird nicht importiert.
- Von Lumora ins EXIF geschriebene Orte enthalten nur Breite/Länge — in einem anderen Katalog gelesen, gälten sie nach
  der Tag-Regel als „geschätzt“ (im eigenen Katalog nie: Herkunft 4/5).
- Vorschläge werden beim Öffnen der Seite gerechnet (bei sehr vielen Kandidaten einige Sekunden im Hintergrund).

### Neutral
- `GpsTracks` liegt im Katalog der Sitzung; eine versteckte Bibliothek hat ihre eigenen Spuren im Container.
- Der USB-Abgleich merkt sich den Zeitpunkt des letzten Einlesens je Katalog; Doppelte verhindert ohnehin die Prüfsumme.

## Alternativen

- **Garmin Connect API / inoffizielle Bibliotheken (Login mit Passwort)** → verworfen: für Privatpersonen nicht verfügbar
  bzw. Passwort in Lumora und Bruch der Nutzungsbedingungen.
- **Punkte als Tabellenzeilen** → verworfen (Größe, Index, Einlesezeit).
- **`Garmin.FIT.Sdk`** → verworfen (Lizenz, siehe Begründung).
- **MTP über WPD-COM** → vertagt: eigene Geräte-Schicht, schwer testbar; der Export deckt alles ab.
- **Vorschläge automatisch übernehmen** → verworfen: der Benutzer entscheidet; von Hand gesetzte Orte nie überschreiben.

## Referenzen

- Code: `src/Lumora.Services/Places/{Accuracy,Tracks,Suggestions}/*`, `Places/GeoLocationService.Assign.cs`,
  `Places/GeoMath.cs`, `Metadata/ExifGpsDetailsReader.cs`, `Wpf/Places/{Suggestions,TrackSettings}/*`,
  `Wpf/Places/ViewModels/PlacesViewModel.Suggestions.cs`, `Wpf/Places/Map/MapControl.Overlay.cs`,
  `Wpf/Lightbox/LightboxViewModel.GeoAccuracy.cs`, `Wpf/Shell/StatusBarViewModel.GeoTracks.cs`
- Tests: `tests/Lumora.Tests/Places/Tracks/*`, `tests/Lumora.Wpf.Tests/Places/{PlacesSuggestionViewModelTests,PlacesSettingsAndUiTests}.cs`,
  Rauchtest-Schritt „Ungenaue Orte“
- Mockup: `mockups/10-ungenaue-orte.html`; Garmin-Datenexport: https://www.garmin.com/account/datamanagement/exportdata/

## Nachtrag 0.5.33 — Quellen vergleichen (Mockup 25, Herbert 2026-10-27)

Herbert konnte auf der Karte nicht erkennen, wo das Foto laut eigener Position, laut Spur und laut Zeit lag. Bisher
rechnete die Engine je Foto nur den besten Vorschlag.

- **Engine:** `GeoSuggestionEngine.Compare` liefert je Foto alle Quellen (`GeoSourceComparison`): die Spur zur
  Aufnahmezeit auch über Lücken von mehr als 15 min (dann Stufe „keine“, nur geradlinig geschätzt), die Zeit aus den
  genauen Fotos und — ohne Spur zur Zeit — die nächste Spur innerhalb von 2 h als Uhr-Hinweis (`TrackClockHint`,
  Abweichung der Kamerauhr). Der Vorschlag selbst (`Suggest`) bleibt unverändert. Liegen Spur und Zeit mehr als 300 m
  auseinander, gelten sie als uneinig (Filter „Quellen uneinig“).
- **Seitenleiste:** Unter der ausgewählten Zeile stehen die drei Quellen mit Abstand zum Punkt laut Foto. „Nehmen“
  macht eine Quelle zum Vorschlag (Stufe und Herkunft beim Übernehmen folgen ihr), „Behalten“ = Ablehnen, Tasten 1–3.
  Darunter ein Satz zur Bewertung (stimmen überein, Lücke, uneinig, nur eine Quelle, Uhr-Hinweis).
- **Karte:** Standard „Nur dieses Foto“: beschriftete Punkte je Quelle, gestrichelte Abstände, GPS-Fotos davor und
  danach mit Uhrzeit, Vorschlag als Ring, Maßstab; Ausschnitt bis Zoom 18, damit auch 30 m sichtbar auseinanderliegen.
  „Ganzer Ausflug“ zeigt wie bisher alle Korrekturen des Tages.

