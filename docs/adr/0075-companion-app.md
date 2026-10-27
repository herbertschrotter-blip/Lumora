# ADR-0075: Companion-App — native Android-App, unterwegs über WireGuard, gegenseitiges TLS

- **Status:** Accepted (2026-10-27, offene Fragen 2–5 von Herbert beantwortet; Entwurf vom selben Tag, ersetzt den
  früheren PWA-Entwurf gleicher Nummer, der nie im Repo lag; ergänzt um „Platz schaffen am Handy“ und „Server-Modus als
  Windows-Dienst“, 0.5.22)
- **Datum:** 2026-10-27
- **Entscheider:** Herbert Schrotter (Vorgaben: richtige App statt Web-App, maximale Sicherheit, nur über WireGuard,
  keine Webfreigabe; WireGuard-Pflicht im Heim-WLAN von Herbert am 2026-10-27 aufgehoben), Claude (Konzept)
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
Herberts eigener, gekoppelter Geräte; die versteckte Bibliothek erscheint nie in der App. Bei der Klärung der offenen
Fragen hat Herbert die WireGuard-Vorgabe für zu Hause gelockert: Im eigenen Heim-WLAN darf die App auch ohne WireGuard
direkt zugreifen (Entscheidung 2).

Vorhandene Bausteine in Lumora: App-Verbindung mit Kestrel im eigenen Projekt (ADR-0073), Hintergrundbetrieb ohne Fenster
mit Symbol im Infobereich (0.5.20), Hintergrund-Planer mit Vorrang für Vordergrund-Arbeit (ADR-0074), Vorschaubild-Cache je
FileId (400 px kurze Kante), SHA-256 für jede Datei (0.5.18), Personen, Alben, Storys, gemeinsamer Filter.

## Entscheidung

1. **Native Android-App „Lumora“** in **Flutter** (eigenes Repo, z. B. `lumora-companion`), mit einem kleinen
   Kotlin-Teil für Schlüssel und TLS (siehe „Technikwahl“). iOS später aus demselben Code.
2. **Netz:** unterwegs nur über WireGuard, das **am Router endet**; **zu Hause im Heim-WLAN auch direkt** ohne
   WireGuard (Herbert 2026-10-27). Lumora lauscht auf seiner Heimnetz-Adresse, lässt **nur die festen Adressen der
   gekoppelten Geräte** zu (je Gerät die WireGuard-Adresse und die feste WLAN-Adresse) und verlangt auf jedem Weg
   **gegenseitiges TLS** (mTLS) mit einer eigenen Lumora-Zertifizierungsstelle. Keine Portfreigabe für Lumora, keine
   öffentliche Domain, kein Cloud-Tunnel.
3. **Am PC** ein neues WPF-freies Projekt `Lumora.Companion` (eigener Kestrel, `AddCompanion()`), **getrennt** von der
   App-Verbindung, plus Einstellungsseite „Mobilgeräte“. Server **standardmäßig aus**.
4. **Funktionsumfang:** ansehen, suchen, bewerten, Storys ansehen, Fotos an Lumora teilen. Am PC **kein** Löschen,
   Verschieben, Umbenennen; **nie** die versteckte Bibliothek.
5. **Platz schaffen am Handy** (Herbert 2026-10-21, bestätigt 2026-10-27): Die App löscht Originale **auf dem Handy**
   erst, wenn Lumora die Datei per Prüfsumme bestätigt hat (wahlweise erst, wenn sie einsortiert ist), und immer über
   Androids eigene Lösch-Bestätigung. Am PC wird dabei nie etwas gelöscht.
6. **Server-Modus als Windows-Dienst** (Herbert 2026-10-21, bestätigt 2026-10-27): Lumora läuft später als Dienst
   „Lumora Server“ ohne Fenster mit Windows; der Dienst ist der einzige Schreiber, die PC-Oberfläche bleibt nutzbar und
   speichert über den Dienst. Eigene Stufe nach der App; bis dahin trägt der Hintergrundbetrieb aus 0.5.20. Lumora
   richtet den Dienst **nicht selbst** ein — Herbert führt ein mitgeliefertes Skript als Administrator aus.
