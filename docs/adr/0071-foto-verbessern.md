# ADR-0071: Foto verbessern — Ergebnis immer als neue, verknüpfte Datei; Modelle über „Modelle & Daten“

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Claude (autonom, Issue #17, nach dem von Herbert abgenommenen Mockup 13)
- **Bezogen auf:** [ADR-0070](0070-modelle-und-daten.md) (Update-Manager), [ADR-0012](0012-operation-logging.md) (OperationBatch),
  [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (FileLinkUndo), [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md) (DirectML, Gesichter),
  [ADR-0052](0052-motive-siglip2-lokal.md) (IGpuGate), [ADR-0067](0067-aktivitaets-anzeige.md) (Aktivitäts-Anzeige),
  [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Sitzung), [ADR-0068](0068-galerie-ordnerleiste-titelbild-kontextmenue.md) (Rechtsklick-Menü),
  [ADR-0072](0072-nutzungs-signale.md) (Nutzungs-Signale)

## Kontext

Welle 8 (Mockup 13) bringt KI-Werkzeuge zum Aufbessern alter und kleiner Fotos: hochskalieren, Gesichter restaurieren,
einfärben, Störendes und Kratzer entfernen, dazu ohne Modell zuschneiden, drehen und Licht. Die Werkzeuge laufen lokal über
ONNX Runtime (DirectML wie Gesichter/Motive). Harte Vorgaben: das Original bleibt immer unverändert; Modelle werden nur nach
Bestätigung geladen und nie ins Repo oder Paket gelegt; in der versteckten Sitzung entsteht nichts außerhalb des Containers;
jede Datei-Operation ist protokolliert und rückgängig machbar. Die Modell-Dateien selbst durften für diese Arbeit nicht
heruntergeladen werden (nur Metadaten), echte Läufe ließen sich also nicht ausprobieren.

## Entscheidung

1. **Ergebnis = neue Datei daneben** (`<Name>_verbessert.jpg`, Muster und Ort — neben dem Original oder in einem Unterordner —
   aus Einstellungen › Verbessern; belegte Namen bekommen „ (2)“). Die Datei-Operation `CreateVariantsAsync`
   (`FileOperationService.Variants.cs`, `OperationType.CreateVariant = 15`) verschiebt die fertige Zwischendatei ans Ziel
   (Dateisystem-Schritt), bucht danach Files-Zeile, FTS, Ordnerkette und die Verknüpfung; Ziele außerhalb der Wurzeln der
   Bibliothek des Originals werden abgelehnt. **Rückgängig** = neue Datei in den Papierkorb, Katalogzeile samt Verknüpfung weg.
2. **Fassungen-Beziehung** als neue Tabelle `PhotoVariants (FileId PK → Files, OriginalFileId → Files, beide ON DELETE CASCADE,
   Tools, CreatedAtUtc, BatchId)`, Migration `AddPhotoVariants` (nur CREATE TABLE). Mitnahme bei „Neu aufbauen“
   (`UserDataCarryOver.Variants.cs`), bei Papierkorb/Verschieben nach außen über `FileLinkUndo` (Arten 4 = Fassung von,
   5 = hat Fassung), bei „Fehlende/Ausgeschlossene entfernen“ automatisch über den Katalog-Abzug (Kaskaden-Kinder).
3. **Metadaten:** Aufnahmedatum (samt Quelle/Versatz), Ort, Kamera aus der Katalogzeile des Originals; bei JPEG-Originalen
   zusätzlich der EXIF-Block Byte für Byte (`JpegExifCopier`, Ausrichtung auf 1, da die Pixel schon gedreht sind). Favorit,
   Sterne und Tags nach Einstellung (Standard an). Personen nicht — die Gesichtserkennung liest die neue Datei selbst.
4. **Modelle** als vier Einträge in „Modelle & Daten“ (`EnhanceModelCatalog`, `AddFileSetModelPackage`), Adressen auf einen
   festen Commit, Größe + SHA-256 aus den LFS-Metadaten: Real-ESRGAN x4plus (BSD-3, 69,5 MB, 2× = 4× + Verkleinern),
   GFPGAN v1.4 (Apache-2.0, 340 MB, findet Gesichter mit buffalo_l), DeOldify artistic (MIT, 255 MB; nur die Farbe, Helligkeit
   aus dem Original), LaMa big-lama fp32 (Apache-2.0, 208 MB, 512 × 512 um die Maske). ONNX-Fassungen von facefusion bzw. Carve
   (Hugging Face). **CodeFormer weggelassen** (S-Lab-Lizenz, nur nicht-kommerziell); **DeOldify statt DDColor** (DDColor
   Apache-2.0, aber 980 MB). Bereitschaft nur über `IModelPackageManager.GetStatusAsync`; „Laden…“ in Leiste und Einstellungen
   über `ModelDownloadDialog.AskAsync` + `InstallAsync`. Fehlt ein Modell eines gewählten Werkzeugs: `EnhanceModelMissingException`,
   es wird nichts berechnet und nichts geschrieben; „Automatisch“ überspringt fehlende Modelle mit Hinweis.
5. **Rechnen:** `IEnhanceEngine` je Werkzeug (`OnnxEnhanceEngineFactory`: DirectML mit Rückfall CPU, Ein-/Ausgabenamen und feste
   Maße aus dem Modell, Wertebereich 0…1/0…255 am Maximum erkannt), Grafikkarte je Schritt über `IGpuGate`. Bilder lesen/
   schreiben über `IEnhanceImageCodec` (WIC im Wpf-Projekt). Reihenfolge: Entfernen → Drehen/Zuschnitt → Einfärben → Gesichter →
   Licht → Hochskalieren (Ergebnis höchstens 64 MP). „Automatisch“ nach festen Regeln (`EnhanceAutoPlanner`: ohne Farbe →
   Einfärben + Kratzer; Augenabstand < 80 px → Gesichter; längste Kante < 2000 px → Hochskalieren 2×, < 900 px 4×).
6. **Oberfläche:** Lightbox-Leiste „Verbessern“ (Taste E, Knopf, Rechtsklick „Bearbeiten ▸ Foto verbessern…“) mit Zustand je
   Werkzeug, Vorschau = volles Ergebnis als JPEG im Temp-Ordner des Katalogs, Bühne mit Vorher/Nachher-Regler bzw. Pinsel
   (`EnhanceStageOverlay`), „Als neue Datei speichern“ / „Verwerfen“; mehrere Fotos aus der Galerie per Rechtsklick → Dialog mit
   Zeitschätzung → Lauf als Vorgang „Foto verbessern“ in der Aktivitäts-Anzeige (Abbrechen; fertige werden in EINEM Batch
   gespeichert, Rückgängig am Vorgang); Galerie-Marke „2 Fassungen“ (öffnet Original + Fassungen in der Lightbox) und Einstellung
   „beste Fassung zeigen“ (Original mit vorhandener Fassung ausgeblendet, im Ordnerfilter nur bei Fassung im selben Ordner);
   Einstellungen › Verbessern (Modelle, Ort, Dateiname, Metadaten, beste Fassung, Grafikkarte).
7. **Versteckte Sitzung:** Zwischenstände nur in `ICatalogStoragePaths.TempRoot` (Container), Ergebnis neben dem Original
   (Container), Modelle aus dem normalen Modellordner (ADR-0070).
8. Gespeicherte Fassungen melden die Verwendung des Originals an die Nutzungs-Signale (`UsageReport.Used`).

## Begründung

Eine neue Datei statt Überschreiben macht jedes Ergebnis verlustfrei rückgängig und lässt Original und Fassung nebeneinander
stehen; die eigene Tabelle hält die Beziehung über alle Datei-Operationen und „Neu aufbauen“, ohne bestehende Tabellen
umzubauen. Der Weg über die vorhandene Datei-Operation nutzt Scan-Sperre, eigene Pfade, Protokoll und Aktivitäts-Anzeige
unverändert. Die Modelle als gewöhnliche Einträge im Update-Manager bekommen Bestätigung, SHA-256, Fortschritt, Prüfen und
Entfernen ohne eigenen Code. Feste Commits und Prüfsummen verhindern, dass eine geänderte Herstellerdatei ungeprüft ankommt.

## Konsequenzen

### Positiv
- Original bleibt Byte für Byte gleich; jede Fassung ist eine eigene Datei mit Katalogzeile, Tags, Datum, Ort.
- Fehlende Modelle sind sichtbar („Modell fehlt“ mit „Laden…“, „braucht Gesichtserkennung“), nie ein stiller Download.
- Tests und Rauchtest laufen vollständig mit Fake-Prozessoren.

### Negativ
- Die ONNX-Pfade sind ohne die Modelle nicht ausprobiert (Vor-/Nachverarbeitung nach den Vorlagen von Real-ESRGAN, GFPGAN,
  DeOldify, LaMa bzw. facefusion umgesetzt) — beim ersten echten Lauf prüfen (CHANGELOG „Offen“).
- Fassungen belegen zusätzlichen Platz; JPEG ist das einzige Ausgabeformat (EXIF-Übernahme nur von JPEG-Originalen).
- Die Zeitschätzung beruht auf groben Annahmen je Megapixel, nicht auf Messungen.
- „Beste Fassung zeigen“ prüft nicht, ob die Fassung die übrigen Filter erfüllt: mit „Sterne/Tags übernehmen“ aus und
  einem Filter wie „Favoriten“ kann ein Foto ganz aus der Ansicht fallen; mit Unterordner-Ablage und Ansicht „mit
  Unterordnern“ stehen beide da. Fassung einer Fassung hängt am ursprünglichen Original (eine Ebene).
- Rückgängig legt die Datei zuerst in den Papierkorb und bucht dann den Katalog (wie „In den Papierkorb“, ADR-0012):
  scheitert die Buchung, bleibt die Zeile bis zum nächsten Einlesen stehen.

### Neutral
- Migration `AddPhotoVariants`, `OperationType.CreateVariant = 15`, `ActivityKind.Enhance = 17`, `FileLinkKind` 4/5.
- Keine neuen Pakete (ONNX Runtime DirectML war schon da).
- Bildgenerierung (ComfyUI) bleibt nur gemerkt; „Original überschreiben“ gibt es bewusst nicht.

## Alternativen

- **Original überschreiben mit Sicherung:** spart Platz, widerspricht aber der Vorgabe und erschwert Rückgängig über Jahre → verworfen.
- **Fassung nur als XMP-/Sidecar-Verweis ohne Katalogtabelle:** keine Abfrage für Marke/„beste Fassung“ möglich → verworfen.
- **CodeFormer für Gesichter:** oft schöner, aber nur nicht-kommerziell lizenziert → weggelassen.
- **DDColor zum Einfärben:** Apache-2.0, aber fast 1 GB → DeOldify artistic (MIT, 255 MB).
- **Eigenes Download-Werkzeug je Modell:** doppelter Code zu ADR-0070 → verworfen.

## Referenzen

- Mockup `mockups/13-foto-verbessern.html` (abgenommen 2026-10-23), GitHub-Issue #17
- Code: `src/Lumora.Services/Enhance/`, `src/Lumora.Services/Operations/FileOperationService.Variants.cs`, `src/Lumora.Wpf/Enhance/`,
  `src/Lumora.Wpf/Lightbox/LightboxViewModel.Enhance.cs`, `src/Lumora.Wpf/Gallery/ViewModels/GalleryViewModel.Enhance.cs`,
  Rauchtest `Shell/Smoke/SmokeScenario.Enhance.cs`
- Modell-Quellen: Hugging Face `facefusion/models-3.0.0` (Commit 728b965…), `Carve/LaMa-ONNX` (Commit c3c0c9e…)

## Nachtrag 0.5.6 — Natürlichkeit und Körnung beim Hochskalieren (Herbert, 2026-10-26)

**Befund:** Gesichter gelingen gut (GFPGAN), Haut und Körper wirken nach dem Hochskalieren mit Real-ESRGAN x4plus „zu glatt
und künstlich“ — das allgemeine Modell hält Poren und feine Struktur für Rauschen.

**Entscheidung:**
- Nach dem Hochskalieren mischt `EnhanceImageOps.Naturalize` das bilinear vergrößerte Bild vor dem Modell zurück
  (**Natürlichkeit**, Anteil 0…1, Regler bis 80 %) und legt wahlweise eine **Körnung** darüber: gleiche Abweichung für R,
  G und B (nur Helligkeit), gewichtet mit `4·l·(1−l)` (am stärksten in Mitteltönen, null in reinem Schwarz/Weiß), höchstens
  ±12 Stufen. Das Rauschen hängt nur von Position und FileId ab — dieselbe Vorschau ergibt dieselbe Datei.
- Gilt für „Hochskalieren“ und für „Automatisch“, wenn es hochskaliert; nicht für Gesichter (dort gibt es die Stärke).
- `EnhanceOptions.Naturalness`/`Grain` (null = Einstellung), `EnhanceSettings.Naturalness = 0,35`, `Grain = 0,2`
  (Settings `enhance.naturalness`, `enhance.grain`, invariant, begrenzt auf 0…1). Die Leiste liest sie beim Öffnen und
  merkt geänderte Regler beim Berechnen; der Stapel nimmt die Einstellung.

**Offen:** Ein zweites Hochskalier-Modell mit erhaltener Struktur (`realesr-general-x4v3`) — die Quelle für eine
ladbare ONNX-Datei mit festem Commit und Prüfsumme ist noch zu entscheiden (keine offizielle ONNX-Fassung bei facefusion).
