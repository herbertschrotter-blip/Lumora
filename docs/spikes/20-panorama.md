# Spike #20 — Panorama: eigene Kette auf OpenCV-Bausteinen gegen Photoshop

- **Status:** 🟡 Gelb — Geometrie auf Photoshop-Niveau, Bild bei 3 von 5 Serien nahe gleichwertig; Nahbereich-Parallaxe
  beim Handy und die Rohfarbe der Drohne offen
- **Datum:** 2026-11-17
- **Bezogen auf:** [ADR-0078](../adr/0078-serien-und-zusammenfuegen.md) (Serien und Zusammenfügen, Punkt 3
  Panorama-Werkzeug, Punkt 6 Aufnahmearten, Punkt 15 Hochskalieren), [ADR-0071](../adr/0071-foto-verbessern.md)
  (Hochskalier-Modelle, Grenzen von `EnhanceImage`)

## Zweck

ADR-0078 lässt das Panorama-Werkzeug offen, bis feststeht: Reicht eine eigene Kette auf OpenCV-Bausteinen (OpenCvSharp)
an die Panoramen heran, die Herbert bisher mit Photoshop/Lightroom gemacht hat — bei Kugel-Panoramen einer Drohne und
bei Handy-Schwenks? Wie lange dauert es, wie viel Speicher braucht es, und lässt sich das Ergebnis hochskalieren?

## Aufbau

- Prüfprogramm in C# außerhalb des Repos (`werkzeuge\runs\panorama-spike\PanoSpike`, .NET 10, OpenCvSharp4
  4.13.0.20261107 mit `runtime.win`, ONNX Runtime DirectML 1.24.4), etwa 4 900 Zeilen mit Mess- und Vergleichscode.
- Kette: SIFT-Merkmale und Abgleich aus OpenCV; Bündelausgleich (Strahlfehler, Levenberg-Marquardt, Huber, k1/k2),
  Projektion, Belichtungsausgleich, Nahtsuche (Graph-Cut), Entgeisterung und Mehrband-Überblendung selbst gebaut, weil
  OpenCvSharp die Stitching-Detail-Klassen nicht anbindet bzw. dort abstürzt.
- Fünf Serien einer Wanderung, je mit einem fertigen Photoshop/Lightroom-Panorama derselben Bilder zum Vergleich:

  | Serie | Bilder | Aufnahme | Projektion |
  |---|---|---|---|
  | Handy-1 | 11 JPEG quer, zwei Reihen | Schwenk ≈ 182° | Zylinder |
  | Handy-2 | 25 JPEG hoch | Schwenk ≈ 282° | Mercator |
  | Handy-3 | 46 JPEG hoch | freihändig 360°, Felsen in 1–3 m | Mercator |
  | Drohne-A, Drohne-B | je 26 DNG (8 × 3 Reihen + 2 Nadir), 20 MP, 16 bit | Kugel 360° (DJI-Automatik) | Kugel, ausgegeben als Horizontstreifen +33° … −45° |

- Zwei Runden: Runde 1, dann eine **unabhängige Bewertung** (eigener Agent, Bildvergleich an je drei Nähten 1:1), dann
  Runde 2 mit deren Vorschlägen. Runde 2 hat nur der bauende Agent selbst eingeschätzt; Claude hat Übersicht und
  Nahtausschnitte stichprobenartig angesehen. **Herberts Urteil steht aus.**
- Die Photoshop-Ergebnisse hat Herbert stark nachbearbeitet (Sättigung, Kontrast, Schärfe). Geometrie und Nähte sind
  fair vergleichbar, Farbe, Tonwert und Schärfe-Zahlen nur eingeschränkt.
- Rechner: siehe Spike 02 (GTX 1080 Ti für das Hochskalieren). Jede Serie in einem eigenen Prozess gemessen
  (`PeakWorkingSet64`). Keine Dateinamen, Orte oder Bildinhalte im Repo.

## Ergebnis

**Urteil je Serie** (gegen Photoshop):

| Serie | Runde 1 (unabhängige Bewertung) | Runde 2 (Einschätzung des Agenten) |
|---|---|---|
| Handy-1 | etwas schlechter | nahe gleichwertig |
| Handy-2 | etwas schlechter | nahe gleichwertig |
| Handy-3 | etwas schlechter | etwas schlechter (Parallaxe-Streifen an einer Naht) |
| Drohne-A | etwas schlechter | nahe gleichwertig |
| Drohne-B | deutlich schlechter | etwas schlechter (Gegenlicht weich, Farbe flacher) |

