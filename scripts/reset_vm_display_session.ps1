[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [int] $Width = 1920,
    [int] $Height = 1080,
    [int] $BitsPerPixel = 32,
    [int] $GracefulStopSeconds = 45,
    [switch] $KeepEnhancedSession,
    [switch] $NoStart
)

$ErrorActionPreference = "Stop"

function Write-Reset {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-display-reset] $Message"
}

function New-BlankCredential {
    param([Parameter(Mandatory = $true)][string] $User)
    if ($User -notmatch '^[^\\]+\\' -and $User -notmatch '@') {
        $User = ".\$User"
    }
    return [pscredential]::new($User, [Security.SecureString]::new())
}

function Wait-PowerShellDirect {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Credential
    )

    for ($i = 0; $i -lt 90; $i += 1) {
        try {
            Invoke-Command -VMName $Name -Credential $Credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null
            return
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "PowerShell Direct did not become ready for $Name."
}

if (-not (Get-VM -Name $VMName -ErrorAction SilentlyContinue)) {
    throw "VM not found: $VMName"
}

$credential = New-BlankCredential -User $VMUser
$vm = Get-VM -Name $VMName -ErrorAction Stop

if ($vm.State -eq "Running") {
    Write-Reset "ask guest to stop Forza/Steam foreground processes"
    try {
        Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
            Get-Process -Name forzahorizon6,steamwebhelper,GameBar,GameBarFTServer -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
        } -ErrorAction Stop
    } catch {
        Write-Warning "Guest process cleanup skipped: $($_.Exception.Message)"
    }

    Write-Reset "stop VM $VMName"
    try {
        Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock {
            & "$env:SystemRoot\System32\shutdown.exe" /s /t 0 /f
        } -ErrorAction Stop | Out-Null
    } catch {
        Write-Warning "Guest shutdown request failed: $($_.Exception.Message)"
    }
    $deadline = (Get-Date).AddSeconds($GracefulStopSeconds)
    do {
        Start-Sleep -Seconds 2
        $vm = Get-VM -Name $VMName
    } while ($vm.State -ne "Off" -and (Get-Date) -lt $deadline)

    if ($vm.State -ne "Off") {
        Write-Reset "force stop VM $VMName"
        Stop-VM -Name $VMName -Force
    }
}

if (-not $KeepEnhancedSession) {
    Write-Reset "set console transport to VMBus"
    Set-VM -Name $VMName -EnhancedSessionTransportType VMBus
}

Write-Reset "set VM console video to ${Width}x${Height}"
Set-VMVideo -VMName $VMName -ResolutionType Single -HorizontalResolution $Width -VerticalResolution $Height

if ($NoStart) {
    Get-VM -Name $VMName | Select-Object Name, State, EnhancedSessionTransportType
    Get-VMVideo -VMName $VMName
    return
}

Write-Reset "start VM $VMName"
Start-VM -Name $VMName

Write-Reset "wait for PowerShell Direct"
Wait-PowerShellDirect -Name $VMName -Credential $credential

Write-Reset "set interactive guest display mode"
$guestResult = Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $Width, $Height, $BitsPerPixel -ScriptBlock {
    param(
        [int] $Width,
        [int] $Height,
        [int] $BitsPerPixel
    )

    $ErrorActionPreference = "Stop"

    Set-ItemProperty -Path "HKCU:\Control Panel\Desktop" -Name "Win8DpiScaling" -Value 0 -ErrorAction SilentlyContinue
    Set-ItemProperty -Path "HKCU:\Control Panel\Desktop" -Name "LogPixels" -Value 96 -ErrorAction SilentlyContinue

    Add-Type @"
using System;
using System.Runtime.InteropServices;

public class ForzaDisplayModeTools {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);
}
"@

    $dm = New-Object ForzaDisplayModeTools+DEVMODE
    $dm.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($dm)
    [ForzaDisplayModeTools]::EnumDisplaySettings($null, -1, [ref]$dm) | Out-Null
    $before = [pscustomobject]@{
        width = $dm.dmPelsWidth
        height = $dm.dmPelsHeight
        bits_per_pixel = $dm.dmBitsPerPel
        frequency = $dm.dmDisplayFrequency
    }

    $dm.dmPelsWidth = $Width
    $dm.dmPelsHeight = $Height
    $dm.dmBitsPerPel = $BitsPerPixel
    $dm.dmFields = 0x00080000 -bor 0x00100000 -bor 0x00040000
    $result = [ForzaDisplayModeTools]::ChangeDisplaySettings([ref]$dm, 0x00000001)

    $afterMode = New-Object ForzaDisplayModeTools+DEVMODE
    $afterMode.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($afterMode)
    [ForzaDisplayModeTools]::EnumDisplaySettings($null, -1, [ref]$afterMode) | Out-Null

    [pscustomobject]@{
        change_display_result = $result
        before = $before
        after = [pscustomobject]@{
            width = $afterMode.dmPelsWidth
            height = $afterMode.dmPelsHeight
            bits_per_pixel = $afterMode.dmBitsPerPel
            frequency = $afterMode.dmDisplayFrequency
        }
        session = ((quser 2>$null) -join "`n")
        video = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, Status)
        pnp_display = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Select-Object FriendlyName, Status, Problem)
    }
} -ErrorAction Stop

[pscustomobject]@{
    vm = Get-VM -Name $VMName | Select-Object Name, State, EnhancedSessionTransportType
    vm_video = Get-VMVideo -VMName $VMName | Select-Object ResolutionType, HorizontalResolution, VerticalResolution
    guest = $guestResult
} | ConvertTo-Json -Depth 8
