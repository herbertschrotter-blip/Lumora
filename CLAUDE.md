# Lumora — Projektprofil für Claude

(bis 0.2.48 „PhotoViewer V3“, Projekt-/Namespace-Namen und Datenordner bis 0.4.2 `PhotoViewerV3`)

Native Windows-Desktop-App (C# / .NET 10 / WPF / WPF-UI) zum Ansehen, Bewerten, Sortieren und
Verschieben von Foto- und Video-Bibliotheken (50k–500k Dateien) mit SQLite-Katalog. Nachfolger
der PowerShell-Web-App V2 (`D:\OneDrive\Dokumente\02 Arbeit\05 Vorlagen - Scripte\03_Foto-Viewer-V2`), die bis zur
Ablösung im Alltag läuft.

- Projektordner `D:\Code\Lumora` mit `repo\` (dieses Git-Repo) und `werkzeuge\` (seit 0.4.11; 0.4.2–0.4.10 lag das Repo direkt in `D:\Code\Lumora`, davor in OneDrive); GitHub `herbertschrotter-blip/Lumora` (bis 2026-10-21 `03_Foto-Viewer-V3`, alte URL leitet weiter) ist Sicherung und Abgleich zwischen Geräten.
- Werkzeuge außerhalb des Repos (gehören nie ins Repo): `D:\Code\Lumora\werkzeuge` — `testlib\` (Rauchtest-Testbibliothek, 43 Dateien), `scripts\run-smoke-v.ps1` (Rauchtest gegen einen Worktree, Ablage in `runs\`), Konflikt-Helfer (`conflib.py`, `resolve_cl.py`, `hunks.py`, `builderrs.py`), `scripts\brief-common.md` (gemeinsame Agenten-Regeln), `models\` (buffalo_l, SigLIP 2 nur für Tests — nie ins Repo), `geodata\` (GeoNames-Rohdaten für `build/geonames`).
- Plan: `PLAN.md` · Entscheidungen: `docs/adr/` · Messungen: `docs/spikes/` · Mockups: `mockups/`
- Doku, Kommentare und Commit-Messages auf Deutsch; Code-Bezeichner auf Englisch.

## Skill-Profil

Branch-Policy: current

### Commit
- Module: Domain, Data, Services, Wpf, Tests, Docs, Deps, Build
- Versionsquelle: changelog:CHANGELOG.md
- Versionsregel: Vorabversion 0.x — jede eingespielte Änderung zählt hoch; Feature → MINOR nur beim Abschluss einer Welle/eines Releases, sonst PATCH
- Push-Policy: nie automatisch — Herbert pusht selbst (`git push origin main --tags`); am Ende einer Sitzung den Push anbieten
- Pre-Commit-Checks: build, unit
- Doku-Check:
  - CHANGELOG.md → bei jedem Commit (oberster Eintrag = Version)
  - docs/adr/ → neue oder revidierte Architekturentscheidung (neues ADR; altes auf „Superseded“, nie überschreiben)
  - docs/adr/README.md → neues ADR in die Übersicht
  - PLAN.md → Scope-, Phasen- oder Schema-Änderung
  - README.md → neue Bedienung oder Voraussetzungen

### Checks
- build: `dotnet build Lumora.slnx -c Debug --nologo -v q` (src/**, tests/**, *.props, *.slnx)
- unit: `dotnet test tests/Lumora.Tests --nologo -v q` (src/**, tests/Lumora.Tests/**)

## Code-Profil
- Stacks: csharp-wpf
- Build/Lint: siehe Checks (`build`)
- Pflicht-Docs: PLAN.md Kap. 3 (Solution-Layout), 4 (Schema), 12 (Offline-vs-Missing), 13 (Thumbnails) → betroffene ADRs in docs/adr/ → docs/spikes/ bei Performance-Fragen
- Aufgabenquelle: GitHub Issues in `herbertschrotter-blip/Lumora` (seit 0.4.34; Labels „in Arbeit“, „Entscheidung“, „Test bei Herbert“, „geplant“, `bug`, `enhancement`) plus Auftrag im Chat bzw. Brief des Agenten (Ziel + Akzeptanzkriterien); kein ClickUp. Das Repo ist **öffentlich**: Issue-Texte neutral halten — keine Namen echter Personen, keine Dateinamen/Pfade echter Fotos, keine Details zu Herberts Containern/Laufwerken. Commits schließen Issues mit „(closes #n)“ in der zweiten Zeile.
- Schichten / Kopplung: Domain (keine Abhängigkeiten) ← Data (EF Core CRUD + RawSql für Bulk/FTS) ← Services (WPF-frei: Interfaces + Implementierungen, `AddLumoraServices`) ← Wpf (Views/ViewModels mit CommunityToolkit.Mvvm, DI in `App.xaml.cs`). Entkopplung zwischen Modulen über `WeakReferenceMessenger` mit den Records aus `Services/Messaging`. Neuer Service = Interface + Registrierung; neues Modul registriert sich in einer eigenen Datei `XyzServiceCollectionExtensions.cs` (`AddXyz()`), die in `App.xaml.cs` bzw. `AddLumoraServices` mit einer Zeile aufgerufen wird.
- Tests: unit (xUnit + FluentAssertions, `tests/Lumora.Tests`); Perf- und Spike-Harness: `tests/Lumora.PerfTests`
- Auslieferung: keine nach jedem Commit (Alltag: lokaler Debug-Build). Portables Paket bei Bedarf bzw. zum Release: `pwsh -File build/publish.ps1` (self-contained win-x64 → `artifacts/Lumora-<Version>-win-x64` + ZIP), danach `pwsh -File build/run-smoke.ps1 -Exe <Paket>\Lumora.exe -TestLib <Testbibliothek>`. Produktname „Lumora“ (ADR-0047); seit 0.4.3 auch Projekte/Namespaces `Lumora.*`, Datenordner `%LocalAppData%\Lumora` (einmaliger Umzug aus `PhotoViewerV3` beim Start), Variablen `LUMORA_*`, Protokolle `lumora-*.log` (ADR-0056)
- Mockup-Pflicht: nein (HTML-Mockups in `mockups/` sind die Design-Vorlage)
- Notiz-Ort: PLAN.md Kap. 15 (Risiken) bzw. CHANGELOG.md Abschnitt „Offen“
- Entscheidungs-Ort: docs/adr/

## Regeln für Arbeit im Repo
- Kein `git push` ohne ausdrücklichen Auftrag.
- Keine WPF-Referenz in Domain/Data/Services (WIC-Renderer lebt deshalb im Wpf-Projekt hinter einem Services-Interface).
- Seiten-Namespaces dürfen keine Entitätsnamen verdecken (deshalb `Lumora.Libraries`, nicht `.Library`).
- Thumbnail-Dateien sind FileId-basiert (`thumbs\{libraryId}\{fileId}_{size}_still.jpg`). FileIds bleiben stabil; wer Files-Zeilen löscht, entfernt auch Thumbnails-Zeile, Cache-Datei und FTS-Eintrag (Katalog-Entfernen mit Rückgängig verschiebt Cache-Datei + Zeile in die Ablage `thumbs\{libraryId}\_undo\{batchId}\`, 30 Tage, ADR-0065 Nachtrag — die Ablage nicht wegräumen). Album-Einträge (`AlbumItems`) und Ordner-Verknüpfungen (`FolderLinks`) gehen per FK-Kaskade mit — Datei-Operationen sichern sie vorher zur `FileOperation` (`FileLinkUndoStore.CaptureAsync`, ADR-0054), damit Rückgängig sie wiederherstellt; wer FileIds neu vergibt („Neu aufbauen“), trägt sie über `UserDataCarryOver` mit.
- `FilesFts` ist eine FTS5-External-Content-Tabelle (`content='Files'`, `rowid = Files.Id`) ohne Trigger. Nach INSERT: `INSERT INTO FilesFts(rowid, Name, Extension) …`. Vor DELETE/UPDATE: `INSERT INTO FilesFts(FilesFts, rowid, Name, Extension) VALUES('delete', …)` mit den ALTEN Werten. Notnagel: `INSERT INTO FilesFts(FilesFts) VALUES('rebuild')`.
- SQLite: WAL, `busy_timeout` 5000; lange Schreibläufe in einer Transaktion; nur eine schreibende Verbindung zur Zeit (Single-Writer, z. B. `SemaphoreSlim`).
- Datei-Operationen immer mit `OperationBatch` + `FileOperation` protokollieren (ADR-0012); Papierkorb statt Hard-Delete.
- Keine externen Sample-Assets im Produktivpfad (nur Spike-Code darf laden). Ausnahme: Laufzeit-Modelle (Gesichtserkennung buffalo_l), die der Benutzer nach Bestätigung selbst herunterlädt — nie ins Repo, nie in die portable Version (Lizenz nur nicht-kommerziell, ADR-0035).
- Keine `Wpf.Ui.Controls.MessageBox` verwenden (Layout-Schleife bei umbrechendem Text mit Eingabefeld, v0.2.0-Hänger). Dialoge laufen über `IDialogService` bzw. `Shell/Dialogs/AppDialogWindow`.
- Fenster erben von `Shell/Dialogs/AppFluentWindow` statt direkt von `FluentWindow` (WPF-UI-4-TitleBar wirft sonst „Hwnd mit dem Wert Null“, wenn ein Fenster vor dem ersten Zeichnen geschlossen wird; 0.2.13).
- Galerie-Kacheln bleiben im UI-Automation-Baum Blätter (`GalleryListBox`/`GalleryListBoxItem`); viele Vorschaubilder gebündelt mit Background-Priorität an den UI-Thread geben. Sonst blockiert `AutomationPeer.UpdateSubtree` den UI-Thread, sobald ein Automation-Client lauscht (Visual Studio Live Visual Tree).
