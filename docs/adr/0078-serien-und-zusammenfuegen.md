# ADR-0078: Serien und Zusammenfügen — aus mehreren Fotos eines, Serien still erkannt, Ergebnis als neue verknüpfte Datei

- **Status:** Accepted (Herbert, 2026-11-18, mit Mockup 28; seine Entscheidungen zu den offenen Punkten und zum Nachtrag
  0.5.44 stehen im Abschnitt „Abnahme 2026-11-18“ am Ende und gehen dem Text darüber und dem Mockup vor)
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
  (Sicherung vor Migrationen), [Spike 19](../spikes/19-onedrive-platzhalter.md) (OneDrive-Platzhalter), [Spike 20](../spikes/20-panorama.md) (Panorama, gelb;
  Folgen im Nachtrag am Ende)
- **Mockup:** `mockups/28-serien-zusammenfuegen.html` (abgenommen 2026-11-18 mit den Abweichungen aus „Abnahme 2026-11-18“;
  17 Ansichten, siehe Punkt 21)

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
  „Microsoft Raw Image Decoder“) liefert die volle Auflösung 5 472 × 3 648 in 2,3 s bei 343 MB Spitze — laut Spike 20 aber
  nativ nur 8 bit (`24bppRGB`, 16 bit nur hochgerechnet), siehe Nachtrag.
- In Handy-Serien wechselt die EXIF-Ausrichtung innerhalb einer Serie (1/3/6).
- OpenCvSharp 4.13 bindet vom OpenCV-Detail-Modul nur die Merkmale (`computeImageFeatures`) und `BestOf2NearestMatcher` an.
  Der Spike baut deshalb Startwerte, Bündelausgleich, Wellenkorrektur, Projektion, Belichtungsausgleich, Graph-Cut-Naht und
  Mehrband-Überblendung nach dem Muster `stitching_detailed` in C# nach.
- **Spike 20 liegt vor** ([`docs/spikes/20-panorama.md`](../spikes/20-panorama.md), gelb): Bildvergleich gegen Herberts
  Photoshop-Ergebnisse, Laufzeit, Speicherspitze. Die Folgen für Werkzeug, RAW und Hochskalieren stehen als Vorschlag im
  Nachtrag am Ende; bis zur Abnahme gilt Punkt 3 unverändert.

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
  fehlt die Erweiterung, Rückfall auf JPEG mit Hinweis. Wahlweise „Farben an JPEG angleichen“. *(Spike 20: die Erweiterung
  liefert nur 8 bit; Vorschlag im Nachtrag: eigene 16-bit-Entwicklung für DNG, Erweiterung nur Rückfall.)*
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

> Die Abnahme 2026-11-18 ergänzt diese Liste (Nr. 2, 3 und 9): Stufe 3 enthält zusätzlich die Wahl „Mit Hugin rechnen“ und die
> eigene 16-bit-Entwicklung der DNG; danach folgt eine eigene Stufe 3b (360°-Rundum-Ansicht in der Lightbox); die
> Einzelbild-Werkzeuge Geraderichten, Perspektive und Himmel kommen nach Stufe 6. Stufe 1 ist gebaut (0.5.46 Teil A, 0.5.47
> Teil B).

### 20. Ausblick (nicht Teil dieses Vorhabens)

- Einzelbild-Werkzeuge Geraderichten, Perspektive, Himmel verbessern in „Foto verbessern“ (Punkt 16).
- Highlights und Storys (ADR-0057/0058) nutzen `PhotoSeries` als Gruppe und zeigen das Ergebnis statt seiner Quellen.
- 360°-Rundum-Ansicht in der Lightbox für Kugel-Panoramen (bis dahin flach in 2 : 1, GPano für andere Programme).
- Pinsel „aus Foto n übernehmen“ für Leute entfernen und HDR.
- Gemeinsames Foto-Menü in der Story-Seite (ADR-0068 nennt sie, gebaut ist dort nur „Als Titelbild für ▸“). Bis dahin startet
  Zusammenfügen aus einer Story über die Lightbox (Punkt 7).

### 21. Mockup 28 (`mockups/28-serien-zusammenfuegen.html`)

