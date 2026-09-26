param(
    [string] $BaseConfig = "config/fh6_current_leaderboard_pages.json",
    [string] $Pages = "3",
    [string] $Classes = "All",
    [string] $Tracks = "Current",
    [string] $Categories = "Current",
    [string] $CurrentCategory = "Road Racing",
    [string] $EventType = "Rivals",
    [string] $RunId = "",
    [string] $OutputRoot = "data/sweeps",
    [string] $ProcessedRoot = "data/processed/sweeps",
    [string] $SharedSqlitePath = "data/processed/fh6_rivals_sweep.sqlite",
    [string] $SharedParquetPath = "data/processed/fh6_rivals_sweep.parquet",
    [switch] $ResetTrackToFirst,
    [switch] $SkipExtract,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$performanceClassOrder = @("D", "C", "B", "A", "S1", "S2", "R", "X")
$categoryPath = @("Road Racing", "Cross-Country", "Street Racing", "Touge", "Drag Racing", "Dirt Racing")
$categoryMoveKeys = @{
    "Road Racing->Cross-Country" = @("RIGHT")
    "Cross-Country->Street Racing" = @("RIGHT")
    "Street Racing->Touge" = @("DOWN")
    "Touge->Drag Racing" = @("LEFT")
    "Drag Racing->Dirt Racing" = @("LEFT")
    "Dirt Racing->Road Racing" = @("UP")
}

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Sweep {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-sweep] $Message"
}

function Ensure-Property {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Set-NestedProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    if (-not ($Object.PSObject.Properties.Name -contains $Parent) -or $null -eq $Object.$Parent) {
        $Object | Add-Member -NotePropertyName $Parent -NotePropertyValue ([pscustomobject]@{})
    }
    Ensure-Property -Object $Object.$Parent -Name $Name -Value $Value
}

function ConvertTo-SafeName {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $safe = $Value.ToLowerInvariant() -replace "[^a-z0-9]+", "_"
    $safe = $safe.Trim("_")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "unknown"
    }
    return $safe
}

function Read-BaseConfig {
    $path = Resolve-WorkspacePath $BaseConfig
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Base config not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Write-RuntimeConfig {
    param(
        [Parameter(Mandatory = $true)] $ConfigObject,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $runtimeDir = Resolve-WorkspacePath "data/runtime/sweeps/$script:RunId"
    New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
    $path = Join-Path $runtimeDir "$Name.json"
    $json = $ConfigObject | ConvertTo-Json -Depth 40
    Set-Content -LiteralPath $path -Value $json -Encoding UTF8
    return $path
}

function Set-RepeatCaptureCount {
    param(
        [Parameter(Mandatory = $true)] $Steps,
        [Parameter(Mandatory = $true)] $Count
    )

    foreach ($step in @($Steps)) {
        if ([string]$step.action -eq "repeat") {
            $hasScreenshot = $false
            foreach ($nestedStep in @($step.steps)) {
                if ([string]$nestedStep.action -eq "screenshot") {
                    $hasScreenshot = $true
                }
            }
            if ($hasScreenshot) {
                $step.count = $Count
            }
            if ($step.steps) {
                Set-RepeatCaptureCount -Steps $step.steps -Count $Count
            }
        }
    }
}

function Invoke-CaptureAutomation {
    param(
        [Parameter(Mandatory = $true)][string] $ConfigPath,
        [switch] $NavigationOnly
    )

    $scriptPath = Resolve-WorkspacePath "scripts/run_capture_automation.ps1"
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $scriptPath,
        "-Config",
        $ConfigPath,
        "-SkipLaunch"
    )
    if ($NavigationOnly) {
        $args += "-SkipExtract"
    }
    if ($DryRun) {
        $args += "-DryRun"
    }

    & powershell @args | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "run_capture_automation.ps1 failed with exit code $LASTEXITCODE"
    }
}

function New-StepKey {
    param(
        [Parameter(Mandatory = $true)][string] $Key,
        [int] $Count = 1,
        [int] $DelayMs = 500
    )

    return [pscustomobject]@{
        action = "key"
        key = $Key
        count = $Count
        delay_ms = $DelayMs
    }
}

function New-StepWaitText {
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [string] $Name = "wait_context_{index:000}.png",
        [int] $TimeoutSeconds = 30
    )

    return [pscustomobject]@{
        action = "wait_for_text"
        pattern = $Pattern
        timeout_seconds = $TimeoutSeconds
        interval_seconds = 1
        name = $Name
    }
}

function New-StepObserve {
    param([Parameter(Mandatory = $true)][string] $Name)

    return [pscustomobject]@{
        action = "observe"
        name = $Name
    }
}

