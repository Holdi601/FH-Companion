param(
    [string] $BaseConfig = "config/fh6_visual_route.example.json",

    [string] $OutputConfig = "config/fh6_learned_route.json",

    [string] $EventsPath = "",

    [string] $SessionRoot = "data/learn",

    [string] $Track = "REPLACE_WITH_TRACK_NAME",

    [Alias("PiClass")]
    [string] $PerformanceClass = "S1",

    [string] $EventType = "Rivals",

    [string] $RivalsMode = "Road Racing",

    [int] $Pages = 20,

    [string] $PageKey = "DOWN",

    [int] $PageKeyCount = 11,

    [int] $StartupWaitSeconds = 90,

    [switch] $SkipLaunch,

    [switch] $NoDisplayManage,

    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public struct LearnPoint {
    public int X;
    public int Y;
}

public struct LearnWindowRect {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class LearnInputTools {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out LearnPoint lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out LearnWindowRect lpRect);
}
"@

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Learn {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-learn] $Message"
}

function ConvertTo-SafeName {
    param([Parameter(Mandatory = $true)][string] $Value)

    $safe = $Value.ToLowerInvariant() -replace "[^a-z0-9]+", "_"
    $safe = $safe.Trim("_")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "state"
    }
    return $safe
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

function Ensure-ObjectProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if (-not ($Object.PSObject.Properties.Name -contains $Name) -or $null -eq $Object.$Name) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue ([pscustomobject]@{})
    }
}

function Get-TargetProcess {
    param([Parameter(Mandatory = $true)] $WindowConfig)

    $names = @()
    if ($WindowConfig.process_names) {
        $names += @($WindowConfig.process_names)
    }
    if ($WindowConfig.process_name) {
        $names += @($WindowConfig.process_name)
    }

    $candidates = @()
    foreach ($name in $names) {
        $candidates += Get-Process -Name $name -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 }
    }

    if ($WindowConfig.title_regex) {
        $regex = [regex]$WindowConfig.title_regex
        $candidates += Get-Process |
            Where-Object { $_.MainWindowHandle -ne 0 -and $regex.IsMatch($_.MainWindowTitle) }
    }

    return $candidates | Sort-Object StartTime -Descending -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-TargetForeground {
    param([Parameter(Mandatory = $true)] $Process)

    $foregroundWindow = [LearnInputTools]::GetForegroundWindow()
    if ($foregroundWindow -eq [IntPtr]::Zero) {
        return $false
    }

    [uint32] $foregroundProcessId = 0
    [LearnInputTools]::GetWindowThreadProcessId($foregroundWindow, [ref]$foregroundProcessId) | Out-Null
    return $foregroundProcessId -eq [uint32]$Process.Id
}

function Hide-ForegroundWindowIfNotTarget {
    param([Parameter(Mandatory = $true)] $WindowConfig)

    $foregroundWindow = [LearnInputTools]::GetForegroundWindow()
    if ($foregroundWindow -eq [IntPtr]::Zero) {
        return [IntPtr]::Zero
    }

    [uint32] $foregroundProcessId = 0
    [LearnInputTools]::GetWindowThreadProcessId($foregroundWindow, [ref]$foregroundProcessId) | Out-Null
    $targetProcess = Get-TargetProcess -WindowConfig $WindowConfig
    if ($null -ne $targetProcess -and $foregroundProcessId -eq [uint32]$targetProcess.Id) {
        return [IntPtr]::Zero
    }

    $foregroundProcess = Get-Process -Id $foregroundProcessId -ErrorAction SilentlyContinue
    if ($null -eq $foregroundProcess -or $foregroundProcess.ProcessName -ieq "explorer") {
        return [IntPtr]::Zero
    }

    Write-Learn "minimize foreground window '$($foregroundProcess.ProcessName)' before game capture"
    [LearnInputTools]::ShowWindowAsync($foregroundWindow, 6) | Out-Null
    Start-Sleep -Milliseconds 500
    return $foregroundWindow
}

function Restore-HiddenForegroundWindow {
    param($WindowHandle)

    if ($null -ne $WindowHandle -and $WindowHandle -ne [IntPtr]::Zero) {
        [LearnInputTools]::ShowWindowAsync($WindowHandle, 9) | Out-Null
    }
}

function Test-KeyDown {
    param([Parameter(Mandatory = $true)][int] $VirtualKey)

    return (([int][LearnInputTools]::GetAsyncKeyState($VirtualKey) -band 0x8000) -ne 0)
}

function Get-CursorPosition {
    $point = New-Object LearnPoint
    [LearnInputTools]::GetCursorPos([ref]$point) | Out-Null
    return $point
}

