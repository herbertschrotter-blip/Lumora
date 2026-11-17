# ADR-0078: Serien und Zusammenfügen — aus mehreren Fotos eines, Serien still erkannt, Ergebnis als neue verknüpfte Datei

- **Status:** Proposed — Abnahme durch Herbert (mit Mockup 28)
- **Datum:** 2026-11-17
- **Entscheider:** Herbert Schrotter (Wünsche, Namen „Serien“ und „Zusammenfügen …“, Projekt `Lumora.Imaging`, Reihenfolge der Verfahren;
  Zeit und Ort von der ersten Quelle mit Angabe samt GPS-Höhe, Punkt 10; Gespräch 2026-11-17), Claude (Konzept: zwei unabhängige Entwürfe bewertet und zu diesem zusammengeführt, Detailentscheidungen
  ohne Rückfrage, begründet unten)
- **Bezogen auf:** [ADR-0071](0071-foto-verbessern.md) (Fassungen, `CreateVariantsAsync`, Hochskalieren),
  [ADR-0074](0074-hintergrund-planer.md) (Datei-Spur, `IForegroundWork`, `ForegroundHold`, `BackgroundCpu`; Nachtrag 0.5.40
  „Vorschaubilder auf Vorrat“ mit `BackgroundJob.ThumbnailStock` und `IBackgroundPlanner.HasWaitingAhead` — beim Schreiben
  dieses ADR parallel entstanden, seit 0.5.40 auf main),
  [ADR-0077](0077-onedrive-bibliothek.md) (OneDrive-Bibliothek: `CloudFiles`, Shell-Vorschaubilder, vorläufiges Datum; 0.5.41),
  [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (Alben, `FileLinkUndo`), [ADR-0067](0067-aktivitaets-anzeige.md)
  (Aktivitäts-Anzeige), [ADR-0068](0068-galerie-ordnerleiste-titelbild-kontextmenue.md) (gemeinsames Foto-Menü),
  [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Sitzung), [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md)/
  [ADR-0036](0036-gesichter-lernen-und-gruppieren.md) (Gesichter), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md)
  (Bibliotheks-Trennung), [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md) (Galerie-Filter),
  [ADR-0057](0057-foto-guete-und-highlights.md) (Foto-Güte, Wort „Serie“ in den Highlights),
  [ADR-0062](0062-ungenaue-orte-und-gps-spuren.md) (Ortsgenauigkeit), [ADR-0073](0073-app-verbindung.md) (App-Verbindung,
  Gipfelbuch), [ADR-0070](0070-modelle-und-daten.md)
  (Modelle & Daten), [ADR-0012](0012-operation-logging.md) (Protokoll), [ADR-0069](0069-katalog-sicherung-und-portabler-modus.md)
  (Sicherung vor Migrationen), [Spike 19](../spikes/19-onedrive-platzhalter.md) (OneDrive-Platzhalter), Spike 20 (Panorama, ausstehend)
- **Mockup:** `mockups/28-serien-zusammenfuegen.html` (Entwurf, Abnahme durch Herbert ausstehend; 17 Ansichten, siehe Punkt 21)

## Kontext

Herbert hat viele Aufnahmen, aus denen **ein** Bild werden soll: 110 Panorama-Ordner einer DJI-Drohne (48 × 26 Bilder ≈ 360°,
43 × 21 ≈ 180°, 6 × 9 Weitwinkel 3 × 3, 3 × 3 vertikal, 10 mit anderer Bildzahl), Handy-Schwenks, Belichtungsreihen,
Serienbilder. Bisher setzt er Panoramen in Photoshop bzw. Lightroom zusammen (Drohne bis 143 MP, zugeschnitten auf ≈ 5 : 1
statt 2 : 1; Handy bis 111 MP). Lumora kann bisher nur **ein** Foto verbessern (ADR-0071, Beziehung 1 : 1).

Vorgaben: Einzelbilder bleiben Byte für Byte gleich, das Ergebnis ist eine neue Datei mit Rückgängig; Vorschau vor dem Speichern;
Vorrang, solange Herbert wartet; die versteckte Bibliothek funktioniert gleich und hinterlässt nichts außerhalb des Containers;
OneDrive-Platzhalter werden nie still heruntergeladen (Spike 19); Bibliotheken mit 50 000–500 000 Dateien; keine WPF-Referenz in
Domain/Data/Services; das Repo ist öffentlich (keine Dateinamen, Pfade oder Inhalte von Herberts Fotos, nur Zahlen).

**Probe-Lauf zu Spike 20** (Zahlen aus `werkzeuge\runs\panorama-spike`, außerhalb des Repos):

- Die DNG-Dateien der Drohne tragen die Gimbal-Winkel im XMP (`drone-dji:GimbalYaw/Pitch/Roll`, `FlightYaw`): Gier in
  ≈ 45°-Schritten, Neigung +13°/−17°/−47°/−75°. 26 Bilder = 8 Richtungen × 3 Reihen + 2 steil nach unten, aufgenommen in 51 s.
- XMP-Datum und XMP-Kameramodell weichen vom EXIF ab → Zeit und Kamera gelten nur aus EXIF.
- Der eingebaute DNG-Decoder von Windows liefert nur die eingebettete Vorschau 960 × 640. Die Raw-Bilderweiterung (WIC
  „Microsoft Raw Image Decoder“) liefert die volle Auflösung 5 472 × 3 648 in 16 bit in 2,3 s bei 343 MB Spitze.
- In Handy-Serien wechselt die EXIF-Ausrichtung innerhalb einer Serie (1/3/6).
- OpenCvSharp 4.13 bindet vom OpenCV-Detail-Modul nur die Merkmale (`computeImageFeatures`) und `BestOf2NearestMatcher` an.
  Der Spike baut deshalb Startwerte, Bündelausgleich, Wellenkorrektur, Projektion, Belichtungsausgleich, Graph-Cut-Naht und
  Mehrband-Überblendung nach dem Muster `stitching_detailed` in C# nach.
- **Spike 20 ausstehend:** Der Bericht (`werkzeuge\runs\panorama-spike\bericht.md`: Bildvergleich gegen Herberts
  Photoshop-Ergebnisse, Laufzeit, Speicherspitze bei voller Auflösung) liegt noch nicht vor. Die Werkzeugwahl bleibt offen (Punkt 3).

**Zwei Entwürfe** wurden unabhängig erstellt und hier zusammengeführt:

| | A „Vorschlag zuerst“ | B „Werkzeug zuerst“ |
|---|---|---|
| Bedienung | ein Fenster (Verfahren links, Vorschau Mitte, Optionen rechts); Serien prominent: Marke, Chip, Serien-Ansicht mit Aufklappen, Serien-Leiste | Assistent mit zwei Seiten („Was soll entstehen?“ → Vorschau); Serien nur als leichte Marke und Filter |
| Vollständigkeit | alle Wünsche, ausführliche Erkennung, Korrigieren von Serien, abgeleiteter Zustand „zusammengefügt“ | alle Verfahren; Serien knapper; gute Einzelideen (Ordnergrenze, Teil-Serie, Stapel je Ergebnis ein Batch, „Mehr Details“) |
| Aufwand | höher (Aufklappen in der Galerie, Probe-Vorschauen) | geringer in der Galerie, eine Seite mehr im Fenster |
| Risiko | Aufklappen in der virtualisierten Galerie (Automation-Blätter, Seiten) | Annahme „Gimbal-XMP fehlt“ durch den Probe-Lauf widerlegt; gespeicherter Zustand „zusammengefügt“ muss bei Rückgängig zurückgesetzt werden |

Rückgrat ist **Entwurf A** (vollständiger gegen die Wunschliste, Herbert will den Ein-Klick-Weg aus einer Serie, Fakten passen
zum Probe-Lauf). Aus Entwurf B kommen: Serien nie über einen Ordner hinaus mit Ordner-Signatur, Marke nur auf dem ersten Foto,
„eine Kachel je Serie“ statt Aufklappen, Platzhalter zählen mit Katalogdaten, Hinweise zu Teil-Serie und mehreren Serien,
Zeit-Nachbarn aus der Lightbox, Stapel mit einem Batch je Ergebnis, „Mehr Details aus der Serie“, zweiphasige
Panorama-Schnittstelle, Aktionsleisten-Knopf, die Fertig-Ansicht, Grenzen je Verfahren und mehrere Risiken.

## Entscheidung

### 1. Namen

- **„Serien“** heißt, was Lumora erkennt: Galerie-Filter „Serien“, Kachel-Marke „Serie · n Fotos“, Serien-Leiste.
- **„Zusammenfügen …“** heißt die Aktion: Rechtsklick-Eintrag, Aktionsleiste, Fenster „Zusammenfügen“. In Menü,
  Aktionsleiste und Knöpfen steht sie wie alle Einträge des Foto-Menüs **ohne Leerzeichen vor den drei Punkten**
  („Zusammenfügen…“, „5 Fotos zusammenfügen…“ neben „5 Fotos verbessern…“). Das gilt für alle Oberflächentexte dieses ADR
  und von Mockup 28 („Neu berechnen…“, „Danach verbessern…“); bei der Abnahme zu bestätigen (Offene Punkte).
- Das Ergebnis heißt nach seinem Verfahren („Panorama“, „Belichtungsreihe“ …) und trägt die Kennzeichnung
  „aus n Fotos zusammengefügt“.
- Code: Projekt `Lumora.Imaging`, Namespaces `Lumora.Imaging.Series` (Erkennung) und `Lumora.Imaging.Merge` (Verfahren),
  Registrierung `AddImaging()` in `ImagingServiceCollectionExtensions.cs`.
- Die Highlight-Auswahl (ADR-0057) nutzt „Serie“ bisher für ähnliche Fotos ≤ 60 s („beste aus einer Serie von n“). Das bleibt
  intern, bis die Highlights `PhotoSeries` nutzen (Ausblick, Punkt 20); danach gilt nur noch die neue Bedeutung.

### 2. Projekt und Schichten

- **`src/Lumora.Imaging`** (net10.0, ohne `UseWPF`) referenziert `Lumora.Services` (damit Data und Domain) und steht wie
  `Lumora.AppConnect` neben den Services: Domain ← Data ← Services ← Imaging ← Wpf. Services und seine Tests
  (`tests/Lumora.Tests`) bleiben frei von nativem OpenCV; die Imaging-Tests bekommen ein eigenes Testprojekt (Punkt 18).
- **Pakete** `OpenCvSharp4` + `OpenCvSharp4.runtime.win` in `Directory.Packages.props` (Apache-2.0; native
  `OpenCvSharpExtern.dll` ≈ 69 MB, nur x64/x86, kein ARM64). Version aus Spike 20 (der Spike nutzt 4.13.0.20261107).
- **Registrierung:** `services.AddImaging()` ruft `AddSeries()` und `AddMerge()`; eine Zeile in `App.xaml.cs`, weil Services
  Imaging nicht kennt. Die Oberfläche registriert sich über `AddMergeUi()` in `Wpf/Merge/MergeUiServiceCollectionExtensions.cs`.
- **Kein OpenCV-Typ in Schnittstellen:** `PixelBuffer` (Breite, Höhe, Kanäle, 8/16 bit, Stride, gepoolter Speicher).
- **Lesen und Schreiben über WIC** (lebt im Wpf-Projekt): Verträge `IImagePixelReader` und `IMergeResultWriter` in Imaging,
  Umsetzungen `WicImagePixelReader` und `WicMergeResultWriter` in `Wpf/Merge/`. Der Leser liest JPEG, HEIC, PNG, TIFF und
  DNG/RAW, wendet die EXIF-Ausrichtung an, verkleinert schon beim Dekodieren und meldet „RAW nur als eingebettete Vorschau“.
- **`OpenCvRuntime`:** prüft beim Start, ob die native DLL lädt (sonst ist das Merkmal aus, mit Hinweis — der Rest der App
  läuft weiter); schaltet OpenCL ab (`Cv2.SetUseOpenCL(false)`, `OPENCV_OPENCL_CACHE_ENABLE=0`, sonst entsteht ein
  Kernel-Cache außerhalb des Containers); vergibt die Threads zentral (Vordergrund alle Kerne, Hintergrund
  `BackgroundCpu.MaxThreads`), weil `Cv2.SetNumThreads` global wirkt; lässt höchstens einen schweren OpenCV-Lauf zur Zeit zu.
  `OpenCVException` wird je Lauf gefangen und als Fehler des Vorgangs gemeldet.
- CLAUDE.md (Schichten-Zeile und Skill-Profil, Check `imaging`, Punkt 18) und PLAN Kap. 3 werden mit Stufe 1 nachgezogen.

