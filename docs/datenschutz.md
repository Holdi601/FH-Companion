# Datenschutz — was wirklich verarbeitet wird

Diese Seite ist die **Quelle** für den Hinweis auf der Website. Sie steht auf
Deutsch, weil sie für den Betreiber geschrieben ist; auf der Website steht der
Hinweis auf Englisch, weil die Betroffenen dort englischsprachige Spieler sind.

> Ich bin kein Jurist. Was hier steht, ist eine **genaue Beschreibung dessen, was
> der Code tut** — das ist die Grundlage jeder Datenschutzerklärung und der Teil,
> den sonst niemand liefern kann. Ob die rechtliche Einordnung trägt, gehört von
> jemandem geprüft, der das darf.

## Der Rahmen, wie er mit dir besprochen wurde

- öffentlich erreichbar, **nicht wirtschaftlich** — kein Verkauf, keine Werbung,
  kein Spendenknopf auf der Seite
- Kontakt: die auf der Webseite genannte Adresse (config/local.json, `contact_email`)
- kein Impressum nötig, solange nichts Wirtschaftliches dazukommt. Ein Spendenknopf
  auf GitHub ändert das nicht; einer **auf der Seite** würde es ändern.

## Was verarbeitet wird — vollständig

### 1. Gamertags aus den Bestenlisten des Spiels

Der größte Posten: **1.393.919 Rundenzeiten** mit dem jeweiligen
Gamertag, abgelesen aus den öffentlichen Rivals-Bestenlisten von Forza Horizon 6.

Das sind **personenbezogene Daten** — ein Gamertag ist ein Online-Kennzeichen im
Sinne von Art. 4 Nr. 1 DSGVO, auch wenn er keinen Klarnamen nennt.

Die Rechtsgrundlage ist das **berechtigte Interesse** (Art. 6 Abs. 1 lit. f): die
Daten sind im Spiel für jeden Spieler ohnehin sichtbar, sie werden unverändert und
im selben Zusammenhang gezeigt (welches Auto ist auf welcher Strecke wie schnell),
und es findet keine Bewertung von Personen statt — bewertet werden Autos.

**Was daraus folgt:** Wer möchte, dass sein Gamertag hier verschwindet, bekommt das.
Eine E-Mail an die Adresse oben genügt; die Zeilen werden aus dem Datensatz
entfernt und die Seite neu gebaut. Der Gamertag ist der Suchschlüssel, das ist
technisch unproblematisch.

### 2. Selbst eingereichte Runden

Wer über die App eine eigene Rundenzeit einreicht, übermittelt:

| Was | Form | Warum |
|---|---|---|
| Gamertag | **Klartext** | Er ist der Name, unter dem die Zeit erscheinen soll |
| Hardware-Kennung | **gepfefferter SHA-256**, nie im Klartext | Damit eine Sperre nicht durch Neuanmeldung umgangen wird |
| Telemetrie der Runde | Positionen, Zeit, Tempo, Eingaben | Ohne sie ist eine Zeit nicht überprüfbar |
| Installationskennung | Zufallswert | Zuordnung von Einreichungen, um sperren zu können |

Rechtsgrundlage ist die **Einwilligung** (Art. 6 Abs. 1 lit. a): niemand reicht
etwas ein, ohne es auszulösen. Die Einwilligung ist widerrufbar — die Runde wird
dann ausgeblendet.

**Der Pfeffer** ist ein serverseitiges Geheimnis in `config/submit_keys.json`. Ohne
ihn lässt sich aus dem abgelegten Hash nicht zurückrechnen, welche Maschine gemeint
ist — auch nicht durch Ausprobieren, denn dazu bräuchte man den Pfeffer mit. Er
liegt nicht in derselben Datei wie die Auswertung und verlässt den Server nie.

### 3. IP-Adressen

Drei Stellen, alle unvermeidbar, alle begrenzt:

- **Server-Protokoll.** Jeder Seitenaufruf und jeder Download schreibt die IP in
  die Ausgabe des Containers. Docker hält davon höchstens **3 × 10 MB** und
  überschreibt danach von vorn — es gibt also kein wachsendes Archiv.
- **`data/runtime/upload_attempts.json`.** Wer beim Hochladen zehnmal ein falsches
  Passwort eingibt, wird für 24 Stunden gesperrt. Dafür steht die IP dauerhaft in
  dieser Datei. Sie ist der Preis dafür, dass eine Sperre einen Serverneustart
  überlebt — im Speicher wäre sie nur eine Bitte.
- **`data/runtime/register_attempts.json`.** Wer die App anmeldet, hinterlässt dort
  einen Zeitstempel zu seiner IP; mehr als fünf Anmeldungen je Stunde und zwanzig je
  Tag werden abgewiesen. **Einträge älter als 24 Stunden werden bei jedem Zugriff
  entfernt**, die Datei wächst also nicht. Ohne diese Bremse legt ein Skript beliebig
  viele Konten an — die andere Grenze hängt am Hardware-Hash, und den schickt der
  Client selbst.

