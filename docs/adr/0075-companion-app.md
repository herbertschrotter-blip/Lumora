# ADR-0075: Companion-App — native Android-App, nur über WireGuard, gegenseitiges TLS

- **Status:** Proposed (Entwurf 2026-10-27; ersetzt den früheren PWA-Entwurf gleicher Nummer, der nie im Repo lag)
- **Datum:** 2026-10-27
- **Entscheider:** Herbert Schrotter (Vorgaben: richtige App statt Web-App, maximale Sicherheit, nur über WireGuard,
  keine Webfreigabe), Claude (Konzept)
- **Bezogen auf:** [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken nie sichtbar),
  [ADR-0073](0073-app-verbindung.md) (App-Verbindung, eigener Vertrag — bleibt getrennt),
  [ADR-0074](0074-hintergrund-planer.md) (Hintergrund-Planer, `IForegroundWork`),
  [ADR-0072](0072-nutzungs-signale.md) (Nutzungs-Signale), [ADR-0064](0064-sicherheits-check.md) (Lumora ändert keine
  Windows-Einstellung), PLAN Kap. 13 (Vorschaubilder), Mockup `mockups/22-companion-app.html`

## Kontext

Herbert will seine Lumora-Bibliotheken auch am Handy ansehen und bewerten — unterwegs und zu Hause, ohne Cloud. Die Fotos
bleiben am PC; das Handy ist nur ein Fenster auf die laufende Lumora. Ein erster Entwurf sah eine Web-App (PWA) vor, die
Lumora selbst ausliefert. Herbert hat sich dagegen entschieden: **richtige App**, **maximale Sicherheit**, Zugriff **nur
über WireGuard**, das auf seinem Router (FritzBox) läuft, **keine Webfreigabe**. Fotos verlassen den PC nur Richtung
Herberts eigener, gekoppelter Geräte; die versteckte Bibliothek erscheint nie in der App.

Vorhandene Bausteine in Lumora: App-Verbindung mit Kestrel im eigenen Projekt (ADR-0073), Hintergrundbetrieb ohne Fenster
mit Symbol im Infobereich (0.5.20), Hintergrund-Planer mit Vorrang für Vordergrund-Arbeit (ADR-0074), Vorschaubild-Cache je
FileId (400 px kurze Kante), SHA-256 für jede Datei (0.5.18), Personen, Alben, Storys, gemeinsamer Filter.

## Entscheidung

1. **Native Android-App „Lumora“** in **Flutter** (eigenes Repo, z. B. `lumora-companion`), mit einem kleinen
   Kotlin-Teil für Schlüssel und TLS (siehe „Technikwahl“). iOS später aus demselben Code.
2. **Netz:** nur über WireGuard, das **am Router endet**. Lumora lauscht auf seiner Heimnetz-Adresse, lässt **nur die
   festen Adressen der gekoppelten WireGuard-Geräte** zu und verlangt **gegenseitiges TLS** (mTLS) mit einer eigenen
   Lumora-Zertifizierungsstelle. Keine Portfreigabe für Lumora, keine öffentliche Domain, kein Cloud-Tunnel.
3. **Am PC** ein neues WPF-freies Projekt `Lumora.Companion` (eigener Kestrel, `AddCompanion()`), **getrennt** von der
   App-Verbindung, plus Einstellungsseite „Mobilgeräte“. Server **standardmäßig aus**.
4. **Funktionsumfang:** ansehen, suchen, bewerten, Storys ansehen, Fotos an Lumora teilen. **Kein** Löschen, Verschieben,
   Umbenennen; **nie** die versteckte Bibliothek.

## Zielbild und Funktionsumfang