### 3. Panorama-Werkzeug: Schnittstelle jetzt, Umsetzung nach Spike 20

- **`IPanoramaStitcher`** in zwei Phasen:
  - `EstimateAsync(sources, PanoramaSettings) → PanoramaAlignment`: Merkmale, Paarabgleich, Startwerte (aus den Gimbal-Winkeln,
    wenn vorhanden), Bündelausgleich, Wellenkorrektur (Horizont). Das Ergebnis (Kameraparameter) steht im Rezept.
  - `ComposeAsync(alignment, ComposeSettings{Projection, Scale, Crop, ExposureComp, Seams, Bands}) → PixelBuffer`:
    Projektion, Belichtungsausgleich, Nahtsuche, Mehrband-Überblendung.
  - Vorschau, Umschalten der Aufnahmeart und volle Auflösung rufen nur `ComposeAsync` neu auf; ausgerichtet wird einmal.
- **Kandidaten** (gewählt wird im Nachtrag nach dem Bericht):
  1. **Eigene Kette auf OpenCV-Bausteinen in C#** (Merkmale/Abgleich aus OpenCV, Rest wie im Spike) — Vorzug, wenn Spike 20
     die Qualität gegen Photoshop bestätigt: volle Kontrolle über Aufnahmeart, absolute Lage für 2 : 1 und Gimbal-Startwerte.
  2. OpenCV-`Stitcher` der Hochstufe (Modus Panorama = Kugel, Scans = affin) — nur Rückfall: keine Wahl der Projektion,
     keine Kameraparameter.
  3. **Hugin** (cpfind, autooptimiser, nona, enblend) als externes, vom Benutzer installiertes Werkzeug über einen
     Prozess-Aufruf — nur, wenn die eigene Kette bei Kugel/Parallaxe deutlich schlechter ist (Punkt „Alternativen“).
  4. Eigener nativer Baustein über das OpenCV-Detail-Modul (C++-Build-Kette) — nur, wenn die eigene Kette zu langsam ist.
- Bis zum Bericht baut keine Stufe etwas, das nur zu einem Kandidaten passt.

### 4. Serien-Erkennung (`Lumora.Imaging.Series`)

- **Still im Hintergrund-Planer (ADR-0074):** neue Aufgabe `BackgroundJob.Series` („Serien“) in der Datei-Spur mit
  **Vorrang 3**, also nach Datum/Orte (0), Doppelten (1) und Foto-Güte (2). Dahinter rücken `Checksums` auf **4** und
  `ThumbnailStock` („Vorschaubilder auf Vorrat“, Nachtrag 0.5.40 zu ADR-0074, dort noch Vorrang 4) auf **5**. Serien lesen nur
  Dateiköpfe der Kandidaten (KB je Datei); Prüfsummen und Vorrat lesen ganze Dateien über Stunden. Pause, „Alle pausieren“,
  Einlesen, Katalog-Umzug und Vordergrund-Vorrang gelten wie für jede Aufgabe; nach jedem Ordner fragt die Erkennung
  `IBackgroundPlanner.HasWaitingAhead(BackgroundJob.Series)` und gibt die Spur ab, wenn Datum/Orte, Doppelte oder Foto-Güte
  warten. Die Bildprüfung (Stufe C) holt nur die Erlaubnis (`CanRun`) und rechnet einzeln mit niedriger Thread-Priorität.
- **„Niedriger Vorrang“ (Herberts Wunsch):** Gegenüber allem, worauf Herbert wartet, hat die Erkennung den niedrigsten Rang —
  sie pausiert bei jedem `IForegroundWork`, läuft mit niedriger Thread-Priorität und höchstens `BackgroundCpu.MaxThreads`.
  Innerhalb der Hintergrund-Aufgaben steht sie bewusst vor Prüfsummen und Vorrat (Begründung unten); das weicht vom Wortlaut
  ab und ist bei der Abnahme zu bestätigen (Offene Punkte). Rückfall bei „nein“: Vorrang 5 hinter den Prüfsummen, dann sieht
  Herbert Serien in einer großen Bibliothek erst nach deren Erstlauf.
- **Eine Serie bleibt in einem Ordner.** Arbeitseinheit ist ein Ordner mit geänderter Signatur (`SeriesFolderScans`: Anzahl,
  größte FileId, jüngste Änderungszeit, Erkenner-Version), höchstens 500 Dateien je Stapel. Der erste Lauf geht einmal über alle
  Ordner, danach nur über geänderte (`FilesChangedMessage`). Was über Ordner hinweg gehört, geht über die freie Auswahl.
- **Stufe A — Katalog** (auch für Platzhalter): Videos, Archiv-Einträge, Ergebnisse (`MergedPhotos`) und Fassungen
  (`PhotoVariants`) bleiben draußen. Nach Aufnahmezeit sortiert, gleiche Kamera (EXIF-Hersteller/-Modell, nie XMP):
  - **enger Lauf:** Lücke ≤ max(3 s; 2,5 × Median-Lücke des Laufs) und ≤ 15 s, höchstens 10 min, mindestens 3 Fotos;
  - **Intervall:** mindestens 20 Fotos mit gleichmäßigem Abstand (1 s–10 min, Streuung ≤ 10 %);
  - **DJI-Panorama-Ordner** (Name `^\d{3}_\d{4}$`, Hersteller DJI) sind immer Kandidat.
- **Stufe B — Dateikopf**, nur Kandidaten, nur lokale Dateien: `IFileMetadataProbe` liefert aus demselben Öffnen zusätzlich
  `CaptureDetails` (Belichtungszeit, Blende, ISO, Belichtungskorrektur, `ExposureMode`, Brennweite KB, Fokusabstand, SubSec,
  Gimbal-Gier/-Neigung/-Rollen, Flug-Gier). Sie landen in `FileCaptureDetails` (gültig für Größe/Änderungszeit). Neue Dateien
  bekommen die Angaben beim ohnehin laufenden Datums-/Orte-Durchgang aus dem Gedächtnis des Probes.
- **Stufe C — Bildprüfung**, nur für unsichere Handserien ohne Gimbal: ORB-Abgleich benachbarter Vorschaubilder (400 px aus dem
  Cache `{fileId}_400_still.jpg`, nie die Originale) → Überlappung und Bewegung (Schwenk, Stand, Zoom). Genau diese
  Vorschaubilder legt der Vorrat (Nachtrag 0.5.40) nach und nach an. Fehlt eines, wartet die Erkennung nicht auf den Vorrat (er
  steht dahinter), sondern erzeugt es für diese wenigen Kandidaten selbst über `IThumbnailService.CreateStockAsync` — gleicher
  Renderer, gleiche Cache-Datei, gleicher DB-Status, nur lokale Dateien, nie ein Download. Platzhalter bleiben ohne Stufe C
  (Punkt 14).
- **Arten und Regeln** (`SeriesClassifier`, rein; Gründe als Codes, Texte erst in der Oberfläche):

  | Art | Erkannt an | Vorschlag |
  |---|---|---|
  | Panorama | DJI-Ordner + Gimbal (Sicherheit 0,95); DJI-Ordner ohne Gimbal nach Bildzahl (0,8); Handserie mit Überlappung 15–70 % bei vorwiegend seitlicher oder senkrechter Bewegung (0,6–0,8) | Panorama, Aufnahmeart nach Punkt 6 |
  | Belichtungsreihe | `ExposureMode` = 2 (automatische Reihe) oder Stufen ≥ 0,7 EV (EV aus Blende, Zeit, ISO bzw. Belichtungskorrektur) bei gleicher Brennweite, Lücke ≤ 2 s | Belichtungsreihe (HDR) |
  | Serienbild | ≥ 3 Fotos, Lücke ≤ 2 s (SubSec), gleiche Einstellungen | Entrauschen bei ISO ≥ 800; sonst Leute entfernen ab 5 Fotos über ≥ 10 s; daneben Langzeit; ab ≥ 2 Gesichtern je Foto Bestes Gruppenfoto |
  | Fokusreihe | wandernder Fokusabstand bei gleicher Belichtung (nur wo EXIF bzw. Hersteller ihn liefern) | Fokus-Stapel; ohne Angabe nur über freie Auswahl |
  | Intervall | Regel „Intervall“ aus Stufe A | Zeitraffer |
  | Panorama-Belichtungsreihe | wiederkehrende Gimbal-Stellungen mit Zyklus aus 3 oder 5 Belichtungen | HDR-Panorama |

- **Sicherheit:** unter 0,5 wird nichts gespeichert; 0,5–0,7 gespeichert, aber nur im Fenster als Vorschlag gezeigt; ab 0,7
  Marke, Filter und Ein-Klick-Knopf.
- **Abgleich** (`SeriesReconciler`): ≥ 50 % gemeinsame Fotos (bezogen auf die kleinere Serie) = dieselbe Serie; der
  Benutzerzustand bleibt (gesehen, verworfen, hinzugefügte und ausgenommene Fotos).
- **Zustand:** Neu → Gesehen (beim ersten Öffnen) → Verworfen („Keine Serie“, auf Wunsch wieder herstellbar).
  **„Zusammengefügt“ wird nicht gespeichert, sondern abgeleitet** (ein Ergebnis mit dieser `SeriesId` oder alle Quellen in
  `MergedPhotoSources`). Rückgängig muss dadurch keinen Zustand zurücksetzen.
- **Vorhandene Ergebnisse erkennen** (Stufe 2): Eine breite Datei derselben Kamera (Seitenverhältnis ≥ 2 : 1 oder ≥ 1,5 × die
  Pixel eines Einzelbilds) mit Aufnahmezeit innerhalb der Serie, auch in einem anderen Ordner derselben Bibliothek, gilt als
  „schon zusammengefügt (eigene Datei)“. Sie wird als Ergebnis verknüpft (`MergedPhotos.Tools = "extern"`), die Serie wird nicht
  mehr angeboten. So zählen Herberts bisherige Photoshop/Lightroom-Panoramen mit.
- **Aktivitäts-Anzeige:** `ActivityKind.Series = 19` „Serien erkennen · 12 400 / 48 000“, nur solange es läuft; unter
  „Zuletzt fertig“ nur bei Fehlern. Keine Meldung, kein Popup — nur der Zähler „n neu“ am Chip.
- Dienste: `SeriesDetector` (rein, Stufe A), `SeriesClassifier`, `DjiPanoramaFolderRule`, `PanoramaLayoutGuesser`,
  `ThumbnailOverlapProbe`, `SeriesReconciler`, `SeriesIndexService` (Hintergrund), `ISeriesService` (`GetMarksAsync`,
  `ListAsync`, `GetAsync`, `CountNewAsync`, `MarkSeenAsync`, `DismissAsync`, `RestoreAsync`, `ExcludeItemsAsync`,
  `AddItemsAsync`), `SeriesSettingsStore` (`series.*`). Derselbe Klassifizierer bewertet im Fenster die freie Auswahl.

### 5. Die Verfahren (Mockup 28, Ansichten 5–10)

Je Verfahren eine `IMergeEngine` (`Method`, `Check(MergeInput) → Eignung + Grund`, `PreviewAsync`, `RenderAsync(recipe)`):

