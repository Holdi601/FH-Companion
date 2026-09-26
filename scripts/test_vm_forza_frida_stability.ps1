[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = "admin",
    [int] $LaunchTimeoutSeconds = 90,
    [int] $BaselineSeconds = 45,
    [int] $AttachSeconds = 45,
    [int] $WinHttpSeconds = 30,
    [string] $OutputDir = "data/network_probes/runtime_hooks/stability",
    [switch] $LeaveRunning
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$hostOutput = if ([IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    [IO.Path]::GetFullPath((Join-Path $workspace $OutputDir))
}
New-Item -ItemType Directory -Force -Path $hostOutput | Out-Null

function Write-Stability {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-frida-stability] $Message"
}

function Test-GuestProcess {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)][int] $ProcessId
    )
    return [bool](Invoke-Command -Session $Session -ArgumentList $ProcessId -ScriptBlock {
        param([int] $ProcessId)
        Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    })
}

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$guestRoot = "C:\ForzaAutomation\stability"
$guestAttachOutput = Join-Path $guestRoot "attach_$stamp.json"
$guestWinHttpOutput = Join-Path $guestRoot "winhttp_$stamp.jsonl"
$hostReportPath = Join-Path $hostOutput "stability_$stamp.json"
$hostAttachOutput = Join-Path $hostOutput "attach_$stamp.json"
$hostWinHttpOutput = Join-Path $hostOutput "winhttp_$stamp.jsonl"
$result = [ordered]@{
    started_at = (Get-Date).ToUniversalTime().ToString("o")
    pid = $null
    baseline_alive = $false
    attach_exit_code = $null
    alive_after_attach = $false
    winhttp_exit_code = $null
    alive_after_winhttp = $false
    stopped_after_test = $false
}

try {
    Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
        param([string] $Root)
        New-Item -ItemType Directory -Force -Path $Root, "C:\ForzaAutomation\scripts" | Out-Null
    }
    Copy-Item -ToSession $session -LiteralPath `
        (Join-Path $workspace "scripts\test_frida_stability.py"), `
        (Join-Path $workspace "scripts\trace_forza_winhttp.py") `
        -Destination "C:\ForzaAutomation\scripts" -Force

    $process = Invoke-Command -Session $session -ScriptBlock {
        Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending |
            Select-Object -First 1 Id, StartTime
    }
    if (-not $process) {
        Write-Stability "start Forza once"
        Invoke-Command -Session $session -ScriptBlock {
            Start-ScheduledTask -TaskName "ForzaRunGame"
        } | Out-Null
        $deadline = (Get-Date).AddSeconds($LaunchTimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 250
            $process = Invoke-Command -Session $session -ScriptBlock {
                Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                    Sort-Object StartTime -Descending |
                    Select-Object -First 1 Id, StartTime
            }
        } while (-not $process -and (Get-Date) -lt $deadline)
    }
    if (-not $process) {
        throw "Forza did not start within $LaunchTimeoutSeconds seconds."
    }

    $pidValue = [int]$process.Id
    $result.pid = $pidValue
    Write-Stability "baseline without Frida for ${BaselineSeconds}s (pid $pidValue)"
    Start-Sleep -Seconds $BaselineSeconds
    $result.baseline_alive = Test-GuestProcess -Session $session -ProcessId $pidValue
    if (-not $result.baseline_alive) {
        throw "Forza exited during the no-Frida baseline."
    }

    Write-Stability "attach Frida without hooks for ${AttachSeconds}s"
    $attachRun = Invoke-Command -Session $session -ArgumentList $pidValue, $AttachSeconds, $guestAttachOutput -ScriptBlock {
        param([int] $ProcessId, [int] $Seconds, [string] $Output)
        $text = & "C:\ForzaTools\Python312\python.exe" `
            "C:\ForzaAutomation\scripts\test_frida_stability.py" `
            --pid $ProcessId --hold-seconds $Seconds --output $Output 2>&1
        [pscustomobject]@{
            exit_code = $LASTEXITCODE
            output = ($text -join "`n")
        }
    }
    $result.attach_exit_code = [int]$attachRun.exit_code
    $result.alive_after_attach = Test-GuestProcess -Session $session -ProcessId $pidValue
    $attachOutputExists = Invoke-Command -Session $session -ArgumentList $guestAttachOutput -ScriptBlock {
        param([string] $Path)
        Test-Path -LiteralPath $Path
    }
    if ($attachOutputExists) {
        Copy-Item -FromSession $session -LiteralPath $guestAttachOutput -Destination $hostAttachOutput -Force
    }

    if ($result.attach_exit_code -eq 0 -and $result.alive_after_attach -and $WinHttpSeconds -gt 0) {
        Write-Stability "run sanitized Xls-only WinHTTP hook for ${WinHttpSeconds}s"
        $winHttpRun = Invoke-Command -Session $session -ArgumentList $pidValue, $WinHttpSeconds, $guestWinHttpOutput -ScriptBlock {
            param([int] $ProcessId, [int] $Seconds, [string] $Output)
            $text = & "C:\ForzaTools\Python312\python.exe" `
                "C:\ForzaAutomation\scripts\trace_forza_winhttp.py" `
                --pid $ProcessId --timeout-seconds $Seconds --max-body-bytes 1024 --output $Output 2>&1
            [pscustomobject]@{
                exit_code = $LASTEXITCODE
                output = ($text -join "`n")
            }
        }
        $result.winhttp_exit_code = [int]$winHttpRun.exit_code
        $result.alive_after_winhttp = Test-GuestProcess -Session $session -ProcessId $pidValue
        $winHttpOutputExists = Invoke-Command -Session $session -ArgumentList $guestWinHttpOutput -ScriptBlock {
            param([string] $Path)
            Test-Path -LiteralPath $Path
        }
        if ($winHttpOutputExists) {
            Copy-Item -FromSession $session -LiteralPath $guestWinHttpOutput -Destination $hostWinHttpOutput -Force
        }
    }
}
finally {
    if (-not $LeaveRunning -and $result.pid) {
        Write-Stability "explicitly stop Forza after the completed test"
        Invoke-Command -Session $session -ArgumentList ([int]$result.pid) -ScriptBlock {
            param([int] $ProcessId)
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        } -ErrorAction SilentlyContinue
        $result.stopped_after_test = $true
    }
    $result.completed_at = (Get-Date).ToUniversalTime().ToString("o")
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $hostReportPath -Encoding UTF8
    if ($session) {
        Remove-PSSession $session
    }
}

$result | ConvertTo-Json -Depth 6
if (-not $result.baseline_alive -or -not $result.alive_after_attach) {
    exit 2
}
if ($WinHttpSeconds -gt 0 -and -not $result.alive_after_winhttp) {
    exit 3
}