| Bereich | In der App | Bleibt am PC |
|---|---|---|
| **Zeitleiste** | alle Fotos/Videos der freigegebenen Bibliotheken nach Aufnahmedatum, Monate als Überschrift, Schnell-Scrollen über Monatsleiste, Filter-Chips (Favoriten, ab n ★, Videos); ab Stufe 6 **gemeinsam mit den Fotos des Handys** (Herbert 2026-10-27): Handy-Fotos, die Lumora noch nicht hat (Abgleich über SHA-256), mit Marke „nur am Handy“ | — |
| **Alben / Ordner** | Alben, Ordner der Bibliothek (wie Ansicht „Ordner“), Personen, Orte — nur ansehen | Alben anlegen/ändern, Ordner ändern |
| **Lightbox** | wischen, zoomen, Video abspielen, Details (Datum, Ort, Personen, Tags), „Original laden“ nur auf Wunsch | Foto verbessern, Drehen, Kontextmenü |
| **Suche** | Namen, Tags, Personen, Orte, Motive, Zeitraum — derselbe Filter wie am PC | Story-Assistent (Sprach-KI) |
| **Bewerten** | Sterne, Favorit (Recht „bewerten“); Tags setzen erst in einer späteren Stufe | Kategorien, Personen zuordnen |
| **Storys** | Storys und Erinnerungen abspielen | Storys anlegen/bearbeiten |
| **Teilen-Ziel** | Android „Teilen › Lumora“ schickt Fotos in einen Eingangs-Ordner am PC (Recht „hochladen“) | Einsortieren |
| **Nie** | Löschen, Verschieben, Umbenennen, Katalog-Wartung, versteckte Bibliothek | — |

Die App zeigt, was Lumora am PC gerade weiß; ohne Verbindung nur den verschlüsselten Zwischenspeicher (siehe Sicherheit),
Bewertungen offline werden nicht gesammelt (Stufe 1–4: nur online, keine Konflikte).

## Technikwahl der App

**Empfehlung: Flutter** (Dart), Android zuerst, iOS später aus demselben Code.

| | Flutter | Kotlin + Jetpack Compose |
|---|---|---|
| iOS später | **derselbe Code**, nur Plattform-Teile neu (Schlüssel, TLS, Teilen-Ziel) | zweite App in Swift — oder Kotlin/Compose Multiplatform (iOS-Teil noch jünger) |
| Schlüssel im Android Keystore + mTLS | `dart:io` braucht den privaten Schlüssel als Bytes — mit einem **nicht exportierbaren** Keystore-Schlüssel geht das nicht. Deshalb **eigenes kleines Kotlin-Plugin**: HTTP über OkHttp mit einem `KeyManager`, der den Keystore-Schlüssel nutzt (~ 300 Zeilen) | direkt: OkHttp + Keystore-`KeyManager`, `BiometricPrompt`, Network Security Config |
| Große Foto-Raster, flüssiges Scrollen | gut (eigene Zeichen-Engine, Skia/Impeller) | sehr gut |
| Abhängigkeiten / Angriffsfläche | Flutter-Engine + Plugins; bewusst wenige, Versionen fest | am kleinsten (nur AndroidX + OkHttp) |
| App-Größe | ~ 15–20 MB | ~ 5–8 MB |
| Hintergrund-Sichern (später) | über Plugin bzw. eigenen Kotlin-Teil (WorkManager) | direkt WorkManager |
| Desktop (Laptop) später | möglich (Flutter Windows/macOS) | nein |

Ehrlich: Mit Flutter wird der **sicherheitskritische Kern trotzdem in Kotlin** geschrieben (und für iOS in Swift). Der
Gewinn ist die gemeinsame Oberfläche (Zeitleiste, Lightbox, Suche, Storys) — dort liegt der meiste Aufwand. Wer iOS sicher
nie will, fährt mit Kotlin/Compose schlanker und mit weniger Fremdcode; dann ist es die bessere Wahl.

## Netz: WireGuard auf der FritzBox

```
Handy (WireGuard-App, getrennter Tunnel nur für „Lumora“)
   │  WireGuard (UDP, verschlüsselt) über Mobilnetz/fremdes WLAN
   ▼
FritzBox (WireGuard endet hier, Gerät bekommt eine Heimnetz-Adresse, z. B. 192.0.2.201 — Beispieladresse)
   │  Heimnetz
   ▼
PC mit Lumora (lauscht z. B. auf 192.0.2.10:47820 — Beispiel), Windows-Firewall: nur Absender-Adressen der Geräte
```

- **WireGuard endet am Router.** Die FritzBox vergibt WireGuard-Geräten Adressen **aus dem Heimnetz**; beim PC kommen die
  Verbindungen also mit dieser festen Adresse als Absender an, nicht aus einem eigenen VPN-Netz. **Am echten Gerät prüfen**
  (Stufe 0): welche Adresse sieht der PC als Absender, bleibt sie fest, und funktioniert die Verbindung auch, wenn das
  Handy zu Hause im WLAN ist und WireGuard eingeschaltet lässt.
