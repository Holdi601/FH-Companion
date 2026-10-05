[CmdletBinding()]
param(
    [string] $VMName = "ForzaScrapeVM",
    [string] $VMUser = ".\admin",
    [string] $GameAppId = "2483190",
    [string] $StatePath = (Join-Path $PSScriptRoot "..\data\vm_share\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json"),
    [switch] $SkipRapidOcrGpuProbe
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Write-Check {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-preflight] $Message"
}

function New-Result {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][bool] $Ok,
        [AllowNull()][object] $Value = $null,
        [string] $Details = ""
    )
    [pscustomobject]@{
        name = $Name
        ok = $Ok
        value = $Value
        details = $Details
    }
}

function Invoke-PythonJson {
    param([Parameter(Mandatory = $true)][string] $Code)
    Push-Location $workspace
    try {
        $output = $Code | python -
    }
    finally {
        Pop-Location
    }
    $lines = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($lines.Count -eq 0) {
        throw "Python command returned no JSON."
    }
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $line = ([string]$lines[$i]).Trim()
        if ($line.StartsWith("{") -or $line.StartsWith("[")) {
            return $line | ConvertFrom-Json
        }
    }
    throw "Python command returned no JSON object. Output: $($lines -join ' | ')"
}

$results = @()

Write-Check "check host Python modules"
$hostPython = Invoke-PythonJson @'
import importlib.util, json, sys
mods = ["cv2", "pandas", "pyarrow", "rapidocr", "onnxruntime", "numpy", "PIL"]
out = {"python": sys.version, "modules": {}}
for name in mods:
    spec = importlib.util.find_spec(name)
    info = {"found": bool(spec)}
    if spec:
        try:
            mod = __import__(name)
            info["version"] = getattr(mod, "__version__", None)
            if name == "onnxruntime":
                info["providers"] = mod.get_available_providers()
        except Exception as exc:
            info["error"] = f"{type(exc).__name__}: {exc}"
    out["modules"][name] = info
print(json.dumps(out))
'@

foreach ($module in @("cv2", "pandas", "pyarrow", "rapidocr", "onnxruntime", "numpy", "PIL")) {
    $info = $hostPython.modules.$module
    $results += New-Result -Name "host.module.$module" -Ok ([bool]$info.found -and -not $info.error) -Value $info
}
$providers = @($hostPython.modules.onnxruntime.providers)
$results += New-Result -Name "host.onnxruntime.cuda_provider" -Ok ($providers -contains "CUDAExecutionProvider") -Value $providers
$results += New-Result -Name "host.python.version" -Ok $true -Value $hostPython.python

if (-not $SkipRapidOcrGpuProbe) {
    Write-Check "probe RapidOCR GPU provider"
    $rapidProbe = Invoke-PythonJson @'
import json
from scripts.extract_leaderboard import create_rapidocr_engine, get_rapidocr_providers
try:
    engine = create_rapidocr_engine(use_gpu=True)
    providers = get_rapidocr_providers(engine)
    ok = all("CUDAExecutionProvider" in provider_list for _name, provider_list in providers)
    print(json.dumps({"ok": ok, "providers": providers}))
except Exception as exc:
    print(json.dumps({"ok": False, "error": f"{type(exc).__name__}: {exc}"}))
'@
    $results += New-Result -Name "host.rapidocr_gpu_probe" -Ok ([bool]$rapidProbe.ok) -Value $rapidProbe
}

Write-Check "check extractor entrypoints"
foreach ($script in @(
    "scripts\run_host_extract_from_vm_captures.ps1",
    "scripts\run_leaderboard_rank_scan.ps1",
    "scripts\run_capture_automation.ps1",
    "scripts\start_vm_capture_resume.ps1",
    "scripts\setup_vm_direct_bootstrap.ps1",
    "scripts\setup_vm_steam_shortcuts.ps1",
    "scripts\start_vm_steam.ps1",
    "scripts\start_vm_forza.ps1"
)) {
    $path = Join-Path $workspace $script
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors) | Out-Null
    $results += New-Result -Name "script.parse.$script" -Ok ($errors.Count -eq 0) -Value $path -Details (($errors | ForEach-Object { $_.Message }) -join " | ")
}

