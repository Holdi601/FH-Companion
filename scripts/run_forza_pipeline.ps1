param(
    [ValidateSet("Status", "Observe", "Learn", "Capture", "Batch", "ExtractOnly")]
    [string] $Mode = "Observe",

    [string] $RouteConfig = "config/fh6_visual_route.example.json",

    [string] $ObserveConfig = "config/fh6_observe_current.json",

    [string] $BatchConfig = "config/fh6_batch.example.json",

    [string] $LearnOutputConfig = "config/fh6_learned_route.json",

    [string] $Track = "",

    [Alias("PerformanceClass")]
    [string] $PiClass = "S1",

    [string] $EventType = "Rivals",

    [string] $RivalsMode = "Road Racing",

    [string] $Pages = "",

    [int] $CaptureCount = 20,

    [int] $StartupWaitSeconds = 90,

    [int] $IdleSeconds = 30,

    [double] $IdlePollSeconds = 2.0,

    [switch] $SkipLaunch,

    [switch] $SkipExtract,

    [switch] $NoDisplayManage,

    [switch] $ClearCaptureOutput,

    [switch] $IdleOnly,

    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$performanceClassOrder = @("D", "C", "B", "A", "S1", "S2", "R", "X")

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Pipeline {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-pipeline] $Message"
}

function Invoke-ProjectScript {
    param(
        [Parameter(Mandatory = $true)][string] $Script,
        [string[]] $Arguments = @()
    )

    $scriptPath = Resolve-WorkspacePath $Script
    & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Script failed with exit code $LASTEXITCODE"
    }
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
            Set-RepeatCaptureCount -Steps $step.steps -Count $Count
        }
    }
}

function Set-PerformanceClassSelection {
    param(
        [Parameter(Mandatory = $true)] $Config,
        [Parameter(Mandatory = $true)][string] $PiClassValue
    )

    $className = Normalize-PerformanceClass $PiClassValue
    $classIndex = [array]::IndexOf($performanceClassOrder, $className)
    if ($classIndex -lt 0) {
        throw "Unsupported performance class '$PiClassValue'. Use one of: $($performanceClassOrder -join ', ')"
    }
    if (-not $Config.steps) {
        return
    }

    $newSteps = @()
    $inserted = $false
    foreach ($step in @($Config.steps)) {
        if (-not $inserted -and [string]$step.action -eq "key" -and [string]$step.key -eq "Y") {
            $newSteps += [pscustomobject]@{
                action = "key"
                key = "LEFT"
                count = 10
                delay_ms = 100
                generated = "reset performance class to D before selecting requested class"
            }
            if ($classIndex -gt 0) {
                $newSteps += [pscustomobject]@{
                    action = "key"
                    key = "RIGHT"
                    count = $classIndex
                    delay_ms = 250
                    generated = "select requested performance class $className"
                }
            }
            $inserted = $true
        }
        $newSteps += $step
    }

    if (-not $inserted) {
        Write-Pipeline "warning: no leaderboard-open Y step found; could not inject performance class selection"
        return
    }

    $Config.steps = $newSteps
}

function Test-AllPagesValue {
    param($Value)

    return ([string]$Value).Trim() -match "^(?i:all)$"
}

function Get-PositiveIntegerOrZero {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }
    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return 0
    }
    $parsed = 0
    if ([int]::TryParse($text, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed
    }
    return 0
}

function Resolve-RequestedPages {
    param(
        $PrimaryValue,
        $SecondaryValue,
        [int] $DefaultValue
    )

    if (Test-AllPagesValue -Value $PrimaryValue) {
        return "All"
    }
    $primaryInt = Get-PositiveIntegerOrZero -Value $PrimaryValue
    if ($primaryInt -gt 0) {
        return $primaryInt
    }

    if (Test-AllPagesValue -Value $SecondaryValue) {
        return "All"
    }
    $secondaryInt = Get-PositiveIntegerOrZero -Value $SecondaryValue
    if ($secondaryInt -gt 0) {
        return $secondaryInt
    }

    return $DefaultValue
}

function ConvertTo-SafeName {
    param([Parameter(Mandatory = $true)][string] $Value)

    $safe = $Value.ToLowerInvariant() -replace "[^a-z0-9]+", "_"
    $safe = $safe.Trim("_")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "unknown"
    }
    return $safe
}