- **Lumora lauscht auf der Heimnetz-Adresse** des PCs (wählbar, nie `0.0.0.0` blind auf allen Adaptern) und auf einem
  festen Port (Standard 47820, änderbar). Kestrel braucht dafür keine Administratorrechte.
- **Adressfilter:** Verbindungen von Adressen, die keinem gekoppelten Gerät gehören, schließt Lumora **vor** dem
  TLS-Handshake (kein Antwort-Text). Das ist nur ein Vorfilter — im Heimnetz lässt sich eine Adresse fälschen; die
  eigentliche Sperre ist mTLS.
- **Gegenseitiges TLS** auf jeder Anfrage (TLS 1.3, ECDSA P-256), siehe Sicherheit.
- **Nicht:** keine Portfreigabe für Lumora im Router, keine Webfreigabe (MyFRITZ!), kein Cloud-Tunnel (Cloudflare & Co.),
  keine öffentliche Domain. Von außen ist nur der WireGuard-Port des Routers sichtbar.
- **Getrennter Tunnel am Handy:** In der WireGuard-App unter „Anwendungen“ **nur „Lumora“ einschließen** und die
  erlaubten Adressen (`AllowedIPs`) auf die Adresse des PCs (`/32`) beschränken. Dann laufen nur die Lumora-Verbindungen
  durch den Tunnel, alle anderen Apps gehen normal ins Internet und sehen das Heimnetz nicht. Ohne WireGuard zeigt die App
  „WireGuard einschalten“ und öffnet auf Knopfdruck die WireGuard-App; optional schaltet sie den Tunnel selbst ein, wenn
  Herbert in der WireGuard-App „Fernsteuerung durch Apps“ erlaubt (Recht `CONTROL_TUNNELS`).
- **Alternative Tailscale** (WireGuard-basiert, mit Koordinationsdienst eines Anbieters): bequemer (kein Router nötig,
  eigene Adressen), aber ein fremder Dienst kennt die Geräte. Wird nur erwähnt, nicht gebaut; die App-Seite bliebe gleich.

## Sicherheit in Schichten

