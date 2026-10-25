# ADR-0069: Katalog-Sicherung vor Migrationen und portabler Modus

- **Status:** Accepted
- **Datum:** 2026-10-25
- **Entscheider:** Herbert Schrotter (Auftrag Issue #6), umgesetzt in 0.4.40
- **Bezogen auf:** ADR-0005 (Migrations-Disziplin), ADR-0026 (Speicherort wählbar), ADR-0029 (portable Auslieferung),
  ADR-0045/0048 (Migrations-Lehren), ADR-0049 (versteckte Bibliotheken), ADR-0056 (Datenordner `%LocalAppData%\Lumora`)

## Kontext

Lumora geht in den Produktivbetrieb (Issue #6). Bisher wendete `DatabaseInitializer` ausstehende EF-Migrationen beim Start
ohne Sicherung an — PLAN Kap. 14 sah ein versioniertes Backup vor, gebaut war es nicht. Die Migrations-Lehren aus ADR-0045/0048
(Tabellen-Neuaufbau löste FK-Kaskaden aus) zeigen, dass eine misslungene Migration Benutzerdaten (Bewertungen, Tags, Personen,
Alben) kosten kann. Zugleich will Herbert die Release-Version 0.5.0 in einem eigenen Ordner mit frischem Katalog betreiben,
getrennt von der Test-Installation unter `%LocalAppData%\Lumora`. Bisher legte jede Lumora.exe ihre Daten dort ab
(ADR-0029/0056); getrennt ging es nur über die Entwickler-Variable `LUMORA_APPDATA`.

## Entscheidung

1. **Sicherung vor Migrationen:** Hat ein bestehender Katalog (mindestens eine Migration angewendet) ausstehende Migrationen,
   legt `ICatalogBackupService` vorher eine Kopie per **SQLite-Online-Backup-API** an — im Ordner `backups` der App-Wurzel der
   Sitzung (normal `<Datenordner>\backups`, versteckt `<Bibliotheksordner>\.lumora\backups`). Scheitert sie, wird **nicht**
   migriert; der Start bricht mit Meldung ab. Aufbewahrung je Anlass: 5 vor Migration, 10 von Hand, 3 vor Wiederherstellung.
2. **Von Hand und Wiederherstellen:** Einstellungen › Speicher & Wartung sichert auf Knopfdruck, listet die Sicherungen und
   stellt nur nach Rückfrage wieder her — als **Vormerkung**, die beim nächsten Start der Sitzung vor jedem DB-Zugriff
   ausgeführt wird; der ersetzte Katalog bleibt als `…-vor-wiederherstellung.db`, die Vorschaubilder werden verworfen.
   Unbrauchbare Vormerkungen (Sicherung fehlt, beschädigt, von einer neueren Version mit unbekannten Migrationen) blockieren
   den Start nicht: Vormerkung gelöscht, Katalog unverändert, Grund in der Einstellungs-Karte.
3. **Portabler Modus:** `lumora.portable` oder ein Ordner `Daten` neben der EXE → App-Wurzel `<EXE-Ordner>\Daten`.
   Vorrang `LUMORA_APPDATA` > portabel > Standard (`AppDataLocationResolver`). `build/publish.ps1 -Portable` legt die
   Markierungsdatei ins Paket.

## Begründung

- **Online-Backup-API statt Dateikopie:** Der Katalog läuft im WAL-Modus; eine Kopie von `library.db` allein verlöre den
  WAL-Inhalt, eine Kopie aller drei Dateien bei offenen Verbindungen wäre nicht konsistent. `SqliteConnection.BackupDatabase`
  kopiert in einem Schritt einen stimmigen Stand inklusive WAL, auch während andere lesen. `VACUUM INTO` wäre gleichwertig,
  braucht aber mehr Zeit (Neuaufbau) — die Backup-API ist die direkte Kopie.
- **Kopie als einzelne Datei** (`journal_mode=DELETE`): eine Sicherung soll ohne `-wal`/`-shm` vollständig sein und sich
  kopieren/archivieren lassen. Beim Wiederherstellen stellt Lumora die Datei zurück auf WAL, bevor EF sie öffnet (EF prüft die
  Existenz über eine schreibgeschützte Verbindung; dort scheiterte das Umschalten).
- **Ordner `backups` in der App-Wurzel** (nicht neben einer per ADR-0026 umgezogenen DB): Logs, Speicherort-Datei, Cache und
  Modelle hängen schon dort; im Normalfall ist es derselbe Ordner wie die DB. Für versteckte Bibliotheken ist die App-Wurzel
  `.lumora` im Container — so landet nie etwas außerhalb (ADR-0049), ohne Sonderfall.
- **Vorschaubilder nach dem Wiederherstellen verwerfen:** Der wiederhergestellte Katalog bringt seine alte
  `sqlite_sequence` mit und vergibt FileIds, die nach der Sicherung schon benutzt wurden, erneut — FileId-basierte
  Cache-Dateien (Vorschaubilder, Gesichts-Ausschnitte) zeigten sonst fremde Fotos. Gezieltes Löschen nur der höheren Ids
  wäre möglich, hängt aber an jedem Cache-Format; ein frischer Cache ist einfach und sicher (seltene Aktion).
- **Keine Sicherung einer neueren Version in eine ältere:** EF hielte einen Katalog mit unbekannten Migrationen für
  aktuell und arbeitete auf einem fremden Schema; deshalb vergleicht Lumora `__EFMigrationsHistory` der Sicherung mit den
  eigenen Migrationen.
- **Sicherung nur bei bestehendem Katalog:** ein neuer Katalog hat nichts zu verlieren; ein Start ohne ausstehende Migration
  braucht keine Sicherung (keine Platzverschwendung bei jedem Start).
- **Ohne Sicherung keine Migration:** lieber ein klarer Startabbruch (Platz frei machen, erneut starten) als eine
  ungesicherte Migration; der Katalog bleibt dabei unverändert.
- **Wiederherstellen beim nächsten Start:** Im laufenden Betrieb halten Dienste, Caches und offene Verbindungen den Katalog;
  ein Austausch zur Laufzeit wäre fehleranfällig. Beim Start ist garantiert noch keine Verbindung offen. Die Vormerkung gilt je
  Katalog (Datei im jeweiligen `backups`-Ordner), deshalb funktioniert sie auch für versteckte Bibliotheken beim nächsten
  Öffnen.
- **Markierungsdatei für den portablen Modus:** einfach zu verstehen und zu prüfen (liegt sichtbar im Paket), kein Schalter in
  den Einstellungen, der erst nach dem Start wirken könnte; der Ordner `Daten` als zweites Kennzeichen erlaubt einen bestehenden
  portablen Datenordner auch ohne Markierung weiterzuverwenden. `LUMORA_APPDATA` behält Vorrang, damit Rauchtests gegen ein
  portables Paket nie dessen Daten berühren.

## Konsequenzen

### Positiv
- Jede Migration eines bestehenden Katalogs ist rückholbar; Fehlschläge der Sicherung führen nie zu halb migrierten Katalogen.
- Herbert kann 0.5.0 als eigenen Ordner mit frischem Katalog neben der Test-Installation betreiben.
- Info zeigt, welcher Datenordner aktiv ist (ohne Pfad) — Verwechslungen zwischen Installationen fallen auf.

### Negativ
- Jede Migration braucht zusätzlichen Platz in Höhe des Katalogs (bei 500k Dateien einige hundert MB) und etwas Startzeit.
- Im portablen Modus fehlen bereits heruntergeladene Modelle (buffalo_l, SigLIP 2) — sie liegen unter `Daten\models` und
  müssen dort erneut geladen oder hineinkopiert werden.
- Ein portabler Ordner an einem schreibgeschützten Ort (z. B. `Program Files`) funktioniert nicht.

### Neutral
- Vorschaubilder werden nicht gesichert (aus den Fotos ableitbar).
- Die Fotos selbst sichert Lumora nicht — das bleibt Herberts Sache (README „Produktivbetrieb“, 3-2-1-Regel).
- Keine Migration, keine neuen Pakete. Der Umzug aus `PhotoViewerV3` (ADR-0056) läuft nur im Standardmodus.
- Die Version im Dateinamen ist die Lumora-Version, die die Sicherung angelegt hat (vor Migration: die neue Version, die
  gleich migrieren will).

## Alternativen

- **Dateikopie von `library.db` nach `PRAGMA wal_checkpoint(TRUNCATE)`** (PLAN Kap. 14 alt) → verworfen: braucht exklusiven
  Zugriff, scheitert bei gleichzeitigen Lesern still; die Backup-API ist dafür gemacht.
- **Sicherung neben der DB (Datenordner aus ADR-0026)** → verworfen: bei einem umgezogenen Katalog auf einem Netz-/Wechsel-
  laufwerk lägen Sicherung und Original auf demselben Datenträger; die App-Wurzel ist lokal.
- **Wiederherstellen sofort zur Laufzeit** → verworfen (offene Verbindungen, Caches, Hintergrunddienste).
- **Portabler Modus per Startschalter `--portable`** → verworfen: eine Verknüpfung ohne Schalter öffnete still den
  Standardkatalog; die Markierungsdatei wirkt immer.
- **Eigener Schalter in den Einstellungen** → verworfen: die App-Wurzel muss vor dem ersten DB-Zugriff feststehen.

## Referenzen

- Code: `src/Lumora.Services/Database/Backup/CatalogBackupService.cs`, `src/Lumora.Services/Database/DatabaseInitializer.cs`,
  `src/Lumora.Services/Infrastructure/AppDataLocation.cs`, `src/Lumora.Wpf/Shell/AppDataRootOverride.cs`,
  `src/Lumora.Wpf/Settings/Backup/`, `build/publish.ps1` (`-Portable`)
- Tests: `tests/Lumora.Tests/Database/CatalogBackupServiceTests.cs`, `AppDataLocationResolverTests.cs`,
  `tests/Lumora.Wpf.Tests/Settings/CatalogBackupSettingsViewModelTests.cs`, Rauchtest-Schritt „Katalog-Sicherung“
- README „Produktivbetrieb“, PLAN.md Kap. 14
