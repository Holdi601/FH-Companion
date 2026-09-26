<#
.SYNOPSIS
    Der Test, der noch fehlt: liegt das Tune im Speicher, waehrend der GEIST FAEHRT?

.DESCRIPTION
    Bis hierher war alles im MENUE gemessen (docs/tune_investigation.md): die
    Bestenlistenzeile fuehrt kein Tune, die Rivalenkarte zeigt keins, und beim
    Blaettern liegt die Rivalenstruktur nicht im Speicher.

    Der Einwand des Nutzers dagegen ist gut: **im Rennen faehrt der Geist des
    Rivalen.** Damit das Spiel dieses Auto ueberhaupt darstellen kann, muss es
    dessen Aufbau geladen haben -- Karosserie, Raeder, Fluegel kommen aus den
    Ausbaustufen, und die haengen am Tune. Im Menue muss davon nichts da sein, im
    Rennen schon.

    Das ist eine andere Frage als die bisherigen, und sie ist ungetestet.

.NOTES
    EHRLICH ZUR ERWARTUNG: es kann sein, dass das Spiel nur den fertigen AUFBAU
    laedt (die Liste der Ausbaustufen) und nicht die Herkunft -- also kein
    Share-Code und kein Erstellername. Der Aufbau allein macht noch keine
    Tuner-Wertung. Gefunden waere dann trotzdem etwas, nur nicht das Gesuchte.

    BRAUCHT DAS SPIEL EXKLUSIV, und mehr als jede bisherige Messung: es startet ein
    Rennen. Kein Sweep darf laufen.

    GESUCHT WIRD OHNE OFFSET-ANNAHME. Anker ist der Gamertag des Rivalen, wie er auf
    dem Schirm steht -- find_bytes_around.py zeigt, was drumherum liegt. Genau das
    unterscheidet diesen Lauf von den vorherigen: findet er nichts, heisst das
    wirklich "da ist nichts", und nicht "meine Offsets waren falsch".

.PARAMETER RivalTag
    Der Gamertag des ausgewaehlten Rivalen. Steht nach dem ENTER auf der
    Rivalenkarte unter "Name" -- das Skript liest ihn selbst aus dem Bild, wenn
    nichts angegeben ist.

.EXAMPLE
    powershell -File scripts/probe_rival_race_tune.ps1

.EXAMPLE
    powershell -File scripts/probe_rival_race_tune.ps1 -RivalTag "SkokuNaFide" -RaceLoadSeconds 120
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $OutDir = "data/runtime/tune_probe_race",
    [string] $RivalTag = "",
    [int] $CardSettleSeconds = 6,
    [int] $RaceLoadSeconds = 100,
    [switch] $SkipSelect,
    [switch] $NoRecover
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$scripts = Join-Path $workspace "scripts"
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir }
           else { Join-Path $workspace $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

function Say { param([string] $Message) Write-Host "[race-tune] $Message" }

function Get-Screen {
    param([string] $Label)
    $dir = Join-Path $outPath $Label
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $scripts "capture_vm_current_screen.ps1") `
            -VMName $VMName -VMUser $VMUser -OutputDir $dir | Out-Null
    } catch {
        # Ein misslungenes Bild darf die Messung nicht beenden -- der Speicherlauf
        # ist der Kern, das Bild ist der Beleg. Am 2026-08-30 scheiterte genau eine
        # von zwei Aufnahmen (Fehler 267009) und haette sonst alles mitgerissen.
        Say "  Bild '$Label' fehlgeschlagen: $($_.Exception.Message)"
        return ""
    }
    $text = Join-Path $dir "current.ocr.txt"
    if (Test-Path $text) { return (Get-Content -Raw -LiteralPath $text) }
    return ""
}

function Send-Key {
    param([string] $Key, [int] $Count = 1)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $scripts "send_vm_forza_keys.ps1") `
        -VMName $VMName -VMUser $VMUser -Key $Key -Count $Count
}

# --- 1. Rivalen waehlen -------------------------------------------------------
if (-not $SkipSelect) {
    Say "1  Bestenliste: ENTER waehlt die markierte Zeile als Rivalen"
    Send-Key -Key ENTER
    Start-Sleep -Seconds $CardSettleSeconds
}
$card = Get-Screen -Label "01_card"

# Den Gamertag von der Rivalenkarte lesen: die Zeile NACH "Name".
if (-not $RivalTag -and $card) {
    $lines = $card -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    for ($i = 0; $i -lt $lines.Count - 1; $i += 1) {
        if ($lines[$i] -match '^Name$') { $RivalTag = $lines[$i + 1]; break }
    }
}
if ($RivalTag) { Say "   Rivale: '$RivalTag'" }
else { Say "   WARNUNG: keinen Gamertag gelesen -- der Anker fehlt, es bleiben die Offset-Suchen" }

# --- 2. Rennen starten --------------------------------------------------------
Say "2  ENTER startet das Rennen; $RaceLoadSeconds s Ladezeit"
Send-Key -Key ENTER
Start-Sleep -Seconds $RaceLoadSeconds
$inRace = Get-Screen -Label "02_race"
if ($inRace) {
    $head = ($inRace -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) -join " / "
    Say "   Schirm im Rennen: $head"
}