**Abgenommen 2026-11-18 mit Abweichungen (siehe „Abnahme 2026-11-18“).** Eine HTML-Datei ohne externe Abhängigkeiten, Tokens 1:1 aus Mockup 08 (dazu
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

> Alle Punkte dieser Liste hat Herbert am 2026-11-18 entschieden — siehe „Abnahme 2026-11-18“ am Ende. Die Liste bleibt als
> Verlauf stehen.

- **Spike 20 (liegt vor, gelb):** Herberts Urteil über die Vergleichsbilder und Abnahme der Vorschläge im Nachtrag
  (eigene Kette, eigene DNG-Entwicklung, Hochskalieren gedämpft, Hilfsprozess). Nicht gemessen: 3 × 3, Vertikal und 21er
  Drohnen-Schwenk; die Kugel nur als Horizontstreifen.
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

- Mockup `mockups/28-serien-zusammenfuegen.html` (abgenommen 2026-11-18 mit Abweichungen), Spike 20 `docs/spikes/20-panorama.md` (Spike-Code und
  Vergleichsbilder außerhalb des Repos in `werkzeuge\runs\panorama-spike`)
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

## Nachtrag 0.5.44 — Ergebnis von Spike 20 (von Herbert am 2026-11-18 einzeln entschieden, siehe „Abnahme 2026-11-18“)

[Spike 20](../spikes/20-panorama.md) ist gelb: Die eigene Kette erreicht die Geometrie von Photoshop; nach zwei Runden sind
Handy-1, Handy-2 und Drohne-A nahe gleichwertig, Handy-3 (Felsen in 1–3 m) und Drohne-B (Gegenlicht) etwas schlechter. Runde 2
ist nicht unabhängig nachgeprüft; Herberts Urteil über die Vergleichsbilder steht aus. Vorschläge, die Punkt 3, 6 und 15
ändern würden — sie gelten erst mit der Abnahme:

1. **Werkzeug:** eigene Kette auf OpenCV-Bausteinen (Punkt 3, Option 1). Hugin bleibt nur als Ausweg für den Nahbereich
   im Blick, nicht als Baustein für Stufe 1.
2. **RAW:** Die Raw-Bilderweiterung liefert nativ nur 8 bit und hat keine Entwicklungs-Schnittstelle. Für DNG (die DJI-DNG
   sind unkomprimiert, RGGB) eine eigene 16-bit-Entwicklung in `Lumora.Imaging` ohne neues Paket (Weißabgleich der Serie,
   Demosaik, GainMap, ColorMatrix, Lichter-Rekonstruktion, gemeinsame Belichtung); später das DNG-Kameraprofil. Die
   Raw-Bilderweiterung bleibt Rückfall für andere Rohformate. Stufe 3 in Punkt 19 („RAW/DNG über die
   Raw-Bilderweiterung“) wird damit zu „DNG über die eigene Entwicklung“.
3. **Kugel 360°:** Standard-Ausgabe der Drohne als Horizontstreifen (wie Herberts bisherige Panoramen); 2 : 1 mit gefülltem
   Zenit/Nadir als Wahl. Spitze heute 5–6 GB je Drohnen-Kugel.
4. **Hochskalieren** (Punkt 15): nur nach dem Zusammenfügen, gedämpft gemischt (Modellanteil höchstens 60 % nach lokaler
   Schärfe, weiche Bereiche bikubisch), standardmäßig aus, ClearReality als Standardmodell. Nötig ist ein Kachel-Strom ohne
   Gesamtpuffer, weil `EnhanceImage` bei 10 000 px / 64 MP endet; Obergrenze JPEG 65 535 px je Seite (Drohne 2× geht, 4× nicht).
5. **Hilfsprozess:** OpenCV rechnet in einem eigenen Prozess (native Abstürze in OpenCvSharp, Spitzen von 5–6 GB). Die eigene
   DNG-Entwicklung macht ihn WIC-frei; damit entfällt der Grund, aus dem die Alternative „OpenCV in einem Hilfsprozess“
   zurückgestellt war.
6. **Prüfung des Ergebnisses:** feste Startwerte für RANSAC und eine Plausibilitätsprüfung (Restfehler, Nahtgüte,
   Zuschnitthöhe) vor dem Speichern; ein gekippter Lauf wird wiederholt statt gespeichert.

## Abnahme 2026-11-18 — Entscheidungen von Herbert

Herbert hat dieses ADR und Mockup 28 als Grundlage abgenommen und die offenen Punkte sowie die sechs Vorschläge aus dem
Nachtrag 0.5.44 einzeln entschieden. Wo diese Liste vom Text oben oder vom Mockup abweicht, gilt diese Liste; Mockup 28 wird
beim Bau der betroffenen Stufe nachgezogen. Mit „Detail (Claude)“ markiert ist, was Claude zur Umsetzung ergänzt hat.

**Spike 20 und Nachtrag 0.5.44**

1. **Urteil über die Vergleichsbilder:** Die Geometrie reicht, **Farbe und Kontrast müssen besser werden**. Nachgeschärft wird
   in Stufe 1 selbst, ohne eigene Spike-Runde; die Abnahme von Stufe 1 enthält einen Farb- und Kontrastvergleich gegen
   Photoshop an denselben Serien wie Spike 20. Herbert 2026-11-18 nach den Vergleichsbildern: Farben wie Photoshop
   (Farbverstärkung Standard an), einstellbar und abschaltbar.
2. **Werkzeug:** Die eigene Kette auf OpenCV-Bausteinen ist Standard (Punkt 3, Option 1). **Hugin kommt dazu**, als Wahl im
   Fenster („Mit Hugin rechnen“, zum Vergleich, etwa bei Felsen im Nahbereich) — als zweite Umsetzung von `IPanoramaStitcher`
   in Stufe 3. Hugin (GPL) wird nicht eingebunden, sondern als eigenes Programm aufgerufen; es kommt nur über „Modelle &
   Daten“ nach Bestätigung mit SHA-256 auf den Rechner (ADR-0070), nie ins Repo und nie ins portable Paket. Ohne Hugin ist die
   Wahl ausgegraut mit Hinweis.
3. **RAW:** eigene 16-bit-Entwicklung der DNG wie im Nachtrag, Punkt 2; die Raw-Bilderweiterung bleibt Rückfall für andere
   Rohformate. Stufe 3 heißt damit „DNG über die eigene Entwicklung“.
4. **Kugel 360°:** Horizontstreifen und 2 : 1-Rundum-Bild (Zenit/Nadir gefüllt) stehen **gleichrangig zur Wahl im Fenster** —
   Herbert will jedes Mal wählen können; ersetzt Nachtrag, Punkt 3. Detail (Claude): vorausgewählt ist die zuletzt gewählte
   Form, beim ersten Mal der Horizontstreifen; die Vorauswahl steht auch unter Einstellungen › Zusammenfügen.
5. **Hochskalieren:** wie Nachtrag, Punkt 4 — nur nach dem Zusammenfügen, gedämpft (Modellanteil höchstens 60 %),
   standardmäßig aus.
6. **Hilfsprozess:** angenommen (Nachtrag, Punkt 5) — stürzt das Rechnen ab, läuft Lumora weiter und meldet den Fehler.
7. **Prüfung vor dem Speichern:** angenommen (Nachtrag, Punkt 6) — schlechte Ergebnisse werden wiederholt oder gemeldet, nie
   still gespeichert.

**Offene Punkte**

8. **Vorrang der Serien-Erkennung:** 3, vor Prüfsummen (4) und Vorschaubildern auf Vorrat (5) — bestätigt. Der Nachtrag in
   ADR-0074 folgt mit Stufe 2, wenn sich die Nummern im Code ändern.
9. **Bau-Reihenfolge** (Punkt 19) bestätigt; Stufe 3 enthält zusätzlich Hugin (Nr. 2). Neu: **360°-Rundum-Ansicht** als
   eigene Stufe 3b nach Stufe 3 (2 : 1-Kugelpanoramen in der Lightbox mit der Maus drehen). Die Einzelbild-Werkzeuge
   Geraderichten, Perspektive und Himmel kommen nach Stufe 6 (bleiben bei „Foto verbessern“, Punkt 16 und 20).
10. **Schreibweise** „Zusammenfügen…“ ohne Leerzeichen bestätigt (Punkt 1).
11. **Ergebnisgröße:** **volle Auflösung ist Standard** statt 16K (ändert Punkt 6 „Größe“ und die Folge in „Negativ“). Reicht
    `MergeMemoryBudget` nicht oder wäre eine Kante länger als 65 535 px (JPEG-Grenze), nimmt Lumora die größte passende Stufe
    und sagt es im Fenster; JPEG Q92, TIFF 16 bit auf Wunsch. Die Größenwahl mit Speicherprüfung bleibt in Stufe 3.
12. **Bewertung:** Das Ergebnis übernimmt **auch Sterne und Favorit** (ändert Punkt 10 „Tags“). Detail (Claude): die höchste
    Sternezahl der Quellen, Favorit, wenn mindestens eine Quelle Favorit ist; dazu wie bisher die gemeinsamen Tags.
13. **Quellen einklappen** standardmäßig an — bestätigt (Punkt 9).
14. **Serie zeigen statt Fenster** (Klick auf die Marke) — bestätigt. Da die Marke dem farbigen Rahmen weicht (Nr. 20), gilt
    das für die Titelkachel bei „Eine Kachel je Serie“ und für „Serie zeigen“ im Rechtsklick: Bedingung + Serien-Leiste, das
    Fenster öffnet erst der Knopf.
15. **Ablage bei Drohnen-Ordnern:** neben dem ersten Einzelbild, „eine Ebene höher“ als Wahl in den Einstellungen — bestätigt.
16. **Stapel ohne Einzelvorschau** als Ausnahme erlaubt; die Prüfung vor dem Speichern (Nr. 7) läuft trotzdem.
17. **Start aus einer Story** nur über die Lightbox, Ergebnis nicht automatisch in der Story — reicht.
18. **DJI-Ordner außerhalb der Muster:** Aufnahmeart aus den Gimbal-Winkeln, bei Unsicherheit „Automatisch“ ohne
    Ein-Klick-Vorschlag — bestätigt.
19. **„Augen zu“ beim Gruppenfoto:** 2d106det als eigener Eintrag in „Modelle & Daten“, Laden nur nach Bestätigung —
    einverstanden.

**Neu: farbiger Rahmen je Serien-Art** (Herbert, 2026-11-18; ändert Punkt 9 „Kachel-Marke“)

20. Erkannte Serien sind in Galerie, Liste und Ordner-Zeilen **farblich markiert: jede Kachel einer Serie bekommt einen
    Rahmen in der Farbe ihrer Art** — statt der Marke „Serie · n Fotos“ auf dem ersten Foto und des Punkts auf den übrigen.
    Vorschlag der Farben: Panorama blau, Belichtungsreihe orange, Serienbild grün, Fokusreihe violett, Intervall türkis. Art
    und Zahl („Panorama · 26 Fotos“) stehen im Tooltip; „Serie zeigen“, „Serie mit 26 Fotos zusammenfügen…“ und „Keine
    Serie“ im Rechtsklick. Bei „Eine Kachel je Serie“ trägt die Titelkachel denselben Rahmen zum Stapel-Rand und Text. Die
    Ergebnis-Marke „aus n Fotos“ bleibt.
    Detail (Claude): Der Rahmen liegt innen in der Kachel-Vorlage (kein eigenes Automation-Element, Kacheln bleiben Blätter)
    und ist von der Auswahl-Markierung unterscheidbar; die Farben werden in Hell und Dunkel geprüft und auch für
    Farbschwäche unterscheidbar gewählt (Symbol der Art im Tooltip und in der Legende); zusammengefügte Serien zeigen den
    Rahmen gedämpft; das Flyout „Serien“ bekommt eine Legende. Gebaut mit Stufe 2.

**Weiter offen (nicht Teil dieses ADR):** Sicherung der Fotos selbst (Issue #6, ADR-0069) — Herbert entscheidet später.

## Nachtrag 0.5.46 — Bau Stufe 1 Teil A (Claude; Kette, Hilfsprozess, Merge-Dienst, Speichern)

Teil A baut alles außer der Oberfläche; Teil B (Fenster, Rechtsklick, Aktionsleiste, Marke, Lightbox-Zeile, Suchen ▸,
Rauchtest-Schritt) setzt auf die Schnittstellen unten. Entscheidungen beim Bau, wo der Text oben offen war oder abweicht:

1. **Drei Projekte statt einem.** `Lumora.Imaging` (Merge-Dienst, Vorschau-Sitzung, Rezept, Einstellungen, Client des
   Hilfsprozesses; WPF-frei und **ohne** OpenCV), `Lumora.Imaging.Protocol` (Nachrichten, keine Abhängigkeiten) und
   `Lumora.Imaging.Worker` (Exe ohne Fenster, OpenCvSharp4, einzige Stelle mit nativem OpenCV). Lumora.Wpf baut den
   Hilfsprozess mit und kopiert ihn neben Lumora.exe (`CopyImagingWorker`), referenziert ihn aber nicht — `Lumora.deps.json`
   nennt OpenCV nicht (prüft `build/publish.ps1`, das den Hilfsprozess ins portable Paket veröffentlicht).
2. **Dekodieren im Hilfsprozess (Abweichung von Punkt 2, WIC im Wpf-Projekt).** Der Hilfsprozess liest die Quellen selbst über
   WIC (WPF-Bildklassen, gleiche Ausrichtungslogik wie Lumora; JPEG, HEIC, PNG, TIFF), verkleinert schon beim Dekodieren und
   liest Brennweite KB, Kamera und die DJI-Gimbal-Winkel aus dem Kopf. Grund: sonst wanderten Puffer von 26 × 60 MB über die
   Prozessgrenze, und Lumora hielte die volle Auflösung selbst im Speicher — genau die Spitze, die der Hilfsprozess abfangen
   soll. OpenCV allein kann kein HEIC. Die eigene DNG-Entwicklung (Stufe 3) kommt ebenfalls in den Hilfsprozess.
3. **Protokoll:** eine JSON-Zeile je Nachricht mit Kennung `@lumora` über stdin/stdout (fremde Ausgabe nativer Bibliotheken
   wird überlesen, stderr verworfen — sie kann Pfade enthalten). Fortschritt je Schritt, Abbrechen erst höflich (zwischen Stufen,
   Bildern, Bändern), nach 15 s hart; Absturz oder Ende des Prozesses → Fehler des Vorgangs, Stille über 10 min bzw. 2 h je
   Arbeit → Prozess beendet. Schließt Lumora die Leitung, endet der Hilfsprozess. Prüfhaken (Absturz, Hängen) nur mit
   `LUMORA_IMAGING_WORKER_TEST=1`.
4. **OpenCvRuntime-Regeln im Hilfsprozess:** OpenCL aus über die Umgebung beim Start (`OPENCV_OPENCL_RUNTIME=disabled`,
   `OPENCV_OPENCL_CACHE_ENABLE=0` — die C-Laufzeit liest sie nur einmal, OpenCvSharp 4.13 bindet `setUseOpenCL` nicht an), TEMP,
   TMP und `OPENCV_TEMP_PATH` = `TempRoot\merge` des Katalogs (versteckte Sitzung: im Container), Threads zentral (`--threads`,
   „Im Hintergrund weiterrechnen“ stellt per Nachricht auf `BackgroundCpu.MaxThreads`), **ein** schwerer Lauf zur Zeit
   (`ImagingWorkerHost`). `opencv_videoio_ffmpeg` (≈ 27 MB) wird nicht mitkopiert.
5. **`IPanoramaStitcher` als Sitzung:** `OpenSessionAsync` → `EstimateAsync` (einmal) und `ComposeAsync` (beliebig oft) in
   einem Hilfsprozess, der Quellen (≈ 1 600 px) und Ausrichtung hält; `RenderAsync` rechnet volle Auflösung in einem frischen
   Prozess aus dem Rezept (keine zweite Ausrichtung). Ergebnis ist eine JPEG-Datei im Temp-Ordner des Katalogs statt eines
   `PixelBuffer` (Prozessgrenze). Hugin (Stufe 3) wird eine zweite Umsetzung derselben Schnittstelle.
6. **Kette:** Port von Spike 20 Runde 2 ohne DNG-Teile. Neu: fester RANSAC-Startwert je Bildpaar (`theRNG` ist je Thread),
   Brennweite KB über die Bilddiagonale, Brennweite je Bildgröße, Restverzeichnung k1/k2 **nur mit Brennweite aus dem EXIF**
   (ohne sie tauschen Brennweite und Verzeichnung — im Test „blähten“ sich die Bilder auf; Rückfall bei |k| > 0,3).
   Aufnahmearten: Kugel 360° als Horizontstreifen (längstes voll gedecktes Band, höchstens 80° hoch ≈ 4,5 : 1 wie die
   Photoshop-Ergebnisse) oder 2 : 1 mit einfach gefülltem Zenit/Nadir (Spalte fortsetzen, weich verwischen) und GPano;
   Zylinder mit Mercator-Teilung, wenn der Schwenk über ±45° hoch reicht (DJI 21 Bilder, Handy hoch); Perspektive auf das
   mittlere Bild bei waagrechtem Horizont (bei Bildwinkeln nahe 180° abgelehnt mit Hinweis); Vertikal als Zylinder mit
   waagrechter Achse; Nur verschieben als Ähnlichkeit je Bild aus denselben Treffern. Vorschlag: mit Gimbal nach Punkt 6,
   ohne Kamera im EXIF und flach besser erklärt → Nur verschieben, sonst aus Gier-/Neigungsspanne der Bildmitten.
7. **Prüfung vor dem Speichern (Abnahme Nr. 7):** Restfehler (Median > 0,35 % bzw. 90 % > 1,2 % der Bilddiagonale), gekippte
   Kette (über ein Viertel der laut Ausrichtung stark überlappenden Paare ohne Treffer), Zuschnitthöhe (< 0,45 Bildhöhen),
   Zuschnittfläche (< 55 % der gedeckten Fläche), eingebundene Bilder (< ⅔), Nahtgüte (NCC < 0). Der Hilfsprozess probiert
   bei Ausrichtungsproblemen drei feste Startwerte, die Sitzung bei Problemen des Zusammensetzens zweimal drei weitere;
   bleibt es dabei, zeigt die Vorschau die Gründe und Speichern ist gesperrt — auch das Rendern prüft noch einmal.
8. **Farbe und Kontrast (Abnahme Nr. 1):** neuer Tonwert-Angleich nach dem Überblenden: Helligkeitsverteilung (L*) aller
   Quellen als Ziel, glatte, monotone, steigungsbegrenzte Tonkurve (Stärke 0,75), Buntheit folgt der Helligkeit und wird global
   nur angehoben. Grund (gemessen): Blockausgleich und Überblenden gleichen die Automatik-Belichtung an, das Panorama wird
   flacher als seine Quellen. Danach — auf Herberts Wunsch nach den ersten Vergleichsbildern (Abnahme Nr. 1, Nachsatz) — die
   **Farbverstärkung** (`ColorBooster`): Dynamik in LCh (Zusatz fällt mit der Buntheit, halb bei C* 55) plus gleichmäßige
   Sättigung, angewandt im linearen Licht um die Leuchtdichte (L* und damit Kontrast bleiben), Hauttöne (Farbwinkel um 50°)
   mit 45 % der Wirkung, Grau (C* < 1,5) unverändert, Tiefen/Lichter sanft ausgenommen, weiches Knie an der Farbraumgrenze
   statt Abschneiden — mit Abstand (Kanäle bleiben in sRGB 16…251), weil sonst erst das JPEG (Farb-Unterabtastung 4 : 2 : 0)
   die knapp unter die Grenze geschobenen Farben abschneidet (gemessen bei Abstand 0,75: 5,5 % statt 1,8 % abgeschnittene
   Pixel). Stärke 0–150 %, Standard 100 — ein Parametersatz (gleichmäßig +40 %, Dynamik bis +65 %), an beiden Testserien
   gegen Photoshop auf Buntheit und Mittelfarbe kalibriert (verkleinert, Werkzeug `Harness kalibrieren`), nie je Bild. Die
   erste Variante im sRGB um die Luma traf Photoshop in der Mittelfarbe besser, verschob aber die L*-Verteilung (Handy-2
   1,7 → 2,1) — verworfen. JPEG 4 : 4 : 4 nähme das restliche Abschneiden in den Tiefen weg, macht die Datei aber ≈ 30 %
   größer — nicht gewählt (Q92 4 : 2 : 0 wie bisher). Kennzahlen (Werkzeug und Bilder außerhalb des Repos,
   `werkzeuge\runs\stufe1-vergleich`; volle Auflösung, „Stufe 1“ = Standard 100):

   | Serie | Fassung | L* Std (Kontrast) | C* Mittel | ΔE Mittelfarbe zu Photoshop | Abstand L*-Verteilung zu Photoshop | abgeschnitten (Kanal 0/255) |
   |---|---|---|---|---|---|---|
   | Handy-2 | Quellen | 20,7 | 25,9 | 4,5 | 1,4 | 2,29 % |
   | Handy-2 | Spike Runde 2 | 17,6 | 26,7 | 4,9 | 3,5 | 0,39 % |
   | Handy-2 | Stufe 1 ohne Tonwert-Angleich | 17,7 | 26,1 | 6,2 | 3,3 | – |
   | Handy-2 | Stufe 1, Farbverstärkung aus (0) | 19,7 | 26,2 | 6,0 | 1,7 | 1,83 % |
   | Handy-2 | **Stufe 1 Standard (100)** | **19,7** | **34,1** | **4,4** | **1,7** | **2,52 %** |
   | Handy-2 | Photoshop (bearbeitet) | 21,4 | 35,9 | 0 | 0 | 27,05 % |
   | Drohne-360-A | Quellen (eigene DNG-Entwicklung) | 19,6 | 20,1 | 11,4 | 2,4 | 0,13 % |
   | Drohne-360-A | Spike Runde 2 | 19,0 | 22,5 | 4,8 | 3,9 | 0,01 % |
   | Drohne-360-A | Stufe 1 ohne Tonwert-Angleich | 18,3 | 22,5 | 4,6 | 3,6 | – |
   | Drohne-360-A | Stufe 1, Farbverstärkung aus (0) | 19,0 | 21,6 | 3,6 | 1,5 | 0,05 % |
   | Drohne-360-A | **Stufe 1 Standard (100)** | **19,0** | **31,9** | **2,7** | **1,5** | **0,15 %** |
   | Drohne-360-A | Photoshop (bearbeitet) | 20,2 | 31,7 | 0 | 0 | 23,16 % |

   Ergebnis: Buntheit 95 % bzw. 101 % von Photoshop, Mittelfarbe näher an Photoshop als ohne Verstärkung und als Runde 2,
   L*-Verteilung unverändert. Abgeschnitten wird im Bild selbst nichts; nach dem JPEG liegen die Anteile auf der Höhe der
   Quellen (der Rest entsteht in gesättigten Tiefen durch die Farb-Unterabtastung). Photoshop ist zusätzlich kühler (b* −22
   bzw. −12) — Weißabgleich bleibt „Foto verbessern“. Laufzeit und Speicherspitze (Herberts PC, Release, Hilfsprozess):
   Handy-2 (25 Fotos) ausrichten 20,5 s / 1,43 GB, Vorschau 3,6 s, volle Auflösung 77 MP 42,9 s / 4,4 GB; Drohne-360-A
   (26 Fotos) ausrichten 13,0 s / 1,69 GB, Vorschau 4,8 s, volle Auflösung 169 MP (Horizontstreifen 27 182 × 6 200)
   78,9 s / 5,5 GB. Neu tonen in der Vorschau-Sitzung ohne Ausrichten und Zusammensetzen.
9. **Größe (Abnahme Nr. 11):** volle Auflösung; der Hilfsprozess wählt die größte passende Stufe (1 · 0,9 · 0,8 · … · 0,1),
   wenn das Budget (freier Arbeitsspeicher − 1,5 GB, Schätzung 1,25 × (Quellen + Leinwand × 13,4 B) + 0,3 GB, an Spike 20
   geeicht) oder die JPEG-Kante überschritten wäre, und meldet `size-reduced:memory` bzw. `size-reduced:jpeg-edge`. Grenze
   **65 500 px** statt 65 535: libjpeg in OpenCV schreibt nicht mehr (`JPEG_MAX_DIMENSION`).
10. **RAW in der Auswahl (Stufe 1):** ein RAW mit JPEG-Geschwister im selben Ordner wird durch dieses ersetzt (Hinweis), sonst
    nicht eingebunden (Hinweis „RAW/DNG kommt mit Stufe 3“). Grund: WIC liefert DNG nur als 960 × 640-Vorschau bzw. in 8 bit;
    eine eingebettete Vorschau neben Vollbildern ergäbe ein unbrauchbares Panorama.
11. **Metadaten:** `MergeMetadataPicker` (Services) wählt Zeit, Ort, Kamera und Bewertung einmal für Datei und Katalog.
    `MergeExifWriter` schreibt den EXIF-Block der Zeitquelle (JPEG) mit Ausrichtung 1, **ohne IFD1** (sonst zeigte das
    eingebettete Vorschaubild das erste Einzelbild) und mit den Maßen des Ergebnisses; das GPS-Verzeichnis kommt **ganz** von
    der Ortsquelle (jeder Container, Byte-Reihenfolge angeglichen), sonst Breite/Länge des Katalogs; ist die Zeitquelle kein
    JPEG, entsteht ein neuer Block (Kamera, Aufnahmezeit, Zeitversatz, GPS). XMP: `CreatorTool` Lumora, Verfahren,
    Quellenzahl, GPano bei 2 : 1. Fehlt einer Quelle Zeit/Ort im Katalog (bis eben OneDrive-Platzhalter), liest der Dienst sie
    aus der jetzt lokalen Datei.
12. **`FileLinkUndo` 6/7 und `MergedPhotoUndo`:** Papierkorb des Ergebnisses sichert je Quelle eine Zeile (Id-Folge =
    Position) und die ganze `MergedPhotos`-Zeile (Verfahren, Aufnahmeart, Rezept, Serie, Batch) in der neuen Tabelle
    `MergedPhotoUndo` (Schlüssel `FileOperationId`, Kaskade mit der Operation — die Migration bleibt bei CREATE TABLE, weil
    `FileLinkUndo` keine Spalte dafür hat); die Rückkehr legt `MergedPhotos` daraus neu an und hängt die noch vorhandenen
    Quellen in alter Reihenfolge an. Papierkorb einer Quelle sichert die Verknüpfung, die Rückkehr hängt die Quelle hinten an.
    Beim Rückgängig des CreateMerged-Batches gehen die Verknüpfungen bewusst mit (Ergebnis weg). „Neu aufbauen“ trägt die Ziele
    gesicherter Datei-Verknüpfungen (`FileLinkUndo` 4–7, auch Fassungen) mit und schreibt sie auf die neuen FileIds um; ohne
    Gegenstück verfällt die Sicherung (sonst zeigte sie auf eine fremde Datei). Ins Ergebnis zählen nur die eingebundenen
    Quellen: ausgelassene (`Dropped`) stehen im Rezept, aber nicht in `MergedPhotoSources`, nicht in der Quellenzahl und nicht
    in der Wahl von Zeit, Ort, Kamera und Tags. Eine vorläufige OneDrive-Zeit im Katalog weicht der frisch gelesenen.
13. **Schnittstellen für Teil B:** `IMergeService` (`GetAvailabilityAsync`, `AnalyzeAsync`, `OpenPreviewSession`,
    `Load/SaveSettingsAsync`), `IMergePreviewSession` (`StartAsync`, `UpdateAsync`, `Estimate`, `SaveAsync`,
    `ContinueInBackground`, `Cancel`, `ProgressChanged`, `Current`, `Analysis`), `IMergedPhotoStore` (Services: Marken,
    Quellen, Ergebnisse einer Quelle, Lightbox-Info), `MergeSavedMessage`, `ActivityKind.Merge`,
    `ActivityAction.ShowResult(FileIds)`. Farbverstärkung: `MergeOptions.ColorBoost` (Prozent 0–150, null = zuletzt gewählt),
    `MergeSettings.ColorBoost` (Einstellung `merge.colorBoost`, erstes Mal 100), `IMergeSettingsStore.RememberColorBoostAsync`,
    Rezept-Feld `colorBoost`. Schließen der Sitzung bricht eine laufende Vorschau ab; ein laufendes Speichern rechnet zu Ende.

## Nachtrag 0.5.47 — Bau Stufe 1 Teil B (Claude; Fenster, Menüs, Marke, Lightbox, Einstellungen, Fix Zuschnitt)

Teil B baut die Oberfläche auf den Schnittstellen aus Teil A (Nachtrag 0.5.46 Nr. 13). Entscheidungen beim Bau, wo der Text
oben offen war oder abweicht:

1. **Fix Zuschnitt „Gerade Kanten“.** Ursache der hellen Flecken links und rechts an der unteren Kante (Handy-Schwenk): der
   Zuschnitt aus Spike 20 dehnte die Deckungsmaske um 3 % der Höhe, schloss sie mit 5 × 5 und füllte die so gewonnenen Ränder
   per Inpaint (¼ Maßstab, weich hochgerechnet); beim Horizontstreifen galten Zeilen mit 97 % Deckung als voll. Jetzt kommt das
   Rechteck aus der **echten Deckungsmaske nach dem Überblenden** (ohne Schließen, ohne Dehnen) abzüglich eines Sicherheitsrands
   von 0,05 % der kurzen Seite (mindestens 2 px; beim 360°-Streifen links/rechts übergreifend): größtes Rechteck auf voll
   gedeckten Blöcken (≤ 1/8 verkleinert), danach pixelgenau nachgezogen, bis kein ungedecktes Pixel mehr darin liegt;
   Horizontstreifen nur aus voll gedeckten Zeilen; nichts wird gefüllt. Bleibt nichts übrig, zeigt die Vorschau ungeschnitten und
   die Prüfung meldet „zu wenig Bildhöhe“. Neues Maß `QualityCheck.UncoveredPixels` (ungedeckte Pixel im Zuschnitt vor jedem
   Füllen; „Gerade Kanten“ = 0). Tests (synthetisch): welliger Schwenk mit Roll- und Neigungsfehlern — „Lücken füllen“ > 1 000
   ungedeckte Pixel, „Gerade Kanten“ 0 und kein schwarzes Pixel an den vier Kanten; Kanten-Nachziehen mit Ecke, Kerbe und Loch
   (ein Loch im Inneren kostet nur den Streifen um dieses Loch, verstreute Löcher werden einzeln abgeschnitten);
   Horizontstreifen der Kugel 0. Nachgerechnet mit dem Harness (volle Auflösung, Kennzahlen außerhalb des Repos in
   `werkzeuge\runs\stufe1-vergleich`, alter Stand unter `vor-fix-0.5.46`; Ecken vergrößert geprüft):

   | Serie | Größe vorher → nachher | ungedeckt im Zuschnitt | L* Std | C* Mittel | ΔE zu Photoshop (Standard / aus) |
   |---|---|---|---|---|---|
   | Handy-2 | 14 102 × 5 462 → 13 892 × 5 069 (70 MP) | 0 | 19,7 | 34,0 | 4,4 / 6,1 |
   | Drohne-360-A | 27 182 × 6 200 → 27 182 × 6 172 (168 MP) | 0 | 19,0 | 31,8 | 2,8 / 3,7 |

   Farbe und Kontrast bleiben praktisch gleich (vorher 19,7 / 34,1 / 4,4 bzw. 19,0 / 31,9 / 2,7); Handy-2 verliert die gefüllten
   Ränder (≈ 7 % Höhe), die Drohne 28 Zeilen.
2. **Bühne mit Zuschnitt-Rahmen und schraffierten Lücken.** Bei „Gerade Kanten“ schreibt der Hilfsprozess zur Vorschau
   zusätzlich das ungeschnittene Panorama (Lücken grau schraffiert) und die Lage des Zuschnitts (`ComposeRequest.FramePath`,
   `ComposeResult.Frame`, `MergePreview.Frame`); die Bühne zeigt es abgedunkelt mit Rahmen und legt die getönte Vorschau in den
   Zuschnitt — so wirkt der Regler „Farbe kräftiger“ sofort, der Rahmen bleibt beim Neu-Tonen. „Ungeschnitten“ zeigt die
   Lücken in der Vorschau schraffiert; gespeichert wird nie mit Schraffur. Zoom/Verschieben über das `ZoomPanControl` der
   Lightbox.
3. **Fenster** (`Wpf/Merge/MergeWindow`, `MergeViewModel`): eines je Sitzung, nicht modal, Owner das Hauptfenster (überlebt die
   Lightbox); ein neuer Start ersetzt ein offenes Fenster, solange es nicht speichert. Haken im Filmstreifen starten nach kurzer
   Pause eine neue Vorschau-Sitzung (neu ausrichten; bis dahin ist Speichern gesperrt); Aufnahmeart, Kugel-Form, Zuschnitt und Belichtung setzen in derselben
   Sitzung neu zusammen; der Regler tont nur neu (`UpdateAsync` mit gleicher Geometrie) und merkt die Stärke sofort
   (`SaveSettingsAsync`), die Kugel-Form ebenso. Schließen während des Speicherns wirkt wie „Im Hintergrund weiterrechnen“.
   Rückgängig in der Fertig-Ansicht führt zurück zur Vorschau (nochmals speichern geht).
4. **„Warum dieser Vorschlag“** in Stufe 1 ohne Klassifizierer (der kommt mit Stufe 2): Belege aus der Auswahl (Zahl,
   Zeitspanne, Kamera) und nach der Vorschau aus der Ausrichtung (eingebunden, vorgeschlagene Aufnahmeart, Gimbal, geschätzte
   Brennweite, Photometrie).
5. **Größe:** volle Auflösung als Standard; ist sie nicht machbar, steht sie ausgegraut mit Grund und Zahl, darunter die größte
   passende Stufe (aus `IMergePreviewSession.Estimate`). Keine Größenwahl, Quelle immer JPEG (Stufe 3); Hochskalieren
   ausgegraut „kommt später“.
6. **Start-Wege:** „Bearbeiten ▸“ direkt nach „… verbessern…“: „Zusammenfügen…“ bzw. „n Fotos zusammenfügen…“ in Kachel,
   Listenzeile und Lightbox; Aktionsleiste ab 2 gewählten. Gesperrt mit Grund im Tooltip bei gemischten Bibliotheken, nur
   Videos/Archiv-Einträgen/fehlenden Dateien, fehlendem Rechen-Prozess (bekannt nach dem ersten Start — die Prüfung startet
   einen Hilfsprozess und läuft deshalb nicht beim App-Start) und während ein Speichern läuft. „Serie mit n Fotos…“ kommt mit
   Stufe 2. Die Lightbox bekommt das Album der Galerie über `ILightboxService.SetAlbumContext` vor `Open` (nur die Galerie setzt
   es; aus Story oder Orte geöffnet ohne Album).
7. **Ergebnis-Marke** „Panorama · aus n Fotos“ als Knopf über der Endung (über „2 Fassungen“), je Seite gebündelt
   (`IMergedPhotoStore.GetMarksAsync`), nach `MergeSavedMessage` nachgetragen; Tooltip nennt nicht mehr vorhandene Quellen.
8. **Lightbox:** eigene Zeile unter dem Bild (eigene Zeile des Fensters statt im Overlay: kollidiert nicht mit dem
   OneDrive-Hinweis und der Video-Leiste), Info-Abschnitt „Zusammengefügt“ (Verfahren, Aufnahmeart samt Form, Zuschnitt, Farbe
   kräftiger, Quelle, Quellen davon nicht mehr vorhanden, erstellt mit). „Quellen zeigen“ öffnet Ergebnis und Quellen ab dem
   ersten Einzelbild, „Neu berechnen…“ das Fenster mit den noch vorhandenen Quellen und den Optionen aus dem Rezept.
9. **Suchen ▸** „Quellen“ (Bedingung „Quellen von …“, Ergebnis eingeschlossen) bzw. „Zusammengefügtes Ergebnis“ (Bedingung
   „zusammengefügt aus …“). **Aktivität:** Knopf „Öffnen“/„Ergebnisse zeigen“ in Liste und Meldung
   (`IActivityNavigator.ShowFiles`; ein Ergebnis öffnet mit seinen Quellen).
10. **Einstellungen › Zusammenfügen** (Reiter nach „Verbessern“, `NavigationTags.SettingsMerge`): Farbe kräftiger, Kugel-
    Vorauswahl, Ablage (neben dem ersten Einzelbild | Unterordner; Drohnen-Ordner eine Ebene höher), Namenszusatz,
    JPEG-Qualität, Tags und Sterne/Favorit, Arbeitsspeicher, Zustand des Rechen-Prozesses; Serien, Stapel, RAW/TIFF und Hugin als
    „kommt später“.
11. **Versteckte Sitzung / OneDrive:** Vorschau, Rahmen und Ergebnis liegen im Temp-Ordner des Katalogs (Container) und werden
    ohne Dateisperre in den Speicher geladen; „Im Explorer zeigen“ ist gesperrt. Platzhalter: Rückfrage „Herunterladen und
    fortfahren“ / „Ohne diese Fotos“ / Abbrechen (schließt das Fenster); Filmstreifen-Vorschaubilder von Platzhaltern nur aus dem
    Cache.
12. **Abweichungen vom Mockup 28** (nachgezogen in den Ansichten 4, 5, 11, 14, 15): keine Wahl JPEG/RAW (Stufe 3), kein Schalter
    „Horizont gerade“ (Wellenkorrektur immer an), keine Überlagerungen „Nähte“/„Raster“ (die Vorschau hat keine Nahtdaten) —
    stattdessen der Schalter „Zuschnitt und Lücken“; Filmstreifen ohne Rollen („R2 · 45°“), nur Uhrzeit bzw. Abstand zum
    gestarteten Foto; kein Knopf „Neues Zusammenfügen“ in der Fertig-Ansicht.
13. **Abnahme-Läufe Stufe 1 (Punkt 19):** Handy-Schwenk (Handy-2, 25 Fotos) ergibt Vorschau und Panorama (oben). Für den
    9-Bilder-Weitwinkel (3 × 3) und den Vertikal-Schwenk gibt es in Spike 20 keine Quellen (dort nur Handy-1 bis -3 und die
    Drohnen-Kugeln A/B; „nicht gemessen: 3 × 3, Vertikal“) — dieser Abnahme-Lauf steht aus. Rauchtest-Schritt
    `SmokeScenario.Merge.cs` mit zur Laufzeit gezeichneten, überlappenden Bildern (keine externen Assets).

## Nachtrag 0.5.48 — Befunde der Prüfung (Claude; Entscheidungen beim Beheben)

Eine unabhängige Code-Prüfung von Stufe 1 fand 19 Befunde und zwei unsichere Stellen; behoben in 0.5.48 (Liste im CHANGELOG).
Entscheidungen, wo der Text oben offen war oder sich ändert:

1. **Sitzungsende wartet auf ein Speichern.** Zusperren, Wechsel normal ↔ versteckt, Verschieben des Containers und Beenden
   schließen die Dienste der Sitzung bzw. hängen den Container aus — ein Rechen-Prozess mit offenen Quellen und Arbeitsordner im
   Container darf dabei nicht weiterlaufen, und das fertige Ergebnis darf nicht still verloren gehen. Speichert
   „Zusammenfügen“ (auch nach „Im Hintergrund weiterrechnen“ ohne Fenster): **automatisches Zusperren verschiebt sich** (wie bei
   einem laufenden Scan), **von Hand fragt Lumora „Warten / Abbrechen und …“** (`MergeSessionGuard` über `IDialogService`;
   „Zurück“ lässt die Sitzung offen); Beenden aus dem Infobereich fragt ebenso, das Hintergrund-Ende wartet still. Danach
   beendet `MergeService.DisposeAsync` alle Sitzungen — auch ein Speichern — und wartet, bis ihre Hilfsprozesse weg sind,
   **bevor** die Dienste entsorgt werden bzw. ausgehängt wird. Schließen nur des Fensters rechnet weiterhin zu Ende (Punkt 12).
2. **Optionen gehören zur gezeigten Vorschau.** `IMergePreviewSession.Options` ändert sich erst mit einer fertigen Vorschau;
   gespeichert wird genau diese (Optionen, Aufnahmeart, Ausrichtung — auch im Rezept). Ein abgebrochenes Umstellen sperrt
   „Speichern“, bis die Vorschau wieder zu den Einstellungen passt; neu gerechnet wird nach einem Abbruch nur durch eine
   Änderung oder den Knopf „Vorschau rechnen“, nie von selbst.
3. **Farbprofile:** Quellen mit eingebettetem Nicht-sRGB-Profil (Display P3, Adobe RGB …) rechnet der Hilfsprozess beim Lesen
   per WIC nach sRGB um; sRGB-Profile und Quellen ohne Profil bleiben unverändert. Das Ergebnis trägt kein Profil und
   `ColorSpace` = 1 (sRGB) im EXIF.
4. **Ort:** maßgeblich ist der Katalog-Ort der Ortsquelle. Rang aus Herkunft **und** Genauigkeit (ergänzt Punkt 10): echtes GPS
   aus der Datei (genau) vor in Lumora gesetztem bzw. übernommenem Ort (gleich welche Genauigkeit die Zeile noch nennt — „Ort
   setzen…“ lässt sie stehen) und unbewertetem GPS, das vor geschätztem. Das ganze GPS-Verzeichnis der Datei (Höhe, GPS-Zeit)
   wird nur übernommen, wenn die Datei genau diesen Ort trägt; sonst (in Lumora gesetzt, nur im Katalog verschoben,
   XMP-Begleitdatei) entsteht es aus dem Katalog-Ort (Breite/Länge). Frisch gelesene Orte berücksichtigen wie der Katalog die
   XMP-Begleitdatei.
5. **Zeitversatz:** `OffsetTimeOriginal` nur, wenn die Zeitquelle selbst einen nennt (`ExifTimeOffset` bzw. frisch gelesen) —
   nie die Zeitzone dieses PCs.
6. **XMP-Namensraum** der Lumora-Felder neutral: `urn:lumora:merge:1.0` (bis 0.5.47 eine Adresse mit Kontonamen).
7. **Rückgängig und Verknüpfungen** (ändert Nachtrag 0.5.46 Nr. 12 „gehen bewusst mit“): Rückgängig eines Ergebnisses bzw. einer
   Fassung legt nur die Datei dieses Vorgangs in den Papierkorb (FileId, Pfad, Größe und Änderungszeit müssen passen — ein
   späteres Ergebnis am frei gewordenen Namen bleibt) und sichert ihre Verknüpfungen zur Operation: holt jemand die Datei aus dem
   Papierkorb zurück, kommen sie mit. Datei-Verknüpfungen (`FileLinkUndo` 4–7) zu einer Datei, die selbst noch fehlt, bleiben
   offen; kehrt eine Datei mit neuer Id zurück, werden offene Verknüpfungen auf sie umgeschrieben und nachgeholt — die
   Reihenfolge der Rückkehr ist egal. Der Abzug der Katalogzeile (Ordner in den Papierkorb, Fehlende entfernen, Aus dem Katalog
   entfernen/Ausschließen, Doppelte aufräumen) nimmt die Quellen eines Ergebnisses mit (Enkel an `MergedPhotos.FileId`).
8. **Vorauswahl:** das Fenster merkt nur die eigenen Schlüssel (Farbe kräftiger, Kugel-Form), nie die übrigen Einstellungen;
   „Neu berechnen…“ ändert die Vorauswahl erst, wenn der Benutzer den Regler bewegt bzw. speichert.
9. **Hilfsprozess:** die Thread-Zahl erreicht OpenCV nur zwischen zwei Arbeiten (`cv::setNumThreads` baut den Thread-Pool ohne
   Sperre neu); „Im Hintergrund weiterrechnen“ wirkt sofort auf die eigenen parallelen Abschnitte und senkt die
   Prozess-Priorität. Abstürze: `SetErrorMode` (kein Fehlerbericht-Dialog), verwaltete Ausnahme ohne Fänger → sofort
   `TerminateProcess`, keine Absturz-Abbilder der .NET-Laufzeit (Umgebung des Kindprozesses). **Restrisiko:** ein nativer
   Absturz in OpenCV beendet die .NET-Laufzeit per Fast-Fail; ein eigener Filter kommt dabei nicht zum Zug (gemessen, daher
   nicht eingebaut). Der Prozess endet ohne Dialog sofort, die Windows-Fehlerberichterstattung kann aber einen Bericht außerhalb
   des Containers ablegen (PLAN Kap. 15) — ohne Registry- oder Systemeinstellung nicht abschaltbar.
10. **Verfügbarkeit:** gemerkt wird nur „Programmdatei fehlt“ bzw. „OpenCV geladen“; ein einmaliger Startfehler wird beim
    nächsten Versuch neu geprüft (`MergeAvailability.Transient`) und sperrt das Menü nicht — das Fenster nennt den Grund.