function Expand-PlaceholdersInString {
    param(
        [AllowEmptyString()]
        [string] $Value,
        [Parameter(Mandatory = $true)] $Variables
    )

    $result = $Value
    foreach ($property in $Variables.PSObject.Properties) {
        $token = "{{" + $property.Name + "}}"
        $result = $result.Replace($token, [string]$property.Value)
    }
    return $result
}

function Expand-Placeholders {
    param(
        [Parameter(Mandatory = $true)] $Node,
        [Parameter(Mandatory = $true)] $Variables
    )

    if ($null -eq $Node) {
        return $null
    }

    if ($Node -is [string]) {
        if ([string]::IsNullOrEmpty($Node)) {
            return $Node
        }
        return Expand-PlaceholdersInString -Value $Node -Variables $Variables
    }

    if ($Node -is [System.Array]) {
        for ($i = 0; $i -lt $Node.Count; $i += 1) {
            $Node[$i] = Expand-Placeholders -Node $Node[$i] -Variables $Variables
        }
        return $Node
    }

    if ($Node -is [pscustomobject]) {
        foreach ($property in @($Node.PSObject.Properties)) {
            $Node.($property.Name) = Expand-Placeholders -Node $property.Value -Variables $Variables
        }
        return $Node
    }

    return $Node
}

function New-RuntimeConfig {
    param(
        [Parameter(Mandatory = $true)][string] $SourceConfig,
        [Parameter(Mandatory = $true)][string] $ModeName,
        [string] $TrackValue = "",
        [string] $PiClassValue = "",
        [string] $EventTypeValue = "",
        [string] $RivalsModeValue = "",
        [object] $PagesValue = 0,
        [string] $JobId = "",
        [string] $CaptureOutputDir = "",
        [string] $ProcessedOutputDir = "",
        [string] $SharedSqlitePath = "",
        [string] $SharedParquetPath = "",
        [object] $DisplayManageValue = $null
    )

    $sourcePath = Resolve-WorkspacePath $SourceConfig
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Config not found: $sourcePath"
    }

    $config = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json
    $pageCount = Resolve-RequestedPages -PrimaryValue $PagesValue -SecondaryValue $Pages -DefaultValue $CaptureCount

    if ([string]::IsNullOrWhiteSpace($TrackValue)) {
        $TrackValue = $Track
    }
    if ([string]::IsNullOrWhiteSpace($PiClassValue)) {
        $PiClassValue = $PiClass
    }
    if ([string]::IsNullOrWhiteSpace($EventTypeValue)) {
        $EventTypeValue = $EventType
    }
    if ([string]::IsNullOrWhiteSpace($RivalsModeValue)) {
        $RivalsModeValue = $RivalsMode
    }
    if ([string]::IsNullOrWhiteSpace($JobId)) {
        $JobId = ConvertTo-SafeName "$TrackValue $RivalsModeValue $PiClassValue"
    }

    $displayManage = (-not $NoDisplayManage)
    if ($null -ne $DisplayManageValue) {
        $displayManage = [bool]$DisplayManageValue
    }
    Set-NestedProperty -Object $config -Parent "display" -Name "manage" -Value $displayManage
    Set-NestedProperty -Object $config -Parent "display" -Name "mode_before_launch" -Value "Windowed"
    Set-NestedProperty -Object $config -Parent "display" -Name "restore_after" -Value $true
    Set-NestedProperty -Object $config -Parent "display" -Name "backup_root" -Value "data/backups/forza_display"
    Set-NestedProperty -Object $config -Parent "window" -Name "idle_only" -Value ([bool]$IdleOnly)
    Set-NestedProperty -Object $config -Parent "window" -Name "idle_seconds" -Value $IdleSeconds
    Set-NestedProperty -Object $config -Parent "window" -Name "idle_poll_seconds" -Value $IdlePollSeconds
    Set-NestedProperty -Object $config -Parent "window" -Name "restore_previous_after_run" -Value ([bool]$IdleOnly)

    if ($config.launch) {
        $config.launch.startup_wait_seconds = $StartupWaitSeconds
    }

    if ($config.capture) {
        $config.capture.clear_output_dir = [bool]$ClearCaptureOutput
        if (-not [string]::IsNullOrWhiteSpace($CaptureOutputDir)) {
            $config.capture.output_dir = $CaptureOutputDir
            $config.capture.state_dir = (Join-Path $CaptureOutputDir "_state")
        }
        $config.capture.prefix = $JobId
    }

    if ($config.extraction) {
        if ($ModeName -eq "Observe") {
            $config.extraction.enabled = $false
        } else {
            $config.extraction.enabled = (-not $SkipExtract)
        }
        if (-not [string]::IsNullOrWhiteSpace($TrackValue)) {
            $config.extraction.track = $TrackValue
        }
        $config.extraction.pi_class = $PiClassValue
        $config.extraction.performance_class = $PiClassValue
        $config.extraction.event_type = $EventTypeValue
        $config.extraction.rivals_mode = $RivalsModeValue
        if (-not [string]::IsNullOrWhiteSpace($ProcessedOutputDir)) {
            $config.extraction.output_dir = $ProcessedOutputDir
        }
        if (-not [string]::IsNullOrWhiteSpace($SharedSqlitePath)) {
            $config.extraction.sqlite_path = $SharedSqlitePath
        }
        if (-not [string]::IsNullOrWhiteSpace($SharedParquetPath)) {
            $config.extraction.parquet_path = $SharedParquetPath
        }
    }

    if ($config.steps -and $pageCount -gt 0) {
        Set-RepeatCaptureCount -Steps $config.steps -Count $pageCount
    }

    $variables = [pscustomobject]@{
        track = $TrackValue
        performance_class = $PiClassValue
        pi_class = $PiClassValue
        event_type = $EventTypeValue
        rivals_mode = $RivalsModeValue
        pages = $pageCount
        job_id = $JobId
    }
    $config = Expand-Placeholders -Node $config -Variables $variables

    if ($ModeName -eq "Capture") {
        Set-PerformanceClassSelection -Config $config -PiClassValue $PiClassValue
    }

    $runtimeDir = Resolve-WorkspacePath "data/runtime"
    New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
    $safeMode = $ModeName.ToLowerInvariant()
    $runtimePath = Join-Path $runtimeDir "forza_${safeMode}_${JobId}_runtime.json"
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $runtimePath -Encoding UTF8
    return $runtimePath
}