| Schicht | Umsetzung |
|---|---|
| **1. Netz** | nur WireGuard, Adressfilter auf die Gerätelisten-Adressen, Windows-Firewall-Regel nur für diese Adressen und das Profil „Privat“ |
| **2. Kopplung** | QR-Code am PC (einmaliges Kopplungs-Token 128 bit, 5 Minuten, Fingerabdruck des Lumora-Stammzertifikats, Adresse, Port). Das Handy erzeugt seinen Schlüssel, schickt eine Zertifikatsanfrage; **am PC erscheint „Gerät ‚Pixel 8‘ will sich koppeln“** mit einem 6-stelligen **Vergleichscode**, der auch am Handy steht (aus beiden Schlüsseln abgeleitet — schützt gegen ein abgefangenes QR-Foto). Erst nach „Zulassen“ stellt Lumora das Gerätezertifikat aus. Kopplung nur, solange „Gerät hinzufügen“ offen ist; nach 5 Fehlversuchen schließt das Fenster. |
| **3. Geräteschlüssel** | im **Android Keystore**, hardwaregestützt (StrongBox, wenn vorhanden), **nicht exportierbar**, nur bei entsperrtem Gerät nutzbar (`setUnlockedDeviceRequired`). Ohne den Schlüssel nützt eine Kopie der App-Daten nichts. |
| **4. Gegenseitiges TLS** | eigene **Lumora-Zertifizierungsstelle** am PC: Stammschlüssel im Windows-Schlüsselspeicher des Benutzers (CNG, nicht exportierbar; TPM, wenn vorhanden), stellt das Server-Zertifikat (Adresse als SAN) und die Gerätezertifikate (1 Jahr, automatische Erneuerung) aus. Die App vertraut **nur** diesem Stamm (**Pinning** auf den öffentlichen Schlüssel aus dem QR-Code), nicht den System-Zertifikaten. **Keine öffentliche Domain, kein Let's Encrypt** — die Zertifikats-Transparenz-Protokolle würden den Namen öffentlich machen. Lumora prüft je Anfrage: Zertifikat von der eigenen Stelle, Gerät bekannt und nicht gesperrt, Absender-Adresse passt zum Gerät. |
| **5. App-Sperre** | Fingerabdruck oder Geräte-PIN (`BiometricPrompt`, starke Biometrie) beim Öffnen und nach einer Pause (Standard 1 Minute im Hintergrund); keine Vorschau in „Zuletzt verwendet“ und keine Bildschirmfotos (`FLAG_SECURE`, abschaltbar). |
| **6. Zwischenspeicher** | Vorschaubilder nur im app-eigenen Speicher, **verschlüsselt** (AES-GCM, Schlüssel im Keystore), Obergrenze (Standard 300 MB) und **Ablauf** (Standard 30 Tage); Originale nie dauerhaft; nichts in der Android-Galerie; keine Sicherung in Googles Cloud (`allowBackup=false`); keine Analyse- oder Absturz-Dienste Dritter. Beim Entkoppeln oder Sperren löscht die App alles. |
| **7. Rechte je Gerät** | Bibliotheken einzeln freigeben (neues Gerät: keine), Rechte **ansehen** (Standard), **bewerten**, **hochladen** (mit Zielordner), **Originale laden**. Die versteckte Bibliothek kann nie gewählt werden. |
| **8. Server an/aus** | **standardmäßig aus**; einschalten dauerhaft (solange Lumora läuft) oder mit **Zeitlimit** (1 h, 4 h, heute). Sichtbar in Statusleiste und Infobereich („Mobilgeräte: an · 1 verbunden“). |
| **9. Sperren / Entfernen** | am PC **ein Klick „Sperren“** je Gerät (wirkt sofort, auch auf laufende Verbindungen); „Entfernen“ widerruft das Zertifikat. Hinweis, dass die WireGuard-Verbindung in der FritzBox ebenfalls gelöscht werden sollte. |
| **10. Protokoll und Hinweise** | Zugriffsprotokoll je Gerät (Zeit, Art, Anzahl — keine Dateinamen), 90 Tage; Windows-Hinweis bei **neuem Gerät** und bei Zugriff von **neuer Adresse**; Gerät „lange nicht gesehen“ (> 90 Tage) zum Entfernen vorschlagen. |
| **11. Fehlversuche** | abgelehnte Handshakes je Absender zählen; nach 10 in 10 Minuten diese Adresse 1 Stunde ignorieren; Kopplungs-Fehlversuche siehe Schicht 2; Anfragen je Gerät begrenzt. |
| **12. Versteckte Bibliothek** | Start mit `_hidden` startet keinen Companion-Server (wie ADR-0073). Ist in einer normalen Lumora eine versteckte Sitzung offen, antwortet der Server nur „gerade nicht verfügbar“ — **ohne Grund**, damit das Handy nichts über die versteckte Bibliothek erfährt. |

### Bedrohungsmodell

