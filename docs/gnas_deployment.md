# Die Seite auf dem GNAS

Der Webserver ist vom Spielrechner auf das GNAS umgezogen. Diese Seite sagt, warum,
was wo liegt und wie ein Update läuft.

## Warum der Umzug

Der Spielrechner ist als Webserver die falsche Maschine. Er spielt Forza, er friert
ein, er startet neu, er ist tagsüber belegt. Das GNAS läuft ohnehin durch.

Gescannt und gebaut wird weiterhin **hier** — nur hier läuft das Spiel, nur hier
liegen die Sweeps. Besucht wird **dort**.

## Was wo läuft

| | Spielrechner | GNAS (im Heimnetz) |
|---|---|---|
| Forza, VM, Sweeps | ja | nein |
| Seite bauen (`build_analytics_site.py`) | ja | nein |
| App-Paket bauen | ja | nein |
| Seite ausliefern | nein | ja, Port 8787 |
| Abgaben annehmen | nein | ja |

Der Container läuft im **Spiegelbetrieb** (`--nur-ausliefern`). Er baut nichts. Ohne
diesen Riegel würde jeder Aufruf einen Bau anstoßen, der am fehlenden Bauskript
scheitert — die Seite bliebe heil, aber das Protokoll liefe mit Fehlern voll und
jeder echte Fehler ginge darin unter.

## Warum Port 8787 und nicht 80

Auf dem GNAS bedient Apache bereits Nextcloud auf Port 80 (`nc.gnas`). Diesen vhost
anzufassen hieße, für eine Rennseite an der Wolke zu schrauben, die dort täglich
gebraucht wird. 8787 kostet eine Portangabe in der Adresse und berührt nichts
Bestehendes.

## Warum ein Container, obwohl Python auf dem GNAS liegt

Dort läuft Debian 11 mit Python 3.9. Der Code setzt an mehreren Stellen 3.10er
Schreibweisen ein. Auf 3.9 fällt das nicht beim Start auf, sondern beim ersten
Aufruf, der die Zeile trifft — also genau dann, wenn jemand die Seite benutzt. Der
Container legt die Laufzeit fest und macht das NAS austauschbar.

Der Code wird **eingehängt**, nicht ins Image kopiert. Ein Update ist damit ein
Dateikopieren und ein Neustart des Containers.

## Autostart

Zwei Stellen, beide geprüft:

```
systemctl is-enabled docker          -> enabled
docker inspect -f '{{.HostConfig.RestartPolicy.Name}}' forza-site  -> unless-stopped
```

Der Docker-Dienst zieht beim Hochfahren an, der Container hängt mit dran.
`unless-stopped` und nicht `always`: ein absichtliches `docker stop` soll einen
Neustart überdauern.

## Der Befehl

```
python scripts/deploy_gnas.py                 Probelauf -- was würde geschehen
python scripts/deploy_gnas.py --apply         holen, bauen, schicken, neu starten
python scripts/deploy_gnas.py --status        läuft der Dienst, was liegt dort
python scripts/deploy_gnas.py --einrichten    einmalig: Ordner, Image, Dienst
```

### Von Hand muss das niemand

`scripts/category_sweep.sh` ruft am Ende eines Durchgangs das Ausliefern auf, das
vollständig gebaut war und trotzdem nie lief:

```
python scripts/deploy_gnas.py --apply --kein-bau ausliefern
```

Der Seitenaufbau läuft schon nach jedem Board — Seite **und** Haptik-Paket. Auf dem
GNAS landete beides bis zum 2026-09-14 nur, wenn jemand den Befehl von Hand tippte.
Ein Datensatz, der eine Woche alt ist, während hier täglich gescannt wird, ist für
alle außer dem Scanner wertlos.

Nach den Klassen und nicht nach jedem Board: es gehen rund 95 MB über die Leitung
(Seite 27 MB, Paket 66 MB) — je Board wäre das ein spürbarer Aufschlag auf einen
elfminütigen Scan.

### Warum erst geholt und dann geschickt wird

An beiden Enden entstehen Daten:

```
hier -> dort    die gebaute Seite, der Datensatz, das App-Paket, der Server-Code
dort -> hier    Fremdbeiträge (jemand lädt einen Scan hoch)
                der Sichtbarkeits-Schalter (jemand blendet im Admin einen Lauf aus)
```

Wer nur schickt, überschreibt beim nächsten Lauf genau das, was Besucher beigetragen
haben.