### 4. Die Nutzungszählung

Seit dem 2026-09-15 zählt der Server zwei Dinge, weil sonst niemand weiß, ob das
hier überhaupt jemand benutzt:

- **Downloads.** Jeder Abruf von `/download/haptics`. Eine schlichte Zahl je Tag,
  ohne jeden Absenderbezug.
- **Aktive Installationen.** Jede App, die sich an einem Tag meldet. Sie tut das
  ohnehin: beim Start fragt sie `/api/summary` und `/api/haptics`.

**Was gespeichert wird.** Nicht die IP, sondern `HMAC(geheimes Salz, Kennung)`,
gekürzt auf 16 Zeichen. Das Salz liegt in `data/runtime/usage_salt.txt`, wird beim
ersten Lauf zufällig erzeugt, ist chmod 600 und **wird nie ausgeliefert** — es
steht in keiner Sicherung, die das Haus verlässt. Aus dem Gespeicherten lässt sich
die Adresse nicht zurückrechnen, und wer das Salz nicht hat, kann auch nicht
prüfen, ob eine bestimmte Adresse dabei war.

**Aufbewahrung: 40 Tage.** Das ist die längste Frage, die die Verwaltungsseite
stellt (30 Tage), plus Luft. Ältere Tage werden bei **jedem Schreibvorgang**
entfernt — nicht von einem Aufräumlauf, den irgendwann niemand mehr anstößt.

**Was die Zahlen nicht sind.** Zwei Menschen hinter demselben Anschluss zählen als
einer. Ein Mensch mit zwei Rechnern zählt als zwei. Wer das Programm nie startet,
fehlt. Es ist eine Untergrenze für die Benutzung, keine Kopfzahl, und die
Verwaltungsseite sagt das auch so.

**Abschaltbar.** `"offline": true` in `config/overlay.json` — dann fragt die App
den Server überhaupt nicht mehr und taucht in keiner Zählung auf. Die Erklärung
beim ersten Start der App nennt diesen Schalter ausdrücklich.

### 5. Die Besucherstatistik der Seite

Seit dem 2026-09-25 zählt der Server, wie oft die öffentlichen Seiten aufgerufen
werden (Auswertung, `/app`, `/contribute`) — je Stunde, Tag, Land und Region — und
wie oft jede Fassung der App und des Scan-Werkzeugs geladen wurde.

**Ohne Cookie, ohne gespeicherte Adresse.** Ein Besucher ist
`HMAC(Tagessalz, IP + Browserkennung)`, gekürzt auf 16 Zeichen. Das Tagessalz
entsteht zufällig und wird um Mitternacht (UTC) **verworfen**, zusammen mit allen
Hashes des Tages. Besucher lassen sich damit innerhalb eines Tages unterscheiden,
aber nie über Tage hinweg wiedererkennen — auch nicht von jemandem, der die Datei
`data/runtime/visits.json` hat. Auf dem Gerät des Besuchers wird nichts gespeichert
(§ 25 TTDSG greift nicht); eine Einwilligung ist dafür nicht nötig.