| Verfahren | Eingabe | Ergebnis | Optionen | Technik | Stufe |
|---|---|---|---|---|---|
| **Panorama** | 2–200 überlappende Fotos (15–70 %), möglichst eine Kamera | `_panorama.jpg` (TIFF 16 bit wahlweise bei RAW) | Aufnahmeart (Punkt 6), Zuschnitt, Belichtung ausgleichen, Quelle JPEG/RAW, Größe | Kette aus Punkt 3 | 1, 3 |
| **Belichtungsreihe (HDR)** | 2–9 Fotos einer Szene, gestufte Belichtung | `_hdr.jpg` | Stil Natürlich (Exposure Fusion) / Kräftig (HDR + Tonung), Ausrichten (aus der Hand / Stativ), Geister entfernen + Bezugsbild, Vorher/Nachher gegen die mittlere Belichtung | `MergeMertens` bzw. `CalibrateDebevec` + `MergeDebevec` + Reinhard/Mantiuk; Ausrichten über Merkmale + ECC | 4 |
| **Entrauschen / mehr Details** | 4–64 Fotos einer Szene, gleiche Belichtung | `_entrauscht.jpg` | Stärke (Ausreißer verwerfen), Bezugsbild (Standard: schärfstes), „Mehr Details aus der Serie“ 1,5× / 2× (ab 8 Fotos aus der Hand) | ausrichten, Sigma-Clipping-Mittel in Kacheln; Mehr Details = Shift-and-add auf feinerem Gitter | 4 |
| **Leute entfernen** | ab 5 (besser 10) Fotos über ≥ 10 s, gleiche Belichtung | `_ohne-leute.jpg` | Bezugsbild, Maske „veränderte Stellen“, Hinweis „jede Stelle muss meist frei sein“; später Pinsel „aus Foto n übernehmen“ | Median je Pixel über ausgerichtete Bilder, kachelweise | 4 |
| **Langzeitbelichtung** | ab 5 Fotos | `_langzeit.jpg` | Weich (Mittel: Wasser, Wolken) / Lichtspuren (Hellste), Stativ (nicht ausrichten) | Mittel bzw. Maximum | 4 |
| **Fokus-Stapel** | 3–64 Fotos mit wanderndem Fokus, gleiche Belichtung | `_fokus.jpg` | Ausrichten mit Maßstab, Glättung, Rand beschneiden, Überlagerung „Schärfe-Herkunft“, Hinweis auf Halos | Laplace-Energie → Auswahlkarte → Pyramiden-Überblendung; ECC mit Maßstab gegen das „Atmen“ des Fokus | 5 |
| **Zeitraffer** | 20–10 000 Fotos in gleichmäßigem Abstand | `_zeitraffer.mp4` (Video-Katalogzeile) | Bilder je s 24/25/30 (zeigt die Länge), 1080p / 4K, Ausschnitt 16 : 9, Ausrichten, Flimmern ausgleichen | Rohbilder per stdin an FFmpeg (keine Einzelbild-Dateien), libx264 CRF 18, sonst vorhandener Encoder; Suche wie `FfmpegVideoThumbnailer` | 5 |
| **HDR-Panorama** | Panorama-Positionen × 3 oder 5 Belichtungen | `_hdr-panorama.jpg` | Gruppen („26 Positionen × 3 Belichtungen“), HDR-Optionen, Panorama-Optionen | je Position fusionieren, dann Panorama | 5 |
| **Bestes Gruppenfoto** | 2–20 Fotos einer Gruppe, ≥ 2 Gesichter je Foto | `_gruppenfoto.jpg` | Grundbild (Standard: beste Foto-Güte, ADR-0057), je Person ein Gesicht aus der Serie | siehe unten | 6 |

- **Gemeinsame Bausteine:** `FrameAligner` (Merkmals-Homographie auf verkleinerten Bildern, Feinschliff `FindTransformECC`,
  auf volle Größe skaliert), `MergeMemoryBudget` (freier Arbeitsspeicher über `GlobalMemoryStatusEx` → höchste machbare
  Größe, Kachelgröße), `MergeRecipe` (JSON: Verfahren, Optionen, Kameraparameter, Zuschnitt, Verstärkungen — für volle
  Auflösung, „Neu berechnen“ und Stapel), `MergeLimits`, `MergeNaming`, `MergeSettingsStore` (`merge.*`).
- **Grenzen:** Stapel-Verfahren höchstens 64 Bilder und nur gleiche Bildgröße (sonst ausgegraut: „verschiedene Bildgrößen“),
  Panorama höchstens 200, Zeitraffer gestreamt bis 10 000. Videos, Archiv-Einträge und fehlende Dateien werden mit Zahl
  ausgelassen („2 Videos werden nicht verwendet“).
- **Bestes Gruppenfoto** nutzt die vorhandene Gesichtserkennung (ADR-0035/0036): Gesichter aus `Faces` (Box, Landmarken,
  Fingerabdruck); fehlende erkennt Lumora jetzt mit Vorrang (Grafikkarte). Zuordnung über die Fotos per Fingerabdruck
  (Kosinus ≥ 0,5) und Position; benannte Personen tragen ihren Namen. Vorgeschlagen wird je Person das beste Gesicht nach
  Schärfe, Gesichtsgüte und Frontalität (wie `FaceCoverScoring`); „Augen zu“ erst mit dem Landmarken-Modell 2d106det aus dem
  buffalo_l-Archiv (gleiche Quelle und Lizenz, bisher nicht entpackt) als eigener Eintrag in „Modelle & Daten“ nur nach
  Bestätigung — ohne es fehlt nur diese Marke. Getauscht wird nur das Gesicht derselben Person (lokal ausrichten +
  `SeamlessClone` mit erweiterter Ellipsen-Maske); ist die Zuordnung unsicher, wird nicht getauscht. Ohne geladene
  Gesichtserkennung ist das Verfahren ausgegraut („braucht Gesichtserkennung“). Die Gesichtserkennung liest das Ergebnis neu.

### 6. Panorama: Aufnahmearten wie in Photoshop (Mockup 28, Ansicht 5)

- **Wahl:** Automatisch (→ Vorschlag) | **Kugel 360°** (equirektangular 2 : 1, Lücken oben/unten gefüllt, GPano-XMP für
  360°-Betrachter) | **Zylinder** (bis ≈ 180° und mehr, Handy-Schwenk, DJI 21 Bilder) | **Perspektive** (wenige Bilder,
  Weitwinkel 3 × 3) | **Vertikal** (senkrechter Schwenk) | **Nur verschieben** (Scans, affin).
- **Vorschlag** (`PanoramaLayoutGuesser`, umstellbar):
  - mit Gimbal: Gier-Spanne ≥ 300° und Neigung bis ≤ −70° → Kugel 360°; Gier-Spanne 120–300° → Zylinder; Gier fest
    (< 15°) und Neigungs-Spanne ≥ 30° → Vertikal; sonst Perspektive;
  - DJI-Ordner ohne Gimbal nach Bildzahl: 26 → Kugel, 21 → Zylinder, 9 → Perspektive, 3 → Vertikal, sonst Automatisch ohne
    Ein-Klick-Vorschlag;
  - ohne Gimbal: Bildwinkel aus der Brennweite KB plus Schwenk aus der Bildprüfung (Stufe C); ohne Kamera im EXIF (Scans)
    → Nur verschieben.
- Umschalten rechnet nur die Projektion neu (Sekunden), die Ausrichtung bleibt.
- **Zuschnitt:** Gerade Kanten | Lücken füllen | Ungeschnitten. Kugel 360° steht auf „2 : 1, Lücken füllen“ (Himmel: weicher
  Verlauf aus den obersten Zeilen; Boden: Inpainting, mit dem LaMa-Modell aus „Foto verbessern“, wenn geladen), wahlweise
  „Gerade Kanten“ wie die bisherigen Photoshop-Ergebnisse (≈ 5 : 1).
- **Kette:** Merkmale auf ≈ 0,6 MP, Bündelausgleich (Strahl), Wellenkorrektur, Belichtungsausgleich (Blockverstärkung),
  Nahtsuche (Graph-Cut), Mehrband-Überblendung, volle Auflösung aus dem Rezept. EXIF-Ausrichtung wird vor dem Ausrichten
  angewandt.
- **RAW (DNG) als Quelle** über die Raw-Bilderweiterung in 16 bit (Vorschau aus den JPEG-Geschwistern bzw. kleiner dekodiert);
  fehlt die Erweiterung, Rückfall auf JPEG mit Hinweis. Wahlweise „Farben an JPEG angleichen“.
- **Größe:** Kugel 360° Standard 16K (16 384 × 8 192 ≈ 134 MP), sonst Vorschlag ≤ 150 MP; „Voll“ (DJI-Kugel rechnerisch
  ≈ 30 000 × 15 000 ≈ 450 MP, ein 16-bit-Puffer allein ≈ 2,7 GB) nur, wenn `MergeMemoryBudget` reicht — sonst ausgegraut mit
  Zahl; „Klein“ ≈ 40 MP. Keine Kante über 65 535 px (JPEG-Grenze).

### 7. Start-Wege (Mockup 28, Ansichten 2–4 und 16)

- **Rechtsklick** im gemeinsamen Foto-Menü. `PhotoContextMenuBuilder.Build` läuft heute an zwei Stellen: in der Galerie
  (`GalleryView.PhotoMenu.cs`, Kachel und Listenzeile, `PhotoMenuSource.Gallery`/`List`) und in der Lightbox
  (`LightboxWindow.PhotoMenu.cs`, `PhotoMenuSource.Lightbox`). Unter „Bearbeiten ▸“ direkt nach „Foto verbessern…“ bzw.
  „n Fotos verbessern…“:
  - ab 2 Fotos „n Fotos zusammenfügen…“ (Beispiel „5 Fotos zusammenfügen…“);
  - bei einem Foto einer Serie „Serie mit 26 Fotos zusammenfügen…“;
  - in Galerie, Liste und Lightbox bei einem Foto ohne Serie „Zusammenfügen…“ — das Fenster zeigt die Zeit-Nachbarn
    (gleicher Ordner, gleiche Kamera, ± 2 min) im Filmstreifen, noch nicht angehakt; Vorschau und Verfahren erst ab 2 Haken;
  - ausgegraut mit Tooltip bei Videos, Archiv-Einträgen, fehlenden Dateien, gemischten Bibliotheken oder nicht geladenem OpenCV.
  - Bei Mehrfachauswahl fallen wie bisher Öffnen, Personen, Titelbild, Suchen und Eigenschaften weg (ADR-0068).
  - `PhotoMenuHost` bekommt `Merge` (Action), `CurrentAlbumId` und `SeriesOf(FileId)`.
- **Aktionsleiste der Galerie:** „Zusammenfügen…“ ab 2 gewählten Fotos.
- **Aus einem Album:** Ein eigenes Album öffnet sich als gefilterte Galerie (Bedingung „Album …“), der Start läuft also über
  dieselben Wege; die Lightbox übernimmt `CurrentAlbumId` von der Galerie, aus der sie geöffnet wurde. Im Fuß des Fensters
  steht „auch ins Album „…““ (gesetzt); nach dem Speichern trägt `IAlbumService.AddFilesAsync(albumId, [Ergebnis])` das
  Ergebnis als Verknüpfung ein (ADR-0054). Bei intelligenten Alben gibt es keinen Haken (der Filter entscheidet).
- **Aus einer Story:** Die Story-Seite hat das gemeinsame Foto-Menü nicht (ADR-0068 nennt sie als Ort, gebaut ist dort nur
  „Als Titelbild für ▸“ über `FillCoversAsync`). Gestartet wird deshalb über die Lightbox, die sich aus der Story öffnet. Das
  Ergebnis kommt nicht von selbst in die Story (Storys wählen ihre Fotos selbst; von Hand über „Story ▸“). Das Menü in der
  Story-Seite einzubauen, gehört nicht zu diesem Vorhaben (Ausblick, Punkt 20).
- **Aus einer erkannten Serie mit einem Klick:** Knopf auf der Serien-Kachel bzw. in der Serien-Leiste → Fenster mit
  vorausgewähltem Verfahren und vorausgewählter Aufnahmeart, die Vorschau startet sofort.
- **Viele Serien auf einmal:** Stapel (Punkt 12).
- **Hinweise beim Start:** Teil einer Serie („Diese Fotos gehören zu einer Serie mit 26 Fotos — alle 26 nehmen?“), mehrere
  Serien („Die Auswahl enthält 2 Serien — getrennt zusammenfügen (2 Ergebnisse) oder als ein Bild?“).
- Kein eigenes Tastenkürzel in Stufe 1 (Strg+M ist „In die Bibliothek…“, `PhotoMenuGestures.IntoLibrary`).

### 8. Fenster „Zusammenfügen“ (Mockup 28, Ansichten 5–11)

`MergeWindow` + `MergeViewModel` (erbt von `AppFluentWindow`, nicht modal, eines je Sitzung; Dialoge nur über `IDialogService`):

- **Links die Verfahrensliste** (alle neun): je Verfahren „passt“ / „eher nicht“ mit Grund („Belichtung überall gleich“), noch
  nicht gebaute als „kommt später“. Der Vorschlag ist hervorgehoben, darüber der Kasten „Warum dieser Vorschlag“ mit den
  Belegen (DJI-Ordner, Gimbal 8 × 3 + 2, 51 s, gleiche Kamera, EV-Stufen, Überlappung).
- **Mitte die Bühne:** große Vorschau mit Zuschnitt-Rahmen und schraffierten Lücken, Zoom/Verschieben, Vorher/Nachher-Regler bei
  HDR und Stapel-Verfahren (wie `EnhanceStageOverlay`), Überlagerungen „Nähte“, „veränderte Stellen“, „Schärfe-Herkunft“,
  Fortschritt mit Schritt („Nähte suchen 4/6“).
