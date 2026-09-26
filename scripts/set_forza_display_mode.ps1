param(
    [ValidateSet("Status", "Windowed", "Fullscreen", "Restore")]
    [string] $Action = "Status",

    [string] $ForzaLocalDir = (Join-Path $env:LOCALAPPDATA "ForzaHorizon6"),

    [string] $BackupRoot = "data/backups/forza_display",

    [string] $RestoreBackupDir = "",

    [switch] $Force,

    [switch] $DryRun
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

function Get-DisplayConfigPaths {
    param([Parameter(Mandatory = $true)][string] $ConfigRoot)

    $userConfig = Join-Path $ConfigRoot "LocalStorage_Shared\ForzaUserConfigSelections\UserConfigSelections"
    $fullscreenChoice = Join-Path $ConfigRoot "fullscreen_choice"

    [pscustomobject]@{
        Root = $ConfigRoot
        UserConfig = $userConfig
        FullscreenChoice = $fullscreenChoice
    }
}

function Get-ForzaDisplayStatus {
    param([Parameter(Mandatory = $true)] $Paths)

    $fullscreenValue = $null
    if (Test-Path -LiteralPath $Paths.UserConfig) {
        $xml = New-Object System.Xml.XmlDocument
        $xml.PreserveWhitespace = $true
        $xml.Load($Paths.UserConfig)
        $fullscreenNode = $xml.SelectSingleNode("/UserConfig/settings/Fullscreen")
        if ($null -ne $fullscreenNode) {
            $fullscreenValue = $fullscreenNode.GetAttribute("value")
        }
    }

    $choiceByte = $null
    if (Test-Path -LiteralPath $Paths.FullscreenChoice) {
        $bytes = [System.IO.File]::ReadAllBytes($Paths.FullscreenChoice)
        if ($bytes.Length -gt 0) {
            $choiceByte = [int]$bytes[0]
        }
    }

    [pscustomobject]@{
        config_root = $Paths.Root
        user_config = $Paths.UserConfig
        user_config_exists = (Test-Path -LiteralPath $Paths.UserConfig)
        fullscreen_value = $fullscreenValue
        fullscreen_choice = $choiceByte
        fullscreen_choice_path = $Paths.FullscreenChoice
        fullscreen_choice_exists = (Test-Path -LiteralPath $Paths.FullscreenChoice)
        game_running = [bool](Get-Process -Name "forzahorizon6" -ErrorAction SilentlyContinue)
    }
}

function New-DisplayBackup {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [Parameter(Mandatory = $true)][string] $BackupRootPath
    )

    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupDir = Join-Path $BackupRootPath $timestamp
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    $manifest = [ordered]@{
        created_at = (Get-Date).ToString("o")
        config_root = $Paths.Root
        files = @()
    }

    foreach ($entry in @(
        @{ Source = $Paths.UserConfig; Name = "UserConfigSelections" },
        @{ Source = $Paths.FullscreenChoice; Name = "fullscreen_choice" }
    )) {
        if (Test-Path -LiteralPath $entry.Source) {
            $destination = Join-Path $backupDir $entry.Name
            Copy-Item -LiteralPath $entry.Source -Destination $destination -Force
            $manifest.files += [ordered]@{
                source = $entry.Source
                backup = $destination
            }
        }
    }

    $manifestPath = Join-Path $backupDir "manifest.json"
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    return $backupDir
}

function Set-FullscreenValue {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [Parameter(Mandatory = $true)][string] $Value
    )

    if (-not (Test-Path -LiteralPath $Paths.UserConfig)) {
        throw "UserConfigSelections not found: $($Paths.UserConfig)"
    }

    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($Paths.UserConfig)
    $fullscreenNode = $xml.SelectSingleNode("/UserConfig/settings/Fullscreen")
    if ($null -eq $fullscreenNode) {
        throw "Could not find /UserConfig/settings/Fullscreen in $($Paths.UserConfig)"
    }

    $fullscreenNode.SetAttribute("value", $Value)
    if (-not $DryRun) {
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
        $settings.Indent = $false
        $writer = [System.Xml.XmlWriter]::Create($Paths.UserConfig, $settings)
        try {
            $xml.Save($writer)
        }
        finally {
            $writer.Close()
        }
    }
}

function Set-FullscreenChoice {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [Parameter(Mandatory = $true)][byte] $Value
    )

    if (-not (Test-Path -LiteralPath $Paths.FullscreenChoice)) {
        throw "fullscreen_choice not found: $($Paths.FullscreenChoice)"
    }

    if (-not $DryRun) {
        [System.IO.File]::WriteAllBytes($Paths.FullscreenChoice, [byte[]]@($Value))
    }
}

function Restore-DisplayBackup {
    param(
        [Parameter(Mandatory = $true)][string] $BackupDir
    )

    $manifestPath = Join-Path $BackupDir "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Backup manifest not found: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in @($manifest.files)) {
        if (-not (Test-Path -LiteralPath $file.backup)) {
            throw "Backup file missing: $($file.backup)"
        }
        if ($DryRun) {
            Write-Host "[display-mode] would restore $($file.backup) -> $($file.source)"
        } else {
            Copy-Item -LiteralPath $file.backup -Destination $file.source -Force
            Write-Host "[display-mode] restored $($file.source)"
        }
    }
}

$paths = Get-DisplayConfigPaths -ConfigRoot $ForzaLocalDir
$backupRootPath = Resolve-WorkspacePath $BackupRoot

if ($Action -eq "Status") {
    Get-ForzaDisplayStatus -Paths $paths | ConvertTo-Json -Depth 4
    exit 0
}

if ($Action -eq "Restore") {
    $backupDir = $RestoreBackupDir
    if ([string]::IsNullOrWhiteSpace($backupDir)) {
        if (-not (Test-Path -LiteralPath $backupRootPath)) {
            throw "No backup root exists: $backupRootPath"
        }
        $backupDir = Get-ChildItem -LiteralPath $backupRootPath -Directory |
            Sort-Object Name -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($backupDir)) {
        throw "No backup directory found to restore."
    }
    Restore-DisplayBackup -BackupDir $backupDir
    Get-ForzaDisplayStatus -Paths $paths | ConvertTo-Json -Depth 4
    exit 0
}

$status = Get-ForzaDisplayStatus -Paths $paths
if ($status.game_running -and -not $Force) {
    throw "Forza Horizon 6 is running. Close it before changing display config, or use -Force if you know the game will not overwrite it."
}

if ($DryRun) {
    Write-Host "[display-mode] dry-run: no files will be modified"
} else {
    New-Item -ItemType Directory -Force -Path $backupRootPath | Out-Null
}

$backupDir = $null
if (-not $DryRun) {
    $backupDir = New-DisplayBackup -Paths $paths -BackupRootPath $backupRootPath
    Write-Host "[display-mode] backup $backupDir"
}

if ($Action -eq "Windowed") {
    Set-FullscreenValue -Paths $paths -Value "0"
    Set-FullscreenChoice -Paths $paths -Value 0
    Write-Host "[display-mode] set FH6 display mode to windowed"
} elseif ($Action -eq "Fullscreen") {
    Set-FullscreenValue -Paths $paths -Value "1"
    Set-FullscreenChoice -Paths $paths -Value 1
    Write-Host "[display-mode] set FH6 display mode to fullscreen"
}

$result = Get-ForzaDisplayStatus -Paths $paths
if ($backupDir) {
    $result | Add-Member -NotePropertyName backup_dir -NotePropertyValue $backupDir
}
$result | ConvertTo-Json -Depth 4