7. **Hochladen früh** (Herbert 2026-10-27): Hochladen/Teilen ist die erste Funktion nach der Kopplung (Stufe 3), noch
   vor dem Ansehen.
8. **Fingerabdruck je Bibliothek** (Herbert 2026-10-27): Je freigegebener Bibliothek wird am PC eingestellt, ob die App
   den Fingerabdruck verlangt (Standard: an). Lumora erzwingt das selbst über ein zweites, biometrisch gesperrtes
   Gerätezertifikat (siehe Sicherheit, Schicht 5).
9. **Laptop später prüfen** (Herbert 2026-10-27): kein Laptop-Zugriff in den Stufen 0–8; als Idee ohne Zusage vermerkt.

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
| **Platz schaffen** | Originale **am Handy** löschen, die Lumora per Prüfsumme bestätigt hat (siehe unten) | — |
| **Nie** | am PC löschen, verschieben, umbenennen, Katalog-Wartung; versteckte Bibliothek | — |

Die App zeigt, was Lumora am PC gerade weiß; ohne Verbindung nur den verschlüsselten Zwischenspeicher (siehe Sicherheit),
Bewertungen offline werden nicht gesammelt (Stufe 1–5: nur online, keine Konflikte).

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

## Netz: WireGuard auf der FritzBox, zu Hause auch direkt

