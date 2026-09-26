# Windows Defender meldet `Trojan:Win32/Bearfoos.A!ml`

Gemeldet am 2026-09-15, zuerst von einem fremden Nutzer, wenige Stunden später auch
auf dem Baurechner selbst: die EXE wurde beim Download sofort gelöscht.

**Die Ursache ist gefunden und behoben.** Es war nicht die fehlende Signatur, nicht
das Speicherlesen und nicht die Selbstaktualisierung — es war der **Markenname
„Forza" in den Herstellerangaben einer unsignierten Datei**. Und er stand dort, weil
*ich* ihn am selben Tag hineingeschrieben hatte, um Defender zu besänftigen.

## Der Versuch, der es zeigt

Vier Bauten, die sich **nur** in der Versionsressource unterscheiden. Die Datei mit
dem eigentlichen Programmcode (`Forza Grip Haptics.dll`, 698 KB) war in allen Fällen
sauber; erkannt wurde ausschließlich der 220-KB-Startstub, den `dotnet publish`
erzeugt.

| Variante | `Company` / `Product` in der EXE | Defender |
|---|---|---|
| A | `Forza Grip Haptics` | **`Trojan:Win32/Bearfoos.A!ml`** |
| B | *gar keine Versionsressource* | sauber |
| C | ein Personenname / `Grip Haptics` | **sauber** |
| D | nur `<Version>`, sonst keine Felder | sauber |

Unterschied zwischen A und B: **512 Bytes**. Zwischen A und C: nur der Name in zwei
Feldern.

Gegenprobe zur Datierung: die Pakete vom 13. und 14. September — gebaut, bevor die
Versionsressource dazukam — sind sauber. Das vom 15. war es nicht.

## Warum das plausibel ist

Eine ausführbare Datei, die sich als Hersteller mit einem geschützten Produktnamen
ausweist und **keine Signatur trägt, die das belegt**, ist das Muster einer
Nachahmung. Genau darauf sind diese Klassifikatoren trainiert — und dass die Angaben
frei beschreibbar sind, ist ja der Grund, warum Windows für „Herausgeber"
ausschließlich die Signatur heranzieht.

Der **Dateiname** `Forza Grip Haptics.exe`, der Fenstertitel und der Text in der
Oberfläche sind unbedenklich: Variante C trägt sie alle und ist sauber. Nur die
Felder der Versionsressource nicht.

## Was jetzt drinsteht

Seit der Umbenennung (2026-09-26) steht dort `FH Companion` -- ohne den Markennamen,
also nach dieser Messung unbedenklich; nachgeprueft wird das mit demselben Scan.

```
CompanyName     : FH Companion
ProductName     : FH Companion
FileDescription : FH Companion
FileVersion     : 2026.9.15.0
LegalCopyright  : Free to use. No warranty.
```

Damit ist die Datei **nicht mehr anonym** — der ursprüngliche Zweck der Übung — und
gleichzeitig sauber. Sie ist obendrein ehrlicher: ein inoffizielles Werkzeug sollte
sich nicht als „Forza" ausgeben.

Belegt **nach Entfernen der Testausnahme**, mit vollem Echtzeitschutz:

```
Scanning ...\dist\forza-grip-haptics\app\Forza Grip Haptics.exe   found no threats.
```

Und vom Server geladen, wie ein Nutzer es tut:

```
689c65b10cae4b2aa9e3903447c5c06118063837a8c88e8f6f0bcac710156884
forza-grip-haptics-20260915.zip   (Bau acdc71c28e2eea58, 64,7 MB)   found no threats.
```

## Die zweite Änderung vom selben Tag

**Das Austauschfenster ist sichtbar** (`Rivals/AppUpdate.cs`, `CreateNoWindow = false`).
Eine versteckte Konsole, die auf das Ende ihres Elternprozesses wartet und dann
dessen Programmordner überschreibt, ist das auffälligste Einzelverhalten der ganzen
App. Ob sie zum Fehlalarm beigetragen hat, ist **nicht gemessen** — der Versuch oben
hat nur die Versionsressource variiert. Sichtbar bleibt sie trotzdem: wer auf Update
drückt, darf sehen, was ersetzt wird.

