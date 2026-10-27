# ADR-0076: KI-Bearbeitung mit ComfyUI — nur in der versteckten Sitzung

- **Status:** Accepted (Konzept und Mockup 23 abgenommen von Herbert am 2026-10-27; Leerlauf 10 min, Kleiderliste und
  `<Name>_ki.jpg` wie vorgeschlagen; Stufe 1 „Laufzeit“ gebaut in 0.5.30, siehe Nachtrag)
- **Datum:** 2026-10-27
- **Entscheider:** Herbert Schrotter (Anforderung: lokale KI-Bildbearbeitung, nur versteckt, alles im Container,
  ComfyUI-Oberfläche zusätzlich im eigenen Fenster), Claude (Entwurf, Grenzen der Bearbeitung, Umsetzungsvorschlag)
- **Bezogen auf:** [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Sitzung, Spurenfreiheit, Zusperren),
  [ADR-0064](0064-sicherheits-check.md) (Dienste nur im versteckten DI-Container),
  [ADR-0070](0070-modelle-und-daten.md) („Modelle & Daten“, Download nur nach Bestätigung),
  [ADR-0071](0071-foto-verbessern.md) (Fassungen, `CreateVariant`, Pinsel-Bühne),
  [ADR-0074](0074-hintergrund-planer.md) (Nachtrag 0.5.12: `IForegroundWork`), ADR-0035/0037 (Gesichter, buffalo_l)

## Kontext

„Foto verbessern“ (ADR-0071) kann hochskalieren, Gesichter restaurieren, einfärben und kleine Stellen entfernen — aber
nichts *erzeugen*: kein anderer Hintergrund, kein erweitertes Bild, kein Austausch größerer Bildteile, keine andere
Kleidung. Das können Diffusionsmodelle (FLUX.1 Kontext/Fill, SDXL), die sich am besten über ComfyUI lokal betreiben
lassen. Herbert will diese Bearbeitung **nur in der versteckten Sitzung**: Programm, Modelle, Ein-/Ausgaben und Caches
liegen im verschlüsselten Container, die normale Sitzung weiß nichts davon. Cloud-Dienste sind ausgeschlossen; Fotos
verlassen nie den PC.

Randbedingungen: Grafikkarte mit 11 GB VRAM aus der Pascal-Generation (keine bf16-/fp8-Rechenwerke, neuere PyTorch-
Builds mit CUDA ≥ 12.8 unterstützen sie nicht mehr → ComfyUI-Paket mit CUDA 12.6). ComfyUI ist ein Python-Server mit
eigener Weboberfläche, Erweiterungs-Ökosystem, Update-Funktionen und optionalen Cloud-/API-Knoten — alles, was Lumora
davon nicht braucht, muss aus bleiben. Dazu kommt eine Grenze, die nicht verhandelbar ist: Lumora erzeugt **keine
sexualisierten Bearbeitungen echter Personen** (Unterwäsche, Lingerie, Nacktheit, „Ausziehen“) — auch nicht versteckt.

## Entscheidung

1. **Nur versteckt.** Alle Dienste der KI-Bearbeitung werden nur im DI-Container der versteckten Sitzung registriert
   (`AddAiEdit()`, wie der Sicherheits-Check, ADR-0064). Die normale Sitzung hat kein Menü, keinen Reiter, keine
   Einstellung, keinen Eintrag in „Modelle & Daten“ und keine Protokollzeile dazu. Zusperren beendet alles (Punkt 4).

