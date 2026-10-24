# ADR-0064: Sicherheits-Check der versteckten Bibliothek

- **Status:** Accepted
- **Datum:** 2026-10-24
- **Entscheider:** Herbert Schrotter (Anforderungen, Mockup 14 abgenommen 2026-10-24; harte Regel „nur lesen, nichts an
  Windows ändern, kein PowerShell/Admin/UAC“), Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken, Nachtrag 0.4.14 „keine Spur
  nach dem Zusperren“), [ADR-0061](0061-container-assistent.md) (Container-Assistent, Nachtrag 0.4.26 „kein PowerShell“)

## Kontext

Lumora hinterlässt von einer versteckten Bibliothek außerhalb des Containers nichts (ADR-0049). Windows selbst merkt
sich aber an vielen Stellen, was auf dem PC passiert: Auslagerungsdatei und Ruhezustand (Arbeitsspeicher samt
entschlüsselter Vorschaubilder), Zuletzt-Listen, Aktivitätsverlauf, Explorer-Miniaturbilder, der Suchindex, VeraCrypts
Verlauf — und ohne BitLocker liegt das alles lesbar auf C:. README „Grenzen“ nannte das bisher nur als Text. Herbert will
in der geöffneten versteckten Sitzung sehen, welche dieser Stellen offen sind, und die passende Windows-Einstellung mit
einem Klick öffnen. Nach dem Kaspersky-Fehlalarm vom 23.10.2026 (ADR-0061, Nachtrag) darf nichts davon wie Schadsoftware
aussehen: Lumora **liest nur** und **ändert keine Windows-Einstellung** — kein Schreiben in Registry/Richtlinien, kein
powercfg, keine Administratorrechte, keine UAC, kein PowerShell.

## Entscheidung

1. **Reiter „Sicherheits-Check“** in Einstellungen, hinter „Versteckte Bibliotheken“ — nur bei
   `{ IsHiddenModeEnabled: true, IsHiddenSessionActive: true }` (wie ADR-0049 Nachtrag 0.4.14). Alle Dienste
   (`AddSecurityCheckUi` → `AddSecurityCheck`) werden **nur im DI-Container einer versteckten Sitzung** registriert; die
   normale Sitzung hat keinen Reiter, keinen Dienst, keinen Statusleisten-Text und keine Einstellung.