function Focus-TargetWindow {
    param(
        [Parameter(Mandatory = $true)] $WindowConfig,
        [int] $TimeoutSeconds = 0
    )

    $timeout = 120
    if ($WindowConfig.focus_timeout_seconds) {
        $timeout = [int]$WindowConfig.focus_timeout_seconds
    }
    if ($TimeoutSeconds -gt 0) {
        $timeout = $TimeoutSeconds
    }

    $deadline = (Get-Date).AddSeconds($timeout)
    do {
        $process = Get-TargetProcess -WindowConfig $WindowConfig
        if ($null -ne $process) {
            [LearnInputTools]::ShowWindowAsync($process.MainWindowHandle, 9) | Out-Null
            Start-Sleep -Milliseconds 250
            [LearnInputTools]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            Start-Sleep -Milliseconds 250
            if (Test-TargetForeground -Process $process) {
                Write-Learn "focused '$($process.MainWindowTitle)' (pid $($process.Id))"
                return $process
            }
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw "Could not find/focus target game window. Check window.process_names and window.title_regex."
}

function Get-TargetWindowBounds {
    param([Parameter(Mandatory = $true)] $WindowConfig)

    $process = Get-TargetProcess -WindowConfig $WindowConfig
    if ($null -eq $process) {
        throw "Could not find target game window for screenshot bounds."
    }

    $rect = New-Object LearnWindowRect
    if (-not [LearnInputTools]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) {
        throw "Could not read target game window bounds."
    }

    $width = [int]($rect.Right - $rect.Left)
    $height = [int]($rect.Bottom - $rect.Top)
    if ($width -le 0 -or $height -le 0) {
        throw "Target game window bounds are invalid: left=$($rect.Left), top=$($rect.Top), right=$($rect.Right), bottom=$($rect.Bottom)."
    }

    return [pscustomobject]@{
        X = [int]$rect.Left
        Y = [int]$rect.Top
        Width = $width
        Height = $height
        ProcessId = [int]$process.Id
        Title = [string]$process.MainWindowTitle
    }
}

function Capture-Screenshot {
    param(
        [Parameter(Mandatory = $true)][string] $OutputPath,
        $WindowConfig = $null
    )

    $absolutePath = Resolve-WorkspacePath $OutputPath
    $parent = Split-Path -Parent $absolutePath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    if ($null -ne $WindowConfig) {
        $bounds = Get-TargetWindowBounds -WindowConfig $WindowConfig
        $sourcePoint = New-Object System.Drawing.Point $bounds.X, $bounds.Y
        $captureSize = New-Object System.Drawing.Size $bounds.Width, $bounds.Height
    } else {
        $screenBounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $sourcePoint = $screenBounds.Location
        $captureSize = $screenBounds.Size
    }

    $bitmap = New-Object System.Drawing.Bitmap $captureSize.Width, $captureSize.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($sourcePoint, [System.Drawing.Point]::Empty, $captureSize)
        $bitmap.Save($absolutePath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    return $absolutePath
}

function Invoke-ImageOcr {
    param(
        [Parameter(Mandatory = $true)][string] $ImagePath,
        [Parameter(Mandatory = $true)][string] $Language,
        [string] $RawOutputPath = ""
    )

    $ocrScript = Resolve-WorkspacePath "scripts/windows_ocr.ps1"
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $ocrScript -ImagePath $ImagePath -Language $Language
    if ($LASTEXITCODE -ne 0) {
        throw "OCR failed for $ImagePath"
    }
    $rawJson = $output | Out-String
    if (-not [string]::IsNullOrWhiteSpace($RawOutputPath)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $RawOutputPath) | Out-Null
        Set-Content -LiteralPath $RawOutputPath -Value $rawJson -Encoding UTF8
    }
    return Convert-OcrJson -RawJson $rawJson -ImagePath $ImagePath
}

function Convert-OcrJson {
    param(
        [Parameter(Mandatory = $true)][string] $RawJson,
        [Parameter(Mandatory = $true)][string] $ImagePath
    )

    try {
        return ($RawJson | ConvertFrom-Json)
    }
    catch {
        $originalError = $_.Exception.Message
        try {
            $repaired = Repair-OcrJsonText -RawJson $RawJson
            return ($repaired | ConvertFrom-Json)
        }
        catch {
            throw "Could not parse OCR JSON for $ImagePath. PowerShell error: $originalError. Repair fallback error: $($_.Exception.Message)"
        }
    }
}

function Repair-OcrJsonText {
    param([Parameter(Mandatory = $true)][string] $RawJson)

    $text = [regex]::Replace($RawJson, "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", " ")
    $builder = [System.Text.StringBuilder]::new()
    $inString = $false
    $escaped = $false

    for ($i = 0; $i -lt $text.Length; $i += 1) {
        $char = $text[$i]
        $code = [int][char]$char

        if ($escaped) {
            [void]$builder.Append($char)
            $escaped = $false
            continue
        }

        if ($inString -and $code -eq 92) {
            [void]$builder.Append($char)
            $escaped = $true
            continue
        }

        if ($code -eq 34) {
            if (-not $inString) {
                $inString = $true
                [void]$builder.Append($char)
                continue
            }

            $next = [char]0
            for ($j = $i + 1; $j -lt $text.Length; $j += 1) {
                if (-not [char]::IsWhiteSpace($text[$j])) {
                    $next = $text[$j]
                    break
                }
            }

            if ($next -eq ':' -or $next -eq ',' -or $next -eq '}' -or $next -eq ']' -or $next -eq [char]0) {
                $inString = $false
                [void]$builder.Append($char)
            } else {
                [void]$builder.Append("'")
            }
            continue
        }

        [void]$builder.Append($char)
    }

    return $builder.ToString()
}

function Save-OcrDump {
    param(
        [Parameter(Mandatory = $true)] $Ocr,
        [Parameter(Mandatory = $true)][string] $ImagePath,
        [Parameter(Mandatory = $true)][string] $StateDir
    )

    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    $name = [System.IO.Path]::GetFileNameWithoutExtension($ImagePath)
    $jsonPath = Join-Path $StateDir "$name.ocr.json"
    $txtPath = Join-Path $StateDir "$name.ocr.txt"
    $lines = @($Ocr.lines | ForEach-Object { $_.text })

    if (-not (Test-Path -LiteralPath $jsonPath)) {
        $Ocr | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    }
    ($lines -join [Environment]::NewLine) | Set-Content -LiteralPath $txtPath -Encoding UTF8

    return [pscustomobject]@{
        json = $jsonPath
        text = $txtPath
        line_count = $lines.Count
        plain_text = ($lines -join " ")
    }
}

function New-LearningSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $SessionDir,
        [Parameter(Mandatory = $true)][string] $StateDir,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $OcrLanguage,
        [Parameter(Mandatory = $true)] $WindowConfig
    )

    $imagePath = Capture-Screenshot -OutputPath (Join-Path $SessionDir "$Name.png") -WindowConfig $WindowConfig
    $jsonPath = Join-Path $StateDir "$Name.ocr.json"
    $txtPath = Join-Path $StateDir "$Name.ocr.txt"

    try {
        $ocr = Invoke-ImageOcr -ImagePath $imagePath -Language $OcrLanguage -RawOutputPath $jsonPath
        $dump = Save-OcrDump -Ocr $ocr -ImagePath $imagePath -StateDir $StateDir
        Write-Learn "captured $Name ($($dump.line_count) OCR lines)"
    }
    catch {
        New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
        $message = "OCR failed for ${Name}: $($_.Exception.Message)"
        if (-not (Test-Path -LiteralPath $jsonPath)) {
            [pscustomobject]@{
                image = $imagePath
                error = $message
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
        }
        $message | Set-Content -LiteralPath $txtPath -Encoding UTF8
        Write-Learn "captured $Name (OCR failed; screenshot kept)"
        $dump = [pscustomobject]@{
            json = $jsonPath
            text = $txtPath
            line_count = 0
            plain_text = ""
        }
    }

    return [pscustomobject]@{
        image_path = $imagePath
        ocr_json_path = $dump.json
        ocr_text_path = $dump.text
        ocr_line_count = $dump.line_count
        ocr_text = $dump.plain_text
    }
}

function Invoke-DisplayModeScript {
    param(
        [Parameter(Mandatory = $true)][string] $Action,
        [string] $BackupRoot = "data/backups/forza_display",
        [string] $RestoreBackupDir = ""
    )

    $script = Resolve-WorkspacePath "scripts/set_forza_display_mode.ps1"
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $script,
        "-Action",
        $Action,
        "-BackupRoot",
        $BackupRoot
    )
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

function New-RecordedKeyMap {
    $keys = @(
        @{ name = "ENTER"; vk = 0x0D },
        @{ name = "ESC"; vk = 0x1B },
        @{ name = "TAB"; vk = 0x09 },
        @{ name = "SPACE"; vk = 0x20 },
        @{ name = "UP"; vk = 0x26 },
        @{ name = "DOWN"; vk = 0x28 },
        @{ name = "LEFT"; vk = 0x25 },
        @{ name = "RIGHT"; vk = 0x27 },
        @{ name = "PAGEUP"; vk = 0x21 },
        @{ name = "PAGEDOWN"; vk = 0x22 },
        @{ name = "HOME"; vk = 0x24 },
        @{ name = "END"; vk = 0x23 },
        @{ name = "BACKSPACE"; vk = 0x08 },
        @{ name = "DELETE"; vk = 0x2E },
        @{ name = "A"; vk = 0x41 },
        @{ name = "B"; vk = 0x42 },
        @{ name = "C"; vk = 0x43 },
        @{ name = "D"; vk = 0x44 },
        @{ name = "E"; vk = 0x45 },
        @{ name = "F"; vk = 0x46 },
        @{ name = "G"; vk = 0x47 },
        @{ name = "H"; vk = 0x48 },
        @{ name = "I"; vk = 0x49 },
        @{ name = "J"; vk = 0x4A },
        @{ name = "K"; vk = 0x4B },
        @{ name = "L"; vk = 0x4C },
        @{ name = "M"; vk = 0x4D },
        @{ name = "N"; vk = 0x4E },
        @{ name = "O"; vk = 0x4F },
        @{ name = "P"; vk = 0x50 },
        @{ name = "Q"; vk = 0x51 },
        @{ name = "R"; vk = 0x52 },
        @{ name = "S"; vk = 0x53 },
        @{ name = "T"; vk = 0x54 },
        @{ name = "U"; vk = 0x55 },
        @{ name = "V"; vk = 0x56 },
        @{ name = "W"; vk = 0x57 },
        @{ name = "X"; vk = 0x58 },
        @{ name = "Y"; vk = 0x59 },
        @{ name = "Z"; vk = 0x5A },
        @{ name = "0"; vk = 0x30 },
        @{ name = "1"; vk = 0x31 },
        @{ name = "2"; vk = 0x32 },
        @{ name = "3"; vk = 0x33 },
        @{ name = "4"; vk = 0x34 },
        @{ name = "5"; vk = 0x35 },
        @{ name = "6"; vk = 0x36 },
        @{ name = "7"; vk = 0x37 },
        @{ name = "8"; vk = 0x38 },
        @{ name = "9"; vk = 0x39 }
    )

    return @($keys | ForEach-Object { [pscustomobject]$_ })
}

function Get-RouteDelayMs {
    param(
        $CurrentEvent,
        $NextEvent,
        [int] $DefaultDelayMs = 650
    )

    if ($null -eq $CurrentEvent -or $null -eq $NextEvent) {
        return $DefaultDelayMs
    }

    $deltaMs = [int](([datetime]$NextEvent.time - [datetime]$CurrentEvent.time).TotalMilliseconds)
    if ($deltaMs -lt 250) {
        return 250
    }
    if ($deltaMs -gt 1800) {
        return 1800
    }
    return $deltaMs
}

function ConvertTo-RegexAlternative {
    param([AllowEmptyString()][string] $Text)

    $clean = ($Text -replace "\s+", " ").Trim()
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return ""
    }
    return [regex]::Escape($clean).Replace("\ ", "\s+")
}