| Bedrohung | Was schützt | Was bleibt |
|---|---|---|
| **Handy verloren, gesperrt** | Bildschirmsperre, Keystore-Schlüssel nur entsperrt nutzbar, App-Sperre, verschlüsselter Zwischenspeicher | Am PC Gerät sperren und WireGuard-Verbindung in der FritzBox löschen — bis dahin ist der Schlüssel zwar nicht kopierbar, aber das Gerät existiert |
| **Handy verloren, entsperrt** | App-Sperre nach Pause (1 Min.), Fingerabdruck beim Öffnen | Wer das Handy in der ersten Minute mit offener App hat, sieht, was offen ist; sofort am PC sperren |
| **Anderes WireGuard-Gerät gekapert** (z. B. Laptop mit WireGuard-Zugang) | Adressfilter (Adresse gehört keinem Gerät) und kein Gerätezertifikat → keine Antwort | Der Port ist im Heimnetz sichtbar; die TLS-Schicht von Kestrel ist Angriffsfläche (aktuell halten) |
| **Angreifer im Heimnetz** (Besucher-Gerät, unsicheres Smart-Home-Gerät) | mTLS — eine gefälschte Absender-Adresse reicht nicht; Firewall-Regel nur für die Adressen; Gäste im Gast-WLAN der FritzBox sehen das Heimnetz nicht | Er kann den Dienst stören (Überlastung), aber nichts lesen |
| **Abhören unterwegs** (fremdes WLAN, Mobilnetz) | WireGuard und darin TLS 1.3 mit Pinning | — |
| **Abgefangener QR-Code beim Koppeln** | Token einmalig und 5 Min., Bestätigung am PC mit Vergleichscode | Wer Herbert dazu bringt, ein fremdes Gerät zuzulassen, ist drin — der Dialog nennt Gerätename und Adresse |
| **Gefälschte App / gefälschtes Update** | Updates nur über Lumora am PC mit SHA-256, Android prüft die gleiche Signatur | Verlust des Signaturschlüssels = keine Updates mehr (Schlüssel sichern) |
| **Handy mit Schadsoftware / gerootet** | Schlüssel nicht exportierbar | Was die App gerade zeigt, kann abgegriffen werden; nicht abwehrbar → Gerät sperren |
| **Router kompromittiert / WireGuard-Lücke** | mTLS hält wie beim Heimnetz-Angreifer | Störung möglich |
| **PC kompromittiert** | **nicht abwehrbar** — dort liegen alle Fotos und der Stammschlüssel; Lumora kann sich nicht gegen den eigenen PC schützen | ehrlich: Schutz des PCs (BitLocker, Updates, Sicherheits-Check ADR-0064) ist Voraussetzung |

## Umbau in Lumora (PC)

### Neues Projekt `Lumora.Companion`

- WPF-frei, referenziert Services und Data, nutzt ASP.NET Core (Kestrel) wie `Lumora.AppConnect` — aber **eigener
  Kestrel**, eigener Port, eigene Adresse, eigene Prüfung. **Getrennt vom App-Verbindungs-Vertrag** (ADR-0073): keine
  Companion-Endpunkte in `Lumora.AppConnect`, keine Vertragsfelder erfinden; die App-Verbindung bleibt auf `127.0.0.1`.
- Registrierung in `CompanionServiceCollectionExtensions.AddCompanion()`, Aufruf mit einer Zeile in `App.xaml.cs`.
- Bausteine:
  - `ICompanionServer` (Start/Stopp, Zeitlimit, Zustand als Messenger-Record für Statusleiste und Infobereich),
  - `ICompanionCertificateAuthority` (Stamm im CNG-Schlüsselspeicher, Server-/Gerätezertifikate, Widerruf),
  - `ICompanionDeviceStore` (Geräte, Rechte, Bibliotheken, Sperre, Protokoll),
  - `ICompanionPairing` (Token, QR-Inhalt, Vergleichscode, Bestätigungsdialog über `IDialogService`),
  - Endpunkte als dünne Schicht über die **vorhandenen** Services (Zeitleiste, Filter, Alben, Storys, Bewerten) — keine
    eigene SQL, gleiche Regeln wie am PC.
- Kestrel: `ClientCertificateMode.RequireCertificate`, eigene Zertifikatsprüfung gegen die Lumora-Stelle,
  Verbindungs-Vorfilter auf die Geräteadressen, TLS 1.3, Anfrage-Größen und -Raten begrenzt.

### API-Entwurf (Version `v1`, JSON, nur mTLS)