$profilePath = Join-Path $workspace "config\fh6_rivals_1080p.json"
$results += New-Result -Name "host.profile.1080p" -Ok (Test-Path -LiteralPath $profilePath) -Value $profilePath
$results += New-Result -Name "host.resume_state" -Ok (Test-Path -LiteralPath $StatePath) -Value $StatePath
if (Test-Path -LiteralPath $StatePath) {
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $results += New-Result -Name "host.resume_state.summary" -Ok $true -Value ([pscustomobject]@{
        track = $state.track
        pi_class = $state.pi_class
        rivals_mode = $state.rivals_mode
        total_ranks = $state.total_ranks
        next_rank = $state.next_rank
        max_scanned_rank = $state.max_scanned_rank
        rows_per_screenshot = $state.rows_per_screenshot
    })
}

Write-Check "check Hyper-V and guest state"
$vm = Get-VM -Name $VMName -ErrorAction Stop
$results += New-Result -Name "vm.running" -Ok ($vm.State -eq "Running") -Value ([pscustomobject]@{
    state = [string]$vm.State
    memory_assigned = $vm.MemoryAssigned
    processor_count = $vm.ProcessorCount
    uptime = [string]$vm.Uptime
})

$video = Get-VMVideo -VMName $VMName
$results += New-Result -Name "vm.video.1920x1080" -Ok ($video.HorizontalResolution -eq 1920 -and $video.VerticalResolution -eq 1080) -Value ([pscustomobject]@{
    resolution_type = [string]$video.ResolutionType
    horizontal = $video.HorizontalResolution
    vertical = $video.VerticalResolution
})

$gpuAdapters = @(Get-VMGpuPartitionAdapter -VMName $VMName -ErrorAction SilentlyContinue)
$results += New-Result -Name "vm.gpu_partition" -Ok ($gpuAdapters.Count -gt 0) -Value ($gpuAdapters | Select-Object InstancePath, OptimalPartitionVRAM, OptimalPartitionDecode, OptimalPartitionCompute)

$credential = [pscredential]::new($VMUser, [Security.SecureString]::new())
$guest = Invoke-Command -VMName $VMName -Credential $credential -ArgumentList $GameAppId -ScriptBlock {
    param([string] $GameAppId)
    $ErrorActionPreference = "Continue"

    function Read-AcfValue {
        param(
            [Parameter(Mandatory = $true)][string] $Path,
            [Parameter(Mandatory = $true)][string] $Key
        )
        if (-not (Test-Path -LiteralPath $Path)) {
            return $null
        }
        $pattern = '"' + [regex]::Escape($Key) + '"\s+"(.*)"'
        $line = Get-Content -LiteralPath $Path | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($line -and $line -match $pattern) {
            return $Matches[1]
        }
        return $null
    }

    & net.exe use Z: "\\172.17.128.1\ForzaCapture" /persistent:yes | Out-Null
    & net.exe use Y: "\\172.17.128.1\ForzaRepo" /persistent:yes | Out-Null

    $steamRoot = "C:\Program Files (x86)\Steam"
    $steamExe = Join-Path $steamRoot "steam.exe"
    $manifest = Join-Path $steamRoot "steamapps\appmanifest_$GameAppId.acf"
    $installDir = Read-AcfValue -Path $manifest -Key "installdir"
    $appName = Read-AcfValue -Path $manifest -Key "name"
    $stateFlags = Read-AcfValue -Path $manifest -Key "StateFlags"
    $sizeOnDisk = Read-AcfValue -Path $manifest -Key "SizeOnDisk"
    $gameDir = if ($installDir) { Join-Path (Join-Path $steamRoot "steamapps\common") $installDir } else { $null }
    $gameExe = if ($gameDir) { Join-Path $gameDir "forzahorizon6.exe" } else { $null }
    $dpi = Get-ItemProperty "HKCU:\Control Panel\Desktop" -ErrorAction SilentlyContinue
    $video = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM)
    $pnpDisplay = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Select-Object FriendlyName, Status, Problem, ProblemCode, InstanceId)
    $tasks = @(Get-ScheduledTask -TaskName "ForzaCaptureResume", "ForzaLaunchSteam", "ForzaRunGame" -ErrorAction SilentlyContinue | Select-Object TaskName, State)
    $timeouts = [pscustomobject]@{
        monitor_ac = ((powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE) -join "`n")
        standby_ac = ((powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE) -join "`n")
    }

    [pscustomobject]@{
        computer = $env:COMPUTERNAME
        user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        steam_exe = $steamExe
        steam_exists = (Test-Path -LiteralPath $steamExe)
        appmanifest = $manifest
        appmanifest_exists = (Test-Path -LiteralPath $manifest)
        app_name = $appName
        state_flags = $stateFlags
        size_on_disk = $sizeOnDisk
        game_dir = $gameDir
        game_dir_exists = if ($gameDir) { Test-Path -LiteralPath $gameDir } else { $false }
        game_exe = $gameExe
        game_exe_exists = if ($gameExe) { Test-Path -LiteralPath $gameExe } else { $false }
        z_access = (Test-Path "Z:\")
        y_access = (Test-Path "Y:\scripts\run_leaderboard_rank_scan.ps1")
        state_access = (Test-Path "Z:\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json")
        capture_shortcut = (Test-Path "C:\Users\admin\Desktop\Run Forza Capture Resume.cmd")
        run_game_shortcut = (Test-Path "C:\Users\admin\Desktop\Run Forza Horizon 6.cmd")
        install_game_shortcut = (Test-Path "C:\Users\admin\Desktop\Install Forza Horizon 6.cmd")
        win8_dpi_scaling = $dpi.Win8DpiScaling
        log_pixels = $dpi.LogPixels
        video = $video
        pnp_display = $pnpDisplay
        tasks = $tasks
        power = $timeouts
    }
} -ErrorAction Stop