- **Geometrie** auf Photoshop-Niveau (unabhängige Bewertung Runde 1): Horizont, Nordrichtung und geschlossene
  360°-Panoramen stimmen, kein Bild fehlt, keine groben Fehlregistrierungen.
- **Runde 1 hatte sichtbare Geisterkanten** an fünf der bewerteten Nähte. Ursachen: zu grobe Nahtsuche (0,1 MP, nur
  Farbe), Restfehler 4–6 px (90 % bis 19 px), einkanaliger Belichtungsausgleich, weiche Rohentwicklung über WIC.
  Runde 2 beseitigt sie bis auf die Nahbereich-Parallaxe in Handy-3.
- **Claudes Stichprobe Runde 2:** Drohne-A ohne sichtbare Nähte in der Übersicht, Geometrie wie Photoshop; Himmel blasser
  und leicht lavendel, Schattenseite dunstiger, Kontrast deutlich flacher als Herberts Bearbeitung.

**Messung** (Runde 1 → Runde 2):

| Serie | Zeit s | Spitze RAM | Ergebnis | Restfehler Median / 90 % | Naht-NCC vor → nach Entgeisterung (R2) |
|---|---|---|---|---|---|
| Handy-1 | 17 → 39 | 2,0 → 1,9 GB | 25 MP | 4,0 / 8,5 → 3,8 / 7,6 px | 0,23 → 0,78 |
| Handy-2 | 61 → 82 | 2,4 → 2,9 GB | 70 MP | 5,3 / 19,1 → 3,9 / 8,9 px | 0,14 → 0,60 |
| Handy-3 | 103 → 169 | 3,7 → 4,0 GB | 87 MP | 3,6 / 15,2 → 2,8 / 6,2 px | 0,01 → 0,10 |
| Drohne-A | 106 → 86 | 7,3 → 5,0 GB | 249 → 161 MP | 5,0 / 12,1 → 1,7 / 4,6 px | 0,61 → 0,91 |
| Drohne-B | 107 → 111 | 7,4 → 5,7 GB | 249 → 161 MP | 6,1 / 17,8 → 3,9 / 10,8 px | 0,35 → 0,80 |

- **Schärfe 1:1** (Laplace-σ an den bewerteten Stellen): Handy +20 … +140 %, Drohne +40 … +140 % gegenüber Runde 1;
  Photoshop liegt weiter höher (mit Herberts Nachschärfung).
- **Zeit:** Der größte Posten ist die Nahtsuche bei 0,5 MP (Handy-3: 81 von 169 s). Drohne: Laden und Entwickeln der
  26 DNG 8–12 s, Verzerren 25–28 s.
- **Speicher:** Die Drohne braucht weniger, seit nur der Horizontstreifen ausgegeben wird (Nadir weg, −30 %).
- **Zufall:** RANSAC/FLANN sind nicht deterministisch; Handy-3 lieferte je Lauf 4 391–4 808 Zeilen Höhe, bei einer
  schärferen Paarschwelle in einem von drei Läufen eine gekippte Kette (zwei Sonnen).

**Rohentwicklung (Drohne):**

- Windows' DNG-Decoder liefert nur die eingebettete Vorschau (960 × 640). Die Raw-Bilderweiterung liefert die volle
  Größe, aber **nur 8 bit** (`24bppRGB`, 16 bit nur hochgerechnet), ohne Belichtung, Schärfung, Lichter-Rekonstruktion
  oder Objektivkorrektur (kein `IWICDevelopRaw`). Damit wurden die Panoramen in Runde 1 weich und dunkel, Drohne-B bekam
  einen Cyanstich (gesättigter Grünkanal, Blau abgeschnitten).
- **Eigene 16-bit-Entwicklung in C#** (die DJI-DNG sind unkomprimiert, RGGB), ohne neues Paket und ohne LibRaw:
  Weißabgleich aus der Serie, kantenbewusstes Demosaik, GainMap linear, ColorMatrix2, Lichter-Rekonstruktion, gemeinsame
  Belichtung der Serie, Grundkurve und Schärfung. Ergebnis: kein Cyanstich, Gegenlicht mit Zeichnung.
- Noch nicht umgesetzt: das Kameraprofil aus dem DNG (HueSatMap, ForwardMatrix) — ohne es wirkt der Himmel leicht lavendel.

