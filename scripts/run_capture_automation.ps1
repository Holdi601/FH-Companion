param(
    [string] $Config = "config/capture_automation.example.json",
    [switch] $SkipLaunch,
    [switch] $SkipExtract,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WindowRect {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class WindowTools {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
}
"@

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [System.IO.Path]::GetFullPath((Join-Path $workspace $Path))
}

function Write-Step {
    param([Parameter(Mandatory = $true)][string] $Message)
    Write-Host "[forza-capture] $Message"
}

function Test-Truthy {
    param([AllowNull()] $Value)

    if ($null -eq $Value) {
        return $false
    }
    if ($Value -is [bool]) {
        return [bool]$Value
    }
    return (([string]$Value).Trim() -match "^(?i:true|1|yes|on)$")
}

function Format-StepName {
    param(
        [string] $Name,
        [Parameter(Mandatory = $true)] $ConfigObject,
        [int] $Index = 0,
        [string] $DefaultSuffix = "capture"
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        $Name = "$($ConfigObject.capture.prefix)_$DefaultSuffix`_{index:0000}.png"
    }

    $Name = $Name.Replace("{index:000}", ("{0:000}" -f $Index))
    $Name = $Name.Replace("{index:0000}", ("{0:0000}" -f $Index))
    return $Name
}

function Convert-KeyToSendKeys {
    param([Parameter(Mandatory = $true)][string] $Key)

    switch ($Key.ToUpperInvariant()) {
        "ENTER" { return "{ENTER}" }
        "ESC" { return "{ESC}" }
        "ESCAPE" { return "{ESC}" }
        "TAB" { return "{TAB}" }
        "SPACE" { return " " }
        "UP" { return "{UP}" }
        "DOWN" { return "{DOWN}" }
        "LEFT" { return "{LEFT}" }
        "RIGHT" { return "{RIGHT}" }
        "PAGEUP" { return "{PGUP}" }
        "PGUP" { return "{PGUP}" }
        "PAGEDOWN" { return "{PGDN}" }
        "PGDN" { return "{PGDN}" }
        "HOME" { return "{HOME}" }
        "END" { return "{END}" }
        "BACKSPACE" { return "{BACKSPACE}" }
        "DELETE" { return "{DELETE}" }
        default { return $Key }
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

function Get-TargetWindowDiagnostics {
    param([Parameter(Mandatory = $true)] $WindowConfig)

    $names = @()
    if ($WindowConfig.process_names) {
        $names += @($WindowConfig.process_names)
    }
    if ($WindowConfig.process_name) {
        $names += @($WindowConfig.process_name)
    }
    $names = @($names | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)

    $details = @()
    foreach ($name in $names) {
        $details += @(Get-Process -Name $name -ErrorAction SilentlyContinue |
            Select-Object ProcessName, Id, MainWindowHandle, MainWindowTitle, Responding)
    }
    if ($WindowConfig.title_regex) {
        $regex = [regex]$WindowConfig.title_regex
        $details += @(Get-Process |
            Where-Object { $_.MainWindowHandle -ne 0 -and ($_.MainWindowTitle -match $regex) } |
            Select-Object ProcessName, Id, MainWindowHandle, MainWindowTitle, Responding)
    }

    if (@($details).Count -eq 0) {
        return "No matching process/window was found. If you used -AlreadyAtLeaderboard, Forza must already be running and sitting on a visible Rivals leaderboard. If this track/class has no personal Rivals time yet, drive/post a time first so the Change Rival leaderboard exists."
    }

    return ((@($details) | Sort-Object ProcessName, Id -Unique | ForEach-Object {
        "process=$($_.ProcessName) pid=$($_.Id) hwnd=$($_.MainWindowHandle) title='$($_.MainWindowTitle)' responding=$($_.Responding)"
    }) -join "; ")
}

function Test-TargetForeground {
    param([Parameter(Mandatory = $true)] $Process)

    $foregroundWindow = [WindowTools]::GetForegroundWindow()
    if ($foregroundWindow -eq [IntPtr]::Zero) {
        return $false
    }

    [uint32] $foregroundProcessId = 0
    [WindowTools]::GetWindowThreadProcessId($foregroundWindow, [ref]$foregroundProcessId) | Out-Null
    return $foregroundProcessId -eq [uint32]$Process.Id
}

function Hide-ForegroundWindowIfNotTarget {
    param([Parameter(Mandatory = $true)] $WindowConfig)

    $foregroundWindow = [WindowTools]::GetForegroundWindow()
    if ($foregroundWindow -eq [IntPtr]::Zero) {
        return [IntPtr]::Zero
    }

    [uint32] $foregroundProcessId = 0
    [WindowTools]::GetWindowThreadProcessId($foregroundWindow, [ref]$foregroundProcessId) | Out-Null
    $targetProcess = Get-TargetProcess -WindowConfig $WindowConfig
    if ($null -ne $targetProcess -and $foregroundProcessId -eq [uint32]$targetProcess.Id) {
        return [IntPtr]::Zero
    }

    $foregroundProcess = Get-Process -Id $foregroundProcessId -ErrorAction SilentlyContinue
    if ($null -eq $foregroundProcess -or $foregroundProcess.ProcessName -ieq "explorer") {
        return [IntPtr]::Zero
    }

    Write-Step "minimize foreground window '$($foregroundProcess.ProcessName)' before game capture"
    [WindowTools]::ShowWindowAsync($foregroundWindow, 6) | Out-Null
    Start-Sleep -Milliseconds 500
    return $foregroundWindow
}

function Restore-HiddenForegroundWindow {
    param($WindowHandle)

    if ($null -ne $WindowHandle -and $WindowHandle -ne [IntPtr]::Zero) {
        [WindowTools]::ShowWindowAsync($WindowHandle, 9) | Out-Null
    }
}

function Focus-TargetWindow {
    param(
        [Parameter(Mandatory = $true)] $WindowConfig,
        [int] $TimeoutSeconds = 0,
        [switch] $AllowAltTab
    )

    $timeout = 120
    if ($WindowConfig.focus_timeout_seconds) {
        $timeout = [int]$WindowConfig.focus_timeout_seconds
    }
    if ($TimeoutSeconds -gt 0) {
        $timeout = $TimeoutSeconds
    }

    $deadline = (Get-Date).AddSeconds($timeout)
    $altTabAttempted = $false
    $shell = $null
    do {
        $process = Get-TargetProcess -WindowConfig $WindowConfig
        if ($null -ne $process) {
            [WindowTools]::ShowWindowAsync($process.MainWindowHandle, 9) | Out-Null
            Start-Sleep -Milliseconds 250
            [WindowTools]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            Start-Sleep -Milliseconds 250
            if (Test-TargetForeground -Process $process) {
                Write-Step "focused '$($process.MainWindowTitle)' (pid $($process.Id))"
                return $process
            }

            if ($AllowAltTab -and -not $altTabAttempted) {
                Write-Step "foreground fallback: Alt+Tab"
                if ($null -eq $shell) {
                    $shell = New-Object -ComObject WScript.Shell
                }
                $shell.SendKeys("%{TAB}")
                Start-Sleep -Milliseconds 800
                [WindowTools]::ShowWindowAsync($process.MainWindowHandle, 9) | Out-Null
                [WindowTools]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
                Start-Sleep -Milliseconds 250
                if (Test-TargetForeground -Process $process) {
                    Write-Step "focused '$($process.MainWindowTitle)' (pid $($process.Id))"
                    return $process
                }
                $altTabAttempted = $true
            }
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    $diagnostics = Get-TargetWindowDiagnostics -WindowConfig $WindowConfig
    throw "Could not find/focus target game window. Check window.process_names and window.title_regex. Diagnostics: $diagnostics"
}

function Test-EnsureFocusEnabled {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    if ($ConfigObject.window.ensure_focus_before_actions -eq $false) {
        return $false
    }
    return $true
}

function Get-RefocusTimeoutSeconds {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    if ($ConfigObject.window.refocus_timeout_seconds) {
        return [int]$ConfigObject.window.refocus_timeout_seconds
    }
    return 8
}

function Test-AltTabFallbackEnabled {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    return (Test-Truthy $ConfigObject.window.alt_tab_fallback)
}

function Test-IdleOnlyEnabled {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    return (Test-Truthy $ConfigObject.window.idle_only)
}

function Test-RestorePreviousFocusEnabled {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    return (Test-Truthy $ConfigObject.window.restore_previous_after_run)
}

function Get-IdleSeconds {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    $seconds = 30.0
    if ($ConfigObject.window.idle_seconds) {
        $seconds = [double]$ConfigObject.window.idle_seconds
    }
    return [Math]::Max(1.0, $seconds)
}

function Get-IdlePollMilliseconds {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    $seconds = 2.0
    if ($ConfigObject.window.idle_poll_seconds) {
        $seconds = [double]$ConfigObject.window.idle_poll_seconds
    }
    return [int]([Math]::Max(0.25, $seconds) * 1000)
}

function Get-UserIdleSeconds {
    $info = New-Object WindowTools+LASTINPUTINFO
    $info.cbSize = [uint32][System.Runtime.InteropServices.Marshal]::SizeOf($info)
    if (-not [WindowTools]::GetLastInputInfo([ref]$info)) {
        return 0.0
    }

    $current = [uint32][WindowTools]::GetTickCount()
    $current64 = [uint64]$current
    $last64 = [uint64]([uint32]$info.dwTime)
    if ($current64 -ge $last64) {
        return ([double]($current64 - $last64) / 1000.0)
    }
    return ([double]((4294967296.0 - [double]$last64) + [double]$current64) / 1000.0)
}

function Wait-ForUserIdle {
    param(
        [Parameter(Mandatory = $true)] $ConfigObject,
        [string] $Reason = "focus"
    )

    if ($DryRun -or -not (Test-IdleOnlyEnabled -ConfigObject $ConfigObject)) {
        return
    }

    $required = Get-IdleSeconds -ConfigObject $ConfigObject
    $pollMs = Get-IdlePollMilliseconds -ConfigObject $ConfigObject
    $lastLog = [DateTime]::MinValue
    while ($true) {
        $idle = Get-UserIdleSeconds
        if ($idle -ge $required) {
            return
        }
        $now = Get-Date
        if (($now - $lastLog).TotalSeconds -ge 15) {
            Write-Step ("idle wait before {0}: user idle {1:N1}/{2:N0}s" -f $Reason, $idle, $required)
            $lastLog = $now
        }
        Start-Sleep -Milliseconds $pollMs
    }
}

function Ensure-TargetFocus {
    param(
        [Parameter(Mandatory = $true)] $ConfigObject,
        [string] $Reason = "action"
    )

    if ($DryRun -or -not (Test-EnsureFocusEnabled -ConfigObject $ConfigObject)) {
        return
    }

    $process = Get-TargetProcess -WindowConfig $ConfigObject.window
    if ($null -ne $process -and (Test-TargetForeground -Process $process)) {
        return
    }

    Write-Step "ensure focus before $Reason"
    Wait-ForUserIdle -ConfigObject $ConfigObject -Reason $Reason
    Focus-TargetWindow `
        -WindowConfig $ConfigObject.window `
        -TimeoutSeconds (Get-RefocusTimeoutSeconds -ConfigObject $ConfigObject) `
        -AllowAltTab:(Test-AltTabFallbackEnabled -ConfigObject $ConfigObject) | Out-Null
}

function Restore-PreviousForegroundWindow {
    param(
        [Parameter(Mandatory = $true)] $ConfigObject,
        $WindowHandle
    )

    if ($DryRun -or -not (Test-RestorePreviousFocusEnabled -ConfigObject $ConfigObject)) {
        return
    }
    if ($null -eq $WindowHandle -or $WindowHandle -eq [IntPtr]::Zero -or -not [WindowTools]::IsWindow($WindowHandle)) {
        return
    }

    [uint32] $previousProcessId = 0
    [WindowTools]::GetWindowThreadProcessId($WindowHandle, [ref]$previousProcessId) | Out-Null
    $targetProcess = Get-TargetProcess -WindowConfig $ConfigObject.window
    if ($null -ne $targetProcess -and $previousProcessId -eq [uint32]$targetProcess.Id) {
        return
    }

    $previousProcess = Get-Process -Id $previousProcessId -ErrorAction SilentlyContinue
    if ($null -ne $previousProcess) {
        Write-Step "restore previous foreground window '$($previousProcess.ProcessName)'"
    } else {
        Write-Step "restore previous foreground window"
    }
    [WindowTools]::ShowWindowAsync($WindowHandle, 9) | Out-Null
    Start-Sleep -Milliseconds 150
    [WindowTools]::SetForegroundWindow($WindowHandle) | Out-Null
}

function Get-TargetWindowBounds {
    param([Parameter(Mandatory = $true)] $WindowConfig)

    $process = Get-TargetProcess -WindowConfig $WindowConfig
    if ($null -eq $process) {
        throw "Could not find target game window for screenshot bounds."
    }

    $rect = New-Object WindowRect
    if (-not [WindowTools]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) {
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
        $bounds = [pscustomobject]@{
            X = $screenBounds.X
            Y = $screenBounds.Y
            Width = $screenBounds.Width
            Height = $screenBounds.Height
        }
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

    Write-Step "screenshot $absolutePath ($($bounds.Width)x$($bounds.Height) at $($bounds.X),$($bounds.Y))"
    return $absolutePath
}

function Get-OcrLanguage {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    if ($ConfigObject.capture.ocr_language) {
        return [string]$ConfigObject.capture.ocr_language
    }
    return "en"
}

function Get-StateOutputDir {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    if ($ConfigObject.capture.state_dir) {
        return Resolve-WorkspacePath ([string]$ConfigObject.capture.state_dir)
    }
    return Join-Path (Resolve-WorkspacePath $ConfigObject.capture.output_dir) "_state"
}

function Invoke-ImageOcr {
    param(
        [Parameter(Mandatory = $true)][string] $ImagePath,
        [Parameter(Mandatory = $true)][string] $Language,
        [int] $TimeoutSeconds = 35
    )

    $ocrScript = Resolve-WorkspacePath "scripts/windows_ocr.ps1"
    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$ocrScript`"",
        "-ImagePath", "`"$ImagePath`"",
        "-Language", $Language
    )

    try {
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw "OCR timed out after ${TimeoutSeconds}s for $ImagePath"
        }
        $process.WaitForExit()
        $process.Refresh()
        $output = Get-Content -LiteralPath $stdoutPath -Raw -Encoding UTF8
        $stderr = ""
        if (Test-Path -LiteralPath $stderrPath) {
            $stderr = (Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue)
        }
        if ([string]::IsNullOrWhiteSpace($output)) {
            throw "OCR returned no output for $ImagePath. Exit code $($process.ExitCode): $stderr"
        }
        if ($null -ne $process.ExitCode -and $process.ExitCode -ne 0) {
            throw "OCR failed for $ImagePath with exit code $($process.ExitCode): $stderr"
        }
        return Convert-OcrJson -RawJson $output -ImagePath $ImagePath
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
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
        [Parameter(Mandatory = $true)] $ConfigObject
    )

    $stateDir = Get-StateOutputDir -ConfigObject $ConfigObject
    New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
    $name = [System.IO.Path]::GetFileNameWithoutExtension($ImagePath)
    $jsonPath = Join-Path $stateDir "$name.ocr.json"
    $txtPath = Join-Path $stateDir "$name.ocr.txt"
    $lines = @($Ocr.lines | ForEach-Object { $_.text })

    $Ocr | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    ($lines -join [Environment]::NewLine) | Set-Content -LiteralPath $txtPath -Encoding UTF8
    Write-Step "ocr dump $jsonPath"
}

function New-OcrSnapshot {
    param(
        [Parameter(Mandatory = $true)] $ConfigObject,
        [int] $Index = 0,
        [string] $Name = "",
        [switch] $Dump
    )

    $captureDir = Resolve-WorkspacePath $ConfigObject.capture.output_dir
    $fileName = Format-StepName -Name $Name -ConfigObject $ConfigObject -Index $Index -DefaultSuffix "observe"
    Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "OCR screenshot"
    $imagePath = Capture-Screenshot -OutputPath (Join-Path $captureDir $fileName) -WindowConfig $ConfigObject.window
    $ocr = Invoke-ImageOcr -ImagePath $imagePath -Language (Get-OcrLanguage -ConfigObject $ConfigObject)

    if ($Dump -or $ConfigObject.capture.dump_state_ocr) {
        Save-OcrDump -Ocr $ocr -ImagePath $imagePath -ConfigObject $ConfigObject
    }

    return [pscustomobject]@{
        ImagePath = $imagePath
        Ocr = $ocr
    }
}

function Find-OcrText {
    param(
        [Parameter(Mandatory = $true)] $Ocr,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $matches = @()

    foreach ($line in @($Ocr.lines)) {
        if ($regex.IsMatch([string]$line.text)) {
            $matches += $line
        }
    }

    if ($matches.Count -eq 0) {
        $allText = (@($Ocr.lines | ForEach-Object { $_.text }) -join " ")
        if ($regex.IsMatch($allText)) {
            return [pscustomobject]@{
                text = $allText
                x = 0
                y = 0
                width = $Ocr.width
                height = $Ocr.height
            }
        }
        return $null
    }

    return $matches | Sort-Object y, x | Select-Object -First 1
}

function Test-OcrText {
    param(
        [Parameter(Mandatory = $true)] $Ocr,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    return $null -ne (Find-OcrText -Ocr $Ocr -Pattern $Pattern)
}

function Invoke-MouseClick {
    param(
        [Parameter(Mandatory = $true)][int] $X,
        [Parameter(Mandatory = $true)][int] $Y,
        [int] $DelayMs = 300
    )

    $mouseEventLeftDown = 0x0002
    $mouseEventLeftUp = 0x0004
    [WindowTools]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 80
    [WindowTools]::mouse_event($mouseEventLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WindowTools]::mouse_event($mouseEventLeftUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $DelayMs
}

function Invoke-MouseDrag {
    param(
        [Parameter(Mandatory = $true)][int] $StartX,
        [Parameter(Mandatory = $true)][int] $StartY,
        [Parameter(Mandatory = $true)][int] $EndX,
        [Parameter(Mandatory = $true)][int] $EndY,
        [int] $DurationMs = 250,
        [int] $DelayMs = 300
    )

    $mouseEventLeftDown = 0x0002
    $mouseEventLeftUp = 0x0004
    [WindowTools]::SetCursorPos($StartX, $StartY) | Out-Null
    Start-Sleep -Milliseconds 60
    [WindowTools]::mouse_event($mouseEventLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    $steps = [Math]::Max(4, [int][Math]::Ceiling($DurationMs / 15.0))
    $stepDelay = [Math]::Max(1, [int][Math]::Floor($DurationMs / [double]$steps))
    for ($i = 1; $i -le $steps; $i += 1) {
        $ratio = $i / [double]$steps
        $x = [int][Math]::Round($StartX + (($EndX - $StartX) * $ratio))
        $y = [int][Math]::Round($StartY + (($EndY - $StartY) * $ratio))
        [WindowTools]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds $stepDelay
    }
    [WindowTools]::mouse_event($mouseEventLeftUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $DelayMs
}

function Send-AutomationKey {
    param(
        [Parameter(Mandatory = $true)] $Shell,
        [Parameter(Mandatory = $true)][string] $Key,
        [int] $Count = 1,
        [int] $DelayMs = 250,
        [switch] $Burst,
        [int] $BurstSize = 250,
        $ConfigObject = $null
    )

    $sequence = Convert-KeyToSendKeys $Key
    if ($null -ne $ConfigObject) {
        Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "key $Key"
    }
    if ($Burst -and $Count -gt 1 -and $sequence -match '^\{([A-Z]+)\}$') {
        $burstKeyToken = $Matches[1]
        $remaining = $Count
        $sent = 0
        $resolvedBurstSize = [Math]::Max(1, $BurstSize)
        while ($remaining -gt 0) {
            $burstCount = [Math]::Min($resolvedBurstSize, $remaining)
            $burstSequence = "{$burstKeyToken $burstCount}"
            $Shell.SendKeys($burstSequence)
            $sent += $burstCount
            $remaining -= $burstCount
            if ($remaining -gt 0 -and $DelayMs -gt 0) {
                Start-Sleep -Milliseconds $DelayMs
            }
            if ($Count -ge 10000 -and ($sent % 10000 -eq 0 -or $remaining -eq 0)) {
                Write-Step "key $Key burst progress $sent/$Count"
            }
        }
        return
    }

    $focusEvery = if ($Count -gt 250) { 250 } else { $Count + 1 }
    for ($i = 0; $i -lt $Count; $i += 1) {
        if ($null -ne $ConfigObject -and $i -gt 0 -and ($i % $focusEvery) -eq 0) {
            Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "key $Key"
        }
        $Shell.SendKeys($sequence)
        Start-Sleep -Milliseconds $DelayMs
        if ($Count -ge 500 -and (($i + 1) % 500) -eq 0) {
            Write-Step "key $Key progress $($i + 1)/$Count"
        }
    }
}

function Send-AutomationKeyForDuration {
    param(
        [Parameter(Mandatory = $true)] $Shell,
        [Parameter(Mandatory = $true)][string] $Key,
        [double] $DurationSeconds = 7.0,
        [int] $BurstSize = 10,
        [int] $BurstPauseMs = 10,
        $ConfigObject = $null
    )

    $sequence = Convert-KeyToSendKeys $Key
    if ($sequence -notmatch '^\{([A-Z]+)\}$') {
        throw "Timed key spam requires a named key such as DOWN or UP: $Key"
    }
    $keyToken = $Matches[1]
    if ($null -ne $ConfigObject) {
        Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "timed key $Key"
    }

    $resolvedBurstSize = [Math]::Max(1, $BurstSize)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $sent = 0
    while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $Shell.SendKeys("{$keyToken $resolvedBurstSize}")
        $sent += $resolvedBurstSize
        if ($BurstPauseMs -gt 0) {
            Start-Sleep -Milliseconds $BurstPauseMs
        }
    }
    Write-Step ("timed key {0} sent {1} inputs in {2:N2}s" -f $Key, $sent, $stopwatch.Elapsed.TotalSeconds)
}

function ConvertTo-IntegerFromText {
    param([AllowEmptyString()][string] $Value)

    $digits = ([string]$Value) -replace "[^0-9]", ""
    if ([string]::IsNullOrWhiteSpace($digits)) {
        return 0
    }
    return [int]$digits
}

function Resolve-RepeatCount {
    param(
        [Parameter(Mandatory = $true)] $Step,
        [Parameter(Mandatory = $true)] $ConfigObject,
        [int] $Index = 0
    )

    $rawCount = [string]$Step.count
    if ([string]::IsNullOrWhiteSpace($rawCount)) {
        return 0
    }
    if ($rawCount -notmatch "^(?i:all)$") {
        return [int]$Step.count
    }

    $rowsPerPage = 11
    if ($Step.rows_per_page) {
        $rowsPerPage = [int]$Step.rows_per_page
    } elseif ($ConfigObject.capture.leaderboard_rows_per_page) {
        $rowsPerPage = [int]$ConfigObject.capture.leaderboard_rows_per_page
    }
    if ($rowsPerPage -lt 1) {
        $rowsPerPage = 11
    }

    $snapshot = New-OcrSnapshot -ConfigObject $ConfigObject -Index $Index -Name "leaderboard_total_players_{index:000}.png" -Dump
    $allText = (@($snapshot.Ocr.lines | ForEach-Object { $_.text }) -join " ")
    $match = [regex]::Match($allText, "([0-9][0-9., ]*)\s+Players", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw "Could not detect total leaderboard player count for Pages=All. Expected footer text like '20,381 Players'."
    }

    $players = ConvertTo-IntegerFromText -Value $match.Groups[1].Value
    if ($players -lt 1) {
        throw "Detected invalid leaderboard player count for Pages=All: $($match.Groups[1].Value)"
    }

    $count = [int][Math]::Ceiling($players / [double]$rowsPerPage)
    Write-Step "repeat all resolved: $players players / $rowsPerPage rows per screenshot = $count screenshots"
    return $count
}

function Invoke-AutomationSteps {
    param(
        [Parameter(Mandatory = $true)] $Steps,
        [Parameter(Mandatory = $true)] $ConfigObject,
        [int] $Index = 0,
        [int] $RepeatIndex = -1,
        [int] $RepeatCount = 0
    )

    $captureDir = Resolve-WorkspacePath $ConfigObject.capture.output_dir
    $shell = New-Object -ComObject WScript.Shell
    $stepIndex = 0

    foreach ($step in $Steps) {
        $stepIndex += 1
        $action = [string]$step.action
        switch ($action.ToLowerInvariant()) {
            "wait" {
                $seconds = [double]$step.seconds
                if ($step.label) {
                    Write-Step "wait ${seconds}s ($($step.label))"
                } else {
                    Write-Step "wait ${seconds}s"
                }
                if (-not $DryRun) {
                    Start-Sleep -Milliseconds ([int]($seconds * 1000))
                }
            }
            "focus" {
                Write-Step "focus target window"
                if (-not $DryRun) {
                    Wait-ForUserIdle -ConfigObject $ConfigObject -Reason "focus"
                    Focus-TargetWindow -WindowConfig $ConfigObject.window | Out-Null
                }
            }
            "key" {
                $count = 1
                if ($step.count) {
                    $count = [int]$step.count
                }
                $delayMs = 250
                if ($step.delay_ms) {
                    $delayMs = [int]$step.delay_ms
                }
                if ($step.skip_on_last_repeat -and $RepeatCount -gt 0 -and $RepeatIndex -eq ($RepeatCount - 1)) {
                    Write-Step "skip key $($step.key) on last repeat"
                    continue
                }
                if ($RepeatIndex -eq 0 -and $step.first_count) {
                    $count = [int]$step.first_count
                }
                if ($RepeatIndex -eq 0 -and $step.first_delay_ms) {
                    $delayMs = [int]$step.first_delay_ms
                }
                $burst = Test-Truthy $step.burst
                $burstSize = 250
                if ($step.burst_size) {
                    $burstSize = [int]$step.burst_size
                }
                $settleAfterMs = 0
                if ($step.settle_after_ms) {
                    $settleAfterMs = [int]$step.settle_after_ms
                }
                $modeText = if ($burst -and $count -gt 1) { " burst=$burstSize" } else { "" }
                Write-Step "key $($step.key) x$count$modeText"
                if (-not $DryRun) {
                    Send-AutomationKey -Shell $shell -Key ([string]$step.key) -Count $count -DelayMs $delayMs -Burst:$burst -BurstSize $burstSize -ConfigObject $ConfigObject
                    if ($settleAfterMs -gt 0) {
                        Start-Sleep -Milliseconds $settleAfterMs
                    }
                }
            }
            "keys" {
                $sequence = [string]$step.sequence
                $delayMs = 250
                if ($step.delay_ms) {
                    $delayMs = [int]$step.delay_ms
                }
                Write-Step "keys '$sequence'"
                if (-not $DryRun) {
                    Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "keys"
                    $shell.SendKeys($sequence)
                    Start-Sleep -Milliseconds $delayMs
                }
            }
            "key_for_duration" {
                $durationSeconds = 7.0
                $burstSize = 10
                $burstPauseMs = 10
                $settleAfterMs = 250
                if ($step.duration_seconds) {
                    $durationSeconds = [double]$step.duration_seconds
                }
                if ($step.burst_size) {
                    $burstSize = [int]$step.burst_size
                }
                if ($step.burst_pause_ms -ne $null) {
                    $burstPauseMs = [int]$step.burst_pause_ms
                }
                if ($step.settle_after_ms -ne $null) {
                    $settleAfterMs = [int]$step.settle_after_ms
                }
                Write-Step ("key {0} for {1:N2}s (burst {2}, pause {3}ms)" -f $step.key, $durationSeconds, $burstSize, $burstPauseMs)
                if (-not $DryRun) {
                    Send-AutomationKeyForDuration -Shell $shell -Key ([string]$step.key) -DurationSeconds $durationSeconds -BurstSize $burstSize -BurstPauseMs $burstPauseMs -ConfigObject $ConfigObject
                    if ($settleAfterMs -gt 0) {
                        Start-Sleep -Milliseconds $settleAfterMs
                    }
                }
            }
            "screenshot" {
                $name = Format-StepName -Name ([string]$step.name) -ConfigObject $ConfigObject -Index $Index -DefaultSuffix "screenshot"
                $path = Join-Path $captureDir $name
                if ($DryRun) {
                    Write-Step "would screenshot $path"
                } else {
                    Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "screenshot"
                    Capture-Screenshot -OutputPath $path -WindowConfig $ConfigObject.window
                }
            }
            "observe" {
                $name = Format-StepName -Name ([string]$step.name) -ConfigObject $ConfigObject -Index $Index -DefaultSuffix "observe"
                if ($DryRun) {
                    Write-Step "would observe screenshot $name"
                } else {
                    if (Test-Truthy $step.ocr) {
                        $snapshot = New-OcrSnapshot -ConfigObject $ConfigObject -Index $Index -Name $name -Dump
                        $lineCount = @($snapshot.Ocr.lines).Count
                        Write-Step "observed $lineCount OCR lines"
                    } else {
                        Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "observe screenshot"
                        Capture-Screenshot -OutputPath (Join-Path $captureDir $name) -WindowConfig $ConfigObject.window | Out-Null
                        Write-Step "observed screenshot $name"
                    }
                }
            }
            "wait_for_text" {
                $pattern = [string]$step.pattern
                $timeout = 20
                if ($step.timeout_seconds) {
                    $timeout = [int]$step.timeout_seconds
                }
                $interval = 1
                if ($step.interval_seconds) {
                    $interval = [double]$step.interval_seconds
                }
                Write-Step "wait for text /$pattern/"
                if (-not $DryRun) {
                    $deadline = (Get-Date).AddSeconds($timeout)
                    $found = $false
                    $lastOcrError = ""
                    do {
                        $snapshot = $null
                        try {
                            $snapshot = New-OcrSnapshot -ConfigObject $ConfigObject -Index $Index -Name ([string]$step.name)
                        } catch {
                            $lastOcrError = $_.Exception.Message
                            Write-Step "OCR retry while waiting: $lastOcrError"
                            Start-Sleep -Milliseconds ([int]($interval * 1000))
                            continue
                        }
                        if (Test-OcrText -Ocr $snapshot.Ocr -Pattern $pattern) {
                            Write-Step "text found /$pattern/"
                            $found = $true
                            break
                        }
                        Start-Sleep -Milliseconds ([int]($interval * 1000))
                    } while ((Get-Date) -lt $deadline)
                    if (-not $found) {
                        if (-not [string]::IsNullOrWhiteSpace($lastOcrError)) {
                            Write-Step "last OCR error while waiting: $lastOcrError"
                        }
                        throw "Timed out waiting for OCR text: $pattern"
                    }
                }
            }
            "key_until_text" {
                $pattern = [string]$step.pattern
                $maxPresses = 12
                if ($step.max_presses) {
                    $maxPresses = [int]$step.max_presses
                }
                $delayMs = 500
                if ($step.delay_ms) {
                    $delayMs = [int]$step.delay_ms
                }
                Write-Step "key $($step.key) until /$pattern/ (max $maxPresses)"
                if (-not $DryRun) {
                    for ($press = 0; $press -le $maxPresses; $press += 1) {
                        $snapshot = New-OcrSnapshot -ConfigObject $ConfigObject -Index $press -Name ([string]$step.name)
                        if (Test-OcrText -Ocr $snapshot.Ocr -Pattern $pattern) {
                            Write-Step "text found /$pattern/"
                            break
                        }
                        if ($press -eq $maxPresses) {
                            throw "Text was not found after $maxPresses key presses: $pattern"
                        }
                        Send-AutomationKey -Shell $shell -Key ([string]$step.key) -Count 1 -DelayMs $delayMs -ConfigObject $ConfigObject
                    }
                }
            }
            "click" {
                $delayMs = 300
                if ($step.delay_ms) {
                    $delayMs = [int]$step.delay_ms
                }
                if ($DryRun) {
                    Write-Step "would click x=$($step.x) y=$($step.y)"
                } else {
                    Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "click"
                    $windowBounds = Get-TargetWindowBounds -WindowConfig $ConfigObject.window
                    if ($step.x_ratio -ne $null -and $step.y_ratio -ne $null) {
                        $x = [int]($windowBounds.X + $windowBounds.Width * [double]$step.x_ratio)
                        $y = [int]($windowBounds.Y + $windowBounds.Height * [double]$step.y_ratio)
                    } else {
                        $x = [int]$step.x
                        $y = [int]$step.y
                    }
                    Invoke-MouseClick -X $x -Y $y -DelayMs $delayMs
                    Write-Step "clicked $x,$y"
                }
            }
            "drag" {
                $durationMs = 250
                $delayMs = 300
                if ($step.duration_ms) {
                    $durationMs = [int]$step.duration_ms
                }
                if ($step.delay_ms) {
                    $delayMs = [int]$step.delay_ms
                }
                if ($DryRun) {
                    Write-Step "would drag ratio $($step.start_x_ratio),$($step.start_y_ratio) -> $($step.end_x_ratio),$($step.end_y_ratio)"
                } else {
                    Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "drag"
                    $windowBounds = Get-TargetWindowBounds -WindowConfig $ConfigObject.window
                    $startX = [int]($windowBounds.X + $windowBounds.Width * [double]$step.start_x_ratio)
                    $startY = [int]($windowBounds.Y + $windowBounds.Height * [double]$step.start_y_ratio)
                    $endX = [int]($windowBounds.X + $windowBounds.Width * [double]$step.end_x_ratio)
                    $endY = [int]($windowBounds.Y + $windowBounds.Height * [double]$step.end_y_ratio)
                    Invoke-MouseDrag -StartX $startX -StartY $startY -EndX $endX -EndY $endY -DurationMs $durationMs -DelayMs $delayMs
                    Write-Step "dragged $startX,$startY -> $endX,$endY"
                }
            }
            "click_text" {
                $pattern = [string]$step.pattern
                $offsetX = 0
                $offsetY = 0
                $delayMs = 300
                if ($step.offset_x) {
                    $offsetX = [int]$step.offset_x
                }
                if ($step.offset_y) {
                    $offsetY = [int]$step.offset_y
                }
                if ($step.delay_ms) {
                    $delayMs = [int]$step.delay_ms
                }
                Write-Step "click text /$pattern/"
                if (-not $DryRun) {
                    $snapshot = New-OcrSnapshot -ConfigObject $ConfigObject -Index $Index -Name ([string]$step.name) -Dump
                    $match = Find-OcrText -Ocr $snapshot.Ocr -Pattern $pattern
                    if ($null -eq $match) {
                        throw "Could not find OCR text to click: $pattern"
                    }
                    $windowBounds = Get-TargetWindowBounds -WindowConfig $ConfigObject.window
                    $x = [int]($windowBounds.X + [double]$match.x + [double]$match.width / 2 + $offsetX)
                    $y = [int]($windowBounds.Y + [double]$match.y + [double]$match.height / 2 + $offsetY)
                    Ensure-TargetFocus -ConfigObject $ConfigObject -Reason "click text"
                    Invoke-MouseClick -X $x -Y $y -DelayMs $delayMs
                    Write-Step "clicked text '$($match.text)' at $x,$y"
                }
            }
            "repeat" {
                if ($DryRun -and ([string]$step.count).Trim() -match "^(?i:all)$") {
                    Write-Step "repeat All (would resolve from leaderboard footer at runtime)"
                    continue
                }
                $count = Resolve-RepeatCount -Step $step -ConfigObject $ConfigObject -Index $Index
                Write-Step "repeat $count"
                for ($i = 0; $i -lt $count; $i += 1) {
                    Invoke-AutomationSteps -Steps $step.steps -ConfigObject $ConfigObject -Index $i -RepeatIndex $i -RepeatCount $count
                }
            }
            default {
                throw "Unsupported automation step action: $action"
            }
        }
    }
}

function Invoke-Extractor {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    if (-not $ConfigObject.extraction.enabled) {
        Write-Step "extraction disabled"
        return
    }

    $extract = $ConfigObject.extraction
    $inputDir = Resolve-WorkspacePath $ConfigObject.capture.output_dir
    $profile = Resolve-WorkspacePath $extract.profile
    $outputDir = Resolve-WorkspacePath $extract.output_dir
    $performanceClass = [string]$extract.pi_class
    if ($extract.performance_class) {
        $performanceClass = [string]$extract.performance_class
    }
    $ocrBackend = "windows-batch"
    if ($extract.ocr_backend) {
        $ocrBackend = [string]$extract.ocr_backend
    }

    $args = @(
        (Resolve-WorkspacePath "scripts/extract_leaderboard.py"),
        "--input", $inputDir,
        "--track", [string]$extract.track,
        "--performance-class", $performanceClass,
        "--event-type", [string]$extract.event_type,
        "--rivals-mode", [string]$extract.rivals_mode,
        "--profile", $profile,
        "--output-dir", $outputDir,
        "--include-glob", "leaderboard_*.png",
        "--ocr-backend", $ocrBackend
    )

    if ($extract.ocr_mode) {
        $args += @("--ocr-mode", [string]$extract.ocr_mode)
    }
    if ($extract.dump_ocr) {
        $args += "--dump-ocr"
    }
    if ($extract.keep_crops) {
        $args += "--keep-crops"
    }
    if ($extract.sqlite_path) {
        $args += @("--sqlite-path", (Resolve-WorkspacePath ([string]$extract.sqlite_path)))
    }
    if ($extract.parquet_path) {
        $args += @("--parquet-path", (Resolve-WorkspacePath ([string]$extract.parquet_path)))
    }

    Write-Step "run extractor"
    & python @args
    if ($LASTEXITCODE -ne 0) {
        throw "Extractor failed with exit code $LASTEXITCODE"
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

function Test-IsChildPath {
    param(
        [Parameter(Mandatory = $true)][string] $Candidate,
        [Parameter(Mandatory = $true)][string] $Root
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($rootFull)) {
        return $false
    }
    return $candidateFull.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-AllowedClearRoots {
    param([Parameter(Mandatory = $true)] $ConfigObject)

    $roots = @($workspace, "C:\ForzaCaptures")
    if ($env:FORZA_ALLOWED_CLEAR_ROOTS) {
        $roots += @($env:FORZA_ALLOWED_CLEAR_ROOTS -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    if ($ConfigObject.capture.allowed_clear_roots) {
        $roots += @($ConfigObject.capture.allowed_clear_roots | ForEach-Object { [string]$_ })
    }
    return @($roots | Select-Object -Unique)
}

$configPath = Resolve-WorkspacePath $Config
$configObject = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

$displayBackupRoot = "data/backups/forza_display"
if ($configObject.display.backup_root) {
    $displayBackupRoot = [string]$configObject.display.backup_root
}
$displayBackupDir = $null
$restoreDisplay = $false
$hiddenForegroundWindow = [IntPtr]::Zero
$previousForegroundWindow = [IntPtr]::Zero

try {
    $captureDir = Resolve-WorkspacePath $configObject.capture.output_dir
    if ($configObject.capture.clear_output_dir -and (Test-Path -LiteralPath $captureDir)) {
        $resolvedCaptureDir = (Resolve-Path -LiteralPath $captureDir).Path
        $allowedClearRoots = @(Get-AllowedClearRoots -ConfigObject $configObject)
        $isAllowedClearDir = $false
        foreach ($root in $allowedClearRoots) {
            if (Test-IsChildPath -Candidate $resolvedCaptureDir -Root $root) {
                $isAllowedClearDir = $true
                break
            }
        }
        if (-not $isAllowedClearDir) {
            throw "Refusing to clear output directory outside allowed roots: $resolvedCaptureDir"
        }
        if (-not $DryRun) {
            Remove-Item -LiteralPath (Join-Path $resolvedCaptureDir "*") -Force -Recurse -ErrorAction SilentlyContinue
        }
    }
    New-Item -ItemType Directory -Force -Path $captureDir | Out-Null

    if ($configObject.display.manage) {
        if ($SkipLaunch) {
            Write-Step "display mode management skipped because -SkipLaunch was used"
        } else {
            $modeBeforeLaunch = "Windowed"
            if ($configObject.display.mode_before_launch) {
                $modeBeforeLaunch = [string]$configObject.display.mode_before_launch
            }
            Write-Step "set FH6 display mode before launch: $modeBeforeLaunch"
            Invoke-DisplayModeScript -Action $modeBeforeLaunch -BackupRoot $displayBackupRoot
            if (-not $DryRun) {
                $displayBackupDir = Get-LatestDisplayBackup -BackupRoot $displayBackupRoot
            }
            $restoreDisplay = [bool]$configObject.display.restore_after
        }
    }

    if (-not $SkipLaunch -and $configObject.launch.command) {
        Write-Step "launch $($configObject.launch.command)"
        if (-not $DryRun) {
            if ($configObject.launch.arguments) {
                Start-Process -FilePath $configObject.launch.command -ArgumentList @($configObject.launch.arguments)
            } else {
                Start-Process -FilePath $configObject.launch.command
            }
            if ($configObject.launch.startup_wait_seconds) {
                Start-Sleep -Seconds ([int]$configObject.launch.startup_wait_seconds)
            }
        }
    } else {
        Write-Step "launch skipped"
    }

    if (-not $DryRun) {
        Wait-ForUserIdle -ConfigObject $configObject -Reason "initial focus"
        if (Test-RestorePreviousFocusEnabled -ConfigObject $configObject) {
            $previousForegroundWindow = [WindowTools]::GetForegroundWindow()
        }
        Focus-TargetWindow `
            -WindowConfig $configObject.window `
            -AllowAltTab:(Test-AltTabFallbackEnabled -ConfigObject $configObject) | Out-Null
        if (-not (Test-IdleOnlyEnabled -ConfigObject $configObject)) {
            $hiddenForegroundWindow = Hide-ForegroundWindowIfNotTarget -WindowConfig $configObject.window
            if ($hiddenForegroundWindow -ne [IntPtr]::Zero) {
                Focus-TargetWindow `
                    -WindowConfig $configObject.window `
                    -AllowAltTab:(Test-AltTabFallbackEnabled -ConfigObject $configObject) | Out-Null
            }
        }
    }

    Invoke-AutomationSteps -Steps $configObject.steps -ConfigObject $configObject

    if (-not $SkipExtract) {
        if ($DryRun) {
            if ($configObject.extraction.enabled) {
                Write-Step "would run extractor"
            } else {
                Write-Step "extraction disabled"
            }
        } else {
            Invoke-Extractor -ConfigObject $configObject
        }
    }
}
finally {
    if ($restoreDisplay -and -not [string]::IsNullOrWhiteSpace($displayBackupDir)) {
        Write-Step "restore FH6 display config from backup"
        Invoke-DisplayModeScript -Action "Restore" -BackupRoot $displayBackupRoot -RestoreBackupDir $displayBackupDir
    } elseif ($restoreDisplay -and $DryRun) {
        Write-Step "would restore FH6 display config"
    }
    Restore-HiddenForegroundWindow -WindowHandle $hiddenForegroundWindow
    Restore-PreviousForegroundWindow -ConfigObject $configObject -WindowHandle $previousForegroundWindow
}

Write-Step "done"