2. **Alles im Container** unter `<Container-Wurzel>\.lumora-ki\` (Attribute versteckt + System + nicht indizieren;
   Scanner/Flatten/Zielbaum überspringen den Namen wie `.lumora`):
   - `ComfyUI\` — das portable Paket unverändert (eingebettetes Python, ComfyUI, kein Update-Lauf),
   - `models\` — Diffusionsmodelle, Text-Encoder, VAE (als `extra_model_paths`), dazu `lumora\` für Lumoras Prüfmodell,
   - `work\input`, `work\output`, `work\temp`, `work\user` — Ein-/Ausgabe, Zwischenstände, ComfyUI-Benutzerordner,
   - `cache\` — Python-/Torch-/Hugging-Face-/CUDA-/pip-Caches,
   - `webview\` — Browserdaten des ComfyUI-Fensters (Punkt 5a),
   - `ki-install.json` — geprüfte ComfyUI-Version, Liste der installierten Modelle mit SHA-256 und Freigabedatum.

   Eine Installation je Container, gemeinsam für alle versteckten Bibliotheken darin. Container zu = kein ComfyUI,
   keine Verknüpfung, kein Startmenü-Eintrag, kein Dienst.

3. **Lumora startet ComfyUI selbst** (`IComfyRuntime`, Services/AiEdit), erst beim Öffnen der Bearbeiten-Ansicht:
   - `python_embeded\python.exe -s ComfyUI\main.py` mit `--listen 127.0.0.1`, zufälligem freiem `--port`,
     `--disable-auto-launch`, `--input-directory`/`--output-directory`/`--temp-directory`/`--user-directory` in
     `work\`, `--extra-model-paths-config` auf `models\`, ohne Manager, ohne Erweiterungen außer den freigegebenen
     (Punkt 7), API-/Cloud-Knoten aus. **Jedes Flag wird vor dem Bau an der installierten ComfyUI-Version geprüft**
     (`main.py --help`); Lumora startet nur Versionen aus seiner geprüften Liste (Start mit 0.43.0), sonst Hinweis
     „ungeprüfte Version“ und kein Start.
   - Umgebung nur für diesen Prozess: `TEMP`/`TMP`, `HF_HOME`, `TORCH_HOME`, `CUDA_CACHE_PATH`, `PIP_CACHE_DIR`,
     `XDG_CACHE_HOME`, `MPLCONFIGDIR`, `TRITON_CACHE_DIR` → `cache\`; dazu `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`,
     `DO_NOT_TRACK=1`, `PYTHONNOUSERSITE=1`. Kein Fenster, Ausgabe nur in das Protokoll der versteckten Sitzung (ohne Pfade).
   - Der Prozess läuft in einem Windows-Job-Objekt mit „beim Schließen beenden“ — stürzt Lumora ab, endet ComfyUI mit.
   - Bereit, sobald `GET /system_stats` antwortet (Version und VRAM werden geprüft).

4. **Beenden:** beim Zusperren (von Hand und nach Untätigkeit), beim Ende der versteckten Sitzung, beim Beenden von
   Lumora und nach 10 min ohne Auftrag (einstellbar). Ablauf: `POST /interrupt` → Prozessbaum beenden → auf Ende warten
   (höchstens 10 s) → `work\input|output|temp` leeren. Erst danach darf der Container ausgehängt werden (offene Dateien
   würden das Auswerfen verhindern). Ein laufender Auftrag wird beim Zusperren **abgebrochen**, nicht abgewartet —
   anders als Scans (ADR-0049 Punkt 6), weil ein KI-Lauf Minuten dauert und Zusperren Vorrang hat.

5. **Standard ist Lumoras eigene Leiste.** Die Bearbeiten-Ansicht (Mockup 23) mit festen Vorlagen spricht ComfyUI
   über die API an (`IComfyClient`): `POST /upload/image`, `POST /prompt`, `WS /ws?clientId=` (Fortschritt),
   `GET /history/{id}`, `GET /view`, `POST /interrupt`, `POST /free`, `POST /history` (Eintrag löschen),
   `GET /object_info` (Knoten-Prüfung). Der Port wird nirgends angezeigt.

5a. **Zusätzlich: ComfyUI-Fenster** (Herbert, 2026-10-27: „kann ich direkt in comfy bearbeiten“ → Weg „eigenes Fenster
   aus Lumora“). In der versteckten Sitzung öffnet „ComfyUI-Oberfläche öffnen…“ (Leiste, Lightbox-Rechtsklick „In ComfyUI
   öffnen“, Einstellungen) ein eigenes Lumora-Fenster mit WebView2 (`ComfyWindow`, Wpf):
   - Browserdaten nur im Container: `CoreWebView2Environment` mit `UserDataFolder` = `.lumora-ki\webview\`; kein
     Speichern von Kennwörtern/Formularen, beim Schließen Verlauf und Cache leeren (`Profile.ClearBrowsingDataAsync`).
     Nicht im normalen Browser — der würde Verlauf und Cache außerhalb des Containers anlegen.
   - Nur `http://127.0.0.1:<port>`: jede andere Adresse, neue Fenster und externe Links werden abgelehnt (nichts wird im
     Standard-Browser geöffnet). Downloads aus der Oberfläche sind gesperrt — Modelle und Erweiterungen kommen nur über
     Lumoras Freigabe (Punkt 7); ComfyUI läuft weiter ohne Manager.
   - „In ComfyUI öffnen“ legt eine Kopie des aktuellen Fotos unter neutralem Namen in `work\input` und merkt sich die
     Zuordnung; der Benutzer wählt es im Knoten *Load Image*.
   - **„Ergebnisse übernehmen“** (Leiste oben im Fenster, mit Zahl): Lumora zeigt neue Bilder aus `work\output`, prüft
     jedes wie in Punkt 8.3 (sonst verworfen) und speichert die gewählten als Fassung des zugeordneten Originals
     (`<Name>_ki.jpg`) bzw. ohne Zuordnung als neue Datei im gewählten Ordner der versteckten Bibliothek. Kennzeichnung wie
     Punkt 8.4; Modellnamen liest Lumora aus dem eingebetteten Arbeitsablauf (PNG-Text `prompt`), sonst „unbekannt“.
     Übernommene Ausgaben werden gelöscht.
   - Nicht übernommene Ergebnisse bleiben in `work\output` (im Container) und werden beim nächsten Öffnen angeboten;
     Schließen des Fensters fragt nach („n Ergebnisse noch nicht übernommen“). Zusperren schließt das Fenster ohne Frage.
   - Schalter „ComfyUI-Oberfläche anbieten“ in Einstellungen › KI-Bearbeitung (Standard an).
   - Ob WebView2 außerhalb des `UserDataFolder` etwas schreibt (Absturzberichte, Shader-Cache), wird vor dem Bau am echten
     System geprüft; Fund → in den Container umlenken oder als Grenze im Sicherheits-Check nennen.