function New-NavigationConfig {
    param(
        [Parameter(Mandatory = $true)] $Steps,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $config = Read-BaseConfig
    Set-NestedProperty -Object $config -Parent "display" -Name "manage" -Value $false
    Set-NestedProperty -Object $config -Parent "display" -Name "restore_after" -Value $false
    Set-NestedProperty -Object $config -Parent "launch" -Name "startup_wait_seconds" -Value 0
    Set-NestedProperty -Object $config -Parent "capture" -Name "clear_output_dir" -Value $false
    Set-NestedProperty -Object $config -Parent "capture" -Name "dump_state_ocr" -Value $true
    Set-NestedProperty -Object $config -Parent "capture" -Name "output_dir" -Value ("data/runtime/sweeps/$script:RunId/nav/$Name")
    Set-NestedProperty -Object $config -Parent "capture" -Name "state_dir" -Value ("data/runtime/sweeps/$script:RunId/nav/$Name/_state")
    Set-NestedProperty -Object $config -Parent "capture" -Name "prefix" -Value $Name
    Set-NestedProperty -Object $config -Parent "extraction" -Name "enabled" -Value $false
    $config.steps = @($Steps)
    return Write-RuntimeConfig -ConfigObject $config -Name "nav_$Name"
}

function Invoke-Navigation {
    param(
        [Parameter(Mandatory = $true)] $Steps,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $configPath = New-NavigationConfig -Steps $Steps -Name $Name
    Write-Sweep "navigate $Name"
    Invoke-CaptureAutomation -ConfigPath $configPath -NavigationOnly
    return Resolve-WorkspacePath "data/runtime/sweeps/$script:RunId/nav/$Name/_state"
}

function Get-LatestOcrText {
    param([Parameter(Mandatory = $true)][string] $StateDir)

    if (-not (Test-Path -LiteralPath $StateDir)) {
        return ""
    }
    $file = Get-ChildItem -LiteralPath $StateDir -Filter "*.ocr.txt" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $file) {
        return ""
    }
    return Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
}

function Parse-ClassContext {
    param([AllowEmptyString()][string] $Text)

    $lines = @($Text -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $track = ""
    $class = ""

    for ($i = 0; $i -lt $lines.Count; $i += 1) {
        if ($lines[$i] -match "['""]?\s*(D|C|B|A|S1|S2|R|X)\s*['""]?\s+Performance\s+Class") {
            $class = $matches[1].ToUpperInvariant()
            if ($i -gt 0) {
                $track = $lines[$i - 1]
            }
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($track)) {
        for ($i = 0; $i -lt $lines.Count; $i += 1) {
            if ($lines[$i] -match "^Route\s+Length:") {
                if ($i -gt 0) {
                    $track = $lines[$i - 1]
                }
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($class)) {
        foreach ($candidate in $performanceClassOrder) {
            if ($Text -match "(?<![A-Z0-9])$([regex]::Escape($candidate))(?![A-Z0-9])") {
                $class = $candidate
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($track)) {
        $track = "UNKNOWN_TRACK"
    }
    if ([string]::IsNullOrWhiteSpace($class)) {
        $class = "UNKNOWN_CLASS"
    }

    return [pscustomobject]@{
        track = $track
        performance_class = $class
    }
}

function Parse-CategoryCounts {
    param([AllowEmptyString()][string] $Text)

    $counts = @{}
    $lines = @($Text -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($i = 0; $i -lt ($lines.Count - 1); $i += 1) {
        $category = $lines[$i]
        if ($categoryPath -notcontains $category) {
            continue
        }
        if ($lines[$i + 1] -match "(\d+)\s+Routes\s+Available") {
            $counts[$category] = [int]$matches[1]
        }
    }
    return $counts
}

function Expand-Classes {
    param([Parameter(Mandatory = $true)][string] $Value)

    $trimmed = $Value.Trim()
    if ($trimmed -match "^(?i:current)$") {
        return @("__CURRENT__")
    }
    if ($trimmed -match "^(?i:all)$") {
        return $performanceClassOrder
    }
    $items = @($trimmed -split "," | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
    if ($items.Count -eq 0) {
        return @("__CURRENT__")
    }
    foreach ($item in $items) {
        if ($performanceClassOrder -notcontains $item) {
            throw "Unsupported performance class '$item'. Use Current, All, or a comma list from: $($performanceClassOrder -join ', ')"
        }
    }
    return $items
}

function Resolve-CountValue {
    param(
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][string] $Name,
        [int] $AllCount = 0
    )

    $trimmed = $Value.Trim()
    if ($trimmed -match "^(?i:current)$") {
        return 1
    }
    if ($trimmed -match "^(?i:all)$") {
        if ($AllCount -gt 0) {
            return $AllCount
        }
        throw "$Name=All could not be resolved from OCR context."
    }
    $count = 0
    if ([int]::TryParse($trimmed, [ref]$count) -and $count -gt 0) {
        return $count
    }
    throw "$Name must be Current, All, or a positive integer."
}

function Get-CategorySequence {
    param(
        [Parameter(Mandatory = $true)][string] $Current,
        [Parameter(Mandatory = $true)][string] $Scope
    )

    $startIndex = [array]::IndexOf($categoryPath, $Current)
    if ($startIndex -lt 0) {
        throw "CurrentCategory must be one of: $($categoryPath -join ', ')"
    }

    $count = Resolve-CountValue -Value $Scope -Name "Categories" -AllCount $categoryPath.Count
    $sequence = @()
    for ($i = 0; $i -lt $count; $i += 1) {
        $sequence += $categoryPath[($startIndex + $i) % $categoryPath.Count]
    }
    return $sequence
}

function Invoke-OpenCurrentClassFromLeaderboard {
    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_context_{index:000}.png"),
        (New-StepObserve -Name "class_context.png")
    )
    $stateDir = Invoke-Navigation -Steps $steps -Name "open_class_context"
    return Parse-ClassContext -Text (Get-LatestOcrText -StateDir $stateDir)
}

function Invoke-OpenNextClassFromLeaderboard {
    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_context_{index:000}.png"),
        (New-StepKey -Key "RIGHT" -DelayMs 500),
        (New-StepObserve -Name "class_context.png")
    )
    $stateDir = Invoke-Navigation -Steps $steps -Name "open_next_class_context"
    return Parse-ClassContext -Text (Get-LatestOcrText -StateDir $stateDir)
}

function Invoke-ReturnToLeaderboardFromClass {
    $steps = @(
        (New-StepKey -Key "Y" -DelayMs 1200),
        (New-StepWaitText -Pattern "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival" -Name "wait_leaderboard_{index:000}.png" -TimeoutSeconds 45)
    )
    Invoke-Navigation -Steps $steps -Name "return_leaderboard_from_class" | Out-Null
}

function Invoke-MoveToNextTrackAndLeaderboard {
    param([int] $TrackIndex)

    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Routes|Route\s+Length" -Name "wait_routes_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "RIGHT" -DelayMs 600),
        (New-StepKey -Key "ENTER" -DelayMs 1200),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_after_track_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 10 -DelayMs 120),
        (New-StepKey -Key "Y" -DelayMs 1200),
        (New-StepWaitText -Pattern "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival" -Name "wait_leaderboard_after_track_{index:000}.png" -TimeoutSeconds 45)
    )
    Invoke-Navigation -Steps $steps -Name ("next_track_{0:000}" -f $TrackIndex) | Out-Null
}

function Invoke-ResetTrackAndClassToFirst {
    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Routes|Route\s+Length" -Name "wait_routes_reset_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 50 -DelayMs 80),
        (New-StepKey -Key "ENTER" -DelayMs 1200),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_reset_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 10 -DelayMs 100),
        (New-StepKey -Key "Y" -DelayMs 1200),
        (New-StepWaitText -Pattern "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival" -Name "wait_leaderboard_reset_{index:000}.png" -TimeoutSeconds 45)
    )
    Invoke-Navigation -Steps $steps -Name "reset_track_class_to_first" | Out-Null
}

