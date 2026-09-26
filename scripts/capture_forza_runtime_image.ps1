<#
.SYNOPSIS
    Dump the decrypted forzahorizon6.exe image out of the running VM game and
    bring it back to the host for offline analysis.

.DESCRIPTION
    Most of forzahorizon6.exe is encrypted on disk and only decrypted in memory,
    so the shipped executable disassembles to garbage exactly where the Xls
    transport and scoreboard deserializer live. This runs
    scripts/dump_forza_runtime_image.py inside the VM against the live process,
    compresses the result, copies it to the host, and reports how much of each
    section is runtime-only.

    Read-only with respect to the game: the guest-side dumper opens the process
    with PROCESS_VM_READ and injects nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture_forza_runtime_image.ps1
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $OutputRoot = "data/network_probes/runtime_analysis",
    [string] $GuestPython = "C:\ForzaTools\Python312\python.exe",
    [string] $GuestWorkspace = "C:\ForzaAutomation",
    [string] $GuestGameExe = "C:\Program Files (x86)\Steam\steamapps\common\ForzaHorizon6\forzahorizon6.exe",
    [switch] $KeepGuestCopy
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Runtime {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-runtime-image] $Message"
}

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return [IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$hostRoot = Resolve-WorkspacePath (Join-Path $OutputRoot $stamp)
New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
try {
    $guestRoot = Join-Path $GuestWorkspace "runtime_image"
    $guestScripts = Join-Path $GuestWorkspace "scripts"

    Write-Runtime "checking the guest game process"
    $target = Invoke-Command -Session $session -ScriptBlock {
        $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending |
            Select-Object -First 1
        [pscustomobject]@{
            Found = [bool]$process
            ProcessId = $process.Id
            StartTime = $process.StartTime
            WorkingSetGB = if ($process) { [math]::Round($process.WorkingSet64 / 1GB, 2) } else { 0 }
        }
    }
    if (-not $target.Found) {
        throw "forzahorizon6 is not running in $VMName. Start the game and sit on any online screen first."
    }
    Write-Runtime "guest pid $($target.ProcessId), working set $($target.WorkingSetGB) GB, started $($target.StartTime)"

    Invoke-Command -Session $session -ArgumentList $guestRoot, $guestScripts -ScriptBlock {
        param([string] $Root, [string] $Scripts)
        New-Item -ItemType Directory -Force -Path $Root, $Scripts | Out-Null
    } | Out-Null

    Copy-Item -ToSession $session `
        -LiteralPath (Join-Path $workspace "scripts\dump_forza_runtime_image.py") `
        -Destination $guestScripts -Force

    Write-Runtime "dumping the live image inside the VM"
    $result = Invoke-Command -Session $session `
        -ArgumentList $GuestPython, $guestScripts, $guestRoot, $target.ProcessId, $GuestGameExe `
        -ScriptBlock {
            param(
                [string] $Python,
                [string] $Scripts,
                [string] $Root,
                [int] $ProcessId,
                [string] $GameExe
            )
            $image = Join-Path $Root "forza_runtime.exe"
            $report = Join-Path $Root "forza_runtime.json"
            $arguments = @(
                (Join-Path $Scripts "dump_forza_runtime_image.py"),
                "--pid", [string]$ProcessId,
                "--module", "forzahorizon6.exe",
                "--output", $image,
                "--report", $report
            )
            if (Test-Path -LiteralPath $GameExe) {
                $arguments += @("--compare-exe", $GameExe)
            }
            $stdout = & $Python @arguments 2>&1
            $archive = Join-Path $Root "forza_runtime.zip"
            if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
            if (Test-Path -LiteralPath $image) {
                Compress-Archive -LiteralPath $image -DestinationPath $archive -CompressionLevel Optimal
            }
            [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Output = ($stdout | Out-String)
                Image = $image
                ImageBytes = (Get-Item -LiteralPath $image -ErrorAction SilentlyContinue).Length
                Archive = $archive
                ArchiveBytes = (Get-Item -LiteralPath $archive -ErrorAction SilentlyContinue).Length
                Report = $report
            }
        }

    if ($result.ExitCode -ne 0 -or -not $result.ArchiveBytes) {
        Write-Host $result.Output
        throw "The guest dumper failed with exit code $($result.ExitCode)."
    }
    Write-Runtime ("dumped {0:N0} bytes, compressed to {1:N0} bytes" -f $result.ImageBytes, $result.ArchiveBytes)

    Write-Runtime "copying to the host"
    Copy-Item -FromSession $session -LiteralPath $result.Archive -Destination $hostRoot -Force
    Copy-Item -FromSession $session -LiteralPath $result.Report -Destination $hostRoot -Force

    $archivePath = Join-Path $hostRoot "forza_runtime.zip"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $hostRoot -Force
    Remove-Item -LiteralPath $archivePath -Force

    if (-not $KeepGuestCopy) {
        Invoke-Command -Session $session -ArgumentList $guestRoot -ScriptBlock {
            param([string] $Root)
            Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $reportPath = Join-Path $hostRoot "forza_runtime.json"
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Write-Runtime "unreadable bytes: $($report.unreadable_bytes)"
    if ($report.disk_comparison) {
        Write-Runtime "runtime-only decryption per section:"
        $report.disk_comparison.sections |
            Sort-Object identical_ratio |
            ForEach-Object {
                Write-Host ("  {0,-10} identical {1,7:P1}  runtime entropy {2,6:N3}  disk entropy {3,6:N3}{4}" -f `
                    $_.section, $_.identical_ratio, $_.runtime_entropy, $_.disk_entropy,
                    $(if ($_.executable) { "  [exec]" } else { "" }))
            }
    }
    Write-Runtime "image: $(Join-Path $hostRoot 'forza_runtime.exe')"

    [pscustomobject]@{
        output_root = $hostRoot
        image = Join-Path $hostRoot "forza_runtime.exe"
        report = $reportPath
        process_id = $target.ProcessId
    } | ConvertTo-Json
} finally {
    if ($session) { Remove-PSSession $session }
}
