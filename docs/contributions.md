# Fremde Scans annehmen

Freunde scannen Boards auf ihrem eigenen Rechner und schicken die Zeilen zurück. Es
ist derselbe Scanner wie hier, nur ohne VM — die Bilder kommen vom eigenen Bildschirm.

## Einem Freund ein Paket geben

```powershell
python scripts\build_contrib_package.py kai
```

Legt ein Geheimnis für `kai` an, sammelt die 18 Dateien ein, die der Scanner auf
einem fremden Rechner braucht, und schreibt `dist\forza-contrib-kai-<datum>.zip`
(rund 100 KB — es ist alles Text).

Das Paket enthält sein persönliches Geheimnis. Also über einen Weg schicken, dem du
traust, und nicht öffentlich ablegen.

Er entpackt, doppelklickt `start.cmd`, das richtet beim ersten Mal eine
Python-Umgebung ein (~300 MB, einmalig) und startet dann den Scanner.

## Was zurückkommt

Eine ZIP mit **Zeilen, nicht Bildern**:

```
manifest.json          wer, wann, womit, und je Lauf ein SHA-256
runs/<lauf>/state.json
runs/<lauf>/rows.jsonl
signature.txt          HMAC über das Manifest, mit seinem Geheimnis
gate.txt               dasselbe, mit dem allgemeinen Zugangspasswort
```

Rund 5 MB je Board. Rohe Aufnahmen wären zwischen 266 MB und 2,2 GB — gemessen, nicht
geschätzt — und werden nach dem Lesen ohnehin gelöscht.

Sie kommt entweder über `/api/contribute` an (das Werkzeug lädt selbst hoch) oder von
Hand:

```powershell
python server\import_contrib.py eingang\forza-contrib-kai-20260827.zip --dry-run
python server\import_contrib.py eingang\forza-contrib-kai-20260827.zip
```

