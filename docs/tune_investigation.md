# Tune, Share-Code und Tuner -- was messbar ist und was nicht

Die Frage des Nutzers: **zu der besten Runde jedes Autos auf einer Bestenliste den
Share-Code des gefahrenen Tunes und den Namen des Tuners finden**, beides an die
Zeiten auf der Seite haengen und daraus eine **Tuner-Bestenliste** bauen.

Dieses Dokument haelt fest, was davon gemessen ist. Es ist bewusst als Kette von
Fragen geschrieben, weil jede Antwort die naechste erst moeglich oder ueberfluessig
macht.

## Die Kette

    Runde  ->  Tune-Id  ->  Share-Code + Ersteller  ->  Tuner-Wertung
             (Frage 1-3)      (Frage 4)                 (Seite)

Ohne Glied 1 gibt es keine Kette. Genau dort steht die Untersuchung.

## Frage 1: fuehrt die Bestenlistenzeile die Tune-Id? -- NEIN

`ScoreboardRow` hat laut Typtabelle des Spiels `versionedTuneId` bei +372 und
`tuneCreator` bei +392 (volles user-Objekt). Beide Felder sind **leer**:

| Quelle | Zeilen | mit Tune-Id |
|---|---|---|
| roher Abzug Juni, Soni Circuit R, Raenge 5.700-5.710 | 11 | 0 |
| live 2026-08-29, idx08 A, Raenge 16.001-16.050 | 50 | 0 |
| live 2026-08-29, idx09 A frisch geoeffnet, Raenge 201-2.350 | 250 | 0 |

**311 Zeilen, keine einzige mit Tune.** Kein Offset-Irrtum: dieselbe Tabelle
beschreibt Rang, xuid, Gamertag, Rundenzeit, submittedTime, driveType, powertrain,
Flags, `carId` (+344) und `carPerformanceIndex` (+348, als PI/1000) byte-genau
richtig. Hinter den Flags liegt ein *konstruiertes, aber leeres* user-Objekt -- ein
vtable-Zeiger bei 0x1C8 und fuenfmal die 15, die Kapazitaet leerer Kurzstrings. Die
Struktur ist da, der Dienst fuellt sie nicht.

**Folge: eine Tuner-Wertung ueber den ganzen Bestand (447.000+ Runden) ist tot.**
Es gibt keine Quelle, aus der sich der Tuner fuer die Masse ergaebe.

`capture_scoreboard_memory.py` liest die beiden Felder seitdem bei jedem Speicherlauf
mit und schreibt sie leer mit -- kostet nichts und beantwortet die Frage neu, falls
ein Spiel-Update das aendert.

## Frage 2: gibt es Id/Share-Code-Paare im Heap? -- WAHRSCHEINLICH, ungenau

Vier UGC-Satztypen halten Id und Share-Code nebeneinander (`UGCItem`, `TuningItem`,
`ForzaStorefrontFile`, `ForzaStorefrontFileLightweightData`), in **jeder** Fassung mit
genau **40 Byte Abstand**:

    UGCItem (kompakt, 552 B)          UGCItem (breit, 1648 B)
      +144  Id        versioned_id      +696  Id        versioned_id
      +184  ShareCode 10 Zeichen        +736  ShareCode 22 Byte
      +248  Creator   user(304)         +792  Creator   user(856)

Der erste Heap-Scan am laufenden Spiel fand **1.721 Treffer, alle Muell**
(Texturnamen wie `PRP_EW_R4_CO_RAM`). Die Signatur ist zu locker; sie braucht die
Laengenpruefung des Erstellernamens. Ungetestet geblieben, weil es ohne Tune-Ids
nichts zu verbinden gab. Werkzeug: `scripts/find_tune_share_codes.py`.

Wichtig: **hier liegt auch der Tuner-Name** (`Creator`), nicht nur der Code. Steht
die Id fest, faellt beides zusammen ab.

## Frage 3a: liegt der Rivalen-Datensatz im Speicher, waehrend nur geblaettert wird? -- NEIN

Gemessen am **2026-08-30 um 17:15**, waehrend der Sweep River Descent S2 las
(`scripts/probe_rival_tune.ps1`, rein lesend, neben dem laufenden Sweep).

`ScoreboardScoreData` (112 Byte) ist die Struktur hinter dem **ausgewaehlten**
Rivalen, mit `versionedTuneId` bei +80 und `versionedLiveryId` bei +96.

