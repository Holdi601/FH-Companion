[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $OutputDir = "data/runtime/current_vm_screen",
    [int] $TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)

    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

$hostOutput = Resolve-WorkspacePath $OutputDir
New-Item -ItemType Directory -Force -Path $hostOutput | Out-Null

$credential = New-BlankCredential -User $VMUser
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$taskName = "ForzaObserve_$stamp"
$guestOutput = "C:\ForzaCaptures\observe\$stamp"
$runnerPath = Join-Path $GuestWorkspace "observe_$stamp.ps1"
$configPath = Join-Path $GuestWorkspace "observe_$stamp.json"

try {
    $config = [ordered]@{
        launch = [ordered]@{
            command = "steam://rungameid/2483190"
            arguments = @()
            startup_wait_seconds = 0
        }
        window = [ordered]@{
            process_names = @("forzahorizon6")
            title_regex = "^Forza Horizon 6$"
            focus_timeout_seconds = 20
            refocus_timeout_seconds = 8
            ensure_focus_before_actions = $true
            alt_tab_fallback = $true
        }
        display = [ordered]@{
            manage = $false
            mode_before_launch = "Windowed"
            restore_after = $false
            backup_root = "data/backups/forza_display"
        }
        capture = [ordered]@{
            output_dir = $guestOutput
            state_dir = (Join-Path $guestOutput "_state")
            prefix = "current"
            clear_output_dir = $true
            dump_state_ocr = $true
            ocr_language = "en"
        }
        steps = @(
            [ordered]@{
                action = "observe"
                name = "current.png"
                ocr = $true
            }
        )
        extraction = [ordered]@{
            enabled = $false
            track = ""
            performance_class = ""
            pi_class = ""
            event_type = "Rivals"
            rivals_mode = ""
            profile = "config/fh6_rivals_1080p.json"
            output_dir = "data/processed"
            sqlite_path = "data/processed/leaderboard_entries.sqlite"
            parquet_path = "data/processed/leaderboard_entries.parquet"
            dump_ocr = $false
            keep_crops = $false
        }
    }
    $runner = @"
`$ErrorActionPreference = "Stop"
Set-Location "$GuestWorkspace"
& powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\run_capture_automation.ps1" -Config "$configPath" -SkipLaunch -SkipExtract
exit `$LASTEXITCODE
"@

    Invoke-Command -Session $session -ArgumentList $configPath, $config, $runnerPath, $runner, $taskName -ScriptBlock {
        param($ConfigPath, $Config, $RunnerPath, $Runner, $TaskName)

        $Config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
        Set-Content -LiteralPath $RunnerPath -Value $Runner -Encoding UTF8
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$RunnerPath`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $result = Invoke-Command -Session $session -ArgumentList $taskName, $guestOutput -ScriptBlock {
            param($TaskName, $Output)
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
            [pscustomobject]@{
                state = if ($task) { [string]$task.State } else { "Missing" }
                result = if ($info) { [int]$info.LastTaskResult } else { -1 }
                files = @(
                    Get-ChildItem -LiteralPath $Output -Recurse -File -ErrorAction SilentlyContinue |
                        Select-Object FullName, Name, Length, LastWriteTime
                )
            }
        }
        $png = @($result.files | Where-Object Name -like "*.png" | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    } while ((Get-Date) -lt $deadline -and $result.state -eq "Running")

    if ($png.Count -eq 0) {
        throw "VM screenshot task did not produce a PNG. Task state=$($result.state), result=$($result.result)."
    }
    if ($result.result -ne 0) {
        throw "VM screenshot task failed with result $($result.result)."
    }

    foreach ($file in @($result.files)) {
        Copy-Item -FromSession $session -LiteralPath $file.FullName -Destination (Join-Path $hostOutput $file.Name) -Force
    }
    Get-ChildItem -LiteralPath $hostOutput -File | Sort-Object LastWriteTime -Descending
} finally {
    if ($session) {
        Invoke-Command -Session $session -ArgumentList $taskName, $runnerPath, $configPath -ScriptBlock {
            param($TaskName, $RunnerPath, $ConfigPath)
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $RunnerPath, $ConfigPath -Force -ErrorAction SilentlyContinue
        } -ErrorAction SilentlyContinue
        Remove-PSSession $session
    }
}
