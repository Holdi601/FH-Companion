<#
.SYNOPSIS
    Run forza_paged_scan.ps1 inside the guest as an interactive task and follow it.

.DESCRIPTION
    The driver has to live in the guest console session because it sends the keys,
    and only that session's input reaches the game. This host script copies the
    driver and the row server in, starts them, tails the log, and brings the rows
    back.

    Measures the split loop: PowerShell keeps SendKeys, one resident Python process
    holds the memory handle and answers page requests over a pipe. Baseline to beat
    is 750-770 ranks/min from the old per-page-process loop.

.EXAMPLE
    .\scripts\start_forza_paged_scan.ps1 -StartRank 1
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [int] $StartRank = 1,
    [int] $MaxAdvance = 50,
    [int] $KeyDelayMs = 18,
    [int] $TimeoutMs = 8000,
    [int] $MaxPages = 100000,
    [string] $HostOutputRoot = "data/runtime/paged_scan",
    [int] $TimeoutMinutes = 40
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Host-Paged { param([string] $M) Write-Host "[paged-host] $((Get-Date).ToString('HH:mm:ss')) $M" }

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
$guestRoot = "$GuestWorkspace\data\paged_scan\$stamp"
$guestLog = "$guestRoot\paged.log"
$guestRows = "$guestRoot\rows.jsonl"
$guestCheck = "$guestRoot\checkpoint.json"
$taskName = "ForzaPagedScan_$stamp"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential

try {
    Write-Host-Paged "copying the driver and reader into the guest"
    foreach ($name in "forza_paged_scan.ps1", "forza_row_server.py",
                      "forza_fast_scan.py", "forza_scoreboard_scan.py",
                      "forza_scoreboard_layout.py") {
        Copy-Item -ToSession $session -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $GuestWorkspace "scripts") -Force
    }
    Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
        param($Root) New-Item -ItemType Directory -Force -Path $Root | Out-Null
    }

    $runner = "$GuestWorkspace\paged_run_$stamp.ps1"
    $body = @"
Start-Transcript -LiteralPath "$guestLog" -Force | Out-Null
try {
    & "$GuestWorkspace\scripts\forza_paged_scan.ps1" ``
        -Output "$guestRows" ``
        -Checkpoint "$guestCheck" ``
        -StartRank $StartRank ``
        -MaxAdvance $MaxAdvance ``
        -KeyDelayMs $KeyDelayMs ``
        -TimeoutMs $TimeoutMs ``
        -MaxPages $MaxPages
} catch {
    Write-Host "[paged] ERROR `$(`$_.Exception.Message)"
} finally {
    Stop-Transcript | Out-Null
}
"@
    Invoke-Command -Session $session -ArgumentList $runner, $body -ScriptBlock {
        param($Path, $Text) Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
    }

    Write-Host-Paged "registering interactive guest task $taskName"
    Invoke-Command -Session $session -ArgumentList $taskName, $runner -ScriptBlock {
        param($Name, $Script)
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$Script`""
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" `
            -LogonType Interactive -RunLevel Highest
        Register-ScheduledTask -TaskName $Name -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName $Name
    }

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $seen = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $state = Invoke-Command -Session $session -ArgumentList $taskName, $guestLog -ScriptBlock {
            param($Name, $Log)
            $t = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
            $text = if (Test-Path $Log) { Get-Content $Log -Raw } else { "" }
            [pscustomobject]@{ Running = ($t -and $t.State -eq "Running"); Text = $text }
        }
        if ($state.Text) {
            $lines = @($state.Text -split "`r?`n" | Where-Object { $_ -match "^\[paged\]" })
            for ($i = $seen; $i -lt $lines.Count; $i++) { Write-Host ("  " + $lines[$i]) }
            $seen = $lines.Count
        }
        if (-not $state.Running) { break }
    }

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($Name) Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    }

    foreach ($pair in @(@($guestRows, "rows.jsonl"), @($guestCheck, "checkpoint.json"), @($guestLog, "paged.log"))) {
        $exists = Invoke-Command -Session $session -ArgumentList $pair[0] -ScriptBlock { param($P) Test-Path $P }
        if ($exists) {
            Copy-Item -FromSession $session -LiteralPath $pair[0] `
                -Destination (Join-Path $hostRoot "${stamp}_$($pair[1])") -Force
        }
    }
    Write-Host-Paged "artifacts: $hostRoot"
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}
