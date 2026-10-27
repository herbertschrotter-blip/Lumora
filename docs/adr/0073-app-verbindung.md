# ADR-0073: App-Verbindung — Lumora setzt den Vertrag „App-Verbindung“ 1.1 um

- **Status:** Accepted (Stufe 1 in 0.5.5, Stufe 2 in 0.5.8, Stufe 3 in 0.5.9, Stufe 4 in 0.5.13, Stufe 5 in 0.5.16; weitere Stufen ergänzen diesen ADR)
- **Datum:** 2026-10-26
- **Entscheider:** Herbert Schrotter (Vertrag, Stufenplan, Antworten auf die offenen Fragen am 2026-10-26), Claude (Umsetzung
  und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken nie sichtbar),
  [ADR-0062](0062-ungenaue-orte-und-gps-spuren.md) (GPS-Spuren als Ortsquelle), Mockup `mockups/11-verbundene-apps.html`

## Kontext

Lumora und das Gipfelbuch (Herberts Touren- und Gipfelbuch) sollen auf demselben PC Daten austauschen: Touren, Spuren und
Gipfel vom Gipfelbuch zu Lumora, Fotos als Verweis von Lumora zum Gipfelbuch. Die Schnittstelle ist im eigenen Repo
**herbertschrotter-blip/app-verbindung** festgelegt (`VERTRAG.md`, `schema/app-verbindung.schema.json`, `CHANGELOG.md`).
Der Vertrag ist die einzige Wahrheit; dieser ADR schreibt ihn nicht ab, sondern hält fest, **welche Version Lumora
umsetzt** und was Lumora darüber hinaus selbst entscheidet. Das Gipfelbuch setzt 1.1 als Anbieter um.

## Entscheidung

Lumora setzt **App-Verbindung Version 1.1** um, in sieben Stufen (Plan vom 2026-10-26, von Herbert freigegeben):

| Stufe | Version | Inhalt (Abschnitte des Vertrags) |
|---|---|---|
| 1 | 0.5.5 | Anmeldedatei (4), eine Instanz je Benutzer (9), Schnittstelle mit Schutzregeln (3), Fehler (11), `GET /info` (7) |
| 2 | 0.5.8 | Kopplung als Anbieter mit Bestätigungsdialog, Halte-Zusagen (5, 7) |
| 3 | 0.5.9 | Abfrager: Apps finden, Kopplung anfragen, Geheimnis mit DPAPI, Trennen (4, 5) |
| 4 | 0.5.13 | Reiter „Verbundene Apps“ nach Mockup 11 |
| 5 | 0.5.16 | Foto-Endpunkte als Anbieter (8.1) |
| 6 | geplant | Hintergrundmodus und Start durch andere Apps (9) |
| 7 | geplant | Touren, Spuren, Gipfel nutzen (8.2) — Umfang A + B (siehe unten) |

Lumora-eigene Entscheidungen (keine Vertragsänderung):

1. **Start mit `_hidden` nimmt nicht teil** (Herbert, Frage 1a): kein Mutex, keine Anmeldedatei, keine Schnittstelle —
   der versteckte Modus hinterlässt keine Spur, und eine laufende normale Lumora bleibt davon unberührt.
2. **Testläufe abgeschirmt:** Mit umgelenkter App-Wurzel (`LUMORA_APPDATA`, Tests und Rauchtests) schreibt Lumora die
   Anmeldedatei nach `<Wurzel>\AppVerbindung\apps` und nimmt keinen Mutex — wie `--data` beim Gipfelbuch.
3. **Kestrel** (ASP.NET Core) im eigenen Projekt `Lumora.AppConnect` (WPF-frei), weil `HttpListener` für `127.0.0.1`
   Administratorrechte (URL-ACL) bräuchte. Das portable Paket wird dadurch größer.
4. **Erst lauschen, dann anmelden:** Die Schnittstelle startet nach dem Hauptfenster; die Anmeldedatei mit `port` wird
   erst geschrieben, wenn sie Anfragen annimmt. Ein Fehler der App-Verbindung hält Lumora nicht auf.