### Der Sichtbarkeits-Schalter vergleicht drei Stände

`config/dataset_visibility.json` lässt sich an beiden Enden ändern. Ein bloßer
Vergleich „beide verschieden → einer gewinnt" wirft die Änderung der anderen Seite
weg, ohne dass es auffällt. Darum liegt unter `data/runtime/gnas_sync/` eine Kopie
des zuletzt abgeglichenen Standes. Aus drei Ständen lässt sich sagen, *wer* geändert
hat — und wenn beide es taten, bricht der Lauf ab, statt zu raten.

### Neu gestartet wird nur bei Code

Seite und Datensatz werden bei jedem Aufruf frisch vom Datenträger gelesen; ein
Neustart bräuchte es dafür nicht, und jeder Neustart wirft einen laufenden Upload
weg.

**`docker compose restart`, nicht `up -d`.** Der Code liegt als eingehängter Ordner
im Container, nicht im Image. `up -d` vergleicht die Compose-Angaben — die sind
unverändert, also passiert nichts, und der Server läuft mit dem Modul weiter, das er
beim Start geladen hat. Genau so ging am 2026-09-13 eine neue Schnittstelle raus,
die drüben weiterhin `does not exist here` antwortete.

## Warum `server/` ein eigener Ordner ist

Was auf einem erreichbaren Rechner läuft, soll man an einer Stelle aufzählen können.
`server/` enthält genau das, und die Liste `CODE` in `deploy_gnas.py` ist genau sein
Inhalt.

Ausdrücklich **nicht** dabei:

- `build_analytics_site.py` — drüben wird nichts gebaut.
- `build_contrib_package.py` — es schnürt das Scanner-Werkzeug aus rund zehn OCR- und
  Fernsteuerungsdateien, die drüben gar nicht liegen. Der Bau müsste scheitern.
  Stattdessen geht das fertige Werkzeug als ZIP mit, und `/download/tool` liefert sie
  aus, wenn der Bauer fehlt.

## Was ausgeliefert wird — und was nicht

Der Server gibt **genau den Ordner der Seite** frei (`http.server` bekommt
`page.parent` als Wurzel). Auf dem Spielrechner liegen in `data/analytics/` auch
Arbeitsstände wie `unmatched_car_names_matched.tsv` (10 MB Zwischenergebnisse). Die
haben auf einem erreichbaren Server nichts zu suchen. Die Liste `INHALT` in
`deploy_gnas.py` ist der Grund, warum sie nie dorthin kommen — drüben liegen nur die
Dateien, die dort aufgezählt sind.

## Geheimnisse

| Was | Wo | Im Git? |
|---|---|---|
| Beitragenden-Schlüssel, Admin-Geheimnis | `config/contrib_keys.json`, drüben mit `600` | nein (`.gitignore`) |
| Adresse, Konto, Zielordner | `config/gnas_deploy.json` | nein (`.gitignore`) |
| SSH-Schlüssel für die Pipeline | `~/.ssh/gnas_deploy` | nein — **außerhalb des Baums** |

Der Schlüssel liegt bewusst nicht im Repository: das Schloss und der Schlüssel sollen
nicht nebeneinander liegen. `scripts/check_no_secrets.py` prüft vor jedem Commit
(`.githooks/pre-commit`), dass nichts davon hineinrutscht.

Adresse und Kontoname sind kein Geheimnis im engeren Sinn — 192.168.x.x führt von
außen nirgendwohin. Aber zusammen sind sie genau die Angaben, die man braucht, um es
an einer Tür zu versuchen, und sie nützen niemandem außer dieser einen Maschine. Was
keinen Nutzen hat, veröffentlicht zu werden, wird nicht veröffentlicht.

## Der Selbstaktualisierer der App

```
GET /api/haptics -> {"build": "<Inhaltskennung>", "sha256": ..., "bytes": ..., "url": ...}
```

Die installierte App trägt dieselbe Kennung in `app.meta.json` und vergleicht. Nicht
das Datum: zwei Pakete desselben Tages sind verschieden, und ein zurückgenommener
Stand trägt ein älteres Datum als das, was schon installiert ist. Nicht die
Dateizeit: eine Übertragung darf sie verändern.

Beim Austausch bleibt `config/` unberührt — dort liegt `overlay.json` mit allem, was
je eingestellt wurde. Ein Update, das die Arbeit eines Abends wegwirft, ist schlimmer
als gar kein Update.