function Get-ValueOrDefault {
    param(
        $Value,
        $Default
    )

    if ($null -ne $Value -and -not [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $Value
    }
    return $Default
}

function Get-ListOrDefault {
    param(
        $Object,
        [string] $SingularName,
        [string] $PluralName,
        $Default
    )

    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $PluralName -and $null -ne $Object.$PluralName) {
        return @($Object.$PluralName)
    }
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $SingularName -and $null -ne $Object.$SingularName) {
        return @($Object.$SingularName)
    }
    return @($Default)
}

function Get-ListByNamesOrDefault {
    param(
        $Object,
        [string[]] $SingularNames,
        [string[]] $PluralNames,
        $Default
    )

    if ($null -ne $Object) {
        foreach ($name in $PluralNames) {
            if ($Object.PSObject.Properties.Name -contains $name -and $null -ne $Object.$name) {
                return @($Object.$name)
            }
        }
        foreach ($name in $SingularNames) {
            if ($Object.PSObject.Properties.Name -contains $name -and $null -ne $Object.$name) {
                return @($Object.$name)
            }
        }
    }
    return @($Default)
}

function Normalize-PerformanceClass {
    param([Parameter(Mandatory = $true)][string] $Value)

    $normalized = $Value.Trim().ToUpperInvariant()
    if ($normalized -eq "*") {
        return "All"
    }
    if ($normalized -eq "ALL") {
        return "All"
    }
    return $normalized
}

function Expand-PerformanceClasses {
    param($Classes)

    $expanded = @()
    foreach ($classValue in @($Classes)) {
        $normalized = Normalize-PerformanceClass ([string]$classValue)
        if ($normalized -eq "All") {
            $expanded += $performanceClassOrder
        } else {
            $expanded += $normalized
        }
    }

    $seen = @{}
    $unique = @()
    foreach ($classValue in $expanded) {
        if (-not $seen.ContainsKey($classValue)) {
            $seen[$classValue] = $true
            $unique += $classValue
        }
    }
    return $unique
}

function Get-FirstExistingProperty {
    param(
        $Object,
        [string[]] $Names,
        $Default
    )

    if ($null -eq $Object) {
        return $Default
    }
    foreach ($name in $Names) {
        if ($Object.PSObject.Properties.Name -contains $name -and $null -ne $Object.$name -and -not [string]::IsNullOrWhiteSpace([string]$Object.$name)) {
            return $Object.$name
        }
    }
    return $Default
}