5. **Zweiter Start** (normal oder portabel, gleicher Benutzer) holt das Fenster der laufenden Lumora nach vorn
   (`AllowSetForegroundWindow` vor der Pipe-Nachricht) und beendet sich.
6. **Antworten auf die offenen Fragen** (Herbert, 2026-10-26): neue Kopplungen sehen zunächst **keine** Bibliothek (2);
   fehlende SHA-256-Prüfsummen rechnet Lumora im Leerlauf nach (3); Touren nutzt Lumora als Ortsquelle für „Ungenaue
   Orte“ und als Galerie-Filter „Tour“ (4: A + B); abgerufene Spuren darf Lumora in seiner Spurentabelle ablegen (5); der
   eigene Garmin-Import bleibt, bei derselben Aktivität hat das Gipfelbuch Vorrang (6).

## Begründung

- Ein eigenes Vertrags-Repo verhindert zwei Fassungen der Schnittstelle; Lumora verweist nur auf die Version.
- Stufen halten jeden Schritt testbar; ab Stufe 3 lässt sich echt gegen das laufende Gipfelbuch prüfen.
- `_hidden` außen vor zu lassen ist die einfachste Antwort auf „versteckte Bibliotheken nie sichtbar“ — es gibt dann
  nicht einmal eine laufende Schnittstelle, die etwas verraten könnte.

## Konsequenzen

### Positiv
- Gleiches Verhalten wie das Gipfelbuch (Schutzregeln, Fehler, eine Instanz); Tests prüfen mit echten HTTP-Anfragen.
- Lumora läuft je Benutzer nur noch einmal (außer mit `_hidden` und in Testläufen).

### Negativ
- Größeres portables Paket (ASP.NET-Core-Framework).
- Ein zweiter normaler Start öffnet kein zweites Fenster mehr — gewollt, aber neu.

### Neutral
- Bis Stufe 2 endet jede geschützte Anfrage mit 401 `not_paired`; `GET /info` verrät nur Name, Version, Vertragsversion
  und angebotene Rechte.

## Alternativen

- **HttpListener:** ohne Administratorrechte nur für `localhost` → verträgt sich nicht mit der Host-Regel
  `127.0.0.1:<port>` → verworfen.
- **`_hidden` übergibt sich per Pipe an die laufende Lumora:** bräuchte eine neue Pipe-Nachricht (Vertragsvorschlag) und
  hinterließe eine Spur → verworfen (Herbert, Frage 1b).

## Referenzen

- Vertrag: https://github.com/herbertschrotter-blip/app-verbindung (VERTRAG.md 1.1, Vorschläge 0001 und 0002)
- Code: `src/Lumora.AppConnect/` (AppConnectContract, AppConnectServer, RegistrationFiles, SingleInstance,
  AppConnectHost), `src/Lumora.Wpf/Program.cs`, `src/Lumora.Wpf/App.xaml.cs` (StartAppConnectAsync, StopAppConnect)
- Tests: `tests/Lumora.Tests/AppConnect/AppConnectServerTests.cs`, `AppConnectHostTests.cs`
- Issue: herbertschrotter-blip/Lumora#5

## Nachtrag 0.5.8 — Stufe 2: Kopplung als Anbieter

- **Ablauf nach Vertrag 1.1 Abschnitt 5:** `POST /pairing` prüft die Anfrage (App-Kennung `[a-z][a-z0-9-]{1,31}`, nicht
  `lumora`, Code sechsstellig, Anzeigename) und öffnet den Dialog; höchstens eine offene Anfrage (sonst 429 mit
  `Retry-After: 5`). `GET /pairing/{id}` nur für die App aus `X-AV-App` (sonst 404), nach 5 Minuten `expired` ohne
  Geheimnis, nach weiteren 5 Minuten 404. Bei „Verbinden“: 32 Zufallsbytes Base64url, nur angefragte bzw. angebotene
  Rechte. `DELETE /pairing` → 204, danach 401. Halte-Zusagen `POST/PUT/DELETE /lease` (60…900 s, ohne Rumpf bisherige
  Dauer, fremd/abgelaufen 404) — wirken erst mit dem Hintergrundmodus (Stufe 6).
