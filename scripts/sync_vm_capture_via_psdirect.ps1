[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $RunId = "20260609_170935_977_highway_circuit_d_09fcb916",
    [string] $GuestRunRoot = "",
    [string] $HostRunRoot = "",
    [int] $PollSeconds = 15,
    [int] $StableSeconds = 3,
    [switch] $Watch,
    [switch] $Once
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

function Write-Sync {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-sync] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function New-ForzaVmSession {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $User
    )

    $credential = New-BlankCredential -User $User
    return New-PSSession -VMName $Name -Credential $credential
}

function Get-ExpectedScreenshotCount {
    param([Parameter(Mandatory = $true)][string] $ChunkName)

    $match = [regex]::Match($ChunkName, "_([0-9]{3,})$")
    if ($match.Success) {
        return [int]$match.Groups[1].Value
    }
    return 0
}

function Get-CompleteGuestChunks {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)][string] $ChunksRoot,
        [Parameter(Mandatory = $true)][int] $StableAgeSeconds
    )

    Invoke-Command -Session $Session -ArgumentList $ChunksRoot, $StableAgeSeconds -ScriptBlock {
        param([string] $ChunksRoot, [int] $StableAgeSeconds)

        if (-not (Test-Path -LiteralPath $ChunksRoot)) {
            return @()
        }

        $now = Get-Date
        $result = @()
        foreach ($dir in Get-ChildItem -LiteralPath $ChunksRoot -Directory -ErrorAction SilentlyContinue) {
            $completionPath = Join-Path $dir.FullName ".capture_complete.json"
            $expected = 0
            if (Test-Path -LiteralPath $completionPath) {
                try {
                    $completion = Get-Content -LiteralPath $completionPath -Raw | ConvertFrom-Json
                    $expected = [int]$completion.actual_screenshot_count
                } catch {
                    $expected = 0
                }
            }
            if ($expected -lt 1) {
                $match = [regex]::Match($dir.Name, "_([0-9]{3,})$")
                if (-not $match.Success) {
                    continue
                }
                $expected = [int]$match.Groups[1].Value
            }
            $files = @(Get-ChildItem -LiteralPath $dir.FullName -Filter "leaderboard*.png" -File -ErrorAction SilentlyContinue)
            if ($files.Count -lt $expected) {
                continue
            }
            $latest = ($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
            if (($now - $latest).TotalSeconds -lt $StableAgeSeconds) {
                continue
            }
            $result += [pscustomobject]@{
                name = $dir.Name
                full_name = $dir.FullName
                expected = $expected
                files = $files.Count
                latest_write = $latest.ToUniversalTime().ToString("o")
            }
        }
        $result
    }
}

function Copy-GuestFileIfPresent {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)][string] $GuestPath,
        [Parameter(Mandatory = $true)][string] $HostPath
    )

    $exists = Invoke-Command -Session $Session -ArgumentList $GuestPath -ScriptBlock {
        param([string] $Path)
        Test-Path -LiteralPath $Path
    }
    if (-not $exists) {
        return $false
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $HostPath) | Out-Null
    try {
        Copy-Item -FromSession $Session -LiteralPath $GuestPath -Destination $HostPath -Force -ErrorAction Stop
        return $true
    } catch {
        Write-Sync "skip file $GuestPath ($($_.Exception.Message))"
        return $false
    }
}

if ([string]::IsNullOrWhiteSpace($GuestRunRoot)) {
    $GuestRunRoot = Join-Path "C:\ForzaCaptures\rank_scans" $RunId
}
if ([string]::IsNullOrWhiteSpace($HostRunRoot)) {
    $HostRunRoot = Resolve-WorkspacePath (Join-Path "data/vm_share/rank_scans" $RunId)
} else {
    $HostRunRoot = Resolve-WorkspacePath $HostRunRoot
}

$guestChunksRoot = Join-Path $GuestRunRoot "chunks"
$hostChunksRoot = Join-Path $HostRunRoot "chunks"
$hostLogsRoot = Join-Path $HostRunRoot "logs"
New-Item -ItemType Directory -Force -Path $HostRunRoot, $hostChunksRoot, $hostLogsRoot | Out-Null

Write-Sync "guest root: $GuestRunRoot"
Write-Sync "host root: $HostRunRoot"

$session = $null
try {
    $session = New-ForzaVmSession -Name $VMName -User $VMUser
    do {
        Copy-GuestFileIfPresent -Session $session -GuestPath (Join-Path $GuestRunRoot "state.json") -HostPath (Join-Path $HostRunRoot "state.json") | Out-Null

        $guestLogs = Invoke-Command -Session $session -ArgumentList (Join-Path $GuestRunRoot "logs") -ScriptBlock {
            param([string] $LogRoot)
            if (Test-Path -LiteralPath $LogRoot) {
                Get-ChildItem -LiteralPath $LogRoot -File -ErrorAction SilentlyContinue | Select-Object FullName, Name
            }
        }
        foreach ($log in @($guestLogs)) {
            Copy-GuestFileIfPresent -Session $session -GuestPath $log.FullName -HostPath (Join-Path $hostLogsRoot $log.Name) | Out-Null
        }

        $completeChunks = @(Get-CompleteGuestChunks -Session $session -ChunksRoot $guestChunksRoot -StableAgeSeconds $StableSeconds)
        $synced = 0
        foreach ($chunk in $completeChunks) {
            $hostChunk = Join-Path $hostChunksRoot $chunk.name
            $marker = Join-Path $hostChunk ".sync_complete.json"
            if (Test-Path -LiteralPath $marker) {
                continue
            }

            $tempChunk = Join-Path $hostChunksRoot (".$($chunk.name).syncing")
            if (Test-Path -LiteralPath $tempChunk) {
                Remove-Item -LiteralPath $tempChunk -Recurse -Force
            }
            Copy-Item -FromSession $session -LiteralPath $chunk.full_name -Destination $tempChunk -Recurse -Force

            if (Test-Path -LiteralPath $hostChunk) {
                Remove-Item -LiteralPath $hostChunk -Recurse -Force
            }
            Move-Item -LiteralPath $tempChunk -Destination $hostChunk
            [pscustomobject]@{
                completed_at = (Get-Date).ToUniversalTime().ToString("o")
                guest = $chunk.full_name
                host = $hostChunk
                expected = $chunk.expected
                files = $chunk.files
                latest_write = $chunk.latest_write
            } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $marker -Encoding UTF8
            Write-Sync "synced $($chunk.name) ($($chunk.files) files)"
            $synced += 1
        }

        if ($synced -eq 0) {
            Write-Sync "no complete new chunks"
        }
        if ($Once -or -not $Watch) {
            break
        }
        Start-Sleep -Seconds $PollSeconds
    } while ($true)
}
finally {
    if ($null -ne $session) {
        Remove-PSSession $session
    }
}

Write-Sync "done"