Der Scan fand **vier Saetze, alle mit leerer Tune-Id -- und alle vier sind
Fehltreffer**:

    car=18350  class=0  PI=101.4  livery=50d7463400000000...
    car=18350  class=0  PI=165.7  livery=50d7463400000000...
    car=18350  class=0  PI=178.6  livery=50d7463400000000...
    car=18350  class=0  PI=219.9  livery=50d7463400000000...

Woran man das sieht: viermal dieselbe `car_id`, viermal dieselben acht
Fuehrungsbytes im "Livery"-Feld, die zweite Haelfte davon lauter kleine negative
Gleitkommazahlen (`...be`, `...bf`), PI-Werte am aeussersten unteren Rand des
erlaubten Bereichs, und drei der vier Adressen in benachbarten Bloecken. Das ist
Zahlenmuell, der durch die Plausibilitaetspruefung rutscht, kein Fahrzeugdatensatz.

**Folge: die Tune-Id laesst sich NICHT nebenbei ernten.** Der Sweep blaettert nur,
und beim Blaettern legt das Spiel die Rivalenstruktur nicht an. Der Traum -- 245
Boards liefern die Tunes im Vorbeigehen -- ist damit erledigt.

## Nebenbefund: auf dem Schirm steht das Tune nirgends

Alle **8.044** je aufgenommenen Bildschirmtexte (`data/runtime/navigation/*/frames/*.txt`,
seit dem 18.08.) nach `tun` durchsucht. Was vorkommt, gehoert zur eigenen Garage --
"Tune your Car", "Tune Car", "Upgrades & Tuning" -- und zu Streckenbeschreibungen
("stunning bamboo", "cliffside tunnels"). Zu einem **fremden** Fahrer taucht kein
einziges Mal ein Tune-Name, ein Ersteller oder ein Share-Code auf.

Das schliesst den OCR-Weg fuer alles aus, was wir bisher gesehen haben: Bestenliste,
Klassenschirm, Streckenliste. Es sagt nichts ueber die Rivalen-Detailkarte nach
ENTER -- diesen Zustand hat noch nie jemand aufgenommen. Genau darum macht
`probe_rival_detail_tune.ps1` vorher UND nachher ein Bild: faende sich das Tune dort
im Klartext, waere der ganze Speicherpfad ueberfluessig.

## Frage 3b: fuellt das Spiel die Struktur, wenn ein Rivale AUSGEWAEHLT ist? -- NEIN

Das ist die letzte offene Frage, und an ihr haengt alles Weitere.

Auf der Bestenliste waehlt **ENTER** die markierte Zeile; das Spiel geht dann auf den
Klassenschirm zurueck und zeigt dort ein Feld "Details" mit *Name*, *Time to Beat*
und *Vehicle Used*. Erst in diesem Moment hat es die Daten dieses Rivalen geholt.

Messwerkzeug steht bereit: **`scripts/probe_rival_detail_tune.ps1`**. Es macht ein
Bild, drueckt ENTER, wartet, macht ein Bild und liest dann Speicher (Rivalensatz und
Share-Code-Paare). Es **braucht das Spiel exklusiv** -- es drueckt eine Taste, also
darf kein Sweep laufen.

Drei moegliche Ausgaenge:

* **gefuellt** -> es gibt einen Weg zum Tuner, aber einen Menueaufruf je Runde;
* **leer, obwohl der Schirm den Rivalen zeigt** -> das Spiel haelt das Tune nur
  serverseitig, das Thema ist zu;
* **Struktur gar nicht auffindbar** -> die Offsets der Typtabelle passen nicht auf
  diese Fassung, dann muss der Anker neu gesucht werden.

## Das Ergebnis von Frage 3b (2026-08-30, 18:00)

Gemessen an der Klassenboundary nach Cedar Run S2, mit
`scripts/probe_rival_detail_tune.ps1`: ENTER auf die markierte Bestenlistenzeile,
6 s warten, Bild, dann Speicher lesen.

**Der Tastendruck hat gewirkt** -- der Schirm zeigt danach die Rivalenkarte:

    Details
      Name          SkokuNaFide
      Time to Beat  01:48.751        Gap to Rival  ---
      Vehicle Used  2005 Mitsubishi Sierra Sierra Enterprises Lancer Evolution
                    S2 900   AWD   ABS o  TCS o  STM o  GEAR M

**Und genau hier steht die Antwort, im Bild und ohne jede Speicherforensik: die
Karte nennt kein Tune.** Name, Zeit, Auto, Klasse, PI, Antrieb, drei Assistenten,
Getriebe -- das ist alles. Kein Tune-Name, kein Ersteller, kein Share-Code, und auch
keine Taste, die dorthin fuehrte (ENTER faehrt das Rennen, Y wechselt den Rivalen,
ESC geht zurueck).