- **Dialog (Mockup 11 „Neue App koppeln“):** „<App> möchte sich verbinden“, Code „482 913“, Zeilen statt einzelner
  Rechte — Fotos (suchen/Vorschau/Original, an), Personen (aus), Touren/Spuren/Gipfel (an), Hintergrundstart (aus;
  „gegenseitig“, wenn beide Seiten ihn wollen). Unbekannte Rechte zählen nicht. Nicht modal, einmal nach vorn geholt,
  schließt sich beim Ablauf. Hinweis: Bibliotheken legt Herbert erst in „Verbundene Apps“ fest (Frage 2: keine).
- **Speicher:** `<App-Wurzel>\appconnect\connections.json` (nicht im Katalog — unabhängig von Katalog-Sitzungen,
  portabel im Ordner `Daten`), Geheimnis mit DPAPI (Benutzerbereich, eigene Entropie), atomar geschrieben; nicht
  entschlüsselbar (anderer Benutzer/PC) → Kopplung gilt als getrennt. Prüfung in konstanter Zeit. Neues Paket
  `System.Security.Cryptography.ProtectedData` 9.0.0.
- **Code:** `PairingModels.cs` (Rümpfe, `PairingPermissions`), `PairingCoordinator`, `ConnectionStore`, `LeaseRegistry`;
  Wpf: `AppConnect/PairingDialogWindow`, `WindowPairingPrompt`. Tests: `PairingTests.cs`, `PairingDialogTests.cs`,
  Rauchtest-Schritt „App-Verbindung“ (Kopplung einer Test-App, Dialog hell/dunkel).

## Nachtrag 0.5.9 — Stufe 3: Lumora als Abfrager

- **`AppConnectClient`** (WPF-frei, nach dem Muster des Gipfelbuchs): andere Apps über ihre Anmeldedatei finden
  (`RegistrationFiles.Others`; laufend nur mit `running`, lebender `pid` und Port — `IsAlive`), Kopplung mit
  sechsstelligem Code anfragen (vorher Hauptversion des Vertrags prüfen → `contract_mismatch`), Status höchstens jede
  Sekunde und höchstens 5 Minuten abfragen (`accepted` | `rejected` | `expired`; 404 gilt als abgelaufen), angenommene
  Kopplung speichern (dasselbe `ConnectionStore` und Geheimnis wie als Anbieter), Rechte über `grantedToYou`
  nachführen, trennen (`DELETE /pairing`, wenn die Gegenseite läuft; lokal immer).
- **Rechte:** Lumora fragt an, was es möchte und die Gegenseite anbietet (`wants ∩ provides`), und bietet an, was die
  Gegenseite möchte (`provides ∩ wants`, ohne Angabe alles). Gespeichert wird nur Bekanntes (Abschnitt 3).
- **Getrennt erkennen:** 401 `not_paired` auf eine Anfrage mit Geheimnis löscht die Kopplung (Abschnitt 5, Ränder).
  Lumoras Lesart dazu: `GET /info` ist ohne Geheimnis erreichbar und nennt `grantedToYou` nur „mit gültigem Geheimnis“
  (Abschnitt 7) — fehlt es trotz mitgesendetem Geheimnis, gilt das ebenso als getrennt. Ein Kandidat für eine
  Klarstellung im Vertrag (Vorschlag), keine Änderung.
- **Gegenseite starten:** läuft sie nicht, startet Lumora sie über `exePath` **normal** (ohne `backgroundArg`; der
  Hintergrundstart ist erst nach der Kopplung erlaubt) und wartet höchstens 20 s auf die Anmeldedatei mit Port
  (Abschnitt 9, Bereitschaft); nur eine vorhandene `.exe`.
- **Oberfläche:** noch keine — `App.AppConnectClient` ist verdrahtet, der Reiter „Verbundene Apps“ (Stufe 4, Mockup 11)
  ruft ihn auf.
- **Tests:** `AppConnectClientTests` gegen eine zweite echte Schnittstelle im selben Testlauf (Lumora-Code als
  „Gipfelbuch“): finden, koppeln (beide Seiten dasselbe Geheimnis), abgelehnt, abgelaufen, Rechte nachführen,
  getrennt erkennen, trennen (auch ohne laufende Gegenseite), Starten mit Bereitschaft und Zeitgrenze.

