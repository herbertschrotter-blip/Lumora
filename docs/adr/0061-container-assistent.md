# ADR-0061: Container-Assistent für versteckte Bibliotheken — BitLocker-VHDX oder VeraCrypt, Kennwörter nur in Windows/VeraCrypt

- **Status:** Accepted (Nachtrag 0.4.26: die VHDX legt Lumora selbst über Windows-Schnittstellen an — kein PowerShell,
  kein Skript mehr; ersetzt den VHDX-Teil von Punkt 2, 4 und 5 sowie die Begründung „Skript erst im erhöhten Lauf“)
- **Datum:** 2026-10-22 (Nachtrag 2026-10-23)
- **Entscheider:** Herbert Schrotter (Wahl beim Anlegen, BitLocker und VeraCrypt, verstecktes Volume als Standard,
  Installation über winget nach Bestätigung), Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken, Spurenfreiheit,
  `IContainerTools`), [ADR-0042](0042-navigation-und-einstellungen-mit-reitern.md) (Bibliotheks-Umschalter)

## Kontext

Bis 0.4.14 setzte „Versteckte Bibliothek anlegen…“ voraus, dass der verschlüsselte Container schon eingehängt ist. Einen
Container anzulegen (VeraCrypt installieren, Assistent bedienen, bzw. VHDX anlegen, initialisieren, formatieren und mit
BitLocker verschlüsseln) war Handarbeit außerhalb von Lumora und für Herbert die größte Hürde. Lumora soll dabei führen,
ohne selbst zu verschlüsseln und ohne je ein Container-Kennwort zu sehen. Während der Entwicklung darf auf Herberts PC
nichts davon echt laufen (kein winget, keine VHDX, kein BitLocker, keine UAC-Rückfrage) — alles hängt an Schnittstellen
mit Fakes.

## Entscheidung

1. **Erst fragen, dann anlegen.** Umschalter → „Versteckte Bibliothek anlegen…“ öffnet den Container-Assistenten
   (`ContainerAssistantWindow`, `ContainerAssistantViewModel`): **Container ist schon eingehängt** (bisheriger Weg),
   **Neu mit BitLocker** oder **Neu mit VeraCrypt**. Am Ende beider neuen Wege folgt der bisherige Anlegen-Dialog (Name,
   Lumora-Kennwort, Ordner) mit dem neuen Laufwerk vorgewählt (`HiddenLibrariesViewModel.PreferDrive`).
2. **BitLocker (Windows, ohne Zusatzprogramm):** Speicherort (Vorschlag `<NTFS-Laufwerk mit meistem Platz, nicht System>\
   Daten\sicherung.vhdx`; nur NTFS/ReFS, Cloud-Ordner gesperrt), Größe (Hälfte des freien Platzes, ab 20 GB auf 10 GB
   abgerundet, höchstens 1024 GB; dynamisch erweiterbar), freier Laufwerksbuchstabe (der letzte freie), Bezeichnung
   „Daten“. Das **diskpart-Skript** (`VhdxScript.Build`: `create vdisk … type=expandable`, `attach vdisk`, `convert gpt`,
   `create partition primary`, `format fs=ntfs label=… quick`, `assign letter=…`, ASCII) steckt in einem PowerShell-Befehl
   (`VhdxScript.BuildElevatedPowerShell`, übergeben als `-EncodedCommand`), den Lumora **erhöht** startet (`Verb=runas`,
   eine Windows-Rückfrage, `IElevatedRunner`). Erst dieser erhöhte Lauf schreibt das Skript mit Zufallsnamen nach
   `%SystemRoot%\Temp`, führt `diskpart /s` aus, hängt bei einem Fehler eine schon angelegte VHDX ab und löscht sie
   (`VhdxScript.BuildCleanup`) und löscht das Skript in jedem Fall (`finally`). Danach öffnet Lumora den **Windows-eigenen BitLocker-Assistenten**
   `%windir%\System32\BitLockerWizardElev.exe X:\ T` (derselbe Aufruf wie der Explorer, Registry
   `HKCR\Drive\shell\encrypt-bde-elev\command`) mit Hinweisen: Kennwort, Wiederherstellungsschlüssel in Datei/Druck statt
   Microsoft-Konto, „Automatisch entsperren“ nicht einschalten. „Verschlüsselung prüfen“ liest den Zustand **ohne
   Administratorrechte** über die Shell-Eigenschaft `System.Volume.BitLockerProtection` (1 = an, 3 = wird verschlüsselt
   → weiter; 6 = gesperrt → Hinweis; sonst „noch nicht verschlüsselt“).
