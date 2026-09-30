# Selbst gefahrene Runden einreichen

Spieler können eine eigene Rundenzeit an den Server schicken. Diese Seite sagt, wie
das geht, was dabei entschieden wird und **was es ausdrücklich nicht leistet**.

Stand 2026-09-13: Server und App-Client sind gebaut und geprüft. Die Endpunkte sind
**noch nicht auf dem GNAS freigeschaltet** — das ist eine bewusste Entscheidung, die
noch aussteht.

## Warum nicht `contrib_format`

Es gibt bereits ein Verfahren für Fremdbeiträge (siehe [Contributions](contributions.md)).
Dort gibt ein **vorher eingetragener** Beitragender gescannte Bestenlisten ab, mit
einem Geheimnis, das er von Hand bekommen hat.

Hier ist die Lage umgekehrt: ein beliebiger Spieler reicht eine selbst gefahrene
Runde ein, und **niemand kennt ihn**. Es gibt kein Geheimnis, das man ihm vorher
geben könnte. Deshalb ein eigener Weg — und nicht eine aufgeweichte Fassung des
bestehenden, denn zwei Ausweisverfahren im selben Rumpf sind genau die Stelle, an
der später das falsche genommen wird.

## Der Handschlag

```
POST /api/lap/register   {"hardware": "<SHA-256>", "gamertag": "..."}
->                       {"install_id": "...", "secret": "..."}
```

Das Geheimnis wandert **einmal** durchs Netz und danach nie wieder. Jede Einreichung
*rechnet* damit:

```
X-Forza-Install:   <install_id>
X-Forza-Timestamp: <Unixzeit>
X-Forza-Nonce:     <8-64 Zeichen>
X-Forza-Signature: HMAC-SHA256(secret, METHODE \n PFAD \n ZEITSTEMPEL \n NONCE \n SHA256(Rumpf))
```

Jedes Stück hat einen Grund:

| Bestandteil | verhindert |
|---|---|
| Pfad | dass eine Unterschrift an einem anderen Endpunkt gilt |
| Zeitstempel | dass ein mitgeschnittener Aufruf morgen noch zählt (±300 s) |
| Nonce | dass er innerhalb dieser 300 s hundertmal zählt |
| Rumpf-Hash | dass unterwegs etwas verändert wird |

Über einfaches HTTP liest jeder im selben Netz den ganzen Aufruf mit. Verborgen wird
hier nichts — aber niemand kann sich als jemand anderes ausgeben, und nichts lässt
sich unterwegs ändern.

## Warum überhaupt unterschrieben wird

**Nicht** um jemanden fernzuhalten — mitmachen soll jeder. Sondern damit eine
Einreichung einem Konto **zuzuordnen** ist. Ohne das lässt sich niemand sperren, und
ohne Sperre ist jede Bestenliste beliebig.

Wer sich neu anmeldet, fängt neu an. Aber der Hardware-Hash bleibt, und darüber
fällt eine Kette von Neuanmeldungen auf. Eine Maschine bekommt höchstens fünf
Kennungen — genug für Neuinstallation, zweites Windows, zurückgesetztes Profil.

> **Diese Grenze allein trägt nicht, und das ist kein Versehen.** Sie hängt am
> Hardware-Hash, und den schickt der *Client*. Ein Skript setzt jedes Mal einen
> anderen hinein und legt beliebig viele Konten an, bis die Schlüsseldatei platzt.
> Sie hält Versehen ab, keinen Angriff.

Die Grenze, die trägt, hängt an der **Absenderadresse**: fünf Anmeldungen je Stunde
und zwanzig je Tag. Eine IP behauptet der Aufrufer nicht, sie entsteht an der
Leitung — sie ist das Einzige an einer offenen Anmeldung, das sich nicht frei
erfinden lässt. Gefragt wird **vor** der Prüfung der Eingaben: wer zu oft anklopft,
soll nicht auch noch erfahren, was der Server erwartet.

Was auch sie nicht kann: jemanden aufhalten, der über viele Adressen verfügt.
Dagegen hilft nur, die Anmeldung ganz zuzumachen — dafür ist der Schalter da.

## Der Hardware-Hash und der Pfeffer

Die App bildet einen SHA-256 aus der Windows-MachineGuid und dem Rechnernamen und
schickt **nie** die Kennung selbst. Der Server hasht das ein zweites Mal mit einem
eigenen Geheimnis — dem *Pfeffer* — bevor er es ablegt.