# --- 3. Speicher lesen, waehrend der Geist faehrt ------------------------------
Say "3  Speicher lesen (rein lesend, waehrend das Rennen laeuft)"
$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop
try {
    $state = Invoke-Command -Session $session -ScriptBlock {
        $process = Get-Process forzahorizon6 -ErrorAction SilentlyContinue
        $python = @("C:\ForzaTools\Python312\python.exe",
                    "C:\Users\admin\AppData\Local\Programs\Python\Python313\python.exe",
                    "C:\Users\admin\AppData\Local\Programs\Python\Python312\python.exe") |
                  Where-Object { Test-Path $_ } | Select-Object -First 1
        [pscustomobject]@{ Pid = if ($process) { $process.Id } else { 0 }; Python = $python }
    }
    if (-not $state.Pid) { throw "forzahorizon6 laeuft nicht." }
    Say "   Spiel pid $($state.Pid)"

    foreach ($name in @("find_bytes_around.py", "find_rival_score_data.py",
                        "find_tune_share_codes.py", "find_tune_data.py")) {
        Copy-Item -ToSession $session -Force `
            -LiteralPath (Join-Path $scripts $name) `
            -Destination (Join-Path $GuestWorkspace "scripts\$name")
    }

    # NUR die Fassung TuningData (Voreinstellung), nicht "--layout all": fuer
    # TuneData und VersionedTuneData gibt es keinen billigen Anker, die werden an
    # jeder 4-Byte-Grenze geprueft und kaemen im Zeitfenster eines Rennens nie durch.
    # Ein Rivalen-Tune waere ohnehin ein TuningItem, und darin sitzt TuningData.
    #
    # find_tune_data.py steht ABSICHTLICH zuerst: es ist der einzige Lauf, der den
    # kompletten Aufbau liefern kann (Teileliste + Einstellungen + Ersteller), und
    # waehrend eines Rennens ist der Speicher am ehesten so, wie er sein soll.
    # Die anderen drei sind Absicherung, falls dieser nichts findet.
    $runs = @(
        @{ Label = "tune_data";          Script = "find_tune_data.py";
           Args = @("--max-hits", "60", "--max-seconds", "240") },
        @{ Label = "bytes_around_rival"; Script = "find_bytes_around.py";
           Args = @("--text", $RivalTag, "--max-hits", "25") },
        @{ Label = "rival_score_data";   Script = "find_rival_score_data.py"; Args = @() },
        @{ Label = "share_codes";        Script = "find_tune_share_codes.py"; Args = @() }
    )
    foreach ($run in $runs) {
        if ($run.Label -eq "bytes_around_rival" -and -not $RivalTag) {
            Say "   '$($run.Label)' uebersprungen -- kein Gamertag"
            continue
        }
        Say "   $($run.Label)"
        $result = Invoke-Command -Session $session `
            -ArgumentList $GuestWorkspace, $state.Python, $state.Pid, $run.Script, $run.Args, $run.Label `
            -ScriptBlock {
                param($Workspace, $Python, $GamePid, $Script, $ExtraArgs, $Label)
                $json = Join-Path $Workspace "$Label.json"
                Remove-Item $json -ErrorAction SilentlyContinue
                $argv = @((Join-Path $Workspace "scripts\$Script"), "--out", $json)
                if ($Script -ne "find_tune_share_codes.py") { $argv += @("--pid", "$GamePid") }
                $argv += $ExtraArgs
                $text = & $Python @argv 2>&1 | Out-String
                [pscustomobject]@{
                    Console = $text
                    Json = if (Test-Path $json) { Get-Content -Raw -LiteralPath $json } else { "" }
                }
            }
        if ($result.Console) { Write-Host $result.Console }
        else { Say "     WARNUNG: keine Ausgabe -- der Lauf ist gescheitert, nicht leer ausgegangen" }
        if ($result.Json) {
            $target = Join-Path $outPath "$($run.Label).json"
            Set-Content -LiteralPath $target -Value $result.Json -Encoding utf8
            Say "     -> $target"
        } else {
            Say "     WARNUNG: keine JSON zurueckbekommen"
        }
    }
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}

# --- 4. Aus dem Rennen zurueck ------------------------------------------------
# Bewusst zurueckhaltend: ESC oeffnet die Pause, danach wird NUR geschaut. Welcher
# Eintrag "Rennen verlassen" heisst, hat noch nie jemand aufgenommen -- blind durch
# ein unbekanntes Menue zu druecken ist genau die Sorte Rateversuch, die diesem
# Projekt schon Stunden gekostet hat. Der Sweep-Navigator kommt aus der offenen Welt
# von selbst zurueck; er bekommt hier nur einen sauberen Ausgangspunkt.
if (-not $NoRecover) {
    Say "4  ESC -- Pause oeffnen und ansehen (es wird NICHT blind weitergedrueckt)"
    Send-Key -Key ESC
    Start-Sleep -Seconds 3
    $pause = Get-Screen -Label "03_pause"
    if ($pause) {
        $head = ($pause -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 10) -join " / "
        Say "   Pausenschirm: $head"
    }
}

Say "fertig -- alles in $outPath"
Say "Die Frage: steht in tune_data.json ein Satz mit einer PLAUSIBLEN Teileliste"
Say "und Einstellungen? Dann laesst sich der Wagen nachbauen -- mit oder ohne Code."
Say ""
Say "UND BEIM AUSWERTEN: der Ersteller ist NICHT der Fahrer. Ein Fahrer benutzt in"
Say "aller Regel ein fremdes Tune. Der Gamertag der Bestenlistenzeile gehoert zum"
Say "Fahrer, 'creator_gamertag_candidates' zum Tuner. Nie gleichsetzen."