| Endpunkt | Zweck |
|---|---|
| `POST /pair` | Kopplung — nur auf eigenem Kanal ohne Gerätezertifikat, nur während „Gerät hinzufügen“ offen ist |
| `GET /api/v1/info` | PC-Name, Lumora- und API-Version, Rechte dieses Geräts, freigegebene Bibliotheken |
| `GET /api/v1/timeline?library=&cursor=&limit=200&…` | Zeitleiste, **Keyset-Paging** über (Aufnahmedatum, FileId), Filter wie der gemeinsame Filter |
| `GET /api/v1/timeline/months?library=` | Anzahl je Monat für die Monatsleiste |
| `GET /api/v1/albums`, `/albums/{id}/items?cursor=` | Alben |
| `GET /api/v1/folders?library=&parent=`, `/folders/{id}/items?cursor=` | Ordner (nur relative Namen, nie absolute Pfade) |
| `GET /api/v1/people`, `/places`, `/tags` | Auswahllisten für die Suche |
| `GET /api/v1/search?q=&person=&tag=&place=&from=&to=&minRating=&favorite=&cursor=` | Suche |
| `GET /api/v1/stories`, `/stories/{id}` | Storys und Erinnerungen (Abfolge der FileIds) |
| `GET /api/v1/files/{id}` | Details (Datum, Ortsname, Maße, Personen, Tags, Sterne, Favorit) |
| `GET /api/v1/files/{id}/thumb?size=s\|m` | **Vorschaubilder in Stufen:** `s` = vorhandener 400-px-Cache (Raster), `m` = lange Kante 2048 px, auf Abruf erzeugt und als `{fileId}_2048_still.jpg` im selben Cache abgelegt (Lightbox); `ETag`, `Cache-Control: private` |
| `GET /api/v1/files/{id}/original` | Original (Recht „Originale laden“), mit `Range` für Videos |
| `PUT /api/v1/files/{id}/rating`, `/favorite` | Bewerten (Recht „bewerten“) über denselben Dienst wie die Lightbox am PC |
| `POST /api/v1/uploads`, `PUT /uploads/{id}`, `POST /uploads/{id}/complete` | Hochladen: erst SHA-256 und Größe melden (schon vorhanden → überspringen), dann in Teilen (fortsetzbar), Lumora prüft die Prüfsumme, legt die Datei atomar in den Eingangs-Ordner und protokolliert sie als `OperationBatch` (Rückgängig = Papierkorb) |
| `GET /api/v1/app/latest`, `/app/apk` | App-Update von Lumora (siehe Verteilung) |
| `DELETE /api/v1/device` | Gerät entkoppelt sich selbst |

Jeder Endpunkt prüft die Bibliotheks-Freigabe **je FileId** (keine Fotos fremder Bibliotheken über erratene IDs).
Fehlerformat angelehnt an das der App-Verbindung, aber eigene Definition. Hochladen nimmt nur Bild-/Videotypen an
(Inhalt geprüft, nicht nur die Endung), bereinigt Dateinamen und prüft freien Platz.

### Schema (Migration `AddCompanionDevices`, nur im normalen Katalog)

| Tabelle | Inhalt |
|---|---|
| `CompanionDevices` | Id, Name, Plattform, Zertifikats-Fingerabdruck (SHA-256), öffentlicher Schlüssel, feste Adresse, Rechte (Bewerten, Hochladen, Originale), Upload-Zielordner, Erstellt/Zuletzt gesehen, Gesperrt-seit, Fehlversuche |
| `CompanionDeviceLibraries` | DeviceId, LibraryId (FK mit Kaskade) |
| `CompanionAccessLog` | DeviceId, Zeit, Art, Anzahl, Adresse — keine Dateinamen, 90 Tage |

Öffentliche Zertifikate der Lumora-Stelle und Server-Einstellungen (Adresse, Port, an/aus, Zeitlimit) in der
Settings-Tabelle; der **private Stammschlüssel nie im Katalog**, sondern im CNG-Schlüsselspeicher. Der versteckte Katalog
bekommt die Tabellen nicht befüllt (keine Geräte dort). PLAN Kap. 4 wird beim Bau ergänzt.

### Einstellungsseite „Mobilgeräte“ (Mockup 22)

- Karte **Companion-Server**: Schalter, Zeitlimit, Adresse/Port, Fingerabdruck des Stammzertifikats, Zustand der
  Windows-Firewall-Regel (nur geprüft).
- Karte **Gekoppelte Geräte**: Name, Rechte, Bibliotheken, zuletzt gesehen, Adresse; „Rechte…“, **„Sperren“**,
  „Entfernen“; Protokoll je Gerät.
- Karte **Gerät hinzufügen**: QR-Code mit Restzeit, Vergleichscode, Standard-Rechte für neue Geräte.
- Bestätigungsdialog bei Kopplung (über `IDialogService`).

### Zusammenspiel mit vorhandenen Teilen

- **Hintergrund-Planer (ADR-0074):** Anfragen vom Handy für Vorschaubilder melden sich als Vordergrund-Arbeit
  (`ForegroundHold`, 2 s Nachlauf wie Galerie/Lightbox); `m`-Vorschaubilder laufen über die Warteschlange mit Vorrang
  „Critical“, Raster über „Viewport“.