function Invoke-ResetClassToFirstFromLeaderboard {
    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_reset_only_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 10 -DelayMs 100),
        (New-StepKey -Key "Y" -DelayMs 1200),
        (New-StepWaitText -Pattern "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival" -Name "wait_leaderboard_class_reset_{index:000}.png" -TimeoutSeconds 45)
    )
    Invoke-Navigation -Steps $steps -Name "reset_class_to_first" | Out-Null
}

function Invoke-MoveToNextCategoryAndLeaderboard {
    param(
        [Parameter(Mandatory = $true)][string] $FromCategory,
        [Parameter(Mandatory = $true)][string] $ToCategory
    )

    $transitionKey = "$FromCategory->$ToCategory"
    if (-not $categoryMoveKeys.ContainsKey($transitionKey)) {
        throw "No category movement defined for $transitionKey"
    }

    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Horizon\s+Rivals|Routes\s+Available|Road\s+Racing" -Name "wait_categories_{index:000}.png" -TimeoutSeconds 45)
    )

    foreach ($key in @($categoryMoveKeys[$transitionKey])) {
        $steps += New-StepKey -Key $key -DelayMs 600
    }

    $steps += @(
        (New-StepKey -Key "ENTER" -DelayMs 1200),
        (New-StepWaitText -Pattern "Routes|Route\s+Length" -Name "wait_routes_after_category_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 50 -DelayMs 80),
        (New-StepKey -Key "ENTER" -DelayMs 1200),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_after_category_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 10 -DelayMs 100),
        (New-StepKey -Key "Y" -DelayMs 1200),
        (New-StepWaitText -Pattern "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival" -Name "wait_leaderboard_after_category_{index:000}.png" -TimeoutSeconds 45)
    )

    Invoke-Navigation -Steps $steps -Name ("next_category_{0}_to_{1}" -f (ConvertTo-SafeName $FromCategory), (ConvertTo-SafeName $ToCategory)) | Out-Null
}