- **Unten der Filmstreifen der Quellen:** Haken, Rolle („−2 EV“, „Reihe 2 · 45°“), Marken „nicht eingebunden“, „nur
  OneDrive“, „fehlt“.
- **Rechts die Optionen** des gewählten Verfahrens (Punkte 5 und 6), bei Panorama die Aufnahmearten als Kacheln mit Mini-Skizze.
- **Fuß:** Dateiname (`<erstes Bild>_panorama.jpg`), Ablage („neben dem ersten Einzelbild“), „auch ins Album …“, „gemeinsame
  Tags übernehmen“, Zeit- und Speicherschätzung, [Als neue Datei speichern] [Verwerfen].
- **Nach dem Speichern:** Fortschritt (Schrittliste, Restzeit, „Im Hintergrund weiterrechnen“ schließt das Fenster, Abbrechen),
  dann die **Fertig-Ansicht** (Öffnen, In der Galerie zeigen, Rückgängig, Danach verbessern…, Schließen).
- **Gestartet mit einem Foto** (Galerie, Liste oder Lightbox ohne Serie): Filmstreifen mit dem Foto (angehakt, Bezug) und den
  Zeit-Nachbarn (nicht angehakt, „Alle n anhaken“); die Verfahren stehen auf „eher nicht · nur 1 Foto angehakt“, die Bühne
  zeigt „Mindestens 2 Fotos anhaken“, Speichern ist gesperrt.
- **Analyse** in ≤ 2 s (`IMergeService.AnalyzeAsync`: Katalog, `FileCaptureDetails`, Bildprüfung auf Vorschaubildern) mit
  Warnungen über der Vorschau (verschiedene Kameras; Fotos aus verschiedenen Bibliotheken → gesperrt; Platzhalter, Punkt 14).
- **Eigene Ansichten** für Gruppenfoto (Grundbild mit nummerierten Gesichtsrahmen, darunter je Person eine Leiste mit dem
  Gesicht aus jedem Foto, Marken „Vorschlag“, „unscharf“, „abgewandt“, „Augen zu“; Klick tauscht) und Zeitraffer
  (Abspielfläche aus den Vorschaubildern mit Play/Pause und Zeitleiste, Zustand von FFmpeg).

### 9. Galerie (Mockup 28, Ansichten 1–3)

- **Kachel-Marke** „Serie · 26 Fotos“ mit Art-Symbol (Panorama, Belichtung, Stapel, Fokus, Intervall) nur auf dem **ersten**
  Foto einer Serie; die übrigen tragen einen kleinen Punkt mit Tooltip („Teil einer Serie mit 26 Fotos“).
  - Klick auf die Marke → Bedingung „Serie: Panorama · 26 Fotos“ in „Es gilt“ plus Serien-Leiste; × führt zurück.
  - Rechtsklick auf die Marke: Serie mit 26 Fotos zusammenfügen…, Fotos der Serie zeigen, Keine Serie.
  - Nach dem Zusammenfügen trägt die Marke ein Häkchen.
- **Ergebnis-Marke** „Panorama · aus 26 Fotos“: öffnet Ergebnis und Quellen in der Lightbox (Ergebnis zuerst).
- Marken werden je Seite gebündelt nachgetragen wie „2 Fassungen“; Kacheln bleiben UI-Automation-Blätter (Klick auf Marke
  und Knopf per Treffer-Test im `GalleryListBoxItem`, Aktionen zusätzlich im Kontextmenü und per Tastatur).
- **Chip „Serien · 12 neu“** in der Zeile „Inhalt“ neben „Doppelte“. Flyout:
  - Arten mit Zahlen als Haken-Liste;
  - Zustand Neu | Offen | Alle | Zusammengefügt | Verworfene;
  - „Eine Kachel je Serie“ (Standard an);
  - Stand der Erkennung („aktuell“ bzw. „läuft · 62 %“, „3 Ordner nur in OneDrive“);
  - „Alle 108 sichtbaren zusammenfügen…“ (Stapel; die Zahl folgt dem Filter).
  - In „Es gilt“: „Serien: Panoramen, offen ×“. Mit allen übrigen Filtern kombinierbar (ADR-0041); eine Serie zählt, wenn
    ihr erstes Foto den Filter erfüllt. Arten und Zustand lassen sich im intelligenten Album speichern, eine einzelne Serie nicht.
- **Eine Kachel je Serie:** Die Abfrage zeigt nur das Titelbild jeder Serie (erstes Foto, nach dem Zusammenfügen das Ergebnis)
  mit Stapel-Rand, „Panorama · Kugel 360°“, „26 Fotos · 51 s“, Marke „neu“ und beim Darüberfahren dem Ein-Klick-Knopf
  („Panorama 360° zusammenfügen“ + Pfeil für andere Verfahren). Kein Aufklappen in der Galerie: Klick oder Enter öffnet die
  Serie (Bedingung + Serien-Leiste). Tageskopf wie in der Zeitachse.
- **Serien-Leiste** über einer geöffneten Serie: Art-Symbol, Titel („Panorama · Kugel 360° · 26 Fotos“), Zeitspanne, Kamera,
  Gründe als Chips, Probe-Vorschau (Stufe 2: mittleres Foto; Stufe 3: Mini-Panorama bzw. Fusion aus den Vorschaubildern, Cache
  `thumbs\{libraryId}\series\{seriesId}_{signatur}.jpg` im Katalog-Datenordner), Knöpfe [Panorama 360° zusammenfügen ▾]
  [Alle auswählen] [Serie anpassen] [Keine Serie]. Der Pfeil bietet die übrigen passenden Verfahren mit „passt“/„eher nicht“.
  Beim Öffnen wird die Serie „gesehen“, der Zähler am Chip sinkt.
- **Serie korrigieren:** „Keine Serie“ (verworfen, bleibt über den Abgleich); „Serie anpassen“ blendet Haken ein (Foto
  herausnehmen = Origin 2); „Zur Serie hinzufügen ▸“ auf gewählten Fotos (Origin 1); Flyout „Verworfene“ mit „Wieder als Serie
  zeigen“; Einstellungen „Serien neu erkennen“ (Benutzerzustand bleibt).
- **Quellen einklappen:** Einstellung „Quellen zusammengefügter Serien in der Galerie einklappen“, Standard **an** (sonst füllen
  über 2 200 Drohnen-Einzelbilder die Galerie); Regel wie „beste Fassung zeigen“: nur, wenn das Ergebnis im Bereich bzw. im
  gefilterten Ordner liegt. Erreichbar bleiben die Quellen über die Ergebnis-Marke.
- **Suchen ▸** „Gleiche Serie“, bei einem Ergebnis „Quellen“, bei einer Quelle „Zusammengefügtes Ergebnis“ (über `ApplySearch`).
- **Abfragen:** `GalleryPageSource.Series.cs` hängt Marken-Spalten und Klauseln nur an, wenn der Bereich Serien- bzw.
  Merge-Zeilen hat (Plan-Test analog `Ordner_ohne_Verknuepfungen_behaelt_den_Abfrageplan`). `GallerySeriesFilter` in
  `GalleryFilterState` zählt in `ActiveCount`. `GalleryItem.Series.cs` (Marken), `GalleryViewModel.Series.cs` (Chip, Zähler,
  Serien-Leiste, Ein-Klick, Stapel).

### 10. Ergebnis als neue Datei, Kennzeichnung, Verknüpfung (Mockup 28, Ansichten 11 und 14)

- **Datei-Vorgang** `IFileOperationService.CreateMergedAsync(IReadOnlyList<MergeCreateRequest>)` in
  `Services/Operations/FileOperationService.Merge.cs`, `OperationType.CreateMerged = 16`. Gleicher Ablauf wie
  `CreateVariantsAsync` (ADR-0071): Zwischendatei an das Ziel verschieben, Files-Zeile, FTS, Ordnerkette, freier Name „ (2)“,
  Ziel nur unter den Wurzeln der Bibliothek der Quellen, Scan-Sperre, eigene Pfade, `ActivityScope`. Der Element-Schritt wird
  dafür in `ExecuteCreateDerivedItemAsync` herausgezogen (Rückruf für die Verknüpfung). `ActivityTrackingFileOperationService`
  reicht die Methode durch.
- **Rückgängig** = Ergebnis in den Papierkorb, Katalogzeile weg; `MergedPhotos`, `MergedPhotoSources`, Album- und
  Story-Verknüpfung gehen per Kaskade mit. Die Serie ist danach sofort wieder „offen“ (abgeleiteter Zustand).
- **Ablage:** neben dem **ersten Einzelbild** (früheste Aufnahme, Position 0), wahlweise Unterordner „zusammengefügt“; Option
  „Drohnen-Panorama-Ordner: eine Ebene höher ablegen“ (Standard aus). Nie überschreiben.
- **Alle Quellen aus einer Bibliothek** (ADR-0048); gemischte Auswahl ist gesperrt.
- **Dateinamen:** `<erstes Bild>_panorama.jpg`, `_hdr.jpg`, `_entrauscht.jpg`, `_ohne-leute.jpg`, `_langzeit.jpg`,
  `_fokus.jpg`, `_hdr-panorama.jpg`, `_gruppenfoto.jpg`, `_zeitraffer.mp4` (Zusätze einstellbar, Punkt 17).
- **Metadaten — Zeit, Ort und Höhe (Nachtrag nach Herberts Vorgabe, 2026-11-17):** Zeit und Ort des Ergebnisses müssen
  stimmen, damit Orte, Storys und die App-Verbindung es derselben Tour zuordnen wie die Einzelbilder (Gipfelbuch fragt nach
  Zeitfenster und Kartenausschnitt, `CatalogPhotoCatalog`, ADR-0073).
  - **Aufnahmezeit** (mit Quelle, Genauigkeit und Versatz) von der frühesten Quelle, **die eine Aufnahmezeit hat**; Quellen
    ohne Zeit zählen nicht. Hat keine Quelle eine, bleibt sie leer — nie das Erstelldatum der Ergebnisdatei.
  - **Ort** von der ersten Quelle, **die einen Ort hat**, nicht starr vom ersten Bild: Handys haben beim ersten Bild einer
    Serie oft noch keinen GPS-Fix. Echtes GPS (`GeoAccuracy.Exact`) geht vor geschätztem (ADR-0062), bei gleicher Güte die
    früheste Quelle. Maßgeblich sind die Katalogwerte, also zählen auch in Lumora gesetzte Orte. Lag eine Quelle bis eben
    nur in OneDrive, fehlt ihr im Katalog noch der Ort (ADR-0077: GPS erst, wenn die Datei lokal ist); dann liest das
    Zusammenfügen Ort und Zeit aus der gerade geladenen Datei selbst. Hat keine Quelle einen Ort, bleibt er leer (kein
    Raten aus Nachbarfotos); „Ort setzen…“ geht wie bei jedem Foto.
  - **Kamera** von der ersten Quelle.
  - **Katalog:** Die Files-Zeile des Ergebnisses bekommt Zeit (`CapturedAt`, `CapturedAtSource`, `CapturedAtPrecision`,
    `CapturedAtOffset`) und Ort (`Latitude`, `Longitude`, `GeoAccuracy`, `GeoAccuracyReason`) sofort beim Anlegen aus den so
    gewählten Quellen — wie `CreateVariantsAsync` sie vom Original übernimmt. Die App-Verbindung findet das Ergebnis damit
    ohne Nachlesen der Datei.
  - **Datei:** Bei JPEG-Quelle der EXIF-Block der ersten Quelle über `JpegExifCopier` (Ausrichtung 1); stammt der Ort von
    einer anderen Quelle, wird deren GPS-Block eingesetzt. Bei DNG-Quelle ein eigener EXIF-Block mit Datum, Kamera und dem
    **ganzen** GPS-Block der Ortsquelle: Breite/Länge, **Höhe** (`GPSAltitudeRef`/`GPSAltitude`, Tags 5/6) und GPS-Zeit
    (Tags 7/29). Ohne Höhe und GPS-Zeit stufte jedes Nachlesen der Datei (z. B. „Neu aufbauen“) den Ort als „geschätzt“ ein
    (`GeoAccuracyReason.NoAltitudeNoTime`); die Höhe brauchen außerdem Gipfelbuch und der geplante 3D-Übergang in Orte. Bei
    DJI gilt die EXIF-GPS-Höhe, nicht die Flughöhe über dem Startpunkt aus dem XMP (`RelativeAltitude`). Zeitraffer (MP4):
    Katalog wie oben, in der Datei Aufnahmezeit (`creation_time`) und Ort (`©xyz`).
  - **XMP:** `CreatorTool` „Lumora“, Verfahren, Quellenzahl; bei Kugel 360° GPano (`ProjectionType` equirectangular,
    `UsePanoramaViewer`, Vollbild-Maße).
  - Das Eigenschaften-Fenster nennt die Herkunft, z. B. „Datum: erstes Bild“, „Ort: Bild 3 von 26 (erstes mit Ort)“. Gleiches
    gilt für Stapel und „Neu berechnen…“.