Der Grund ist nicht Misstrauen gegen die App, sondern gegen die eigene Datei: ein
Hash ohne Pfeffer lässt sich durchprobieren, wenn man weiß, woraus er gebildet wurde.
Mit Pfeffer geht das nur, wenn man auch den Pfeffer hat — und der liegt in
`config/submit_keys.json`, die nie das Haus verlässt.

> **Der Pfeffer darf sich nie ändern.** Alle abgelegten Hashes sind mit ihm gebildet.
> Ein neuer Pfeffer macht jede Sperre wirkungslos, ohne dass irgendwo eine
> Fehlermeldung erscheint — der Gesperrte wäre einfach wieder da.

Der Gamertag steht dagegen im Klartext da. Er ist der Name, unter dem die Zeit
erscheinen soll; ihn zu verbergen wäre sinnlos.

**Der Gamertag ist freiwillig (seit 2026-09-27).** Gesperrt wird über Kennung und
gepfefferten Hardware-Hash, nicht über einen Namen. Ohne Gamertag wird trotzdem
eingereicht.

- **Der Name reist mit jeder Einreichung.** Die App schickt ihn unterschrieben
  (`"gamertag"` neben `"lap"`), und der Server übernimmt ihn für die Installation
  (`set_gamertag`). Ein später eingetragener oder geänderter Name braucht so keine
  neue Anmeldung. Früher kostete jeder neue Name eine Kennung, und nach fünf je
  Maschine nahm der Server keine mehr an.
- **Ein leeres Feld löscht nichts.** Der zuletzt geschickte Name bleibt.
- **Ein Spieler ist ein Hardware-Hash.** Angezeigt wird der Name beim Ausliefern
  der Liste (`spielernamen`, `mit_spielernamen`), nicht aus der abgelegten Runde:
  der zuletzt geschickte Name unter allen Kennungen derselben Maschine. So tragen
  nach einer Umbenennung auch die früheren Runden den neuen Namen, und eine
  Neuinstallation bleibt derselbe Spieler.
- **Ohne je einen Namen** zeigt die Seite einen vorläufigen, `Player-` und sechs
  Hex-Zeichen aus einem weiteren Hash des gepfefferten Hardware-Hashes. Er bleibt
  für diese Maschine gleich und führt nicht zum Hash zurück. Die Liste markiert ihn
  mit `gamertag_temporary`, die Verwaltungsseite mit „(temporary)".
- Weist der Server einen Namen wegen seiner Zeichen ab, meldet die App sich ohne
  ihn an. Neuere Xbox-Gamertags mit Nummer (`Name#1234`) sind erlaubt.

## Was als Betrugsschutz geprüft wird — und was nicht

Die Prüfungen finden **Unmögliches**, nicht Unwahrscheinliches:

- Schnitt über 600 km/h → abgewiesen
- weniger als 10 Messpunkte → abgewiesen (ohne Telemetrie wird nichts angenommen)
- Uhr läuft rückwärts, Weg wird kürzer → abgewiesen
- Sprung über 300 m zwischen zwei Messpunkten → abgewiesen
- zwei Punkte, die 600 km/h erfordern würden → abgewiesen
- **Telemetrie endet bei einer anderen Zeit als behauptet** → abgewiesen

Die letzte ist die wichtigste: sie fängt die einfachste denkbare Fälschung, nämlich
die Telemetrie einer langsamen Runde unter eine schnelle Zeit zu legen.

Was nur *merkwürdig* ist — Schneckentempo, fehlender Leistungsindex, Weglänge um
über 10 % daneben — wird **abgelegt und gekennzeichnet**, nicht verworfen. Wer
Merkwürdiges gleich wegwirft, verliert genau die Fälle, aus denen sich lernen ließe.

> **Wer mit einem veränderten Spiel eine plausible Zeit fährt, kommt hier durch.**
> Dagegen hilft nur ein Mensch, der sich die Telemetrie ansieht. Genau dafür wird sie
> mitgespeichert.

## Die volle Telemetrie reist mit (seit 2026-09-28)

