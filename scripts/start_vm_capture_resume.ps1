[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $TaskName = "ForzaCaptureResume",
    [string] $StatePath = (Join-Path $PSScriptRoot "..\data\vm_share\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json"),
    [switch] $StartHostExtractor,
    [switch] $NoElevate
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-CanAccessVm {
    param([Parameter(Mandatory = $true)][string] $Name)
    try {
        Get-VM -Name $Name -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

function ConvertTo-ArgumentList {
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-VMUser", "`"$VMUser`"",
        "-TaskName", "`"$TaskName`"",
        "-StatePath", "`"$StatePath`"",
        "-NoElevate"
    )
    if ($StartHostExtractor) {
        $args += "-StartHostExtractor"
    }
    return $args
}

function Write-Start {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-capture-start] $Message"
}

if (-not $NoElevate -and -not (Test-IsAdmin) -and -not (Test-CanAccessVm -Name $VMName)) {
    Write-Start "relaunch elevated for Hyper-V access"
    Start-Process powershell -Verb RunAs -ArgumentList (ConvertTo-ArgumentList)
    return
}

$vm = Get-VM -Name $VMName -ErrorAction Stop
if ($vm.State -ne "Running") {
    Write-Start "start VM $VMName"
    Start-VM -Name $VMName
}

Write-Start "wait for PowerShell Direct"
$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 2
    }
}
if (-not $ready) {
    throw "PowerShell Direct did not become ready for $VMName."
}

Write-Start "start interactive VM task $TaskName"
Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $TaskName -ScriptBlock {
    param([string] $TaskName)
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName | Select-Object TaskName, State
} -ErrorAction Stop

if ($StartHostExtractor) {
    $extractor = Join-Path $PSScriptRoot "run_host_extract_from_vm_captures.ps1"
    Write-Start "start host extractor watcher"
    Start-Process powershell -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$extractor`"",
        "-StatePath", "`"$StatePath`"",
        "-Watch"
    ) -WindowStyle Minimized
}

Write-Start "done"
