$ErrorActionPreference = "Stop"

Write-Host "Start menu apps matching Forza/Horizon/Xbox/Steam:" -ForegroundColor Cyan
Get-StartApps |
    Where-Object { $_.Name -match "Forza|Horizon|Xbox|Steam" -or $_.AppID -match "Forza|Horizon|Xbox|Steam" } |
    Sort-Object Name |
    Format-Table Name, AppID -AutoSize

Write-Host ""
Write-Host "Installed AppX packages matching Forza/Horizon:" -ForegroundColor Cyan
Get-AppxPackage |
    Where-Object { $_.Name -match "Forza|Horizon" -or $_.PackageFamilyName -match "Forza|Horizon" } |
    Sort-Object Name |
    Select-Object Name, PackageFamilyName, InstallLocation |
    Format-Table -AutoSize

Write-Host ""
Write-Host "Running processes matching Forza/Horizon:" -ForegroundColor Cyan
Get-Process |
    Where-Object { $_.ProcessName -match "Forza|Horizon" -or $_.MainWindowTitle -match "Forza|Horizon" } |
    Select-Object ProcessName, Id, MainWindowTitle |
    Format-Table -AutoSize