- **Tags:** nur die gemeinsamen aller Quellen (Haken „gemeinsame Tags übernehmen“, an). **Favorit und Sterne nicht** (neues
  Bild). **Personen** ermittelt die Gesichtserkennung neu. Die Quellen melden `UsageReport.Used` (ADR-0072).
- **Kennzeichnung** „aus n Fotos zusammengefügt“: Ergebnis-Marke in der Galerie, Lightbox-Zeile „Zusammengefügt aus 26 Fotos ·
  Panorama, Kugel 360° · Quellen zeigen · Neu berechnen…“ („Neu berechnen“ öffnet das Fenster mit dem gespeicherten Rezept und
  speichert als weitere Datei), Abschnitt „Zusammengefügt“ im Eigenschaften-Fenster (Verfahren, Aufnahmeart, Größe, Quelle
  JPEG/RAW, Quellenzahl, davon nicht mehr vorhanden).
- **Warum nicht `PhotoVariants`:** Die Tabelle hat genau ein `OriginalFileId`; „beste Fassung zeigen“ würde nur die erste Quelle
  ausblenden, die Marke „2 Fassungen“ wäre falsch. Ein Ergebnis kann aber selbst Fassungen aus „Foto verbessern“ bekommen
  (`PhotoVariants` mit `OriginalFileId` = Ergebnis).

### 11. Schema und Domain

- **Migration `AddMergedPhotos`** (Stufe 1, nur CREATE TABLE):
  - `MergedPhotos (FileId PK → Files ON DELETE CASCADE, Method INT, Projection INT NULL, SourceCount INT, SeriesId INT NULL
    ohne FK, Tools TEXT, RecipeJson TEXT, CreatedAtUtc, BatchId)`. `SourceCount` bleibt stehen, wenn eine Quelle verschwindet
    („aus 26 Fotos, 2 nicht mehr in der Bibliothek“).
  - `MergedPhotoSources (ResultFileId → MergedPhotos ON DELETE CASCADE, SourceFileId → Files ON DELETE CASCADE, Position INT;
    PK (ResultFileId, SourceFileId))`, Index `IX_MergedPhotoSources_Source`.
  - `FileLinkKind` 6 = `MergedFrom`, 7 = `MergeSourceOf` für `FileLinkUndo` (Papierkorb oder Verschieben nach außen von Quelle
    bzw. Ergebnis); `UserDataCarryOver.Merges.cs` für „Neu aufbauen“ (Zuordnung über Pfade).
- **Migration `AddPhotoSeries`** (Stufe 2, nur CREATE TABLE):
  - `PhotoSeries (Id PK AUTOINCREMENT, LibraryId → Libraries CASCADE, FolderId → Folders CASCADE, Kind INT, SuggestedMethod
    INT, SuggestedProjection INT NULL, Confidence REAL, ReasonsJson TEXT (Codes, keine Namen), ItemCount INT,
    FirstCapturedAt, LastCapturedAt, CoverFileId INT NULL ohne FK (beim Lesen geprüft), Signature TEXT, State INT (0 neu,
    1 gesehen, 2 verworfen), DetectorVersion, DetectedAtUtc, ChangedAtUtc)`, Indizes (LibraryId, State, FirstCapturedAt) und
    (FolderId).
  - `PhotoSeriesItems (SeriesId → PhotoSeries CASCADE, FileId → Files CASCADE, Position INT, Role TEXT NULL, Origin INT
    (0 erkannt, 1 hinzugefügt, 2 ausgenommen); PK (SeriesId, FileId))`, Index `IX_PhotoSeriesItems_File`.
  - `FileCaptureDetails (FileId PK → Files CASCADE, SubSecMs, ExposureTime, FNumber, Iso, ExposureBias, ExposureMode,
    FocalLength35, FocusDistance, GimbalYaw, GimbalPitch, GimbalRoll, FlightYaw, SourceModifiedAtUtc, ReadAtUtc)`, alle
    Werte NULL-fähig — getrennt von `Files`, damit die heiße Galerie-Tabelle schmal bleibt.
  - `SeriesFolderScans (FolderId PK → Folders CASCADE, Signature TEXT, DetectorVersion, ScannedAtUtc)`.
  - `UserDataCarryOver.Series.cs` trägt nur den Benutzerzustand (verworfen, Origin 1/2) über die Pfade mit; erkannte Serien
    werden neu gerechnet.
- Vor beiden Migrationen läuft die Katalog-Sicherung (ADR-0069). PLAN Kap. 4 wird mit der jeweiligen Stufe nachgetragen.
- **Domain:** Entitäten `MergedPhoto`, `MergedPhotoSource`, `PhotoSeries`, `PhotoSeriesItem`, `FileCaptureDetails`,
  `SeriesFolderScan`; Enums `SeriesKind` (Burst 0, Panorama 1, ExposureBracket 2, FocusBracket 3, Interval 4,
  PanoramaBracket 5), `SeriesState` (New 0, Seen 1, Dismissed 2), `SeriesItemOrigin` (Detected 0, Added 1, Excluded 2),
  `MergeMethod` (Panorama 0, ExposureFusion 1, Denoise 2, RemovePeople 3, LongExposure 4, FocusStack 5, GroupShot 6,
  HdrPanorama 7, Timelapse 8), `PanoramaProjection` (Auto 0, Spherical360 1, Cylindrical 2, Perspective 3, Vertical 4,
  ShiftOnly 5).

### 12. Vorschau, Vorrang, Aktivitäts-Anzeige, Stapel (Mockup 28, Ansichten 11–13)

- **Vorschau vor dem Speichern:** `IMergeService.OpenPreviewSession(MergeRequest)` liest die Quellen verkleinert (lange Kante
  ≈ 1 600 px), richtet bei ≈ 0,6 MP aus und setzt ≈ 4–6 MP zusammen. Die Sitzung hält Zwischenstände im Speicher, solange das
  Fenster offen ist (nur Speicher, kein Vorrang); `UpdateAsync(options)` rechnet nur die betroffenen Schritte neu. Zwischenstände auf der Platte nur in
  `ICatalogStoragePaths.TempRoot`, beim Schließen gelöscht.
- **Volle Auflösung** aus dem Rezept (keine zweite Ausrichtung), aus Originalen bzw. DNG; `SaveAsync(session, MergeSaveOptions)
  → MergeSaveResult (FileId, BatchId)`; `EstimateAsync` liefert Pixel, Zeit, Arbeitsspeicher und Platz.
- **Vorrang nur, solange Herbert auf ein Rechnen wartet** (wie in ADR-0074, nicht für die Lebensdauer des Fensters):
  - **Vorschau:** Jede Berechnung (`OpenPreviewSession`, `UpdateAsync`) läuft in einem `ForegroundHold(ForegroundNeed.Cpu,
    "Zusammenfügen")` des Fensters. Er hält **eine** Anmeldung bei `IForegroundWork`, solange eine Berechnung läuft, und
    2 s danach (`ForegroundHold.Linger`) — schnelles Umstellen von Optionen lässt den Vorrang so nicht flackern. Ein offenes,
    untätiges Fenster hält den Hintergrund **nicht** an.
  - **Speichern:** `IForegroundWork.Begin(ForegroundNeed.Cpu, "Zusammenfügen")` vom Klick auf „Als neue Datei speichern“ bis
    zum Eintrag im Katalog. „Im Hintergrund weiterrechnen“ bzw. Schließen gibt die Anmeldung zurück und rechnet mit halben
    Kernen weiter, damit Lightbox und Galerie wieder Vorrang haben.
  - `| Gpu` nur für Hochskalieren und Gruppenfoto-Gesichter.
- **Ein schwerer Lauf zur Zeit** (`OpenCvRuntime`, Punkt 2): Vordergrund geht vor. Ein Stapel „nebenbei“ (unten) wartet vor
  dem nächsten Ergebnis bzw. Schritt, solange eine Vorschau oder ein Speichern mit Vorrang läuft, und zeigt „wartet — Vorrang
  für „Zusammenfügen““; Serien-Erkennung, Prüfsummen und Vorrat warten über den Planer ebenso.
- Gerechnet wird auf eigenem Thread (`TaskCreationOptions.LongRunning`); Abbruch zwischen Stufen, Bildern und Bändern (ein
  laufender OpenCV-Aufruf endet erst und wird dann verworfen).
- **Aktivitäts-Anzeige** (ADR-0067): `ActivityKind.Merge = 18`. Die Vorschau erscheint als Vorgang „Zusammenfügen – Vorschau“
  mit Abbrechen, ohne Eintrag unter „Zuletzt fertig“. Speichern ist ein Vorgang „Panorama zusammenfügen · 26 Fotos ·
  Überblenden (5/6) · noch ≈ 2 min · Vorrang“ mit Schritten, Restzeit, Abbrechen und danach „Panorama gespeichert“ mit
  [Öffnen] [Rückgängig]. Neue `ActivityActionKind.ShowResult = 3` („Öffnen“ bzw. „Ergebnisse zeigen“).
- **Stapel** (Stufe 2): mehrere Serien-Kacheln wählen oder „Alle 48 sichtbaren zusammenfügen…“ (Filter Kugel 360°). Ein Dialog
  (`MergeBatchDialog`) nennt Anzahl, Verfahren und Aufnahmeart je Gruppe (aus dem Vorschlag, umstellbar), Größe, Speicher- und
  Zeitschätzung, den Hinweis „ohne Einzelvorschau — unsichere werden übersprungen, jedes Ergebnis ist einzeln rückgängig“ und
  die Wahl „Nebenbei (halbe Kerne, Hintergrund läuft weiter)“ — Standard ab 3 Serien — oder „Mit Vorrang“. Der Lauf ist **ein**
  Vorgang „Zusammenfügen · 12 / 48“ mit Pause und Abbrechen; **jedes Ergebnis wird sofort in einem eigenen Batch** gespeichert
  (bei einem Absturz geht nichts verloren), „Rückgängig“ am Vorgang nimmt alle Batches des Laufs zurück. Ergebnisse unter der
  Konfidenzschwelle (Quellen nicht eingebunden, zu wenig Überlappung) werden nicht gespeichert; sie stehen unter „Details“ mit
  „Einzeln öffnen“ und behalten ihre Marke. Am Ende z. B.: „46 Panoramen gespeichert · Ergebnisse zeigen · Rückgängig“.
  Vor jedem Ergebnis prüft der Stapel den Zustand der Serie neu; was inzwischen anders zusammengefügt wurde, überspringt er.

### 13. Versteckte Bibliothek (ADR-0049; Mockup 28, Ansicht 17)

- Alles läuft gleich, im Katalog des Containers: Serien, Erkennung, `FileCaptureDetails`, Verknüpfungen.
- Zwischenstände, Vorschau-Dateien und FFmpeg-Ausgabe nur in `ICatalogStoragePaths.TempRoot`, Probe-Vorschauen unter
  `ThumbnailsRoot`, das Ergebnis neben der ersten Quelle (also im Container).
- OpenCL aus (kein Kernel-Cache außerhalb); externe Prozesse bekommen `TEMP`/`TMP` = `TempRoot`; FFmpeg ohne Bericht
  (`-nostats -loglevel error`).
- Das Protokoll nennt nur Verfahren und Id, nie Titel, Ordner- oder Dateinamen. „Im Explorer zeigen“ in der Fertig-Ansicht ist
  gesperrt.
- Ein externes Werkzeug (Hugin, falls es nach Spike 20 kommt) bleibt in der versteckten Sitzung gesperrt, bis belegt ist, dass es
  nichts außerhalb schreibt.

### 14. OneDrive-Platzhalter (Spike 19, ADR-0077; Mockup 28, Ansichten 2, 12 und 16)

- Die Erkennung lädt **nie** herunter. Platzhalter (zentrale Prüfung `CloudFiles.IsOnlineOnly` aus ADR-0077; Recall bzw.
  Offline = nur online) und Dateien unter Offline-Wurzeln zählen nur in Stufe A mit ihren Katalogdaten; ein
  DJI-Ordner wird so auch in OneDrive erkannt. Stufe B und C lassen sie aus; die Serie bekommt den Grund „n Fotos nur in
  OneDrive“ und eine niedrigere Sicherheit. Ein späterer Lauf nimmt sie auf, sobald sie lokal sind (Ordner-Signatur).
