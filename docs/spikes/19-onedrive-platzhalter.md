# Spike #19 — OneDrive-Platzhalter: Vorschaubilder und Eigenschaften ohne Download

- **Status:** 🟢 Grün — Vorschaubilder und Aufnahmedatum gibt es ohne Download, GPS nicht
- **Datum:** 2026-11-17
- **Bezogen auf:** PLAN.md Kap. 11 (Storage-Profile), 12 (Offline-vs-Missing), 13 (Thumbnail-Pipeline);
  `ThumbnailService.IsLocallyAvailable` (erkennt Platzhalter, die Hintergrund-Dienste lassen sie aus)

## Zweck

Mit „Dateien bei Bedarf“ liegen Fotos in einem OneDrive-Ordner nur als **Platzhalter** auf der Platte: Name, Größe und
Datum sind da, der Inhalt nicht. Liest ein Programm den Inhalt, lädt Windows die ganze Datei herunter. Der Explorer zeigt
für solche Fotos trotzdem Vorschaubilder.

Frage: Kann Lumora einen OneDrive-Ordner als Bibliothek führen, **ohne** dabei die Fotos herunterzuladen — also
Vorschaubilder und Grunddaten so holen wie der Explorer?

## Aufbau

- Prüfprogramm (PowerShell mit eingebettetem C#, außerhalb des Repos): Vorschaubild über die Shell
  (`IShellItemImageFactory::GetImage` mit `SIIGBF_THUMBNAILONLY`, bei `WTS_E_EXTRACTIONPENDING` alle 100 ms erneut),
  Eigenschaften über den Property Store (`SHGetPropertyStoreFromParsingName`), danach jeweils das Attribut
  `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x00400000) prüfen — ist es noch gesetzt, wurde nichts heruntergeladen.
- Bestand: ein OneDrive-Ordner mit rund 66 000 Bilddateien, davon rund 46 000 Platzhalter (JPEG). Gemessen nur an
  Platzhaltern, die vorher noch nie angefragt waren; keine Dateinamen im Protokoll.
- Rechner: siehe Spike 02 (Grafikkarte spielt keine Rolle), Verbindung: Heim-Internet.

**Stolperstein:** Läuft das Prüfprogramm im App-Container der Claude-Desktop-App, liefert die Shell für Platzhalter nie
ein Vorschaubild (dauerhaft `WTS_E_EXTRACTIONPENDING`). Nativ gestartet (über `explorer.exe`) klappt es. Lumora läuft
nativ; Messungen deshalb immer nativ.

## Ergebnis

| Prüfung | Ergebnis |
|---|---|
| Vorschaubild 256 px, erster Abruf | ja, **0,35–2,8 s** je Bild (OneDrive holt es aus der Cloud) |
| Vorschaubild 256 px, erneut / nur aus dem Cache (`SIIGBF_INCACHEONLY`) | ja, **5–10 ms** |
| Vorschaubild 1024 px | ja, eigener Abruf 0,4–2,2 s; volle 1024 px an der langen Kante |
| Nacheinander, 30 Bilder | 44 s, Median 1,3 s je Bild (≈ 0,7 Bilder/s) |
| 4 gleichzeitig, 40 Bilder | 17,4 s (≈ 2,3 Bilder/s) |
| 8 gleichzeitig, 40 Bilder | 10,1 s (≈ 4,0 Bilder/s), langsamstes 4,7 s |
| Eigenschaften, nur schnelle (`GPS_FASTPROPERTIESONLY`) | nein (`E_FAIL`) |
| Eigenschaften, normal (`GPS_DEFAULT`) | **Aufnahmedatum 30 von 30, Bildbreite ja, GPS 0 von 30** |
| Vergleich: 30 lokale Fotos, GPS | 20 von 30 |
| Datei dabei heruntergeladen | **in keinem Fall** (über 150 Abrufe) |

## Folgerungen

1. **Vorschaubilder ohne Download sind möglich.** Für Platzhalter darf Lumora das Original nicht dekodieren (das löst den
   Download aus), sondern holt das Vorschaubild über die Shell — wie der Explorer. 1024 px reichen auch für eine
   Lightbox-Vorschau, bevor das Original geladen ist.
2. **Nur bei Bedarf, nicht auf Vorrat.** Bei ≈ 4 Bildern/s (8 gleichzeitig) dauert eine volle Galerieseite einige
   Sekunden; 46 000 Platzhalter auf Vorrat wären über drei Stunden Abruf. Also: sichtbare Kacheln zuerst, mit 8
   gleichzeitigen Abrufen; das fertige Bild kommt in Lumoras eigenen Vorschaubild-Cache, danach gilt die normale Pipeline.
3. **Aufnahmedatum ohne Download:** Der Property Store liefert für Platzhalter das Aufnahmedatum (offenbar von OneDrive);
   Lumora kann das Datum beim Einlesen so übernehmen, statt aufs Dateidatum auszuweichen.
4. **Orte erst nach dem Download:** GPS liefert der Property Store für Platzhalter nicht (0 von 30, lokal 20 von 30).
   Orte bleiben leer, bis ein Foto einmal lokal war; die bestehende Nachtrag-Logik holt sie dann.

## Offen

- **Ohne Internet:** nicht geprüft. Erwartung: Vorschaubilder nur aus dem Cache, neue Abrufe scheitern — Lumora darf das
  nicht als „fehlt“ werten (PLAN Kap. 12).
- **Videos und andere Formate** (HEIC, RAW): nicht geprüft, nur JPEG.
- **Gleichzeitigkeit über 8:** nicht geprüft; ob OneDrive mehr Abrufe parallel bedient, offen.
- **Lumoras eigener Prozess:** gemessen mit einem eigenen Prüfprogramm, nicht in Lumora. Der Abruf muss in Lumora auf
  STA-Threads laufen (Shell-COM).
