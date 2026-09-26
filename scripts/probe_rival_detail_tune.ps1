<#
.SYNOPSIS
    Die entscheidende Messung: traegt der AUSGEWAEHLTE Rivale ein Tune?

.DESCRIPTION
    Stand der Untersuchung (siehe docs/tune_investigation.md):

      Frage 1  Fuehrt die Bestenlistenzeile die Tune-Id?   NEIN, 311 Zeilen, null Treffer.
      Frage 3a Liegt ScoreboardScoreData im Speicher,
               waehrend der Sweep nur BLAETTERT?           NEIN (2026-08-30 gemessen:
               vier Treffer, alle Fehltreffer -- dieselbe car_id 18350, dieselben
               acht Fuehrungsbytes im "Livery"-Feld, PI am unteren Rand).

    Bleibt Frage 3b, und das ist die letzte offene: fuellt das Spiel die Struktur,
    wenn ein Rivale wirklich AUSGEWAEHLT ist? Auf der Bestenliste waehlt ENTER die
    markierte Zeile; das Spiel geht dann auf den Klassenschirm zurueck und zeigt dort
    "Details -- Name / Time to Beat / Vehicle Used". Erst in diesem Moment hat es die
    Daten des Rivalen geholt.

    Faellt die Antwort JA aus, gibt es einen Weg zum Tuner -- einen Rivalen je
    Menueaufruf, also nur fuer die Spitze eines Boards bezahlbar, aber es gibt ihn.
    Faellt sie NEIN aus, ist das Thema endgueltig zu, und das gehoert dann genauso
    aufgeschrieben.

.NOTES
    BRAUCHT DAS SPIEL EXKLUSIV. Es drueckt eine Taste, also darf kein Sweep laufen.
    Der Ablauf laesst das Spiel auf dem Klassenschirm stehen -- ein neu gestarteter
    Sweep navigiert ohnehin von vorn.

    Voraussetzung: eine offene Bestenliste. Genau dort steht das Spiel, wenn ein
    Sweep nach einem fertigen Board angehalten wurde.

.EXAMPLE
    powershell -File scripts/probe_rival_detail_tune.ps1
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $OutDir = "data/runtime/tune_probe_detail",
    [int] $SettleSeconds = 6
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir }
           else { Join-Path $workspace $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

function Say { param([string] $Message) Write-Host "[detail-tune] $Message" }

$scripts = Join-Path $workspace "scripts"

Say "1/5  Schirm VOR dem Tastendruck festhalten"
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $scripts "capture_vm_current_screen.ps1") `
    -VMName $VMName -VMUser $VMUser `
    -OutputDir (Join-Path $outPath "before") | Out-Null

Say "2/5  ENTER -- die markierte Bestenlistenzeile als Rivalen waehlen"
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $scripts "send_vm_forza_keys.ps1") `
    -VMName $VMName -VMUser $VMUser -Key ENTER -Count 1

Say "3/5  $SettleSeconds s warten -- das Spiel holt die Rivalendaten erst jetzt"
Start-Sleep -Seconds $SettleSeconds

Say "4/5  Schirm NACH dem Tastendruck festhalten"
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $scripts "capture_vm_current_screen.ps1") `
    -VMName $VMName -VMUser $VMUser `
    -OutputDir (Join-Path $outPath "after") | Out-Null

Say "5/5  Speicher lesen: ScoreboardScoreData UND Id/Share-Code-Paare"
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $scripts "probe_rival_tune.ps1") `
    -VMName $VMName -VMUser $VMUser `
    -OutDir (Join-Path $outPath "memory") -WithShareCodes

Say "fertig -- Bilder in $outPath\before und \after, Speicher in $outPath\memory"
Say "Die Frage lautet: steht in rival_score_data.json ein Satz MIT Tune-Id?"
