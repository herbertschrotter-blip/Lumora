# Spike #18 — Companion-App: Netz-Prüfung am echten Gerät (ADR-0075, Stufe 0)

- **Status:** ⬜ offen — Herbert prüft an Handy, FritzBox und PC
- **Datum:** 2026-10-27 (v0.5.28, Prüfliste angelegt)
- **Bezogen auf:** [ADR-0075](../adr/0075-companion-app.md) (Netz, Schicht 1, Stufe 0), Mockup
  `mockups/22-companion-app.html`

## Zweck

ADR-0075 baut auf Annahmen über WireGuard in der FritzBox und über das Heim-WLAN, die nur am echten Gerät zu klären
sind. Diese Liste prüft sie **ohne etwas zu bauen**: mit FritzBox, WireGuard-App, einem Browser am Handy und einem
kurzzeitigen Test-Webserver am PC (Python, leerer Ordner). Das Ergebnis entscheidet, ob der Adressfilter je Gerät so
funktioniert wie geplant.

**Nicht Teil dieser Liste:** der Keystore-mTLS-Prototyp (Kotlin, zwei Schlüssel). Er braucht Code und kommt mit dem Bau
von Stufe 1/2.

**Öffentliches Repo:** Unten nur „ja / nein / Hinweis“ eintragen — **keine echten Adressen, Gerätenamen, Schlüssel
oder Netznamen**. Die Beispieladressen `192.0.2.x` stehen für die eigenen.

## Vorbereitung

1. **Test-Webserver am PC** (nur für die Prüfung, danach beenden):
   - Leeren Ordner anlegen, z. B. `%TEMP%\lumora-netztest` — der Server zeigt den Inhalt dieses Ordners, deshalb
     **nichts hineinlegen**.
   - Adresse des PCs im Heimnetz nachsehen (`ipconfig`, Adapter Ethernet/WLAN), hier Beispiel `192.0.2.10`.
   - Starten:
     ```
     python -m http.server 47820 --bind 192.0.2.10 --directory "%TEMP%\lumora-netztest"
     ```
   - Fragt Windows nach der Firewall: **nur „Private Netzwerke“** zulassen. Jeder Aufruf erscheint im Fenster mit der
     **Absender-Adresse** am Zeilenanfang — das ist der Messwert dieser Liste.
2. **Netzwerkprofil:** Windows-Einstellungen › Netzwerk und Internet › Ethernet bzw. WLAN › Netzwerkprofiltyp =
   **Privat**.
3. **WireGuard-Gerät in der FritzBox** (falls noch keins für das Handy besteht): Internet › Freigaben › VPN (WireGuard) ›
   „VPN-Verbindung hinzufügen“ › einzelnes Gerät verbinden; den QR-Code mit der WireGuard-App am Handy einlesen.
   Bezeichnungen können je nach FRITZ!OS-Version leicht abweichen.
4. **Handy-Browser** als Ersatz für die spätere Lumora-App; aufgerufen wird `http://192.0.2.10:47820/`.

## Prüfungen

### A — Absender-Adresse über WireGuard

Handy **im Mobilnetz** (WLAN aus), WireGuard an, Seite aufrufen.

1. Die Seite lädt (leere Liste).
2. Im PC-Fenster steht eine Adresse aus dem **Heimnetz** (nicht aus einem eigenen VPN-Netz) — dieselbe, die die
   FritzBox unter Heimnetz › Netzwerk für das WireGuard-Gerät zeigt.
3. Tunnel aus und wieder an, erneut aufrufen: **gleiche Adresse**.
4. Am nächsten Tag bzw. nach einem Neustart der FritzBox: **gleiche Adresse**.

### B — Getrennter Tunnel nur für eine App

In der WireGuard-App den Tunnel bearbeiten: Anwendungen › **nur einschließen** › den Test-Browser wählen. Unter dem
Peer „Zulässige IPs“ (`AllowedIPs`) auf **`192.0.2.10/32`** setzen (Beispiel = PC-Adresse). Handy im Mobilnetz.