3. **VeraCrypt:** Fehlt VeraCrypt (`IVeraCryptLocator`), zeigt der Assistent eine Bestätigungsseite (Paket
   `IDRIX.VeraCrypt`, Quelle winget/Hersteller IDRIX, ca. 40 MB, Administratorrechte, Lizenz *VeraCrypt License: Apache 2.0
   und TrueCrypt License 3.0*); erst der Klick **„Zustimmen und installieren“** startet
   `winget install --id IDRIX.VeraCrypt -e --accept-package-agreements --accept-source-agreements` (Ausgabe mitgelesen,
   letzte Zeilen sichtbar, bekannte winget-Rückgabewerte verständlich übersetzt; Erfolg zählt nur, wenn VeraCrypt danach
   gefunden wird). Ohne winget: Hinweis mit Link zur offiziellen Download-Seite und „Erneut prüfen“. Dann startet Lumora
   **`VeraCrypt Format.exe`** (neben `VeraCrypt.exe`) und zeigt daneben eine **Schritt-Anleitung** (`VeraCryptGuide`) —
   Standard **verstecktes Volume** (Containerdatei → Verstecktes Volume → Normaler Modus → Speicherort → äußeres Volume →
   harmlose Fotos → inneres Volume mit anderem Kennwort), wählbar „normaler Container“; Warnungen: Schreiben in den
   äußeren Bereich zerstört ggf. den inneren, äußeren Bereich nur mit „Verstecktes Volume schützen“ einhängen, Bibliothek
   in den INNEREN Bereich. Danach **einhängen** über das vorhandene `IContainerTools.MountVeraCryptAsync` (VeraCrypt fragt
   das Kennwort selbst); eine Containerdatei in einem Cloud-Ordner hängt Lumora nicht ein. Ist VeraCrypt inzwischen von
   Hand installiert, geht „Erneut prüfen“ ohne winget weiter.
4. **Abbrechen** geht auf jeder Seite (nicht, solange ein Schritt läuft) und tut selbst nichts. Wurde die VHDX schon
   angelegt, aber noch nicht verschlüsselt, bzw. ist der neue Container schon eingehängt, sagt Lumora nach dem Abbrechen,
   was zu tun ist. UAC abgelehnt → nichts angelegt (ein von Lumora angelegter leerer Ordner wird wieder entfernt).
   diskpart scheitert halb → der erhöhte Lauf hängt die neue VHDX ab und löscht sie; bleibt sie trotzdem, Meldung mit
   „Auswerfen und Datei löschen“. Zeitüberschreitung → ehrliche Meldung, nichts weggeräumt.
5. **Spuren (ADR-0049):** Der Assistent und `ContainerCreationService` **protokollieren nichts** — schon „Container
   angelegt“ wäre im normalen Protokoll eine Spur. Keine Pfade in Einstellungen; Dateidialoge mit `AddToRecent = false`;
   das Skript existiert nur im erhöhten Lauf unter `%SystemRoot%\Temp` (nicht unter `%LocalAppData%\Lumora`) und wird
   sofort gelöscht; Kennwörter gibt es weder im Skript noch in einer
   Befehlszeile noch in Lumora.
6. **Schnittstellen** (Services, `Lumora.Services.HiddenLibraries.Containers`): `IContainerCreation`
   (`ContainerCreationService`), `IElevatedRunner`, `IProcessOutputRunner`, `IShellLauncher`, `IBitLockerPlatform`,
   `IVeraCryptInstaller`, `IDriveSpaceSource`; Oberfläche: `IContainerAssistant`, `IContainerFilePicker`. Tests ersetzen
   alle Windows-Seiten; im Rauchtest wird der Assistent nicht geöffnet (der Schritt „Versteckte Bibliothek“ legt wie bisher
   direkt an).

## Begründung

- **diskpart statt PowerShell `New-VHD`:** `New-VHD` gehört zum Hyper-V-Modul und fehlt auf vielen PCs; diskpart ist
  überall da und kann in einem Lauf anlegen, anhängen, partitionieren, formatieren und den Buchstaben setzen. Eine
  einzige erhöhte Rückfrage reicht.
