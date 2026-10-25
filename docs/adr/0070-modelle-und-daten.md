# ADR-0070: Update-Manager „Modelle & Daten“ — ein Register für alle Modelle und Datenpakete

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Claude (autonom, Issue #19; Abnahme von Mockup 19 durch Herbert ausstehend)
- **Bezogen auf:** [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md) (buffalo_l, Download nach Bestätigung),
  [ADR-0052](0052-motive-siglip2-lokal.md) (SigLIP 2), [ADR-0050](0050-orte.md) (GeoNames eingebettet),
  [ADR-0051](0051-orte-weltkarte-mit-foto-gruppen.md)/[ADR-0055](0055-kartenstile-und-gemeinsamer-filter.md) (Kartenkacheln),
  [ADR-0063](0063-story-assistent.md) (Ollama), [ADR-0067](0067-aktivitaets-anzeige.md) (Aktivitäts-Anzeige),
  [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Sitzung)

## Kontext

Lumora braucht neben den Programmdateien mehrere große Bausteine: das Gesichtsmodell buffalo_l (nur nicht-kommerziell),
SigLIP 2 für Motive, die eingebetteten GeoNames-Ortsnamen, zwischengespeicherte Kartenkacheln und — extern — ein
Sprachmodell in Ollama. Bisher hatte jedes Modul seinen eigenen Lade-Knopf und Status; einen Überblick über Größe,
Version, Quelle und Lizenz gab es nicht, auch kein „Prüfen“ oder „Entfernen“. Mit Welle 8 („Foto verbessern“, Mockup 13)
kommen vier weitere Modelle dazu. Projektregel: Downloads nur nach Bestätigung mit Name, Quelle und Größe; Modelle nie im
Repo oder im portablen Paket; nichts über den Benutzer senden. Die versteckte Sitzung lud SigLIP 2 bisher in den Container
(eigener Modellordner je AppData-Wurzel), obwohl Modelle keine Benutzerdaten enthalten.

## Entscheidung

1. **Register in Services** (`Services/ModelPackages`): `ModelPackageDefinition` (Id, Zweck, Name, Gruppe Modell/Daten/
   Extern, Version, Hersteller mit Website, Lizenz + Einschränkung, Download-Größe, Ordner, Dateien mit Größe und SHA-256,
   Update-Quelle, Hinweis) und je Eintrag eine Quelle `IModelPackageSource` (Zustand, Laden, Prüfen, Entfernen,
   Fähigkeiten). Feste Einträge in `ModelPackageCatalog`: buffalo_l (über `IFaceModelProvider`, Prüfsummen der zwei
   entpackten Dateien), SigLIP 2 (über `IMotifModelProvider`), GeoNames (eingebaut, „Prüfen“ liest die Ressource),
   Kartenkacheln (`<CacheRoot>\maptiles`, nur Größe), Ollama (nur Status über `IAssistantRuntime`). Neue Modelle kommen
   mit `AddFileSetModelPackage(definition, baseUri)` als **ein Eintrag** dazu (`FileSetModelPackageSource`: je Datei
   `*.download`, Größe, SHA-256, dann an ihren Platz).
2. **Manager** `IModelPackageManager`: Laden nur mit einer vom Manager erzeugten und erst nach dem Dialog **bestätigten**
   `ModelDownloadRequest` (sonst `ModelDownloadNotConfirmedException`, kein Abruf); nach dem Laden SHA-256 aller Dateien,
   **Prüf-Vermerk** `lumora-package.json` (Id, Version, Ergebnis, Zeitpunkt — keine Benutzerdaten) im Modellordner;
   Abbruch/Fehler räumen `*.download`/`*.tmp` auf; Fortschritt als Vorgang „Modell laden (MB)“ in der Aktivitäts-Anzeige
   (`ActivityKind.ModelDownload`, abbrechbar); ein Vorgang je Eintrag; Protokoll nur mit der Id, nie mit Pfaden.
3. **Zustände** ohne Prüfsumme (schnell): nicht geladen, geladen, **Update verfügbar** (Vermerk nennt ältere Version als
   die Definition dieser Lumora-Version), **defekt** (unvollständig, falsche Größe oder letzte Prüfung falsch — „Neu
   laden…“ holt dann alle eigenen Dateien neu), eingebaut, bei Bedarf, extern bereit/fehlt.
4. **„Update suchen“** nur auf Klick, nur Metadaten (GET mit Kennung „Lumora“, ohne Parameter): für SigLIP 2 die
   Hugging-Face-Dateiliste (`/api/models/…/tree/main[/onnx]`), verglichen werden die LFS-SHA-256 der großen Dateien.
   Geänderte Dateien beim Hersteller → Hinweis, dass die geprüfte Fassung mit einem Lumora-Update kommt (Lumora lädt nie
   ungeprüfte Dateien). Ohne Quelle (buffalo_l, Testmodell): „Version x, keine automatische Prüfung“.
5. **Oberfläche** Einstellungen › **Modelle & Daten** (Mockup 19, Reiter vor „Speicher & Wartung“): Gruppen Modelle/Daten/
   Extern, je Karte Zweck, Größe, Version, Lizenz (eingeschränkte hervorgehoben), Quelle mit Website (nur http/https, auf
   Klick), Speicherort, Prüfzeitpunkt, Aktionen **Laden…/Neu laden…/Update laden…**, **Prüfen**, **Update suchen**,
   **Entfernen…** (mit Rückfrage), Ollama mit „Einstellungen › Assistent“. Die Knöpfe in Gesichter und Motive nutzen
   denselben Bestätigungsdialog (`ModelDownloadDialog`) und laden über den Manager.
6. **Versteckte Sitzung:** `IModelStorage` und der Motiv-Anbieter zeigen dort auf den **normalen** Modellordner
   (wie buffalo_l seit ADR-0049) — dieselben Modelle, nichts davon in den Container und nichts über die Sitzung nach außen
   (Vermerk enthält nur Modell-Daten). Kartenkacheln bleiben im Cache des Katalogs (Container).
7. Ein über `LUMORA_FACE_MODELS`/`LUMORA_MOTIF_MODEL` vorgegebener Ordner wird nur angezeigt und geprüft — kein Laden,
   kein Entfernen, kein Vermerk.

## Begründung

Ein Register mit Quellen trennt „was gibt es“ (Definition, testbar ohne Netz) von „wie wird es geholt“ (Anbieter), sodass
die bewährten Download-Wege von Gesichtern und Motiven unverändert bleiben und neue Modelle nur eine Definition brauchen.
Die bestätigte Anfrage macht die Projektregel im Code prüfbar: ohne Dialog gibt es keinen Abruf. Prüfsummen der
entpackten Dateien erkennen beschädigte Modelle auch Jahre später; der Vermerk vermeidet teures Hashen beim Öffnen der
Liste. Update-Hinweise ohne automatischen Download halten an der Regel „nur geprüfte Fassungen“ fest.

## Konsequenzen

### Positiv
- Ein Ort für Größe, Version, Quelle, Lizenz und Zustand aller Bausteine; Prüfen und Entfernen ohne Explorer.
- „Foto verbessern“ registriert seine Modelle mit einer Zeile; Liste, Dialog, Fortschritt und Prüfung kommen mit.
- Die versteckte Sitzung lädt SigLIP 2 nicht mehr ein zweites Mal in den Container.

### Negativ
- Der Prüf-Vermerk fehlt bei Modellen, die vor 0.4.42 geladen wurden („Noch nicht geprüft.“, bis „Prüfen“ geklickt wird).
- Die Prüfsummen der buffalo_l-Dateien sind hart hinterlegt; ein anderes Archiv derselben Version würde als defekt gelten.

### Neutral
- Keine Migration, keine neuen Pakete. `ActivityKind.ModelDownload = 16` neu.
- Kein „Entfernen“ für Kartenkacheln (Grenze 500 MB und Speicher & Wartung), keine Installation von Ollama hier.

## Alternativen

- **Download-Logik der Anbieter in den Manager verschieben:** ein Weg für alles, aber Umbau der erprobten Archiv- bzw.
  Fortsetzen-Logik und ihrer Tests → verworfen; der allgemeine `FileSetModelPackageSource` ist der Weg für neue Modelle.
- **Automatische Update-Prüfung beim Start:** widerspricht „nur auf Klick, nichts senden“ → verworfen.
- **Neuere Herstellerdateien direkt laden:** ohne geprüfte Prüfsumme nicht vertretbar → nur Hinweis.
- **Zustand in der Datenbank:** Modelle gehören keinem Katalog (versteckte Sitzung teilt sie) → Vermerk im Modellordner.

## Referenzen

- Mockup `mockups/19-modelle-und-daten.html` (Entwurf, autonom erstellt)
- Code: `src/Lumora.Services/ModelPackages/`, `src/Lumora.Wpf/Settings/ModelPackages/`, Rauchtest
  `Shell/Smoke/SmokeScenario.ModelPackages.cs`
- GitHub-Issue #19
