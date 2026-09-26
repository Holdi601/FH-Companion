[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [int] $Width = 1920,
    [int] $Height = 1080,
    [int] $BitsPerPixel = 32,
    [switch] $NoElevate
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Resolution {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[vm-resolution-interactive] $Message"
}

if (-not $NoElevate -and -not (Test-IsAdmin)) {
    Write-Resolution "relaunch elevated for Hyper-V access"
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-VMName", "`"$VMName`"",
        "-VMUser", "`"$VMUser`"",
        "-Width", $Width,
        "-Height", $Height,
        "-BitsPerPixel", $BitsPerPixel,
        "-NoElevate"
    )
    return
}

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())

$guestSetup = {
    param(
        [int] $Width,
        [int] $Height,
        [int] $BitsPerPixel
    )

    $ErrorActionPreference = "Stop"
    $scriptPath = "C:\Users\admin\Documents\SetGuestResolution.ps1"
    $resultPath = "C:\Users\admin\Documents\SetGuestResolution.result.json"
    $scriptContent = @'
param(
    [int] $Width = 1920,
    [int] $Height = 1080,
    [int] $BitsPerPixel = 32,
    [string] $ResultPath = "C:\Users\admin\Documents\SetGuestResolution.result.json"
)

$ErrorActionPreference = "Stop"
Add-Type @"
using System;
using System.Runtime.InteropServices;

public class DisplayModeToolsInteractive {
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

$dm = New-Object DisplayModeToolsInteractive+DEVMODE
$dm.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($dm)
[DisplayModeToolsInteractive]::EnumDisplaySettings($null, -1, [ref]$dm) | Out-Null
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
$result = [DisplayModeToolsInteractive]::ChangeDisplaySettings([ref]$dm, 0x00000001)

$afterMode = New-Object DisplayModeToolsInteractive+DEVMODE
$afterMode.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($afterMode)
[DisplayModeToolsInteractive]::EnumDisplaySettings($null, -1, [ref]$afterMode) | Out-Null

[pscustomobject]@{
    result = $result
    before = $before
    after = [pscustomobject]@{
        width = $afterMode.dmPelsWidth
        height = $afterMode.dmPelsHeight
        bits_per_pixel = $afterMode.dmBitsPerPel
        frequency = $afterMode.dmDisplayFrequency
    }
    video = @(Get-CimInstance Win32_VideoController | Select-Object Name, CurrentHorizontalResolution, CurrentVerticalResolution)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
'@

    $scriptContent | Set-Content -LiteralPath $scriptPath -Encoding UTF8
    Remove-Item -LiteralPath $resultPath -ErrorAction SilentlyContinue

    $taskName = "ForzaSetGuestResolution"
    $argument = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Width $Width -Height $Height -BitsPerPixel $BitsPerPixel -ResultPath `"$resultPath`""
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $argument
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    [pscustomobject]@{ task = $taskName; script = $scriptPath; result = $resultPath }
}

Write-Resolution "schedule interactive resolution change to ${Width}x${Height}"
$setup = Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock $guestSetup -ArgumentList $Width, $Height, $BitsPerPixel -ErrorAction Stop
$setup

Write-Resolution "wait for result"
$guestRead = {
    param([string] $ResultPath)
    for ($i = 0; $i -lt 30; $i++) {
        if (Test-Path -LiteralPath $ResultPath) {
            return Get-Content -LiteralPath $ResultPath -Raw
        }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for $ResultPath."
}
Invoke-Command -VMName $VMName -Credential $credential -ScriptBlock $guestRead -ArgumentList ([string]$setup.result) -ErrorAction Stop
