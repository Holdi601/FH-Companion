<#
.SYNOPSIS
    One capture chunk: scroll and film for N seconds, hand back a zip of frames.

.DESCRIPTION
    The unit the OCR sweep works in. Pressing and filming run as two separate guest
    tasks so neither blocks the other, the frames are zipped in the guest and copied
    once, and the guest copy is deleted straight away -- a chunk is ~350 MB and a night
    of them would fill the disk.

.EXAMPLE
    .\capture_one_chunk.ps1 -Seconds 120 -OutZip C:\...\chunk01.zip
#>
[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [int] $Seconds = 120,
    [int] $IntervalMs = 45,
    [Parameter(Mandatory = $true)][string] $OutZip
)

$ErrorActionPreference = "Stop"
$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$session = New-PSSession -VMName $VMName -Credential $credential
try {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmssfff"
    $result = Invoke-Command -Session $session -ArgumentList $stamp, $Seconds, $IntervalMs -ScriptBlock {
        param($Stamp, $Seconds, $IntervalMs)
        $dir = "C:\ForzaAutomation\data\frames\$Stamp"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        foreach ($job in @(
            @{ n = "ChunkPress_$Stamp"
               a = "-NoProfile -ExecutionPolicy Bypass -File `"C:\ForzaAutomation\scripts\press_down_continuously.ps1`" -Seconds $Seconds" },
            @{ n = "ChunkFrames_$Stamp"
               a = "-NoProfile -ExecutionPolicy Bypass -File `"C:\ForzaAutomation\scripts\capture_leaderboard_frames.ps1`" -OutDir `"$dir`" -Seconds $Seconds -IntervalMs $IntervalMs -X 78 -Y 268 -Width 1700 -Height 610" })) {
            $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $job.a
            $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\admin" -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName $job.n -Action $action -Principal $principal -Force | Out-Null
            Start-ScheduledTask -TaskName $job.n
        }
        Start-Sleep -Seconds ($Seconds + 6)
        Get-ScheduledTask -TaskName "ChunkPress_$Stamp", "ChunkFrames_$Stamp" -ErrorAction SilentlyContinue |
            ForEach-Object {
                Stop-ScheduledTask -TaskName $_.TaskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
            }
        $files = @(Get-ChildItem $dir -Filter *.png -ErrorAction SilentlyContinue)
        $zip = "C:\ForzaAutomation\data\frames\$Stamp.zip"
        if ($files.Count -gt 0) {
            Compress-Archive -Path (Join-Path $dir '*.png') -DestinationPath $zip -CompressionLevel Fastest
        }
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
        [pscustomobject]@{ frames = $files.Count; zip = $zip }
    }

    if ($result.frames -gt 0) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutZip) | Out-Null
        Copy-Item -FromSession $session -LiteralPath $result.zip -Destination $OutZip -Force
        Invoke-Command -Session $session -ArgumentList $result.zip -ScriptBlock {
            param($Zip) Remove-Item -Force $Zip -ErrorAction SilentlyContinue
        }
        Write-Host "[chunk] $($result.frames) frames -> $OutZip"
    } else {
        Write-Host "[chunk] 0 frames"
    }
} finally {
    Remove-PSSession $session -ErrorAction SilentlyContinue
}
