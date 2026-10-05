<#
.SYNOPSIS
    Run forza_navigator.ps1 inside the VM as a single interactive task and follow
    its log from the host.

.DESCRIPTION
    The host's only jobs are to copy the navigator in, start it, and tail its log.
    All perception and input happen in one guest process, because sending keys via
    per-key host round trips puts 10-30 seconds between inputs, which the game's
    auto-hiding HUD and menu timeouts make unworkable.

.EXAMPLE
    .\scripts\start_forza_navigation.ps1 -Track "Soni Circuit" -PerformanceClass R

.EXAMPLE
    # Derive the menu map for a new build without a human describing the menus
    .\scripts\start_forza_navigation.ps1 -Explore
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $Track = "Soni Circuit",
    [string] $PerformanceClass = "R",
    [string] $RivalsMode = "Road Racing",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $HostOutputRoot = "data/runtime/navigation",
    [int] $TimeoutMinutes = 20,
    [int] $LaunchTimeoutSeconds = 240,
    [switch] $FreshStart,
    [switch] $Explore,
    [switch] $KeepFrames,
    # Pass through when the game is already sitting on a board's leaderboard and
    # a DIFFERENT board is wanted. Without it the navigator sees a leaderboard,
    # calls the run done, and the sweep rescans the board it was already on.
    [switch] $FromLeaderboard,
    # Enumerate every route in the selected category and write routes.json.
    [switch] $EnumerateRoutes,
    # Reach a route by carousel index (robust to dirty route-name OCR) instead of
    # by -Track name. -1 disables.
    [int] $RouteIndex = -1,
    [string] $RouteAnchor = "Highway Circuit",
    [int] $RouteAnchorIndex = 0,
    [int] $RouteCount = 23
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Host-Nav {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-nav-host] $Message"
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$runId = "$stamp"
$hostRoot = if ([IO.Path]::IsPathRooted($HostOutputRoot)) {
    $HostOutputRoot
} else {
    [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
}
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$taskName = "ForzaNavigate_$stamp"
$guestScripts = Join-Path $GuestWorkspace "scripts"
$guestNavigator = Join-Path $guestScripts "forza_navigator.ps1"
$guestRunRoot = "$GuestWorkspace\data\navigation\$runId"
$guestLog = "$guestRunRoot\navigator.log"
$guestState = "$guestRunRoot\state.json"

try {
    if ($FreshStart) {
        # A cold start is the only genuinely deterministic entry point: any
        # mid-session state can include modal dialogs, an online session, or a
        # submenu a previous run drifted into.
        Write-Host-Nav "closing any running Forza for a deterministic cold start"
        # Forza holds ~5 GB and does not unwind in three seconds. That fixed wait
        # reported "did not exit" for a process that was alive, responding, and
        # simply mid-shutdown, which aborted a whole sweep on 2026-08-20. Poll
        # instead, and re-issue the kill once before giving up.
        $stillRunning = Invoke-Command -Session $session -ScriptBlock {
            $deadline = (Get-Date).AddSeconds(45)
            $reissued = $false
            Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Seconds 2
                $alive = @(Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)
                if (-not $alive) { return $false }
                if (-not $reissued -and (Get-Date) -gt $deadline.AddSeconds(-25)) {
                    $reissued = $true
                    $alive | Stop-Process -Force -ErrorAction SilentlyContinue
                }
            }
            [bool](Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)
        }
        if ($stillRunning) {
            throw "Forza did not exit within 45 s; restart the VM rather than forcing a GPU-partitioned VM into a stuck state."
        }

        # The pre-existing ForzaRunGame scheduled task fails with 0x800705E0 and
        # never produces a process, so launch Steam directly from a fresh
        # interactive task instead. It must be interactive: the game has to land
        # in session 1 where the console desktop and GPU partition are.
        Write-Host-Nav "launching Forza via Steam"
        Invoke-Command -Session $session -ArgumentList $GuestWorkspace -ScriptBlock {
            param($Workspace)
            $stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
            $runner = Join-Path $Workspace "launch_forza_$stamp.ps1"
            $body = @'
$steam = "C:\Program Files (x86)\Steam\steam.exe"
if (Test-Path -LiteralPath $steam) {
    Start-Process -FilePath $steam -ArgumentList "-applaunch", "2483190"
}
'@
            New-Item -ItemType Directory -Force -Path $Workspace | Out-Null
            Set-Content -LiteralPath $runner -Value $body -Encoding UTF8
            $name = "ForzaLaunch_$stamp"
            $action = New-ScheduledTaskAction -Execute "powershell.exe" `
                -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$runner`""
            $principal = New-ScheduledTaskPrincipal -UserId "admin" -LogonType Interactive -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
            Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
            Start-ScheduledTask -TaskName $name
            Start-Sleep -Seconds 2
            Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $runner -Force -ErrorAction SilentlyContinue
        } | Out-Null

        $launchDeadline = (Get-Date).AddSeconds($LaunchTimeoutSeconds)
        $ready = $false
        do {
            Start-Sleep -Seconds 5
            $ready = Invoke-Command -Session $session -ScriptBlock {
                $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                # Working set climbing past ~200 MB means the game is past process
                # creation and actually initialising.
                [bool]($process -and $process.WorkingSet64 -gt 200MB)
            }
        } while (-not $ready -and (Get-Date) -lt $launchDeadline)
        if (-not $ready) {
            throw "Forza did not start within $LaunchTimeoutSeconds seconds."
        }
        Write-Host-Nav "Forza is up; giving it a moment to reach the title screen"
        Start-Sleep -Seconds 20
    }

    Invoke-Command -Session $session -ArgumentList $guestScripts -ScriptBlock {
        param($Scripts)
        New-Item -ItemType Directory -Force -Path $Scripts | Out-Null
    }
    foreach ($name in @("forza_navigator.ps1", "windows_ocr.ps1")) {
        $path = Join-Path $workspace "scripts\$name"
        if (Test-Path -LiteralPath $path) {
            Copy-Item -ToSession $session -LiteralPath $path -Destination $guestScripts -Force
        }
    }
    Write-Host-Nav "navigator copied to $guestNavigator"

    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$guestNavigator`"",
        "-Track", "`"$Track`"",
        "-PerformanceClass", "`"$PerformanceClass`"",
        "-RivalsMode", "`"$RivalsMode`"",
        "-RunId", "`"$runId`""
    )
    if ($Explore) { $arguments += "-Explore" }
    if ($KeepFrames) { $arguments += "-KeepFrames" }
    if ($FromLeaderboard) { $arguments += "-FromLeaderboard" }
    if ($EnumerateRoutes) { $arguments += "-EnumerateRoutes" }
    if ($RouteIndex -ge 0) {
        $arguments += @("-RouteIndex", [string]$RouteIndex,
                        "-RouteAnchor", "`"$RouteAnchor`"",
                        "-RouteAnchorIndex", [string]$RouteAnchorIndex)
    }
    # Die Streckenzahl gehoert AUCH zum Aufnehmen, nicht nur zum Anfahren einer
    # Position. Am 2026-08-28 haengte sie nur an -RouteIndex, das beim Aufnehmen -1
    # ist: Dirt Racing wurde mit dem Standardwert 23 statt mit seinen 21 Strecken
    # gezaehlt, und die letzten zwei Eintraege waren Wiederholungen von Position 0
    # und 1 -- unauffaellig genau dort, wo niemand mehr hinsieht.
    if ($RouteIndex -ge 0 -or $EnumerateRoutes) {
        $arguments += @("-RouteCount", [string]$RouteCount)
    }
    $argumentLine = $arguments -join " "

    Invoke-Command -Session $session -ArgumentList $taskName, $argumentLine, $TimeoutMinutes -ScriptBlock {
        param($TaskName, $ArgumentLine, $Minutes)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $ArgumentLine
        $principal = New-ScheduledTaskPrincipal -UserId "admin" -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet `
            -ExecutionTimeLimit (New-TimeSpan -Minutes $Minutes) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    } | Out-Null
    Write-Host-Nav "started guest task $taskName (run $runId)"

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes + 2)
    $shown = 0
    $finalState = $null
    do {
        Start-Sleep -Seconds 4
        $snapshot = Invoke-Command -Session $session -ArgumentList $taskName, $guestLog, $guestState, $shown -ScriptBlock {
            param($TaskName, $LogPath, $StatePath, $Shown)
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            $lines = @()
            if (Test-Path -LiteralPath $LogPath) {
                $all = @(Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue)
                if ($all.Count -gt $Shown) { $lines = $all[$Shown..($all.Count - 1)] }
            }
            [pscustomobject]@{
                State = if ($task) { [string]$task.State } else { "Missing" }
                NewLines = $lines
                TotalLines = if (Test-Path -LiteralPath $LogPath) {
                    @(Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue).Count
                } else { 0 }
                StateJson = if (Test-Path -LiteralPath $StatePath) {
                    Get-Content -LiteralPath $StatePath -Raw
                } else { $null }
            }
        }
        foreach ($line in @($snapshot.NewLines)) { Write-Host "    $line" }
        $shown = $snapshot.TotalLines
        if ($snapshot.StateJson) {
            $parsed = $snapshot.StateJson | ConvertFrom-Json
            if ($parsed.status -ne "running") { $finalState = $parsed; break }
        }
        if ($snapshot.State -eq "Ready" -and $shown -gt 0) {
            if ($snapshot.StateJson) { $finalState = $snapshot.StateJson | ConvertFrom-Json }
            break
        }
    } while ((Get-Date) -lt $deadline)

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($TaskName)
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    $exists = Invoke-Command -Session $session -ArgumentList $guestRunRoot -ScriptBlock {
        param($Path); Test-Path -LiteralPath $Path
    }
    if ($exists) {
        Copy-Item -FromSession $session -LiteralPath $guestRunRoot -Destination $hostRoot -Recurse -Force
        Write-Host-Nav "artifacts: $(Join-Path $hostRoot $runId)"
    }

    if ($finalState) {
        Write-Host-Nav "status: $($finalState.status)"
        if ($finalState.error) { Write-Host-Nav "error: $($finalState.error)" }
        if ($finalState.status -eq "leaderboard_reached" -or $finalState.status -eq "explored") { exit 0 }
        exit 1
    }
    Write-Host-Nav "no terminal state was reported before the timeout"
    exit 2
} finally {
    if ($session) { Remove-PSSession $session }
}
