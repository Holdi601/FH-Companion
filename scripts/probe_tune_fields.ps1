<#
.SYNOPSIS
    Eine Messung: fuehrt die Bestenliste die Tune-Id, und liegen Share-Codes im Speicher?

.DESCRIPTION
    Zwei Fragen, die nur das laufende Spiel beantworten kann, und von denen die ganze
    Tuner-Auswertung abhaengt:

    1. Ist `versionedTuneId` in einer ScoreboardRow gefuellt? Die Typtabelle sagt, das
       Feld steht bei +372, und `tuneCreator` (Gamertag des Erstellers) bei +392. In
       dem einen rohen Abzug vom Juni waren beide in ALLEN elf Zeilen null. Ist das
       auch live so, kommt die Tune-Id nur ueber den Rivalen-Detailpfad -- ein
       Menueaufruf je Runde, fuer Massendaten also tot.

    2. Liegen im Heap Paare aus Id und Share-Code? Vier UGC-Satztypen halten beides
       nebeneinander, in jeder Fassung mit genau 40 Byte Abstand. Sobald das Spiel
       irgendeine Tune-Liste geladen hat, muessten sie da sein.

    Das Skript nimmt dem Spiel nichts weg: es liest nur, drueckt keine Taste und
    bewegt nichts. Es setzt voraus, dass Forza laeuft -- am besten mit einer
    geoeffneten Bestenliste, sonst sind gar keine Zeilen resident.

.EXAMPLE
    powershell -File scripts/probe_tune_fields.ps1

.EXAMPLE
    powershell -File scripts/probe_tune_fields.ps1 -OutDir data/runtime/tune_probe_2
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $OutDir = "data/runtime/tune_probe"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir }
           else { Join-Path $workspace $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

function Say { param([string] $Message) Write-Host "[tune-probe] $Message" }

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop
try {
    $state = Invoke-Command -Session $session -ScriptBlock {
        $process = Get-Process forzahorizon6 -ErrorAction SilentlyContinue
        $python = @("C:\ForzaTools\Python312\python.exe",
                    "C:\Users\admin\AppData\Local\Programs\Python\Python313\python.exe",
                    "C:\Users\admin\AppData\Local\Programs\Python\Python312\python.exe") |
                  Where-Object { Test-Path $_ } | Select-Object -First 1
        [pscustomobject]@{ Pid = if ($process) { $process.Id } else { 0 }
                           Python = $python }
    }
    if (-not $state.Pid) { throw "forzahorizon6 laeuft nicht -- ohne Spiel keine Messung." }
    if (-not $state.Python) { throw "Kein Python im Gast gefunden." }
    Say "Spiel pid $($state.Pid), Python $($state.Python)"

    Say "Skripte in den Gast kopieren"
    Invoke-Command -Session $session -ArgumentList $GuestWorkspace -ScriptBlock {
        param([string] $Workspace)
        New-Item -ItemType Directory -Force -Path (Join-Path $Workspace "scripts") | Out-Null
    }
    foreach ($name in @("capture_scoreboard_memory.py", "find_tune_share_codes.py")) {
        Copy-Item -ToSession $session -Force `
            -LiteralPath (Join-Path $workspace "scripts\$name") `
            -Destination (Join-Path $GuestWorkspace "scripts\$name")
    }

    # --- Frage 1: fuehrt die Zeile die Tune-Id? ---
    Say "Zeilen lesen (Frage 1: versionedTuneId)"
    $rows = Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $state.Python, $state.Pid -ScriptBlock {
        param([string] $Workspace, [string] $Python, [int] $GamePid)
        $json = Join-Path $Workspace "tune_probe_rows.json"
        Remove-Item $json -ErrorAction SilentlyContinue
        # --allow-missing-gamertag, weil eine gerade geblaetterte Seite Namen
        # nachlaedt und eine namenlose Zeile trotzdem eine vollstaendige Zeile ist.
        & $Python (Join-Path $Workspace "scripts\capture_scoreboard_memory.py") `
            --pid $GamePid --output-json $json --max-rows 400 `
            --allow-missing-gamertag 2>&1 | Out-String
        if (Test-Path $json) { Get-Content -Raw -LiteralPath $json } else { "" }
    }
    $rowsJson = Join-Path $outPath "rows.json"
    if ($rows -and $rows.Trim().StartsWith("{") -or $rows.Trim().StartsWith("[")) {
        Set-Content -LiteralPath $rowsJson -Value $rows -Encoding utf8
        Say "Zeilen -> $rowsJson"
    } else {
        Say "keine Zeilen-JSON zurueckbekommen; Ausgabe war:"
        Write-Host $rows
    }

    # --- Frage 2: liegen Id/Share-Code-Paare im Heap? ---
    Say "Heap nach Share-Codes durchsuchen (Frage 2)"
    $codes = Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $state.Python -ScriptBlock {
        param([string] $Workspace, [string] $Python)
        $json = Join-Path $Workspace "tune_share_codes.json"
        Remove-Item $json -ErrorAction SilentlyContinue
        & $Python (Join-Path $Workspace "scripts\find_tune_share_codes.py") `
            --out $json 2>&1 | Out-String
        [pscustomobject]@{
            Log = $LASTEXITCODE
            Text = if (Test-Path $json) { Get-Content -Raw -LiteralPath $json } else { "" }
        }
    }
    $codesJson = Join-Path $outPath "share_codes.json"
    if ($codes.Text) {
        Set-Content -LiteralPath $codesJson -Value $codes.Text -Encoding utf8
        Say "Share-Codes -> $codesJson"
    } else {
        Say "keine Share-Code-JSON zurueckbekommen"
    }
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}

Say "fertig -- Auswertung mit: python scripts/report_tune_probe.py $OutDir"