## Nachtrag 0.5.13 — Stufe 4: Reiter „Verbundene Apps“

- **Reiter** Einstellungen › Verbundene Apps (Mockup 11, vor „Info“): Einleitung, Zahl der Verbindungen, je App eine Karte
  — alle Apps mit Anmeldedatei (`RegistrationFiles.Others`) und alle Kopplungen, verbundene zuerst. Zustand wie im Mockup:
  „läuft“, „läuft im Hintergrund“ (`mode`), „nicht gestartet“, „nicht verbunden“; dazu „startet…“ und „wartet auf
  Bestätigung“. Zeile darunter: Version und „verbunden seit“ bzw. „auf diesem PC nicht mehr angemeldet“.
- **Verbinden…** (nicht verbundene App): läuft sie nicht, startet Lumora sie normal (höchstens 20 s); dann sechsstelliger
  Code in der Karte („Prüfen Sie, ob in <App> derselbe Code steht … läuft um hh:mm ab“) mit **Abbrechen**. Ergebnis
  angenommen / abgelehnt / abgelaufen / abgebrochen steht in der Karte. Abbrechen beendet nur das Warten hier — der Vertrag
  kennt kein Zurückziehen; der Dialog der anderen App schließt sich beim Ablauf.
- **Rechte in beide Richtungen:** links „<App> darf von Lumora“ — was die App möchte und Lumora anbietet, **umschaltbar**
  (Abschnitt 5, Schritt 7: jede Seite ändert nur, was sie erlaubt; `ConnectionStore.SetGrantedToPeer`, nur eigene
  Rechte; die andere App erfährt es über `grantedToYou`); rechts „Lumora bekommt von <App>“ — nur Anzeige aus
  `grantedToYou`, beim Öffnen des Reiters und mit **Aktualisieren** nachgefragt.
- **Getrennt sichtbar:** trennt die andere App (401 bzw. `/info` ohne `grantedToYou`, Stufe 3) oder koppelt sie neu, folgt
  der Reiter über `ConnectionStore.Changed`; die Karte meldet „Die Verbindung mit <App> ist getrennt.“ bzw. „Mit <App>
  verbunden.“ **Verbindung trennen…** fragt nach und trennt wie in Stufe 3. **<App> starten** für nicht laufende Apps
  (normal, ohne `backgroundArg`).
- **Fehler** nur nach Code aus Abschnitt 11 in eigenen Worten (z. B. `contract_mismatch` → „andere Fassung der
  App-Verbindung“); Texte der Gegenseite und das Geheimnis zeigt der Reiter nie.
- **Verdrahtung:** `AppConnectRuntime` (Singleton) übergibt Abfrager und Kopplungen nach dem Start der Schnittstelle an
  `ConnectedAppsViewModel`; ohne App-Verbindung (Start mit `_hidden`) zeigt der Reiter nur einen Hinweis.
- **Bewusst noch nicht:** Schalter „Verbindungen erlauben“, „Hintergrundstart erlauben“, „Symbol im Infobereich“ und
  „Im Hintergrund beenden“ (Stufe 6), Bibliotheken je App (Stufe 5), Anfrage-Zähler unter „Zuletzt“ (es gibt noch keine
  Foto-Anfragen). Nichts davon braucht eine Vertragsänderung.
- **Tests:** `ConnectedAppsTests` (Wpf) gegen eine zweite echte Schnittstelle: Leer-Zustand, Verbinden angenommen mit
  Rechten, Rechte ändern, abgelehnt, abgebrochen, von drüben getrennt, Trennen mit Rückfrage, Starten (gelingt/scheitert),
  Reiter hell/dunkel ohne Binding-Fehler; `PairingTests`: eigene Rechte ändern, `/info` zeigt sie. Der Rauchtest-Schritt
  „App-Verbindung“ prüft die gekoppelte Test-App im Reiter und das Trennen.

## Nachtrag 0.5.16 — Stufe 5: Fotos als Anbieter