## Was die App sonst tut, das verdächtig aussieht

Falls der Alarm wiederkommt, ist das die Liste, an der man ansetzt — und die
Begründung, die in eine Fehlalarm-Meldung gehört:

| Was die App tut | Wofür | Wie es von außen aussieht |
|---|---|---|
| `OpenProcess`, `VirtualQueryEx`, `ReadProcessMemory` auf `ForzaHorizon6.exe` | Tuning-Inspektor: die Garagen-Datenbank aus dem Heap holen | Speicher fremder Prozesse lesen — Cheat oder Passwortdieb |
| ZIP laden, entpacken, eigenen Ordner überschreiben, neu starten | Selbstaktualisierung | Dropper |
| `GetAsyncKeyState` | Hotkeys (F10 = eigene Start-Ziel-Linie) | globale Tastenabfrage |
| `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | das Overlay soll sich bei OCR nicht selbst fotografieren | Tarnung vor Bildschirmaufnahme |
| `MachineGuid` lesen, gehasht mitsenden | Missbrauchsschutz der Rundeneinreichung (derzeit abgeschaltet) | Rechner-Kennung übertragen |
| keine Signatur | — | „Unbekannter Herausgeber" |

## Wenn es wiederkommt

### 1. Erst messen, dann glauben

Der Versuch oben ist billig und dauert zehn Minuten. Ausnahme für die beiden
Bauordner setzen, Varianten bauen, **außerhalb der Ausnahme** scannen — ein Scan im
ausgenommenen Ordner prüft nichts und meldet fröhlich „sauber".

```powershell
# erhöht:
Add-MpPreference -ExclusionPath '<repo>\dist'
Add-MpPreference -ExclusionPath '<repo>\haptics'
# ... bauen, Datei anderswohin kopieren, dort scannen ...
Remove-MpPreference -ExclusionPath '<repo>\dist'
Remove-MpPreference -ExclusionPath '<repo>\haptics'
```

Nachsehen, was überhaupt erkannt wurde:

```powershell
Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending |
  Select-Object -First 5 InitialDetectionTime, ThreatID, Resources
```

### 2. Den Fehlalarm bei Microsoft melden

<https://www.microsoft.com/en-us/wdsi/filesubmission> — als *Software developer*,
Datei hochladen, als Fehlalarm markieren, die Tabelle oben als Begründung mitgeben.
Antwort üblicherweise in ein bis drei Tagen; danach gilt es bei **jedem** Defender.
Braucht einen angemeldeten Browser, ist also nicht automatisierbar.

### 3. Signieren

Nimmt der Datei die Anonymität und ist der einzige Weg, „Unbekannter Herausgeber"
loszuwerden. Möglichkeiten und Preise stehen in **`code-signing.md`**;
`scripts/sign_app.py` ist eingerichtet und braucht nur noch ein Zertifikat.

## Was ein betroffener Nutzer sofort tun kann

Die alte, erkannte Fassung liegt in Quarantäne. **Der einfachste Weg ist, sie
wegzuwerfen und neu zu laden** — was auf dem Server liegt, ist die saubere Fassung.

Ist die Windows-Sicherheit-Oberfläche schwarz und unbedienbar (kommt vor), geht es
auch ohne sie, in einer Konsole **als Administrator**:

```powershell
# Was liegt in Quarantäne?
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Restore -ListAll

# Die Oberfläche selbst reparieren:
Get-AppxPackage Microsoft.SecHealthUI -AllUsers | Reset-AppxPackage
Get-Service SecurityHealthService | Restart-Service
```

## Was nicht hilft

- **Umbenennen, packen, obfuskieren.** Verschleierung ist selbst ein Merkmal.
- **Ein selbstsigniertes Zertifikat**, solange es nicht auf dem Zielrechner als
  vertrauenswürdig eingetragen ist (siehe `code-signing.md`).
- **`PublishSingleFile`.** Wird bewusst *nicht* verwendet: eine Datei, die sich beim
  Start nach `%TEMP%` auspackt, ist ein weiteres Verdachtsmerkmal.
- **Mehr Angaben in die Versionsressource schreiben.** Genau das war der Fehler.
