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

## Verwalten

```
POST /api/admin/lap/hide     {"id": "...", "reason": "..."}
POST /api/admin/lap/show     {"id": "..."}
POST /api/admin/lap/ban      {"install_id": "...", "reason": "..."}
POST /api/admin/lap/unban    {"install_id": "..."}
GET  /api/admin/installs
```

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

- **Die Oberfläche in der App.** `LapSubmit.cs` kann anmelden, unterschreiben und
  einreichen; es gibt noch keinen Knopf dafür.
- **Der Auslöser.** Gedacht ist: eine Runde, die die Bestenliste für dieses Auto
  schlägt, oder ein Auto, das dort noch gar nicht steht. Dafür fehlt die Brücke
  zwischen der *geometrischen* Streckenkennung der App (`course_1300_275`) und dem
  *Namen*, unter dem die Bestenliste die Strecke führt. Ohne diese Brücke weiß die
  App nicht, gegen welche Bestzeit sie vergleichen soll.
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