function Test-UsefulMarkerLine {
    param([AllowEmptyString()][string] $Line)

    $clean = ($Line -replace "\s+", " ").Trim()
    if ($clean.Length -lt 5) {
        return $false
    }
    if ($clean -match "^(Forza Horizon 6|EINGABE|Select|Back|ESC|TABULATOR)$") {
        return $false
    }
    if ($clean -match "^(FPS|GPU|CPU|LAT|Fps|[0-9., ]+)$") {
        return $false
    }
    if ($clean -match "FPS|GPU|CPU|LAT") {
        return $false
    }
    if ($clean -match "@\s*[0-9,]+") {
        return $false
    }
    if ($clean -match "\b(19|20)[0-9]{2}\b") {
        return $false
    }
    if ($clean -match "\b[0-9]{1,2}:[0-9]{2}\.[0-9]{3}\b") {
        return $false
    }
    if ($clean -match "\b[A-D]\s*[0-9]{3}\b|\bS[12]\s*[0-9]{3}\b|\b[RX]\s*[0-9]{3}\b") {
        return $false
    }
    if ($clean -match "\b(ABS|TCS|STM|GEAR|RWD|FWD|AWD|MC)\b") {
        return $false
    }
    if ($clean -match "\b(Audi|Honda|Peel|Sportback|S800|P50)\b") {
        return $false
    }
    return $true
}