6. **Feste Vorlagen** (`IEditTemplateCatalog`): Arbeitsabläufe im API-Format als eingebettete Ressourcen mit
   Platzhaltern (Bild, Maske, Auswahl, Größe, Seed). Vor jedem Senden prüft Lumora den fertigen Graphen: nur Knotentypen
   aus der Liste der Vorlage, nur Modelldateien aus `ki-install.json`, keine Knoten mit Netzwerkzugriff.
   | Vorlage | Eingabe | Modell (Vorschlag) | Zeit (Schätzung) |
   |---|---|---|---|
   | **Hintergrund** | Auswahl aus Liste + optional kurzer Text | FLUX.1 Kontext [dev] GGUF Q4 | 2–4 min |
   | **Outfit** | nur Auswahl aus der Liste Alltagskleidung, kein Freitext | FLUX.1 Kontext [dev] GGUF Q4 | 2–4 min |
   | **Entfernen** | Pinsel-Maske | FLUX.1 Fill [dev] GGUF Q4; schnell: SDXL-Inpainting | 2–3 min / 20–40 s |
   | **Erweitern** | Seitenverhältnis bzw. Ränder | FLUX.1 Fill [dev] GGUF Q4 | 2–4 min |
   | **Verbessern** | Stärke (gering/mittel) | SDXL-Fotomodell, img2img mit niedriger Stärke | 20–60 s |

   „Verbessern“ ergänzt das bestehende „Foto verbessern“ (rekonstruiert Details), ersetzt es nicht. Qwen-Image-Edit
   wird nicht vorgesehen (auf 11 GB nur stark gestaucht, 3–8 min). InstantID/IP-Adapter bleiben spätere Option.

7. **Modelle und Erweiterungen nur nach Freigabe.** Jede Datei (ComfyUI-Paket, jedes Modell, jede Erweiterung wie
   ComfyUI-GGUF) wird einzeln mit Name, Quelle, Version/Commit, Größe, Lizenz und SHA-256 vorgelegt und erst nach
   Herberts Bestätigung geladen — direkt in den Container (keine Kopie in Downloads/Temp außerhalb). „Modelle & Daten“
   zeigt in der versteckten Sitzung einen Abschnitt „KI-Bearbeitung (im Container)“; nichts davon liegt im normalen
   Modellordner, im Repo oder im portablen Paket. Größen und Quellen werden vor jeder Freigabe frisch geprüft.
   Lizenz FLUX.1 [dev]: nur nicht-kommerziell (passt zur privaten Nutzung, wird im Eintrag angezeigt).