function Expand-BatchJobs {
    param([Parameter(Mandatory = $true)] $Batch)

    $defaults = $Batch.defaults
    $defaultEventType = Get-FirstExistingProperty -Object $defaults -Names @("event_type", "eventType") -Default $EventType
    $defaultPages = Get-FirstExistingProperty -Object $defaults -Names @("pages", "page_count", "capture_pages") -Default (Resolve-RequestedPages -PrimaryValue $Pages -SecondaryValue "" -DefaultValue $CaptureCount)
    $defaultPiClasses = Expand-PerformanceClasses (Get-ListByNamesOrDefault -Object $defaults -SingularNames @("performance_class", "pi_class") -PluralNames @("performance_classes", "pi_classes") -Default $PiClass)
    $defaultRivalsModes = Get-ListOrDefault -Object $defaults -SingularName "rivals_mode" -PluralName "rivals_modes" -Default $RivalsMode

    $expanded = @()
    $index = 0
    foreach ($job in @($Batch.jobs)) {
        $trackName = Get-FirstExistingProperty -Object $job -Names @("track", "map", "event", "name") -Default ""
        if ([string]::IsNullOrWhiteSpace([string]$trackName)) {
            throw "Batch job is missing track/map/event/name."
        }

        $eventTypeValue = Get-FirstExistingProperty -Object $job -Names @("event_type", "eventType") -Default $defaultEventType
        $pagesValue = Get-FirstExistingProperty -Object $job -Names @("pages", "page_count", "capture_pages") -Default $defaultPages
        $piClasses = Expand-PerformanceClasses (Get-ListByNamesOrDefault -Object $job -SingularNames @("performance_class", "pi_class") -PluralNames @("performance_classes", "pi_classes") -Default $defaultPiClasses)
        $rivalsModes = Get-ListOrDefault -Object $job -SingularName "rivals_mode" -PluralName "rivals_modes" -Default $defaultRivalsModes

        foreach ($modeValue in @($rivalsModes)) {
            foreach ($classValue in @($piClasses)) {
                $index += 1
                $jobId = Get-FirstExistingProperty -Object $job -Names @("job_id", "id") -Default ""
                if ([string]::IsNullOrWhiteSpace([string]$jobId)) {
                    $jobId = "{0:000}_{1}_{2}_{3}" -f $index, (ConvertTo-SafeName ([string]$trackName)), (ConvertTo-SafeName ([string]$modeValue)), (ConvertTo-SafeName ([string]$classValue))
                }
                $expanded += [pscustomobject]@{
                    job_id = $jobId
                    track = [string]$trackName
                    event_type = [string]$eventTypeValue
                    rivals_mode = [string]$modeValue
                    pi_class = [string]$classValue
                    pages = $pagesValue
                }
            }
        }
    }

    return $expanded
}

function Invoke-DisplayModeScript {
    param(
        [Parameter(Mandatory = $true)][string] $Action,
        [string] $BackupRoot = "data/backups/forza_display",
        [string] $RestoreBackupDir = ""
    )

    $script = Resolve-WorkspacePath "scripts/set_forza_display_mode.ps1"
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script, "-Action", $Action, "-BackupRoot", $BackupRoot)
    if (-not [string]::IsNullOrWhiteSpace($RestoreBackupDir)) {
        $args += @("-RestoreBackupDir", $RestoreBackupDir)
    }
    if ($DryRun) {
        $args += "-DryRun"
    }
    & powershell @args
    if ($LASTEXITCODE -ne 0) {
        throw "Display mode script failed with exit code $LASTEXITCODE"
    }
}