Die Messpunkte der Runde (`samples`: Zeit, Weg, Ort, Tempo, Eingaben, G, Gang) reichen
für die Prüfungen oben — nicht aber, um eine verdächtige Zeit wirklich zu beurteilen.
Darum schickt die App mit **jeder** eingereichten Runde ihre ganze `.tele.gz` mit:
**alles, was das Spiel während der Runde geschickt hat** (Format 2, seit 2026-09-30) —
jedes Paket (rund 110 je Sekunde), jedes der 88 Felder in voller Genauigkeit
(Gleitkomma exakt, die Spieluhr `TimestampMS` als ganze Zahl), dazu jedes Byte ohne
Feld als eigene Spalte (`Byte323`, bei längeren Paketen mehr). Pakete mit derselben
Spielzeit wie das vorige bleiben drin; der Kopf zählt sie (`repeatedTimestamps`,
davon Byte für Byte gleich: `identicalRepeats`).

Format 1 (bis 2026-09-30) nahm 73 ausgewählte Felder, rundete auf drei
Nachkommastellen und verwarf jedes Paket mit wiederholter Spielzeit — in einer echten
Runde 4 689 von 11 247. Gelesen wird eine Spur darum immer über die Spaltennamen im
Kopf, nie über die Stelle; beide Formate stehen im Bestand nebeneinander.

```json
{"lap": {...}, "gamertag": "...",
 "telemetry": {"encoding": "gzip+base64", "format": "fhc-tele-2", "data": "<Base64>"}}
```

Größe: rund 27 KB je Sekunde Runde gepackt — eine 3-Minuten-Runde ~5 MB, die
18 Minuten, die die App höchstens aufhebt (120 000 Pakete), ~29 MB. Die App gibt einer
großen Einreichung mehr Zeit (gerechnet mit 64 KB/s), sonst liefe eine lange Runde auf
einer langsamen Leitung bei jedem Versuch in dieselbe Grenze.

- **Ohne volle Telemetrie reicht die App keine Runde ein.** Der Schalter „volle
  Telemetrie" betrifft nur das Archiv auf der Platte; für die Einreichung wird die
  Spur trotzdem behalten. Eine wartende Runde legt sie als Nebendatei neben sich
  (`pending_laps/<schlüssel>.tele.gz`). Gepackt wird abseits des UI-Threads — eine
  lange Runde kostet Zehntelsekunden, und das wäre ein Ruckler an der Ziellinie.
- **Der Server prüft sie wie die Runde selbst** (`pruefe_telemetrie`): gültiges
  Base64, gepackt höchstens 32 MB (die ganze Anfrage 48 MB), **entpackt nie über
  200 MB** (eine kleine Datei, die zu Gigabytes aufgeht, wird beim Entpacken gestoppt),
  10 bis 150 000 Zeilen, jede
  Zeile gleich breit und nur Zahlen, die Uhr `t` läuft vorwärts und **endet bei der
  Rundenzeit**. Sonst: abgewiesen.
- **Fehlt sie, wird die Runde angenommen, aber markiert** (`no full telemetry`) —
  ältere Fassungen der App schicken sie nicht.
- Abgelegt wird sie **unverändert neben** der Rundendatei (`<id>.tele.gz`); die Runde
  trägt nur eine Kurzinfo (`fullTelemetry`: Zeilen, Spalten, Bytes). Die Listen lesen
  sie nie.
- Die Grenze für einen Rumpf liegt darum bei 16 MB (vorher 8). Eine 214-s-Runde sind
  1,8 MB gepackt.
- Der Zustimmungstext nennt das seit **Fassung 6** — wer 5 zugestimmt hatte, kannte
  nur „positions and speeds".

## Je Auto, Strecke und Klasse die zehn schnellsten — je Mensch eine (seit 2026-09-28)

Nach jeder Annahme stutzt der Server die Gruppe (Strecke, Klasse, Auto — dieselbe
Einteilung wie der Abgleich mit der Bestenliste) auf **zehn Runden**, und davon
**höchstens eine je Mensch**. Mensch heißt: der gepfefferte Hardware-Hash der
Installation. Eine Neuinstallation auf derselben Maschine ist also derselbe Mensch.

Der Zweck: schickt jemand zehn gefälschte Runden, belegt er **einen** Platz, nicht
alle. Wird seine Runde ausgeblendet oder er gesperrt, stehen die neun davor noch da.

- **Ausgeblendete Runden zählen nicht mit und werden nie entfernt** — sie sind der
  Beleg dafür, was ausgeblendet wurde.
- Entfernt wird mit der Rundendatei auch ihre volle Telemetrie.
- Fällt die gerade eingereichte Runde selbst heraus, antwortet der Server trotzdem
  200, aber mit `"kept": false` — sie war gültig, nur nicht unter den zehn.

Weil der Server ohnehin nur Runden annimmt, die schneller sind als alles, was für
diese Gruppe schon eingereicht wurde, sind die zehn die letzten zehn Rekordhalter.