8. **Grenzen der Bearbeitung** (`IEditPolicy`, `Lumora.Services/Edit/`) — gelten in jeder Sitzung, ohne Schalter:
   1. *Nur Vorlagen:* kein freier Arbeitsablauf, keine eigenen LoRAs/Checkpoints. Outfit nur aus der festen Liste
      Alltagskleidung (z. B. Jeans + Pullover, Anzug, Wanderkleidung, Winterjacke, Tracht, Sommerkleid knielang);
      Bademode, Unterwäsche, Nachtwäsche, Lingerie, transparente oder „freizügige“ Kleidung gibt es nicht.
   2. *Begriffsliste* (deutsch + englisch, Wortstämme) prüft jeden Freitext vor dem Senden; Treffer → nicht senden,
      sachlicher Hinweis. Jede Vorlage hängt eine feste Negativ-Beschreibung gegen Nacktheit an.
   3. *Ergebnisprüfung:* ein lokales Nacktheits-Erkennungsmodell (Kandidat NudeNet ONNX, Lizenz vor Freigabe prüfen)
      prüft jedes Ergebnis, bevor es angezeigt wird. Erkennt buffalo_l ein Gesicht (= echte Person) und ist das Ergebnis
      freizügig → **verwerfen**, nichts anzeigen, nichts speichern, Arbeitsdateien löschen. Schätzt buffalo_l
      (genderage) eine Person auf dem Foto **unter 21** (Sicherheitsabstand zu 18), gibt es für dieses Foto **keine
      Outfit-Vorlage**, und die Ergebnisprüfung verwirft schon bei geringerer Schwelle.
   4. *Kennzeichnung:* jedes Ergebnis trägt „KI-bearbeitet“ — XMP `Iptc4xmpExt:DigitalSourceType` =
      `compositeWithTrainedAlgorithmicMedia`, `xmp:CreatorTool`/EXIF `Software` = „Lumora KI-Bearbeitung (Vorlage,
      Modell, Datum)“, dazu `PhotoVariants.Tools` = `ki:<vorlage>:<modell>`.

   Im ComfyUI-Fenster (Punkt 5a) gelten 8.1 und 8.2 nicht — dort sind freie Arbeitsabläufe möglich; 8.3 und 8.4
   greifen beim Übernehmen. Lumora liefert für sexualisierte Bearbeitungen keine Arbeitsabläufe, LoRAs oder Modelle
   und richtet keine ein.

   Die Prüfungen schützen vor **Versehen und Fehlbedienung**, nicht gegen den Besitzer des PCs: Wer im ComfyUI-Fenster
   arbeitet oder ComfyUI selbst aus dem Container startet, kommt an die Dateien in `work\output` auch ohne Lumora. Das
   wird so offen gesagt (Einstellungen › KI-Bearbeitung, dieser ADR).

9. **Ablauf eines Auftrags** (`IEditJobRunner`): Policy-Vorprüfung → Bild verkleinert auf die Arbeitsgröße der Vorlage
   (`work\input`, eindeutiger Name ohne Original-Dateinamen) → `/prompt` → Fortschritt über `/ws` → Ergebnis über
   `/view` in `ICatalogStoragePaths.TempRoot` → Ergebnisprüfung → Vorher/Nachher in der Ansicht. Danach sofort
   ComfyUI-Eingabe, -Ausgabe und History-Eintrag löschen. „Als Fassung speichern“ legt über `CreateVariantsAsync`
   (ADR-0071) `<Name>_ki.jpg` neben dem Original an (Rückgängig wie dort), Metadaten wie ADR-0071 Punkt 3 plus Punkt 8.4.
   Mehrere Varianten (Seeds) je Lauf sind möglich, gespeichert wird nur die gewählte.

10. **Grafikkarte:** Während eines Auftrags meldet die Bearbeitung `Gpu | Cpu` bei `IForegroundWork` an (ADR-0074
    Nachtrag 0.5.12) — Gesichter, Motive und Dateidienste pausieren. Nach dem Auftrag `POST /free` (Modelle aus dem
    VRAM), damit die Hintergrund-Aufgaben wieder Platz haben. Auf dem Prozessor ohne CUDA-Karte wird nicht gerechnet
    (Hinweis statt Stunden-Lauf).

