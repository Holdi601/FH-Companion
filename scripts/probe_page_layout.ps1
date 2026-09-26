<#
.SYNOPSIS
    Settle how the leaderboard buffer behaves under scrolling: does the row array
    slide by one row per DOWN, or does the game hold fixed 50-row pages?

.DESCRIPTION
    This decides whether a much faster scan design is possible. If the array slides
    per keypress, the address of any visible row is computable and the per-page
    memory SEARCH -- which is what the current scan spends its time on -- can be
    deleted. If the game instead refreshes a whole 50-row page at a time, reading
    after every press would return the same page fifty times over and there is
    nothing to win there.

    Existing scan data already points at fixed pages: 200 consecutive chunks each
    held exactly 50 contiguous ranks starting at rank 1 mod 50, and the scanner
    stopped at 50 rows although it was allowed 512. This probe is the direct
    measurement rather than the inference.

    Nothing here touches the production scan path. It reads memory and sends key
    presses, so it needs the game to itself -- do not run it while a sweep is
    scanning the same guest.

.EXAMPLE
    .\scripts\probe_page_layout.ps1
    .\scripts\probe_page_layout.ps1 -Presses 1 -Samples 8
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    # DOWN presses between two reads of the same address.
    [int] $Presses = 1,
    [int] $Samples = 8,
    [string] $HostOutputRoot = "data/runtime/page_layout_probe",
    [int] $TimeoutMinutes = 15
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Probe { param([string] $Message) Write-Host "[page-probe] $((Get-Date).ToString('HH:mm:ss')) $Message" }

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = [IO.Path]::GetFullPath((Join-Path $workspace $HostOutputRoot))
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
$guestRoot = "$GuestWorkspace\data\page_layout_probe\$stamp"
$guestLog = "$guestRoot\probe.log"
$guestJson = "$guestRoot\probe.json"
$taskName = "ForzaPageProbe_$stamp"

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential

# The probe body runs INSIDE the guest as an interactive task: SendKeys only
# reaches the game from the session the game is rendering in, which is not the
# session PowerShell Direct lands in.
$guestBody = @'
param([int] $Presses, [int] $Samples, [string] $RunRoot)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null
Start-Transcript -LiteralPath "$RunRoot\probe.log" -Force | Out-Null
try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -Namespace ProbeWin -Name N -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
"@
    $game = Get-Process -Name forzahorizon6 -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    [void][ProbeWin.N]::SetForegroundWindow($game.MainWindowHandle)
    Start-Sleep -Milliseconds 400

    $python = "C:\ForzaTools\Python312\python.exe"
    $scanner = "C:\ForzaAutomation\scripts\capture_scoreboard_memory.py"

    function Read-Page {
        param([string] $Label, [string] $ExactAddress)
        $json = "$RunRoot\$Label.json"
        $args = @($scanner, "--pid", [string]$game.Id, "--minimum-rows", "3", "--max-rows", "512",
                  "--chunk-size-mb", "64", "--output-json", $json)
        if ($ExactAddress) { $args += @("--exact-address", $ExactAddress) }
        & $python @args | Out-Null
        if (-not (Test-Path $json)) { return $null }
        $rows = @((Get-Content $json -Raw | ConvertFrom-Json).rows)
        if ($rows.Count -eq 0) { return $null }
        return [pscustomobject]@{
            Count = $rows.Count
            Min   = ($rows | Measure-Object -Property rank -Minimum).Minimum
            Max   = ($rows | Measure-Object -Property rank -Maximum).Maximum
            Base  = [string]$rows[0].source_address
        }
    }

    # Bootstrap: find whatever page is loaded right now. This one pays for a full
    # sweep; every later read is targeted at the base it returns.
    $start = Read-Page -Label "sample_000" -ExactAddress $null
    if ($null -eq $start) { throw "no row block found; is a leaderboard open?" }
    Write-Host ("[probe] start: ranks {0}-{1} at {2}" -f $start.Min, $start.Max, $start.Base)

    $results = @([pscustomobject]@{ sample = 0; presses = 0; min = $start.Min; max = $start.Max; base = $start.Base })
    for ($i = 1; $i -le $Samples; $i++) {
        for ($k = 0; $k -lt $Presses; $k++) {
            [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
            Start-Sleep -Milliseconds 60
        }
        Start-Sleep -Milliseconds 350
        $now = Read-Page -Label ("sample_{0:000}" -f $i) -ExactAddress $start.Base
        if ($null -eq $now) {
            Write-Host ("[probe] sample {0}: nothing readable at the original base any more" -f $i)
            $results += [pscustomobject]@{ sample = $i; presses = $i * $Presses; min = $null; max = $null; base = $null }
            continue
        }
        Write-Host ("[probe] sample {0} after {1} press(es): ranks {2}-{3} at {4}" -f $i, ($i * $Presses), $now.Min, $now.Max, $now.Base)
        $results += [pscustomobject]@{ sample = $i; presses = $i * $Presses; min = $now.Min; max = $now.Max; base = $now.Base }
    }

    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$RunRoot\probe.json" -Encoding UTF8
    Write-Host "[probe] done"
} catch {
    Write-Host "[probe] ERROR $($_.Exception.Message)"
} finally {
    Stop-Transcript | Out-Null
}
'@