**Land und Region** kommen aus einer **lokalen** Datenbank (DB-IP „IP to City
Lite", CC BY 4.0), die der Server selbst monatlich holt. Die Adresse eines
Besuchers geht dabei an **niemanden** — nachgeschlagen wird in einer Datei auf
diesem Server, gespeichert wird nur die Zählung je Land und Region. Städte werden
bewusst nicht gelesen: bei wenigen Besuchern wäre eine Stadt schon fast eine Person.

**Nicht gezählt** werden Bots und Skripte (an der Browserkennung), die
Verwaltungsseite und das eigene Netz des Betreibers.

**Rechtsgrundlage:** Art. 6 Abs. 1 lit. f DSGVO — das berechtigte Interesse, zu
wissen, ob und von wo die Seite genutzt wird. Die IP wird dafür nur einen
Augenblick verarbeitet und nicht aufbewahrt; ein Widerspruch ist über die Adresse
unten möglich, praktisch aber gegenstandslos, weil sich kein gespeicherter Wert
einer Person zuordnen lässt.

### 6. Was die App beim ersten Start erklärt

Seit dem 2026-09-15 zeigt die App beim ersten Start einen Text, der sagt, was sie
tut: dass sie den Speicher des laufenden Spiels liest (nur lesend, nur im
Tuning-Reiter), dass sie sich beim Update selbst austauscht, dass sie **lädt und
nichts sendet**, und dass Windows sie deshalb für einen Trojaner halten kann. Der
Haken darunter wird erst anklickbar, wenn der Text bis zum Ende gescrollt wurde;
wer ablehnt, bekommt die App nicht.

Die Zustimmung hängt an einer **Fassungsnummer** (`Disclosure.Fassung`). Ändert
sich, *was* die App tut, wird erneut gefragt — eine Zustimmung gilt für den Text,
dem zugestimmt wurde.

### 7. Was **nicht** stattfindet

- keine Cookies, kein LocalStorage, keine Sitzungskennung
- keine Reichweitenmessung durch Dritte, kein Analytics-Dienst, kein Tracking-Pixel
  — die eigene Besucherzählung (Abschnitt 5) läuft auf diesem Server, ohne Cookie
  und ohne gespeicherte Adresse
- keine externen Ressourcen: die Seite ist **eine einzige Datei**, sie lädt weder
  Schriften noch Skripte noch Bilder von fremden Servern. Ein Aufruf erreicht
  genau einen Rechner
- keine Weitergabe an Dritte, keine Übermittlung außerhalb der EU
- kein Profiling, keine automatisierte Entscheidung mit rechtlicher Wirkung

## Aufbewahrung

| Was | Wie lange | Warum |
|---|---|---|
| Bestenlisten-Zeilen | dauerhaft | Sie sind der Zweck der Seite |
| Eingereichte Runden samt Telemetrie | dauerhaft, solange sichtbar | Eine Bestzeit ohne Beleg ist wertlos |
| Ausgeblendete Runden | bleiben, aber unsichtbar | Was heute falsch aussieht, ist morgen der einzige Beleg dafür, **was** schiefging |
| Hardware-Hashes nicht gesperrter Konten | **12 Monate** ohne Einreichung | Danach hat er keinen Zweck mehr |
| Hardware-Hashes gesperrter Konten | länger | Sonst hebt sich die Sperre von selbst auf |
| IP im Container-Protokoll | rotierend, max. ~30 MB | Fehlersuche |
| IP in `upload_attempts.json` | bis die Sperre abläuft | Ohne sie keine Sperre |
| IP in `register_attempts.json` | 24 Stunden, dann automatisch entfernt | Ohne sie ist die Anmeldung beliebig oft möglich |
| Gehashte Kennungen in `usage.json` | **40 Tage**, bei jedem Schreiben beschnitten | Untergrenze der Benutzung; keine Adresse, nicht zurückrechenbar |
| Downloadzahlen je Tag | 40 Tage | dieselbe Datei, kein Personenbezug |
| Besucher-Hashes (`visits.json`) | **nur der laufende UTC-Tag**, dann mit dem Salz verworfen | Besucher eines Tages unterscheiden, nie über Tage verfolgen |
| Besuchszahlen je Stunde, Tag, Land, Region | dauerhaft | reine Zählung, kein Personenbezug |
| Downloads je Fassung (`downloads.json`) | dauerhaft | reine Zählung, kein Personenbezug |

## Rechte der Betroffenen

Auskunft, Berichtigung, Löschung, Einschränkung, Widerspruch, Beschwerde bei einer
Aufsichtsbehörde — alles über die auf der Webseite genannte Kontaktadresse.

**Eine Einschränkung, die ehrlich benannt gehört:** Für eingereichte Runden kann der
Betreiber niemanden identifizieren. Es liegen ein Gamertag und ein gepfefferter Hash
vor, sonst nichts. Wer eine Löschung verlangt, muss darum sagen, **welcher Gamertag**
gemeint ist — etwas anderes gibt es nicht, wonach gesucht werden könnte. Das ist der
Fall, den Art. 11 Abs. 2 DSGVO beschreibt.

## Warum kein Impressum

§ 5 DDG / Art. 3:15d BW verlangen Angaben von Diensten **wirtschaftlicher Art**.
Eine öffentlich erreichbare, kostenlose Hobbyseite ohne Werbung, Verkauf oder
Spendenaufruf fällt nicht darunter. **Das kippt**, sobald auf der Seite selbst um
Geld gebeten wird — dann ist ein Impressum mit ladungsfähiger Anschrift nötig, und
eine Postfachadresse genügt dafür nicht.

## Sperren und „Hausrecht"

Ja — es gibt kein Recht darauf, auf einer privaten, kostenlosen Seite zu erscheinen.
Eine Sperre ist keine automatisierte Entscheidung im Sinne von Art. 22 DSGVO: sie
hat keine rechtliche Wirkung und keine vergleichbare erhebliche Beeinträchtigung,
und sie wird ohnehin von Hand ausgelöst, nicht automatisch.

Was trotzdem gilt: Der Grund steht in der Antwort des Servers, damit niemand rätselt
(`403 This installation is banned: <Grund>`). Und sie ist zurücknehmbar — beim
Entsperren kommen die ausgeblendeten Runden zurück.
