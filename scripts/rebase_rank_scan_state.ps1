param(
    [Parameter(Mandatory = $true)]
    [string] $StatePath,

    [string] $OldWorkspaceRoot = "",

    [Parameter(Mandatory = $true)]
    [string] $NewWorkspaceRoot,

    [string] $OutputPath = "",

    [switch] $InPlace
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

function Normalize-Root {
    param([Parameter(Mandatory = $true)][string] $Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    return $full.TrimEnd("\", "/")
}

function Convert-RebasedValue {
    param(
        [AllowNull()] $Value,
        [Parameter(Mandatory = $true)][string] $OldRoot,
        [Parameter(Mandatory = $true)][string] $NewRoot
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string]) {
        $text = [string]$Value
        if ($text.StartsWith($OldRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            return ($NewRoot + $text.Substring($OldRoot.Length))
        }
        return $text
    }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string]) -and -not ($Value -is [pscustomobject])) {
        $items = @()
        foreach ($item in $Value) {
            $items += (Convert-RebasedValue -Value $item -OldRoot $OldRoot -NewRoot $NewRoot)
        }
        return $items
    }

    if ($Value -is [pscustomobject]) {
        $object = [pscustomobject]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $object | Add-Member -NotePropertyName $property.Name -NotePropertyValue (Convert-RebasedValue -Value $property.Value -OldRoot $OldRoot -NewRoot $NewRoot)
        }
        return $object
    }

    return $Value
}

$resolvedState = Resolve-WorkspacePath $StatePath
if (-not (Test-Path -LiteralPath $resolvedState)) {
    throw "State file not found: $resolvedState"
}

if ([string]::IsNullOrWhiteSpace($OldWorkspaceRoot)) {
    $OldWorkspaceRoot = $workspace
}

$oldRoot = Normalize-Root $OldWorkspaceRoot
$newRoot = Normalize-Root $NewWorkspaceRoot
if ($oldRoot -ieq $newRoot) {
    throw "OldWorkspaceRoot and NewWorkspaceRoot are the same: $oldRoot"
}

$state = Get-Content -LiteralPath $resolvedState -Raw | ConvertFrom-Json
$rebased = Convert-RebasedValue -Value $state -OldRoot $oldRoot -NewRoot $newRoot
$rebased | Add-Member -NotePropertyName rebased_at -NotePropertyValue ((Get-Date).ToUniversalTime().ToString("o")) -Force
$rebased | Add-Member -NotePropertyName rebased_from_workspace -NotePropertyValue $oldRoot -Force
$rebased | Add-Member -NotePropertyName rebased_to_workspace -NotePropertyValue $newRoot -Force

if ($InPlace) {
    $backup = "$resolvedState.bak_$(Get-Date -Format yyyyMMdd_HHmmss)"
    Copy-Item -LiteralPath $resolvedState -Destination $backup -Force
    $resolvedOutput = $resolvedState
    Write-Host "[state-rebase] backup: $backup"
} elseif (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = Resolve-WorkspacePath $OutputPath
} else {
    $resolvedOutput = Join-Path (Split-Path -Parent $resolvedState) "state.rebased.json"
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
$rebased | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8

Write-Host "[state-rebase] old workspace: $oldRoot"
Write-Host "[state-rebase] new workspace: $newRoot"
Write-Host "[state-rebase] output: $resolvedOutput"