- Das Flyout zeigt, was ausgelassen ist („3 Ordner nur in OneDrive“).
- Zusammenfügen braucht die Pixel: Das Fenster fragt „5 von 26 Fotos liegen nur in OneDrive (≈ 120 MB) — zum Zusammenfügen
  herunterladen?“ [Herunterladen und fortfahren] [Ohne diese Fotos]. Nicht erreichbare Dateien auf Offline-Laufwerken werden
  genannt, nie als „fehlt“ gewertet (PLAN Kap. 12). Ergebnisse in OneDrive-Ordnern lädt OneDrive hoch (große Dateien).

### 15. Hochskalieren danach (optional; Mockup 28, Ansichten 5–10, aktiv gezeigt in 6)

- **Erst nach dem Zusammenfügen**, nie vorher: ein KI-Modell erfindet jedes Einzelbild anders und stört Merkmalssuche, Median
  und Fusion; außerdem dauerte es n-mal so lange.
- Im Fenster unter „Ergebnis“: „Danach hochskalieren 2× | 4×“ mit dem Modell aus „Foto verbessern“ (Real-ESRGAN und die übrigen
  Hochskalier-Modelle, ADR-0071). Neu: `IPhotoEnhanceService.UpscaleFileAsync(inputPath, outputPath, EnhanceUpscaleOptions,
  progress, ct)` neben `RenderAsync(fileId, …)` — derselbe `OnnxUpscaler`, Natürlichkeit und Körnung.
- **Grenzen** (aus `PhotoEnhanceService`): Ergebnis höchstens 64 MP, Ladekante höchstens 10 000 px, JPEG-Kante höchstens
  65 535 px. Also 2× nur für Zusammenfüge-Ergebnisse bis 16 MP, 4× nur bis 4 MP, jeweils mit langer Kante ≤ 10 000 px. Sonst ist
  der Haken ausgegraut mit Grund („Panorama hat 143 MP — volle Auflösung statt Hochskalieren wählen“). Zeit aus der vorhandenen
  Schätzung von „Foto verbessern“ (grob, je Megapixel), angezeigt neben „Speichern“. Fehlt das Modell: „Modell „…“ laden…“ wie in
  der Verbessern-Leiste.
- Sinnvoll vor allem für Entrauschen, HDR und Gruppenfoto aus Handyfotos; bei Panoramen fast nie. Für Stapel aus der Hand ist
  „Mehr Details aus der Serie“ (Punkt 5) die echte Alternative.
- Es entsteht **eine** Datei; das Rezept nennt „merge:hdr+upscale“. Wer danach weiter verbessern will, nutzt „Foto verbessern“ am
  Ergebnis — daraus wird eine normale Fassung.

### 16. Abgrenzung zu „Foto verbessern“

- **Geraderichten (Horizont), Perspektive korrigieren und Himmel verbessern (kein Himmeltausch)** sind Einzelbild-Werkzeuge
  und gehören in die Leiste „Foto verbessern“ (eigener Nachtrag zu ADR-0071 mit eigenem Mockup), nicht in das Fenster
  „Zusammenfügen“. Sie können später OpenCV-Bausteine aus `Lumora.Imaging` nutzen (Hough-Linien, Vier-Punkt-Entzerrung,
  Himmelsmaske mit lokalem Kontrast/Dunst).
- „Horizont gerade“ im Panorama ist die Wellenkorrektur der Panorama-Kette, kein Einzelbild-Werkzeug.
- Das Fenster „Zusammenfügen“ bietet nach dem Speichern nur den Sprung „Danach verbessern…“ (öffnet die Verbessern-Leiste mit
  dem Ergebnis).

### 17. Einstellungen › Zusammenfügen (Mockup 28, Ansicht 15)

Neuer Reiter nach „Verbessern“:

- **Serien erkennen:** an/aus, Arten, Zeitabstand Serienbild (2 s) und Panorama (15 s), mindestens 3 Fotos, Verworfene zeigen,
  „Serien neu erkennen“.
- **Ergebnis:** Ablage (neben dem ersten Einzelbild | Unterordner „zusammengefügt“), „Drohnen-Panorama-Ordner: eine Ebene höher
  ablegen“ (aus), Dateinamen-Zusätze je Verfahren (`_panorama`, `_hdr`, `_entrauscht`, `_ohne-leute`, `_langzeit`, `_fokus`,
  `_gruppenfoto`, `_hdr-panorama`, `_zeitraffer`), JPEG-Qualität 92, TIFF 16 bit bei RAW-Quellen (aus), Standardgröße je
  Aufnahmeart, gemeinsame Tags übernehmen (an), Quellen zusammengefügter Serien einklappen (an).
- **Rechnen:** Stapel ab n Serien „nebenbei“ (3).
- **Werkzeuge:** OpenCV (eingebaut, Version, geladen ja/nein), Raw-Bilderweiterung (gefunden / fehlt mit Verweis auf den
  Microsoft Store), FFmpeg (gefunden / fehlt), Hugin nur, wenn Spike 20 es vorsieht.

### 18. Tests und Prüfung

- **Eigenes Testprojekt `tests/Lumora.Imaging.Tests`** (xUnit + FluentAssertions, referenziert `Lumora.Imaging` und damit
  OpenCvSharp mit nativer DLL), in `Lumora.slnx` eingetragen. `tests/Lumora.Tests` bleibt frei von nativem OpenCV (Punkt 2);
  dort bleiben die Tests der Services-Teile (`CreateMergedAsync`, Migrationen, Galerie-Abfragen, Planer), die WIC-Teile aus
  `Wpf/Merge/` gehen nach `tests/Lumora.Wpf.Tests`. Stufe 1 ergänzt im Skill-Profil der CLAUDE.md unter „Checks“ die Zeile
  `imaging: dotnet test tests/Lumora.Imaging.Tests --nologo -v q (src/Lumora.Imaging/**, tests/Lumora.Imaging.Tests/**)` und
  nimmt `imaging` in die Pre-Commit-Checks auf. Verworfen: Imaging-Tests in `tests/Lumora.Tests/Imaging/` (bräuchte keine
  Profilzeile, zöge aber die native DLL in das einzige Unit-Testprojekt und widerspräche Punkt 2).
- Alle Bilder entstehen synthetisch im Test: prozedurale Szene → überlappende Ausschnitte mit bekannter Verschiebung, Drehung
  und Verstärkung → Panorama (PSNR/SSIM gegen die Wahrheit); simulierte Belichtungen; verrauschte Kopien (PSNR-Gewinn beim
  Entrauschen); wandernde Flecken als „Leute“ (Median); lokal unscharfe Zonen (Fokus-Stapel).
- Erkenner-Tests mit erfundenen Katalogzeilen nach den Mustern 26/21/9/3 (Neigung +13/−17/−47/−75°, Gier in 45°-Schritten),
  Reihen mit `ExposureMode` 2, Intervallen, Platzhaltern und Gegenbeispielen.
- Plan-Test der Galerie-Abfragen, Planer-Test für `BackgroundJob.Series`, Vorrang-Test analog `EnhanceForegroundTests`,
  Speichern/Rückgängig/„Neu aufbauen“ analog den Fassungen.
- Metadaten-Tests (Punkt 10): erstes Bild ohne Ort → Ort der nächsten Quelle mit Ort; echtes GPS vor geschätztem; keine
  Quelle mit Zeit bzw. Ort → leer, nie das Erstelldatum; der geschriebene DNG-Block enthält Höhe und GPS-Zeit, und nach dem
  Nachlesen bleibt `GeoAccuracy.Exact`; die App-Verbindung liefert das Ergebnis im Zeitfenster und Kartenausschnitt seiner
  Einzelbilder.
- Rauchtest `SmokeScenario.Merge.cs` mit zur Laufzeit erzeugten Bildern (keine externen Assets). Prüfung der versteckten Sitzung:
  nach einem Lauf ist außerhalb des Containers nichts entstanden.
- Der Mess-Harness aus Spike 20 wandert nach `tests/Lumora.PerfTests`; echte Fotos nur über eine `LUMORA_*`-Variable aus
  `werkzeuge`, nie ins Repo, keine Dateinamen im Bericht.

### 19. Bau-Stufen (Reihenfolge der Verfahren von Herbert; Einordnung der Serien-Erkennung und Teilung des Panoramas: Claudes Entscheidung)

Von Herbert stammt die Reihenfolge Panorama-Spike auswerten → Konzept → Panorama → Belichtungsreihe/Entrauschen/Leute
entfernen/Langzeit → Fokus-Stapel/Zeitraffer/HDR-Panorama → Gruppenfoto. **Eigene Ergänzung (Claude), bei der Abnahme zu
bestätigen:** Die Serien-Erkennung ist eine eigene Stufe 2 zwischen den beiden Panorama-Stufen, und Panorama ist in Stufe 1
(Fundament, aus der Auswahl) und Stufe 3 (RAW/DNG, Größenwahl, bessere Lückenfüllung) geteilt. Grund: Stufe 1 liefert schnell
ein nutzbares Panorama aus der freien Auswahl, ohne auf die Erkennung zu warten; die 110 Drohnen-Ordner lassen sich erst mit
der Erkennung und dem Stapel (Stufe 2) bequem abarbeiten, und RAW, 16K und „Voll“ prüft erst Stufe 3 am ganzen Stapel. Die
übrigen Verfahren folgen danach in Herberts Reihenfolge (Stufen 4–6).

0. **Klären (nur Docs):** Spike 20 auswerten (`docs/spikes/20-panorama.md`: Bildvergleich gegen Photoshop an je einem
   360°-Drohnensatz, Handy-Schwenk, Weitwinkel 3 × 3 und Vertikal; Nähte, Belichtung, Lücken, Laufzeit, Speicherspitze bei voller
   Auflösung; zentrale Frage: Aufnahmeart wählbar und absolute Lage für 2 : 1?). Werkzeugwahl als Nachtrag hier. Mockup 28
   abnehmen lassen, ADR auf Accepted, Nachtrag ADR-0074 (Serien 3, Prüfsummen 4, Vorschaubilder auf Vorrat 5 — abgeglichen mit
   dem Nachtrag 0.5.40, sobald der auf main ist), PLAN Kap. 3/4/7/15, je Stufe ein neutral formuliertes GitHub-Issue (keine
   Dateinamen).
1. **Fundament + Panorama aus der Auswahl:** Projekt `Lumora.Imaging`, `OpenCvRuntime`, WIC-Leser/-Schreiber, Migration
   `AddMergedPhotos`, `CreateMergedAsync` mit Rückgängig, `FileLinkUndo` 6/7, Carry-Over, `IMergeService` (Analyse,
   Vorschau-Sitzung, Speichern), Panorama mit allen Aufnahmearten nach dem Spike-Weg (Kugel 360° als 2 : 1 mit einfacher Füllung
   und GPano), Vorschlag aus der Auswahl (Klassifizierer auf Dateiköpfen und Vorschaubildern), Fenster (nur Panorama aktiv,
   übrige „kommt später“), Rechtsklick in Galerie, Liste und Lightbox (Zeit-Nachbarn; damit auch aus Album und aus einer Story
   geöffneter Lightbox) und Aktionsleiste, „auch ins Album“, `ActivityKind.Merge` mit Vorrang nur beim Rechnen, Ergebnis-Marke,
   Lightbox-Zeile, Suchen ▸ Quellen/Ergebnis, versteckte Sitzung, Testprojekt `Lumora.Imaging.Tests` mit Profilzeile `imaging`,
   Tests und Rauchtest.
   *Abnahme:* ein 9-Bilder-Weitwinkel und ein Handy-Schwenk ergeben je ein Panorama mit Vorschau; Rückgängig räumt Datei und
   Verknüpfungen weg; in der versteckten Bibliothek entsteht nichts außerhalb des Containers.
2. **Serien-Erkennung + Angebot in der Galerie:** Migration `AddPhotoSeries`, `CaptureDetails` im Probe, Erkennung Stufe A–C,
   `BackgroundJob.Series` (Vorrang 3, Prüfsummen 4, Vorrat 5; `HasWaitingAhead`), `ActivityKind.Series`, Platzhalter-Regel, Marke und Punkt, Chip mit Flyout,
   eine Kachel je Serie, Serien-Leiste, Ein-Klick, Keine Serie / Anpassen / Verworfene, Suchen ▸ Gleiche Serie, Quellen
   einklappen, vorhandene Ergebnisse erkennen, Stapel, Einstellungen (Teil Serien). Angeboten werden zunächst Panoramen;
   Belichtungsreihen und Serienbilder werden schon gespeichert.
   *Abnahme:* alle 110 DJI-Ordner erkannt (48 Kugel, 43 Zylinder, 6 Perspektive, 3 Vertikal, die übrigen 10 nach den
   Gimbal-Winkeln), kein Platzhalter heruntergeladen, der Erstlauf auf Herberts großer Bibliothek blockiert Galerie und Lightbox
   nicht spürbar.
