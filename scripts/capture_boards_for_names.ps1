<#
.SYNOPSIS
    Capture a short frame run on each performance class, to name carIds.

.DESCRIPTION
    Car names come from the screen, and each class fields different cars -- so the way to
    turn 108 named carIds into most of the roster is a short capture per class rather
    than a long one anywhere. Two minutes of scrolling names dozens of ids, because the
    join needs only a lap time that appears in both the frames and a memory scan.

    Per class: navigate warm, run the presser and the frame capture as two separate
    guest tasks, then zip the frames and pull them to the host. Nothing here reads or
    parses -- that happens on the host afterwards, with the game shut down.

.EXAMPLE
    .\scripts\capture_boards_for_names.ps1 -Classes D,C,B,A -Seconds 100
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string[]] $Classes = @("D", "C", "B", "A", "S1", "S2", "R"),
    [string] $Track = "Highway Circuit",
    [int] $RouteIndex = 0,
    [string] $RivalsMode = "Road Racing",
    [int] $Seconds = 100,
    [int] $IntervalMs = 45,
    [string] $HostOutputRoot = "data/runtime/frames"
)

$ErrorActionPreference = "Continue"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Cap { param([string] $M) Write-Host "[capture-loop] $((Get-Date).ToString('HH:mm:ss')) $M" }

$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())

foreach ($class in $Classes) {
    Write-Cap "class $class -- navigating"
    # -FromLeaderboard is the cheap path and assumes the game is sitting on a board. It
    # is wrong whenever something ended mid-navigation: on 2026-08-23 an interrupted
    # sweep left the game in another category's route carousel -- 7 Sprint routes where
    # Road Racing has 23 -- and every class failed in 40 seconds. So a failure is
    # retried once with a full navigation, which finds its own way back.
    $nav = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "start_forza_navigation.ps1") `
        -VMName $VMName -RivalsMode $RivalsMode -PerformanceClass $class `
        -RouteIndex $RouteIndex -RouteAnchor $Track -RouteCount 23 -FromLeaderboard 2>&1
    if (-not ($nav -match "status: leaderboard_reached")) {
        Write-Cap "class $class -- warm entry failed; navigating from scratch"
        $nav = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "start_forza_navigation.ps1") `
            -VMName $VMName -RivalsMode $RivalsMode -PerformanceClass $class `
            -RouteIndex $RouteIndex -RouteAnchor $Track -RouteCount 23 2>&1
    }
    if (-not ($nav -match "status: leaderboard_reached")) {
        # A class with no personal time posted cannot be opened, and that is a fact
        # about the account, not a failure worth retrying further.
        Write-Cap "class $class could not be opened; skipping"
        continue
    }

    $session = New-PSSession -VMName $VMName -Credential $credential
    try {
        $stamp = "{0}_{1}" -f (Get-Date -Format "yyyyMMdd_HHmmss"), $class
        $result = Invoke-Command -Session $session -ArgumentList $stamp, $Seconds, $IntervalMs -ScriptBlock {
            param($Stamp, $Seconds, $IntervalMs)
            $dir = "C:\ForzaAutomation\data\frames\$Stamp"
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            foreach ($job in @(
                @{ n = "CapPress_$Stamp"
                   a = "-NoProfile -ExecutionPolicy Bypass -File `"C:\ForzaAutomation\scripts\press_down_continuously.ps1`" -Seconds $Seconds" },
                @{ n = "CapFrames_$Stamp"
                   a = "-NoProfile -ExecutionPolicy Bypass -File `"C:\ForzaAutomation\scripts\capture_leaderboard_frames.ps1`" -OutDir `"$dir`" -Seconds $Seconds -IntervalMs $IntervalMs -X 78 -Y 268 -Width 1700 -Height 610" })) {
                $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $job.a
                $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
                Register-ScheduledTask -TaskName $job.n -Action $action -Principal $principal -Force | Out-Null
                Start-ScheduledTask -TaskName $job.n
            }
            Start-Sleep -Seconds ($Seconds + 8)
            Get-ScheduledTask -TaskName "CapPress_$Stamp", "CapFrames_$Stamp" -ErrorAction SilentlyContinue |
                ForEach-Object {
                    Stop-ScheduledTask -TaskName $_.TaskName -ErrorAction SilentlyContinue
                    Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
                }
            $files = @(Get-ChildItem $dir -Filter *.png -ErrorAction SilentlyContinue)
            $zip = "C:\ForzaAutomation\data\frames\$Stamp.zip"
            if (Test-Path $zip) { Remove-Item $zip -Force }
            if ($files.Count -gt 0) {
                Compress-Archive -Path (Join-Path $dir '*.png') -DestinationPath $zip -CompressionLevel Fastest
            }
            [pscustomobject]@{ stamp = $Stamp; frames = $files.Count; zip = $zip }
        }

        if ($result.frames -gt 0) {
            $local = Join-Path $hostRoot "$($result.stamp).zip"
            Copy-Item -FromSession $session -LiteralPath $result.zip -Destination $local -Force
            Write-Cap "class $class -- $($result.frames) frames -> $local"
        } else {
            Write-Cap "class $class -- the capture produced nothing"
        }
    } finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }
}
Write-Cap "done; frames are in $hostRoot"