- **Skript erst im erhöhten Lauf, unter `%SystemRoot%\Temp`** (Abweichung vom ursprünglichen Plan „Skript im `TempRoot`
  der Sitzung“, Selbst-Review): Eine Datei im Benutzerordner könnte jeder Prozess des Benutzers zwischen Schreiben und
  UAC-Zustimmung austauschen (z. B. `clean` auf Datenträger 0) — der Benutzer würde dann fremde Befehle mit
  Administratorrechten bestätigen. Dateien, die ein Administrator-Prozess in `%SystemRoot%\Temp` anlegt, können normale
  Prozesse weder lesen noch ändern; außerdem bleibt so nicht einmal kurz eine Spur unter `%LocalAppData%\Lumora`.
- **Pfad und Bezeichnung nur ASCII:** diskpart liest Skriptdateien nicht verlässlich mit Umlauten; der neutrale Vorschlag
  ist ASCII, eine klare Meldung erklärt die Grenze.
- **Windows-BitLocker-Assistent statt `manage-bde -on`/`Enable-BitLocker`:** Das Kennwort bliebe sonst nicht in einem
  Windows-Fenster (Befehlszeile bzw. Lumora-Eingabe). Der Assistent führt außerdem sauber durch den Wiederherstellungsschlüssel.
- **Zustand über die Shell-Eigenschaft:** `manage-bde -status` und WMI `Win32_EncryptableVolume` verlangen
  Administratorrechte — das wäre eine zweite Rückfrage nur zum Nachsehen.
- **VeraCrypt nur über den Assistenten:** Die VeraCrypt-Befehlszeile kann unter Windows keine versteckten Volumes anlegen;
  ein Kennwort als Argument (`/password`) stünde in der Prozessliste.
- **winget statt eigenem Download:** offizielles Paket mit Prüfsumme aus dem Manifest, kein eigener Download-/Prüfcode in
  Lumora, Updates über winget; Zustimmung zu Lizenz und Quelle gibt der Benutzer ausdrücklich per Klick.

## Konsequenzen

### Positiv
- Herbert kann eine versteckte Bibliothek ohne Vorwissen vom leeren PC bis zum ersten Scan anlegen.
- Kein Kennwort berührt Lumora; die Windows-Seite ist vollständig hinter Schnittstellen und mit Fakes getestet.

### Negativ
- Die echten Abläufe (diskpart, BitLocker-Assistent, winget-Rückgabewerte, `VeraCrypt Format.exe`, Shell-Eigenschaft)
  sind nur mit Fakes geprüft; der erste echte Durchlauf geschieht gemeinsam mit Herbert (Checkliste im CHANGELOG 0.4.18).
- Die Anleitung beschreibt die VeraCrypt-Oberfläche in Worten; ändert VeraCrypt Bezeichnungen, muss sie nachgezogen werden.
- Kurz liegt das diskpart-Skript (VHDX-Pfad, Bezeichnung, Buchstabe — kein Kennwort) unter `%SystemRoot%\Temp`; ein
  Absturz des erhöhten Laufs genau dazwischen ließe es dort liegen (nur für Administratoren lesbar).
- Läuft diskpart länger als 5 Minuten, meldet Lumora das ehrlich („läuft eventuell noch“) und räumt nichts weg — einen
  erhöhten Prozess kann Lumora nicht beenden.

### Neutral
- `HiddenLibrariesViewModel` nimmt den Assistenten optional entgegen (`IContainerAssistant? assistant = null`); ohne ihn
  (Tests) geht es wie bisher direkt zum Anlegen-Dialog.
- Explorer-Doppelklick auf eine BitLocker-VHDX hängt sie später wieder ein (Windows fragt das BitLocker-Kennwort);
  Aushängen bis 0.4.30 über `Dismount-DiskImage`, seit 0.4.31 Auswerfen wie im Explorer (ADR-0049, Nachtrag 0.4.31).

## Alternativen

- **Eigene Verschlüsselung in Lumora** (verschlüsselter Katalog/Ordner) → verworfen (Herberts Vorgabe, ADR-0049): der
  Container schützt alles inklusive Originale; eigene Krypto wäre zusätzlicher, schwer zu prüfender Code.
- **VeraCrypt-Befehlszeile mit Kennwort** (`VeraCrypt Format.exe /create … /password …`) → verworfen: Kennwort in der
  Prozessliste und in Lumora, versteckte Volumes ohnehin nicht möglich.
