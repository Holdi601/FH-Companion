<#
.SYNOPSIS
    Page a leaderboard by keeping ONE resident reader open and sending the keys
    from PowerShell, where they demonstrably work.

.DESCRIPTION
    This is the salvageable half of Phase 1. Moving the whole loop into Python
    failed on 2026-08-21: nine runs read memory perfectly and never scrolled,
    because synthetic input from a guest scheduled task is ignored by the game
    even with the window confirmed foreground. `SendKeys` from a PowerShell
    process in the console session is what actually drives it.

    So the sender stays here and only the genuinely expensive parts go away:

    * `forza_row_server.py` starts ONCE and holds one OpenProcess handle, instead
      of a fresh Python process per 50-row page (~0.5 s each)
    * it POLLS for the wanted rank every few ms, replacing the fixed 350 ms settle
      and the blunt multi-second waits when a page is late
    * rows append to one JSONL file rather than a JSON+CSV pair per page

    Measured baseline to beat: 4.0 s per 50-rank page, 750 ranks/min, of which
    0.9 s is the key presses themselves.

    Run this INSIDE the guest console session. From the host use
    start_forza_paged_scan.ps1, which registers it as an interactive task.

.EXAMPLE
    .\forza_paged_scan.ps1 -Output C:\ForzaAutomation\data\paged\rows.jsonl
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Output,
    [string] $Checkpoint,
    [string] $Python = "C:\ForzaTools\Python312\python.exe",
    [string] $ServerScript = "C:\ForzaAutomation\scripts\forza_row_server.py",
    [string] $ProfilePath = "C:\ForzaAutomation\build_profile.json",
    [int] $StartRank = 1,
    [int] $MaxAdvance = 50,
    [int] $KeyDelayMs = 18,
    [int] $TimeoutMs = 8000,
    [int] $MaxPages = 100000,
    [int] $StallPages = 3
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

function Write-Paged { param([string] $Message) Write-Host "[paged] $Message" }

$game = Get-Process -Name forzahorizon6 -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $game) { throw "Forza has no main window; nothing to drive." }

Add-Type -Namespace PagedWin -Name N -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
"@

function Focus-Game {
    if ([PagedWin.N]::GetForegroundWindow() -ne $game.MainWindowHandle) {
        [void][PagedWin.N]::SetForegroundWindow($game.MainWindowHandle)
        Start-Sleep -Milliseconds 40
    }
}

function Send-Down {
    param([int] $Count)
    Focus-Game
    for ($i = 0; $i -lt $Count; $i++) {
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
        if ($KeyDelayMs -gt 0) { Start-Sleep -Milliseconds $KeyDelayMs }
    }
}

# --- start the resident reader and keep its pipes ------------------------------
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $Python
$info.Arguments = "`"$ServerScript`" --profile `"$ProfilePath`" --timeout-ms $TimeoutMs"
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$server = [System.Diagnostics.Process]::Start($info)

function Ask {
    param([hashtable] $Request)
    $server.StandardInput.WriteLine(($Request | ConvertTo-Json -Compress))
    $server.StandardInput.Flush()
    $line = $server.StandardOutput.ReadLine()
    if ($null -eq $line) { throw "the row server closed its output" }
    return $line | ConvertFrom-Json
}

try {
    $ready = $server.StandardOutput.ReadLine() | ConvertFrom-Json
    if (-not $ready.ok) { throw "row server did not come up: $($ready.reason)" }
    Write-Paged "reader ready: stride $($ready.stride), $($ready.addresses) address(es)"

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
    $writer = [System.IO.StreamWriter]::new($Output, $true)
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $highest = $StartRank - 1
    $pages = 0
    $stalls = 0
    $started = Get-Date

    try {
        while ($pages -lt $MaxPages) {
            $answer = Ask @{ want = ($highest + 1) }
            if (-not $answer.ok) {
                $stalls++
                Write-Paged "rank $($highest + 1) did not arrive ($stalls/$StallPages, waited $($answer.waited_ms) ms)"
                if ($stalls -ge $StallPages) { break }
                # A late page is not a missing page: nudge, do not stride past it.
                Send-Down -Count 2
                continue
            }
            $stalls = 0
            $pages++
            $previous = $highest
            foreach ($row in ($answer.rows | Sort-Object { [int]$_.rank })) {
                $rank = [int]$row.rank
                if (-not $seen.Add($rank)) { continue }
                if ($rank -gt $highest) { $highest = $rank }
                $writer.WriteLine(($row | ConvertTo-Json -Compress -Depth 4))
            }
            $writer.Flush()

            $gained = $highest - $previous
            if ($pages % 10 -eq 0) {
                $minutes = [Math]::Max(0.001, ((Get-Date) - $started).TotalMinutes)
                Write-Paged ("ranks={0} highest={1} pages={2} waited={3}ms rate={4:N0}/min" -f `
                    $seen.Count, $highest, $pages, $answer.waited_ms, ($seen.Count / $minutes))
            }
            if ($Checkpoint) {
                @{ ranks_collected = $seen.Count; highest_rank = $highest
                   pages = $pages; next_rank = $highest + 1 } |
                    ConvertTo-Json | Set-Content -LiteralPath $Checkpoint -Encoding UTF8
            }
            if ($gained -le 0) { Write-Paged "a page returned nothing new; stopping"; break }
            # Advance by what was actually read, capped: scrolling further than the
            # data jumps over pages the game then never fetches.
            Send-Down -Count ([Math]::Min($gained, $MaxAdvance))
        }
    } finally {
        $writer.Close()
    }

    $minutes = [Math]::Max(0.001, ((Get-Date) - $started).TotalMinutes)
    Write-Paged ("done: {0} ranks in {1:N1} min -> {2:N0} ranks/min" -f `
        $seen.Count, $minutes, ($seen.Count / $minutes))
} finally {
    try { $server.StandardInput.WriteLine('{"quit":1}'); $server.StandardInput.Flush() } catch {}
    Start-Sleep -Milliseconds 300
    if (-not $server.HasExited) { $server.Kill() }
}