- **Hintergrundbetrieb (0.5.20):** neue Option „Für Mobilgeräte im Hintergrund weiterlaufen“. Ist der Server an, läuft
  Lumora nach dem Schließen des Fensters schlank weiter (Katalog, Vorschaubilder, Server — kein Scan, keine Gesichter),
  **mit Pflicht-Symbol im Infobereich**, bis Zeitlimit oder „Beenden“. Lumora richtet **keinen Autostart** ein; ist
  Lumora aus, zeigt die App „Lumora am PC läuft nicht“.
- **Nutzungs-Signale (ADR-0072):** Ansehen am Handy meldet `UsageReport` (gedämpft wie die Diashow), abschaltbar mit
  „Nutzung lernen“.
- **Aktivitäts-Anzeige (ADR-0067):** Hochladen erscheint als Vorgang mit Fortschritt.
- **Windows-Firewall:** Lumora **ändert sie nicht**. Beim ersten Lauschen kann Windows fragen — Anleitung auf der Seite:
  Regel „eingehend, TCP-Port, nur Profil Privat, Remote-Adressen = Geräteadressen“. Lumora **liest** die Regeln nur und
  zeigt „Regel fehlt / zu weit / passt“ (wie der Sicherheits-Check, ADR-0064).

## Stufenplan und Aufwand (grob)

| Stufe | Inhalt | Aufwand |
|---|---|---|
| 0 | Prüfung am echten Gerät: WireGuard-Adresse der FritzBox, getrennter Tunnel, Keystore-mTLS-Prototyp (Kotlin) | 1–2 PT |
| 1 | **Server**: `Lumora.Companion`, Zertifizierungsstelle, Schema, Seite „Mobilgeräte“ (ohne Kopplung), `/info`, Tests mit echten Zertifikaten | 4–6 PT |
| 2 | **Kopplung** beidseitig: QR, Vergleichscode, Bestätigung, Sperren/Entfernen, App-Gerüst mit Fingerabdruck-Sperre | 4–6 PT |
| 3 | **Ansehen**: Zeitleiste, Alben/Ordner, Lightbox, Suche, Storys, verschlüsselter Zwischenspeicher | 10–15 PT |
| 4 | **Bewerten**: Sterne, Favorit (später Tags) | 2–3 PT |
| 5 | **Hochladen/Teilen**: Teilen-Ziel, fortsetzbare Übertragung, Prüfsumme, Eingangs-Ordner | 3–5 PT |
| 6 | später: **Handy-Fotos in der gemeinsamen Zeitleiste** (entschieden, Abgleich über SHA-256, Marke „nur am Handy“) / **automatisches Sichern** (WorkManager, nur über WireGuard, nur im WLAN wählbar) | 5–8 PT |
| 7 | später: **iOS** (Plattform-Teile in Swift, Apple-Entwicklerkonto nötig) | 5–10 PT |

## Verteilung ohne Play Store

- **APK direkt**, signiert mit einem eigenen Release-Schlüssel (sicher aufbewahren — Verlust heißt: keine Updates mehr,
  App neu koppeln). Erste Installation per Datei oder ADB.
- **Update-Weg:** Herbert legt die neue APK in Lumora ab (bzw. ein Build-Skript); die App fragt über `/app/latest` nach,
  lädt über die mTLS-Verbindung, prüft SHA-256 und übergibt an den Android-Paket-Installer (Herbert bestätigt). Android
  akzeptiert das Update nur mit derselben Signatur.
- **Googles Entwicklerverifizierung:** Google verlangt schrittweise, dass auch Apps außerhalb des Play Stores von einem
  verifizierten Entwickler stammen (erste Länder ab September 2026, weltweit ab 2027 angekündigt). Angekündigt sind ein
  kostenloses Konto für Hobby-Entwickler mit begrenzter Geräteanzahl und ein „erweiterter Weg“ mit 24 Stunden Wartezeit
  für nicht verifizierte Apps; Installation per ADB ist laut Berichten ausgenommen. **Vor Stufe 2 den dann gültigen Stand
  prüfen** und voraussichtlich das Hobby-Konto nutzen.

## Begründung