try {
    $guestScript = "$GuestWorkspace\page_probe_$stamp.ps1"
    Invoke-Command -Session $session -ArgumentList $guestScript, $guestBody, $GuestWorkspace -ScriptBlock {
        param($Path, $Body, $Workspace)
        New-Item -ItemType Directory -Force -Path $Workspace | Out-Null
        Set-Content -LiteralPath $Path -Value $Body -Encoding UTF8
    }

    Write-Probe "registering interactive guest task $taskName"
    Invoke-Command -Session $session -ArgumentList $taskName, $guestScript, $Presses, $Samples, $guestRoot -ScriptBlock {
        param($Name, $Script, $P, $S, $Root)
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument ("-NoProfile -ExecutionPolicy Bypass -File `"$Script`" -Presses $P -Samples $S -RunRoot `"$Root`"")
        $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
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
            $lines = @($state.Text -split "`r?`n" | Where-Object { $_ -match "^\[probe\]" })
            for ($i = $seen; $i -lt $lines.Count; $i++) { Write-Host ("  " + $lines[$i]) }
            $seen = $lines.Count
        }
        if (-not $state.Running) { break }
    }

    Invoke-Command -Session $session -ArgumentList $taskName -ScriptBlock {
        param($Name) Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    }

    foreach ($name in "probe.json", "probe.log") {
        $remote = Join-Path $guestRoot $name
        $exists = Invoke-Command -Session $session -ArgumentList $remote -ScriptBlock { param($P) Test-Path $P }
        if ($exists) { Copy-Item -FromSession $session -LiteralPath $remote -Destination (Join-Path $hostRoot "${stamp}_$name") -Force }
    }
    Write-Probe "artifacts: $hostRoot"

    $jsonPath = Join-Path $hostRoot "${stamp}_probe.json"
    if (Test-Path $jsonPath) {
        $data = Get-Content $jsonPath -Raw | ConvertFrom-Json
        $first = $data | Select-Object -First 1
        $moved = @($data | Where-Object { $_.sample -gt 0 -and $null -ne $_.min -and $_.min -ne $first.min })
        Write-Host ""
        if ($moved.Count -gt 0) {
            $step = ($moved | Select-Object -First 1).min - $first.min
            Write-Probe "VERDICT: content at the same base CHANGED (first shift $step rank(s) after $(($moved | Select-Object -First 1).presses) press(es))."
            Write-Probe "         a sliding window is plausible -- addresses may be computable, which would remove the per-page search."
        } else {
            Write-Probe "VERDICT: content at the same base did NOT change across $Samples sample(s)."
            Write-Probe "         the game holds fixed pages, so reading after every press would re-read the same rows."
        }
    }
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}