1. Der eingeschlossene Browser erreicht den PC.
2. Ein **anderer** Browser bzw. eine andere App erreicht den PC **nicht**.
3. Andere Apps gehen normal ins Internet (eine beliebige Webseite lädt).
4. Die FritzBox-Oberfläche ist über den Tunnel **nicht** erreichbar (nur der PC ist freigegeben).

### C — Zu Hause direkt im Heim-WLAN

Handy im Heim-WLAN, **WireGuard aus**.

1. Am Handy prüfen: Einstellungen › Netzwerk und Internet › Internet › Heim-WLAN › Datenschutz = „Zufällige
   MAC-Adresse verwenden“ (Standard) — und in den Entwickleroptionen ist eine **nicht persistente** MAC-Zufallsvergabe
   **aus**.
2. In der FritzBox: Heimnetz › Netzwerk › Handy bearbeiten › „Diesem Netzwerkgerät immer die gleiche IPv4-Adresse
   zuweisen“ setzen.
3. Seite aufrufen: Im PC-Fenster steht genau diese **feste Adresse**.
4. WLAN aus und wieder an, Handy neu starten, am nächsten Tag erneut: **gleiche Adresse**.

### D — Zu Hause mit eingeschaltetem WireGuard

Handy im Heim-WLAN, **WireGuard an** (mit den Einstellungen aus B).

1. Der Browser erreicht den PC.
2. Welche Absender-Adresse steht im PC-Fenster — die **WireGuard**-Adresse aus A oder die **WLAN**-Adresse aus C?
   (Beides ist für Lumora in Ordnung, weil je Gerät beide Adressen geführt werden; das Ergebnis bestimmt nur, welche
   „Adresse übernehmen“ zuerst braucht.)

### E — Aufräumen

1. Test-Webserver beenden (Strg+C).
2. Die Firewall-Regel, die Windows dafür angelegt hat, wieder löschen: Windows-Sicherheit › Firewall- und
   Netzwerkschutz › Erweiterte Einstellungen › Eingehende Regeln › Einträge „Python“ bzw. „python.exe“.
3. Den leeren Ordner löschen. Die Tunnel-Einstellungen aus B dürfen bleiben (sie sind der spätere Zielzustand, dann mit
   der Lumora-App statt des Browsers).

## Ergebnis

Nur „ja / nein / Hinweis“, keine echten Adressen.

| Prüfung | Ergebnis | Hinweis |
|---|---|---|
| A2 Absender über WireGuard ist eine Heimnetz-Adresse | | |
| A3/A4 WireGuard-Adresse bleibt fest | | |
| B1–B3 Getrennter Tunnel nur für eine App | | |
| B4 Nur der PC über den Tunnel erreichbar | | |
| C3 Feste WLAN-Adresse kommt beim PC an | | |
| C4 WLAN-Adresse bleibt fest (Neustart, nächster Tag) | | |
| D1 Zu Hause mit WireGuard erreichbar | | |
| D2 Absender zu Hause mit WireGuard (WireGuard / WLAN) | | |

## Folgen für ADR-0075

- **A und C ja:** Adressfilter je Gerät wie geplant (WireGuard- + WLAN-Adresse, „Adresse übernehmen“).
- **A nein** (Adresse wechselt oder kommt nicht aus dem Heimnetz): Filter für WireGuard auf den Adressbereich der
  WireGuard-Geräte statt je Gerät — mTLS bleibt die eigentliche Sperre; ADR-0075 Schicht 1 anpassen.
- **C nein** (WLAN-Adresse wechselt): Heim-WLAN direkt nur mit Hinweis „Adresse übernehmen“ nach jedem Wechsel, oder zu
  Hause ebenfalls WireGuard — Entscheidung durch Herbert.
- **B nein:** getrennter Tunnel nicht möglich; dann Tunnel für alle Apps mit `AllowedIPs` = PC-Adresse (`/32`).
