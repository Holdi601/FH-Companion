<#
.SYNOPSIS
    Refresh config/fh6_car_catalogue.json (carId -> car codename) from the live game.

.DESCRIPTION
    One command, no operator: copies the read-only dumper into the guest, runs it
    against the running Forza process, and merges the result into the host-side
    catalogue. The carId -> codename mapping lives in an in-memory array indexed
    by carId (stride 0x20, null-padded ASCII codenames like LOT_00_ExigeWTA_18);
    see docs/car_name_catalogue_research.md.

    The in-memory array holds the cars loaded for the CURRENT context, so a single
    run covers the boards' current car set, not all 662. Because the dumper merges
    into the existing catalogue, running this after scanning different boards --
    or on a weekly schedule as new cars ship -- accumulates coverage and picks up
    new carIds automatically.

    Forza must be running (any screen with cars loaded; a Rivals leaderboard is
    ideal). Read-only: the guest dumper opens the process with PROCESS_VM_READ and
    injects nothing.

.EXAMPLE
    .\scripts\update_car_catalogue.ps1
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $GuestPython = "C:\ForzaTools\Python312\python.exe",
    [string] $Output = "config/fh6_car_catalogue.json"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $workspace $Output }

function Write-Cat { param([string] $Message) Write-Host "[car-catalogue] $Message" }

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
try {
    $guestScripts = Join-Path $GuestWorkspace "scripts"
    $guestCatalogue = "$GuestWorkspace\data\car_catalogue.json"
    Invoke-Command -Session $session -ArgumentList $guestScripts -ScriptBlock {
        param($Scripts); New-Item -ItemType Directory -Force -Path $Scripts | Out-Null
    }
    foreach ($name in @("dump_car_catalogue.py")) {
        Copy-Item -ToSession $session -LiteralPath (Join-Path $workspace "scripts\$name") `
            -Destination $guestScripts -Force
    }

    # Seed the guest with the current host catalogue so the merge is cumulative
    # across machines/runs rather than starting empty each time.
    if (Test-Path -LiteralPath $outputPath) {
        Invoke-Command -Session $session -ArgumentList (Split-Path -Parent $guestCatalogue) -ScriptBlock {
            param($Dir); New-Item -ItemType Directory -Force -Path $Dir | Out-Null
        }
        Copy-Item -ToSession $session -LiteralPath $outputPath -Destination $guestCatalogue -Force
    }

    Write-Cat "dumping the car catalogue from the live process"
    $result = Invoke-Command -Session $session -ArgumentList $GuestPython, $guestScripts, $guestCatalogue -ScriptBlock {
        param($Python, $Scripts, $Catalogue)
        $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending | Select-Object -First 1
        if (-not $process) { return [pscustomobject]@{ ExitCode = 90; Output = "forzahorizon6 is not running" } }
        $stdout = & $Python (Join-Path $Scripts "dump_car_catalogue.py") `
            --pid $process.Id --output $Catalogue 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($stdout | Out-String) }
    }
    Write-Host $result.Output
    if ($result.ExitCode -eq 90) { throw "forzahorizon6 is not running in $VMName; start the game first." }
    if ($result.ExitCode -ne 0) { throw "dump_car_catalogue.py failed with exit code $($result.ExitCode)." }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
    Copy-Item -FromSession $session -LiteralPath $guestCatalogue -Destination $outputPath -Force
    $catalogue = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    Write-Cat "catalogue now holds $($catalogue.count) car(s): $outputPath"
    exit 0
} finally {
    if ($session) { Remove-PSSession $session }
}