## Verwalten

```
POST /api/admin/lap/hide     {"id": "...", "reason": "..."}
POST /api/admin/lap/show     {"id": "..."}
POST /api/admin/lap/ban      {"install_id": "...", "reason": "..."}
POST /api/admin/lap/unban    {"install_id": "..."}
POST /api/admin/lap/telemetry      {"id": "..."}   die Runde samt Messpunkten
POST /api/admin/lap/fulltelemetry  {"id": "..."}   jedes Paket, alle Felder
GET  /api/admin/installs
```

In der Admin-Ansicht stehen dafür je Runde drei Knöpfe: **full csv** (jedes Paket,
alle Felder — nur, wenn die Runde ihre volle Telemetrie mitbrachte), **json** (die
Runde, wie sie auf dem Server liegt) und **csv** (die Messpunkte, eine Zeile je
Punkt).

Alle mit Admin-Unterschrift (dasselbe Verfahren wie die übrige Admin-Ansicht).

**Ausblenden, nie löschen** — dieselbe Regel wie bei den Scans. Was heute falsch
aussieht, ist morgen vielleicht der einzige Beleg dafür, *was* schiefging; und wer
eine Runde fälschlich ausgeblendet hat, soll das zurücknehmen können, ohne um eine
erneute Fahrt bitten zu müssen.

**Die nächste Zeit rückt von selbst auf.** Die Wertung baut sich aus den sichtbaren
Runden; eine ausgeblendete ist für sie nicht da. Ein eigener „nachrücken"-Schritt
wäre ein zweiter Ort, an dem dieselbe Entscheidung fällt.

**Sperren nimmt die Runden mit**, sonst hätte eine Sperre keine Wirkung auf das, was
schon auf der Seite steht. Entsperren bringt sie zurück — aber eine Runde, die aus
einem *anderen* Grund ausgeblendet wurde, bleibt es. Sonst höbe eine Entsperrung
stillschweigend ein Urteil auf, das mit ihr nichts zu tun hat.

Die Admin-Liste gibt **nie** ein Geheimnis heraus und vom Hardware-Hash nur die
ersten zwölf Zeichen: genug, um zwei Anmeldungen derselben Maschine zu erkennen, zu
wenig, um damit sonst etwas anzufangen.

## Runden, die warten (seit 2026-09-27)

Eine Runde, die die Bestenliste schlägt, aber gerade nicht hinaus kann, geht nicht
mehr verloren. Das gilt für ausgeschaltetes Einreichen, den Offline-Betrieb und
einen Server, der nicht oder mit einem Fehler antwortet.
Sie liegt dann in `pending_laps/` neben dem Buch (`LapQueue.cs`), eine Datei je
Strecke, Klasse und Auto; nur die schnellste bleibt.

- **Wann nachgereicht wird:** direkt nachdem der Server eine Datensatz-Abfrage
  beantwortet hat. Das beweist die Erreichbarkeit, und die Bestenliste ist dann
  die neueste. Solange Runden warten, fragt die App stündlich, außerdem beim
  Einschalten des Einreichens.
- **Vor dem Senden wird neu entschieden,** mit derselben reinen Prüfung wie nach
  einer gefahrenen Runde (`Pruefen`), aber gegen die Bestenliste und das Buch
  *dieses* Tages. Eine inzwischen überholte Runde fällt still weg, ohne Anfrage.
- **Was eine Runde aus der Schlange nimmt:** angenommen (200), „nicht schneller"
  (409, dann auch ins Buch) oder als ungültig abgelehnt (400/413/422). Alles
  andere lässt sie liegen: keine Verbindung, 5xx, 429, 401/403 und eine
  gescheiterte Anmeldung. Ein Verfallsdatum gibt es nicht.
- **Die Bremse gegen Strafpunkte:** Jedes Nachreichen hält beim ersten Ergebnis
  an, das kein Erfolg ist. Nach zwei Ablehnungen binnen 24 Stunden ruht es ganz,
  bis die ältere aus dem Fenster fällt. Der Server sperrt nach fünf Strafpunkten
  in 24 Stunden, und eine lange Schlange mit veraltetem Bild der Bestenliste
  könnte sonst in wenigen Stunden dorthin laufen.
- **Wer „aus" gewählt hatte,** erfährt es aus dem Hinweis beim ersten Start
  (Fassung 5): Einschalten reicht die besten Runden aus der Zwischenzeit nach.
  „Discard waiting laps" im Rivals-Tab wirft sie stattdessen weg.

