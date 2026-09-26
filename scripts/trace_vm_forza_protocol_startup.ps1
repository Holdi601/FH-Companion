[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = "admin",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $OutputDir = "data/network_probes/runtime_hooks",
    [int] $AttachTimeoutSeconds = 90,
    [int] $TraceTimeoutSeconds = 240,
    [int] $PollMilliseconds = 150,
    [int] $MaxBytes = 256,
    [switch] $NoLaunch,
    [switch] $StopExistingForza,
    [switch] $WinHttp,
    [switch] $HttpClient,
    [switch] $BCrypt,
    [switch] $StringAccess,
    [switch] $ResponseDispatch,
    [switch] $EncryptionSession,
    [switch] $StopAfterTrace,
    [string[]] $Rva = @()
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
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function Write-TraceStart {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-protocol-trace] $Message"
}

if ($PollMilliseconds -lt 50) {
    throw "PollMilliseconds must be at least 50."
}
if ($MaxBytes -lt 16 -or $MaxBytes -gt 4096) {
    throw "MaxBytes must be between 16 and 4096."
}

$hostOutput = Resolve-WorkspacePath $OutputDir
New-Item -ItemType Directory -Force -Path $hostOutput | Out-Null

$credential = New-BlankCredential -User $VMUser
$session = New-PSSession -VMName $VMName -Credential $credential
$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$guestProtocol = Join-Path $GuestWorkspace "protocol"
$selectedTraceModes = @($WinHttp, $HttpClient, $BCrypt, $StringAccess, $ResponseDispatch, $EncryptionSession) | Where-Object { [bool]$_ }
if ($selectedTraceModes.Count -gt 1) {
    throw "Choose only one trace mode: -WinHttp, -HttpClient, -BCrypt, -StringAccess, -ResponseDispatch, or -EncryptionSession."
}
$traceScriptName = if ($EncryptionSession) {
    "trace_forza_encryption_session.py"
} elseif ($ResponseDispatch) {
    "trace_forza_response_dispatch.py"
} elseif ($StringAccess) {
    "trace_forza_string_access.py"
} elseif ($BCrypt) {
    "trace_forza_bcrypt.py"
} elseif ($HttpClient) {
    "trace_forza_httpclient.py"
} elseif ($WinHttp) {
    "trace_forza_winhttp.py"
} else {
    "trace_forza_rva_hooks.py"
}
$tracePrefix = if ($EncryptionSession) {
    "startup_encryption_session"
} elseif ($ResponseDispatch) {
    "startup_response_dispatch"
} elseif ($StringAccess) {
    "startup_string_access"
} elseif ($BCrypt) {
    "startup_bcrypt"
} elseif ($HttpClient) {
    "startup_httpclient"
} elseif ($WinHttp) {
    "startup_winhttp"
} else {
    "startup_hooks"
}
$guestOutput = Join-Path $guestProtocol ("{0}_{1}.jsonl" -f $tracePrefix, $stamp)
$hostOutputFile = Join-Path $hostOutput ("{0}_{1}.jsonl" -f $tracePrefix, $stamp)
$normalizedRva = @(
    foreach ($item in $Rva) {
        foreach ($part in ([string]$item -split ",")) {
            $trimmed = $part.Trim()
            if ($trimmed) {
                $trimmed
            }
        }
    }
)
if (-not $WinHttp -and -not $HttpClient -and -not $BCrypt -and -not $StringAccess -and -not $ResponseDispatch -and -not $EncryptionSession -and $normalizedRva.Count -eq 0) {
    throw "RVA tracing requires one or more explicit -Rva name=0xOFFSET values."
}

try {
    Write-TraceStart "copy trace tools"
    Invoke-Command -Session $session -ArgumentList $GuestWorkspace, $guestProtocol -ScriptBlock {
        param([string] $Workspace, [string] $Protocol)
        New-Item -ItemType Directory -Force -Path $Workspace, (Join-Path $Workspace "scripts"), $Protocol | Out-Null
    }
    Copy-Item -ToSession $session -LiteralPath `
        (Join-Path $workspace ("scripts\{0}" -f $traceScriptName)) `
        -Destination (Join-Path $GuestWorkspace ("scripts\{0}" -f $traceScriptName)) `
        -Force

    if ($StopExistingForza) {
        Write-TraceStart "explicitly stop existing Forza process before relaunch"
        Invoke-Command -Session $session -ScriptBlock {
            Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
        } | Out-Null
        Start-Sleep -Seconds 2
    }

    if (-not $NoLaunch) {
        Write-TraceStart "start ForzaRunGame task"
        Invoke-Command -Session $session -ScriptBlock {
            Start-ScheduledTask -TaskName "ForzaRunGame"
        } | Out-Null
    }

    Write-TraceStart "wait for forzahorizon6 pid"
    $pidInfo = $null
    $deadline = (Get-Date).AddSeconds($AttachTimeoutSeconds)
    do {
        $pidInfo = Invoke-Command -Session $session -ScriptBlock {
            Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
                Sort-Object StartTime -Descending |
                Select-Object -First 1 Id, ProcessName, StartTime
        }
        if ($pidInfo) {
            break
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    } while ((Get-Date) -lt $deadline)

    if (-not $pidInfo) {
        throw "Timed out waiting for forzahorizon6."
    }

    $pidValue = [int]$pidInfo.Id
    Write-TraceStart "attach Frida to pid $pidValue"
    Invoke-Command -Session $session -ArgumentList $pidValue, $TraceTimeoutSeconds, $guestOutput, $traceScriptName, $normalizedRva, $MaxBytes -ScriptBlock {
        param([int] $PidValue, [int] $TimeoutSeconds, [string] $Output, [string] $TraceScriptName, [string[]] $Rva, [int] $MaxBytes)
        $traceArgs = @(
            (Join-Path "C:\ForzaAutomation\scripts" $TraceScriptName),
            "--pid", $PidValue,
            "--timeout-seconds", $TimeoutSeconds,
            "--output", $Output
        )
        if ($TraceScriptName -eq "trace_forza_rva_hooks.py") {
            $traceArgs += @("--max-bytes", $MaxBytes)
        }
        foreach ($item in $Rva) {
            $traceArgs += @("--rva", $item)
        }
        & "C:\ForzaTools\Python312\python.exe" @traceArgs
        if ($LASTEXITCODE -ne 0) {
            throw "$TraceScriptName failed with exit code $LASTEXITCODE"
        }
    }

    Write-TraceStart "copy trace log to host"
    Copy-Item -FromSession $session -LiteralPath $guestOutput -Destination $hostOutputFile -Force
    if ($StopAfterTrace) {
        Write-TraceStart "explicitly stop Forza after trace"
        Invoke-Command -Session $session -ArgumentList $pidValue -ScriptBlock {
            param([int] $ProcessId)
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        } | Out-Null
    }
    [pscustomobject]@{
        pid = $pidValue
        guest_output = $guestOutput
        host_output = $hostOutputFile
    } | ConvertTo-Json -Depth 4
}
finally {
    if ($session) {
        Remove-PSSession $session
    }
}