Sie landet unter `data\memory_scans\contrib\kai\<lauf>\` und ist beim nächsten
Seitenaufbau in der Auswertung — als `by: kai` an jedem Scan sichtbar.

## Zwei Unterschriften, zwei Zwecke

**`signature.txt` sagt wer.** Jeder Beitragende hat ein eigenes Geheimnis. Es
zurückzuziehen betrifft nur ihn:

```powershell
python server\contrib_keys.py revoke kai
```

**`gate.txt` sagt ob überhaupt.** Ein 256-Zeichen-Passwort, das in jedem ausgegebenen
Paket steckt. Es neu zu erzeugen ist der Notschalter — danach ist **jedes verteilte
Paket ungültig** und muss neu gebaut werden. Per Knopf in der Verwaltung, oder:

```powershell
python server\contrib_keys.py gate --rotate
```

Beide werden **gerechnet, nie übertragen**. Über einfaches HTTP ist jeder Aufruf
mitlesbar; die Geheimnisse sind es nicht, und eine unterwegs veränderte Datei fällt
durch.

Geprüft wird in dieser Reihenfolge: erst das Tor, dann die Person. So bekommt jemand
mit einem veralteten Paket „hol dir das neue" zu hören und nicht „deine Unterschrift
ist falsch" — zwei sehr verschiedene Probleme.

## Verwaltung

**<http://127.0.0.1:8787/admin>** (oder über die LAN-Adresse). Das Admin-Geheimnis
steht in `config/contrib_keys.json`, angelegt mit `contrib_keys.py admin`.

Dort: alle Läufe mit Herkunft, Zeilenzahl und Endzustand; einzeln aus- und wieder
einblenden **mit Begründung**; das Zugangspasswort neu erzeugen.

**Ausgeblendet heißt ausgeblendet, auch im Download.** `/api/dataset` liefert
denselben Datensatz wie die Seite, und der entsteht ohne die ausgeblendeten Läufe.
Weil ein Neubau eine Viertelstunde dauert, meldet `/api/summary` ein `stale`, solange
die Ausblend-Liste jünger ist als der gebaute Datensatz — die Verwaltung sagt dir das,
statt dich glauben zu lassen, es sei schon durch.

**Gelöscht wird nie.** Was heute falsch aussieht, ist morgen vielleicht der einzige
Beleg dafür, was schiefging.

## Die Schnittstelle

| | |
| --- | --- |
| `GET /api/summary` | Version, Stand, Boards, Beitragende, `stale` |
| `GET /api/dataset` | der ganze Datensatz — was die App lädt |
| `POST /api/contribute` | eine signierte Abgabe |
| `GET /api/admin/runs` | alle Läufe mit Sichtbarkeit *(signiert)* |
| `POST /api/admin/visibility` | aus- oder einblenden *(signiert)* |
| `POST /api/admin/rotate-gate` | Notschalter *(signiert)* |

Admin-Aufrufe unterschreiben `METHODE\nPFAD\nZEITSTEMPEL\nSHA256(Rumpf)` und schicken
`X-Forza-Timestamp` und `X-Forza-Signature`. Der Zeitstempel darf höchstens fünf
Minuten abweichen, sonst ließe sich ein mitgeschnittener Aufruf später wiederholen.

## Was geprüft ist — und was nicht

```powershell
python scripts\test_contrib_pipeline.py   # Format, Ablehnungen, ganzer Rundweg
python scripts\test_analytics_api.py      # jede Antwort der Schnittstelle
python scripts\test_admin_crypto.py       # die JS-Krypto gegen Pythons hashlib
python scripts\test_sweep_smoke.py        # der Scanner, VM-Weg unverändert
```

**Nicht geprüft: ein echter Lauf gegen das Spiel.** Der lokale Weg
(`ocr_board_sweep --local`) ist gebaut und die Teile sind einzeln erprobt, aber
navigieren, filmen und das Standbild sind auf einem Rechner mit laufendem Forza noch
nie zusammen gelaufen. Der erste Lauf gehört beaufsichtigt.

## Warum kein VM-Zwang, und warum „VM selbst aufsetzen" nur halb geht

Der Normalweg ist **ohne VM**: das Werkzeug läuft auf dem Spielrechner. Ein Skript
kann Hyper-V einschalten, eine VM anlegen, GPU-PV durchreichen und die Werkzeuge
hineinkopieren — aber Windows installieren und lizenzieren, sich bei Steam oder Game
Pass anmelden und Forza laden kann es nicht. Das braucht Zugangsdaten und einen
Menschen. Wer die VM-Variante will, macht diesen Teil von Hand.

## Was der Rechner des Freundes während eines Laufs macht

Er gehört dem Scanner. Pfeiltasten und Bildschirmaufnahme gehen an das Fenster im
Vordergrund; wer nebenher etwas anklickt, schickt die Tasten dorthin und der Lauf
liest eine Liste, die stillsteht. Rund 20–25 Minuten je Board.

Ein Unterschied zur VM, der zu unseren Gunsten ausfällt: dort müssen Tastendruck und
Aufnahme als geplante Aufgaben starten, weil Eingaben eine interaktive Sitzung
brauchen — und ein abgebrochener Lauf hinterlässt dann einen weiterdrückenden Dienst,
der jede spätere Navigation kaputtmacht. Lokal ist die Sitzung schon interaktiv, die
Aufgaben entfallen, und diese Falle mit ihnen.

## Das Werkzeug selbst liegt hinter dem Passwort

`/download/tool` und `/api/package` verlangen seit dem 2026-09-09 dasselbe getippte
Zugangspasswort wie das Hochladen (`X-Forza-Upload-Password`), samt derselben Sperre
nach zehn Fehlversuchen je Maschine.

**Warum dasselbe und nicht ein zweites:** das Werkzeug fernsteuert ein Spiel und lädt
am Ende in diesen Bestand hoch. Wer es sinnvoll benutzen kann, braucht das Passwort
ohnehin — ein zweites Geheimnis wäre bloß ein zweites, das veraltet, und es ließe
sich nicht zusammen mit dem ersten wechseln (`POST /api/admin/rotate-gate`).

**Warum überhaupt:** frei zu haben war es, solange nichts darauf zeigte. Seit die
Startseite darauf verlinkte, war „unverlinkt" kein Schutz mehr.

Der Knopf steht auf `/mitmachen`, mit einem Passwortfeld daneben: ein `<a href>` kann
keine Kopfzeile setzen, also holt die Seite die Datei selbst und bietet sie als
Download an — und der Browser bekommt nur bei 200 eine ZIP zu sehen statt einer
gespeicherten Fehlermeldung. Die App dagegen ist bewusst offen, siehe
`docs/haptics_package.md`.