11. **Einrichtung in Stufen:** (1) Laufzeit — Erkennen, Version prüfen, Starten/Beenden, Status in der Ansicht;
    (2) Grenzen — `IEditPolicy`, Prüfmodell, Kennzeichnung, Tests; (3) Vorlagen Entfernen/Erweitern/Verbessern;
    (4) Hintergrund und Outfit — **erst, wenn Stufe 2 fertig und getestet ist**. (5) ComfyUI-Fenster mit „Ergebnisse übernehmen“ — ebenfalls erst nach Stufe 2. Die Erstinstallation von ComfyUI legt
    Claude nach Herberts Freigabe einmalig von Hand in den Container; ein Installer in Lumora ist spätere Option.

## Begründung

- ComfyUI ist der verbreitetste lokale Server für FLUX/SDXL, läuft portabel ohne Installation und hat eine stabile
  JSON-API. Eigene Diffusions-Pipelines in .NET (ONNX) gibt es für FLUX Kontext/Fill nicht in brauchbarer Form.
- Eigene Leiste als Standard: feste Vorlagen sind einfacher zu bedienen und erlauben die Grenzen 8.1/8.2. Das
  ComfyUI-Fenster kommt für freie Arbeit dazu — als eigenes Lumora-Fenster statt im Browser, weil nur so Verlauf, Cache
  und Downloads im Container bleiben und das Fenster beim Zusperren mit zugeht.
- Alles im Container (inkl. CUDA-Cache und Prüfmodell) folgt ADR-0049: außerhalb des Containers darf keine Spur entstehen;
  schon ein Modelleintrag in der normalen Liste würde verraten, dass es die Funktion gibt.
- Ein Job-Objekt und das Beenden vor dem Aushängen sind nötig, weil ein Python-Prozess sonst den Container offen hält
  und ein Absturz von Lumora einen lauschenden Server zurücklassen würde.
- Mehrstufige Grenzen (Vorlage, Begriffe, Ergebnisprüfung) statt nur Begriffsliste: Diffusionsmodelle erzeugen auch ohne
  passende Worte freizügige Bilder (z. B. beim Entfernen über einer Person); erst die Prüfung des Ergebnisses fängt das.
- Der Alters-Sicherheitsabstand (21 statt 18) gleicht die Ungenauigkeit der Altersschätzung aus; im Zweifel wird gesperrt.
- FLUX GGUF Q4 statt fp8/fp16: passt mit Auslagerung in 11 GB VRAM; Pascal kann fp8 ohnehin nicht rechnen.

## Konsequenzen

### Positiv
- Generative Bearbeitung lokal, ohne Cloud, ohne Spur außerhalb des Containers.
- Ergebnisse sind normale Fassungen mit Rückgängig, Kennzeichnung und Herkunft im Katalog.
- Grenzen sind im Code, getestet und für jede Vorlage gleich.

### Negativ
- Großer Platzbedarf im Container (ComfyUI ≈ 6 GB entpackt, Modelle je nach Auswahl ≈ 20–30 GB) — mindestens 30 GB frei.
- Langsam auf Pascal (FLUX 2–4 min je Bild); bei laufender Bearbeitung pausiert der Hintergrund.
- Während ComfyUI läuft, kann jeder lokale Prozess den zufälligen Port erreichen (ComfyUI hat keine Anmeldung). Gemindert
  durch Start nur bei Bedarf, Leerlauf-Ende und 127.0.0.1.
- Neue ComfyUI-Versionen brauchen eine Prüfung der Flags und Vorlagen, bevor Lumora sie startet.
- Die Grenzen schützen nicht vor dem Besitzer (Punkt 8).

### Neutral
- Neues Modul `Lumora.Services/AiEdit/` (Laufzeit, Client, Vorlagen, Aufträge, Übernehmen) und `Lumora.Services/Edit/`
  (`IEditPolicy`); Leiste und `ComfyWindow` (WebView2, NuGet `Microsoft.Web.WebView2`) im Wpf-Projekt. Rauchtest und Tests mit einem Fake-ComfyUI (HTTP/WS-Attrappe), nie echt.