```
unterwegs:                                            zu Hause:
Handy (WireGuard-App, getrennter Tunnel               Handy im Heim-WLAN, WireGuard darf aus sein
       nur für „Lumora“)                                 │  WLAN, feste Adresse z. B. 192.0.2.57
   │  WireGuard (UDP) über Mobilnetz/fremdes WLAN        │  (Beispiel, in der FritzBox fest zugewiesen)
   ▼                                                     │
FritzBox (WireGuard endet hier, Gerät bekommt            │
          Heimnetz-Adresse, z. B. 192.0.2.201)           │
   │  Heimnetz                                           │
   ▼                                                     ▼
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
- **Zu Hause direkt im Heim-WLAN** (Herbert 2026-10-27): Die App verbindet sich immer mit derselben PC-Adresse; ist das
  Handy im Heim-WLAN, kommt die Verbindung auch ohne WireGuard an. Damit der Adressfilter eng bleibt, bekommt das Handy in
  der FritzBox eine **feste WLAN-Adresse** („immer die gleiche IPv4-Adresse zuweisen“). Android nutzt je WLAN eine eigene,
  aber gleichbleibende zufällige Geräte-Adresse (MAC); die feste Zuweisung hält, solange das so eingestellt bleibt —
  **in Stufe 0 prüfen**. Lumora führt je Gerät beide Adressen (WireGuard, WLAN); die zweite übernimmt Herbert auf der
  Seite „Mobilgeräte“ mit **„Adresse übernehmen“**: 5 Minuten lang nimmt Lumora einen Handshake mit **genau diesem
  Gerätezertifikat** von einer neuen Heimnetz-Adresse an und speichert die Adresse (Windows-Hinweis wie bei neuer
  Adresse). Ein ganzes Adressnetz wird nie freigegeben. mTLS, Pinning und Rechte gelten unverändert; im Heim-WLAN
  schützen WLAN-Verschlüsselung und TLS 1.3 die Verbindung. Ist der PC nicht erreichbar, zeigt die App „Unterwegs:
  WireGuard einschalten“.
- **Alternative Tailscale** (WireGuard-basiert, mit Koordinationsdienst eines Anbieters): bequemer (kein Router nötig,
  eigene Adressen), aber ein fremder Dienst kennt die Geräte. Wird nur erwähnt, nicht gebaut; die App-Seite bliebe gleich.

## Sicherheit in Schichten

| Schicht | Umsetzung |
|---|---|
| **1. Netz** | unterwegs nur WireGuard, zu Hause auch das Heim-WLAN; Adressfilter auf die Geräteadressen (je Gerät WireGuard- und feste WLAN-Adresse, nie ein ganzes Netz), Windows-Firewall-Regel nur für diese Adressen und das Profil „Privat“ |
| **2. Kopplung** | QR-Code am PC (einmaliges Kopplungs-Token 128 bit, 5 Minuten, Fingerabdruck des Lumora-Stammzertifikats, Adresse, Port). Das Handy erzeugt seine beiden Schlüssel (Schicht 3), schickt je eine Zertifikatsanfrage; **am PC erscheint „Gerät ‚Pixel 8‘ will sich koppeln“** mit einem 6-stelligen **Vergleichscode**, der auch am Handy steht (aus beiden Schlüsseln abgeleitet — schützt gegen ein abgefangenes QR-Foto). Erst nach „Zulassen“ stellt Lumora das Gerätezertifikat aus. Kopplung nur, solange „Gerät hinzufügen“ offen ist; nach 5 Fehlversuchen schließt das Fenster. |
| **3. Geräteschlüssel** | zwei Schlüssel im **Android Keystore**, hardwaregestützt (StrongBox, wenn vorhanden), **nicht exportierbar**: der **Grundschlüssel** ist nur bei entsperrtem Gerät nutzbar (`setUnlockedDeviceRequired`), der **Biometrie-Schlüssel** zusätzlich nur direkt nach Fingerabdruck oder Geräte-PIN (`setUserAuthenticationRequired`, starke Biometrie, kurze Gültigkeit). Lumora stellt für beide je ein Gerätezertifikat aus. Ohne die Schlüssel nützt eine Kopie der App-Daten nichts. |
| **4. Gegenseitiges TLS** | eigene **Lumora-Zertifizierungsstelle** am PC: Stammschlüssel im Windows-Schlüsselspeicher des Benutzers (CNG, nicht exportierbar; TPM, wenn vorhanden), stellt das Server-Zertifikat (Adresse als SAN) und die Gerätezertifikate (1 Jahr, automatische Erneuerung) aus. Die App vertraut **nur** diesem Stamm (**Pinning** auf den öffentlichen Schlüssel aus dem QR-Code), nicht den System-Zertifikaten. **Keine öffentliche Domain, kein Let's Encrypt** — die Zertifikats-Transparenz-Protokolle würden den Namen öffentlich machen. Lumora prüft je Anfrage: Zertifikat von der eigenen Stelle, Gerät bekannt und nicht gesperrt, Absender-Adresse passt zum Gerät. |
| **5. App-Sperre je Bibliothek** | Am PC je freigegebener Bibliothek **„Fingerabdruck verlangen“** (Standard an, Herbert 2026-10-27). Für eine solche Bibliothek fragt die App Fingerabdruck oder Geräte-PIN (`BiometricPrompt`, starke Biometrie) beim Öffnen und nach einer Pause (Standard 1 Minute im Hintergrund). **Lumora erzwingt das selbst:** Anfragen zu einer geschützten Bibliothek beantwortet es nur auf Verbindungen mit dem **Biometrie-Zertifikat**; diese Verbindungen schließt die App beim Wechsel in den Hintergrund, Lumora nach 5 Minuten ohne Anfrage, TLS-Wiederaufnahme ist für sie aus. Bibliotheken ohne Fingerabdruck öffnen mit dem Grundschlüssel (Gerät entsperrt genügt). Ohne eingerichteten Fingerabdruck bzw. PIN am Handy bleiben geschützte Bibliotheken zu (Hinweis in der App). Keine Vorschau in „Zuletzt verwendet“ und keine Bildschirmfotos (`FLAG_SECURE`, abschaltbar). |
| **6. Zwischenspeicher** | Vorschaubilder nur im app-eigenen Speicher, **verschlüsselt** (AES-GCM, Schlüssel im Keystore; für geschützte Bibliotheken ein eigener Schlüssel, der wie der Biometrie-Schlüssel nur nach Fingerabdruck nutzbar ist), Obergrenze (Standard 300 MB) und **Ablauf** (Standard 30 Tage); Originale nie dauerhaft; nichts in der Android-Galerie; keine Sicherung in Googles Cloud (`allowBackup=false`); keine Analyse- oder Absturz-Dienste Dritter. Beim Entkoppeln oder Sperren löscht die App alles. |
| **7. Rechte je Gerät** | Bibliotheken einzeln freigeben (neues Gerät: keine), je Bibliothek „Fingerabdruck verlangen“ (siehe Schicht 5), Rechte **ansehen** (Standard), **bewerten**, **hochladen** (mit Zielordner), **Originale laden**. Die versteckte Bibliothek kann nie gewählt werden. |
| **8. Server an/aus** | **standardmäßig aus**; einschalten dauerhaft (solange Lumora läuft) oder mit **Zeitlimit** (1 h, 4 h, heute). Sichtbar in Statusleiste und Infobereich („Mobilgeräte: an · 1 verbunden“). |
| **9. Sperren / Entfernen** | am PC **ein Klick „Sperren“** je Gerät (wirkt sofort, auch auf laufende Verbindungen); „Entfernen“ widerruft das Zertifikat. Hinweis, dass die WireGuard-Verbindung in der FritzBox ebenfalls gelöscht werden sollte. |
| **10. Protokoll und Hinweise** | Zugriffsprotokoll je Gerät (Zeit, Art, Anzahl — keine Dateinamen), 90 Tage; Windows-Hinweis bei **neuem Gerät** und bei Zugriff von **neuer Adresse**; Gerät „lange nicht gesehen“ (> 90 Tage) zum Entfernen vorschlagen. |
| **11. Fehlversuche** | abgelehnte Handshakes je Absender zählen; nach 10 in 10 Minuten diese Adresse 1 Stunde ignorieren; Kopplungs-Fehlversuche siehe Schicht 2; Anfragen je Gerät begrenzt. |
| **12. Versteckte Bibliothek** | Start mit `_hidden` startet keinen Companion-Server (wie ADR-0073). Ist in einer normalen Lumora eine versteckte Sitzung offen, antwortet der Server nur „gerade nicht verfügbar“ — **ohne Grund**, damit das Handy nichts über die versteckte Bibliothek erfährt. |

### Bedrohungsmodell

| Bedrohung | Was schützt | Was bleibt |
|---|---|---|
| **Handy verloren, gesperrt** | Bildschirmsperre, Keystore-Schlüssel nur entsperrt nutzbar, App-Sperre, verschlüsselter Zwischenspeicher | Am PC Gerät sperren und WireGuard-Verbindung in der FritzBox löschen — bis dahin ist der Schlüssel zwar nicht kopierbar, aber das Gerät existiert |
| **Handy verloren, entsperrt** | geschützte Bibliotheken: App-Sperre nach Pause (1 Min.), Fingerabdruck beim Öffnen, Lumora antwortet nur dem Biometrie-Zertifikat | Bibliotheken **ohne** „Fingerabdruck verlangen“ sieht, wer das entsperrte Handy hat (bewusst so gewählt); sofort am PC sperren |
| **Zu Hause: fremdes Gerät im Heim-WLAN** | Adressfilter auf die feste WLAN-Adresse des Handys, mTLS; „Adresse übernehmen“ nur 5 Min. und nur für ein bekanntes Gerätezertifikat | wie „Angreifer im Heimnetz“ |
| **Anderes WireGuard-Gerät gekapert** (z. B. Laptop mit WireGuard-Zugang) | Adressfilter (Adresse gehört keinem Gerät) und kein Gerätezertifikat → keine Antwort | Der Port ist im Heimnetz sichtbar; die TLS-Schicht von Kestrel ist Angriffsfläche (aktuell halten) |
| **Angreifer im Heimnetz** (Besucher-Gerät, unsicheres Smart-Home-Gerät) | mTLS — eine gefälschte Absender-Adresse reicht nicht; Firewall-Regel nur für die Adressen; Gäste im Gast-WLAN der FritzBox sehen das Heimnetz nicht | Er kann den Dienst stören (Überlastung), aber nichts lesen |
| **Abhören unterwegs** (fremdes WLAN, Mobilnetz) | WireGuard und darin TLS 1.3 mit Pinning; ein fremdes WLAN mit gleichem Adressbereich kann sich nicht als Lumora ausgeben (Pinning) | — |
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
| `GET /api/v1/files/by-checksum/{sha256}` | Bestätigung für „Platz schaffen“: gibt es die Datei in einer Bibliothek dieses Geräts, mit Zustand `eingang` (noch im Eingangs-Ordner) oder `einsortiert`, und ist sie am Speicherort lesbar (nicht offline) |
| `GET /api/v1/app/latest`, `/app/apk` | App-Update von Lumora (siehe Verteilung) |
| `DELETE /api/v1/device` | Gerät entkoppelt sich selbst |

Jeder Endpunkt prüft die Bibliotheks-Freigabe **je FileId** (keine Fotos fremder Bibliotheken über erratene IDs).
Fehlerformat angelehnt an das der App-Verbindung, aber eigene Definition. Hochladen nimmt nur Bild-/Videotypen an
(Inhalt geprüft, nicht nur die Endung), bereinigt Dateinamen und prüft freien Platz.

### Schema (Migration `AddCompanionDevices`, nur im normalen Katalog)

| Tabelle | Inhalt |
|---|---|
| `CompanionDevices` | Id, Name, Plattform, Zertifikats-Fingerabdrücke (SHA-256) von Grund- und Biometrie-Zertifikat, öffentliche Schlüssel, Rechte (Bewerten, Hochladen, Originale), Upload-Zielordner, Erstellt/Zuletzt gesehen, Gesperrt-seit, Fehlversuche |
| `CompanionDeviceAddresses` | DeviceId, Adresse, Art (`wireguard` / `wlan`), Übernommen-am (FK mit Kaskade) |
| `CompanionDeviceLibraries` | DeviceId, LibraryId, RequireBiometric (Standard 1) (FK mit Kaskade) |
| `CompanionAccessLog` | DeviceId, Zeit, Art, Anzahl, Adresse — keine Dateinamen, 90 Tage |

Öffentliche Zertifikate der Lumora-Stelle und Server-Einstellungen (Adresse, Port, an/aus, Zeitlimit) in der
Settings-Tabelle; der **private Stammschlüssel nie im Katalog**, sondern im CNG-Schlüsselspeicher. Der versteckte Katalog
bekommt die Tabellen nicht befüllt (keine Geräte dort). PLAN Kap. 4 wird beim Bau ergänzt.

### Einstellungsseite „Mobilgeräte“ (Mockup 22)

- Karte **Companion-Server**: Schalter, Zeitlimit, Adresse/Port, Fingerabdruck des Stammzertifikats, Zustand der
  Windows-Firewall-Regel (nur geprüft).
- Karte **Gekoppelte Geräte**: Name, Rechte, Bibliotheken (je Bibliothek „Fingerabdruck verlangen“), zuletzt gesehen,
  Adressen (WireGuard, WLAN); „Rechte…“, „Adresse übernehmen“, **„Sperren“**, „Entfernen“; Protokoll je Gerät.
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

## Platz schaffen am Handy (Originale nach Bestätigung löschen)

Herbert will Handy-Speicher frei machen, ohne ein Foto zu verlieren. Ablauf:

1. Fotos kommen per Senden/Sichern an Lumora; Lumora prüft die SHA-256 und meldet „gespeichert“.
2. Die App merkt sich je Handy-Foto die bestätigte Prüfsumme. Unter „Platz schaffen“ zeigt sie, was sicher am PC liegt
   („412 Fotos, 3,1 GB am PC bestätigt“), mit der Einstellung **„erst nach dem Einsortieren“** (Datei liegt nicht mehr im
   Eingangs-Ordner, sondern in der Bibliothek).
3. **Unmittelbar vor dem Löschen** prüft die App erneut: Prüfsumme der Handy-Datei neu rechnen und bei Lumora über
   `/files/by-checksum` nachfragen. Nur wenn Lumora die Datei jetzt noch hat **und lesen kann** (nicht offline, nicht
   fehlend — Offline-vs-Missing, PLAN Kap. 12), kommt sie auf die Liste. Ohne Verbindung wird nichts gelöscht.
4. Gelöscht wird über `MediaStore.createDeleteRequest` — **Android fragt selbst** („412 Fotos löschen?“). Die App löscht
   nie still und nie automatisch im Hintergrund, auch nicht beim automatischen Sichern.
5. Am PC wird nie etwas gelöscht; das Protokoll hält „n Fotos am Handy gelöscht, am PC bestätigt“ fest.

Vom Handy aus einsortieren (Eingang → Ordner der Bibliothek) bleibt vorerst beim PC; die App zeigt nur den Zustand.

## Server-Modus als Windows-Dienst

Ziel (Herbert): Lumora läuft mit Windows, auch ohne Anmeldung und ohne Fenster, damit das Handy jederzeit Zugriff hat;
die PC-Oberfläche bleibt nutzbar, gespeichert wird nur im Dienst.

- **Eigenes Programm `Lumora.Server`** (.NET Worker Service mit `UseWindowsService`, WPF-frei): Katalog, Vorschaubilder,
  Hintergrund-Planer (Scan, Ordnerüberwachung, Prüfsummen, Gesichter, Motive), Companion-Server. Domain, Data und
  Services sind schon WPF-frei; der WIC-Renderer (heute im Wpf-Projekt hinter einem Services-Interface) zieht in ein
  WPF-freies Projekt. Ob WIC und ONNX DirectML in der Dienst-Sitzung 0 laufen, **prüft ein Spike**; sonst Rückfall CPU.
- **Ein Schreiber:** Der Dienst ist der einzige Schreiber des normalen Katalogs (Single-Writer bleibt). Die Oberfläche
  liest weiter direkt aus SQLite (WAL erlaubt parallele Leser) und schickt **jede Änderung** über eine lokale Named Pipe
  an den Dienst (Zugriff nur für Herberts Benutzerkonto per ACL). Fortschritt der Dienst-Vorgänge kommt über dieselbe Pipe
  in die Aktivitäts-Anzeige. Das ist der größte Umbau: jeder Schreibweg der Oberfläche braucht einen Pipe-Weg.
- **Konto:** Empfehlung **Herberts Benutzerkonto** — gleicher Datenordner, gleiche DPAPI-Geheimnisse der App-Verbindung,
  gleicher CNG-Schlüssel der Zertifizierungsstelle, gleiche Rechte auf die Foto-Laufwerke. Nachteil: Passwort beim
  Einrichten, Recht „Als Dienst anmelden“. Alternative: virtuelles Dienstkonto mit eigenem Datenordner und eigenen
  Ordnerrechten — sauberer getrennt, aber Katalog-Umzug und Rechte je Foto-Ordner.
- **Laufwerke:** Ein Dienst sieht keine verbundenen Netzlaufwerks-Buchstaben des Benutzers; Bibliotheken auf solchen
  Laufwerken brauchen UNC-Pfade, sonst gelten sie als offline (der Offline-vs-Missing-Guard verhindert Schaden).
- **Versteckte Bibliothek nie im Dienst:** `_hidden` startet wie bisher eine eigene Lumora ohne Dienst und ohne Server.
- **Einrichten nur durch Herbert:** Lumora installiert, startet und ändert den Dienst **nicht selbst** (keine
  Windows-Einstellung, wie bei Firewall und ADR-0064). Mitgeliefert werden `build/install-server.ps1` und
  `build/uninstall-server.ps1`, als Administrator auszuführen; das Skript legt den Dienst an und erlaubt Herberts
  Benutzer Starten/Stoppen ohne Administrator. Die Seite „Mobilgeräte“ zeigt den Dienst-Zustand nur an (läuft /
  gestoppt / nicht eingerichtet).
- **Sichtbarkeit:** Ein Dienst hat kein Symbol im Infobereich; das zeigt die Oberfläche bzw. ein kleiner Begleiter beim
  Anmelden. Auch im Dienst bleibt „Mobilgeräte zulassen“ ein bewusster Schalter (Standard aus, Zeitlimit wählbar).
- **App-Verbindung (ADR-0073):** bleibt zunächst in der Oberfläche; ob sie in den Dienst wandert (eine Instanz,
  Anmeldedatei, Hintergrundstart), wird vor der Stufe am Vertrag geprüft und nötigenfalls dort vorgeschlagen.
- **Bis dahin** deckt der Hintergrundbetrieb aus 0.5.20 das meiste ab (Lumora läuft nach dem Schließen weiter, solange
  Herbert angemeldet ist).

## Stufenplan und Aufwand (grob)

| Stufe | Inhalt | Aufwand |
|---|---|---|
| 0 | Prüfung am echten Gerät: WireGuard-Adresse der FritzBox, feste WLAN-Adresse trotz zufälliger MAC, getrennter Tunnel, Keystore-mTLS-Prototyp (Kotlin) mit Grund- und Biometrie-Schlüssel | 1–2 PT |
| 1 | **Server**: `Lumora.Companion`, Zertifizierungsstelle, Schema, Seite „Mobilgeräte“ (ohne Kopplung), `/info`, Tests mit echten Zertifikaten | 4–6 PT |
| 2 | **Kopplung** beidseitig: QR, Vergleichscode, Bestätigung, zwei Gerätezertifikate, „Adresse übernehmen“, Sperren/Entfernen, App-Gerüst mit Fingerabdruck-Sperre je Bibliothek | 5–7 PT |
| 3 | **Hochladen/Teilen** (früh, Herbert 2026-10-27): Teilen-Ziel, fortsetzbare Übertragung, Prüfsumme, Eingangs-Ordner, Liste „gesendet“ in der App. Erster schreibender Weg — Tests für Prüfsumme, Größe, Dateityp, freien Platz und Abbruch sind Pflicht | 3–5 PT |
| 4 | **Ansehen**: Zeitleiste, Alben/Ordner, Lightbox, Suche, Storys, verschlüsselter Zwischenspeicher | 10–15 PT |
| 5 | **Bewerten**: Sterne, Favorit (später Tags) | 2–3 PT |
| 6 | später: **Handy-Fotos in der gemeinsamen Zeitleiste** (entschieden, Abgleich über SHA-256, Marke „nur am Handy“) / **automatisches Sichern** (WorkManager, über WireGuard oder im Heim-WLAN, „nur im WLAN“ wählbar) / **Platz schaffen** (Originale am Handy nach Bestätigung löschen) | 7–11 PT |
| 7 | später: **Server-Modus als Windows-Dienst** — Spike (WIC/DirectML in Sitzung 0, Konto, Laufwerke), `Lumora.Server`, Schreiben über Named Pipe, Einrichtungs-Skripte | 20–35 PT |
| 8 | später: **iOS** (Plattform-Teile in Swift, Apple-Entwicklerkonto nötig) | 5–10 PT |
| – | **Idee, ohne Zusage** (Herbert 2026-10-27: „später prüfen“): **Laptop** über eine Flutter-Desktop-App mit eigenem Gerätezertifikat (Schlüssel im Schlüsselspeicher des Laptops), gleicher Server, gleiche Regeln. Erst nach Stufe 8 bewerten | – |

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
  alles, was schon im Heimnetz ist. Jede Schicht hält auch, wenn eine andere fällt (außer dem PC selbst). Deshalb kostet
  der direkte Weg im Heim-WLAN wenig: Er nimmt nur die WireGuard-Schicht weg, und das nur dort, wo das Handy ohnehin im
  Heimnetz ist.
- Fingerabdruck je Bibliothek wird von Lumora erzwungen, nicht nur von der App: Ohne frischen Fingerabdruck ist der
  Biometrie-Schlüssel im Keystore unbenutzbar, und ohne ihn liefert Lumora keine geschützte Bibliothek.
- Eigener Server statt Erweiterung der App-Verbindung: anderer Zweck (Netz statt `127.0.0.1`), andere Prüfung (mTLS
  statt Geheimnis), und der Vertrag der App-Verbindung gehört nicht Lumora allein.

## Konsequenzen

### Positiv
- Fotos bleiben am PC, kein fremder Dienst, keine öffentliche Spur (Domain, Zertifikat, Portfreigabe).
- Verlorenes Handy ist mit einem Klick am PC ausgesperrt.
- Wiederverwendung: Filter, Vorschaubilder, Storys, Prüfsummen, Hintergrundbetrieb.

### Negativ
- Zweites Repo, zweite Sprache (Dart + Kotlin), eigene Release-Signatur und Update-Weg.
- Der PC muss laufen und Lumora mit eingeschaltetem Server; kein Zugriff, wenn der PC schläft. Der Dienst (Stufe 7)
  hebt die Anmelde-Pflicht auf, ist aber der größte Umbau (Schreiben über Pipe) und braucht einmal Administratorrechte.
- „Platz schaffen“ ist die einzige Funktion, die etwas löscht — am Handy, mit doppelter Prüfung und Android-Rückfrage.
  Ein Fehler in Lumoras Bestätigung wäre teuer; deshalb sind Tests mit fehlender, offline und geänderter Datei Pflicht.
- Eigene Zertifizierungsstelle heißt eigene Pflege (Erneuern, Widerrufen) und eigene Tests; zwei Zertifikate je Gerät
  verdoppeln Ausstellen, Erneuern und Widerruf.
- Bibliotheken ohne „Fingerabdruck verlangen“ sind bei entsperrtem Handy offen; der Schalter steht deshalb standardmäßig
  auf an.
- Der direkte Weg im Heim-WLAN hängt an der festen WLAN-Adresse; ändert Android die zufällige MAC, kommt das Handy bis zu
  „Adresse übernehmen“ zu Hause nur über WireGuard hinein.
- Hochladen kommt vor dem Ansehen: Der erste Weg, der am PC schreibt, ist auch der erste, der gebaut wird.
- Das portable Paket wächst nicht (Kestrel ist schon da), aber die Angriffsfläche am PC: ein lauschender Port im Heimnetz.

### Neutral
- WireGuard-Verwaltung bleibt in der FritzBox; Lumora kennt nur die Adressen.
- Laptop-Zugriff gibt es ohne Web-App nur über eine eigene Desktop-App (mit Flutter später möglich); als Idee ohne
  Zusage im Stufenplan vermerkt.

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

## Geklärte Fragen (Herbert, 2026-10-27)

1. **Handy-Fotos in der App?** Ja, **gemeinsame Zeitleiste** aus PC- und Handy-Fotos (Stufe 6). Die App liest dafür die
   Handy-Galerie (Android-Recht „Fotos und Videos“), nichts davon geht ohne Senden/Sichern an den PC.
2. **Hochladen früh oder spät?** **Ganz früh** — Stufe 3, direkt nach der Kopplung und vor dem Ansehen (Entscheidung 7).
3. **Fingerabdruck immer oder je Bibliothek?** **Je Bibliothek**, Standard an, von Lumora über das
   Biometrie-Zertifikat erzwungen (Entscheidung 8, Schicht 5).
4. **Laptop-Zugriff?** **Später prüfen** — Idee ohne Zusage nach Stufe 8 (Entscheidung 9).
5. **Zu Hause im WLAN ohne WireGuard?** **Ja, direkt im Heim-WLAN** (Empfehlung war nein) — mit fester WLAN-Adresse je
   Gerät und unverändertem mTLS (Entscheidung 2, Netz).

Am echten Gerät bleibt in Stufe 0 zu prüfen: Absender-Adresse über WireGuard, feste WLAN-Adresse trotz zufälliger MAC,
Keystore-mTLS mit beiden Schlüsseln.

## Referenzen

- Mockup `mockups/22-companion-app.html`
- ADR-0073 und Vertrag „App-Verbindung“ (Repo `herbertschrotter-blip/app-verbindung`) — bewusst nicht erweitert
- Android: Keystore/StrongBox, `BiometricPrompt`, Network Security Config; WireGuard-Android: getrennter Tunnel je App,
  Intent-Steuerung mit `CONTROL_TUNNELS`