function Get-CategoryCountsFromLeaderboard {
    $steps = @(
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepKey -Key "ESC" -DelayMs 900),
        (New-StepWaitText -Pattern "Horizon\s+Rivals|Routes\s+Available|Road\s+Racing" -Name "wait_categories_counts_{index:000}.png" -TimeoutSeconds 45),
        (New-StepObserve -Name "category_context.png"),
        (New-StepKey -Key "ENTER" -DelayMs 1200),
        (New-StepWaitText -Pattern "Routes|Route\s+Length" -Name "wait_routes_return_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "ENTER" -DelayMs 1200),
        (New-StepWaitText -Pattern "Performance\s+Class|Change\s+Rival|Route\s+Length" -Name "wait_class_return_{index:000}.png" -TimeoutSeconds 45),
        (New-StepKey -Key "LEFT" -Count 10 -DelayMs 100),
        (New-StepKey -Key "Y" -DelayMs 1200),
        (New-StepWaitText -Pattern "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival" -Name "wait_leaderboard_return_{index:000}.png" -TimeoutSeconds 45)
    )
    $stateDir = Invoke-Navigation -Steps $steps -Name "read_category_counts"
    return Parse-CategoryCounts -Text (Get-LatestOcrText -StateDir $stateDir)
}

function New-CaptureConfig {
    param(
        [Parameter(Mandatory = $true)][string] $Category,
        [Parameter(Mandatory = $true)][string] $Track,
        [Parameter(Mandatory = $true)][string] $PerformanceClass,
        [Parameter(Mandatory = $true)][int] $SegmentIndex
    )

    $config = Read-BaseConfig
    $safeCategory = ConvertTo-SafeName $Category
    $safeTrack = ConvertTo-SafeName $Track
    $safeClass = ConvertTo-SafeName $PerformanceClass
    $segmentName = "{0:0000}_{1}_{2}_{3}" -f $SegmentIndex, $safeCategory, $safeTrack, $safeClass

    Set-NestedProperty -Object $config -Parent "display" -Name "manage" -Value $false
    Set-NestedProperty -Object $config -Parent "display" -Name "restore_after" -Value $false
    Set-NestedProperty -Object $config -Parent "launch" -Name "startup_wait_seconds" -Value 0
    Set-NestedProperty -Object $config -Parent "capture" -Name "clear_output_dir" -Value $true
    Set-NestedProperty -Object $config -Parent "capture" -Name "dump_state_ocr" -Value $true
    Set-NestedProperty -Object $config -Parent "capture" -Name "output_dir" -Value ("$OutputRoot/$script:RunId/$segmentName")
    Set-NestedProperty -Object $config -Parent "capture" -Name "state_dir" -Value ("$OutputRoot/$script:RunId/$segmentName/_state")
    Set-NestedProperty -Object $config -Parent "capture" -Name "prefix" -Value $segmentName
    Set-NestedProperty -Object $config -Parent "extraction" -Name "enabled" -Value (-not $SkipExtract)
    Set-NestedProperty -Object $config -Parent "extraction" -Name "track" -Value $Track
    Set-NestedProperty -Object $config -Parent "extraction" -Name "performance_class" -Value $PerformanceClass
    Set-NestedProperty -Object $config -Parent "extraction" -Name "pi_class" -Value $PerformanceClass
    Set-NestedProperty -Object $config -Parent "extraction" -Name "event_type" -Value $EventType
    Set-NestedProperty -Object $config -Parent "extraction" -Name "rivals_mode" -Value $Category
    Set-NestedProperty -Object $config -Parent "extraction" -Name "output_dir" -Value ("$ProcessedRoot/$script:RunId/$segmentName")
    Set-NestedProperty -Object $config -Parent "extraction" -Name "sqlite_path" -Value $SharedSqlitePath
    Set-NestedProperty -Object $config -Parent "extraction" -Name "parquet_path" -Value $SharedParquetPath

    if ($config.steps) {
        Set-RepeatCaptureCount -Steps $config.steps -Count $Pages
    }

    return Write-RuntimeConfig -ConfigObject $config -Name "capture_$segmentName"
}