3. **Panorama vollständig:** RAW/DNG über die Raw-Bilderweiterung mit Rückfall, Größenwahl mit Speicherprüfung (16K / Voll),
   TIFF 16 bit wahlweise, bessere Lückenfüllung (Inpainting, LaMa wenn geladen), Probe-Vorschauen in der Serien-Leiste, Ablage
   „eine Ebene höher“, optional Hugin, falls Spike 20 es ergibt.
   *Abnahme:* alle 48 Kugel-Sätze im Stapel ohne Absturz auf Herberts Rechner (32 GB, Spike 02); Ergebnis neben Photoshop
   vergleichbar (Herbert urteilt).
4. **Belichtungsreihe, Entrauschen (mit „Mehr Details“), Leute entfernen, Langzeit** samt `FrameAligner`, Vorher/Nachher,
   Masken, Geister entfernen; Serien-Arten Belichtungsreihe und Serienbild werden angeboten; **Hochskalieren danach**.
   *Abnahme:* synthetische Tests (PSNR-Gewinn, entfernter Fleck) und Herberts Serien.
5. **Fokus-Stapel, Zeitraffer, HDR-Panorama**; Serien-Arten Fokusreihe, Intervall, Panorama-Belichtungsreihe.
6. **Bestes Gruppenfoto** (zuletzt, aufwendigstes). Danach Abschluss der Welle mit MINOR-Sprung.

### 20. Ausblick (nicht Teil dieses Vorhabens)

- Einzelbild-Werkzeuge Geraderichten, Perspektive, Himmel verbessern in „Foto verbessern“ (Punkt 16).
- Highlights und Storys (ADR-0057/0058) nutzen `PhotoSeries` als Gruppe und zeigen das Ergebnis statt seiner Quellen.
- 360°-Rundum-Ansicht in der Lightbox für Kugel-Panoramen (bis dahin flach in 2 : 1, GPano für andere Programme).
- Pinsel „aus Foto n übernehmen“ für Leute entfernen und HDR.
- Gemeinsames Foto-Menü in der Story-Seite (ADR-0068 nennt sie, gebaut ist dort nur „Als Titelbild für ▸“). Bis dahin startet
  Zusammenfügen aus einer Story über die Lightbox (Punkt 7).

### 21. Mockup 28 (`mockups/28-serien-zusammenfuegen.html`)

**Entwurf, Abnahme durch Herbert ausstehend.** Eine HTML-Datei ohne externe Abhängigkeiten, Tokens 1:1 aus Mockup 08 (dazu
`--warning-soft`/`--smoke` wie 10/13/14/18/26), Galerie-Rahmen wie 18/24/26, dunkle Bühne und Lightbox wie 13, Aktivitäts-Anzeige
wie 15; Hell/Dunkel, beim Öffnen nach der Systemeinstellung wie 08/13. Oben die Ansichten-Leiste, darunter je Ansicht die
Zustände, unter dem Fenster eine Erläuterung mit Verweis auf die Punkte dieses ADR. Die Ansichten (Zustände in Klammern):

| Nr. | Ansicht (Knopf) | Zeigt | Punkte |
|---|---|---|---|
| 1 | Marken | Galerie mit Marke „Serie · n Fotos“ nur auf dem ersten Foto, Punkt mit Tooltip auf den übrigen, Häkchen nach dem Zusammenfügen, Ergebnis-Marke „Panorama · aus 26 Fotos“, eingeklappte Quellen, Erkennung still in der Aktivitäts-Anzeige | 4, 9 |
| 2 | Filter „Serien“ (Flyout offen, nur Kacheln) | Chip „Serien · 12 neu“, Flyout mit Arten (Panorama 110 = 48 Kugel, 43 Zylinder, 6 Perspektive, 3 Vertikal, 10 andere), Zustand (2 zusammengefügt → 108 offen), „Eine Kachel je Serie“, Stand mit OneDrive-Ordnern, Stapel „Alle 108 sichtbaren zusammenfügen…“; Kacheln mit Stapel-Rand und Ein-Klick-Knopf | 9, 12, 14 |
| 3 | Geöffnete Serie (Serien-Leiste, Verfahren ▾, Serie anpassen) | statt Aufklappen: Bedingung „Serie: …“ + Serien-Leiste mit Gründen, Probe-Vorschau, Ein-Klick mit ▾ („passt“/„eher nicht“), Anpassen mit Haken | 9 |
| 4 | Rechtsklick (Mehrere Fotos, Foto einer Serie, Marke, Suchen ▸, Liste, Lightbox, Lightbox → Fenster, Aus einem Album, Gesperrt) | Bearbeiten ▸ „5 Fotos zusammenfügen…“ neben „5 Fotos verbessern…“ / „Serie mit 26 Fotos zusammenfügen…“, bei Mehrfachauswahl ohne Öffnen und Eigenschaften, Aktionsleiste, Menü der Marke, Suchen ▸ „Gleiche Serie“ / „Zusammengefügtes Ergebnis“, dasselbe Menü in der Listenansicht und in der Lightbox („Zusammenfügen…“ bei einem Foto ohne Serie, auch aus einer Story geöffnet), das Fenster danach mit den Zeit-Nachbarn noch nicht angehakt, Start aus einem Album, gesperrt bei gemischten Bibliotheken | 7, 8, 9 |
| 5 | Panorama (Automatisch, Kugel 360°, Zylinder, Perspektive, Vertikal, Nur verschieben, Stufe 1, HDR-Panorama) | Fenster mit Verfahrensliste und „Warum dieser Vorschlag“, Vorschau mit Zuschnitt, Lücken und Nähten, Filmstreifen mit Rollen, alle Aufnahmearten als Kacheln mit Skizze, Zuschnitt, Quelle JPEG/RAW, Größe mit Speichergrenze, Hochskalieren ausgegraut; Stufe 1 mit „kommt später“; HDR-Panorama mit Gruppen | 5, 6, 8, 15 |
| 6 | Belichtungsreihe (Natürlich, Kräftig) | Vorher/Nachher gegen die mittlere Belichtung, Geister, Ausrichten, „Danach hochskalieren“ 2× aktiv, 4× mit Grenze ausgegraut | 5, 15 |
| 7 | Serienbild (Entrauschen, Leute entfernen, Langzeit) | Entrauschen mit „Mehr Details aus der Serie“, Leute entfernen mit Maske „veränderte Stellen“, Langzeit Weich/Lichtspuren | 5 |
| 8 | Fokus-Stapel | Überlagerung „Schärfe-Herkunft“, Ausrichten mit Maßstab, Halo-Hinweis | 5 |
| 9 | Gruppenfoto | Grundbild mit Gesichtsrahmen, je Person eine Leiste mit Marken, unsichere Zuordnung bleibt | 5 |
| 10 | Zeitraffer | Abspielfläche, Bilder je s mit Länge, 1080p/4K, Ausschnitt, FFmpeg-Zustand | 5 |
| 11 | Speichern → Fertig (Vor dem Speichern, Rechnet, Fertig) | Schätzung, Schrittliste mit Vorrang (nur solange gerechnet wird) und „Im Hintergrund weiterrechnen“, Fertig-Ansicht mit Kennzeichnung „aus 26 Fotos zusammengefügt“, Rückgängig, Danach verbessern…; gestartet aus einem Album („auch ins Album …“, `IAlbumService.AddFilesAsync`) | 7, 8, 10, 12 |
| 12 | Stapel | Dialog „48 Panoramen zusammenfügen“ mit Gruppe, Schätzung, „ohne Einzelvorschau“, OneDrive, Nebenbei/Mit Vorrang | 12, 14 |
| 13 | Aktivitäten (Speichern läuft, Vorschau, Fertig-Meldung) | Speichern als einziger laufender Vorgang mit Schritt und Vorrang; Stapel „Zusammenfügen · 12 / 48“ (ein Vorgang), Serien erkennen, Prüfsummen (Vorrang 4) und Vorschaubilder auf Vorrat (Vorrang 5) unter „Wartet — Vorrang für „Zusammenfügen““; Zuletzt fertig mit Ergebnisse zeigen/Rückgängig; Galerie dahinter ohne Serien-Filter | 4, 12 |
| 14 | Lightbox | Ergebnis zuerst, Zeile „Zusammengefügt aus 26 Fotos · Quellen zeigen · Neu berechnen…“, Info-Abschnitt „Zusammengefügt“ | 10 |
| 15 | Einstellungen | Reiter „Zusammenfügen“: Serien erkennen (Vorrang 3 der Datei-Spur), Standard-Arten, Ergebnis, Rechnen (Vorrang nur beim Rechnen, nicht für das offene Fenster), Werkzeuge | 4, 12, 17 |
| 16 | Hinweise (OneDrive, RAW nur Vorschau, Speicher, Bibliotheken gemischt, Teil einer Serie, Mehrere Serien) | Rückfragen und Warnstreifen im Fenster, gesperrter Menüeintrag | 6, 7, 8, 14 |
| 17 | Versteckt (Galerie, Fertig) | gleiche Bedienung in der versteckten Sitzung mit eigenen Zahlen (7 Serien · 74 Fotos, Chip „3 neu“, keine OneDrive-Marke — der Container ist ein eingehängtes Laufwerk ohne Platzhalter), Übersicht „alles bleibt im Container“, „Im Explorer zeigen“ gesperrt | 13 |

Alle Fotos sind CSS-Verläufe und Platzhalter-Formen, Gesichter neutrale Kreise; Namen, Ordner, Alben und Dateinamen sind
erfundene Muster, keine echten Dateinamen, Ordner, Orte oder Gesichter, nur Zahlen (110 / 48 / 43 / 6 / 3, 26 Fotos, 51 s).
Die offenen Punkte „Quellen einklappen“, „Klick auf die Kachel-Marke“ und „Stapel ohne Einzelvorschau“ zeigt das Mockup als
Entwurf; sie sind bei der Abnahme zu bestätigen.

## Begründung

- **Ein Fenster statt Assistent:** Verfahren, Vorschau und Optionen nebeneinander sparen einen Schritt und zeigen sofort, warum
  Lumora etwas vorschlägt; aus einer sicheren Serie öffnet sich das Fenster gleich mit laufender Vorschau — der Ein-Klick-Weg,
  den Herbert will.
- **Serien still im Planer:** Sie kosten fast nichts (Katalog, dann nur Dateiköpfe der Kandidaten, Bildprüfung nur auf
  Vorschaubildern), halten sich an Vorrang und Pause und melden sich nur über einen Zähler. Vorrang 3 vor Prüfsummen und
  Vorrat, weil sie nur Dateiköpfe lesen, schnell fertig sind und sichtbaren Nutzen bringen; Prüfsummen und Vorrat lesen ganze
  Dateien und würden die Erkennung in einer großen Bibliothek um Stunden bis Tage verschieben. Gegenüber allem, worauf
  Herbert wartet, bleibt sie die niedrigste Arbeit (Punkt 4).
- **Vorrang nur beim Rechnen:** ADR-0074 gibt Vorrang nur, solange der Benutzer wirklich wartet. Ein offenes, untätiges
  Fenster würde sonst alle Hintergrund-Aufgaben unbegrenzt anhalten.
- **Ordnergrenze:** Drohne, Kamera und Handy legen eine Serie in einen Ordner. Die Grenze hält die Erkennung inkrementell
  (Ordner-Signatur), billig und frei von Fehlalarmen über Ordner hinweg.
- **Eine Kachel je Serie statt Aufklappen:** nutzt die vorhandene Galerie-Abfrage, bleibt mit Virtualisierung, Seiten und
  Automation-Blättern verträglich; die Serien-Leiste zeigt Gründe und Knöpfe an einer Stelle.
- **Abgeleiteter Zustand „zusammengefügt“:** Rückgängig, Papierkorb und „Neu aufbauen“ brauchen keine Sonderfälle.
- **Eigene n : 1-Beziehung:** `PhotoVariants` ist 1 : 1; Marke, Einklappen und „Quellen zeigen“ brauchen alle Quellen.
- **Gleicher Datei-Vorgang wie Fassungen:** Scan-Sperre, eigene Pfade, Protokoll, Aktivitäts-Anzeige und Rückgängig ohne neuen
  Mechanismus; eigener `OperationType`, damit Protokoll und Fehlerbilder unterscheidbar bleiben.