- **Endpunkte** nach Abschnitt 8.1 (`AppConnectServer.Photos.cs`): `GET /photos` (Zeitraum `fromUtc`/`toUtc` und/oder
  `bbox`, `mediaType`, `limit` 1…500, `cursor`), `GET /photos/{id}`, `GET /photos/by-checksum/{checksum}`,
  `GET /photos/{id}/thumbnail?size=256|512|1024`, `GET /photos/{id}/original`. Das Recht wird bei jeder Anfrage geprüft
  (403 `missing_right`). **Lesart:** `GET /photos/{id}` und `by-checksum` nennt der Vertrag ohne eigenes Recht; Lumora
  verlangt dafür `photos.search` (dieselben Daten wie die Suche) — Kandidat für eine Klarstellung, keine Änderung.
- **Bibliotheken je App:** Lumora antwortet nur aus den Bibliotheken, die der Benutzer der App im Reiter „Verbundene
  Apps“ anhakt (Lumora-eigene Einstellung `Libraries` je Kopplung in `appconnect\connections.json`, nicht Teil des
  Vertrags). Neue Kopplungen sehen keine (Herbert, Frage 2); die Karte weist darauf hin. Alles außerhalb gibt es für die
  App nicht: kein Treffer, keine Zählung, 404. Versteckte Bibliotheken liegen nie im Katalog dieser Sitzung (Start mit
  `_hidden` nimmt nicht teil).
- **Katalog** (`CatalogPhotoCatalog`, nur lesend, rohe SQLite-Verbindung): Sortierung und Zeitraum über
  `COALESCE(CapturedAt, ModifiedAtUtc)` wie die Galerie (Index `IX_Files_Library_Captured`), Fortsetzung als
  undurchsichtiger Text aus Zeit und Datei. Zeiten: mit bekanntem Abstand `taken` (mit Offset) und `takenUtc`, sonst nur
  `takenLocal` (Wanduhrzeit in der Zeitzone des PCs, ADR-0046). Ort nur mit beiden Koordinaten, `geoAccuracy`/`geoSource`
  aus ADR-0062; `width`/`height` 0, solange unbekannt. Fehlende Dateien bleiben Katalog-Einträge (`available: false`).
- **Prüfsumme:** `checksum` nur, wo Lumora den SHA-256 schon kennt (bisher aus „Doppelte“, gültig für Größe und
  Änderungszeit der Datei); sonst fehlt das Feld. **Offen:** fehlende Prüfsummen im Leerlauf nachrechnen (Herbert, Frage 3)
  — eigener Schritt mit dem Hintergrund-Planer (ADR-0074).
- **Personen:** nur mit `photos.people`, nur bestätigte Gesichter, ausgeblendete Personen nie.
- **Vorschaubilder:** aus dem Vorschaubild-Cache (fehlt es, wird es wie für die Galerie erzeugt, höchstens 10 s, sonst
  503 `busy` mit `Retry-After: 10`), mit dem WIC-Renderer auf längste Seite ≤ `size` verkleinert; 512/1024 bei Fotos aus
  dem Original (Drehung angewendet). Höchstens zwei gleichzeitig; Arbeitsdateien unter `appconnect\tmp`, sofort gelöscht.
  `ETag` aus Datei, Größe und Änderungszeit, `If-None-Match` → 304. Gesperrte Datenbank → 503 `busy`.
- **Behoben:** Der Koordinator meldete `accepted` mit Geheimnis, bevor die Kopplung gespeichert war (Lücke für 401
  `not_paired` bei einer schnellen Gegenseite); jetzt erst speichern, dann melden.
- **Protokoll** nur mit App-Kennung und Anzahlen („gipfelbuch: 12 Fotos gesucht“), nie Pfade, Namen oder Koordinaten.
- **Tests:** `PhotoEndpointsTests` gegen einen echten Testkatalog über HTTP (Felder nach Schema, Seiten, Gebiet, Art,
  400er, Rechte, Bibliotheksgrenze, Prüfsumme, Personen, Vorschaubild mit ETag/304/busy, Original); `ConnectedAppsTests`
  (Bibliotheken anhaken); Rauchtest-Schritt „App-Verbindung“ fragt mit der Test-App echte Fotos, ein echtes Vorschaubild
  und das Original ab.