**Hochskalieren nach dem Zusammenfügen** (Herberts Frage):

| Lauf (DirectML, GTX 1080 Ti) | Zeit | Spitze RAM | Ergebnis | Modellanteil Ø |
|---|---|---|---|---|
| Handy-1, ClearReality 100 % (R1) | 15 s | 0,7 GB | 93 MP | 100 % |
| Handy-1, ClearReality gemischt (R2) | 13 s | 1,1 GB | 101 MP | 26 % |
| Drohne-A, ClearReality 100 % (R1, bis Nadir) | 155 s | 5,0 GB | 996 MP | 100 % |
| Drohne-A, ClearReality gemischt (R2) | 75 s | 5,0 GB | 642 MP | 8 % |

- „Gemischt“: Modell 4× je Kachel, auf 2× gemittelt, mit bikubisch gemischt; Modellanteil höchstens 60 %, nach lokaler
  Schärfe. Himmel, Dunst und Bewegungsunschärfe bleiben bikubisch. Sichtbar knackiger als bikubisch, ohne das
  „wachsartige“ Gestein und die erfundenen Pinselstriche von 100 % Modell.
- Remacri ist gemischt kaum von ClearReality zu unterscheiden, aber etwa 5× langsamer (310 statt 25–60 ms je Kachel).
- Vor dem Zusammenfügen hochskalieren lohnt nicht: doppelte Modellzeit wegen der Überlappung, erfundene Texturen stören
  Abgleich und Naht.

## Folgerungen

Vorschläge für ADR-0078 (Nachtrag dort, Abnahme durch Herbert):

1. **Panorama-Werkzeug:** eigene Kette auf OpenCV-Bausteinen (Option 1 aus ADR-0078 Punkt 3). Hugin ist für Kugel und
   Schwenk nicht nötig; offen bleibt nur der Nahbereich (Offen 1).
2. **DNG der Drohne:** eigene 16-bit-Entwicklung statt der Raw-Bilderweiterung (die liefert nur 8 bit). Die
   Raw-Bilderweiterung bleibt Rückfall für andere Rohformate; LibRaw entfällt für DNG.
3. **Ausgabe Drohne:** Standard Horizontstreifen; ganze Kugel bis Nadir als Option (Speicher +30 %).
4. **Hochskalieren:** nur nach dem Zusammenfügen, gedämpft gemischt, standardmäßig aus, ClearReality als Standard. Für
   Panoramen braucht es einen Kachel-Strom ohne Gesamtpuffer: `EnhanceImage` endet bei 10 000 px / 64 MP, ein
   .NET-`byte[]` bei ≈ 715 MP, JPEG bei 65 535 px je Seite (Drohne 2× = 54 270 px geht, 4× nicht).
5. **Hilfsprozess:** empfohlen. OpenCvSharp stürzt in einigen Stitching-Klassen nativ ab, und eine Drohnen-Kugel braucht
   5–6 GB; beides soll Lumora nicht beenden. Die eigene DNG-Entwicklung macht den Hilfsprozess WIC-frei.
6. **Ergebnis prüfen statt blind speichern:** feste Startwerte für RANSAC und eine Plausibilitätsprüfung (Restfehler,
   Nahtgüte, Höhe des Zuschnitts), weil einzelne Läufe kippen können.
7. **Zeitbudget:** Drohnen-Kugel ≈ 1,5–2 min, Handy-Schwenk 0,5–3 min auf Herberts Rechner; ein Stapel über alle
   48 Drohnen-Kugeln dauert also gut eine Stunde.

## Offen

1. **Parallaxe im Nahbereich** (Handy-3, Felsen in 1–3 m): Gitter-Warping je Bild (APAP) oder Naht aus dem Nahbereich
   herausführen.
2. **Photometrie bei Handy-JPEGs:** Ein globales Modell je Bild erklärt die lokale Tonung nicht; für Gegenlicht eine
   Tonkurven-Angleichung je Überlappung.
3. **Rohfarbe:** DNG-Kameraprofil (HueSatMap, LookTable) anwenden.
4. **Nahtsuche schneller:** zweistufig (0,1 MP grob, Band fein).
5. **Ränder füllen:** Inpaint-Ecken sind weich; Rectangling für Freihand-360°.
6. **Herberts Urteil** über die Vergleichsbilder von Runde 2 (außerhalb des Repos in
   `werkzeuge\runs\panorama-spike\vergleich2\`).
