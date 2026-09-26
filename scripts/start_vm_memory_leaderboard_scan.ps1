[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = "admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [Parameter(Mandatory = $true)]
    [string] $Track,
    [string] $PerformanceClass = "D",
    [string] $RivalsMode = "Road Racing",
    [string] $TotalRanks = "Auto",
    [string] $RunId = "",
    [string] $HostOutputRoot = "data/memory_scans",
    [int] $StartupWaitSeconds = 90,
    [int] $PollSeconds = 3,
    [switch] $NoCopy,
    [switch] $KeepForzaRunning,
    # navigate_forza_rivals_target.ps1 is the pre-patch navigator and cannot reach
    # a board on 6.420.696.0. Use scripts/start_forza_navigation.ps1 to get to the
    # leaderboard, then run this with -SkipNavigation to page it.
    [switch] $SkipNavigation,
    # Scroll/patience tuning, passed through to run_memory_leaderboard_scan.ps1.
    # These decide when a board is declared finished, so they are the difference
    # between "the board ended" and "the game stopped keeping up with us".
    # Resume a board that stopped short. The scan seeks the list to this rank
    # before reading, so a truncated board can be continued instead of rescanned
    # from 1. Passed straight through to run_memory_leaderboard_scan.ps1.
    [switch] $AllowMissingGamertag,
    [int] $StartRank = 1,
    [int] $NoProgressLimit = 8,
    [int] $MaxDownBurst = 60,
    [int] $SettleMs = 350
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Start {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-memory-host] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function Connect-PowerShellDirect {
    $candidates = @(
        $VMUser,
        ".\$VMUser",
        "DESKTOP-V3VB55T\$VMUser"
    ) | Select-Object -Unique
    $deadline = (Get-Date).AddMinutes(3)
    do {
        foreach ($candidate in $candidates) {
            try {
                $credential = New-BlankCredential -User $candidate
                $session = New-PSSession -VMName $VMName -Credential $credential -ErrorAction Stop
                return [pscustomobject]@{
                    session = $session
                    credential = $credential
                    user = $candidate
                }
            } catch {
            }
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw "PowerShell Direct did not accept the blank admin account for $VMName."
}

function ConvertTo-Literal {
    param([AllowEmptyString()][string] $Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $safeTrack = ($Track.ToLowerInvariant() -replace "[^a-z0-9]+", "_").Trim("_")
    $RunId = "{0}_{1}_{2}_{3}" -f (
        Get-Date -Format "yyyyMMdd_HHmmss_fff"
    ), $safeTrack, $PerformanceClass.ToLowerInvariant(), ([guid]::NewGuid().ToString("N").Substring(0, 8))
}

$hostRunRoot = Resolve-WorkspacePath (Join-Path $HostOutputRoot $RunId)
$guestOutputRoot = Join-Path $GuestWorkspace "data\memory_scans"
$guestRunRoot = Join-Path $guestOutputRoot $RunId
$guestRunner = Join-Path $GuestWorkspace "run_memory_scan_$RunId.ps1"
$guestLog = Join-Path $guestRunRoot "runner.log"
$taskName = ("ForzaMemoryScan_$RunId" -replace "[^A-Za-z0-9_]", "_")
if ($taskName.Length -gt 220) {
    $taskName = $taskName.Substring(0, 220)
}
New-Item -ItemType Directory -Force -Path $hostRunRoot | Out-Null

$vmms = Get-Service -Name vmms -ErrorAction Stop
if ($vmms.Status -ne "Running") {
    throw "Hyper-V service vmms is '$($vmms.Status)'. Restart the Windows host before starting a scan; this script will not force a stuck Hyper-V service or VM."
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -in @("Stopping", "Saving", "Pausing", "Starting")) {
    throw "VM '$VMName' is '$($vm.State)'. Restart the Windows host if this state does not clear; this script will not force-stop the VM."
}
if ($vm.State -ne "Running") {
    Write-Start "start VM $VMName"
    Start-VM -Name $VMName | Out-Null
}

# Reconfiguring the GPU partition requires the VM to be off, so this step would
# kill a game that is already sitting on the leaderboard. With -SkipNavigation
# the environment is by definition already prepared.
if ($SkipNavigation) {
    Write-Start "skipping GPU/display preparation; the game is already running"
} else {
    Write-Start "prepare GPU/display without launching Forza"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "start_forza_vm_ready.ps1") `
        -VMName $VMName `
        -VMUser $VMUser `
        -GpuResourcePercent 50 `
        -NoCleanDisplaySession `
        -NoLaunch `
        -NoCopy | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "start_forza_vm_ready.ps1 failed with exit code $LASTEXITCODE."
    }
}

$connection = Connect-PowerShellDirect
$session = $connection.session
$completed = $false
try {
    Write-Start "PowerShell Direct connected as $($connection.user)"
    Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $guestRunRoot -ScriptBlock {
        param($Workspace, $RunRoot)
        New-Item -ItemType Directory -Force -Path $Workspace, $RunRoot | Out-Null
    }

    if (-not $NoCopy) {
        Write-Start "copy scripts and config to VM"
        Invoke-Command -Session $session -ArgumentList $GuestWorkspace -ScriptBlock {
            param($Workspace)
            New-Item -ItemType Directory -Force `
                -Path (Join-Path $Workspace "scripts"), (Join-Path $Workspace "config") |
                Out-Null
        }
        $requiredScripts = @(
            "capture_scoreboard_memory.py",
            "extract_leaderboard.py",
            "navigate_forza_rivals_target.ps1",
            "run_capture_automation.ps1",
            "run_forza_pipeline.ps1",
            "run_memory_leaderboard_scan.ps1",
            "set_forza_display_mode.ps1",
            "windows_ocr.ps1",
            # Layout-discovering scanner and its helpers. Needed because the
            # hard-coded offsets in capture_scoreboard_memory.py are per-build
            # and the game force-updates.
            "forza_scoreboard_layout.py",
            "forza_scoreboard_scan.py",
            "dump_forza_runtime_image.py",
            # Reads the leaderboard scrollbar so the scan can tell a real end of
            # board from the game simply stopping. Needs pillow+numpy in the guest.
            "detect_leaderboard_scrollbar.py",
            # Phase 1 single-process pager. Owns memory reads AND input, so it
            # replaces the per-page PowerShell/Python round trip entirely.
            "forza_fast_scan.py"
        )
        foreach ($name in $requiredScripts) {
            $file = Get-Item -LiteralPath (Join-Path $workspace "scripts\$name") -ErrorAction Stop
            Copy-Item -ToSession $session -LiteralPath $file.FullName `
                -Destination (Join-Path $GuestWorkspace "scripts") -Force
        }
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $workspace "config") -File |
            Where-Object { $_.Extension -eq ".json" }) {
            Copy-Item -ToSession $session -LiteralPath $file.FullName `
                -Destination (Join-Path $GuestWorkspace "config") -Force
        }
    }

    # Killing the game is what makes a from-the-title-screen run deterministic,
    # but it is the exact opposite of what -SkipNavigation asks for: there the
    # caller has already navigated to the board and the open leaderboard IS the
    # state being handed over.
    $stopped = if ($SkipNavigation) {
        Write-Start "leaving the running Forza alone; -SkipNavigation expects an open leaderboard"
        $alive = Invoke-Command -Session $session -ScriptBlock {
            [bool](Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)
        }
        if (-not $alive) {
            throw "-SkipNavigation was given but forzahorizon6 is not running. Navigate to a leaderboard first with scripts/start_forza_navigation.ps1."
        }
        $true
    } else {
    Write-Start "stop any previous Forza instance for a deterministic title-screen start"
    Invoke-Command -Session $session -ScriptBlock {
        $existing = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($existing -and $existing.MainWindowHandle -ne 0) {
            try {
                $shell = New-Object -ComObject WScript.Shell
                if ($shell.AppActivate([int]$existing.Id)) {
                    Start-Sleep -Milliseconds 250
                    $shell.SendKeys("%{F4}")
                    Start-Sleep -Seconds 5
                }
            } catch {
            }
        }
        Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        return -not [bool](Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue)
    }
    }
    if (-not $stopped) {
        throw "The previous Forza process is stuck and cannot be closed safely. Restart the VM before starting a new scan; the script will not force Hyper-V into a stuck Stopping state."
    }

    $trackLiteral = ConvertTo-Literal $Track
    $classLiteral = ConvertTo-Literal $PerformanceClass
    $modeLiteral = ConvertTo-Literal $RivalsMode
    $totalLiteral = ConvertTo-Literal $TotalRanks
    $runLiteral = ConvertTo-Literal $RunId
    $workspaceLiteral = ConvertTo-Literal $GuestWorkspace
    $outputLiteral = ConvertTo-Literal $guestOutputRoot
    $logLiteral = ConvertTo-Literal $guestLog
    $navigationBlock = if ($SkipNavigation) {
        '    Write-Host "[vm-memory-runner] navigation skipped; the leaderboard is assumed to be open"'
    } else {
        @'
    Write-Host "[vm-memory-runner] navigation start"
    & powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\navigate_forza_rivals_target.ps1 -Track TRACK_LITERAL -PerformanceClass CLASS_LITERAL -RivalsMode MODE_LITERAL -StartupWaitSeconds STARTUP_LITERAL
    if ($LASTEXITCODE -ne 0) { throw "navigate_forza_rivals_target.ps1 failed with exit code $LASTEXITCODE" }
'@ -replace "TRACK_LITERAL", $trackLiteral -replace "CLASS_LITERAL", $classLiteral `
   -replace "MODE_LITERAL", $modeLiteral -replace "STARTUP_LITERAL", "$StartupWaitSeconds"
    }

    # Empty string rather than a conditional line: a line holding nothing but a
    # backtick continuation is a parse hazard, and an empty interpolation inside an
    # existing line is not.
    $gamertagArg = if ($AllowMissingGamertag) { "-AllowMissingGamertag" } else { "" }

    $runnerContent = @"
`$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logLiteral) | Out-Null
Start-Transcript -LiteralPath $logLiteral -Force | Out-Null
try {
    Set-Location $workspaceLiteral
$navigationBlock
    Write-Host "[vm-memory-runner] structured memory scan start"
    & powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_memory_leaderboard_scan.ps1 ``
        -Track $trackLiteral ``
        -PerformanceClass $classLiteral ``
        -RivalsMode $modeLiteral ``
        -TotalRanks $totalLiteral ``
        -RunId $runLiteral ``
        -OutputRoot $outputLiteral ``
        -StartRank $StartRank $gamertagArg ``
        -NoProgressLimit $NoProgressLimit ``
        -MaxDownBurst $MaxDownBurst ``
        -SettleMs $SettleMs
    if (`$LASTEXITCODE -ne 0) {
        throw "run_memory_leaderboard_scan.ps1 failed with exit code `$LASTEXITCODE"
    }
    Write-Host "[vm-memory-runner] completed `$((Get-Date).ToString("o"))"
    exit 0
} catch {
    Write-Host "[vm-memory-runner] ERROR `$(`$_.Exception.Message)"
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
"@

    Write-Start "register interactive VM task $taskName"
    Invoke-Command -Session $session -ArgumentList $guestRunner, $runnerContent, $taskName -ScriptBlock {
        param($RunnerPath, $Content, $TaskName)
        Set-Content -LiteralPath $RunnerPath -Value $Content -Encoding UTF8
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal `
            -UserId "$env:COMPUTERNAME\admin" `
            -LogonType Interactive `
            -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit (New-TimeSpan -Hours 72) `
            -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action `
            -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    }

    $lastProgress = ""
    while ($true) {
        Start-Sleep -Seconds $PollSeconds
        $status = Invoke-Command -Session $session -ArgumentList $taskName, $guestRunRoot, $guestLog -ScriptBlock {
            param($TaskName, $RunRoot, $LogPath)
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
            $statePath = Join-Path $RunRoot "state.json"
            $state = $null
            if (Test-Path -LiteralPath $statePath) {
                try {
                    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                } catch {
                }
            }
            $tail = @()
            if (Test-Path -LiteralPath $LogPath) {
                $tail = @(Get-Content -LiteralPath $LogPath -Tail 8)
            }
            [pscustomobject]@{
                task_state = if ($task) { [string]$task.State } else { "Missing" }
                task_result = if ($info) { [int]$info.LastTaskResult } else { -1 }
                progress = $state
                log_tail = $tail
            }
        }

        if ($null -ne $status.progress) {
            $progressText = "rows=$($status.progress.rows_collected) max=$($status.progress.maximum_rank) speed=$($status.progress.ranks_per_minute)/min ETA=$($status.progress.eta_minutes)min"
            if ($progressText -ne $lastProgress) {
                Write-Start $progressText
                $lastProgress = $progressText
            }
        } elseif ($status.log_tail) {
            $line = @($status.log_tail | Where-Object { $_ -match "^\[(vm-memory-runner|forza-target|forza-memory)\]" }) | Select-Object -Last 1
            if ($line -and $line -ne $lastProgress) {
                Write-Start $line
                $lastProgress = $line
            }
        }

        if ($status.task_state -ne "Running") {
            if ($status.task_result -ne 0) {
                Write-Host ($status.log_tail -join [Environment]::NewLine)
                throw "VM task $taskName failed with result $($status.task_result)."
            }
            break
        }
    }

    # One archive, one transfer. Copy-Item -Recurse over PowerShell Direct pays a
    # separate round trip per file, and a full board is hundreds of chunk files:
    # on Highway Circuit / D the copy-back took about 24 minutes against roughly
    # 11 minutes for the scan itself, so most of a sweep would be spent moving
    # small files rather than reading leaderboards.
    Write-Start "compress and copy structured chunks to host"
    $guestArchive = "$guestRunRoot.zip"
    Invoke-Command -Session $session -ArgumentList $guestRunRoot, $guestArchive -ScriptBlock {
        param($Source, $Archive)
        if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
        Compress-Archive -Path (Join-Path $Source "*") -DestinationPath $Archive -CompressionLevel Fastest
    }
    $hostArchive = "$hostRunRoot.zip"
    Copy-Item -FromSession $session -LiteralPath $guestArchive -Destination $hostArchive -Force
    New-Item -ItemType Directory -Force -Path $hostRunRoot | Out-Null
    Expand-Archive -LiteralPath $hostArchive -DestinationPath $hostRunRoot -Force
    Remove-Item -LiteralPath $hostArchive -Force -ErrorAction SilentlyContinue
    Invoke-Command -Session $session -ArgumentList $guestArchive -ScriptBlock {
        param($Archive); Remove-Item -LiteralPath $Archive -Force -ErrorAction SilentlyContinue
    }

    $chunks = Join-Path $hostRunRoot "chunks"
    $combined = Join-Path $hostRunRoot "combined"
    $parquet = Join-Path $hostRunRoot "leaderboard_entries.parquet"
    Write-Start "merge CSV/JSON/Parquet"
    & python (Join-Path $PSScriptRoot "merge_scoreboard_memory_chunks.py") `
        --input-dir $chunks `
        --output-dir $combined `
        --parquet-path $parquet `
        --drop-raw
    if ($LASTEXITCODE -ne 0) {
        throw "merge_scoreboard_memory_chunks.py failed with exit code $LASTEXITCODE."
    }
    $completed = $true
    Write-Start "done: $hostRunRoot"
} finally {
    if ($session) {
        if (-not $completed) {
            Write-Start "stop VM task after interruption/failure"
            Invoke-Command -Session $session -ArgumentList $taskName, $RunId, $KeepForzaRunning.IsPresent -ScriptBlock {
                param($TaskName, $RunId, $KeepForza)
                Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
                if (-not $KeepForza) {
                    Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                        Stop-Process -Force -ErrorAction SilentlyContinue
                }
                Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
                    Where-Object { $_.CommandLine -like "*$RunId*" } |
                    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            } -ErrorAction SilentlyContinue
        }
        Remove-PSSession $session
    }
}