2. **Prüfungen** (`Lumora.Services/HiddenLibraries/SecurityCheck`, WPF-frei): je Punkt eine kleine Klasse
   (`ISecurityCheck`) über austauschbare Leser — `IRegistryReader` (HKLM/HKCU, 64-Bit-Ansicht, `OpenSubKey` nur lesend),
   `IVolumePropertyReader` (Shell-Eigenschaft direkt aus C#), `IUserFileReader` (VeraCrypt-Einstellungen),
   `IVeraCryptLocator`. Ergebnis je Punkt: **Erfüllt / Nicht erfüllt / Unbekannt / Nicht zutreffend**. Ein Leser-Fehler
   wird „Unbekannt“ mit Hinweis — nie ein Absturz; Unsicheres wird „Unbekannt“ statt geraten.
3. **BitLocker ohne PowerShell:** `ShellVolumePropertyReader` holt den Eigenschaftsschlüssel mit
   `PSGetPropertyKeyFromName("System.Volume.BitLockerProtection")`, den Eigenschaftsspeicher des Laufwerks mit
   `SHGetPropertyStoreFromParsingName("C:\")` und den Wert mit `IPropertyStore.GetValue` (eigener STA-Thread, 10 s
   Zeitgrenze; die Schnittstelle ist nur bis `GetValue` deklariert). `BitLockerPlatform` (Container-Assistent) bleibt
   unverändert.
4. **Gefahren-Stufe** (Ausgeschalteter PC / Mitbenutzer / Fachmännische Untersuchung, Standard „Mitbenutzer“) und die
   Lumora-Option **„Lumora nach dem Zusperren ganz beenden“** (Standard aus) stehen in der **Settings-Tabelle des
   versteckten Katalogs** (`securitycheck.level`, `securitycheck.exitAfterLock`) — keine Migration.
5. **„Ganz beenden“:** `CatalogSessionManager.LockAsync` (von Hand und nach Untätigkeit) liest die Einstellung, solange die
   versteckten Dienste leben — zuerst den Stand im Speicher des Dienstes (ein eben umgelegter Schalter gilt sofort, auch
   wenn das Speichern noch läuft), sonst die Settings-Tabelle (5 s Zeitgrenze) —, schließt wie bisher zuerst Lightbox/Dialoge, beendet den versteckten Modus und ruft
   `ICatalogHostSwitcher.ShutdownAfterLock()` (Standard: `Application.Shutdown()`) statt die normale Bibliothek
   aufzubauen. `App.OnExit` schließt dann die Sitzung wie beim normalen Beenden (`PrepareExit`, Protokoll im Container,
   Aushängen nach Einstellung). Nicht lesbar oder Beenden nicht möglich → normal zusperren. Ändern einer Option liest
   vorher die gespeicherten Werte, damit der Standard die andere Option nicht überschreibt.
6. **„Einstellung öffnen“** über `SecuritySettingsLauncher` mit **fester Positivliste** (Ziel-Enum, keine freien
   Befehlszeilen; zweite Prüfung vor dem Start): `%windir%\System32\control.exe` mit `/name Microsoft.BitLockerDriveEncryption`,
   `/name Microsoft.PowerOptions /page pageGlobalSettings`, `folders`, `/name Microsoft.IndexingOptions`;
   `%windir%\System32\secpol.msc` (nur wenn vorhanden); `ms-settings:personalization-start`,
   `ms-settings:privacy-activityhistory`, `ms-settings:deviceencryption` (Home ohne BitLocker-Systemsteuerung);
   `VeraCrypt.exe` vom Locator (nur vorhandene Datei dieses Namens, voller lokaler Pfad, ohne Argumente). Fehlt ein Ziel,
   ist der Knopf aus und die Anleitung (ⓘ) zeigt den Weg.
7. **Statusleiste** „Sicherheits-Check: x von y erfüllt“: erste Prüfung 2 s nach dem Öffnen der versteckten Sitzung
   (`SecurityCheckStartup`, Hintergrunddienst nur im versteckten Container) und nach jedem „Erneut prüfen“ bzw. Umstellen.
   Wird die Sitzung geschlossen (Container entsorgt → `SecurityCheckService.Dispose`), bricht eine laufende Prüfung ab
   und sendet nichts mehr — ein spätes Ergebnis landet nie in der Statusleiste der normalen Sitzung.
8. **Protokoll:** nur Punkt und Fehlertyp (`Sicherheits-Check: Punkt Hibernation nicht lesbar (UnauthorizedAccessException)`),
   nie Pfade, Laufwerksbuchstaben oder Ausnahme-Objekte; es landet ohnehin nur im Protokoll im Container.

### Prüfungen und Quellen

| Punkt | Gelesen (nur lesend) | Erfüllt, wenn | Quelle / Begründung |
|---|---|---|---|
| BitLocker C: | Shell-Eigenschaft `System.Volume.BitLockerProtection` des Windows-Laufwerks | 1 (an) oder 6 (gesperrt); 2 aus, 3 läuft noch, 4 entschlüsselt, 5 angehalten, 8 wartet auf Aktivierung = nicht erfüllt; sonst Unbekannt (Home: Hinweis Geräteverschlüsselung/VeraCrypt) | Werte wie ADR-0061 (`BitLockerProtection`); dieselbe Quelle wie das Schloss im Explorer |
| Ruhezustand | `HKLM\SYSTEM\CurrentControlSet\Control\Power` `HibernateEnabled`, ersatzweise `HibernateEnabledDefault` | 0 | Microsoft: `powercfg /hibernate off` setzt `HibernateEnabled` = 0 und löscht hiberfil.sys; fehlen beide → Unbekannt |
| Schnellstart | `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power` `HiberbootEnabled`; Richtlinie `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` `HiberbootEnabled` | Ruhezustand aus (dann gibt es keinen Schnellstart) oder Wert 0; Richtlinie 1 („Schnellstart erzwingen“) = nicht erfüllt; fehlt → Unbekannt | Gruppenrichtlinie „Verwendung des Schnellstarts anfordern“ (System › Herunterfahren) |
| Auslagerungsdatei leeren | `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management` `ClearPageFileAtShutdown`, `PagingFiles` | 1, oder keine Auslagerungsdatei (`PagingFiles` leer); fehlt → nicht erfüllt (dokumentierter Standard „deaktiviert“) | Microsoft Learn „Shutdown: Clear virtual memory pagefile“: die Sicherheitsrichtlinie schreibt genau diesen Wert — es gibt keinen getrennten Richtlinienwert |
| Zuletzt geöffnet / Sprunglisten | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` `Start_TrackDocs`; Richtlinie `…\Policies\Explorer` `NoRecentDocsHistory` (HKLM, dann HKCU) | Richtlinie 1 oder Wert 0; fehlt → nicht erfüllt (Windows-Standard an) | Gruppenrichtlinie „Keinen Verlauf zuletzt geöffneter Dokumente speichern“ |
| Aktivitätsverlauf | Richtlinien `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` `PublishUserActivities`, `EnableActivityFeed` | eine davon 0; sonst **Unbekannt** | Gruppenrichtlinien „Veröffentlichen von Benutzeraktivitäten zulassen“ / „Aktivitätsfeed aktivieren“ (0 = deaktiviert). Wo der Schalter der Einstellungen gespeichert wird, ist nicht dokumentiert (Recherche: nur die Richtlinien sind belegt; neuere Windows-11-Versionen haben die Seite teils nicht mehr) → nicht raten |
| Miniaturbilder | `HKCU\…\Explorer\Advanced` `IconsOnly`; Richtlinien `…\Policies\Explorer` `DisableThumbnails`, `NoThumbnailCache` | `IconsOnly` = 1 oder eine Richtlinie 1; fehlt → nicht erfüllt | `DisableThumbnails` = „nur Symbole anzeigen“, `NoThumbnailCache` = „Miniaturbilder nicht zwischenspeichern“. `DisableThumbnailCache` betrifft nur `Thumbs.db` im Ordner selbst (also im verschlüsselten Container) und zählt nicht |
| Windows-Suche auf dem Container-Laufwerk | Dienst `HKLM\SYSTEM\CurrentControlSet\Services\WSearch` `Start`; Regeln `HKLM\SOFTWARE\Microsoft\Windows Search\CrawlScopeManager\Windows\SystemIndex\WorkingSetRules\*` (`URL` = `file:///X:\[Volume-Kennung]\…`, `Include`) | Dienst abgeschaltet (4) oder keine Einschluss-Regel auf dem Laufwerk; eine Regel für alles (`file:///*`) zählt nur, wenn die Laufwerkswurzel nicht ausdrücklich ausgeschlossen ist (spezifischste Regel gewinnt); Regeln nicht lesbar → Unbekannt | Speicher des Crawl-Scope-Managers (Microsoft „Managing Scope Rules“); ohne Administratorrechte lesbar (auf Herberts PC geprüft). Das Laufwerks-Attribut „nicht für Inhaltsindizierung“ zählt nicht: es verhindert nur die Inhalte, Dateinamen landen trotzdem im Index (die Anleitung empfiehlt es zusätzlich) |
| VeraCrypt-Verlauf | `%APPDATA%\VeraCrypt\Configuration.xml` `<config key="SaveVolumeHistory">`, `History.xml` | 0 bzw. fehlt (VeraCrypt-Standard, Quelltext `Mount.c`: `ConfigReadCompareInt("SaveVolumeHistory", FALSE, …)`) und keine alte Liste in `History.xml`; kaputtes XML/anderer Wert → Unbekannt; ohne VeraCrypt **nicht zutreffend** | VeraCrypt-Dokumentation „System Files & Application Data“ |
| Lumora nach dem Zusperren beenden | Settings-Tabelle des versteckten Katalogs | an | Lumora-eigene Option (Arbeitsspeicher) |
| Nicht abschaltbar (Info) | — | zählt nie | Prefetch, eingehängte Laufwerke (MountedDevices), Shellbags — nur Hinweis, mit BitLocker auf C: verschlüsselt |

### Wertung je Gefahren-Stufe

„x von y erfüllt“ zählt nur Punkte, die bei der Stufe zählen; „nicht zutreffend“ zählt nie, „unbekannt“ zählt als nicht
erfüllt. Farben: erfüllt grün; zählt und offen → BitLocker rot („wichtigster Punkt“), sonst gelb („empfohlen …“); zählt
nicht → grau („Zusatz“); unbekannt grau mit Fragezeichen.

| Punkt | Ausgeschalteter PC | Mitbenutzer (Standard) | Fachmännische Untersuchung |
|---|---|---|---|
| BitLocker C: | rot (zählt) | rot (zählt) | rot (zählt) |
| Ruhezustand, Schnellstart, Auslagerungsdatei | grau | gelb (zählt) | gelb (zählt) |
| Zuletzt-Liste, Aktivitätsverlauf, Miniaturbilder | grau | gelb (zählt) | gelb (zählt) |
| Windows-Suche, VeraCrypt-Verlauf | grau | gelb (zählt) | gelb (zählt) |
| Lumora nach dem Zusperren beenden | grau | grau | gelb (zählt) |
| **Zählende Punkte** | 1 | 9 | 10 |

Begründung: Gegen einen ausgeschalteten PC genügt die Verschlüsselung von C: — alles andere liegt dann verschlüsselt auf
der Platte (Text im Mockup). Mitbenutzer sehen Listen, Miniaturbilder, Suchergebnisse und Speicherabbilder; nach dem
Zusperren zeigt Lumora ohnehin nur die normale Bibliothek, deshalb ist die Lumora-Option dort Zusatz. Bei einer
fachmännischen Untersuchung zählt auch der Arbeitsspeicher; der Hinweis nennt zusätzlich das versteckte Betriebssystem.

## Begründung

- **Nur lesen:** Jede schreibende Variante (Registry setzen, powercfg, Richtlinien) bräuchte Administratorrechte und
  sähe für einen Virenschutz genau wie das Muster aus, das am 23.10. Kaspersky auslöste. Die Windows-Oberfläche zu öffnen
  ist eindeutig: der Benutzer ändert selbst, Windows fragt selbst nach Rechten (z. B. secpol.msc).
- **Shell-Eigenschaft direkt statt PowerShell:** dieselbe Quelle, ohne Skript-Interpreter und ohne Administratorrechte
  (`manage-bde` und WMI `Win32_EncryptableVolume` bräuchten sie).
- **Leser hinter Schnittstellen:** alle Prüfungen laufen in Tests und im Rauchtest ohne echtes Windows (Fake-Leser),
  echte Werte nur mit `LUMORA_SECURITYCHECK_REAL=1` (Messung, nur lesend).
- **Einstellungen im versteckten Katalog:** gehören zur versteckten Bibliothek, liegen im Container und brauchen keine
  Migration; die Kenndatei (`hidden-library.json`) bleibt bei Kennwort/Zusperren/Aushängen.
- **Beenden über `App.OnExit`:** derselbe, schon geprüfte Weg wie das normale Beenden einer versteckten Sitzung —
  kein zweiter Aufräum-Pfad, keine normale Oberfläche dazwischen.

## Konsequenzen

### Positiv
- Herbert sieht in der versteckten Sitzung, wo Windows Spuren hinterlässt, und kommt mit einem Klick zur Einstellung.
- Kein PowerShell, keine Administratorrechte, keine Schreibzugriffe — durch Quelltext-Wächter abgesichert
  (`SecurityCheckTests.Source_never_writes_the_registry_or_starts_scripts`).
- Option „ganz beenden“ schließt auch den Arbeitsspeicher-Rest der Sitzung.

### Negativ
- Der Aktivitätsverlauf ist ohne Richtlinie „Unbekannt“ (Schalter der Einstellungen nicht dokumentiert).
- Eine VeraCrypt-Systemverschlüsselung erkennt Lumora nicht (BitLocker-Eigenschaft meldet „aus“; die Zeile sagt das).
- Werte, die erst nach Neustart/Abmelden wirken (Schnellstart, Auslagerungsdatei), zeigt der Check sofort als erfüllt —
  er liest die Einstellung, nicht den laufenden Zustand.
- Die Suchindex-Regeln liest Lumora aus dem dokumentierten Speicherort des Crawl-Scope-Managers in der Registry, nicht
  über dessen COM-Schnittstelle; ändert Microsoft den Speicher, wird der Punkt „Unbekannt“.

### Neutral
- Echter Durchlauf mit Herbert steht aus (CHANGELOG 0.4.28 „Offen“); auf Herberts PC lieferten alle Leser ohne
  Administratorrechte Werte (Messung mit `LUMORA_SECURITYCHECK_REAL=1`).
- Neue Schnittstellen-Methode `ICatalogHostSwitcher.ShutdownAfterLock()` mit Standard-Implementierung (bestehende
  Wechsler bleiben unverändert).
- `BitLockerPlatform` (Container-Assistent, ADR-0061) liest denselben Wert weiter über eine nicht erhöhte PowerShell;
  bewusst nicht angefasst (anderer Auftrag). Er könnte künftig `ShellVolumePropertyReader` nutzen — dann gäbe es im
  Container-Ordner gar kein PowerShell mehr (Hinweis in CHANGELOG 0.4.26 „Offen“).

## Alternativen

- **Einstellungen selbst setzen** (Registry, powercfg, Richtlinien) → verworfen: Herberts harte Regel; Administratorrechte,
  Virenschutz-Muster.
- **PowerShell wie `BitLockerPlatform`** → verworfen: kein Skript-Interpreter mehr für neue Funktionen (ADR-0061 Nachtrag).
- **COM `ISearchCrawlScopeManager`** für den Suchindex → verworfen: lange, fehleranfällige vtable-Deklaration ohne
  Mehrwert gegenüber dem lesbaren Speicher; der Dienst-Zustand allein genügt nicht (Standard „läuft“).
- **Einstellungen in der Kenndatei** (`hidden-library.json`) → verworfen: der Auftrag nennt die Settings-Tabelle; die
  Kenndatei bleibt klein (Kennwort-Prüfwert, Zusperren, Aushängen).
- **Freie Befehlszeile für „Einstellung öffnen“** → verworfen: nur feste Ziele, damit nichts eingeschleust werden kann.

## Referenzen

- Code: `src/Lumora.Services/HiddenLibraries/SecurityCheck/*`, `src/Lumora.Wpf/Shell/HiddenLibraries/SecurityCheck/*`,
  `src/Lumora.Wpf/Settings/Sections/SettingsSecurityCheckSection.xaml`, `CatalogSessionManager.LockAsync`,
  `StatusBarViewModel.SecurityCheck.cs`, Rauchtest `SmokeScenario.SecurityCheck.cs` + `SmokeSecurityCheckFakes.cs`
- Tests: `tests/Lumora.Tests/HiddenLibraries/SecurityCheckTests.cs`, `SecurityCheckRealWindowsTests.cs` (nur auf Wunsch),
  `tests/Lumora.Wpf.Tests/HiddenLibraries/SecurityCheckUiTests.cs`
- Mockup: `mockups/14-sicherheits-check.html`
- Microsoft Learn: „Shutdown: Clear virtual memory pagefile“ (Sicherheitsrichtlinie ↔ `ClearPageFileAtShutdown`),
  `powercfg /hibernate`, Gruppenrichtlinien „Allow publishing of User Activities“ / „Enables Activity Feed“,
  „Turn off the display of thumbnails and only display icons“, „Turn off caching of thumbnail pictures“,
  „Do not keep history of recently opened documents“, „Require use of fast startup“; Windows Search „Managing Scope
  Rules“ (Crawl Scope Manager); Property System `PSGetPropertyKeyFromName`, `SHGetPropertyStoreFromParsingName`
- VeraCrypt: Dokumentation „VeraCrypt System Files & Application Data“, Quelltext `src/Mount/Mount.c` (`SaveVolumeHistory`)
