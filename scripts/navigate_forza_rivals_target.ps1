[CmdletBinding()]
param(
    [string] $RouteConfig = "config/fh6_learned_route.json",
    [Parameter(Mandatory = $true)]
    [string] $Track,
    [string] $PerformanceClass = "D",
    [string] $RivalsMode = "Road Racing",
    [int] $StartupWaitSeconds = 90,
    [int] $MaxTracks = 64,
    [string] $RuntimeRoot = "data/runtime/memory_navigation"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$classOrder = @("D", "C", "B", "A", "S1", "S2", "R", "X")
$categoryPath = @("Road Racing", "Cross-Country", "Street Racing", "Touge", "Drag Racing", "Dirt Racing")
$categoryMoveKeys = @{
    "Road Racing->Cross-Country" = @("RIGHT")
    "Cross-Country->Street Racing" = @("RIGHT")
    "Street Racing->Touge" = @("DOWN")
    "Touge->Drag Racing" = @("LEFT")
    "Drag Racing->Dirt Racing" = @("LEFT")
    "Dirt Racing->Road Racing" = @("UP")
}
$leaderboardPattern = "(?m)^(Driver|Drivetrain)\s*$|Filter:\s+Global|Player\s+options"

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Navigation {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-target] $Message"
}

function Ensure-Property {
    param($Object, [string] $Name, $Value)
    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Set-NestedProperty {
    param($Object, [string] $Parent, [string] $Name, $Value)
    if (-not ($Object.PSObject.Properties.Name -contains $Parent) -or $null -eq $Object.$Parent) {
        $Object | Add-Member -NotePropertyName $Parent -NotePropertyValue ([pscustomobject]@{})
    }
    Ensure-Property -Object $Object.$Parent -Name $Name -Value $Value
}

function New-KeyStep {
    param([string] $Key, [int] $Count = 1, [int] $DelayMs = 350)
    return [pscustomobject]@{
        action = "key"
        key = $Key
        count = $Count
        delay_ms = $DelayMs
    }
}

function New-WaitStep {
    param([string] $Pattern, [string] $Name, [int] $TimeoutSeconds = 45)
    return [pscustomobject]@{
        action = "wait_for_text"
        pattern = $Pattern
        timeout_seconds = $TimeoutSeconds
        interval_seconds = 1
        name = $Name
    }
}

function Normalize-Name {
    param([AllowEmptyString()][string] $Value)
    return (($Value.ToLowerInvariant() -replace "[^a-z0-9]+", " ").Trim())
}

function Test-TrackMatch {
    param([string] $Actual, [string] $Expected)
    $left = Normalize-Name $Actual
    $right = Normalize-Name $Expected
    return $left -eq $right -or $left.Contains($right) -or $right.Contains($left)
}

function Parse-ClassContext {
    param([AllowEmptyString()][string] $Text)
    $lines = @(
        $Text -split "\r?\n" |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $trackName = ""
    $className = ""
    for ($index = 0; $index -lt $lines.Count; $index += 1) {
        if ($lines[$index] -match "['""]?\s*(D|C|B|A|S1|S2|R|X)\s*['""]?\s+Performance\s+Class") {
            $className = $matches[1].ToUpperInvariant()
            if ($index -gt 0) {
                $trackName = $lines[$index - 1]
            }
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($trackName)) {
        for ($index = 0; $index -lt $lines.Count; $index += 1) {
            if ($lines[$index] -match "^Route\s+Length:" -and $index -gt 0) {
                $trackName = $lines[$index - 1]
                break
            }
        }
    }
    return [pscustomobject]@{
        track = $trackName
        performance_class = $className
        text = $Text
    }
}

$routePath = Resolve-WorkspacePath $RouteConfig
$runtimePath = Resolve-WorkspacePath $RuntimeRoot
New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null
$runId = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$stepIndex = 0

function Invoke-Steps {
    param(
        [Parameter(Mandatory = $true)] $Steps,
        [Parameter(Mandatory = $true)][string] $Name
    )
    $script:stepIndex += 1
    $config = Get-Content -LiteralPath $routePath -Raw | ConvertFrom-Json
    Set-NestedProperty $config "display" "manage" $false
    Set-NestedProperty $config "display" "restore_after" $false
    Set-NestedProperty $config "launch" "startup_wait_seconds" 0
    Set-NestedProperty $config "capture" "clear_output_dir" $true
    Set-NestedProperty $config "capture" "dump_state_ocr" $true
    $safeName = $Name -replace "[^A-Za-z0-9_-]+", "_"
    $outputDir = Join-Path $runtimePath ("{0}_{1:000}_{2}" -f $runId, $script:stepIndex, $safeName)
    $stateDir = Join-Path $outputDir "_state"
    Set-NestedProperty $config "capture" "output_dir" $outputDir
    Set-NestedProperty $config "capture" "state_dir" $stateDir
    Set-NestedProperty $config "capture" "prefix" $safeName
    Set-NestedProperty $config "extraction" "enabled" $false
    $config.steps = @($Steps)
    $configPath = Join-Path $runtimePath ("nav_{0}_{1:000}_{2}.json" -f $runId, $script:stepIndex, $safeName)
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding UTF8

    Write-Navigation $Name
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run_capture_automation.ps1") `
        -Config $configPath -SkipLaunch -SkipExtract |
        ForEach-Object { Write-Host $_ }
    $stepExitCode = $LASTEXITCODE
    if ($stepExitCode -ne 0) {
        throw "Navigation step '$Name' failed with exit code $stepExitCode."
    }
    return $stateDir
}

function Get-OcrText {
    param([string] $StateDir)
    $file = Get-ChildItem -LiteralPath $StateDir -Filter "*.ocr.txt" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $file) {
        return ""
    }
    return Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
}

$normalizedClass = $PerformanceClass.Trim().ToUpperInvariant()
$classIndex = [array]::IndexOf($classOrder, $normalizedClass)
if ($classIndex -lt 0) {
    throw "Unsupported performance class '$PerformanceClass'. Use: $($classOrder -join ', ')."
}
$modeIndex = [array]::IndexOf($categoryPath, $RivalsMode)
if ($modeIndex -lt 0) {
    throw "Unsupported Rivals mode '$RivalsMode'. Use: $($categoryPath -join ', ')."
}

Write-Navigation "launch and navigate to the learned Road Racing anchor leaderboard"
$anchorRoutePath = Join-Path $runtimePath ("anchor_route_{0}.json" -f $runId)
$anchorRoute = Get-Content -LiteralPath $routePath -Raw | ConvertFrom-Json
for ($index = 0; $index -lt ($anchorRoute.steps.Count - 1); $index += 1) {
    $step = $anchorRoute.steps[$index]
    $next = $anchorRoute.steps[$index + 1]
    if (
        $step.action -eq "wait_for_text" -and
        (
            $step.pattern.Contains("Enter\s+House") -or
            $step.pattern.Contains("Vision\s+House") -or
            $step.pattern.Contains("OWNED")
        )
    ) {
        $anchorRoute.steps[$index] = [pscustomobject]@{
            action = "wait"
            seconds = 8
            label = "wait for free roam after leaving the garage"
        }
        continue
    }
    if (
        $step.action -eq "key" -and
        $step.key -eq "Y" -and
        $next.action -eq "wait_for_text" -and
        $next.pattern.Contains("Driver")
    ) {
        $anchorRoute.steps[$index] = [pscustomobject]@{
            action = "key_until_text"
            key = "Y"
            pattern = $leaderboardPattern
            max_presses = 5
            delay_ms = 1200
            name = "open_anchor_leaderboard_{index:000}.png"
        }
        continue
    }
    if (
        $step.action -eq "wait_for_text" -and
        $step.pattern.Contains("Driver")
    ) {
        $step.pattern = $leaderboardPattern
        $anchorRoute.steps[$index] = $step
        continue
    }
    if (
        $step.action -eq "key" -and
        $step.key -eq "ESC" -and
        $next.action -eq "wait_for_text" -and
        $next.pattern -match "CAMPAIGN|MY\\s\+HORIZON|CREATIVE"
    ) {
        $anchorRoute.steps[$index] = [pscustomobject]@{
            action = "key_until_text"
            key = "ESC"
            pattern = $next.pattern
            max_presses = 15
            delay_ms = 900
            name = "open_pause_menu_{index:000}.png"
        }
        break
    }
}
$anchorRoute | ConvertTo-Json -Depth 30 |
    Set-Content -LiteralPath $anchorRoutePath -Encoding UTF8
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run_forza_pipeline.ps1") `
    -Mode Capture `
    -RouteConfig $anchorRoutePath `
    -Track "AUTO_ANCHOR" `
    -PerformanceClass D `
    -RivalsMode "Road Racing" `
    -Pages 1 `
    -StartupWaitSeconds $StartupWaitSeconds `
    -SkipExtract `
    -NoDisplayManage
if ($LASTEXITCODE -ne 0) {
    throw "Initial learned route failed with exit code $LASTEXITCODE."
}

$classState = Invoke-Steps -Name "inspect_anchor_class" -Steps @(
    [pscustomobject]@{
        action = "key_until_text"
        key = "ESC"
        pattern = "['""]?\s*(D|C|B|A|S1|S2|R|X)\s*['""]?\s+Performance\s+Class"
        max_presses = 4
        delay_ms = 900
        name = "anchor_class_{index:000}.png"
    },
    [pscustomobject]@{ action = "observe"; name = "class_context.png" }
)
$anchor = Parse-ClassContext -Text (Get-OcrText -StateDir $classState)
$selectedContext = $anchor

if ($RivalsMode -eq "Road Racing" -and (Test-TrackMatch $anchor.track $Track)) {
    Write-Navigation "anchor is already target track '$($anchor.track)'"
} else {
    $steps = @(
        [pscustomobject]@{
            action = "key_until_text"
            key = "ESC"
            pattern = "(?m)^Routes\s*$"
            max_presses = 4
            delay_ms = 900
            name = "routes_{index:000}.png"
        }
    )
    if ($RivalsMode -ne "Road Racing") {
        $steps += New-KeyStep -Key "ESC" -DelayMs 900
        $steps += New-WaitStep -Pattern "Horizon\s+Rivals|Routes\s+Available|Road\s+Racing" -Name "categories_{index:000}.png"
        for ($index = 0; $index -lt $modeIndex; $index += 1) {
            $transition = "$($categoryPath[$index])->$($categoryPath[$index + 1])"
            foreach ($key in @($categoryMoveKeys[$transition])) {
                $steps += New-KeyStep -Key $key -DelayMs 550
            }
        }
        $steps += New-KeyStep -Key "ENTER" -DelayMs 1200
        $steps += New-WaitStep -Pattern "(?m)^Routes\s*$" -Name "target_routes_{index:000}.png"
    }
    $steps += New-KeyStep -Key "LEFT" -Count 50 -DelayMs 45
    Invoke-Steps -Name "open_target_routes" -Steps $steps | Out-Null

    $found = $false
    $discovered = @()
    for ($trackIndex = 0; $trackIndex -lt $MaxTracks; $trackIndex += 1) {
        $state = Invoke-Steps -Name ("inspect_track_{0:000}" -f $trackIndex) -Steps @(
            [pscustomobject]@{
                action = "key_until_text"
                key = "ENTER"
                pattern = "['""]?\s*(D|C|B|A|S1|S2|R|X)\s*['""]?\s+Performance\s+Class"
                max_presses = 4
                delay_ms = 1100
                name = "class_{index:000}.png"
            },
            [pscustomobject]@{ action = "observe"; name = "class_context.png" }
        )
        $context = Parse-ClassContext -Text (Get-OcrText -StateDir $state)
        $discovered += $context.track
        Write-Navigation "track[$trackIndex] '$($context.track)'"
        if (Test-TrackMatch $context.track $Track) {
            $selectedContext = $context
            $found = $true
            break
        }
        Invoke-Steps -Name ("next_track_{0:000}" -f $trackIndex) -Steps @(
            [pscustomobject]@{
                action = "key_until_text"
                key = "ESC"
                pattern = "(?m)^Routes\s*$"
                max_presses = 4
                delay_ms = 650
                name = "routes_{index:000}.png"
            },
            (New-KeyStep -Key "RIGHT" -DelayMs 400)
        ) | Out-Null
    }
    if (-not $found) {
        throw "Track '$Track' was not found in '$RivalsMode'. OCR saw: $($discovered -join ' | ')"
    }
}

$currentClassIndex = [array]::IndexOf(
    $classOrder,
    $selectedContext.performance_class.Trim().ToUpperInvariant()
)
if ($currentClassIndex -lt 0) {
    throw "Could not determine the currently selected performance class for '$Track'."
}
$classSteps = @()
$classDelta = $classIndex - $currentClassIndex
if ($classDelta -gt 0) {
    $classSteps += New-KeyStep -Key "RIGHT" -Count $classDelta -DelayMs 220
} elseif ($classDelta -lt 0) {
    $classSteps += New-KeyStep -Key "LEFT" -Count (-$classDelta) -DelayMs 220
}
$classSteps += @(
    (New-WaitStep -Pattern "['""]?\s*(D|C|B|A|S1|S2|R|X)\s*['""]?\s+Performance\s+Class" -Name "class_{index:000}.png"),
    [pscustomobject]@{ action = "observe"; name = "class_context.png" }
)
$selectedClassState = Invoke-Steps -Name "select_target_class" -Steps $classSteps
$verifiedClass = Parse-ClassContext -Text (Get-OcrText -StateDir $selectedClassState)
if ($verifiedClass.performance_class -ne $normalizedClass) {
    throw "Performance class selection failed. Expected '$normalizedClass', OCR saw '$($verifiedClass.performance_class)'."
}

$selectSteps = @(
    [pscustomobject]@{
        action = "key_until_text"
        key = "Y"
        pattern = $leaderboardPattern
        max_presses = 5
        delay_ms = 1300
        name = "leaderboard_{index:000}.png"
    },
    [pscustomobject]@{ action = "observe"; name = "leaderboard_ready.png" }
)
Invoke-Steps -Name "open_target_leaderboard" -Steps $selectSteps | Out-Null

[pscustomobject]@{
    track = $Track
    performance_class = $normalizedClass
    rivals_mode = $RivalsMode
    ready = $true
    completed_at = (Get-Date).ToString("o")
} | ConvertTo-Json