## Der Modus trennt die Wertung (seit 2026-09-28)

Rivals und Horizon Play kennen keine Wandfahrten: Rivals erklärt eine Runde mit
Wandkontakt für ungültig, Horizon Play bremst den Motor. In Solo- und
Koop-Rennen und in der freien Fahrt ist die Wand dagegen oft die schnellste
Linie. Die Seite rechnet darum nur Runden mit `mode` `rivals` oder
`horizon-play` in die Wertung (`CLEAN_MODES` in `scripts/submitted_laps.js`).
Alle anderen stehen in einer eigenen Tabelle unter „Data as it stands“ und
verändern keine Platzierung.

Die App erkennt den Modus am zuletzt gelesenen Menü:

| Schirm | Modus |
|---|---|
| Rivals-Schirm (Überschrift „Rivals“, Klassen darunter) | `rivals`, dazu Strecke und Länge |
| Anmeldeschirm mit „Joining Horizon Play …“ | `horizon-play` |
| Anmeldeschirm ohne Reihe | `race` (Solo oder Koop, der Schirm sagt nicht welches) |
| eigene Freiwelt-Uhr | `freeroam` |

Ein Rivals-Schirm gilt zwei Stunden, eine Anmeldung 45 Minuten. Rivals nur, wenn
in der Runde niemand vor einem lag (`RacePosition` 1): ein Platz dahinter heißt,
es war ein Rennen, dessen Anmeldung nicht gelesen wurde, und der Modus bleibt
`unknown` (`modeEvidence` `conflict:…`). Runden mit unbekanntem Modus sendet die
App nicht; die Feier „schneller als die Website“ gibt es nur in der Wertung.

## Geprüft wird auf drei Ebenen

```
python scripts/run_tests.py --nur lap
```

| Test | beweist |
|---|---|
| `test_lap_submissions.py` | die Logik — 35 Prüfungen von Anmelden bis Entsperren |
| `test_lap_endpoints.py` | dass sie über **echtes HTTP** erreichbar ist, mit laufendem Server |
| `test_lap_signature.py` | dass C# und Python **dieselbe** Unterschrift bilden |

Der dritte ist der unscheinbarste und der wichtigste. Die Krypto selbst ist auf
beiden Seiten eingebaut und sicher richtig; was schiefgehen kann, ist die
*Nachricht* — Reihenfolge, Trennzeichen, Hex-Schreibweise, Kodierung. Und so ein
Fehler meldet sich nicht als das, was er ist: er erzeugt eine Unterschrift, die der
Server ablehnt, und das sieht aus wie „nicht angemeldet". Man sucht tagelang am
falschen Ende.

Der Test schneidet die Methode `Signature` bei jedem Lauf **frisch aus
`LapSubmit.cs`** heraus — eine Kopie hier würde von der ausgelieferten Fassung
abdriften, ohne dass es auffällt. Dasselbe Verfahren sichert im Projekt schon das
JavaScript der Admin-Seite ab.

## Was noch fehlt

- ~~Die Oberfläche in der App~~ und ~~der Auslöser~~: seit 2026-09-24 gebaut.
  `LapAutoSubmit.cs` reicht eine Runde nach dem Fahren selbst ein, wenn sie die
  Bestenliste schlägt. Der Streckenname kommt vom Anmeldeschirm oder aus dem
  gesammelten Kursnamen.
- **Neue Autos gegen die fh6cars-Liste abgleichen.**
Beides ist inzwischen gebaut:

- **Die Bremse je Absenderadresse** (2026-09-14).
- **Das Aufräumen alter Hardware-Hashes** (2026-09-14): zwölf Monate ohne
  Einreichung, dann wird der Eintrag entfernt. Gesperrte bleiben drei Jahre — sonst
  höbe sich jede Sperre nach einem Jahr lautlos auf. Ohne lesbares Datum wird nichts
  gelöscht: unbekanntes Alter heißt behalten. Es läuft bei jeder Anmeldung mit, denn
  ein Wartungsskript, das jemand von Hand starten müsste, läuft nie.

Die Bremse je Absenderadresse war die Bedingung dafür, die Endpunkte überhaupt
öffentlich zu machen. Sie ist seit dem 2026-09-14 gebaut und geprüft — der Schalter
in `config/features.json` steht trotzdem weiter auf `false`, bis jemand das
ausdrücklich ändert.
