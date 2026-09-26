<#
.SYNOPSIS
    Keep the sweep running unattended, restarting it whenever it stops.

.DESCRIPTION
    `run_full_sweep.ps1` already recovers from a failed navigation and recycles the VM,
    but it can still exit -- an unkillable game, a guest that will not come back, a
    PowerShell Direct hiccup. Left alone that costs the rest of the night. This wraps it
    in a restart loop with `-Resume`, which is safe by design: finished boards are in the
    manifest and are skipped.

    Scanner choice, measured 2026-08-22 rather than assumed: the flow scanner reaches
    1,426 ranks/min on a board that is already served, but only 285/min with 80% of ranks
    missed when entering a board at rank 1 -- and a sweep opens nothing but fresh boards.
    The proven memory loop does 724/min gaplessly there, so it is both faster and
    complete for this job.

    The car catalogue refresh is off: it returned the same 270 ids on every board, and
    the lap-time join against OCR'd screen names replaced it.

.EXAMPLE
    .\scripts\run_overnight_sweep.ps1 -UntilHour 9
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string[]] $Categories = @("Road Racing"),
    [int[]] $RouteIndices = @(6..22),
    # Stop starting new attempts after this hour of the morning, so the machine is free
    # again when the operator comes back.
    [int] $UntilHour = 10,
    [int] $MaxAttempts = 40,
    [string] $LogRoot = "data/runtime/overnight"
)

$ErrorActionPreference = "Continue"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$logRoot = [IO.Path]::GetFullPath((Join-Path $workspace $LogRoot))
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$log = Join-Path $logRoot ("overnight_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))

function Write-Night {
    param([string] $Message)
    $line = "[overnight] $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $Message"
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
}

function Boards-Done {
    $manifest = Join-Path $workspace "data/memory_scans/full_sweep/full_sweep_manifest.json"
    if (-not (Test-Path -LiteralPath $manifest)) { return 0 }
    try {
        $data = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        return @($data.boards | Where-Object { $_.status -eq "complete" }).Count
    } catch { return -1 }
}

Write-Night "start; $(Boards-Done) board(s) already complete"

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $now = Get-Date
    if ($now.Hour -ge $UntilHour -and $now.Hour -lt 20) {
        Write-Night "past $UntilHour:00 -- stopping so the machine is free"
        break
    }

    # The VM may be off (the operator shut it down) or wedged from a previous attempt.
    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if (-not $vm) { Write-Night "VM $VMName not found; nothing to do"; break }
    if ($vm.State -ne "Running") {
        Write-Night "VM is $($vm.State); preparing it"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot "start_forza_vm_ready.ps1") -VMName $VMName |
            Out-Null
        Start-Sleep -Seconds 20
    }

    Write-Night "attempt $attempt starting the sweep"
    $before = Boards-Done
    & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot "run_full_sweep.ps1") `
        -VMName $VMName -Categories $Categories -RouteIndices $RouteIndices `
        -Resume -WarmStart -SkipEnumeration -NoCarCatalogue 2>&1 |
        ForEach-Object { Add-Content -LiteralPath $log -Value $_ -Encoding UTF8 }
    $after = Boards-Done
    Write-Night "attempt $attempt ended (exit $LASTEXITCODE); complete boards $before -> $after"

    if ($after -eq $before) {
        # No progress at all usually means the guest is in the state a reboot fixes, and
        # hammering it immediately just burns the night on identical failures.
        Write-Night "no board completed; giving the guest a minute before retrying"
        Start-Sleep -Seconds 60
    }
}

Write-Night "finished; $(Boards-Done) board(s) complete. Log: $log"