function Invoke-CaptureCurrentLeaderboard {
    param(
        [Parameter(Mandatory = $true)][string] $Category,
        [Parameter(Mandatory = $true)][string] $Track,
        [Parameter(Mandatory = $true)][string] $PerformanceClass,
        [Parameter(Mandatory = $true)][int] $SegmentIndex
    )

    $configPath = New-CaptureConfig -Category $Category -Track $Track -PerformanceClass $PerformanceClass -SegmentIndex $SegmentIndex
    Write-Sweep "capture category='$Category' track='$Track' class='$PerformanceClass' pages=$Pages"
    Invoke-CaptureAutomation -ConfigPath $configPath
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = Get-Date -Format "yyyyMMdd_HHmmss"
}
$script:RunId = $RunId

$classTargets = @(Expand-Classes -Value $Classes)
$categorySequence = @(Get-CategorySequence -Current $CurrentCategory -Scope $Categories)
$categoryCounts = @{}
if ($Tracks.Trim() -match "^(?i:all)$" -or $Categories.Trim() -match "^(?i:all)$") {
    Write-Sweep "read category route counts"
    $categoryCounts = Get-CategoryCountsFromLeaderboard
    foreach ($categoryName in $categoryPath) {
        if ($categoryCounts.ContainsKey($categoryName)) {
            Write-Sweep "${categoryName}: $($categoryCounts[$categoryName]) routes"
        }
    }
}

if ($ResetTrackToFirst -or $Tracks.Trim() -match "^(?i:all)$") {
    Write-Sweep "reset current route and class selection to first visible entries"
    Invoke-ResetTrackAndClassToFirst
} elseif ($Classes.Trim() -match "^(?i:all)$") {
    Write-Sweep "reset current class selection to first visible entry"
    Invoke-ResetClassToFirstFromLeaderboard
}

$segmentIndex = 0
for ($categoryIndex = 0; $categoryIndex -lt $categorySequence.Count; $categoryIndex += 1) {
    $category = $categorySequence[$categoryIndex]
    if ($categoryIndex -gt 0) {
        Invoke-MoveToNextCategoryAndLeaderboard -FromCategory $categorySequence[$categoryIndex - 1] -ToCategory $category
    }

    $allTrackCount = 0
    if ($categoryCounts.ContainsKey($category)) {
        $allTrackCount = [int]$categoryCounts[$category]
    }
    $trackCount = Resolve-CountValue -Value $Tracks -Name "Tracks" -AllCount $allTrackCount

    for ($trackIndex = 0; $trackIndex -lt $trackCount; $trackIndex += 1) {
        if ($trackIndex -gt 0) {
            Invoke-MoveToNextTrackAndLeaderboard -TrackIndex $trackIndex
        }

        for ($classIndex = 0; $classIndex -lt $classTargets.Count; $classIndex += 1) {
            if ($classIndex -eq 0) {
                $context = Invoke-OpenCurrentClassFromLeaderboard
            } else {
                $context = Invoke-OpenNextClassFromLeaderboard
            }

            $trackName = $context.track
            $className = $context.performance_class
            Invoke-ReturnToLeaderboardFromClass

            if ($classTargets[$classIndex] -ne "__CURRENT__" -and $className -ne "UNKNOWN_CLASS" -and $classTargets[$classIndex] -ne $className) {
                Write-Sweep "warning: expected class '$($classTargets[$classIndex])' but OCR/selection reports '$className'. Capturing reported class."
            }

            $segmentIndex += 1
            Invoke-CaptureCurrentLeaderboard -Category $category -Track $trackName -PerformanceClass $className -SegmentIndex $segmentIndex
        }
    }
}

Write-Sweep "done"
Write-Sweep "screenshots: $(Resolve-WorkspacePath "$OutputRoot/$script:RunId")"
Write-Sweep "processed segments: $(Resolve-WorkspacePath "$ProcessedRoot/$script:RunId")"
if (-not $SkipExtract) {
    Write-Sweep "shared parquet: $(Resolve-WorkspacePath $SharedParquetPath)"
    Write-Sweep "shared sqlite: $(Resolve-WorkspacePath $SharedSqlitePath)"
}