- Eine native App kann, was eine Web-App nicht kann: Schlüssel im Keystore, Pinning ohne installiertes Stammzertifikat,
  Fingerabdruck-Sperre, verschlüsselter Speicher, Teilen-Ziel, später Sichern im Hintergrund.
- WireGuard am Router schließt das Internet aus, ohne dass Lumora selbst erreichbar wird; mTLS schützt zusätzlich gegen
  alles, was schon im Heimnetz ist. Jede Schicht hält auch, wenn eine andere fällt (außer dem PC selbst).
- Eigener Server statt Erweiterung der App-Verbindung: anderer Zweck (Netz statt `127.0.0.1`), andere Prüfung (mTLS
  statt Geheimnis), und der Vertrag der App-Verbindung gehört nicht Lumora allein.

## Konsequenzen

### Positiv
- Fotos bleiben am PC, kein fremder Dienst, keine öffentliche Spur (Domain, Zertifikat, Portfreigabe).
- Verlorenes Handy ist mit einem Klick am PC ausgesperrt.
- Wiederverwendung: Filter, Vorschaubilder, Storys, Prüfsummen, Hintergrundbetrieb.

### Negativ
- Zweites Repo, zweite Sprache (Dart + Kotlin), eigene Release-Signatur und Update-Weg.
- Der PC muss laufen und Lumora mit eingeschaltetem Server; kein Zugriff, wenn der PC schläft.
- Eigene Zertifizierungsstelle heißt eigene Pflege (Erneuern, Widerrufen) und eigene Tests.
- Das portable Paket wächst nicht (Kestrel ist schon da), aber die Angriffsfläche am PC: ein lauschender Port im Heimnetz.

### Neutral
- WireGuard-Verwaltung bleibt in der FritzBox; Lumora kennt nur die Adressen.
- Laptop-Zugriff gibt es ohne Web-App nur über eine eigene Desktop-App (mit Flutter später möglich).

## Alternativen

- **Web-App (PWA) von Lumora** (früherer Entwurf): kein Keystore, Stammzertifikat müsste am Handy installiert werden,
  keine echte App-Sperre → verworfen (Herbert).
- **Kotlin + Compose:** schlanker und sicherer im Fremdcode, aber iOS wäre eine zweite App → gute Alternative, wenn iOS
  wegfällt.
- **.NET MAUI** (C# wie Lumora): Verträge könnten geteilt werden, aber dieselbe Keystore-Hürde, schwächer bei großen
  Rastern, unsichere Zukunft → verworfen.
- **Tailscale statt FritzBox-WireGuard:** bequemer, aber Koordinationsdienst eines Anbieters → nur als Alternative genannt.
- **Öffentliche Domain + Let's Encrypt / Portfreigabe / Cloud-Tunnel:** sichtbar im Internet bzw. in den
  Zertifikats-Protokollen → ausgeschlossen.
- **Nur Adressfilter ohne mTLS:** im Heimnetz fälschbar → nicht ausreichend.

## Offene Fragen an Herbert

1. ~~Handy-Fotos in der App?~~ **Beantwortet (Herbert, 2026-10-27): ja, gemeinsame Zeitleiste** aus PC- und
   Handy-Fotos (Stufe 6). Die App liest dafür die Handy-Galerie (Android-Recht „Fotos und Videos“), nichts davon geht
   ohne Senden/Sichern an den PC.
2. **Hochladen früh oder spät?** (Stufe 5 vor oder nach Bewerten/Storys, oder gleich mit automatischem Sichern zusammen)
3. **Fingerabdruck immer** beim Öffnen — oder je Bibliothek (z. B. nur für bestimmte Bibliotheken)?
4. **Laptop-Zugriff ganz weglassen?** (ohne Web-App bräuchte er eine eigene Desktop-App)
5. **Zu Hause im WLAN ohne WireGuard?** Empfehlung: nein — WireGuard bleibt immer an, eine Regel für alle Wege.

## Referenzen

- Mockup `mockups/22-companion-app.html`
- ADR-0073 und Vertrag „App-Verbindung“ (Repo `herbertschrotter-blip/app-verbindung`) — bewusst nicht erweitert
- Android: Keystore/StrongBox, `BiometricPrompt`, Network Security Config; WireGuard-Android: getrennter Tunnel je App,
  Intent-Steuerung mit `CONTROL_TUNNELS`