- `.lumora-ki` kommt zur Namensliste der übersprungenen Ordner (`CatalogFolders`).
- Keine Migration: Fassungen nutzen `PhotoVariants` (ADR-0071).

## Alternativen

- **Nur die Vorlagen-Leiste, keine ComfyUI-Oberfläche** (erster Entwurf) → erweitert um das ComfyUI-Fenster (Punkt 5a),
  Herbert will auch frei in ComfyUI arbeiten.
- **ComfyUI-Oberfläche im normalen Browser** → verworfen: Verlauf, Cache und Downloads außerhalb des Containers, kein
  Schließen beim Zusperren.
- **ComfyUI dauerhaft als Dienst/Autostart** → verworfen: Spur außerhalb des Containers, läuft auch bei geschlossenem
  Container nicht.
- **Eigene ONNX-Pipeline in Lumora** → verworfen: für FLUX Kontext/Fill nicht verfügbar, sehr großer Aufwand.
- **Forge/Automatic1111/InvokeAI** → verworfen: schwächere oder keine stabile API für FLUX Kontext, mehr Netzwerkfunktionen.
- **Qwen-Image-Edit** → verworfen für 11 GB (nur stark gestaucht, 3–8 min, Qualität fraglich).
- **Cloud-Dienste** → ausgeschlossen (Fotos verlassen nie den PC).
- **Grenzen nur über Begriffsliste** → verworfen (siehe Begründung).
- **Freizügige Bearbeitung erlauben, weil versteckt** → ausgeschlossen: echte Personen, unabhängig vom Speicherort.

## Offene Punkte (vor dem Bau)

- ~~Herbert: Mockup 23 abnehmen, Leerlauf-Zeit, Liste Alltagskleidung, Dateiname~~ — erledigt 2026-10-27 (wie vorgeschlagen).
- ~~Flags an ComfyUI 0.43.0 prüfen~~ — erledigt 2026-10-27, siehe Nachtrag Stufe 1.
- Prüfmodell: Lizenz und Größe von NudeNet (Alternative: ein Apache-2.0-Klassifizierer) klären, Schwellen mit Testbildern
  ohne echte Personen festlegen.
- Modellgrößen/Quellen frisch prüfen und einzeln zur Freigabe vorlegen.

## Nachtrag 0.5.30 — Stufe 1 „Laufzeit“ gebaut (2026-10-27)

- **Gebaut:** `Lumora.Services/AiEdit/` mit diesen Teilen:
  - `AiEditLayout` — die Ordner unter `.lumora-ki`.
  - `ComfyInstallation` — Erkennen, Version aus `comfyui_version.py`, geprüfte Liste `ComfyVersions.Verified` = {0.43.0}.
  - `AiInstallManifest` — `ki-install.json`, Format `lumora-ki-install` v1.
  - `ComfyRuntime`/`IComfyRuntime` — Starten, Bereitschaft über `/system_stats`, Beenden, Leerlauf.
  - `ComfyProcessLauncher` — Start im Job-Objekt „beim Schließen beenden“.
  - `AiEditSettingsStore` — Schlüssel `aiedit.idleMinutes` im versteckten Katalog.
  - `AddAiEdit()` — die Registrierung.

  Dazu im Wpf-Projekt der Reiter Einstellungen › KI-Bearbeitung (`AiEditSettingsViewModel`, `AiEditUi`). Er zeigt
  Laufzeit, Probelauf Starten/Beenden, Leerlauf-Minuten, Platz im Container und die Liste aus `ki-install.json`. Alles
  ist nur in `AddCatalogSession` der versteckten Sitzung registriert. Der Rauchtest prüft den Reiter dort und dass es in
  der normalen Sitzung weder Dienst noch Reiter noch Einstellung gibt.