- **Eigenes Projekt `Lumora.Imaging`:** natives OpenCV bleibt aus Services, Data und deren Tests heraus; Imaging braucht aber
  Katalog, Datei-Vorgänge, Planer und Aktivität aus den Services — deshalb wie AppConnect über den Services. Folgerichtig
  bekommt es ein eigenes Testprojekt mit eigener Profilzeile (Punkt 18).
- **Schnittstelle vor Werkzeug:** Der Probe-Lauf zeigt, dass OpenCvSharp die Aufnahmeart nicht direkt wählen lässt. Mit der
  zweiphasigen Schnittstelle hängen Oberfläche, Rezept und Stapel nicht am Ergebnis von Spike 20.
- **Hochskalieren erst danach:** einmal statt n-mal und ohne Störung der Ausrichtung; die Grenzen von ADR-0071 gelten unverändert.
- **Stapel mit einem Batch je Ergebnis:** Ein Lauf über 48 Kugeln dauert Stunden; ein Absturz darf fertige Ergebnisse nicht
  verlieren, Rückgängig bleibt trotzdem für den ganzen Lauf möglich.

## Konsequenzen

### Positiv
- Herberts Panoramen, Belichtungsreihen und Serien entstehen in Lumora, mit Vorschau, Rückgängig und ohne Photoshop.
- Einzelbilder bleiben unverändert; jedes Ergebnis ist eine normale Datei mit Katalogzeile, Datum, Ort und Verknüpfung zu den
  Quellen.
- 110 Drohnen-Panoramen lassen sich mit einem Stapel abarbeiten; schon vorhandene Photoshop-Ergebnisse werden erkannt.
- Die versteckte Bibliothek und OneDrive-Ordner verhalten sich wie gewohnt (nichts außerhalb, kein stiller Download).

### Negativ
- Das portable Paket wächst um ≈ 69 MB (gepackt weniger); kein ARM64. Lädt die native DLL nicht (VC-Laufzeit, Virenscanner),
  fällt das Merkmal aus.
- Volle Auflösung braucht viel Speicher und Zeit (Kugel ≈ 450 MP, 16-bit-Pyramiden geschätzt 8–12 GB); deshalb 16K als
  Standard. Ein nativer OpenCV-Absturz im Prozess kann Lumora beenden (Hilfsprozess erst, wenn Spike 20 es nahelegt).
- Abbrechen wirkt nur zwischen Stufen, Bildern und Bändern; bei einer großen Kugel kann das dauern.
- RAW-Farben weichen ohne Kameraprofil und Objektivkorrektur von Lightroom/Photoshop ab; die Raw-Bilderweiterung kann auf anderen
  PCs fehlen.
- Fünf neue Tabellen in zwei Migrationen, zwei neue Hintergrund-/Vorgangsarten, eine weitere Galerie-Spalte.
- Lückenfüllung bei 360° erreicht nicht die inhaltsbasierte Füllung von Photoshop; Parallaxe und Bewegung erzeugen
  Doppelkonturen; Halos beim Fokus-Stapel, Geister bei HDR, sichtbare Übergänge am Gesichtsrand beim Gruppenfoto.
- Große Ergebnisse (134–450 MP) sind in Lightbox, Vorschaubildern und Diashow ungetestet (Dekodierzeit, Speicher, 100-%-Zoom).
- Der Stapel ohne Einzelvorschau ist eine bewusste Ausnahme von „Vorschau vor dem Speichern“ (nur auf ausdrücklichen Start).

### Neutral
- Neue Werte: `BackgroundJob.Series` (Vorrang 3; Prüfsummen 4, Vorschaubilder auf Vorrat 5), `ActivityKind.Merge = 18`,
  `ActivityKind.Series = 19`,
  `ActivityActionKind.ShowResult = 3`, `OperationType.CreateMerged = 16`, `FileLinkKind` 6/7; Nachrichten
  `SeriesChangedMessage`, `MergeSavedMessage`.
- Settings `series.*` und `merge.*` in der Settings-Tabelle des jeweiligen Katalogs.
- FFmpeg (Zeitraffer) und die Raw-Bilderweiterung bleiben Sache des Benutzers; ohne sie sind die Verfahren ausgegraut bzw. nutzen
  JPEG.

## Alternativen

- **Hugin als Haupt-Werkzeug** (cpfind, autooptimiser, nona, enblend): ausgereift bei Kugel und Parallaxe, aber GPL, eigene
  Installation, externer Prozess mit eigenen Temp-/Konfigurationsdateien (versteckte Sitzung), keine Vorschau aus dem Speicher
  → nur als Option nach Spike 20, vom Benutzer installiert, nie im Paket.
- **Photoshop/Lightroom weiter nutzen (Lumora übergibt per Skript, übernimmt das Ergebnis):** Abo und Installation nötig, keine
  Steuerung der Aufnahmeart aus Lumora, kein Stapel über 110 Ordner, Spuren außerhalb des Containers und Adobe-Cloud bei der
  versteckten Bibliothek → verworfen. Vorhandene Photoshop-Ergebnisse werden aber erkannt und verknüpft (Punkt 4).
- **Alles in „Foto verbessern“:** „Verbessern“ ist eine Leiste für **ein** Foto in der Lightbox mit 1 : 1-Fassung; Zusammenfügen
  braucht eine Auswahl, Serien, eine n : 1-Beziehung und ein großes Vorschaufenster → eigenes Fenster. Geteilt werden
  Datei-Vorgang, Hochskalieren, Einstellungs-Aufbau und Marken-Muster.
- **`PhotoVariants` weiterverwenden:** nur ein Original je Fassung → verworfen (Punkt 10).
- **Entwurf „Werkzeug zuerst“ ganz** (zweiseitiger Assistent, Serien nur als leichte Marke): ein Schritt mehr bis zur Vorschau,
  Serien schwer zu korrigieren, gespeicherter Zustand „zusammengefügt“ → als Ganzes verworfen, Einzelideen übernommen (Kontext).
- **Serien-Ansicht mit Aufklappen in der Galerie** (aus Entwurf A): Einfügen in die virtualisierte, seitenweise Liste bei
  Automation-Blättern ist riskant → „eine Kachel je Serie“ + Serien-Leiste.
- **Serien über Ordner hinweg:** teurer, mehr Fehlalarme → verworfen; die freie Auswahl deckt das ab.
- **Hochskalieren vor dem Zusammenfügen:** n-mal so lange, KI-Details stören Ausrichtung, Median und Fusion → verworfen.
- **Ergebnis nur als Rezept (Bild bei Bedarf berechnen):** andere Programme sehen nichts, jedes Öffnen rechnet neu → verworfen.
- **OpenCV in einem Hilfsprozess:** schützt vor nativen Abstürzen und Speicherspitzen, braucht aber einen WIC-freien Dekoder und
  Datenübergabe großer Puffer → vorerst nicht; Entscheidung nach Spike 20.
- **Ergebnis überschreibt die Einzelbilder bzw. löscht sie:** widerspricht der Vorgabe → ausgeschlossen.

## Offene Punkte (vor dem Bau)

- **Spike 20 (ausstehend):** Reicht die eigene Kette auf OpenCV-Bausteinen gegen die Photoshop-Ergebnisse (Kugel mit Zenit/Nadir
  und Parallaxe der Drohne, Handy-Schwenk, 3 × 3, Vertikal)? Laufzeit und Speicherspitze bei 16K und voller Auflösung auf
  Herberts Rechner? Sonst Hugin oder nativer Baustein; Hilfsprozess ja/nein. Entscheidung als Nachtrag hier.
- **Vorrang der Serien-Erkennung:** Herbert wünschte „niedrigen Vorrang“. Entwurf: niedrigste Arbeit gegenüber allem, worauf er
  wartet, aber Vorrang 3 in der Datei-Spur **vor** Prüfsummen (4) und Vorschaubildern auf Vorrat (5), weil sie nur Dateiköpfe
  liest. Bestätigen — oder Vorrang 5 hinter beide (Serien erscheinen dann erst nach deren Erstlauf)?
- **Bau-Reihenfolge:** Serien-Erkennung als eigene Stufe 2 und Panorama geteilt in Stufe 1 und 3 sind Claudes Ergänzung zu
  Herberts Reihenfolge (Punkt 19). Bestätigen?
- **Schreibweise:** Herberts Name „Zusammenfügen …“ steht in Menü und Knöpfen als „Zusammenfügen…“ ohne Leerzeichen, wie alle
  Einträge des Foto-Menüs („Foto verbessern…“); Mehrfachauswahl „5 Fotos zusammenfügen…“, Serie „Serie mit 26 Fotos
  zusammenfügen…“ (Punkt 1). Bestätigen?
- **Start aus einer Story** nur über die Lightbox, Ergebnis nicht automatisch in der Story (Punkt 7) — reicht das?
- **Quellen einklappen** standardmäßig an? (Entwurf: an.) Bei der Abnahme von Mockup 28 bestätigen.
- **Klick auf die Kachel-Marke:** Serie zeigen (Entwurf) oder gleich das Fenster öffnen? Bei der Abnahme entscheiden.
- **Ablage bei Drohnen-Ordnern:** neben dem ersten Einzelbild (Herberts Wunsch, Standard) — Option „eine Ebene höher“ behalten?
- **Bewertung und Tags:** nur gemeinsame Tags, keine Sterne/Favorit — abweichend von „Foto verbessern“. Bestätigen?
- **Stapel ohne Einzelvorschau** als erlaubte Ausnahme von „Vorschau vor dem Speichern“ — bestätigen?
- **Ergebnisgröße:** Kugel 360° Standard 16K, JPEG Q92, TIFF 16 bit nur auf Wunsch — oder volle Auflösung als Standard, wenn der
  Speicher reicht?
- **Die 10 DJI-Ordner** außerhalb der Muster 26/21/9/3: abgebrochene Aufnahmen oder andere Modi? Entwurf: Aufnahmeart aus den
  Gimbal-Winkeln, bei Unsicherheit „Automatisch“ ohne Ein-Klick-Vorschlag.
- **„Augen zu“ beim Gruppenfoto:** 2d106det aus dem buffalo_l-Archiv als eigener Eintrag in „Modelle & Daten“ — einverstanden?
- **360°-Rundum-Ansicht** in der Lightbox als eigene Stufe nach Stufe 3?
- **Reihenfolge der Einzelbild-Werkzeuge** (Geraderichten, Perspektive, Himmel) gegenüber den Stufen 4–6.

## Referenzen

- Mockup `mockups/28-serien-zusammenfuegen.html` (Entwurf, Abnahme ausstehend), Spike 20 `docs/spikes/20-panorama.md` (ausstehend; Probe-Lauf und
  Spike-Code außerhalb des Repos in `werkzeuge\runs\panorama-spike`)
- Vorlagen im Code: `src/Lumora.Wpf/PhotoMenu/PhotoContextMenuBuilder.cs` (Bearbeiten ▸, Suchen ▸),
  `src/Lumora.Wpf/PhotoMenu/PhotoMenuModels.cs` (`PhotoMenuSource`, `PhotoMenuGestures`), `src/Lumora.Wpf/Gallery/Views/GalleryView.PhotoMenu.cs`,
  `src/Lumora.Wpf/Lightbox/LightboxWindow.PhotoMenu.cs`, `src/Lumora.Services/Albums/IAlbumService.cs` (`AddFilesAsync`),
  `src/Lumora.Services/Operations/FileOperationService.Variants.cs` (`CreateVariantsAsync`),
  `src/Lumora.Services/Background/` (`BackgroundPlanner`, `ForegroundWork`, `ForegroundHold`, `BackgroundCpu`; mit dem
  Nachtrag 0.5.40 `HasWaitingAhead` und `ThumbnailStockService`),
  `src/Lumora.Services/Metadata/FileMetadataProbe.cs`, `src/Lumora.Services/Enhance/PhotoEnhanceService.cs` (Grenzen 64 MP /
  10 000 px), `src/Lumora.Services/Faces/` (Gesichter), `ThumbnailService.IsLocallyAvailable` (Platzhalter)
- Mockups als Designvorlage: `mockups/08-lumora-app.html`, `13-foto-verbessern.html`, `18-galerie-ordnerleiste.html`,
  `26-container-verschieben.html`
