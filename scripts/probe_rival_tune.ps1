<#
.SYNOPSIS
    Frage 3 der Tune-Untersuchung: traegt der RIVALEN-Datensatz das Tune?

.DESCRIPTION
    Die Bestenlistenzeile fuehrt kein Tune -- das ist an 311 Zeilen gemessen und
    abgeschlossen (siehe docs/tune_investigation.md). Uebrig blieb genau eine
    Struktur: `ScoreboardScoreData` (112 Byte), der Datensatz hinter dem
    AUSGEWAEHLTEN Rivalen, mit `versionedTuneId` bei +80 und `versionedLiveryId`
    bei +96.

    Dieses Skript beantwortet, ob die Struktur ueberhaupt im Speicher liegt und ob
    +80 gefuellt ist. Davon haengt alles Weitere ab:

      * gefuellt, waehrend der Sweep nur BLAETTERT  -> die Tune-Id laesst sich im
        grossen Stil ernten, das Spiel legt sie beim Markieren einer Zeile an;
      * gefuellt erst im Rivalen-Detailschirm       -> ein Menueaufruf je Runde,
        also nur fuer die Spitze eines Boards bezahlbar;
      * nie gefuellt                                 -> das Thema ist zu Ende.

    Es DRUECKT KEINE TASTE und navigiert nicht. Es liest den Speicher von
    forzahorizon6.exe und darf deshalb neben einem laufenden Sweep laufen -- genau
    dafuer ist es gebaut, weil beide sich sonst um das eine Spiel streiten wuerden.

.EXAMPLE
    powershell -File scripts/probe_rival_tune.ps1

.EXAMPLE
    powershell -File scripts/probe_rival_tune.ps1 -OutDir data/runtime/tune_probe_detail
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $OutDir = "data/runtime/tune_probe_rival",
    [switch] $WithShareCodes
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir }
           else { Join-Path $workspace $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

function Say { param([string] $Message) Write-Host "[rival-tune] $Message" }

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

    $names = @("find_rival_score_data.py")
    if ($WithShareCodes) { $names += "find_tune_share_codes.py" }
    Invoke-Command -Session $session -ArgumentList $GuestWorkspace -ScriptBlock {
        param([string] $Workspace)
        New-Item -ItemType Directory -Force -Path (Join-Path $Workspace "scripts") | Out-Null
    }
    foreach ($name in $names) {
        Copy-Item -ToSession $session -Force `
            -LiteralPath (Join-Path $workspace "scripts\$name") `
            -Destination (Join-Path $GuestWorkspace "scripts\$name")
    }

    Say "ScoreboardScoreData suchen (nur lesen)"
    $result = Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $state.Python, $state.Pid -ScriptBlock {
        param([string] $Workspace, [string] $Python, [int] $GamePid)
        $json = Join-Path $Workspace "rival_score_data.json"
        Remove-Item $json -ErrorAction SilentlyContinue
        $text = & $Python (Join-Path $Workspace "scripts\find_rival_score_data.py") `
            --pid $GamePid --out $json 2>&1 | Out-String
        [pscustomobject]@{
            Console = $text
            Json = if (Test-Path $json) { Get-Content -Raw -LiteralPath $json } else { "" }
        }
    }
    Write-Host $result.Console
    if ($result.Json) {
        $target = Join-Path $outPath "rival_score_data.json"
        Set-Content -LiteralPath $target -Value $result.Json -Encoding utf8
        Say "Datensaetze -> $target"
    } else {
        Say "keine JSON zurueckbekommen -- die Konsolenausgabe oben sagt warum"
    }

    if ($WithShareCodes) {
        Say "Heap nach Id/Share-Code-Paaren durchsuchen"
        $codes = Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $state.Python -ScriptBlock {
            param([string] $Workspace, [string] $Python)
            $json = Join-Path $Workspace "tune_share_codes.json"
            Remove-Item $json -ErrorAction SilentlyContinue
            $text = & $Python (Join-Path $Workspace "scripts\find_tune_share_codes.py") `
                --out $json 2>&1 | Out-String
            [pscustomobject]@{
                Console = $text
                Json = if (Test-Path $json) { Get-Content -Raw -LiteralPath $json } else { "" }
            }
        }
        # Am 2026-08-30 lief dieser Schritt durch, ohne EINE Zeile zu hinterlassen:
        # keine Konsolenausgabe, keine JSON, keine Meldung. Ein Schritt, der still
        # nichts tut, sieht im Protokoll aus wie ein Schritt, der nichts fand -- und
        # das sind zwei sehr verschiedene Dinge. Also hier immer etwas sagen.
        if ($codes.Console) { Write-Host $codes.Console }
        else { Say "WARNUNG: der Share-Code-Lauf im Gast gab keine Zeile aus" }
        if ($codes.Json) {
            $target = Join-Path $outPath "share_codes.json"
            Set-Content -LiteralPath $target -Value $codes.Json -Encoding utf8
            Say "Share-Codes -> $target"
        } else {
            Say "WARNUNG: keine share_codes.json -- der Lauf ist gescheitert, nicht leer ausgegangen"
        }
    }
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}

Say "fertig -- Ordner $outPath"