Der Speicherlauf im selben Moment fand **null** `ScoreboardScoreData`-Saetze -- nicht
etwa leere, sondern gar keine. Beim Blaettern waren es vier Fehltreffer, jetzt keiner.

**Ehrlich zur Reichweite dieses Befunds:** "nicht gefunden" ist schwaecher als
"gefunden und leer". Faende der Anker die Struktur aus einem anderen Grund nicht
(andere Offsets in dieser Spielfassung), saehe das Ergebnis genauso aus. Was den
Schluss trotzdem traegt, ist die Summe: 311 Zeilen mit leerem Tune-Feld, 8.044
Bildschirmtexte ohne ein einziges fremdes Tune, und eine Rivalenkarte, die das Tune
nicht zeigt, obwohl sie sieben andere Angaben zum selben Fahrer zeigt.

### Der eine Test, der noch aussteht

Statt die Struktur ueber vermutete Offsets zu suchen, ueber den **Gamertag** gehen:
im Heap nach `SkokuNaFide` suchen und die Bytes drumherum ansehen. Das findet den
Datensatz des ausgewaehlten Rivalen unabhaengig von jeder Offset-Annahme. Faende sich
dort eine 16-Byte-Id, waere Frage 3b doch noch mit JA zu beantworten.

Das braucht das Spiel exklusiv und gehoert deshalb an die naechste natuerliche
Pause -- nach dem R-Durchgang von Street Racing.

## Frage 5: liegt das Tune im Speicher, waehrend der GEIST FAEHRT? -- laeuft

Der Einwand kam vom Nutzer und er ist gut: **im Rennen faehrt der Geist des Rivalen.**
Damit das Spiel dieses Auto darstellen kann, muss es dessen Aufbau geladen haben --
Karosserie, Raeder, Fluegel kommen aus den Ausbaustufen, und die haengen am Tune. Im
Menue muss davon nichts da sein, im Rennen schon.

Alle bisherigen Messungen waren **Menuemessungen**. Diese Frage ist davon nicht
beruehrt und war schlicht ungetestet.

### Diesmal ohne Offset-Annahme

Der Schwachpunkt aller bisherigen Suchen: sie suchten nach *vermuteten* Offsets.
Findet so ein Lauf nichts, sind zwei sehr verschiedene Dinge moeglich -- die Daten
sind nicht da, ODER die Offsets stimmen fuer diese Fassung nicht. Aus dem Fehlschlag
laesst sich das nicht unterscheiden.

`scripts/find_bytes_around.py` (neu, selbstgetestet) dreht die Richtung um: es sucht
nach etwas, das wir SICHER kennen -- dem **Gamertag** des Rivalen, wie er auf dem
Schirm steht -- und zeigt die Bytes drumherum, in UTF-8 und UTF-16LE. Dazu meldet es
Kurzstrings, Share-Code-foermige Zeichenketten und 16-Byte-Bloecke, die eine
versioned_id sein koennten. Findet DAS nichts, heisst das wirklich "da ist nichts".

### Ehrlich zur Erwartung

Es kann sein, dass das Spiel nur den fertigen **Aufbau** laedt (die Liste der
Ausbaustufen) und nicht die Herkunft -- also keinen Share-Code und keinen
Erstellernamen. Der Aufbau allein macht noch keine Tuner-Wertung. Gefunden waere dann
etwas, nur nicht das Gesuchte.

### Wie er laeuft

`scripts/probe_rival_race_tune.ps1`: Rivale waehlen, Rennen starten, waehrend der
Fahrt lesen (Gamertag-Anker + die beiden Offset-Suchen), danach ESC und **nur
schauen** -- durch ein unbekanntes Pausenmenue wird nicht blind gedrueckt.

Verkettet in `scripts/race_probe_after_sweep.sh`: wartet, bis alle 105 Boards stehen
UND kein Sweep mehr laeuft, navigiert dann selbst zu einem **S1**-Board und startet
den Test. S1, weil zum Starten eines Rivals-Rennens ein zugelassenes Auto noetig ist
und im Spiel gerade ein S1-Wagen sitzt (TVR Griffith, 800) -- auf einem R-Board haette
der Test einen Autoauswahlschirm gemessen statt eines Rennens.

Angehalten wird er mit `touch data/runtime/overnight/RACE_PROBE_STOP`.

## Der Aufbau statt des Codes -- und was die Typtabelle dazu hergibt

Der Nutzer hat die Frage gedreht, und die neue ist besser: **wenn der Share-Code
nicht zu finden ist, dann zeig, WAS getunt wurde** -- Teile und Einstellungen, je
Runde und Auto eine Unterseite, damit man den Wagen nachbauen kann. Ein Code verfaellt,
wenn ein Tune geloescht wird; ein Bauplan nicht.

