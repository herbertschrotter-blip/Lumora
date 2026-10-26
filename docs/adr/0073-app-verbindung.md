# ADR-0073: App-Verbindung — Lumora setzt den Vertrag „App-Verbindung“ 1.1 um

- **Status:** Accepted (Stufe 1 in 0.5.5; weitere Stufen ergänzen diesen ADR)
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
| 2 | geplant | Kopplung als Anbieter mit Bestätigungsdialog, Halte-Zusagen (5, 7) |
| 3 | geplant | Abfrager: Apps finden, Kopplung anfragen, Geheimnis mit DPAPI, Trennen (4, 5) |
| 4 | geplant | Reiter „Verbundene Apps“ nach Mockup 11 |
| 5 | geplant | Foto-Endpunkte als Anbieter (8.1) |
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
