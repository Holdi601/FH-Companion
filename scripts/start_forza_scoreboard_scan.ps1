<#
.SYNOPSIS
    Run the layout-discovering scoreboard scanner against the live game in the VM
    and bring the decoded rows back to the host.

.DESCRIPTION
    Uses scripts/forza_scoreboard_scan.py, which finds ScoreboardScoreData blocks
    by structure and derives the field offsets from the data, rather than
    capture_scoreboard_memory.py's constants from build 6.382.893.0. The game
    force-updated to 6.420.696.0, so hard-coded offsets cannot be trusted, and
    when they are wrong the old scanner reports "no row block found" instead of
    saying the layout moved.

    Read-only: the guest scanner opens the process with PROCESS_VM_READ and
    injects nothing.

    Requires a Rivals leaderboard to be loaded, because rows only exist in memory
    after the game has fetched them.

.EXAMPLE
    .\scripts\start_forza_scoreboard_scan.ps1 -Discover
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $GuestPython = "C:\ForzaTools\Python312\python.exe",
    [string] $HostOutputRoot = "data/memory_scans",
    [string] $RunId = "",
    [int] $MinimumRecords = 8,
    [switch] $Discover,
    [switch] $NoCopy
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Scan {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-scan] $Message"
}

if (-not $RunId) {
    $RunId = "scan_{0}" -f (Get-Date -Format "yyyyMMdd_HHmmss")
}
$hostRoot = if ([IO.Path]::IsPathRooted($HostOutputRoot)) {
    $HostOutputRoot
} else {
    [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
}
$hostRunRoot = Join-Path $hostRoot $RunId
New-Item -ItemType Directory -Force -Path $hostRunRoot | Out-Null

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$guestScripts = Join-Path $GuestWorkspace "scripts"
$guestRunRoot = "$GuestWorkspace\data\scans\$RunId"

try {
    if (-not $NoCopy) {
        Invoke-Command -Session $session -ArgumentList $guestScripts -ScriptBlock {
            param($Scripts); New-Item -ItemType Directory -Force -Path $Scripts | Out-Null
        }
        foreach ($name in @("forza_scoreboard_scan.py", "forza_scoreboard_layout.py")) {
            Copy-Item -ToSession $session `
                -LiteralPath (Join-Path $workspace "scripts\$name") `
                -Destination $guestScripts -Force
        }
    }

    Write-Scan "scanning the live process in the VM"
    $result = Invoke-Command -Session $session `
        -ArgumentList $GuestPython, $guestScripts, $guestRunRoot, $MinimumRecords, $Discover.IsPresent `
        -ScriptBlock {
            param($Python, $Scripts, $RunRoot, $MinimumRecords, $DoDiscover)
            New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null
            $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                Sort-Object StartTime -Descending | Select-Object -First 1
            if (-not $process) {
                return [pscustomobject]@{ ExitCode = 90; Output = "forzahorizon6 is not running"; }
            }
            $rows = Join-Path $RunRoot "rows.json"
            $profile = Join-Path $RunRoot "build_profile.json"
            $arguments = @(
                (Join-Path $Scripts "forza_scoreboard_scan.py"),
                "--pid", [string]$process.Id,
                "--minimum-records", [string]$MinimumRecords,
                "--output", $rows,
                "--profile", $profile
            )
            if ($DoDiscover) { $arguments += "--discover" }
            $stdout = & $Python @arguments 2>&1
            [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Output = ($stdout | Out-String)
                Rows = $rows
                Profile = $profile
                ProcessId = $process.Id
            }
        }

    Write-Host $result.Output
    if ($result.ExitCode -eq 90) {
        throw "forzahorizon6 is not running in $VMName."
    }

    foreach ($name in @("rows.json", "build_profile.json")) {
        $guestPath = "$guestRunRoot\$name"
        $exists = Invoke-Command -Session $session -ArgumentList $guestPath -ScriptBlock {
            param($Path); Test-Path -LiteralPath $Path
        }
        if ($exists) {
            Copy-Item -FromSession $session -LiteralPath $guestPath -Destination $hostRunRoot -Force
        }
    }

    $rowsPath = Join-Path $hostRunRoot "rows.json"
    if (Test-Path -LiteralPath $rowsPath) {
        $report = Get-Content -LiteralPath $rowsPath -Raw | ConvertFrom-Json
        Write-Scan "rows=$($report.row_count) ranks=$($report.minimum_rank)-$($report.maximum_rank) blocks=$($report.blocks.Count)"
        if ($report.layout) {
            Write-Scan "stride=$($report.layout.stride_hex) matchesJuneBuild=$($report.layout.matches_build_6_382_893_0)"
            if ($report.layout.notes) {
                foreach ($note in @($report.layout.notes)) { Write-Scan "layout note: $note" }
            }
        }
        Write-Scan "output: $rowsPath"
    }
    if ($result.ExitCode -ne 0) {
        Write-Scan "scanner exit code $($result.ExitCode) (2 means no rows were found)"
        exit $result.ExitCode
    }
    exit 0
} finally {
    if ($session) { Remove-PSSession $session }
}
