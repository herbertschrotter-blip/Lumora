# ADR-0049: Versteckte Bibliotheken im verschlüsselten Container

- **Status:** Accepted (Nachtrag 0.4.14: Zusperren beendet den versteckten Modus, Reiter nur in geöffneter versteckter Bibliothek;
  Nachtrag 0.4.31: VHDX wird ausgeworfen wie im Explorer statt per `Dismount-DiskImage`)
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Anforderungen: Container, `_hidden`, Kennwort, Zusperren, Aushängen, Spurenfreiheit),
  Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ADR-0001/0002 (Host, DI, Serilog), ADR-0003 (Datenpfade), ADR-0026 (Speicherort des Katalogs),
  ADR-0035/0037 (Gesichter), ADR-0042 (Bibliotheks-Umschalter, Einstellungs-Reiter); ergänzt durch
  [ADR-0061](0061-container-assistent.md) (Container-Assistent: neuen BitLocker-/VeraCrypt-Container erstellen)

## Kontext

Herbert möchte einzelne Bibliotheken so führen, dass ohne Wissen niemand ihre Existenz bemerkt: Originale liegen in
einem verschlüsselten Container (VeraCrypt-Datei oder BitLocker-VHDX), den er selbst einhängt. Lumora hatte bis 0.2.51
genau EINEN Katalog (DB, `thumbs\`, Gesichtsausschnitte, Protokolle) unter `%LocalAppData%\Lumora` bzw. im
Ordner aus `database-location.json`; alle Dienste sind Singletons mit eingebrannten Pfaden, Nachrichten laufen über den
globalen `WeakReferenceMessenger.Default`, protokolliert wird über den globalen Serilog-Logger. Ein versteckter Katalog
muss deshalb vollständig getrennt laufen — ohne Neustart, ohne Spuren außerhalb des Containers.

## Entscheidung

1. **Eigener Katalog je versteckter Bibliothek** in `<Bibliotheksordner>\.lumora\` im Container: `library.db`,
   `thumbs\` (Vorschaubilder, Gesichtsausschnitte), `logs\lumora-*.log`, `cache\`, `temp\` und die Kenndatei
   `hidden-library.json` (Name, Kennwort-Prüfwert, Einstellungen). Lumora verschlüsselt selbst nichts. Der Ordner
   bekommt die Attribute *versteckt + System + nicht indizieren*; Scanner, Flatten und der Zielbaum „Ins Archiv“
   überspringen `.lumora` zusätzlich am Namen (`CatalogFolders`).
2. **Katalogwechsel = neuer DI-Container** (Katalog-Sitzung). `App` implementiert `ICatalogHostSwitcher`: alle
   Fenster schließen (Hauptfenster, Lightbox, Dialoge), Host anhalten und entsorgen (Hintergrunddienste, DB-Verbindungen
   ohne Pooling, Caches), `WeakReferenceMessenger.Default.Reset()`, GC; dann Host für den Zielkatalog bauen, Migrationen
   anwenden, Dienste starten, neues Hauptfenster. Das gilt in beide Richtungen; der Prozess läuft weiter.
3. **`ICatalogContext` / `ICatalogStoragePaths`** (Services/Infrastructure): je Sitzung `IsHidden`, `CatalogRoot`,
   `DatabasePath`, `ThumbnailsRoot`, `LogsRoot`, `CacheRoot`, `TempRoot`. Normal: `cache\`/`temp\` unter der App-Wurzel;
   versteckt: in `.lumora`. Neue Module legen Dateien außerhalb der DB nur dort ab. Andere Module richten sich nach
   `IsHidden` (z. B. „Verknüpfen“ ausblenden — versteckte Bibliotheken werden nie gemeinsam mit anderen angezeigt).
4. **Startschalter `_hidden`** (`--hidden`, `/hidden`): ohne ihn gibt es keine Einträge, keinen Reiter, keine Suche.
   Mit ihm sucht Lumora auf allen eingehängten lokalen Laufwerken (`Fixed`/RAM, bereit; nicht Netz, CD/DVD,
   Wechselmedien) in der Wurzel und eine Ebene darunter nach `.lumora\hidden-library.json` und zeigt die Funde im
   Bibliotheks-Umschalter und im Reiter „Versteckte Bibliotheken“. Gemerkt wird nichts.
5. **Lumora-Kennwort**: Prüfwert PBKDF2-HMAC-SHA-256, 16 Byte Salz, 600 000 Iterationen (gespeichert, später erhöhbar),
   Vergleich in konstanter Zeit, Mindestlänge 6. **Zurücksetzen** nur, wenn im eingehängten Container
   `.lumora\reset-password.txt` liegt (Inhalt egal): „Öffnen“ bietet dann „Kennwort zurücksetzen“ an, danach wird die
   Datei gelöscht. Wer sie anlegen kann, hat den Container schon mit dessen Kennwort geöffnet.
6. **Automatisch zusperren** nach einstellbarer Untätigkeit (Standard 10 min; Maus/Tastatur in Lumora über den
   WPF-`InputManager`, künstliche MouseMove ohne Zeigerbewegung zählen nicht; Prüfung alle 10 s). Läuft gerade ein
   Scan oder Datei-Vorgang (Scan-Sperre belegt), wartet das automatische Zusperren bis zu dessen Ende.
7. **Aushängen** beim Zusperren und Beenden (abschaltbar, Standard an): VeraCrypt-Volume (Gerätename
   `\Device\VeraCryptVolumeX` per `QueryDosDevice`) mit `VeraCrypt.exe /dismount X /quit /silent` (ohne `/force`;
   bei Misserfolg `/unmount` für neuere Fassungen), sonst für eine eingehängte VHD/VHDX (`BusType = File Backed Virtual`)
   bis 0.4.30 PowerShell `Dismount-DiskImage`, seit 0.4.31 Auswerfen wie im Explorer (Nachtrag 0.4.31); sonst Hinweis. Nie das Systemlaufwerk. „Container öffnen…“ startet
   `VeraCrypt.exe /volume <Datei> /letter X /quit` — VeraCrypt fragt das Kennwort in seinem Fenster. Alles über
   `IContainerTools`/`IProcessRunner` (Tests und Rauchtest mit Fakes; Lumora installiert nichts).
8. **Spurenfreiheit**: jede Sitzung hat ihren eigenen Serilog-Logger (versteckt: nur Datei in `.lumora\logs`, keine
   Konsole); der globale Logger leitet an die aktive Sitzung weiter (`CatalogLogRouter`). Nachzügler einer
   geschlossenen versteckten Sitzung schreiben in deren geschlossenen Logger (verworfen). Absturzberichte nach einer
   versteckten Sitzung im normalen Protokoll nur mit Ausnahmetyp. Das normale Protokoll enthält zur versteckten Sitzung
   nichts. Dateidialoge mit `AddToRecent = false`; der Anlegen-Dialog wählt Laufwerk + Ordnername ohne Dateidialog.
   In einer versteckten Sitzung sperrt Lumora „Im Explorer zeigen“, „Ordner öffnen“ und „Mit Standard-App öffnen“
   (`HiddenSessionPrivacy`). Katalog-Umzug ist gesperrt (`HiddenCatalogRelocationBlocker`). Gesichtsmodelle werden
   aus dem normalen Modellordner gelesen (nicht persönlich), Gesichts-Lernen läuft im versteckten Katalog.
9. **Einstellungen der versteckten Bibliothek** (Minuten, Aushängen) stehen in ihrer Kenndatei, nie in den normalen
   Einstellungen.

## Begründung

- Ein neuer Container je Katalog schließt *garantiert* alles: Singletons mit Pfaden, Caches, Bilder im Speicher,
  Hintergrund-Tasks, Messenger-Empfänger. Die Alternative — jeden Dienst auf einen umschaltbaren Kontext umzubauen —
  hätte über hundert Singletons berührt und jede vergessene Stelle wäre ein Leck gewesen (normale Daten in der versteckten
  Sitzung oder umgekehrt). Die Dienste mussten dafür nicht geändert werden; sie bekommen ihre Pfade wie bisher aus
  `IAppDataPaths`, die je Sitzung auf `.lumora` zeigen.
- `WeakReferenceMessenger.Default.Reset()` nach dem Entsorgen ist nötig, weil Empfänger erst beim nächsten GC verschwinden
  und sonst Nachrichten der neuen Sitzung bekämen.
- Eigene Logger je Sitzung statt einer einzigen Umleitung: Hintergrund-Tasks der alten Sitzung können nach dem Wechsel
  noch schreiben — sie dürfen nie im normalen Protokoll landen.
- PBKDF2 aus .NET (`Rfc2898DeriveBytes.Pbkdf2`) braucht kein Paket; 600 000 Iterationen entsprechen der OWASP-Empfehlung
  2023 für PBKDF2-HMAC-SHA256 (≈ 0,3–0,5 s je Prüfung). Das Kennwort ist eine Sperre der Oberfläche, der Schutz der Daten
  ist der Container.
- Die Suche nur in Wurzel + einer Ebene ist schnell und weckt keine Netz-/Wechsellaufwerke; deshalb verlangt „Anlegen“
  einen Ordner direkt auf dem Laufwerk (oder das Laufwerk selbst).

## Konsequenzen

### Positiv
- Wechsel normal ↔ versteckt ohne Neustart (≈ 1–2 s im Rauchtest), versteckter Katalog danach vollständig geschlossen.
- Neue Module bekommen mit `ICatalogContext`/`ICatalogStoragePaths` einen klaren Ort für Dateien außerhalb der DB.
- Rauchtest-Schritt „Versteckte Bibliothek“ und ein Unit-Test prüfen die Spurenfreiheit im AppData-Testordner.

### Negativ
- Jeder Wechsel baut die Oberfläche neu auf (Galerie-Position, Filter und offene Dialoge gehen verloren).
- Zusperren mitten in einem modalen Dialog schließt ihn; Code danach kann auf entsorgte Dienste treffen (wird
  protokolliert). Automatisches Zusperren wartet deshalb auf laufende Scans/Datei-Vorgänge — ein sehr langer Scan
  verzögert das Zusperren.
- Grenzen außerhalb von Lumora: Windows merkt sich im Dateidialog ggf. den zuletzt besuchten Ordner
  (`LastVisitedPidlMRU`), VeraCrypt selbst führt ggf. eine Liste zuletzt verwendeter Container, die Auslagerungsdatei/der
  Ruhezustand können Speicherinhalte enthalten; die Prozess-Befehlszeile zeigt `_hidden`.

### Neutral
- `App.ConfigureServices` nimmt jetzt die Katalog-Sitzung (`CatalogSessionSetup`) entgegen; neue Registrierungen wie
  bisher dort. Hintergrunddienste starten in `App.StartBackgroundServicesAsync` (für jede Sitzung).
- Der Rauchtest simuliert `_hidden` (`LUMORA_SMOKE_HIDDEN`, Standard an; „0“ = aus), nutzt einen Temp-Ordner als
  Container und Fake-Aushängen; die Schritte nach dem Wechsel laufen mit den Diensten der neuen Sitzung.

## Alternativen

- **Ein umschaltbarer Kontext in allen Diensten** (Verbindung/Pfade dynamisch) → verworfen: über hundert Singletons mit
  Caches und eigenem Zustand, Lecks schwer auszuschließen.
- **Neustart der App mit anderem Datenordner** (wie ADR-0026) → verworfen: Herbert will nahtlos wechseln; ein Neustart
  hinterließe zudem Befehlszeilen-/Startspuren.
- **Eigene Verschlüsselung in Lumora** (SQLCipher, verschlüsselte Thumbnails) → verworfen (Herberts Vorgabe): der
  Container verschlüsselt alles inklusive Originale, Lumora bleibt einfach.
- **Ort der versteckten Bibliotheken merken** → verworfen: wäre selbst eine Spur; die Suche ist schnell genug.

## Referenzen

- Code: `src/Lumora.Services/HiddenLibraries/*`, `src/Lumora.Services/Infrastructure/CatalogContext.cs`,
  `src/Lumora.Wpf/Shell/HiddenLibraries/*`, `src/Lumora.Wpf/App.xaml.cs`
- Tests: `tests/Lumora.Tests/HiddenLibraries/*`, `tests/Lumora.Wpf.Tests/HiddenLibraries/*`,
  Rauchtest-Schritt in `Shell/Smoke/SmokeScenario.HiddenLibrary.cs`
- OWASP Password Storage Cheat Sheet (PBKDF2-HMAC-SHA256: 600 000 Iterationen); VeraCrypt-Dokumentation
  „Command Line Usage“ (`/volume`, `/letter`, `/dismount`, `/quit`, `/silent`)

## Nachtrag 0.4.14 — Nach dem Zusperren keine Spur mehr (Herbert, 2026-10-22)

Herbert startete mit `_hidden`, sperrte zu und sah in der normalen Bibliothek weiter den Reiter „Versteckte
Bibliotheken“ (und Einträge im Umschalter): Wer danach an den PC kommt, erfährt so, dass es versteckte Bibliotheken gibt.
Entscheidung per Auswahl:
- **Zusperren beendet den versteckten Modus für den Rest der Laufzeit** (`CatalogSessionManager.IsHiddenModeEnabled`
  = Startschalter und noch nicht zugesperrt). Die normale Sitzung danach sieht aus wie ein normaler Start: kein Reiter,
  keine Einträge im Bibliotheks-Umschalter, kein Suchen auf den Laufwerken. Wieder öffnen nur nach einem Neustart mit
  `_hidden` — gilt für Zusperren von Hand, nach Untätigkeit und über „Zusperren (zurück zur normalen Bibliothek)“.
- **Der Reiter „Versteckte Bibliotheken“ erscheint nur, solange eine versteckte Bibliothek geöffnet ist**
  (Kennwort, Zusperr-Zeit, Aushängen, Jetzt zusperren). In der normalen Bibliothek mit `_hidden` laufen Anlegen,
  Öffnen und „Container öffnen…“ über den Bibliotheks-Umschalter.
- Rauchtest: nach dem ersten Zusperren wird der Modus geprüft und ein Neustart mit `_hidden` simuliert
  (`RestartHiddenModeForSmoke`, nur Rauchtest), dann weiter wie bisher.

## Nachtrag 0.4.31 — VHDX auswerfen wie im Explorer (Herbert, 2026-10-25)

**Befund:** Beim Beenden mit eingehängter BitLocker-VHDX kam jedes Mal „Der Container wurde nicht ausgehängt: Laufwerk X:
konnte nicht ausgehängt werden“. `Dismount-DiskImage` braucht Administratorrechte; Lumora läuft ohne. Explorer ›
„Auswerfen“ geht dagegen ohne Administratorrechte.

**Entscheidung:**
- Erkennen über die Storage-Verwaltung von Windows (`MSFT_Partition` → `MSFT_Disk.BusType` = 15 „File Backed Virtual“,
  WMI-Namensraum `root\Microsoft\Windows\Storage`, nur lesend) statt PowerShell `Get-Partition`/`Get-Disk`.
- Auswerfen über das Shell-Verb „Eject“ auf dem Laufwerk in „Dieser PC“ (`Shell.Application`, STA-Thread) — derselbe Weg
  wie Explorer › „Auswerfen“. Danach höchstens 20 s warten, bis das Laufwerk weg ist.
- Neue Schnittstelle `IVirtualDiskEjector` (Windows: `ShellVirtualDiskEjector`; Tests und Rauchtest mit Fakes, nie echt).
  `ContainerTools` startet für die VHDX keinen Prozess mehr — passt zur Linie von ADR-0061 Nachtrag 0.4.26 (kein
  PowerShell im Container-Weg, Kaspersky).
- Sind noch Dateien offen, meldet Windows das selbst; Lumora zeigt danach wie bisher den Hinweis auf „Auswerfen“.

**Konsequenzen:** Der echte Lauf auf Herberts PC (X:, BitLocker-VHDX) steht aus — Tests prüfen nur mit Fakes und, dass
`ContainerTools` kein PowerShell mehr enthält.