$results += New-Result -Name "guest.steam_installed" -Ok ([bool]$guest.steam_exists) -Value $guest.steam_exe
$results += New-Result -Name "guest.forza_manifest" -Ok ([bool]$guest.appmanifest_exists) -Value ([pscustomobject]@{
    app_name = $guest.app_name
    manifest = $guest.appmanifest
    state_flags = $guest.state_flags
    size_on_disk = $guest.size_on_disk
})
$results += New-Result -Name "guest.forza_installed_state" -Ok ($guest.state_flags -eq "4" -and [bool]$guest.game_exe_exists) -Value ([pscustomobject]@{
    game_dir = $guest.game_dir
    game_exe = $guest.game_exe
    game_dir_exists = $guest.game_dir_exists
    game_exe_exists = $guest.game_exe_exists
})
$results += New-Result -Name "guest.shares" -Ok ([bool]$guest.z_access -and [bool]$guest.y_access -and [bool]$guest.state_access) -Value ([pscustomobject]@{
    z_access = $guest.z_access
    y_access = $guest.y_access
    state_access = $guest.state_access
})
$results += New-Result -Name "guest.shortcuts" -Ok ([bool]$guest.capture_shortcut -and [bool]$guest.run_game_shortcut -and [bool]$guest.install_game_shortcut) -Value ([pscustomobject]@{
    capture = $guest.capture_shortcut
    run_game = $guest.run_game_shortcut
    install_game = $guest.install_game_shortcut
})
$taskNames = @($guest.tasks | ForEach-Object { $_.TaskName })
$results += New-Result -Name "guest.scheduled_tasks" -Ok ($taskNames -contains "ForzaCaptureResume" -and $taskNames -contains "ForzaLaunchSteam" -and $taskNames -contains "ForzaRunGame") -Value $guest.tasks
$results += New-Result -Name "guest.display_scaling_default" -Ok (($null -eq $guest.log_pixels -or [int]$guest.log_pixels -eq 96) -and ([int]$guest.win8_dpi_scaling -eq 0)) -Value ([pscustomobject]@{
    win8_dpi_scaling = $guest.win8_dpi_scaling
    log_pixels = $guest.log_pixels
})
$guestGpuNames = @($guest.video | ForEach-Object { $_.Name })
$results += New-Result -Name "guest.gpu_visible" -Ok (($guestGpuNames -join "|") -match "NVIDIA") -Value $guest.video
$nvidiaPnp = @($guest.pnp_display | Where-Object { $_.FriendlyName -match "NVIDIA" })
$results += New-Result -Name "guest.gpu_pnp_ok" -Ok (@($nvidiaPnp | Where-Object { $_.Status -eq "OK" -and ([string]$_.Problem -eq "CM_PROB_NONE" -or [string]::IsNullOrWhiteSpace([string]$_.Problem)) }).Count -gt 0) -Value $guest.pnp_display

$ok = @($results | Where-Object { -not $_.ok }).Count -eq 0
$report = [pscustomobject]@{
    ok = $ok
    checked_at = (Get-Date).ToUniversalTime().ToString("o")
    results = $results
}

$reportDir = Join-Path $workspace "data\background"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportPath = Join-Path $reportDir ("vm_pipeline_preflight_{0}.json" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8

$results |
    Select-Object @{Name="Status";Expression={ if ($_.ok) { "OK" } else { "FAIL" } }}, name, @{Name="Value";Expression={ if ($_.value -is [string]) { $_.value } else { ($_.value | ConvertTo-Json -Compress -Depth 5) } }} |
    Format-Table -AutoSize

Write-Check "report: $reportPath"
if (-not $ok) {
    throw "Preflight failed. See $reportPath"
}