- **VeraCrypt-Installer selbst von veracrypt.fr laden** (Download, Signatur prüfen, starten) → verworfen zugunsten von
  winget: mehr eigener Code für Download/Prüfung/Updates; bleibt als Weg ohne winget (Link zur offiziellen Seite).
- **`manage-bde`/`Enable-BitLocker` mit Kennwort aus Lumora** → verworfen: Lumora sähe das Kennwort.
- **PowerShell `New-VHD`** → verworfen: braucht das Hyper-V-Modul.

## Referenzen

- Code: `src/Lumora.Services/HiddenLibraries/Containers/*`, `src/Lumora.Wpf/Shell/HiddenLibraries/ContainerAssistant*`,
  `HiddenLibrariesViewModel.CreateAsync`, `HiddenLibrariesServiceCollectionExtensions.AddContainerAssistant`
- Tests: `tests/Lumora.Tests/HiddenLibraries/ContainerCreationTests.cs`,
  `tests/Lumora.Wpf.Tests/HiddenLibraries/ContainerAssistantTests.cs`
- Microsoft-Dokumentation diskpart (`create vdisk`, `attach vdisk`, `convert gpt`, `format`, `assign`); winget
  „install“ und Rückgabewerte; VeraCrypt-Dokumentation „Hidden Volume“ und „Command Line Usage“

## Nachtrag 0.4.26 — VHDX über Windows-Schnittstellen statt PowerShell/diskpart

### Anlass

Beim ersten echten Durchlauf (Herbert, 23.10.2026 03:07) startete „Container erstellen“ per `runas` ein
`powershell.exe -EncodedCommand …`, das ein diskpart-Skript nach `%SystemRoot%\Temp` schrieb und `diskpart /s` aufrief.
Kaspersky (Verhaltensanalyse) sperrte daraufhin die UAC-Erhöhung („PDM:Exploit.Win32.Generic“), beendete Lumora als
„PDM:Trojan.Win32.Generic“ und stellte Lumora.exe unter Quarantäne. Das Muster „unbekanntes Programm +
Administratorrechte + PowerShell mit Base64-Befehl + diskpart“ ist typisch für Erpresser-Software. Herberts Entscheidung:
**Windows-Schnittstellen** — Lumora legt die VHDX selbst über offizielle Windows-Funktionen an, in einem eigenen
Administrator-Schritt mit lesbarem Auftrag; kein PowerShell, kein diskpart, nichts kodiert, keine Skriptdateien.

### Entscheidung

1. **Der erhöhte Schritt ist Lumora selbst** mit einem klaren Verb:
   `Lumora.exe --container-create-vhdx --path "D:\Daten\sicherung.vhdx" --size-mb 51200 --letter X --label "Daten"`
   per `runas` (`IElevatedRunner`, Ziel `Environment.ProcessPath`; ist das keine eigene `.exe` — z. B. `dotnet.exe` oder
   ein Testläufer —, lehnt der Dienst ab; eine umbenannte Lumora.exe funktioniert). Windows zeigt Programm und Auftrag lesbar in der Rückfrage. Keine Datei als Übergabe — zwischen
   Rückfrage und Lauf kann nichts ausgetauscht werden. Befehlszeile: `VhdxElevatedCommand.BuildArguments`/`TryParse`.
2. **Eigener Einsprung** `src/Lumora.Wpf/Program.cs` (`<StartupObject>Lumora.Program</StartupObject>`): erkennt das
   Verb vor allem anderen — vor Datenordner-Umzug, Logger, Katalog, Hintergrunddiensten, Oberfläche und `_hidden` — und
   führt nur `VhdxElevatedJob` aus. Kein Fenster, kein Protokoll, Ergebnis nur als **Exit-Code**. Ohne das Verb startet
   die App wie bisher (`new App(); InitializeComponent(); Run()`; der Exit-Code der App bleibt erhalten).