Und das steht vollstaendig in der Typtabelle:

    TuningItem (1196 B)
      +0    UGCItem (552 B)
              +144  Id         versioned_id
              +184  ShareCode  kurze Zeichenkette
              +248  Creator    UserData -> qwXuid UND wzGamerTag
      +552  TuningData (636 B)
              +8    InstalledParts  (CInstalledParts, 404 B, 49 benannte Plaetze)
              +424  CarTuneDef      (Einstellungen + Getriebe)
              +624  CarId / CarMakeId / CarClassId

`CInstalledParts` fuehrt **49 benannte Teileplaetze**: Motor, Antrieb, Karosserie,
Bremsen, Feder/Daempfer, Stabilisatoren, Reifenmischung, Fluegel, Felgengroesse,
Nockenwelle, Ventile, Hubraum, Kolben, Kraftstoffanlage, Zuendung, Auspuff, Ansaugung,
Schwungrad, Krümmer, Restriktor, Oelkuehlung, Single-/Twin-/Quad-Turbo, zwei
Kompressoren, Ladeluftkuehler, Kupplung, Getriebe, Antriebsstrang, Differential,
Stossfaenger, Haube, Schweller, Reifenbreite, **Gewichtsreduktion**, Chassissteifigkeit
und mehr.

`CCarTuneDef` fuehrt die Einstellungen: Anpressdruck vorn/hinten, Achsantrieb,
Bremsdruck und -balance, Handbremse, Mittendifferential, und **je Achse** Reifendruck,
Sturz, Spur, Nachlauf, Federn, Stabilisator, Fahrhoehe, Druck- und Zugstufe sowie die
beiden LSD-Werte -- dazu Getriebeuebersetzungen und Tankfuellung.

Das ist Zeile fuer Zeile der Tuning-Schirm des Spiels. Wer das hat, kann nachbauen.
Werkzeug: **`scripts/find_tune_data.py`** (selbstgetestet, 19 Pruefungen).

### DER FAHRER IST NICHT DER TUNER

Vom Nutzer angemerkt, und es ist der Satz, an dem eine Tuner-Wertung sonst still
falsch wuerde: **wer eine Runde faehrt, hat das Tune meistens von jemand anderem.**
Die beiden Namen stimmen "die meiste Zeit" nicht ueberein.

Daraus folgt hart:

* Der Gamertag der **Bestenlistenzeile** gehoert zum **Fahrer**.
* `TuningItem.Creator.wzGamerTag` gehoert zum **Tuner**.
* Sie duerfen nirgends zusammenfallen -- nicht in der Auswertung, nicht auf der
  Seite, und schon gar nicht in einer Tuner-Wertung, die sonst dem Fahrer Punkte
  fuer fremde Arbeit gaebe.
* Findet sich kein Erstellername, heisst das **unbekannt** -- und wird NIE vom
  Fahrernamen ergaenzt.

Im Code tragen darum alle betroffenen Felder das Praefix `creator_`, und jeder Fund
bringt eine `warning`-Zeile mit, die genau das sagt.

### Warum der Ersteller-NAME wichtiger ist als die Ersteller-xuid

Naheliegend waere, eine gefundene `creatorXuid` gegen den eigenen Bestand
aufzuloesen. **Gemessen geht das nicht:** ueber alle 294 `rows.jsonl` hinweg stehen
3.496.215 Zeilen, aber nur **713 verschiedene xuids und 66 Zeilen mit Gamertag** --
die grosse Masse der Boards wurde per OCR vom Schirm gelesen, und der Schirm zeigt
keine xuid. Eine Ersteller-xuid ginge also fast immer ins Leere.

Gut, dass `UserData` den Namen gleich mitfuehrt (`wzGamerTag` neben `qwXuid`): dann
braucht es die Aufloesung gar nicht.

### Ein Fehlschlag, der beinahe durchgegangen waere

Die erste Fassung des Filters meldete gegen das laufende Spiel **25 Funde -- alle
Muell**. Zwei Luecken, beide inzwischen als Pruefung festgeschrieben:

1. **Denormale Gleitkommazahlen.** Zufallsbytes werden haeufig zu Winzlingen wie
   1e-42. Die sind groesser als null, endlich und kleiner als jede Obergrenze -- eine
   Pruefung "Federrate > 0" laesst sie also durch, und der ganze Heap sieht nach
   Tunes aus. Jetzt gilt eine echte Untergrenze.