function Test-OcrTextContains {
    param(
        [AllowEmptyString()][string] $InputText,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    return ([regex]::IsMatch([string]$InputText, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase))
}

function New-ContentStateMarkerPattern {
    param([AllowEmptyString()][string] $OcrText)

    if ([string]::IsNullOrWhiteSpace($OcrText)) {
        return ""
    }

    $allText = [string](($OcrText -replace "\s+", " ").Trim())

    if (
        (Test-OcrTextContains $allText "\bDriver\b") -and
        (Test-OcrTextContains $allText "\bCar\b") -and
        (Test-OcrTextContains $allText "\bTime\b")
    ) {
        return "Driver|Drivetrain|Filter:\s+Global|Change\s+Filter|Player\s+options|Change\s+Rival"
    }

    if (Test-OcrTextContains $allText "Time\s+to\s+Beat|Vehicle\s+Used|Gap\s+to\s+Rival") {
        return "Time\s+to\s+Beat|Vehicle\s+Used|Gap\s+to\s+Rival|Change\s+Rival"
    }

    if ((Test-OcrTextContains $allText "Route\s+Length") -and (Test-OcrTextContains $allText "\bDetails\b")) {
        return "Routes|Route\s+Length|Details|KM|full-throttle"
    }

    if ((Test-OcrTextContains $allText "Road\s+Racing") -and (Test-OcrTextContains $allText "Routes\s+Available")) {
        return "Horizon\s+Rivals|Road\s+Racing|Routes\s+Available|Dirt\s+Racing|Street\s+Racing|Cross-Country|Drag\s+Racing|Touge"
    }

    if (Test-OcrTextContains $allText "My\s+Rivals|Monthly\s+Rivals|Showcase\s+Rivals") {
        return "My\s+Rivals|Monthly\s+Rivals|Showcase\s+Rivals|Horizon\s+Rivals"
    }

    if (Test-OcrTextContains $allText "Top\s+the\s+Leaderboards|Find\s*/\s*Start\s+a\s+Convoy|Forza\s+LINK|Horizon\s+Solo") {
        return "Top\s+the\s+Leaderboards|Find\s*/\s*Start\s+a\s+Convoy|Forza\s+LINK|Horizon\s+Solo|Convoy"
    }

    if (
        (Test-OcrTextContains $allText "\bCAMPAIGN\b") -and
        (Test-OcrTextContains $allText "\bONLINE\b") -and
        (Test-OcrTextContains $allText "\bSTORE\b")
    ) {
        return "CAMPAIGN|CARS|MY\s+HORIZON|ONLINE|CREATIVE\s+HUB|STORE|Exit\s+Game|Recommended\s+Content"
    }

    if (Test-OcrTextContains $allText "BUY\s*&\s*SELL|CUSTOMISABLE\s+GARAGE|Forzavista") {
        return "BUY\s*&\s*SELL|CUSTOMISABLE\s+GARAGE|Campaign|Drive|Festival\s+Playlist|Collection\s+Journal|Forzavista"
    }

    if (Test-OcrTextContains $allText "Enter\s+House|Vision\s+House|OWNED|KWH") {
        return "Enter\s+House|Vision\s+House|OWNED|ANNA|LINK|KWH"
    }

    if ((Test-OcrTextContains $allText "\bContinue\b") -and (Test-OcrTextContains $allText "\bOptions\b")) {
        return "Continue|Options|Exit"
    }

    if (Test-OcrTextContains $allText "Start\s+Game|Accessibility") {
        return "Start\s+Game|Accessibility/Settings|Accessibility"
    }

    return ""
}

function New-StateMarkerPattern {
    param(
        [AllowEmptyString()][string] $Label,
        [AllowEmptyString()][string] $OcrText,
        [AllowEmptyString()][string] $OcrTextPath
    )

    if ([string]::IsNullOrWhiteSpace($OcrText) -and -not [string]::IsNullOrWhiteSpace($OcrTextPath) -and (Test-Path -LiteralPath $OcrTextPath)) {
        $OcrText = Get-Content -LiteralPath $OcrTextPath -Raw
    }

    $contentPattern = New-ContentStateMarkerPattern -OcrText $OcrText
    if (-not [string]::IsNullOrWhiteSpace($contentPattern)) {
        return $contentPattern
    }

    $labelValue = $Label.ToLowerInvariant()
    $knownPattern = switch -Regex ($labelValue) {
        "start|title" { "Start\s+Game|Accessibility/Settings|Accessibility" ; break }
        "rivals_entry|continue" { "Continue|Options|Exit" ; break }
        "mode_select|garage|main_menu" { "BUY\s*&\s*SELL|CUSTOMISABLE\s+GARAGE|Campaign|Drive|Festival\s+Playlist|Collection\s+Journal|Forzavista" ; break }
        "track_select|free_drive" { "Enter\s+House|Vision\s+House|OWNED|ANNA|LINK|KWH" ; break }
        "class_filter|pause_menu" { "CAMPAIGN|CARS|MY\s+HORIZON|ONLINE|CREATIVE\s+HUB|STORE|Exit\s+Game|Recommended\s+Content" ; break }
        "online_tab|leaderboard_preview" { "Top\s+the\s+Leaderboards|Find\s*/\s*Start\s+a\s+Convoy|Forza\s+LINK|Horizon\s+Solo|Convoy" ; break }
        "rivals_hub" { "My\s+Rivals|Monthly\s+Rivals|Showcase\s+Rivals|Horizon\s+Rivals" ; break }
        "rivals_mode_selection" { "Horizon\s+Rivals|Road\s+Racing|Routes\s+Available|Dirt\s+Racing|Street\s+Racing|Cross-Country|Drag\s+Racing|Touge" ; break }
        "state_006|race_track_selection|route" { "Routes|Route\s+Length|Details|KM|full-throttle" ; break }
        "state_007|rival_selection|performance_class" { "Time\s+to\s+Beat|Vehicle\s+Used|Gap\s+to\s+Rival|Change\s+Rival" ; break }
        "leaderboard" { "(?=.*Driver)(?=.*Car)(?=.*Time)|Change\s+Rival|Player\s+options|Drivetrain" ; break }
        default { "" }
    }
    if (-not [string]::IsNullOrWhiteSpace($knownPattern)) {
        return $knownPattern
    }

    $candidates = @()
    foreach ($line in @($OcrText -split "\r?\n")) {
        if (Test-UsefulMarkerLine -Line $line) {
            $alternative = ConvertTo-RegexAlternative $line
            if (-not [string]::IsNullOrWhiteSpace($alternative)) {
                $candidates += $alternative
            }
        }
        if ($candidates.Count -ge 5) {
            break
        }
    }

    if ($candidates.Count -eq 0) {
        return ""
    }

    return ($candidates -join "|")
}

function Add-StateGuardStep {
    param(
        [Parameter(Mandatory = $true)] $RouteEvent,
        [Parameter(Mandatory = $true)][string] $GuardName
    )

    $ocrPath = ""
    if ($RouteEvent.PSObject.Properties.Name -contains "ocr_text_path") {
        $ocrPath = [string]$RouteEvent.ocr_text_path
    }

    $pattern = New-StateMarkerPattern -Label ([string]$RouteEvent.label) -OcrText "" -OcrTextPath $ocrPath

    if ([string]::IsNullOrWhiteSpace($pattern) -and $RouteEvent.PSObject.Properties.Name -contains "state_pattern") {
        $pattern = [string]$RouteEvent.state_pattern
    }

    if ([string]::IsNullOrWhiteSpace($pattern)) {
        return
    }

    $script:steps += [pscustomobject]@{
        action = "wait_for_text"
        pattern = $pattern
        timeout_seconds = 90
        interval_seconds = 1
        name = "wait_$GuardName`_{index:000}.png"
    }
}

function Test-OnlineTabPattern {
    param([AllowEmptyString()][string] $Pattern)

    return (
        $Pattern.Contains("Top\s+the\s+Leaderboards") -or
        $Pattern.Contains("Find\s*/\s*Start\s+a\s+Convoy") -or
        $Pattern.Contains("Horizon\s+Solo")
    )
}

function Optimize-LearnedRouteSteps {
    param([Parameter(Mandatory = $true)][object[]] $Steps)

    $result = @()
    $navigationKeys = @("LEFT", "RIGHT", "UP", "DOWN")

    foreach ($step in @($Steps)) {
        $action = [string]$step.action
        $pattern = ""
        if ($step.PSObject.Properties.Name -contains "pattern") {
            $pattern = [string]$step.pattern
        }

        if ($action -eq "wait_for_text" -and (Test-OnlineTabPattern -Pattern $pattern)) {
            $trailingKeys = @()
            while (@($result).Count -gt 0) {
                $lastStep = @($result)[@($result).Count - 1]
                if ([string]$lastStep.action -ne "key") {
                    break
                }
                $lastKey = [string]$lastStep.key
                if (-not ($navigationKeys -contains $lastKey)) {
                    break
                }
                $trailingKeys = @($lastStep) + @($trailingKeys)
                if (@($result).Count -eq 1) {
                    $result = @()
                } else {
                    $result = @($result)[0..(@($result).Count - 2)]
                }
            }

            $rightCount = 0
            $downCount = 0
            foreach ($keyStep in @($trailingKeys)) {
                if ([string]$keyStep.key -eq "RIGHT") {
                    $rightCount += [int]$keyStep.count
                }
                if ([string]$keyStep.key -eq "DOWN") {
                    $downCount += [int]$keyStep.count
                }
            }

            if ($rightCount -ge 3 -and $downCount -ge 1) {
                $maxPresses = [Math]::Max(8, $rightCount + 2)
                $result += [pscustomobject]@{
                    action = "key_until_text"
                    key = "RIGHT"
                    pattern = "Top\s+the\s+Leaderboards|Find\s*/\s*Start\s+a\s+Convoy|Horizon\s+Solo"
                    max_presses = $maxPresses
                    delay_ms = 900
                    name = "nav_online_tab_{index:000}.png"
                    learned_optimization = "replaced recorded RIGHT tab spam with OCR-guided navigation to the Online tab"
                }
                $result += [pscustomobject]@{
                    action = "key"
                    key = "RIGHT"
                    count = 1
                    delay_ms = 900
                    learned_optimization = "move toward the visible Rivals tile after Online tab content is visible"
                }
                $result += [pscustomobject]@{
                    action = "key"
                    key = "DOWN"
                    count = 1
                    delay_ms = 1400
                    learned_optimization = "finish selecting the visible Rivals tile"
                }
            } else {
                foreach ($keyStep in @($trailingKeys)) {
                    $result += $keyStep
                }
            }
        }

        $result += $step
    }

    return @($result)
}

function Convert-EventsToRouteSteps {
    param(
        [Parameter(Mandatory = $true)][object[]] $Events,
        [Parameter(Mandatory = $true)][string] $FinalPageKey,
        [Parameter(Mandatory = $true)][int] $FinalPageKeyCount,
        [Parameter(Mandatory = $true)][int] $FinalPages,
        [Parameter(Mandatory = $true)][bool] $HasLeaderboardReady
    )

    $steps = @(
        [pscustomobject]@{
            action = "wait"
            seconds = 3
            label = "settle after learned focus"
        }
    )

    $pendingKey = $null
    $pendingCount = 0
    $pendingDelay = 650

    function Flush-PendingKey {
        if ($null -ne $script:pendingKey -and $script:pendingCount -gt 0) {
            $script:steps += [pscustomobject]@{
                action = "key"
                key = $script:pendingKey
                count = $script:pendingCount
                delay_ms = $script:pendingDelay
            }
            $script:pendingKey = $null
            $script:pendingCount = 0
            $script:pendingDelay = 650
        }
    }

    $script:steps = $steps
    $script:pendingKey = $pendingKey
    $script:pendingCount = $pendingCount
    $script:pendingDelay = $pendingDelay

    for ($i = 0; $i -lt @($Events).Count; $i += 1) {
        $routeEvent = @($Events)[$i]
        $next = $null
        if ($i + 1 -lt @($Events).Count) {
            $next = @($Events)[$i + 1]
        }

        switch ([string]$routeEvent.type) {
            "key" {
                $delayMs = Get-RouteDelayMs -CurrentEvent $routeEvent -NextEvent $next
                if ($script:pendingKey -eq [string]$routeEvent.key -and $script:pendingDelay -eq $delayMs) {
                    $script:pendingCount += 1
                } else {
                    Flush-PendingKey
                    $script:pendingKey = [string]$routeEvent.key
                    $script:pendingCount = 1
                    $script:pendingDelay = $delayMs
                }
            }
            "mouse_click" {
                Flush-PendingKey
                $script:steps += [pscustomobject]@{
                    action = "click"
                    x_ratio = [double]$routeEvent.x_ratio
                    y_ratio = [double]$routeEvent.y_ratio
                    delay_ms = 900
                }
            }
            "milestone" {
                Flush-PendingKey
                $guardName = "learn_check_$('{0:000}' -f [int]$routeEvent.index)_$($routeEvent.label)"
                Add-StateGuardStep $routeEvent $guardName
                $script:steps += [pscustomobject]@{
                    action = "observe"
                    name = "$guardName`_{index:000}.png"
                }
                if (
                    [string]$routeEvent.label -eq "start_menu" -and
                    $null -ne $next -and
                    [string]$next.type -eq "milestone" -and
                    [string]$next.label -eq "rivals_entry"
                ) {
                    $script:steps += [pscustomobject]@{
                        action = "key"
                        key = "ENTER"
                        count = 1
                        delay_ms = 1800
                        learned_repair = "inserted title-screen enter between consecutive start_menu and rivals_entry milestones"
                    }
                }
            }
            "leaderboard_ready" {
                Flush-PendingKey
                Add-StateGuardStep $routeEvent "learn_leaderboard_ready"
                $script:steps += [pscustomobject]@{
                    action = "observe"
                    name = "learn_leaderboard_ready_{index:000}.png"
                }
            }
        }
    }

    Flush-PendingKey

    if ($HasLeaderboardReady) {
        $script:steps += [pscustomobject]@{
            action = "repeat"
            count = $FinalPages
            rows_per_page = $FinalPageKeyCount
            steps = @(
                [pscustomobject]@{
                    action = "screenshot"
                    name = "leaderboard_{index:000}.png"
                },
                [pscustomobject]@{
                    action = "key"
                    key = $FinalPageKey
                    count = $FinalPageKeyCount
                    first_count = ($FinalPageKeyCount * 2 - 1)
                    delay_ms = 160
                    skip_on_last_repeat = $true
                }
            )
        }
    } else {
        $script:steps += [pscustomobject]@{
            action = "observe"
            name = "learn_stopped_without_leaderboard_{index:000}.png"
        }
    }

    $result = $script:steps
    Remove-Variable -Name steps -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable -Name pendingKey -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable -Name pendingCount -Scope Script -ErrorAction SilentlyContinue
    Remove-Variable -Name pendingDelay -Scope Script -ErrorAction SilentlyContinue
    return (Optimize-LearnedRouteSteps -Steps @($result))
}

function Save-LearnedRoute {
    param(
        [Parameter(Mandatory = $true)] $BaseConfigObject,
        [Parameter(Mandatory = $true)][object[]] $Events,
        [Parameter(Mandatory = $true)][string] $OutputConfigPath,
        [Parameter(Mandatory = $true)][string] $SessionDir,
        [Parameter(Mandatory = $true)][bool] $HasLeaderboardReady
    )

    Ensure-ObjectProperty -Object $BaseConfigObject -Name "capture"
    Ensure-ObjectProperty -Object $BaseConfigObject -Name "extraction"
    Ensure-ObjectProperty -Object $BaseConfigObject -Name "display"

    Ensure-Property -Object $BaseConfigObject.capture -Name "output_dir" -Value "data/inbox/auto"
    Ensure-Property -Object $BaseConfigObject.capture -Name "state_dir" -Value "data/inbox/auto/_state"
    Ensure-Property -Object $BaseConfigObject.capture -Name "prefix" -Value "fh6_learned"
    Ensure-Property -Object $BaseConfigObject.capture -Name "clear_output_dir" -Value $false
    Ensure-Property -Object $BaseConfigObject.capture -Name "dump_state_ocr" -Value $true
    Ensure-Property -Object $BaseConfigObject.capture -Name "leaderboard_rows_per_page" -Value $PageKeyCount
    Ensure-Property -Object $BaseConfigObject.extraction -Name "enabled" -Value $true
    Ensure-Property -Object $BaseConfigObject.extraction -Name "track" -Value $Track
    Ensure-Property -Object $BaseConfigObject.extraction -Name "performance_class" -Value $PerformanceClass
    Ensure-Property -Object $BaseConfigObject.extraction -Name "pi_class" -Value $PerformanceClass
    Ensure-Property -Object $BaseConfigObject.extraction -Name "event_type" -Value $EventType
    Ensure-Property -Object $BaseConfigObject.extraction -Name "rivals_mode" -Value $RivalsMode
    Ensure-Property -Object $BaseConfigObject.extraction -Name "profile" -Value "config/fh6_rivals_1080p.json"
    Ensure-Property -Object $BaseConfigObject.extraction -Name "output_dir" -Value "data/processed"
    Ensure-Property -Object $BaseConfigObject.extraction -Name "sqlite_path" -Value "data/processed/leaderboard_entries.sqlite"
    Ensure-Property -Object $BaseConfigObject.extraction -Name "parquet_path" -Value "data/processed/leaderboard_entries.parquet"
    Ensure-Property -Object $BaseConfigObject.extraction -Name "dump_ocr" -Value $true
    Ensure-Property -Object $BaseConfigObject.extraction -Name "keep_crops" -Value $false

    $BaseConfigObject.steps = Convert-EventsToRouteSteps `
        -Events $Events `
        -FinalPageKey $PageKey `
        -FinalPageKeyCount $PageKeyCount `
        -FinalPages $Pages `
        -HasLeaderboardReady $HasLeaderboardReady

    $learnedFrom = [pscustomobject]@{
        session_dir = $SessionDir
        created_at = (Get-Date).ToString("o")
        track = $Track
        performance_class = $PerformanceClass
        event_type = $EventType
        rivals_mode = $RivalsMode
        pages = $Pages
        page_key = $PageKey
        page_key_count = $PageKeyCount
        note = "Generated by scripts/learn_forza_route.ps1 from a keyboard/mouse learning session."
    }

    Ensure-Property -Object $BaseConfigObject -Name "learned_from" -Value $learnedFrom

    if ($HasLeaderboardReady) {
        $absoluteOutput = Resolve-WorkspacePath $OutputConfigPath
    } else {
        $absoluteOutput = Join-Path (Resolve-WorkspacePath $SessionDir) "learned_route.partial.json"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absoluteOutput) | Out-Null
    $BaseConfigObject | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $absoluteOutput -Encoding UTF8

    return $absoluteOutput
}

$baseConfigPath = Resolve-WorkspacePath $BaseConfig
if (-not (Test-Path -LiteralPath $baseConfigPath)) {
    throw "Base config not found: $baseConfigPath"
}

$baseConfigObject = Get-Content -LiteralPath $baseConfigPath -Raw | ConvertFrom-Json
$sessionId = "learn_" + (Get-Date).ToString("yyyyMMdd_HHmmss")
$sessionDir = Resolve-WorkspacePath (Join-Path $SessionRoot $sessionId)
$stateDir = Join-Path $sessionDir "_state"
$ocrLanguage = "en"
if ($baseConfigObject.capture.ocr_language) {
    $ocrLanguage = [string]$baseConfigObject.capture.ocr_language
}

if (-not [string]::IsNullOrWhiteSpace($EventsPath)) {
    $absoluteEventsPath = Resolve-WorkspacePath $EventsPath
    if (-not (Test-Path -LiteralPath $absoluteEventsPath)) {
        throw "Events file not found: $absoluteEventsPath"
    }

    $rawLoadedEvents = Get-Content -LiteralPath $absoluteEventsPath -Raw | ConvertFrom-Json
    $loadedEvents = @($rawLoadedEvents)
    $sourceSessionDir = Split-Path -Parent $absoluteEventsPath
    $hasLoadedLeaderboardReady = $false
    foreach ($loadedEvent in $loadedEvents) {
        if ([string]$loadedEvent.type -eq "leaderboard_ready") {
            $hasLoadedLeaderboardReady = $true
            break
        }
    }

    $learnedRoutePath = Save-LearnedRoute `
        -BaseConfigObject $baseConfigObject `
        -Events $loadedEvents `
        -OutputConfigPath $OutputConfig `
        -SessionDir $sourceSessionDir `
        -HasLeaderboardReady $hasLoadedLeaderboardReady

    Write-Learn "regenerated learned route saved $learnedRoutePath"
    exit 0
}

Write-Learn "interactive route learning"
Write-Host ""
Write-Host "What you will do:"
Write-Host "  1. Use keyboard/mouse in Forza during recording, not a controller."
Write-Host "  2. Navigate from the current menu to the target Rivals leaderboard."
Write-Host "  3. Press F8 after each important screen has settled: title, continue, garage, free drive, pause menu, Online tab, Rivals hub, Rivals mode, route selection, rival selection."
Write-Host "  4. Press F9 once the leaderboard page is visible and ready to page through."
Write-Host "  5. Press F10 if you want to stop without finishing the route."
Write-Host ""
Write-Host "The recorder stores only whitelisted menu keys, left mouse clicks, screenshots, and OCR during the active FH6 session."
Write-Host "Press keys one at a time and do not hold them, otherwise Windows/menu repeat cannot be learned cleanly."
Write-Host ""
Write-Host "Target metadata: track='$Track', mode='$RivalsMode', class='$PerformanceClass', pages=$Pages"
Write-Host ""

if ($DryRun) {
    Write-Learn "dry-run: would create learning session in $sessionDir"
    Write-Learn "dry-run: would write learned route to $(Resolve-WorkspacePath $OutputConfig)"
    exit 0
}

Read-Host "Press Enter when you are ready. I will then focus Forza and start recording"

New-Item -ItemType Directory -Force -Path $sessionDir | Out-Null
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

$displayBackupRoot = "data/backups/forza_display"
if ($baseConfigObject.display.backup_root) {
    $displayBackupRoot = [string]$baseConfigObject.display.backup_root
}
$displayBackupDir = $null
$restoreDisplay = $false
$hiddenForegroundWindow = [IntPtr]::Zero
$events = New-Object System.Collections.Generic.List[object]
$leaderboardReady = $false

try {
    if (-not $NoDisplayManage -and -not $SkipLaunch) {
        Write-Learn "set FH6 display mode before launch: Windowed"
        Invoke-DisplayModeScript -Action "Windowed" -BackupRoot $displayBackupRoot
        $displayBackupDir = Get-LatestDisplayBackup -BackupRoot $displayBackupRoot
        $restoreDisplay = $true
    }

    if (-not $SkipLaunch -and $baseConfigObject.launch.command) {
        Write-Learn "launch $($baseConfigObject.launch.command)"
        if ($baseConfigObject.launch.arguments) {
            Start-Process -FilePath $baseConfigObject.launch.command -ArgumentList @($baseConfigObject.launch.arguments)
        } else {
            Start-Process -FilePath $baseConfigObject.launch.command
        }
        $waitSeconds = $StartupWaitSeconds
        if ($baseConfigObject.launch.startup_wait_seconds -and $StartupWaitSeconds -le 0) {
            $waitSeconds = [int]$baseConfigObject.launch.startup_wait_seconds
        }
        if ($waitSeconds -gt 0) {
            Write-Learn "wait ${waitSeconds}s for startup"
            Start-Sleep -Seconds $waitSeconds
        }
    } else {
        Write-Learn "launch skipped"
    }

    $targetProcess = Focus-TargetWindow -WindowConfig $baseConfigObject.window
    $hiddenForegroundWindow = Hide-ForegroundWindowIfNotTarget -WindowConfig $baseConfigObject.window
    if ($hiddenForegroundWindow -ne [IntPtr]::Zero) {
        $targetProcess = Focus-TargetWindow -WindowConfig $baseConfigObject.window
    }
    Write-Learn "recording started"
    Write-Learn "F8 = milestone screenshot, F9 = leaderboard ready + finish, F10 = stop without final page loop"

    $keyMap = New-RecordedKeyMap
    $keyStates = @{}
    foreach ($key in $keyMap) {
        $keyStates[[int]$key.vk] = $false
    }

    $hotkeyStates = @{
        0x77 = $false
        0x78 = $false
        0x79 = $false
    }
    $leftMouseDown = $false
    $milestoneIndex = 0
    $milestoneLabels = @(
        "start_menu",
        "continue_menu",
        "garage_main_menu",
        "free_drive",
        "pause_menu",
        "online_tab",
        "rivals_hub",
        "rivals_mode_selection",
        "race_track_selection",
        "rival_selection",
        "leaderboard"
    )

    while ($true) {
        $targetProcess = Get-TargetProcess -WindowConfig $baseConfigObject.window
        if ($null -eq $targetProcess -or -not (Test-TargetForeground -Process $targetProcess)) {
            Start-Sleep -Milliseconds 200
            continue
        }

        $f8Down = Test-KeyDown -VirtualKey 0x77
        $f9Down = Test-KeyDown -VirtualKey 0x78
        $f10Down = Test-KeyDown -VirtualKey 0x79

        if ($f8Down -and -not $hotkeyStates[0x77]) {
            if ($milestoneIndex -lt $milestoneLabels.Count) {
                $label = $milestoneLabels[$milestoneIndex]
            } else {
                $label = "state_$('{0:000}' -f $milestoneIndex)"
            }
            $name = "$('{0:000}' -f $milestoneIndex)_$(ConvertTo-SafeName $label)"
            $snapshot = New-LearningSnapshot -SessionDir $sessionDir -StateDir $stateDir -Name $name -OcrLanguage $ocrLanguage -WindowConfig $baseConfigObject.window
            $events.Add([pscustomobject]@{
                type = "milestone"
                index = $milestoneIndex
                label = (ConvertTo-SafeName $label)
                time = (Get-Date).ToString("o")
                image_path = $snapshot.image_path
                ocr_json_path = $snapshot.ocr_json_path
                ocr_text_path = $snapshot.ocr_text_path
                ocr_line_count = $snapshot.ocr_line_count
                state_pattern = (New-StateMarkerPattern -Label (ConvertTo-SafeName $label) -OcrText $snapshot.ocr_text -OcrTextPath $snapshot.ocr_text_path)
            }) | Out-Null
            $milestoneIndex += 1
        }

        if ($f9Down -and -not $hotkeyStates[0x78]) {
            $snapshot = New-LearningSnapshot -SessionDir $sessionDir -StateDir $stateDir -Name "leaderboard_ready" -OcrLanguage $ocrLanguage -WindowConfig $baseConfigObject.window
            $events.Add([pscustomobject]@{
                type = "leaderboard_ready"
                time = (Get-Date).ToString("o")
                label = "leaderboard_ready"
                image_path = $snapshot.image_path
                ocr_json_path = $snapshot.ocr_json_path
                ocr_text_path = $snapshot.ocr_text_path
                ocr_line_count = $snapshot.ocr_line_count
                state_pattern = (New-StateMarkerPattern -Label "leaderboard_ready" -OcrText $snapshot.ocr_text -OcrTextPath $snapshot.ocr_text_path)
            }) | Out-Null
            $leaderboardReady = $true
            break
        }

        if ($f10Down -and -not $hotkeyStates[0x79]) {
            Write-Learn "stop hotkey pressed"
            break
        }

        $hotkeyStates[0x77] = $f8Down
        $hotkeyStates[0x78] = $f9Down
        $hotkeyStates[0x79] = $f10Down

        foreach ($key in $keyMap) {
            $vk = [int]$key.vk
            $isDown = Test-KeyDown -VirtualKey $vk
            if ($isDown -and -not $keyStates[$vk]) {
                $events.Add([pscustomobject]@{
                    type = "key"
                    key = [string]$key.name
                    time = (Get-Date).ToString("o")
                }) | Out-Null
                Write-Learn "key $($key.name)"
            }
            $keyStates[$vk] = $isDown
        }

        $mouseDown = Test-KeyDown -VirtualKey 0x01
        if ($mouseDown -and -not $leftMouseDown) {
            $cursor = Get-CursorPosition
            $windowBounds = Get-TargetWindowBounds -WindowConfig $baseConfigObject.window
            $events.Add([pscustomobject]@{
                type = "mouse_click"
                x = [int]$cursor.X
                y = [int]$cursor.Y
                x_relative = [int]($cursor.X - $windowBounds.X)
                y_relative = [int]($cursor.Y - $windowBounds.Y)
                x_ratio = [double](($cursor.X - $windowBounds.X) / [double]$windowBounds.Width)
                y_ratio = [double](($cursor.Y - $windowBounds.Y) / [double]$windowBounds.Height)
                window_x = [int]$windowBounds.X
                window_y = [int]$windowBounds.Y
                window_width = [int]$windowBounds.Width
                window_height = [int]$windowBounds.Height
                time = (Get-Date).ToString("o")
            }) | Out-Null
            Write-Learn "mouse click $($cursor.X),$($cursor.Y) relative $([int]($cursor.X - $windowBounds.X)),$([int]($cursor.Y - $windowBounds.Y))"
        }
        $leftMouseDown = $mouseDown

        Start-Sleep -Milliseconds 35
    }
}
finally {
    $postRunError = $null
    try {
        $eventArray = @($events.ToArray())
        $eventsPath = Join-Path $sessionDir "events.json"
        if ($eventArray.Count -eq 0) {
            "[]" | Set-Content -LiteralPath $eventsPath -Encoding UTF8
        } else {
            ConvertTo-Json -InputObject $eventArray -Depth 20 | Set-Content -LiteralPath $eventsPath -Encoding UTF8
        }
        Write-Learn "events saved $eventsPath"

        $learnedRoutePath = Save-LearnedRoute `
            -BaseConfigObject $baseConfigObject `
            -Events $eventArray `
            -OutputConfigPath $OutputConfig `
            -SessionDir $sessionDir `
            -HasLeaderboardReady $leaderboardReady
        Write-Learn "learned route saved $learnedRoutePath"

        $summaryPath = Join-Path $sessionDir "summary.txt"
        $summaryLines = @(
            "Forza learned route session",
            "created_at=$((Get-Date).ToString('o'))",
            "route=$learnedRoutePath",
            "events=$eventsPath",
            "leaderboard_ready=$leaderboardReady",
            "track=$Track",
            "rivals_mode=$RivalsMode",
            "performance_class=$PerformanceClass",
            "pages=$Pages",
            ""
        )
        if ($leaderboardReady) {
            $summaryLines += @(
                "Next dry-run:",
                "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 -Mode Capture -RouteConfig $OutputConfig -Track `"$Track`" -PerformanceClass $PerformanceClass -RivalsMode `"$RivalsMode`" -Pages $Pages -DryRun",
                "",
                "Next real capture:",
                "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_forza_pipeline.ps1 -Mode Capture -RouteConfig $OutputConfig -Track `"$Track`" -PerformanceClass $PerformanceClass -RivalsMode `"$RivalsMode`" -Pages $Pages"
            )
        } else {
            $summaryLines += @(
                "This is a partial route because F9 was not recorded.",
                "Rerun Learn and press F9 when the leaderboard is visible."
            )
        }
        $summaryLines | Set-Content -LiteralPath $summaryPath -Encoding UTF8
        Write-Learn "summary saved $summaryPath"
    }
    catch {
        $postRunError = $_
        Write-Learn "post-run save failed: $($_.Exception.Message)"
    }

    if ($restoreDisplay -and -not [string]::IsNullOrWhiteSpace($displayBackupDir)) {
        Write-Learn "restore FH6 display config from backup"
        Invoke-DisplayModeScript -Action "Restore" -BackupRoot $displayBackupRoot -RestoreBackupDir $displayBackupDir
    }

    Restore-HiddenForegroundWindow -WindowHandle $hiddenForegroundWindow

    if ($null -ne $postRunError) {
        throw $postRunError
    }
}

if ($leaderboardReady) {
    Write-Learn "done: leaderboard route learned"
} else {
    Write-Learn "done: route saved, but leaderboard was not marked with F9"
}