3. **Strenge Prüfung im erhöhten Lauf** (`VhdxElevatedJob.Validate`; die Befehlszeile könnte jeder Prozess des Benutzers
   zusammensetzen): genau Verb + vier Optionen, je einmal; Größe nur Ziffern im Bereich 1 GB–64 TB; Buchstabe D–Z und
   frei; Bezeichnung wie in der Oberfläche (A–Z, Ziffern, Leerzeichen, `- _ .`, ohne Rand-Leerzeichen); Pfad nur
   `X:\Ordner\Datei.vhdx` — voller lokaler Pfad, nur Rückstriche, keine leeren/`.`/`..`-Teile, keine Teile mit Punkt oder
   Leerzeichen am Rand, kein `:` nach dem Laufwerk (Datenströme), kein UNC-/Geräte-Pfad (`\\server`, `\\?\`, `\\.\`),
   `GetFullPath` darf nichts umschreiben; Datei existiert noch nicht; Ordner existiert (legt die Oberfläche an); **keine
   Verknüpfung (Junction/Symlink) im Pfad**; kein Cloud-Ordner (Ordnernamen + OneDrive-Umgebung); Laufwerk lokal, fest,
   NTFS/ReFS. Ohne Administratorrechte: eigener Code, nichts versucht.
4. **Windows-Funktionen** hinter `IVirtualDiskApi` (Tests: Fake), echt in `WindowsVirtualDiskApi`:
   `CreateVirtualDisk` (VHDX, Version 2, dynamisch), `OpenVirtualDisk` + `AttachVirtualDisk` mit
   `ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME` (bleibt eingehängt bis Auswerfen/Neustart — wie vorher),
   `GetVirtualDiskPhysicalPath` → Datenträgernummer (P/Invoke in `VirtualDiskNative`); dann die **Storage-Verwaltung**
   (WMI `root\Microsoft\Windows\Storage`, dieselben Klassen wie `Initialize-Disk`/`New-Partition`/`Format-Volume`):
   `MSFT_Disk` ggf. `Online`, `Initialize` (GPT), `CreatePartition` (größte Größe, **ohne** Buchstaben),
   `MSFT_Volume.Format` (NTFS, Bezeichnung, schnell), zuletzt `MSFT_Partition.AddAccessPath("X:\")` — der Buchstabe erst
   nach dem Formatieren, sonst fragt der Explorer „Datenträger formatieren?“. Datenträger und Volume meldet Windows mit
   kurzer Verzögerung → bis 20 s nachfragen; WMI-Aufrufe je 2 Minuten Zeitgrenze. Einhängen, Datenträgernummer und
   Abhängen laufen über **denselben** virtdisk-Handle (die eingehängte VHDX wird nicht ein zweites Mal geöffnet). Meldet `AttachVirtualDisk` „Recht
   fehlt“ (1314), schaltet Lumora `SeManageVolumePrivilege` im eigenen erhöhten Prozess ein und versucht es einmal neu.
5. **Aufräumen:** Scheitert ein Schritt, hängt der Lauf die VHDX ab und löscht sie — nur, was er selbst angelegt hat (die
   Datei gab es vorher nicht; auch ein Rest eines gescheiterten `CreateVirtualDisk` wird gelöscht, außer Windows meldet
   „Datei existiert“ 80/183). Ist inzwischen ein Ordner im Pfad eine Verknüpfung geworden, löscht er nicht (das Löschen
   könnte sonst woanders landen). Bleibt die Datei liegen, sieht das die Oberfläche und rät zu „Auswerfen und löschen“; bei
   einer abgelehnten Prüfung (7101–7108) hat der Lauf nichts angelegt — dann behauptet Lumora das auch nicht. Zeitgrenze für den ganzen Schritt wie bisher 5 Minuten; danach „läuft eventuell noch“, nichts weggeräumt.
6. **Exit-Codes** `VhdxJobExit` im Bereich **7101–7120** (je Prüfung und je Schritt), in verständliche Meldungen
   übersetzt (`ContainerCreationService.DescribeFailure`, mit „Fehler 71xx“). Startet Windows den Lauf gar nicht
   (`ElevatedRunResult.StartFailed`, z. B. Lumora.exe gesperrt/in Quarantäne): „Windows konnte Lumora nicht mit
   Administratorrechten starten …“. Endet er mit einem fremden Code oder meldet 0 ohne Laufwerk und ohne Datei, sagt Lumora:
   **„Ein Sicherheitsprogramm hat den Schritt vermutlich blockiert …“** mit den Auswegen „Neu mit VeraCrypt“ bzw.
   Container selbst anlegen und „Container ist schon eingehängt“. UAC abgelehnt (1223) wie bisher.
7. **Entfernt:** `VhdxScript` (diskpart-Skript, Aufräum-Skript, PowerShell-Befehl, Base64-Übergabe, Quoting) samt
   Tests. Die Regeln (Pfad, Bezeichnung, Größenvorschlag, `VhdxPlan`) heißen jetzt `VhdxRules`. Der **ASCII-Zwang für den
   Pfad entfällt** (er kam nur von diskpart); Anführungs- und Steuerzeichen bleiben verboten, und die Oberfläche verlangt
   dieselbe strenge Pfadform wie der erhöhte Lauf (`VhdxElevatedJob.IsStrictLocalPath`) — abgelehnt wird vor der Rückfrage. Unverändert:
   BitLocker-Assistent `BitLockerWizardElev.exe X:\ T`, die Statusprüfung über `System.Volume.BitLockerProtection` (eine
   PowerShell **ohne** Administratorrechte, Klartext-Befehl), VeraCrypt-Weg, Oberfläche (nur Texte).
8. **Paket:** `System.Management` (Microsoft, schon zentral in `Directory.Packages.props` für die Speicher-Messung, 9.0.0)
   jetzt auch in `Lumora.Services` — statt `Microsoft.Management.Infrastructure`, weil es ohne zusätzliche native
   Laufzeitteile auskommt, im Repo schon eingeführt ist und für die wenigen Methodenaufrufe genügt.

### Begründung

- **Lesbar statt verschleiert:** Was Lumora mit Administratorrechten tut, steht im Klartext in der Windows-Rückfrage;
  ein Virenschutz sieht ein Programm, das sich selbst mit einem eigenen Schalter startet und Windows-APIs aufruft — keinen
  Skript-Interpreter, keine kodierte Befehlszeile, keine Skriptdatei, kein diskpart.
- **virtdisk + Storage-WMI statt eigener IOCTLs:** offizielle, dokumentierte Schnittstellen; die Storage-Verwaltung nimmt
  Lumora Partitionstabelle, Ausrichtung und Formatieren ab (keine eigene Low-Level-Logik).
- **Prüfen im erhöhten Lauf:** Die Oberfläche prüft für die Bedienung, der erhöhte Lauf noch einmal für die Sicherheit —
  er vertraut der Befehlszeile nicht.
- **Exit-Codes statt Ausgabe:** Bei `runas` ist die Ausgabe des erhöhten Prozesses nicht lesbar; ein eigener
  Code-Bereich unterscheidet „Lumora meldet Fehler X“ von „von außen beendet“.

### Konsequenzen

- Positiv: kein PowerShell-/Skriptweg mehr beim Anlegen; Umlaute im Pfad möglich; jeder Fehler hat eine eigene Meldung.
- Negativ: `WindowsVirtualDiskApi` ist nur über Fakes und Strukturgrößen-Tests geprüft — der **echte Lauf mit Herbert
  (Kaspersky aktiv)** steht aus (CHANGELOG 0.4.26 „Offen“). Ein unsigniertes Programm, das sich selbst erhöht, kann einem
  Virenschutz trotzdem auffallen; dafür gibt es die Meldung mit den Auswegen. Nur 64-Bit-Prozesse (Strukturen nach
  `virtdisk.h` für x64).
- Neutral: `IElevatedRunner`/`ElevatedProcessRunner` bleiben; Tests prüfen zusätzlich den Quelltext (kein
  `EncodedCommand`, kein diskpart, `runas` nur in `ContainerProcesses.cs`, PowerShell im Container-Ordner nur in der
  Statusabfrage).

### Referenzen (Nachtrag)

- Code: `src/Lumora.Wpf/Program.cs`, `src/Lumora.Services/HiddenLibraries/Containers/VhdxElevatedCommand.cs`,
  `VhdxElevatedJob.cs`, `IVirtualDiskApi.cs`, `WindowsVirtualDiskApi.cs`, `VirtualDiskNative.cs`, `VhdxRules.cs`
- Tests: `tests/Lumora.Tests/HiddenLibraries/VhdxElevatedJobTests.cs`, `ContainerCreationTests.cs`,
  `tests/Lumora.Wpf.Tests/Shell/ProgramEntryTests.cs`, `HiddenLibraries/ContainerAssistantTests.cs`
- Microsoft-Dokumentation: Virtual Disk API (`CreateVirtualDisk`, `OpenVirtualDisk`, `AttachVirtualDisk`,
  `GetVirtualDiskPhysicalPath`), Windows Storage Management API (`MSFT_Disk`, `MSFT_Partition`, `MSFT_Volume`)