2. **Teilewerte in Zeigergroesse.** In den Funden stand `Engine=1073741824` --
   das ist 0x40000000, also die Bytes der Gleitkommazahl 2.0. Ausbaustufen sind
   kleine Ganzzahlen; diese eine Pruefung raeumt weg, woran die Zahlenpruefung
   allein scheitert.

### Der Suchlauf war zunaechst unbrauchbar -- dreimal gemessen statt geraten

Ein Werkzeug, das nicht fertig wird, ist schlimmer als eines, das nichts findet: es
sagt einem nicht einmal, dass es nichts gefunden hat. Genau das passierte.

| Fassung | Abgesucht | Zeit | Ergebnis |
|---|---|---|---|
| erste | -- | >900 s | von aussen abgeschnitten, **keine Ausgabe** |
| Zeitgrenze + Vorpruefung | 1,18 GiB | 300 s | unvollstaendig |
| **Negativ-Vorschauen im Anker** | **5,7 GiB** | **99,7 s** | **vollstaendig** |

Zwei Lehren, beide teuer erkauft:

1. **Geraten hat zweimal danebengelegen.** Erst vermutete ich die Kosten in der
   Pruefung hinter jedem Treffer und baute eine billige Vorpruefung ein -- die Rate
   blieb exakt gleich (4,9 MB/s). Erst eine getrennte Messung von Lesen und Suchen
   zeigte es: 15,4 s Lesen gegen 84,2 s Suchen. Die Kosten lagen in der schieren
   ANZAHL der Anker-Treffer.
2. **Der Anker passte auf genullten Speicher.** Ohne Einschraenkung trifft
   "drei kleine Ganzzahlen" an jeder Stelle eines genullten Bereichs, und davon hat
   ein Spiel sehr viel. Zwei Negativ-Vorschauen -- CarId und CarMakeId duerfen nicht
   null sein -- brachten den Faktor 14.

Seitdem meldet **jeder** Lauf, wie viel Speicher er angesehen hat und ob er fertig
wurde. "Nichts gefunden" ist nur dann eine Aussage, wenn danebensteht, wie weit
gesucht wurde.

**Der Menue-Befund ist damit belastbar:** 5,7 GiB vollstaendig abgesucht,
**null** Tune-Datensaetze. Das ist die erwartete Antwort, solange kein fremdes Tune
geladen ist -- und der Grund, warum erst das Rennen etwas beweist.

## Was ein JA kosten wuerde -- gerechnet

Fuer die Seite waere die Zielmenge "beste Runde je Auto je Board":

    245 Boards, 68.530 verschiedene (Board, Auto)-Paare

Bei geschaetzt einer Minute je Rivale sind das **rund 1.140 Stunden**. Ein
vollstaendiger Tune-Bestand ueber die Seite ist also auch bei einem JA
**ausgeschlossen**. Bezahlbar sind nur Spitzen:

| Zielmenge | Aufrufe | Zeit |
|---|---|---|
| Platz 1 je Board | 245 | ~4 h |
| Top 3 je Board | 735 | ~12 h |
| Top 10 je Board | 2.450 | ~41 h |
| beste Runde je Auto je Board | 68.530 | ~1.140 h |

Eine **Tuner-Bestenliste** ueber "Top 3 je Board" waere trotzdem etwas wert: sie
beantwortet "wessen Tunes stehen an der Spitze", und das ist die Frage, die hinter
dem Wunsch steht. Ueber alle Runden ist sie nicht zu haben.

## Was NICHT geht

* **Den Share-Code-Dienst rufen.** `ForzaStorefront.GenerateShareCode(fileId)` liegt
  hinter `bin/xtsw`, und diese Antworten sind verschluesselt.
* **Aus dem Geisterwagen lesen.** Jede Zeile traegt `has_ghost_file`, aber das Herunterladen
  eines Geistes ist wieder ein Dienstaufruf.

## Werkzeuge

| Skript | tut | braucht das Spiel exklusiv |
|---|---|---|
| `find_tune_share_codes.py` | Id/Share-Code-Paare im Heap (40-Byte-Signatur) | nein, liest nur |
| `find_rival_score_data.py` | `ScoreboardScoreData` im Heap | nein, liest nur |
| `probe_rival_tune.ps1` | fuehrt beide im Gast aus, holt JSON zurueck | nein, liest nur |
| `probe_tune_fields.ps1` | Frage 1 + Frage 2 in einem Lauf | nein, liest nur |
| `probe_rival_detail_tune.ps1` | **Frage 3b**: ENTER, dann lesen | **ja** |
| `report_tune_probe.py` | liest eine Messung im Klartext vor | -- |