function Get-LatestDisplayBackup {
    param([Parameter(Mandatory = $true)][string] $BackupRoot)

    $backupRootPath = Resolve-WorkspacePath $BackupRoot
    if (-not (Test-Path -LiteralPath $backupRootPath)) {
        return $null
    }
    return Get-ChildItem -LiteralPath $backupRootPath -Directory |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Start-ForzaFromRouteConfig {
    param(
        [Parameter(Mandatory = $true)][string] $SourceConfig,
        [int] $WaitSeconds = 90
    )

    $sourcePath = Resolve-WorkspacePath $SourceConfig
    $config = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json
    if (-not $config.launch.command) {
        Write-Pipeline "launch skipped: no launch.command in $SourceConfig"
        return
    }

    Write-Pipeline "launch $($config.launch.command)"
    if (-not $DryRun) {
        if ($config.launch.arguments) {
            Start-Process -FilePath ([string]$config.launch.command) -ArgumentList @($config.launch.arguments)
        } else {
            Start-Process -FilePath ([string]$config.launch.command)
        }
        Start-Sleep -Seconds $WaitSeconds
    }
}

function Invoke-Batch {
    param([Parameter(Mandatory = $true)][string] $ConfigPath)

    $batchPath = Resolve-WorkspacePath $ConfigPath
    if (-not (Test-Path -LiteralPath $batchPath)) {
        throw "Batch config not found: $batchPath"
    }

    $batch = Get-Content -LiteralPath $batchPath -Raw | ConvertFrom-Json
    $defaults = $batch.defaults
    $routeConfigValue = Get-FirstExistingProperty -Object $batch -Names @("route_config", "routeConfig") -Default ""
    if ([string]::IsNullOrWhiteSpace([string]$routeConfigValue)) {
        $routeConfigValue = Get-FirstExistingProperty -Object $defaults -Names @("route_config", "routeConfig") -Default $RouteConfig
    }
    $routeConfigValue = [string]$routeConfigValue

    $sharedSqlite = Get-FirstExistingProperty -Object $batch -Names @("sqlite_path", "shared_sqlite_path") -Default "data/processed/leaderboard_entries.sqlite"
    $sharedParquet = Get-FirstExistingProperty -Object $batch -Names @("parquet_path", "shared_parquet_path") -Default "data/processed/leaderboard_entries.parquet"
    $processedRoot = Get-FirstExistingProperty -Object $batch -Names @("processed_root", "output_root") -Default "data/processed/batch"
    $captureRoot = Get-FirstExistingProperty -Object $batch -Names @("capture_root", "input_root") -Default "data/inbox/batch"
    $backupRoot = Get-FirstExistingProperty -Object $batch -Names @("backup_root") -Default "data/backups/forza_display"
    $jobs = Expand-BatchJobs -Batch $batch

    if ($jobs.Count -eq 0) {
        throw "Batch config contains no jobs."
    }

    Write-Pipeline "batch jobs: $($jobs.Count)"
    $displayBackupDir = $null
    $restoreDisplay = $false

    try {
        if (-not $NoDisplayManage -and -not $SkipLaunch) {
            Write-Pipeline "set FH6 display mode before batch: Windowed"
            Invoke-DisplayModeScript -Action "Windowed" -BackupRoot ([string]$backupRoot)
            if (-not $DryRun) {
                $displayBackupDir = Get-LatestDisplayBackup -BackupRoot ([string]$backupRoot)
            }
            $restoreDisplay = $true
        }

        if (-not $SkipLaunch) {
            Start-ForzaFromRouteConfig -SourceConfig $routeConfigValue -WaitSeconds $StartupWaitSeconds
        } else {
            Write-Pipeline "launch skipped"
        }

        $jobNumber = 0
        foreach ($job in @($jobs)) {
            $jobNumber += 1
            Write-Pipeline "job $jobNumber/$($jobs.Count): $($job.track) | $($job.rivals_mode) | $($job.pi_class) | pages=$($job.pages)"
            $captureDir = Join-Path ([string]$captureRoot) ([string]$job.job_id)
            $processedDir = Join-Path ([string]$processedRoot) ([string]$job.job_id)
            $runtimeConfig = New-RuntimeConfig `
                -SourceConfig $routeConfigValue `
                -ModeName "Capture" `
                -TrackValue ([string]$job.track) `
                -PiClassValue ([string]$job.pi_class) `
                -EventTypeValue ([string]$job.event_type) `
                -RivalsModeValue ([string]$job.rivals_mode) `
                -PagesValue $job.pages `
                -JobId ([string]$job.job_id) `
                -CaptureOutputDir $captureDir `
                -ProcessedOutputDir $processedDir `
                -SharedSqlitePath ([string]$sharedSqlite) `
                -SharedParquetPath ([string]$sharedParquet) `
                -DisplayManageValue $false

            $captureArgs = @("-Config", $runtimeConfig, "-SkipLaunch")
            if ($SkipExtract) {
                $captureArgs += "-SkipExtract"
            }
            if ($DryRun) {
                $captureArgs += "-DryRun"
            }
            Invoke-ProjectScript -Script "scripts/run_capture_automation.ps1" -Arguments $captureArgs
        }
    }
    finally {
        if ($restoreDisplay -and -not [string]::IsNullOrWhiteSpace($displayBackupDir)) {
            Write-Pipeline "restore FH6 display config from backup"
            Invoke-DisplayModeScript -Action "Restore" -BackupRoot ([string]$backupRoot) -RestoreBackupDir $displayBackupDir
        } elseif ($restoreDisplay -and $DryRun) {
            Write-Pipeline "would restore FH6 display config"
        }
    }
}

if ($Mode -eq "Status") {
    Write-Pipeline "checking display config"
    Invoke-ProjectScript -Script "scripts/set_forza_display_mode.ps1" -Arguments @("-Action", "Status")
    Write-Pipeline "discovering FH6 app/window"
    Invoke-ProjectScript -Script "scripts/find_forza_apps.ps1" -Arguments @()
    exit 0
}

if ($Mode -eq "Learn") {
    $learnPages = $CaptureCount
    $requestedLearnPages = Get-PositiveIntegerOrZero -Value $Pages
    if ($requestedLearnPages -gt 0) {
        $learnPages = $requestedLearnPages
    } elseif (Test-AllPagesValue -Value $Pages) {
        Write-Pipeline "warning: -Pages All is for capture replay; Learn will store pages=$learnPages. Use Capture -Pages All after learning."
    }
    $learnTrack = $Track
    if ([string]::IsNullOrWhiteSpace($learnTrack)) {
        $learnTrack = "REPLACE_WITH_TRACK_NAME"
    }
    $learnRivalsMode = $RivalsMode
    if ([string]::IsNullOrWhiteSpace($learnRivalsMode)) {
        $learnRivalsMode = "Road Racing"
    }

    $learnArgs = @(
        "-BaseConfig", $RouteConfig,
        "-OutputConfig", $LearnOutputConfig,
        "-Track", $learnTrack,
        "-PerformanceClass", $PiClass,
        "-EventType", $EventType,
        "-RivalsMode", $learnRivalsMode,
        "-Pages", ([string]$learnPages),
        "-StartupWaitSeconds", ([string]$StartupWaitSeconds)
    )
    if ($SkipLaunch) {
        $learnArgs += "-SkipLaunch"
    }
    if ($NoDisplayManage) {
        $learnArgs += "-NoDisplayManage"
    }
    if ($DryRun) {
        $learnArgs += "-DryRun"
    }

    Write-Pipeline "running Learn"
    Invoke-ProjectScript -Script "scripts/learn_forza_route.ps1" -Arguments $learnArgs
    Write-Pipeline "done"
    exit 0
}

$effectiveMode = $Mode

if ($effectiveMode -eq "Batch") {
    Invoke-Batch -ConfigPath $BatchConfig
    Write-Pipeline "done"
    exit 0
}

if ($effectiveMode -eq "ExtractOnly") {
    if ([string]::IsNullOrWhiteSpace($Track)) {
        throw "-Track is required for ExtractOnly."
    }
    $inputDir = Resolve-WorkspacePath "data/inbox/auto"
    $profile = Resolve-WorkspacePath "config/fh6_rivals_1080p.json"
    $outputDir = Resolve-WorkspacePath "data/processed"
    $extractor = Resolve-WorkspacePath "scripts/extract_leaderboard.py"
    Write-Pipeline "running extractor only"
    $sqlitePath = Resolve-WorkspacePath "data/processed/leaderboard_entries.sqlite"
    $parquetPath = Resolve-WorkspacePath "data/processed/leaderboard_entries.parquet"
    & python $extractor --input $inputDir --include-glob "leaderboard_*.png" --track $Track --performance-class $PiClass --event-type $EventType --rivals-mode $RivalsMode --profile $profile --output-dir $outputDir --sqlite-path $sqlitePath --parquet-path $parquetPath --dump-ocr
    if ($LASTEXITCODE -ne 0) {
        throw "extract_leaderboard.py failed with exit code $LASTEXITCODE"
    }
    exit 0
}

$sourceConfig = $RouteConfig
if ($effectiveMode -eq "Observe") {
    $sourceConfig = $ObserveConfig
}

if ($effectiveMode -eq "Capture" -and [string]::IsNullOrWhiteSpace($Track)) {
    Write-Pipeline "warning: -Track was not set; extraction rows will use the config value."
}

$runtimeConfig = New-RuntimeConfig -SourceConfig $sourceConfig -ModeName $effectiveMode
Write-Pipeline "runtime config: $runtimeConfig"

$captureArgs = @("-Config", $runtimeConfig)
if ($SkipLaunch) {
    $captureArgs += "-SkipLaunch"
}
if ($effectiveMode -eq "Observe" -or $SkipExtract) {
    $captureArgs += "-SkipExtract"
}
if ($DryRun) {
    $captureArgs += "-DryRun"
}

Write-Pipeline "running $Mode"
Invoke-ProjectScript -Script "scripts/run_capture_automation.ps1" -Arguments $captureArgs

Write-Pipeline "done"
