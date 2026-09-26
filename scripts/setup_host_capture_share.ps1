[CmdletBinding()]
param(
    [string] $ShareName = "ForzaCapture",
    [string] $Path = "data/vm_share",
    [string] $FullAccessUser = "",
    [switch] $EnableFirewallRules,
    [switch] $Force
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $InputPath)

    if ([System.IO.Path]::IsPathRooted($InputPath)) {
        return $InputPath
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $InputPath))
}

function Test-ElevatedAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DefaultSwitchHostIp {
    $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceAlias -like "*Default Switch*" -and $_.IPAddress -notlike "169.254.*" } |
        Sort-Object InterfaceMetric, IPAddress |
        Select-Object -First 1
    if ($null -eq $ip) {
        return ""
    }
    return [string]$ip.IPAddress
}

if (-not (Test-ElevatedAdmin)) {
    throw "Run this script from an elevated PowerShell."
}

if ([string]::IsNullOrWhiteSpace($FullAccessUser)) {
    $FullAccessUser = "$env:COMPUTERNAME\$env:USERNAME"
}

$resolvedPath = Resolve-WorkspacePath $Path
New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedPath "rank_scans") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedPath "processed") | Out-Null

$existing = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if (-not $Force -and ([System.IO.Path]::GetFullPath($existing.Path) -ine $resolvedPath)) {
        throw "SMB share '$ShareName' already exists at '$($existing.Path)'. Pass -Force to replace it."
    }
    if ($Force) {
        Remove-SmbShare -Name $ShareName -Force
        $existing = $null
    }
}

if ($null -eq $existing) {
    New-SmbShare -Name $ShareName -Path $resolvedPath -FullAccess $FullAccessUser | Out-Null
} else {
    Grant-SmbShareAccess -Name $ShareName -AccountName $FullAccessUser -AccessRight Full -Force | Out-Null
}

if ($EnableFirewallRules) {
    Enable-NetFirewallRule -DisplayGroup "File and Printer Sharing" -ErrorAction SilentlyContinue | Out-Null
}

$hostIp = Get-DefaultSwitchHostIp
$uncByName = "\\$env:COMPUTERNAME\$ShareName"
$uncByIp = if ([string]::IsNullOrWhiteSpace($hostIp)) { "" } else { "\\$hostIp\$ShareName" }

$summary = [pscustomobject]@{
    share_name = $ShareName
    host_path = $resolvedPath
    full_access_user = $FullAccessUser
    unc_by_name = $uncByName
    unc_by_default_switch_ip = $uncByIp
    guest_map_command_by_name = "net use Z: $uncByName /user:$FullAccessUser"
    guest_map_command_by_ip = if ([string]::IsNullOrWhiteSpace($uncByIp)) { "" } else { "net use Z: $uncByIp /user:$FullAccessUser" }
}

$summaryPath = Join-Path $resolvedPath "share_info.json"
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "[capture-share] share: $uncByName"
if (-not [string]::IsNullOrWhiteSpace($uncByIp)) {
    Write-Host "[capture-share] default switch IP share: $uncByIp"
}
Write-Host "[capture-share] host path: $resolvedPath"
Write-Host "[capture-share] guest command:"
Write-Host "[capture-share]   $($summary.guest_map_command_by_name)"
if (-not [string]::IsNullOrWhiteSpace($summary.guest_map_command_by_ip)) {
    Write-Host "[capture-share]   $($summary.guest_map_command_by_ip)"
}
Write-Host "[capture-share] summary: $summaryPath"