- **Startoptionen an 0.43.0 geprüft** (`comfy/cli_args.py`, `main.py`). Zwei Abweichungen von Punkt 3:
  - `--offline` statt `--disable-api-nodes`: Letzteres ist in 0.43.0 nur noch ein veralteter Alias. `--offline` sperrt
    zusätzlich per Content-Security-Policy den Internetzugriff der Oberfläche.
  - `--models-directory .lumora-ki\models` statt einer `extra_model_paths.yaml`: Die Option gibt es in 0.43.0, eine
    erzeugte Datei entfällt.

  Weitere Festlegungen:
  - `--disable-all-custom-nodes` + `--whitelist-custom-nodes`: nur Namen aus `ki-install.json`, die auch in
    `custom_nodes` liegen.
  - `--previews-directory` liegt in `work\`.
  - Manager und Assets bleiben aus: Sie liefen nur mit `--enable-manager` bzw. `--enable-assets`, und Lumora setzt
    beides nicht.
  - Arbeitsverzeichnis des Prozesses ist `work\`. ComfyUI schreibt sein Detail-Protokoll relativ dazu; Lumora löscht es
    beim Beenden.
  - Zusätzlich zu Punkt 3 werden `PYTHONPATH`, `PYTHONHOME` und `PYTHONSTARTUP` entfernt.
- **Beenden:** Zwei Wege, beide vor dem Aushängen.
  - `AiEditSessionHook.StopAsync` läuft im Sitzungs-Host beim Zusperren, Wechseln und Beenden. Beim Sitzungsstart liest
    der Hook nichts: Hosted Services starten vor der Migration des Katalogs, ein früher Zugriff ließ im Rauchtest das
    Öffnen einer neuen versteckten Bibliothek scheitern. Die Leerlauf-Zeit liest die Laufzeit erst beim Start von ComfyUI.
  - Ein synchrones `Dispose` greift beim Abbau des Containers.

  Dabei gilt:
  - Ein laufender Start wird abgebrochen.
  - Die Leerlauf-Prüfung läuft alle 30 s. `BeginWork()` hält ComfyUI während eines Auftrags am Leben (ab Stufe 3); bei
    0 Minuten endet ComfyUI nur beim Zusperren.
  - Endet der Prozess unerwartet, zeigt die Ansicht einen Fehler.
  - Geleert werden `work\input|output|temp|previews` und `*.log`; `work\user` bleibt.

  **Offen für Stufe 5:** Punkt 5a lässt nicht übernommene Ergebnisse in `work\output` liegen. Stufe 1 leert `output` aber
  wie Punkt 4 — das ComfyUI-Fenster muss diese Ausnahme mitbringen.
- **Grafikkarte:** gestartet wird nur, wenn `/system_stats` die erkannte Version und ein Gerät vom Typ `cuda` meldet
  (Punkt 10). Der Kartenname erscheint ohne Torch-Zusätze.
- **Protokoll:** Jede Ausgabezeile geht auf Debug-Stufe ins Protokoll der versteckten Sitzung, ohne Farbcodes. Pfade
  werden zu `.lumora-ki\…` bzw. `<Pfad>` gekürzt. Scheitert ein Start, kommen die letzten 40 Zeilen als Warnung dazu.
- **Am Zielrechner gemessen** (Probelauf über die Dienste, ohne Oberfläche):
  - Bereit nach 47 s beim ersten Start, danach nach 26 s; Beenden in 0,1 s.
  - ComfyUI-GGUF ist geladen (`UnetLoaderGGUF`), andere Erweiterungen werden übersprungen.
  - Nach dem Lauf gibt es keine neue Datei außerhalb von `.lumora-ki` (geprüft: Temp, `.cache`, NVIDIA- und
    Torch-Caches). Torch legt seinen Inductor-Cache unter `cache\tmp` an.
  - Endet das startende Programm hart, beendet das Job-Objekt ComfyUI mit; es bleibt kein Python-Prozess zurück.
- **Noch nicht gebaut:**
  - Stufen 2–5: Grenzen, Vorlagen, ComfyUI-Fenster.
  - `IForegroundWork` und `/free` — sie werden erst mit Aufträgen gebraucht (Stufe 3).
  - Ein Download-Abschnitt in „Modelle & Daten“. Modelle legt weiterhin Claude nach Freigabe von Hand in den Container
    (Punkt 11).

## Referenzen

- ComfyUI: github.com/comfyanonymous/ComfyUI (Releases, `server.py` für die API-Endpunkte, `comfy/cli_args.py` für die Flags)
- IPTC Digital Source Type (`compositeWithTrainedAlgorithmicMedia`)
- Mockup: [`mockups/23-ki-bearbeitung.html`](../../mockups/23-ki-bearbeitung.html)
