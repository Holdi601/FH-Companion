<#
.SYNOPSIS
    Self-contained in-VM navigator: drives Forza from wherever it is to a Rivals
    leaderboard, in one process, with no host round trips.

.DESCRIPTION
    This runs INSIDE the guest as a single interactive task. That is the whole
    point. The previous approach sent one key per host-side scheduled task, so
    inputs landed 10-30 seconds apart; the game auto-hides its HUD after a few
    seconds of inactivity, menus drift, and OCR sees nothing but the car. A game
    cannot be navigated at that cadence.

    Design consequences of that failure:

    * One process, one loop. The WinRT OCR engine is created once and reused, so
      a perceive-act cycle is a few hundred milliseconds instead of the seconds a
      per-frame `powershell.exe windows_ocr.ps1` spawn costs.
    * The HUD hiding is handled explicitly. When a frame yields no menu text, the
      mouse is nudged one pixel, which re-shows the HUD without changing the
      selected item -- unlike pressing a direction key, which would move the
      selection and desynchronise the route.
    * Focus is verified, not assumed. The game owns several top-level windows
      including a `ForzaFullscreenShadeWindow`; only the `App` window may be
      focused, and `SendKeys` reports success even when nothing received input.
    * Steps are data, not code, and each one is a goal with a bounded retry
      rather than a fixed keystroke count. `seek` presses a key until a marker
      appears; `press` sends a key and confirms the expected marker followed.
      A step that cannot reach its marker triggers `Reset-ToKnownState` rather
      than blindly continuing.

    Every frame's OCR text and each transition are logged, so a failed run says
    which state it was in and what it actually saw.

.PARAMETER Explore
    Do not follow the route. Walk the menus, recording each distinct screen and
    its marker text, to build the route table for a new game build without a
    human watching.
#>
[CmdletBinding()]
param(
    [string] $Track = "Soni Circuit",
    [string] $PerformanceClass = "R",
    [string] $RivalsMode = "Road Racing",
    [string] $OutputRoot = "C:\ForzaAutomation\data\navigation",
    [string] $RunId = "",
    [int] $FrameIntervalMs = 250,
    [int] $StepTimeoutSeconds = 90,
    [int] $LoadTimeoutSeconds = 420,
    [switch] $Explore,
    [switch] $KeepFrames,
    # Drive to the selected category's route list, then record every route name by
    # walking the carousel instead of selecting one. Writes routes.json and exits.
    # Used to build the board catalogue for a category without knowing its routes.
    [switch] $EnumerateRoutes,
    # Je Strecke die KARTE aufnehmen: Liste -> ENTER -> Klassenschirm
    # (dort liegt die magenta Linie) -> Bild -> ESC -> naechste.
    [switch] $EnumerateMaps,
    # Reach a route by its position in the carousel instead of by name. Route
    # names OCR unreliably (garbled mid-list, a spurious "Details" entry), but the
    # carousel ORDER is fixed, so an index reached from a clean anchor is robust.
    # -1 disables (name-based selection via -Track is used instead).
    [int] $RouteIndex = -1,
    # A route whose name OCRs cleanly, used to anchor the index walk. "Highway
    # Circuit" is carousel position 0 and reads cleanly in every capture.
    [string] $RouteAnchor = "Highway Circuit",
    [int] $RouteAnchorIndex = 0,
    [int] $RouteCount = 23,
    # Set when the run starts on the PREVIOUS board's leaderboard. Without it the
    # navigator sees a leaderboard, declares success and returns the wrong board,
    # which makes an unattended sweep silently rescan whatever was already open.
    [switch] $FromLeaderboard,
    # Nur die Aufnahme pruefen: Spielfenster finden, Aufnahmebereich bestimmen,
    # ein Bild holen, lesen, speichern, beenden. Drueckt keine Taste.
    [switch] $CaptureTest,
    # "x,y,breite,hoehe" in Bildschirmpunkten: den Aufnahmebereich erzwingen
    # statt ihn vom Spielfenster zu nehmen. Fuer Tests anderer Aufloesungen und
    # als Notausgang, falls ein Fenster seine Flaeche falsch meldet.
    [string] $CaptureArea = "",
    # Gespeicherte Frames (Datei oder Ordner) mit GENAU der Lese- und
    # Zustandslogik dieses Skripts einordnen, ohne Spiel und ohne Tasten.
    [string] $ClassifyFrames = "",
    # Mit -ClassifyFrames: jedes Bild vorher auf diese Hoehe (16:9) verkleinern
    # und wieder auf den Bezugsrahmen bringen -- so sieht die Aufnahme aus, wenn
    # das Spiel in dieser Aufloesung laeuft. 720 prueft die Untergrenze.
    [int] $SimulateHeight = 0
)

$ErrorActionPreference = "Stop"

if (-not $RunId) {
    $RunId = "{0}_{1}" -f (Get-Date -Format "yyyyMMdd_HHmmss"), ([guid]::NewGuid().ToString("N").Substring(0, 6))
}
$runRoot = Join-Path $OutputRoot $RunId
$frameRoot = Join-Path $runRoot "frames"
New-Item -ItemType Directory -Force -Path $runRoot, $frameRoot | Out-Null
$logPath = Join-Path $runRoot "navigator.log"
$statePath = Join-Path $runRoot "state.json"

# DIE STATUSZEILE UEBER DEM SPIEL (run_status_overlay.ps1). Waehrend eines Laufs
# ist Forza vorne, und sonst sieht niemand, ob gerade gearbeitet wird oder alles
# steht. Jede Protokollzeile wird dort zur Detailzeile.
$script:RunStatusPath = Join-Path (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path "data\runtime\run_status.json"
$script:RunPhase = if ($EnumerateMaps) { "Rivals maps: $RivalsMode" }
                   elseif ($EnumerateRoutes) { "Route list: $RivalsMode" }
                   else { "Navigator: $RivalsMode / $PerformanceClass" }

function Write-RunStatus {
    param([string] $Detail, [string] $State = "running")
    try {
        $eintrag = [ordered]@{ phase = $script:RunPhase; detail = $Detail; state = $State; updated = (Get-Date).ToString("o") }
        $tmp = "$($script:RunStatusPath).tmp"
        [IO.File]::WriteAllText($tmp, ($eintrag | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
        Move-Item -LiteralPath $tmp -Destination $script:RunStatusPath -Force
    } catch {
        # Die Anzeige ist Zugabe; sie darf nie einen Lauf abbrechen.
    }
}

function Write-Nav {
    param([Parameter(Mandatory = $true)][string] $Message)
    $line = "{0} {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $Message
    Write-Host "[forza-nav] $line"
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    $zustand = if ($Message -match '^FAILED|Stuck in state') { "error" } else { "running" }
    Write-RunStatus -Detail $Message -State $zustand
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.WindowsRuntime

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NavWin {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", EntryPoint="SetCursorPos")] static extern bool SetCursorPosRaw(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    // BEZUGSRAHMEN 1920x1080. Jede Koordinate in diesem Skript -- Ausschnitte,
    // Zeilenrechtecke, Parkplatz des Zeigers -- ist in diesem Rahmen gemessen,
    // in der VM. Auf einem 4K-Schirm wird das Bild auf den Rahmen verkleinert und
    // der Zeiger hier wieder auf den echten Schirm vergroessert. Forzas Oberflaeche
    // skaliert mit der Aufloesung, bei 16:9 liegt alles an derselben relativen Stelle.
    // Dazu der VERSATZ des Aufnahmebereichs: ein Spiel im Fenster, auf dem
    // zweiten Schirm oder mit schwarzen Raendern beginnt nicht bei 0,0.
    public static double ScaleX = 1.0, ScaleY = 1.0;
    public static int OffsetX = 0, OffsetY = 0;
    public static int ToScreenX(int x) { return OffsetX + (int)Math.Round(x * ScaleX); }
    public static int ToScreenY(int y) { return OffsetY + (int)Math.Round(y * ScaleY); }
    public static bool SetCursorPos(int x, int y) {
        return SetCursorPosRaw(ToScreenX(x), ToScreenY(y));
    }
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public struct POINT { public int X; public int Y; }

    public static IntPtr FindGameWindow(uint wantedPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != wantedPid || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
            if (cls.ToString() == "App") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Focus(IntPtr h) {
        if (h == IntPtr.Zero) return false;
        ShowWindow(h, 9);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        return GetForegroundWindow() == h;
    }
    // Park the cursor in a dead corner. Forza's menus are mouse-aware, so a
    // cursor left hovering over a list item pins the highlight there and
    // keyboard navigation appears to do nothing -- 64 DOWN presses moved the
    // route list not at all while the cursor sat over it.
    public static void ParkMouse(int x, int y) {
        SetCursorPos(x, y);
    }
    // Wake the auto-hidden HUD by jiggling inside the parked corner, so the
    // cursor never crosses a menu item.
    public static void NudgeMouse(int x, int y) {
        SetCursorPos(x, y);
        SetCursorPos(x - 1, y);
        SetCursorPos(x, y);
    }
    // The same mouse-awareness, used deliberately: park the cursor ON an item to
    // pin the highlight there. A single SetCursorPos sometimes lands without the
    // game seeing a move, so arrive with an actual delta.
    public static void HoverMouse(int x, int y) {
        SetCursorPos(x, y);
        SetCursorPos(x + 1, y);
        SetCursorPos(x, y);
    }
    public static void ClickMouse(int x, int y) {
        HoverMouse(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
}
'@

# ZUERST DPI-bewusst werden: ohne das meldet Windows bei 150 % Skalierung einen
# kleineren "Schirm" und CopyFromScreen nimmt nur einen Teil davon auf.
[void][NavWin]::SetProcessDPIAware()
$script:RefWidth = 1920
$script:RefHeight = 1080

# JEDE AUFLOESUNG AB 720p, bis 8K und 16K.
#
# Aufgenommen wird mit StretchBlt DIREKT in den 1920x1080-Bezugsrahmen. Ein
# Vollbild bei 16K waeren 132 Millionen Punkte, eine halbe Milliarde Bytes, alle
# 250 ms -- so entsteht nie ein Bild in voller Groesse, und 720p wird auf
# demselben Weg hochgerechnet. HALFTONE mittelt beim Verkleinern ueber alle
# Quellpunkte, statt einzelne herauszupicken; die duennen Streckenlinien und
# kleinen Schriften ueberleben das.
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class NavCapture {
    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
                                                          IntPtr src, int sx, int sy, int sw, int sh, int rop);
    const int HALFTONE = 4;
    const int SRCCOPY = 0x00CC0020;

    // Die Zeichenflaeche des Fensters in Bildschirmpunkten: x, y, Breite, Hoehe.
    public static int[] ClientArea(IntPtr h) {
        RECT r;
        if (!GetClientRect(h, out r)) return null;
        var p = new POINT();
        if (!ClientToScreen(h, ref p)) return null;
        return new int[] { p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static Bitmap CaptureScaled(int sx, int sy, int sw, int sh, int dw, int dh) {
        var bmp = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetDC(IntPtr.Zero);
            try {
                SetStretchBltMode(dst, HALFTONE);
                SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
                if (!StretchBlt(dst, 0, 0, dw, dh, src, sx, sy, sw, sh, SRCCOPY)) {
                    throw new InvalidOperationException("StretchBlt failed");
                }
            } finally {
                ReleaseDC(IntPtr.Zero, src);
                g.ReleaseHdc(dst);
            }
        }
        return bmp;
    }
}
'@

function Set-CaptureArea {
    <#
        Den Bereich festlegen, in dem das Spiel zu sehen ist, und Bild wie Zeiger
        darauf abbilden.

        SEITENVERHAELTNIS: gemessen ist alles bei 16:9. Bei 16:10, 21:9 oder 32:9
        wird der mittige 16:9-Ausschnitt genommen -- die ANNAHME ist, dass Forza
        seine Menues dort zeichnet. Auf einem 16:9-Schirm ist sie nicht pruefbar,
        darum steht sie laut im Protokoll statt still im Code.
    #>
    param(
        [Parameter(Mandatory = $true)][int] $X,
        [Parameter(Mandatory = $true)][int] $Y,
        [Parameter(Mandatory = $true)][int] $Width,
        [Parameter(Mandatory = $true)][int] $Height,
        [string] $Source = "screen"
    )
    $w = $Width
    $h = $Height
    if ($w * 9 -gt $h * 16) {
        $w = [int][math]::Round($h * 16 / 9)
    } elseif ($w * 9 -lt $h * 16) {
        $h = [int][math]::Round($w * 9 / 16)
    }
    if ($w -lt 1280 -or $h -lt 720) {
        # Darunter wird die Schrift zu klein fuer die OCR -- lieber sofort sagen
        # als hunderte Zyklen lang "unknown" lesen.
        throw ("Spielflaeche ${Width}x${Height} ($Source) ist kleiner als 720p; " +
               "der Navigator braucht mindestens 1280x720.")
    }
    $ox = $X + [int][math]::Floor(($Width - $w) / 2)
    $oy = $Y + [int][math]::Floor(($Height - $h) / 2)
    $script:CaptureRect = @{ X = $ox; Y = $oy; Width = $w; Height = $h }
    [NavWin]::OffsetX = $ox
    [NavWin]::OffsetY = $oy
    [NavWin]::ScaleX = $w / $script:RefWidth
    [NavWin]::ScaleY = $h / $script:RefHeight
    Write-Nav ("capture area from {0}: {1}x{2} at {3},{4}; used {5}x{6} at {7},{8}, scale {9:0.###}" -f
               $Source, $Width, $Height, $X, $Y, $w, $h, $ox, $oy, [NavWin]::ScaleX)
    if ($w -ne $Width -or $h -ne $Height) {
        Write-Nav ("WARNING the game area is not 16:9 -- using the centred 16:9 part. " +
                   "Menu positions at this aspect ratio are an assumption, not measured.")
    }
}

function Get-ForcedCaptureArea {
    if (-not $CaptureArea) { return $null }
    $teile = @($CaptureArea -split '\s*,\s*' | ForEach-Object { [int]$_ })
    if ($teile.Count -ne 4) { throw "-CaptureArea erwartet 'x,y,breite,hoehe', bekam '$CaptureArea'" }
    return $teile
}

# Bis das Spielfenster gefunden ist: der Hauptschirm.
$primary = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Set-CaptureArea -X $primary.X -Y $primary.Y -Width $primary.Width -Height $primary.Height -Source "primary screen"

# ---------------------------------------------------------------- OCR (once)

$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]

$script:AsTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq "AsTask" -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq "IAsyncOperation``1"
    } | Select-Object -First 1)

function Await-WinRt {
    param($Operation, [Type] $ResultType)
    $method = $script:AsTaskGeneric.MakeGenericMethod($ResultType)
    $task = $method.Invoke($null, @($Operation))
    $task.Wait(20000) | Out-Null
    return $task.Result
}

$script:OcrEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if (-not $script:OcrEngine) {
    throw "No Windows OCR engine is available in this session."
}

function Get-ScreenFrame {
    <#
        Resilient wrapper: a single capture occasionally throws a transient WinRT
        error ("Stream was not readable") when the OCR stream is reused rapidly.
        That once killed a whole enumeration run at frame 7. Retry a few times
        with a fresh capture before giving up.
    #>
    for ($attempt = 1; $attempt -le 4; $attempt += 1) {
        try {
            return Get-ScreenFrameOnce
        } catch {
            if ($attempt -ge 4) { throw }
            Write-Nav "screen capture retry $attempt after: $($_.Exception.Message)"
            Start-Sleep -Milliseconds 400
        }
    }
}

function Get-RegionText {
    <#
        OCR auf einem AUSSCHNITT des Bildschirms, als zweite Lesung neben der ganzen
        Aufnahme.

        Warum das noetig ist: '"D" Performance Class' wurde auf hellen Karten regelmaessig
        nicht gelesen, und ohne diese Zeile faellt der Klassenschirm auf 'rival_detail'
        oder 'unknown' zurueck -- Tateyama Kurobe Sprint gab deswegen zweimal auf, nach je
        150 Leseversuchen ueber 6,5 Minuten.
        Gemessen am 2026-08-26 an 24 dieser gescheiterten Frames: die ganze Aufnahme
        verliert die Zeile, ein Ausschnitt derselben Frames liest sie in 22 von 24 --
        '"B" Performance Class'. Es ist also kein Kontrastproblem, sondern die
        Segmentierung: in einem 1920x1080-Bild geht die kurze Zeile unter.

        Fehler werden geschluckt und leerer Text zurueckgegeben: diese Lesung ist eine
        ZUGABE zur vollen Aufnahme, sie darf einen Zyklus nie zum Scheitern bringen.
    #>
    param(
        [Parameter(Mandatory = $true)] $Bitmap,
        [Parameter(Mandatory = $true)][int] $X,
        [Parameter(Mandatory = $true)][int] $Y,
        [Parameter(Mandatory = $true)][int] $Width,
        [Parameter(Mandatory = $true)][int] $Height
    )
    try {
        if ($X -lt 0 -or $Y -lt 0) { return "" }
        if (($X + $Width) -gt $Bitmap.Width -or ($Y + $Height) -gt $Bitmap.Height) { return "" }
        $rect = New-Object System.Drawing.Rectangle($X, $Y, $Width, $Height)
        $crop = $Bitmap.Clone($rect, $Bitmap.PixelFormat)
        try {
            $memory = New-Object System.IO.MemoryStream
            $crop.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $memory.ToArray()
            $memory.Dispose()

            $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
            $writer = New-Object Windows.Storage.Streams.DataWriter($stream)
            $writer.WriteBytes($bytes)
            Await-WinRt ($writer.StoreAsync()) ([uint32]) | Out-Null
            $writer.DetachStream() | Out-Null
            $stream.Seek(0) | Out-Null

            $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $software = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $result = Await-WinRt ($script:OcrEngine.RecognizeAsync($software)) ([Windows.Media.Ocr.OcrResult])
            return (@($result.Lines | ForEach-Object { $_.Text }) -join "`n")
        } finally {
            $crop.Dispose()
        }
    } catch {
        return ""
    }
}

# Der Ausschnitt, in dem die Klassenzeile steht. Gemessen bei y=666, x 120-370 auf einem
# 1920x1080-Schirm; das Band ist grosszuegiger, damit ein Layoutwechsel nicht sofort
# daneben liegt.
$script:ClassLineRegion = @{ X = 90; Y = 644; Width = 520; Height = 52 }

function Get-ScreenFrameOnce {
    <#
        Capture the game's area and OCR it, keeping the bitmap in memory.
        Returns the recognised text plus the PNG bytes so a caller can persist
        only the frames that matter. The bitmap is always the 1920x1080
        reference frame, whatever the real resolution -- see Set-CaptureArea.
    #>
    param([System.Drawing.Bitmap] $FromBitmap)
    if ($FromBitmap) {
        # Ein gespeichertes Bild statt einer Aufnahme (-ClassifyFrames). Es wird
        # hier -- wie jede Aufnahme -- am Ende freigegeben.
        $bitmap = $FromBitmap
    } else {
        $r = $script:CaptureRect
        $bitmap = [NavCapture]::CaptureScaled($r.X, $r.Y, $r.Width, $r.Height,
                                              $script:RefWidth, $script:RefHeight)
    }
    try {
        $memory = New-Object System.IO.MemoryStream
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $memory.ToArray()
        $memory.Dispose()

        $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
        $writer = New-Object Windows.Storage.Streams.DataWriter($stream)
        $writer.WriteBytes($bytes)
        Await-WinRt ($writer.StoreAsync()) ([uint32]) | Out-Null
        $writer.DetachStream() | Out-Null
        $stream.Seek(0) | Out-Null

        $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $software = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $result = Await-WinRt ($script:OcrEngine.RecognizeAsync($software)) ([Windows.Media.Ocr.OcrResult])
        $text = (@($result.Lines | ForEach-Object { $_.Text }) -join "`n")

        # Zweite Lesung auf dem Ausschnitt mit der Klassenzeile, angehaengt an den
        # Bildschirmtext. Angehaengt und nicht ersetzt: alle Zustandsmuster arbeiten
        # weiter auf einem Text, und class_screen findet die Zeile jetzt auch dann, wenn
        # die ganze Aufnahme sie verliert. Siehe Get-RegionText.
        $zoomed = Get-RegionText -Bitmap $bitmap `
            -X $script:ClassLineRegion.X -Y $script:ClassLineRegion.Y `
            -Width $script:ClassLineRegion.Width -Height $script:ClassLineRegion.Height
        if ($zoomed -and $zoomed.Trim()) { $text = $text + "`n" + $zoomed }

        # Keep every line's on-screen rectangle, not just its text. Without it the
        # only way to choose a tile is to guess arrow-key counts, and a guess is
        # exactly what walked this navigator into the Social panel's Online Player
        # List three runs running after the patch reordered the ONLINE tab. WinRT
        # reports a rect per word, so a line's box is the union of its words'.
        $lines = foreach ($line in $result.Lines) {
            $left = [double]::PositiveInfinity
            $top = [double]::PositiveInfinity
            $right = [double]::NegativeInfinity
            $bottom = [double]::NegativeInfinity
            foreach ($word in $line.Words) {
                $rect = $word.BoundingRect
                if ($rect.X -lt $left) { $left = $rect.X }
                if ($rect.Y -lt $top) { $top = $rect.Y }
                if (($rect.X + $rect.Width) -gt $right) { $right = $rect.X + $rect.Width }
                if (($rect.Y + $rect.Height) -gt $bottom) { $bottom = $rect.Y + $rect.Height }
            }
            if ([double]::IsInfinity($left)) { continue }
            [pscustomobject]@{
                Text    = $line.Text
                Left    = [int]$left
                Top     = [int]$top
                Right   = [int]$right
                Bottom  = [int]$bottom
                CenterX = [int](($left + $right) / 2)
                CenterY = [int](($top + $bottom) / 2)
            }
        }

        return [pscustomobject]@{ Text = $text; Png = $bytes; Lines = @($lines) }
    } finally {
        $bitmap.Dispose()
    }
}

# The Windows activation watermark is always present and is not game text.
$script:NoisePattern = 'Activate Windows|Go to Settings to activate Windows\.?'

function Test-HudVisible {
    param([string] $Text)
    $stripped = ($Text -replace $script:NoisePattern, '').Trim()
    return $stripped.Length -gt 0
}

# ---------------------------------------------------------------- input

$script:GameWindow = [IntPtr]::Zero
$script:GamePid = 0

function Initialize-GameWindow {
    $process = Get-Process -Name forzahorizon6 -ErrorAction SilentlyContinue |
        Sort-Object StartTime -Descending | Select-Object -First 1
    if (-not $process) {
        throw "forzahorizon6 is not running in this session."
    }
    $script:GamePid = $process.Id
    $script:GameWindow = [NavWin]::FindGameWindow([uint32]$process.Id)
    if ($script:GameWindow -eq [IntPtr]::Zero) {
        throw "Could not find the game's App window."
    }
    if (-not [NavWin]::Focus($script:GameWindow)) {
        Write-Nav "WARNING focus did not stick on first attempt"
    }
    # DIE SPIELFLAECHE, nicht der Hauptschirm: so stimmt es auch im Fenster,
    # auf dem zweiten Schirm und bei jeder Aufloesung.
    $erzwungen = Get-ForcedCaptureArea
    if ($erzwungen) {
        Set-CaptureArea -X $erzwungen[0] -Y $erzwungen[1] -Width $erzwungen[2] -Height $erzwungen[3] -Source "-CaptureArea"
    } else {
        $flaeche = [NavCapture]::ClientArea($script:GameWindow)
        if ($flaeche -and $flaeche[2] -ge 640 -and $flaeche[3] -ge 360) {
            Set-CaptureArea -X $flaeche[0] -Y $flaeche[1] -Width $flaeche[2] -Height $flaeche[3] -Source "game window"
        } else {
            # Ein minimiertes oder noch nicht gezeichnetes Fenster meldet eine
            # winzige Flaeche; dann der Schirm, auf dem es liegt.
            $schirm = [System.Windows.Forms.Screen]::FromHandle($script:GameWindow).Bounds
            Set-CaptureArea -X $schirm.X -Y $schirm.Y -Width $schirm.Width -Height $schirm.Height -Source "game's monitor"
        }
    }
    # Keep the pointer out of the UI for the whole run.
    $script:ParkX = $script:RefWidth - 2
    $script:ParkY = $script:RefHeight - 2
    [NavWin]::ParkMouse($script:ParkX, $script:ParkY)
    Write-Nav "attached pid=$($script:GamePid) window=$([int64]$script:GameWindow); cursor parked at $($script:ParkX),$($script:ParkY)"
}

$script:KeyMap = @{
    UP = "{UP}"; DOWN = "{DOWN}"; LEFT = "{LEFT}"; RIGHT = "{RIGHT}"
    ENTER = "{ENTER}"; ESC = "{ESC}"; SPACE = " "; TAB = "{TAB}"
    BACKSPACE = "{BS}"; PAGEDOWN = "{PGDN}"; PAGEUP = "{PGUP}"
    B = "b"; E = "e"; F = "f"; Q = "q"; X = "x"; Y = "y"
}

function Send-GameKey {
    param(
        [Parameter(Mandatory = $true)][string] $Key,
        [int] $Count = 1,
        [int] $DelayMs = 90
    )
    if (-not $script:KeyMap.ContainsKey($Key)) {
        throw "Unsupported key '$Key'."
    }
    if ([NavWin]::GetForegroundWindow() -ne $script:GameWindow) {
        [void][NavWin]::Focus($script:GameWindow)
    }
    for ($index = 0; $index -lt $Count; $index += 1) {
        [System.Windows.Forms.SendKeys]::SendWait($script:KeyMap[$Key])
        Start-Sleep -Milliseconds $DelayMs
    }
}

$script:FrameIndex = 0

function Save-Frame {
    param([string] $Label, [byte[]] $Png, [string] $Text)
    $name = "{0:0000}_{1}" -f $script:FrameIndex, ($Label -replace '[^A-Za-z0-9_-]', '_')
    $script:FrameIndex += 1
    [IO.File]::WriteAllBytes((Join-Path $frameRoot "$name.png"), $Png)
    Set-Content -LiteralPath (Join-Path $frameRoot "$name.txt") -Value $Text -Encoding UTF8
}

# Line rectangles from the most recent Read-Screen. See Find-TextRect.
$script:LastLines = @()

function Read-Screen {
    <#
        One perceive cycle. Nudges the mouse and retries when the HUD has hidden
        itself, which is what made the slow host-driven approach blind.
    #>
    param([string] $Label = "frame", [switch] $Persist)
    $frame = Get-ScreenFrame
    if (-not (Test-HudVisible -Text $frame.Text)) {
        [NavWin]::NudgeMouse($script:ParkX, $script:ParkY)
        Start-Sleep -Milliseconds 350
        $frame = Get-ScreenFrame
    }
    if ($Persist -or $KeepFrames) {
        Save-Frame -Label $Label -Png $frame.Png -Text $frame.Text
    }
    # Callers take the text; Find-TextRect needs the geometry of the same cycle,
    # so it is stashed rather than widening every caller's return type.
    $script:LastLines = $frame.Lines
    return $frame.Text
}

# ---------------------------------------------------------------- category frame

# DIE KATEGORIE AM GELBEN RAHMEN WAEHLEN, nicht per Zeiger.
#
# Auf dem Kategorieschirm folgt die Markierung dem Mauszeiger NICHT (gemessen am
# 2026-09-13, und am 2026-09-24 auf dem Wirt wieder: "die Streckenliste ging
# nicht auf"). Road Racing gelang nur, weil es ohnehin markiert war. Die VM-Laeufe
# fingen das mit select_rivals_category.py ab -- das nimmt Bild und Tasten aber
# ueber die VM und laeuft darum hier nicht. Also dieselbe Regel hier drin: EINE
# Taste, dann hinsehen, wo der Rahmen jetzt sitzt; ENTER erst auf der Zielkachel.
#
# Rahmenfarbe und Kachelmitten sind die aus select_rivals_category.py, am
# 2026-09-24 an den Kategorieschirmen des Wirts nachgeprueft (5238 Rahmenpixel,
# Schwerpunkt 0.198/0.381 = Road Racing, wie in der VM).
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class NavFrame {
    // RGB (202, 255, 2), drei Pixel dick: GRUEN GROESSER ROT. Jedes gelbe Auto
    // und jede gelbe Schrift auf diesem Schirm hat es umgekehrt.
    public static double[] Centroid(Bitmap bmp) {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try {
            int stride = data.Stride;
            var buf = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            long n = 0; double sx = 0, sy = 0;
            for (int y = 0; y < bmp.Height; y++) {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x++) {
                    int i = row + x * 3;
                    int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    if (g >= 235 && r >= 150 && r <= 230 && b <= 40 && g > r + 20) {
                        n++; sx += x; sy += y;
                    }
                }
            }
            if (n == 0) return new double[] { 0, 0, 0 };
            return new double[] { n, sx / n / bmp.Width, sy / n / bmp.Height };
        } finally {
            bmp.UnlockBits(data);
        }
    }
}
'@

# EIGENER NAME: weiter unten steht ein aelteres $script:CategoryGrid (mit Row/Col),
# das diese Tabelle unter demselben Namen ueberschrieb -- am 2026-09-24 war das
# Ziel darum leer, und der Navigator drueckte achtmal LINKS im Kreis.
$script:CategoryCellOf = @{
    "Road Racing"   = @(0, 0); "Cross-Country" = @(0, 1); "Street Racing" = @(0, 2)
    "Dirt Racing"   = @(1, 0); "Drag Racing"   = @(1, 1); "Touge"         = @(1, 2)
}
# Kachelmitten als Anteile des Bildes (Mitten der TILES aus select_rivals_category.py).
$script:CategoryTileCentre = @{
    "0,0" = @(0.198, 0.3795); "0,1" = @(0.499, 0.3795); "0,2" = @(0.798, 0.3795)
    "1,0" = @(0.198, 0.653);  "1,1" = @(0.499, 0.653);  "1,2" = @(0.798, 0.653)
}

function Get-HighlightedCategoryCell {
    <# Welche Kachel hat den Rahmen? Ueber den Schwerpunkt; $null, wenn keine. #>
    param([Parameter(Mandatory = $true)][byte[]] $Png)
    $memory = New-Object System.IO.MemoryStream(, $Png)
    $bitmap = New-Object System.Drawing.Bitmap($memory)
    try {
        $c = [NavFrame]::Centroid($bitmap)
    } finally {
        $bitmap.Dispose()
        $memory.Dispose()
    }
    # Der Rahmen um EINE Kachel bringt rund 5.000 Pixel; darunter ist es Rauschen.
    if ($c[0] -lt 800) { return $null }
    $best = $null
    $bestDistance = 9.9
    foreach ($key in $script:CategoryTileCentre.Keys) {
        $m = $script:CategoryTileCentre[$key]
        $d = [math]::Sqrt([math]::Pow($c[1] - $m[0], 2) + [math]::Pow($c[2] - $m[1], 2))
        if ($d -lt $bestDistance) { $best = $key; $bestDistance = $d }
    }
    # Weit weg von jeder Kachelmitte: ein anderer Schirm mit gruenen Flaechen.
    if ($bestDistance -ge 0.12) { return $null }
    return $best
}

function Select-CategoryByFrame {
    <#
        Den Rahmen Schritt fuer Schritt auf die Kategorie bringen und ENTER druecken.
        $true heisst nur: ENTER ging auf der richtigen Kachel raus. Ob sich die
        richtige Liste geoeffnet hat, entscheidet danach das Wahrzeichen.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Category,
        [int] $MaxSteps = 8
    )
    $target = $script:CategoryCellOf[$Category]
    if (-not $target) {
        Write-Nav "category frame: '$Category' is not one of the six tiles"
        return $false
    }
    if ($target -isnot [array] -or $target.Count -ne 2 -or $null -eq $target[0] -or $null -eq $target[1]) {
        # Laut scheitern statt blind im Kreis zu druecken.
        throw "category frame: target cell for '$Category' is malformed ($target)"
    }
    $targetKey = "$($target[0]),$($target[1])"
    for ($step = 0; $step -le $MaxSteps; $step++) {
        $frame = Get-ScreenFrame
        if ($KeepFrames) { Save-Frame -Label "category-frame-$step" -Png $frame.Png -Text $frame.Text }
        # Ein Rahmen allein beweist nichts: der Klassenschirm hat auch einen, an
        # derselben Stelle. Erst "Routes Available" und mehrere Kategorienamen
        # machen es zum Kategorieschirm.
        $flat = ($frame.Text -split '\s+') -join ' '
        $hits = @($script:CategoryCellOf.Keys | Where-Object { $flat -match [regex]::Escape($_) }).Count
        if ($flat -notmatch 'Routes\s+Available' -or $hits -lt 3) {
            Write-Nav "category frame: not the category screen ($hits names) -- stopping"
            return $false
        }
        $cell = Get-HighlightedCategoryCell -Png $frame.Png
        if (-not $cell) {
            Write-Nav "category frame: no highlight frame visible"
            return $false
        }
        if ($cell -eq $targetKey) {
            Write-Nav "category frame on '$Category' after $step step(s); ENTER"
            Send-GameKey -Key ENTER
            return $true
        }
        $parts = $cell -split ','
        $row = [int]$parts[0]
        $col = [int]$parts[1]
        # Immer nur EINE Taste, dann wieder hinsehen.
        $key = if ($col -ne $target[1]) {
            if ($target[1] -gt $col) { "RIGHT" } else { "LEFT" }
        } else {
            if ($target[0] -gt $row) { "DOWN" } else { "UP" }
        }
        Write-Nav "category frame on $cell, target $targetKey -> $key"
        Send-GameKey -Key $key
        Start-Sleep -Milliseconds 1200
    }
    Write-Nav "category frame: not on '$Category' after $MaxSteps step(s)"
    return $false
}

# ---------------------------------------------------------------- primitives

function Find-TextRect {
    <#
        Where a line of text sits on screen, from the most recent perceive cycle.
        Returns $null when nothing matches, so a caller can fall back instead of
        aiming at a coordinate it invented.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [switch] $Refresh,
        [string] $Label = "find"
    )
    if ($Refresh -or -not $script:LastLines) {
        [void](Read-Screen -Label $Label)
    }
    return @($script:LastLines | Where-Object { $_.Text -match $Pattern })[0]
}

function Wait-ForText {
    <#
        Look for a text until it appears or the time is up.

        WARUM ES DAS GIBT: ein noch nicht gezeichneter Text und ein fehlender Text
        sehen in EINEM Bild vollkommen gleich aus. Unterscheiden lassen sie sich nur
        ueber die Zeit -- was kommt, erscheint innerhalb von Sekunden; was fehlt,
        bleibt aus.

        Am 2026-09-12 hat genau dieser Unterschied zweimal einen Sweep gekostet:
        erst wurde der Kategorieschirm zwei Sekunden nach einem Ladezustand nach
        'Touge' abgesucht, dann die frisch geoeffnete Streckenliste nach ihrem
        Wahrzeichen. Beide Male war die Antwort "nicht da", beide Male stimmte sie
        eine Sekunde spaeter nicht mehr.

        Das Urteil bleibt hart: erscheint der Text nicht, wird nicht gescannt.
        Gewartet wird nur, damit das Urteil ueber den fertigen Schirm faellt.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [int] $Seconds = 10,
        [string] $Label = "look"
    )
    $until = (Get-Date).AddSeconds($Seconds)
    $looks = 0
    while ($true) {
        $looks++
        $found = Find-TextRect -Pattern $Pattern -Refresh -Label $Label
        if ($found) {
            if ($looks -gt 1) {
                Write-Nav "'$Pattern' erschien erst beim $looks. Blick"
            }
            return $found
        }
        if ((Get-Date) -ge $until) {
            Write-Nav "'$Pattern' nicht gefunden (nach $looks Blicken in $Seconds s)"
            return $null
        }
        Start-Sleep -Milliseconds 700
    }
}

function Select-ByHover {
    <#
        Choose a menu item by hovering its own label, then confirming.

        Forza's menus are mouse-aware: a cursor resting on an item pins the
        highlight there. Everywhere else in this script that is a hazard, and the
        cursor is parked in a dead corner to avoid it. Here it is the asset --
        it is the only *deterministic* way to select a named tile.

        Which tile is highlighted cannot be read by OCR, so the alternative is
        counting arrow presses, and a press count is a guess that every game
        patch invalidates. Hovering names the target instead: OCR says where
        "Rivals" is, the cursor goes there, and the highlight follows. Nothing
        about the tile's index or the row's order needs to be known.

        The cursor is re-parked afterwards, success or failure, so no later
        keyboard step is left fighting a pinned highlight.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [string] $Expect = "",
        [int] $TimeoutSeconds = 30,
        [int] $SettleMs = 700,
        [int] $LookSeconds = 10,
        [switch] $Click,
        [string] $Label = "hover"
    )
    # Nicht einmal hinsehen, sondern eine Weile -- warum, steht bei Wait-ForText.
    $target = Wait-ForText -Pattern $Pattern -Seconds $LookSeconds -Label "$Label-look"
    if (-not $target) {
        Write-Nav "hover target '$Pattern' is not on screen"
        return $false
    }
    Write-Nav "hovering '$($target.Text)' at $($target.CenterX),$($target.CenterY) for '$Pattern'"
    try {
        if ($Click) {
            [NavWin]::ClickMouse($target.CenterX, $target.CenterY)
        } else {
            # ZWEIMAL hinfahren, ein paar Pixel versetzt.
            #
            # Die Markierung folgt einer BEWEGUNG des Zeigers, nicht seinem blossen
            # Aufenthaltsort. Am 2026-09-12 lag der Zeiger nachweislich auf dem
            # Schriftzug 'Touge' (1344,776) -- und der gelbe Rahmen stand weiter auf
            # Road Racing, also nahm ENTER Road Racing. Ein zweiter Sprung von wenigen
            # Pixeln erzeugt die Bewegung, die das Menue braucht.
            [NavWin]::HoverMouse($target.CenterX - 4, $target.CenterY + 2)
            Start-Sleep -Milliseconds 150
            [NavWin]::HoverMouse($target.CenterX, $target.CenterY)
            Start-Sleep -Milliseconds $SettleMs
            Send-GameKey -Key ENTER
        }
    } finally {
        Start-Sleep -Milliseconds 250
        [NavWin]::ParkMouse($script:ParkX, $script:ParkY)
    }
    if (-not $Expect) {
        Start-Sleep -Milliseconds 600
        return $true
    }
    return Wait-ForMarker -Pattern $Expect -TimeoutSeconds $TimeoutSeconds -Label $Label
}

function Wait-ForMarker {
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [int] $TimeoutSeconds = 60,
        [string] $Label = "wait"
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = ""
    do {
        $last = Read-Screen -Label $Label
        if ($last -match $Pattern) {
            Write-Nav "matched '$Pattern'"
            return $true
        }
        Start-Sleep -Milliseconds $FrameIntervalMs
    } while ((Get-Date) -lt $deadline)
    Write-Nav "TIMEOUT waiting for '$Pattern'; last screen: $((($last -replace $script:NoisePattern,'') -split "`r?`n" | Where-Object { $_ -match '\S' }) -join ' / ')"
    return $false
}

function Wait-ForMarkerGone {
    <#
        Wait for a marker to stop being visible. Used to detect that a selection
        actually took effect and the game started loading, rather than assuming a
        keypress landed.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [int] $TimeoutSeconds = 60,
        [string] $Label = "wait-gone"
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $text = Read-Screen -Label $Label
        if ($text -notmatch $Pattern) {
            Write-Nav "'$Pattern' is gone"
            return $true
        }
        Start-Sleep -Milliseconds $FrameIntervalMs
    } while ((Get-Date) -lt $deadline)
    Write-Nav "TIMEOUT waiting for '$Pattern' to disappear"
    return $false
}

function Get-ScreenExcerpt {
    <#
        A short, log-friendly slice of a screen read. Frames are not kept by
        default, so when a seek fails there is otherwise no record of what the OCR
        actually said -- which is what made the S1 class failure undiagnosable.
    #>
    param([string] $Text, [string] $Around = 'Performance\s+Class')
    if ([string]::IsNullOrWhiteSpace($Text)) { return "<empty>" }
    $flat = ($Text -replace '\s+', ' ').Trim()
    $m = [regex]::Match($flat, ".{0,70}$Around.{0,25}")
    if ($m.Success) { return $m.Value }
    if ($flat.Length -gt 160) { return $flat.Substring(0, 160) + " ..." }
    return $flat
}

function Invoke-Seek {
    <#
        Press a key until a marker appears. Used instead of a fixed number of
        presses, because a replayed key count desynchronises the moment the menu
        layout changes -- which is exactly what a game patch does.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Key,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [int] $MaxAttempts = 12,
        [int] $SettleMs = 600,
        [string] $Label = "seek",
        # Log what each press actually read. Only worth it for short seeks whose
        # failure needs explaining; a 60-attempt track seek would just bloat the log.
        [switch] $LogText
    )
    $text = Read-Screen -Label "$Label-start"
    if ($LogText) { Write-Nav "  seek start reads: $(Get-ScreenExcerpt -Text $text)" }
    if ($text -match $Pattern) { return $true }
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt += 1) {
        Send-GameKey -Key $Key
        Start-Sleep -Milliseconds $SettleMs
        $text = Read-Screen -Label "$Label-$attempt"
        if ($LogText) { Write-Nav "  seek $Key $attempt reads: $(Get-ScreenExcerpt -Text $text)" }
        if ($text -match $Pattern) {
            Write-Nav "seek $Key found '$Pattern' after $attempt press(es)"
            return $true
        }
    }
    Write-Nav "seek $Key did NOT find '$Pattern' in $MaxAttempts presses"
    return $false
}

function Invoke-SeekAny {
    <#
        Seek with several candidate keys in turn. Which direction a Forza list
        navigates is not guessable: the world pause menu and the Rivals hub are
        horizontal, the garage sections are horizontal, and the route list is a
        horizontal carousel too -- 64 DOWN presses moved it not at all, with the
        cursor parked away from the UI, while RIGHT moved it immediately.
    #>
    param(
        [Parameter(Mandatory = $true)][string[]] $Keys,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [int] $MaxAttempts = 60,
        [int] $SettleMs = 450,
        [string] $Label = "seek",
        [switch] $LogText
    )
    foreach ($key in $Keys) {
        if (Invoke-Seek -Key $key -Pattern $Pattern -MaxAttempts $MaxAttempts -SettleMs $SettleMs -Label "$Label-$key" -LogText:$LogText) {
            Write-Nav "seek succeeded using $key"
            return $true
        }
        Write-Nav "seek with $key exhausted; trying the next direction"
    }
    return $false
}

function Invoke-Press {
    param(
        [Parameter(Mandatory = $true)][string] $Key,
        [string] $Expect = "",
        [int] $Count = 1,
        [int] $TimeoutSeconds = 30,
        [string] $Label = "press"
    )
    Send-GameKey -Key $Key -Count $Count
    if (-not $Expect) {
        Start-Sleep -Milliseconds 400
        return $true
    }
    return Wait-ForMarker -Pattern $Expect -TimeoutSeconds $TimeoutSeconds -Label $Label
}

function Reset-ToKnownState {
    <#
        Back out to a recognisable root screen. Needed because a failed step
        leaves the game in an unknown submenu, and continuing from there sends
        keys into the wrong context -- how 33 stray RIGHT presses ended up inside
        the Character submenu.
    #>
    param([int] $MaxBacks = 6)
    Write-Nav "recovering to a known state"
    for ($index = 0; $index -lt $MaxBacks; $index += 1) {
        $text = Read-Screen -Label "reset-$index"
        if ($text -match $script:RootMarker) {
            Write-Nav "recovered at root"
            return $true
        }
        Send-GameKey -Key ESC
        Start-Sleep -Milliseconds 900
    }
    $text = Read-Screen -Label "reset-final"
    return ($text -match $script:RootMarker)
}

# Markers for the hub/pause root that every route step can fall back to.
$script:RootMarker = 'Festival Playlist|Collection Journal|Campaign'

# ---------------------------------------------------------------- explore mode

function Invoke-Explore {
    <#
        Walk the menus and record every distinct screen, so the route table for a
        new build can be derived without a human describing the menus.
    #>
    param([int] $Steps = 40)
    Write-Nav "explore mode: recording distinct screens"
    $seen = @{}
    $order = @()
    $keys = @("RIGHT", "RIGHT", "RIGHT", "RIGHT", "LEFT", "DOWN", "UP", "ESC")
    for ($index = 0; $index -lt $Steps; $index += 1) {
        $text = Read-Screen -Label "explore-$index" -Persist
        $clean = (($text -replace $script:NoisePattern, '') -split "`r?`n" |
            Where-Object { $_ -match '\S' }) -join ' | '
        $key = ($clean -split ' \| ' | Select-Object -First 6) -join '|'
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $clean
            $order += $clean
            Write-Nav "NEW SCREEN: $clean"
        }
        Send-GameKey -Key $keys[$index % $keys.Count]
        Start-Sleep -Milliseconds 700
    }
    $order | Set-Content -LiteralPath (Join-Path $runRoot "explored_screens.txt") -Encoding UTF8
    Write-Nav "explore complete: $($seen.Count) distinct screens -> explored_screens.txt"
}

# ---------------------------------------------------------------- route

# Screen identification, most specific first. Order matters, and so does
# specificity: an earlier attempt used 'Festival Playlist|Collection Journal|
# Campaign' as the hub marker, which matches the garage menu, the world pause menu
# AND the map footer, so the navigator "recognised" the wrong screen and pressed
# ENTER into it, landing on the World Map. Every pattern here is text that appears
# on exactly one screen.
#
# ONLINE tab is tested before the world pause menu, because the pause menu's tab
# bar still reads CREATIVE HUB while ONLINE is the active tab.
# OCR on this build renders 'S1' as 'SI' every single time -- the class strip
# itself reads as 's 12' / 'sa' -- so every place that recognises a class by name
# has to tolerate the digit/letter confusion. The token builder lives ABOVE both
# the state table and the seek pattern on purpose: on 2026-08-20 only the seek
# pattern was relaxed, so the seek finally landed on S1 and the state machine
# then failed to match class_screen, fell through to route_list, and walked the
# class strip from S1 to R with 25 RIGHT presses meant for the route carousel.
$script:ClassConfusable = @{ "1" = "1IilL"; "2" = "2Zz" }
$script:PerformanceClasses = @("D", "C", "B", "A", "S1", "S2", "R", "X")

function Get-ClassToken {
    <#
        The regex token matching one class name as OCR actually renders it. Only
        digits are relaxed, and only to the glyphs they are confused with, so the
        classes stay mutually exclusive: 'S1' cannot match 'S2'.
    #>
    param([Parameter(Mandatory = $true)][string] $Class)
    $token = ""
    foreach ($ch in $Class.ToCharArray()) {
        $s = [string]$ch
        if ($script:ClassConfusable.ContainsKey($s)) { $token += "[" + $script:ClassConfusable[$s] + "]" }
        else { $token += [regex]::Escape($s) }
    }
    return $token
}

$script:ClassAlternation = (($script:PerformanceClasses | ForEach-Object { Get-ClassToken -Class $_ }) -join '|')


$script:StateOrder = @(
    # game_crashed ganz vorne: ein abgestuerztes Spiel ist kein Menue, durch das man
    # sich druecken koennte, und jeder Tastendruck dorthin ist verschwendet.
    # Am 2026-09-18 gemessen: nach einem "Video Card Crash" hat der Navigator
    # 151 Zyklen lang gegen das Fehlerfenster gedrueckt und dann "Stuck in state
    # 'unknown'" gemeldet -- fuenf Minuten fuer eine Lage, die in der ersten
    # Sekunde erkennbar war, und mit einer Diagnose, die in die Irre fuehrt.
    "game_crashed",
    # server_error danach: dieser Schirm ueberdeckt alles andere, und er ist der einzige,
    # bei dem Warten nachweislich sinnlos ist.
    "server_error",
    "confirm_dialog",
    "leaderboard", "class_screen", "rival_detail", "route_list", "rivals_modes",
    "rivals_hub", "forza_link", "online_tab", "map", "world_pause", "garage_menu",
    "continue_menu", "title",
    # NACH world_pause und garage_menu: das Pausenmenue zeigt die Festival-Serie als
    # Kachel mit, samt "Series Update" und Restlaufzeit. Weiter vorne einsortiert hielt
    # der Navigator jedes Pausenmenue fuer den Serien-Schirm und drueckte 15 Zyklen
    # lang ENTER hinein (2026-09-11, von mir selbst verursacht). Der echte Schirm ist
    # modal und zeigt KEINE Pausen-Eintraege -- also entscheidet die Reihenfolge.
    "series_update",
    "loading_screen"
)
$script:States = @{
    # Das Spiel meldet "Server Error / There was an error communicating with the server".
    # Ohne eigenen Zustand fiel das unter 'unknown', und seit 'unknown' geduldig ist,
    # wartete der Navigator 151 Zyklen -- rund sechs Minuten -- auf einem Schirm, der
    # bereits sagt, dass nichts kommt. Zweimal je Board macht zwoelf verschenkte Minuten.
    # Warten hilft hier NICHT: der Rueckzug gehoert eine Ebene hoeher, in den Sweep, der
    # acht Minuten pausiert und dasselbe Board erneut anfaehrt. Hier heisst richtig:
    # sofort scheitern, damit dieser Rueckzug frueher beginnt.
    server_error  = 'Server\s+Error|error\s+communicating\s+with\s+the\s+server'
    # Das Absturzfenster des Spiels. "has nit an unexpected error" ist KEIN Tippfehler:
    # so liest die Texterkennung "has hit an unexpected error", und das Muster muss
    # den gelesenen Text treffen, nicht den gedruckten. Darum die lockeren Teile.
    game_crashed  = 'terminated\s+unexpectedly|has\s+\w+\s+an\s+unexpected\s+error|Video\s+Card\s+Crash'
    # Nach jedem frischen Spielstart legt Forza die neue Festival-Serie vor: "Series
    # Update", die Belohnungsautos und "Series Ends In: 27d 19h". Der Schirm ist modal
    # und blockiert das Menue vollstaendig.
    #
    # Ohne eigenen Zustand fiel er unter 'unknown', der ESC-Handler kam nicht durch, die
    # Kategoriewahl scheiterte -- und der Aufseher legte sich 45 Minuten schlafen. Am
    # 2026-09-10 um 20:31 hat das genau eine Dreiviertelstunde gekostet, obwohl ein
    # einziger ENTER genuegt. Und er kommt nach JEDEM Neustart wieder.
    #
    # VOR rivals_modes einsortiert: die Ereignisnamen auf dem Schirm enthalten
    # Kategoriewoerter, und der wuerde ihn sonst fuer die Kategorieliste halten.
    series_update = 'Series\s+Update|Series\s+Ends\s+In|Festival\s+Playlist'
    # Must not include 'Change Rival': that is the B-button prompt shown on the
    # class selection screen, and matching it there made the navigator declare
    # success one screen early. The row filter and the player count only exist on
    # the real leaderboard table.
    leaderboard   = 'Filter:\s*Global|[0-9][0-9,]{2,}\s+Players'
    class_screen  = '\b(?:' + $script:ClassAlternation + ')\b\W{0,3}Performance\s+Class'
    # Selecting a class can land on the rival's detail card instead of the board:
    # "Time to Beat", the rival's car, and a "Change Rival" prompt bound to B. It
    # was not in this table, so it classified as "unknown" and the unknown handler
    # pressed ESC at it until the run gave up -- one nav failure and a VM recycle
    # on 2026-08-20. Matched on "Time to Beat" alone and ordered AFTER class_screen
    # deliberately: "Gap to Rival" also appears on the class strip, so using that
    # as the marker would classify the class screen as this one.
    rival_detail  = 'Time\s+to\s+Beat'
    route_list    = 'Route\s+Length|Select\s+Route|Choose\s+Route'
    rivals_modes  = 'Road Racing|Street Racing|Cross-Country|Dirt Racing|Drag Racing|Touge'
    online_tab    = 'Top the Leaderboards|Horizon Open|Horizon Tour'
    # The Rivals hub on 6.420.696.0 is a tile row of Horizon Rivals / Monthly
    # Rivals / My Rivals / Showcase Rivals -- not the Road Racing / Street Racing
    # category list the older route expected.
    rivals_hub    = 'My Rivals|Showcase Rivals|Monthly Rivals'
    # Reachable by accident from the ONLINE tab; back straight out of it.
    forza_link    = 'Forza LINK Selection|The Eliminator|Horizon Stunt Party'
    # Modal confirmations such as "Return to Horizon Solo?" must be cancelled,
    # not answered blindly: several of them change the online session.
    confirm_dialog = 'Are you sure you want to|Return to Horizon Solo\?'
    map           = 'Close Map|Toggle Map Regions|Set Route'
    world_pause   = 'CREATIVE HUB|Reset Car Position|Exit Game'
    garage_menu   = 'CUSTOMISABLE GARAGE|Forzavista'
    continue_menu = '(?m)^\s*Continue\s*$'
    # Der Trenner in 'Accessibility/Settings' wird bei 720p als 'l' gelesen
    # (am 2026-09-24 an einem verkleinerten Titelbild gemessen); also beliebig.
    title         = 'Start\s*Game|Accessibility.{0,2}Settings'
    # Forza's loading screen cycles stat cards and radio/skill tips. Without
    # this they classify as "unknown", and the unknown handler's ESC then fires
    # repeatedly into a load. Listed last so a real menu always wins.
    loading_screen = 'LOADING|PLEASE WAIT|Favourite Radio Station|HORIZON PULSE|Rivals Beaten|Number of Podiums|Ultimate Draft Skills|Skill Points|Distance Driven'
}

# Die Gitterplaetze des Kategorieschirms, abgelesen am Standbild vom 2026-08-28
# (data/runtime/vm_drive, "Horizon Rivals"). Reihenfolge ist die Anzeige, NICHT die
# Reihenfolge, in der die OCR die Namen ausspuckt -- daran ist die alte Annahme
# "senkrechte Liste, also UP/DOWN" gescheitert.
$script:CategoryGrid = @{
    "Road Racing"   = @{ Row = 0; Col = 0 }
    "Cross-Country" = @{ Row = 0; Col = 1 }
    "Street Racing" = @{ Row = 0; Col = 2 }
    "Dirt Racing"   = @{ Row = 1; Col = 0 }
    "Drag Racing"   = @{ Row = 1; Col = 1 }
    "Touge"         = @{ Row = 1; Col = 2 }
}

# Ein Name, der in der Streckenliste dieser Kategorie steht und sauber liest --
# der Beweis, dass die richtige Kategorie offen ist. Wird beim Aufnehmen einer
# neuen Kategorie ergaenzt; fehlt er, sagt der Navigator das im Log.
$script:CategoryLandmark = @{
    "Road Racing"   = "Highway\s+Circu(?:i(?:t)?)?"
    "Street Racing" = "Daikoku\s+Chase"
    # Cross-Country braucht KEINEN Streckennamen als Wahrzeichen, und das ist ein
    # Vorteil: die Kategorie schreibt "Cross-Country" in JEDEN ihrer 19 Namen und
    # sonst kommt das Wort in keiner Kategorie vor. Damit ist die Pruefung
    # unabhaengig davon, dass die Namen dort am Kartenrand abgeschnitten sind
    # ("and Cross-Country Circuit Le" an Position 0). Der Bindestrich wird locker
    # gefasst, weil die OCR ihn gelegentlich als Leerzeichen liest.
    "Cross-Country" = "Cross[\s-]?Country"
    # Dirt Racing braucht ebenfalls keinen einzelnen Streckennamen: 10 seiner 21
    # Strecken heissen "... Scramble", 10 "... Trail", und beide Woerter kommen in
    # keiner anderen Kategorie vor (gegen alle Katalognamen geprueft). Nur "The
    # Gauntlet" faellt heraus, was nichts macht: die Streckenliste zeigt immer
    # mehrere Eintraege auf einmal.
    "Dirt Racing"   = "Scramble|Trail"
    # Drag Racing: alle drei Strecken heissen "... Drag Strip"; das Wort kommt in
    # keiner anderen Kategorie vor (gegen alle Katalognamen geprueft).
    "Drag Racing"   = "Drag"
    # Touge hat KEIN gemeinsames Wort -- also eine Alternative aus vier der fuenf
    # Streckennamen. "Mt Haruna" fehlt bewusst: die Liste zeigt immer mehrere
    # Eintraege, ein Treffer genuegt.
    # FALLE: "Norikura" allein waere falsch, Street Racing hat eine "Norikura
    # Descent". Nur der volle "Norikura Skyline" ist eindeutig.
    #
    # KORRIGIERT am 2026-09-24: die Streckenliste zeigt NICHT mehrere Namen, sondern
    # nur den Titel der gewaehlten Strecke -- die uebrigen sind Formbildchen mit
    # Kilometerzahl. Beim Oeffnen ist das Hakone Nanamagari, und die OCR las es
    # "Nanamagarl". Zwei Versuche scheiterten daran an der richtigen Liste. Darum
    # Wortanfaenge, deren Ende die OCR verschmieren darf, und Mt Haruna dazu --
    # alle fuenf gegen den Katalog geprueft: keiner kommt anderswo vor.
    "Touge"         = "Nanamaga|Arashiya|Bandai|Norikura\s+Sky|Haruna"
}

function Get-ScreenState {
    param([string] $Text)
    foreach ($name in $script:StateOrder) {
        if ($Text -match $script:States[$name]) { return $name }
    }
    if (-not (Test-HudVisible -Text $Text)) { return "loading" }

    # Forza's loading screen cycles stat cards -- "Showcases Won 2/2", "Time
    # Spent in First Place", "Rivals Beaten 68" and dozens more -- so matching
    # them by name is hopeless. Structurally they are one or two short lines,
    # whereas every real menu shows a tab bar, an item list and a key legend, so
    # at least four lines. Treating sparse screens as transient stops the machine
    # sending keys into a load, which is both useless and occasionally harmful.
    $lines = @(($Text -replace $script:NoisePattern, '') -split "`r?`n" |
        Where-Object { $_ -match '\S' })
    if ($lines.Count -le 3) { return "loading_screen" }
    return "unknown"
}

function Get-SelectedRoute {
    <#
        The selected route's name on the route-list screen. Layout is:
            Routes
            <route name>          <- selected
            Route Length: X.X KM
        so the name is the line directly before "Route Length". Falls back to the
        line right after the "Routes" header.
    #>
    param([string] $Text)
    $lines = @(($Text -replace $script:NoisePattern, '') -split "`r?`n" |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\S' })
    for ($i = 0; $i -lt $lines.Count; $i += 1) {
        if ($lines[$i] -match '^Route\s+Length' -and $i -ge 1) { return $lines[$i - 1] }
    }
    for ($i = 0; $i -lt $lines.Count - 1; $i += 1) {
        if ($lines[$i] -match '^Routes$') { return $lines[$i + 1] }
    }
    return ""
}

function Get-TolerantNamePattern {
    <#
        A regex for a menu name that survives a clipped tail. OCR on this build
        drops the last character of a route name often enough to matter: on
        2026-08-20 'Highway Circuit' read as 'Highway Circui' and cost two boards
        plus two VM reboots, while the same name had read cleanly minutes earlier
        on the same screen. Everything except the final characters still has to
        match, so routes stay distinguishable ('Shirakawa' vs 'Shimanoyama'), and
        the relaxation is skipped entirely for names too short to spare them.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [int] $OptionalTail = 2,
        [int] $MinimumRequired = 6
    )
    $trimmed = $Name.Trim()
    $words = @($trimmed -split '\s+')
    $escaped = @($words | ForEach-Object { [regex]::Escape($_) })
    $last = $words[$words.Count - 1]
    if ($last.Length -gt $OptionalTail -and ($trimmed.Length - $OptionalTail) -ge $MinimumRequired) {
        $keep = $last.Substring(0, $last.Length - $OptionalTail)
        $optional = $last.Substring($last.Length - $OptionalTail)
        $pattern = [regex]::Escape($keep)
        foreach ($ch in $optional.ToCharArray()) { $pattern += '(?:' + [regex]::Escape([string]$ch) }
        $pattern += (')?' * $optional.Length)
        $escaped[$words.Count - 1] = $pattern
    }
    return ($escaped -join '\s+')
}

function Get-RouteHead {
    <#
        Der unterscheidende Teil eines Streckennamens -- der Ort davor.

        JEDER Name dieser Kategorie endet auf "Cross-Country", die meisten auf
        "Cross-Country Circuit". Vergleicht man ganze Namen, dominiert dieser
        gemeinsame Teil alles: 'Izu Cross-Country' und 'Temple Cross-Country' sehen
        dann zu 70 % gleich aus. Am 2026-09-13 hat genau das fuenf von sieben
        Pruefungen umgeworfen -- 'Naruo' galt als 'Nangan', 'Soni Highlands' als
        'Ruriko-ji'.

        Also wird abgeschnitten, sobald das "Cross" beginnt. Die OCR verstuemmelt es
        ('Cras-eouniry', 'eross', 'ross'), darum wird nicht nach dem Wort gesucht,
        sondern nach einem der Anfaenge, zu denen es zerfaellt.
    #>
    param([string] $Name)
    $s = ($Name -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
    if (-not $s) { return "" }
    $marken = @("cro", "cra", "ero", "era", "ros", "ras")
    for ($i = 2; $i -le $s.Length - 3; $i += 1) {
        if ($marken -contains $s.Substring($i, 3)) { return $s.Substring(0, $i) }
    }
    return $s
}

function Get-LongestCommon {
    <#
        Laengstes gemeinsames Stueck zweier Zeichenketten, samt Startstellen.
        Gibt @(Laenge, StartInA, StartInB) zurueck. Namen sind kurz, also genuegt
        der einfache Weg.
    #>
    param([string] $A, [string] $B)
    $beste = 0; $ia = -1; $ib = -1
    for ($i = 0; $i -lt $A.Length; $i += 1) {
        for ($j = 0; $j -lt $B.Length; $j += 1) {
            $k = 0
            while ($i + $k -lt $A.Length -and $j + $k -lt $B.Length -and
                   $A[$i + $k] -eq $B[$j + $k]) { $k += 1 }
            if ($k -gt $beste) { $beste = $k; $ia = $i; $ib = $j }
        }
    }
    return @($beste, $ia, $ib)
}

function Test-SameRoute {
    <#
        Zeigen zwei Lesungen DIESELBE Strecke?

        ## Wogegen das robust sein muss

        **Laufschrift.** Lange Namen wandern durch ihr Feld. Dieselbe Position in
        fuenf Lesungen am 2026-09-13:

            'Tateyama Alpine Cross-Coun' -> 'rateyama Alpine Cross-Coun'
            -> 'ateyama Alpine Cross-Count' -> 'iteyama Alpin Cross-Countr'

        Fuer die Schrittzaehlung ist das toedlich: ein wandernder Text AENDERT sich,
        ohne dass sich die Position aendert. Der Zaehler haelt das fuer einen Schritt
        und landet am Ende eins zu kurz -- so ist idx15 wiederholt auf idx14 gelandet.

        **OCR-Schaden.** 'Edogawa Cross-Cdufitry Cire' und 'idogawa eras-country Cire'
        sind dieselbe Strecke.

        ## Und wogegen es NICHT zu grosszuegig sein darf

        Benachbarte Positionen muessen sich unterscheiden -- 'Shimanoyama' (15) und
        'Yahikoyama' (14) stehen nebeneinander und teilen sich 'oyama'. Darum zaehlt
        ein gemeinsames Stueck nur, wenn es lang genug ist UND bei einer der beiden
        Lesungen fast am Anfang steht. Eine Laufschrift schiebt vorne hoechstens ein
        paar Zeichen weg; zwei verschiedene Orte teilen sich hoechstens die Endung.

        ## Was ein Fehlurteil kostet

        Wenig, und das ist der Grund, warum eine Faustregel hier genuegt: haelt der
        Zaehler falsch, landet die Navigation daneben -- und der Aufrufer verwirft
        das Board, weil der Name nicht zur Position passt. Das ist der Zustand von
        vorher, kein neuer Schaden. Falsch beschriftet wird nie.
    #>
    param([string] $A, [string] $B)
    $x = Get-RouteHead -Name $A
    $y = Get-RouteHead -Name $B
    if (-not $x -or -not $y) { return $false }
    if ($x -eq $y) { return $true }

    $kurz = [Math]::Min($x.Length, $y.Length)
    # Kurze Orte ('izu') brauchen weniger, lange nicht mehr als sechs Zeichen.
    $noetig = [Math]::Min(6, [Math]::Max(3, [int][Math]::Floor($kurz * 0.6)))
    $treffer = Get-LongestCommon -A $x -B $y
    if ($treffer[0] -lt $noetig) { return $false }
    # Fast am Anfang bei mindestens einer der beiden: sonst ist es die Endung.
    return ($treffer[1] -le 3) -or ($treffer[2] -le 3)
}

function Get-SettledRoute {
    <#
        Den Streckennamen lesen -- aber erst, wenn zwei Lesungen hintereinander
        dasselbe sagen.

        WARUM: `Select-RouteByIndex` drueckt RIGHT so oft wie noetig und liest
        DANACH SOFORT. Laeuft die Umblendung des Karussells noch, zeigt der Schirm
        in diesem Moment die VORIGE Strecke.

        Genau so sieht das Protokoll vom 2026-09-13 aus: idx12 landete dreimal als
        'Takashiro Cross-Country' (Index 11), idx15 dreimal als 'Yahikoyama
        Cross-Country' (Index 14) -- IMMER genau eins zu kurz, nie zwei. Ein
        zufaellig verschluckter Tastendruck saehe anders aus; die Zeitmarken zeigen
        ausserdem, dass alle Druecke gesendet wurden (8,4 s fuer 12 Schritte).

        Der Sweep hat diese Boards daraufhin verworfen -- er hielt eine veraltete
        LESUNG fuer eine fehlgeschlagene NAVIGATION. Sein Riegel ist richtig; nur
        die Lesung war zu frueh.

        Diese Funktion ist auch dann kein Fehler, wenn die Vermutung falsch ist:
        eine Lesung von einem ruhenden Schirm ist nie schlechter als eine von einem
        bewegten. Sie kostet im Normalfall eine zusaetzliche Lesung.
    #>
    param([string] $Label = "route", [int] $Tries = 6, [int] $PauseMs = 400)
    $previous = ""
    for ($i = 0; $i -lt $Tries; $i += 1) {
        $now = Get-SelectedRoute -Text (Read-Screen -Label "$Label-settle-$i")
        if ($now -and $previous -and (Test-SameRoute -A $now -B $previous)) {
            Write-Nav "route name settled after $($i + 1) read(s): '$now'"
            return $now
        }
        if ($now -and $previous -and -not (Test-SameRoute -A $now -B $previous)) {
            Write-Nav "route name still moving: '$previous' -> '$now'"
        }
        $previous = $now
        Start-Sleep -Milliseconds $PauseMs
    }
    Write-Nav "route name did NOT settle in $Tries reads; taking '$previous'"
    return $previous
}

function Select-RouteByIndex {
    <#
        Position the carousel on a route by its fixed index, robust to the dirty
        route-name OCR. Walk RIGHT until the clean anchor route is recognised
        (establishing the current index), then step RIGHT the remaining offset.
        Returns the OCR-confirmed name of the landed route (recorded with the
        scan; may be noisy, which is acceptable as a label).
    #>
    param([Parameter(Mandatory = $true)][int] $TargetIndex)
    $anchorPattern = Get-TolerantNamePattern -Name $RouteAnchor
    Write-Nav "anchor pattern: $anchorPattern"
    $foundAnchor = $false
    for ($i = 0; $i -lt $RouteCount + 2; $i += 1) {
        $text = Read-Screen -Label "route-anchor-$i"
        $name = Get-SelectedRoute -Text $text
        if ($name -match $anchorPattern) { $foundAnchor = $true; break }
        Send-GameKey -Key RIGHT
        Start-Sleep -Milliseconds 550
    }
    if (-not $foundAnchor) {
        throw "Could not find the anchor route '$RouteAnchor' to index from."
    }
    Write-Nav "anchor '$RouteAnchor' (index $RouteAnchorIndex) reached; stepping to index $TargetIndex"
    $offset = (($TargetIndex - $RouteAnchorIndex) % $RouteCount + $RouteCount) % $RouteCount

    # BESTAETIGTE SCHRITTE ZAEHLEN, NICHT TASTENDRUECKE.
    #
    # Vorher wurde `offset` mal blind RIGHT gedrueckt und am Ende einmal gelesen.
    # Das landet zuverlaessig daneben, sobald EIN Druck verloren geht -- und genau
    # das passiert. Am 2026-09-13 gemessen: idx05, idx12 und idx15 landeten
    # wiederholt auf 4, 11 und 14, immer genau eins zu kurz.
    #
    # Meine erste Erklaerung war, die Bestaetigungslesung komme zu frueh und zeige
    # noch die vorige Strecke. Das ist WIDERLEGT: seit `Get-SettledRoute` erst liest,
    # wenn zwei Lesungen uebereinstimmen, landet idx05 immer noch auf 'Oka
    # Cross-Country Circuit' -- die Lesung ist verlaesslich, die Position ist es
    # nicht. Es geht wirklich ein Druck verloren.
    #
    # Also wird jeder Schritt nachgesehen: hat sich der Name geaendert? Wenn nicht,
    # kam der Druck nicht an und wird wiederholt. Zwei Versuche je Schritt -- mehr
    # waere gefaehrlich, denn wenn zwei Strecken gleich HEISSEN, saehe ein echter
    # Schritt wie ein verlorener aus, und dann liefe man zu weit.
    #
    # Der Preis sind ein paar Sekunden je Anfahrt. Gegen ein Board, das eine
    # Viertelstunde braucht, ist das nichts -- und gegen ein Board, das gar nicht
    # gescannt wird, erst recht.
    $current = Get-SettledRoute -Label "route-step-start" -Tries 4 -PauseMs 300
    Write-Nav "stepping $offset position(s) from '$current'"
    for ($step = 0; $step -lt $offset; $step += 1) {
        $moved = $false
        for ($try = 0; $try -lt 2; $try += 1) {
            Send-GameKey -Key RIGHT
            Start-Sleep -Milliseconds 450
            $next = Get-SettledRoute -Label "route-step-$step-$try" -Tries 4 -PauseMs 300
            if ($next -and -not (Test-SameRoute -A $next -B $current)) {
                $current = $next
                $moved = $true
                break
            }
            Write-Nav ("step " + ($step + 1) + "/" + $offset +
                       " did not move (still '$current'); pressing again")
        }
        if (-not $moved) {
            # Zweimal gedrueckt, kein Namenswechsel gesehen. Entweder heissen zwei
            # Strecken gleich, oder der Leser irrt. Weiterlaufen und die Pruefung
            # des Aufrufers entscheiden lassen -- sie verwirft ein Board, dessen
            # Name nicht zur Position passt, statt es falsch zu beschriften.
            Write-Nav ("step " + ($step + 1) + "/" + $offset +
                       " unverified after 2 presses; continuing")
        }
    }
    # Erst atmen lassen, dann bestaetigen -- siehe Get-SettledRoute.
    Start-Sleep -Milliseconds 400
    $confirmed = Get-SettledRoute -Label "route-index-$TargetIndex"
    Write-Nav "route index $TargetIndex confirmed on screen as '$confirmed'"
    return $confirmed
}

$script:SelectedRouteName = ""

function Invoke-RouteEnumeration {
    <#
        Walk the route carousel of the current category, recording every route
        name, until the list wraps (a name repeats) or a bound is hit. Writes
        routes.json so the board catalogue can be built for this category without
        knowing its routes in advance.
    #>
    param([int] $MaxRoutes = 60, [int] $SettleMs = 550)
    Write-Nav "enumerating routes for category '$RivalsMode'"
    $order = New-Object System.Collections.ArrayList

    # Ist die Streckenzahl bekannt, wird GENAU so oft weitergeschaltet und je Schritt
    # ein Eintrag geschrieben -- auch ein leerer.
    #
    # WARUM NICHT MEHR "bis sich ein Name wiederholt": Am 2026-08-28 las die Gast-OCR
    # bei Dirt Racing als ersten "Namen" die Kopfzeile "Routes". Nach neun Schritten
    # kam "Routes" erneut, die Rundlauf-Erkennung schlug zu, und aus 21 Strecken
    # wurden 9. Die Erkennung haengt also an der Zuverlaessigkeit der Namen -- genau
    # dem, was hier unzuverlaessig IST. Die Position ist verlaesslich, der Name nicht,
    # also zaehlt die Position.
    #
    # Die Namen werden hinterher ohnehin aus den Standbildern gelesen
    # (scripts/read_route_titles.py, Host-OCR statt Gast-OCR).
    $total = if ($RouteCount -gt 0) { $RouteCount } else { 0 }
    $steps = if ($total -gt 0) { $total } else { $MaxRoutes }
    if ($total -gt 0) {
        Write-Nav "walking exactly $total position(s) as announced by the category tile"
    }

    $seen = @{}
    $repeatStreak = 0
    for ($i = 0; $i -lt $steps; $i += 1) {
        $text = Read-Screen -Label "enum-$i" -Persist
        $name = Get-SelectedRoute -Text $text
        if ($total -gt 0) {
            # Ein Eintrag je Position, ohne Entdoppeln: Position i IST Position i,
            # auch wenn der Name unlesbar war.
            [void]$order.Add($(if ($name) { $name } else { "" }))
            Write-Nav "route $($order.Count): $(if ($name) { $name } else { '(unlesbar)' })"
        } elseif ($name) {
            if ($seen.ContainsKey($name)) {
                $repeatStreak += 1
                # Two consecutive already-seen names means the carousel has looped.
                if ($repeatStreak -ge 2) {
                    Write-Nav "route list wrapped at '$name'; enumeration complete"
                    break
                }
            } else {
                $repeatStreak = 0
                $seen[$name] = $true
                [void]$order.Add($name)
                Write-Nav "route $($order.Count): $name"
            }
        }
        Send-GameKey -Key RIGHT
        Start-Sleep -Milliseconds $SettleMs
    }
    $payload = [pscustomobject]@{
        rivals_mode = $RivalsMode
        route_count = $order.Count
        routes = @($order)
    }
    $outPath = Join-Path $runRoot "routes.json"
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $outPath -Encoding UTF8
    Write-Nav "enumerated $($order.Count) route(s) for '$RivalsMode' -> $outPath"
}

function Invoke-EnumerateMaps {
    # EIGENER param-Block, wie bei Invoke-EnumerateRoutes. Ohne ihn waere
    # $SettleMs hier $null, jede Pause null Millisekunden, und die Tasten kaemen,
    # bevor der Kartenschirm steht -- aufgenommen wuerde die Streckenliste, und
    # jede Strecke meldete "not the class screen, skipped".
    param([int] $SettleMs = 900)
    <#
        Je Strecke ein Standbild des KLASSENSCHIRMS -- dort zeichnet das Spiel den
        Verlauf magenta auf die Weltkarte, und darunter stehen Name und Laenge.

        Warum nicht der Streckenlisten-Schirm: der zeigt nur Namen. Die Karte
        erscheint erst, wenn eine Strecke geoeffnet ist.

        Warum ESC statt zurueckzaehlen: nach dem Oeffnen steht die Markierung
        wieder auf derselben Strecke, also genuegt ein RIGHT fuer die naechste.
        Sich die Position zu merken und neu anzufahren waere eine zweite Wahrheit
        ueber den Zustand des Schirms -- und die weicht irgendwann ab.
    #>
    $total = if ($RouteCount -gt 0) { $RouteCount } else { $MaxRoutes }
    Write-Nav "capturing $total route map(s) for '$RivalsMode'"
    $gemacht = 0
    for ($i = 0; $i -lt $total; $i += 1) {
        # Oeffnen.
        Send-GameKey -Key ENTER
        Start-Sleep -Milliseconds ($SettleMs * 2)

        # Das Bild MUSS den Klassenschirm zeigen. Ohne diese Pruefung landen
        # Standbilder der Streckenliste in der Ernte und sehen dort aus wie
        # Karten ohne Linie.
        $text = Read-Screen -Label "map-$i" -Persist
        if ($text -match 'Performance\s+Class' -or $text -match 'Route\s+Length') {
            $gemacht += 1
            Write-Nav "map $($i + 1)/$total captured"
        } else {
            Write-Nav "map $($i + 1)/${total}: not the class screen, skipped"
        }

        # Zurueck zur Liste.
        Send-GameKey -Key ESC
        Start-Sleep -Milliseconds ($SettleMs * 2)
        Send-GameKey -Key RIGHT
        Start-Sleep -Milliseconds $SettleMs
    }
    Write-Nav "captured $gemacht of $total map(s)"
}

function Get-TolerantClassPattern {
    <#
        A regex for the class header that survives OCR's digit/letter confusion.
        The header always names the selected class, but on this build 'S1' reads
        as 'SI' -- on 2026-08-20 all 24 seek presses (12 RIGHT, 12 LEFT) read
        "SI" Performance Class, so a literal \bS1\b never matched and the board
        was recorded unreachable while the strip was sitting on it. Only the
        digits are relaxed, and only to the glyphs they are actually confused
        with, so the classes stay mutually exclusive: 'S1' cannot match 'S2'.
    #>
    param([Parameter(Mandatory = $true)][string] $Class)
    return '\b' + (Get-ClassToken -Class $Class) + '\b\W{0,3}Performance\s+Class'
}

function Invoke-Route {
    <#
        A state machine rather than a fixed sequence. Each cycle identifies the
        current screen and takes the one action that advances it, so a run
        recovers from any starting point -- title screen, garage, map, or halfway
        into Rivals -- instead of assuming a known entry state.
    #>
    $trackPattern = Get-TolerantNamePattern -Name $Track
    $classPattern = Get-TolerantClassPattern -Class $PerformanceClass
    $modePattern = [regex]::Escape($RivalsMode)

    # 200, damit die Geduld von 150 Zyklen ueberhaupt ausgeschoepft werden kann: die
    # Schleife bricht sonst vorher mit "Route did not reach the leaderboard" ab, und die
    # Wartezeit auf eine lesbare Klassenzeile waere wieder wirkungslos. Der Rest sind die
    # Zyklen bis dorthin -- Titelbild, Rivals-Menue, Streckenliste.
    $maxCycles = 200
    $stateCounts = @{}
    # Zwei Zustaende sind kein Routenfehler, sondern Warten auf eine lesbare Zeile.
    #
    # Auf dem Klassenschirm steht '"D" Performance Class' weiss auf der
    # Satellitenkarte. Auf dunklen Karten (Shimanoyama) liest OCR das zuverlaessig;
    # auf hellen (Electric Town Circuit) nur sporadisch -- gemessen 2 von 41 Frames.
    # Ist die Zeile unlesbar, faellt derselbe Schirm je nach Restinhalt auf
    # 'rival_detail' ("Time to Beat") oder 'unknown'. Beide Handler bewegen dort
    # nichts: das ESC ist auf diesem Schirm wirkungslos, er blieb 15 Zyklen stehen.
    # Die Schleife wartet also nur darauf, dass die Zeile lesbar wird.
    #
    # Bei ~5% Trefferquote je Frame reichten 15 Zyklen fuer 1-0.95^15 = 54%: am
    # 2026-08-24 scheiterte Electric Town D zweimal, waehrend C direkt danach beim
    # Lesen in Zyklus 38 durchkam -- derselbe Schirm, ein Wuerfelwurf. 55 Zyklen
    # heben das auf ~94%.
    #
    # Falsche Daten koennen dadurch nicht entstehen: der class_screen-Handler
    # bestaetigt die Klasse aus der Kopfzeile, bevor er Y drueckt, und WIRFT sonst
    # ("Could not select performance class"). Laenger lesen erhoeht nur die Chance,
    # die Bestaetigung ueberhaupt zu bekommen. Die Kacheln der Leiste taugen als
    # Ersatzquelle nicht: auf 701 Frames mit lesbarer Kopfzeile sind stets ALLE
    # Kachelzahlen lesbar (600/700/800/900 gleich oft), sie zeigen nicht die Auswahl.
    # 150, nicht 55. Die Grenze zaehlt Zyklen, gemeint ist aber WARTEZEIT, und die beiden
    # haben sich mit dem Entfernen des ESC entkoppelt: vorher kostete ein Zyklus rund 10
    # Sekunden (Tastendruck plus Pause), 55 davon waren also gut neun Minuten. Ohne
    # Tastendruck dauert ein Zyklus 1,4 Sekunden -- dieselben 55 sind dann 78 Sekunden.
    # Genau daran scheiterte Tateyama Kurobe Sprint D am 2026-08-26 um 03:08, obwohl das
    # Warten selbst korrekt lief. 150 Zyklen a 2,5 s sind wieder rund sechs Minuten.
    $patientStates = @{ rival_detail = 150; unknown = 150 }
    # [RIGHT, DOWN] is the plan that actually opened the Rivals hub on
    # 6.420.696.0, so it is tried first; the rest are fallbacks.
    $tilePlans = @(@("RIGHT", "DOWN"), @(), @("DOWN"), @("RIGHT"), @("DOWN", "DOWN"), @("RIGHT", "RIGHT"))
    $tileAttempt = 0
    $hubPlans = @(@(), @("RIGHT"), @("RIGHT", "RIGHT"), @("LEFT"), @("RIGHT", "RIGHT", "RIGHT"))
    $hubAttempt = 0
    # One counter for "not on a recognised menu", covering blank frames, loading
    # cards and unrecognised HUD alike. Separate per-state counters do not work:
    # free roam alternates between a blank frame, a sparse HUD frame and a
    # 4-line HUD frame, so each state's streak kept resetting the others and the
    # escalation never fired.
    $offMenuCycles = 0
    $lastState = ""
    # Back out to the route list rather than just one screen: from there the
    # normal route re-selects both track and class, so one mechanism covers a
    # class change and a track change instead of needing a different ESC depth
    # for each.
    $leavingPreviousBoard = $FromLeaderboard.IsPresent
    # HAT DIESER LAUF DIE STRECKENLISTE SCHON GESEHEN?
    #
    # Davon haengt ab, was ein Rivalen-Detailschirm bedeutet. Kommen wir ueber die
    # Streckenliste dorthin, ist es der Schirm der ANGEFORDERTEN Strecke, und die
    # richtige Antwort auf eine unlesbare Klassenzeile ist stehenbleiben und neu
    # lesen (die Begruendung steht beim rival_detail-Zweig).
    #
    # Stehen wir schon beim START darauf, ist es ein UEBERBLEIBSEL der vorigen
    # Navigation -- moeglicherweise in einer ganz anderen Kategorie. Gemessen am
    # 2026-09-14 um 15:15: der Sweep wollte Street Racing idx9, das Spiel zeigte
    # Shinjuku Gyoen Cross-Country, und der Navigator las dieselbe unlesbare
    # Klassenzeile 106 Mal, weil er den Schirm fuer den richtigen hielt. Sechs
    # Minuten je Versuch, und am Ende ein "Stuck in state", das nach einem
    # Anzeigefehler aussieht statt nach dem Navigationsfehler, der es ist.
    $routeListSeen = $false
    # Hat DIESER Lauf die Kategorie selbst geoeffnet und am Wahrzeichen
    # bestaetigt? Nur dann darf -EnumerateMaps die offene Liste abgehen.
    $categoryConfirmed = $false

    for ($cycle = 0; $cycle -lt $maxCycles; $cycle += 1) {
        $text = Read-Screen -Label "cycle-$cycle" -Persist
        $state = Get-ScreenState -Text $text
        if ($state -ne $lastState) {
            Write-Nav "state: $state"
            $lastState = $state
        }
        if (-not $stateCounts.ContainsKey($state)) { $stateCounts[$state] = 0 }
        $stateCounts[$state] += 1

        # A state that will not advance is a route bug, not something to hammer.
        $stateLimit = if ($patientStates.ContainsKey($state)) { $patientStates[$state] } else { 14 }
        if ($stateCounts[$state] -gt $stateLimit -and $state -notin @("loading", "loading_screen")) {
            $visible = (($text -replace $script:NoisePattern, '') -split "`r?`n" |
                Where-Object { $_ -match '\S' }) -join ' / '
            throw "Stuck in state '$state' after $($stateCounts[$state]) cycles. Screen: $visible"
        }

        if ($state -in @("loading", "loading_screen", "unknown")) {
            $offMenuCycles += 1
        } else {
            $offMenuCycles = 0
        }

        # Off-menu for a while means either a slow load or free roam with the HUD
        # hidden, and those are indistinguishable from screen text alone. ESC is
        # harmless mid-load and is the only action that progresses from free roam,
        # so escalate periodically rather than trying to tell them apart.
        if ($offMenuCycles -ge 5) {
            Write-Nav "off-menu for $offMenuCycles cycles; pressing ESC to reach a known screen"
            Send-GameKey -Key ESC
            $offMenuCycles = 0
            Start-Sleep -Milliseconds 1800
            continue
        }

        switch ($state) {
            "game_crashed" {
                # AUFGEBEN UND ES SAGEN. Das Spiel ist tot; es wieder zu beleben ist
                # nicht die Aufgabe des Navigators, sondern die der Ebene darueber,
                # die auch weiss, wie es gestartet wurde. Die Meldung ist so
                # formuliert, dass ocr_board_sweep sie erkennen kann.
                throw "Game crashed: Forza has terminated unexpectedly. Screen: $visible"
            }
            "server_error" {
                # Sofort scheitern, nicht warten. Der Schirm sagt selbst, dass die
                # Verbindung weg ist -- kein Tastendruck und keine Geduld aendern daran
                # etwas. Der Rueckzug gehoert eine Ebene hoeher: ocr_board_sweep erkennt
                # diese Meldung in der Ausgabe, pausiert acht Minuten und faehrt DASSELBE
                # Board erneut an, bis zu dreimal. Je frueher hier abgebrochen wird, desto
                # frueher beginnt diese Pause.
                throw "Server Error: the game cannot reach the leaderboard service."
            }
            "leaderboard" {
                if ($leavingPreviousBoard) {
                    Write-Nav "on the previous board's leaderboard; backing out to re-select"
                    Send-GameKey -Key ESC
                    Start-Sleep -Milliseconds 1200
                } else {
                    Write-Nav "leaderboard reached"
                    return $true
                }
            }
            "loading" {
                # A blank screen is ambiguous: either a load is in progress, or we
                # are stood in free roam with the gameplay HUD auto-hidden. The
                # mouse nudge in Read-Screen restores the Windows cursor but not
                # the game's HUD, so a blank screen can persist indefinitely.
                # After a bounded quiet period, assume free roam and open the
                # pause menu, which is the only action that makes progress from
                # there and is harmless mid-load.
                Start-Sleep -Seconds 2
            }
            "loading_screen" {
                # Counts towards the same quiet streak as a blank screen. Free
                # roam alternates between a blank frame and a sparse HUD frame, so
                # resetting the counter here meant the streak never reached the
                # escalation threshold and the run sat in free roam until it ran
                # out of cycles.
                Start-Sleep -Seconds 2
            }
            "title" {
                Send-GameKey -Key ENTER
                Start-Sleep -Seconds 3
            }
            "series_update" {
                # ENTER blaettert durch die Seiten der Serie und schliesst sie am Ende.
                # ESC waere schneller, wird auf diesem Schirm aber nicht ueberall
                # angenommen; ENTER kommt immer durch.
                Send-GameKey -Key ENTER
                Start-Sleep -Seconds 2
            }
            "continue_menu" {
                Send-GameKey -Key ENTER
                Write-Nav "selected Continue; waiting for the world to load"
                Start-Sleep -Seconds 8
            }
            "garage_menu" {
                # The garage carousel has no Online tab at all, so Rivals cannot
                # be reached from here; leave via CAMPAIGN > Drive.
                if (-not (Invoke-Seek -Key LEFT -Pattern 'Festival Playlist' -MaxAttempts 6 -SettleMs 800 -Label "campaign-section")) {
                    throw "Could not select the Campaign section in the garage menu."
                }
                Send-GameKey -Key ENTER
                Write-Nav "selected Drive to leave the garage"
                Start-Sleep -Seconds 8
            }
            "map" {
                Send-GameKey -Key ESC
                Start-Sleep -Milliseconds 1200
            }
            "world_pause" {
                if (-not (Invoke-Seek -Key RIGHT -Pattern $script:States["online_tab"] -MaxAttempts 10 -SettleMs 900 -Label "online-tab")) {
                    throw "Could not activate the ONLINE tab from the world pause menu."
                }
            }
            "online_tab" {
                # The tile row carries its own labels -- "Rivals" over the
                # subtitle "Top the Leaderboards" -- so hover the label rather
                # than guessing how many tiles right it sits. Guessing is what
                # sent three consecutive runs into the Social panel's Online
                # Player List: [RIGHT,DOWN] was correct before the patch and
                # points somewhere else now.
                $opened = Select-ByHover -Pattern '^\s*(?:Rivals|Top the Leaderboards)\s*$' `
                    -Expect $script:States["rivals_hub"] `
                    -TimeoutSeconds 40 -Label "rivals-tile"
                if ($opened) {
                    Write-Nav "opened the Rivals hub by hovering its tile"
                } elseif ($tileAttempt -ge $tilePlans.Count) {
                    throw "Neither hovering the Rivals tile nor any tile position opened Rivals from the ONLINE tab."
                } else {
                    # Fallback only: kept because a build that renders the label
                    # as an image would defeat the hover, and a blind plan that
                    # backs out of its mistakes still beats stopping.
                    $plan = $tilePlans[$tileAttempt]
                    $tileAttempt += 1
                    Write-Nav "hover did not open Rivals; falling back to [$($plan -join ',')] then ENTER"
                    foreach ($key in $plan) {
                        Send-GameKey -Key $key
                        Start-Sleep -Milliseconds 500
                    }
                    Send-GameKey -Key ENTER
                    Start-Sleep -Seconds 3
                    $after = Read-Screen -Label "tile-check-$tileAttempt"
                    if ((Get-ScreenState -Text $after) -eq "online_tab") {
                        Write-Nav "still on the ONLINE tab; backing out"
                        Send-GameKey -Key ESC
                        Start-Sleep -Milliseconds 900
                    }
                }
            }
            "confirm_dialog" {
                # ESC does NOT dismiss these dialogs -- they have explicit No/Yes
                # buttons and a "Select / ENTER" prompt, so escaping just leaves
                # the modal on screen forever. "No" is the left button, so move
                # left (a no-op if it is already selected) and confirm that.
                Write-Nav "confirmation dialog; selecting No"
                Send-GameKey -Key LEFT
                Start-Sleep -Milliseconds 400
                Send-GameKey -Key ENTER
                Start-Sleep -Milliseconds 1500
            }
            "forza_link" {
                Write-Nav "Forza LINK menu opened by mistake; backing out"
                Send-GameKey -Key ESC
                Start-Sleep -Milliseconds 1200
            }
            "rivals_hub" {
                # Horizon Rivals is the tile that leads to per-route, per-class
                # leaderboards. Highlight state is invisible to OCR, so try the
                # leftmost tile first and fall back along the row.
                if ($hubAttempt -ge $hubPlans.Count) {
                    throw "None of the tried Rivals hub tiles led onwards."
                }
                $plan = $hubPlans[$hubAttempt]
                $hubAttempt += 1
                Write-Nav "entering Rivals hub tile via [$($plan -join ',')] then ENTER"
                foreach ($key in $plan) {
                    Send-GameKey -Key $key
                    Start-Sleep -Milliseconds 500
                }
                Send-GameKey -Key ENTER
                Start-Sleep -Seconds 4
                $after = Read-Screen -Label "hub-check-$hubAttempt"
                if ((Get-ScreenState -Text $after) -eq "rivals_hub") {
                    Write-Nav "still on the Rivals hub; backing out"
                    Send-GameKey -Key ESC
                    Start-Sleep -Milliseconds 900
                }
            }
            "rivals_modes" {
                # Der Kategorieschirm ist ein 2x3-GITTER, kein senkrechtes Menue:
                #
                #     Road Racing 23    Cross-Country 19   Street Racing 15
                #     Dirt Racing 21    Drag Racing 3      Touge 5
                #
                # Zwei frueher Versuche sind daran gescheitert, und beide Male war
                # die Ursache dieselbe -- niemand hat auf das BILD geschaut:
                #
                # 1. `Invoke-Seek -Key RIGHT -Pattern <Name>` brach beim ersten Blick
                #    ab, weil jeder Name schon auf dem Schirm steht. Es wurde nie eine
                #    Taste gedrueckt, ENTER nahm die zuletzt benutzte Kategorie.
                # 2. `Select-ByHover` auf das Label: am 2026-08-28 direkt geprueft --
                #    aus dem Kategorieschirm heraus mit -RivalsMode "Street Racing"
                #    aufgerufen, geoeffnet wurde Road Racing (23 Strecken statt 15).
                #
                # Beides scheiterte am selben Punkt: die Markierung ist ein gelber
                # RAHMEN und steht in keinem OCR-Text. Also wird sie nicht gelesen,
                # sondern hergestellt: zweimal LINKS und einmal OBEN landen aus jeder
                # Position des Gitters garantiert oben links (Tasten am Rand tun
                # nichts), und von dort zaehlen die Offsets.
                $landmark = $script:CategoryLandmark[$RivalsMode]

                # Den NAMEN anfahren, nicht Positionen zaehlen.
                #
                # Der Kategorieschirm schreibt seine Eintraege aus -- "Touge" ueber
                # "5 Routes Available" -- und Forzas Menues folgen dem Mauszeiger.
                # Genau so waehlt dieses Skript schon die Rivals-Kachel im
                # ONLINE-Reiter, aus demselben Grund: ein Tastendruck-Zaehlwerk ist
                # eine Annahme, die jedes Spiel-Update entwertet.
                #
                # Es WAR eine solche Annahme. Die 2x3-Karte vom 2026-08-28 entstand,
                # als Drag Racing und Touge gesperrt waren. Am 2026-09-12 oeffnete der
                # Navigator damit zuverlaessig Dirt Racing statt Touge; danach habe ich
                # erst alle sechs Zellen und dann acht Plaetze linear durchprobiert --
                # beides fand nichts, waehrend der Rahmenleser Touge auf Anhieb fand.
                # Zwei Fehlschlaege, weil ich die Anordnung erraten wollte, statt den
                # Namen zu benutzen, der die ganze Zeit auf dem Schirm stand.
                #
                # Das Wahrzeichen bleibt der Richter: getroffen heisst erst, dass die
                # geoeffnete Streckenliste die erwartete Strecke zeigt.
                #
                # UND DAS WAHRZEICHEN STEUERT NACH, statt nur abzubrechen.
                #
                # Es ist der einzige verlaessliche Richter, den es hier gibt: welche
                # Kachel markiert ist, steht in keinem Text, aber welche Liste sich
                # geoeffnet hat, steht in jeder Zeile. Am 2026-09-12 hat er eine
                # Road-Racing-Liste ("Highway Circuit") als Nicht-Touge erkannt und
                # den ganzen Lauf beendet -- richtig erkannt, falsch reagiert: der
                # Weg zurueck ist EIN Tastendruck, und der naechste Versuch kostet
                # zehn Sekunden statt eines Laufs.
                $muster = "^\s*" + [regex]::Escape($RivalsMode) + "\s*$"
                $bestaetigt = $false
                $letzterGrund = "kein Versuch"
                # NUR EIN VERSUCH -- der zweite und dritte sind nachweislich
                # sinnlos.
                #
                # GEMESSEN am 2026-09-13: nach drei Schwebe-Versuchen stand die
                # Markierung unveraendert auf Road Racing (Rahmenleser:
                # Schwerpunkt 0.198/0.381, oben links), obwohl der Zeiger jedes Mal
                # exakt auf dem Schriftzug 'Touge' lag. Auf DIESEM Schirm folgt die
                # Markierung dem Mauszeiger nicht -- anders als im ONLINE-Reiter,
                # wo dieselbe Technik die Rivals-Kachel zuverlaessig oeffnet.
                #
                # Der Aufrufer faengt den Fehlschlag ohnehin mit dem Rahmenleser ab
                # (select_rivals_category.py, Pfeiltasten mit Kontrolle nach jedem
                # Schritt). Drei Anlaeufe kosten nur anderthalb Minuten je Board --
                # ueber 43 Boards eine Stunde.
                # ZWEI Versuche mit dem Rahmen: der ist, anders als das Schweben,
                # deterministisch -- ein zweiter Anlauf faengt nur einen Schirm ab,
                # der beim ersten Blick noch nicht fertig gezeichnet war.
                for ($versuch = 1; $versuch -le 2 -and -not $bestaetigt; $versuch++) {
                    # Der Schirm baut sich auf; wer sofort drueckt, drueckt auf ein
                    # Bild, das das Menue noch gar nicht annimmt.
                    Start-Sleep -Milliseconds 1500
                    Write-Nav "category: bringe den Rahmen auf '$RivalsMode' (Versuch $versuch von 2)"
                    $geoeffnet = $false
                    if (Select-CategoryByFrame -Category $RivalsMode) {
                        $geoeffnet = Wait-ForMarker -Pattern $script:States["route_list"] `
                            -TimeoutSeconds 40 -Label "category-open-$versuch"
                    }

                    if (-not $geoeffnet) {
                        $letzterGrund = "die Streckenliste ging nicht auf"
                        Write-Nav "category: $letzterGrund (Versuch $versuch)"
                        Send-GameKey -Key ESC
                        Start-Sleep -Milliseconds 1200
                        continue
                    }

                    if (-not $landmark) {
                        Write-Nav ("category '$RivalsMode' opened; no landmark known " +
                                   "yet, so nothing was verified")
                        $bestaetigt = $true
                        break
                    }

                    # Eine gerade geoeffnete Liste ist selten sofort gezeichnet; 2 s
                    # reichten am 2026-09-12 nicht. 12 s, dann faellt das Urteil.
                    if (Wait-ForText -Pattern $landmark -Seconds 12 -Label "category-check-$versuch") {
                        Write-Nav "category confirmed: '$landmark' is in the route list"
                        $bestaetigt = $true
                        break
                    }

                    $letzterGrund = "die geoeffnete Liste zeigt '$landmark' nicht"
                    Write-Nav ("category: $letzterGrund (Versuch $versuch) -- " +
                               "mit ESC zurueck und nochmal")
                    Send-GameKey -Key ESC
                    Start-Sleep -Seconds 2
                }

                if (-not $bestaetigt) {
                    throw ("Keine bestaetigte Kategorie '$RivalsMode': " +
                           "$letzterGrund. Refusing to scan the wrong category.")
                }
                $categoryConfirmed = $true
            }
            "route_list" {
                $routeListSeen = $true
                if ($EnumerateMaps) {
                    # NUR EINE LISTE, DIE DIESER LAUF SELBST GEOEFFNET UND
                    # BESTAETIGT HAT. Am 2026-09-24 begann der Cross-Country-Lauf auf
                    # der Road-Racing-Liste, die der vorige Lauf offen gelassen hatte,
                    # und nahm deren Karten ein zweites Mal auf -- Cross-Country selbst
                    # waere nie drangekommen, und jede weitere Kategorie genauso.
                    # Ein Wahrzeichen auf dem Schirm genuegt als Beweis nicht: ob es
                    # sichtbar ist, haengt davon ab, wo das Karussell gerade steht.
                    if (-not $categoryConfirmed) {
                        Write-Nav ("route list open, but this run did not open and confirm " +
                                   "'$RivalsMode' -- backing out to the category screen")
                        Send-GameKey -Key ESC
                        Start-Sleep -Seconds 3
                        continue
                    }
                    Invoke-EnumerateMaps
                    # return, nicht break: break verliesse nur den switch, und die
                    # Schleife liefe weiter. Und NICHT $state beschreiben -- das ist
                    # hier der Name des Schirms, ein String; genau daran brach der
                    # Lauf vom 2026-09-24 nach 23 aufgenommenen Karten ab.
                    return $true
                }
                if ($EnumerateRoutes) {
                    # Erst pruefen, WELCHE Kategorie hier offen ist. Am 2026-08-28
                    # wurde mit -RivalsMode "Street Racing" aus einem offenen
                    # Road-Racing-Board heraus aufgenommen: der Navigator war schon
                    # in einer Streckenliste, hat sie gezaehlt und 23 Road-Racing-
                    # Strecken als "Street Racing" ausgegeben. Die Liste war nicht
                    # falsch, die Beschriftung war es -- und das ist die teurere
                    # Sorte Fehler, weil sie plausibel aussieht.
                    $landmark = $script:CategoryLandmark[$RivalsMode]
                    $wrong = $null
                    if ($landmark) {
                        # Positive Probe, wenn die Kategorie schon bekannt ist.
                        if (-not (Find-TextRect -Pattern $landmark -Refresh -Label "enum-category-check")) {
                            $wrong = "'$RivalsMode' landmark is missing"
                        }
                    } else {
                        # Fuer eine NOCH UNBEKANNTE Kategorie gibt es kein eigenes
                        # Wahrzeichen -- und das ist genau der Fall, in dem man es
                        # braucht. Also die Umkehrung: findet sich hier das
                        # Wahrzeichen einer ANDEREN bekannten Kategorie, ist diese
                        # Liste sicher die falsche.
                        foreach ($known in $script:CategoryLandmark.Keys) {
                            if ($known -eq $RivalsMode) { continue }
                            if (Find-TextRect -Pattern $script:CategoryLandmark[$known] -Refresh -Label "enum-foreign-check") {
                                $wrong = "this is '$known', not '$RivalsMode'"
                                break
                            }
                        }
                    }
                    if ($wrong) {
                        Write-Nav "route list open, but $wrong -- backing out to the category screen"
                        Send-GameKey -Key ESC
                        Start-Sleep -Seconds 3
                        continue
                    }
                    Invoke-RouteEnumeration
                    return $true
                }
                if ($leavingPreviousBoard) {
                    Write-Nav "reached the route list; resuming the normal route"
                    $leavingPreviousBoard = $false
                }
                if ($RouteIndex -ge 0) {
                    $script:SelectedRouteName = Select-RouteByIndex -TargetIndex $RouteIndex
                    Send-GameKey -Key ENTER
                    Start-Sleep -Seconds 3
                } else {
                    # The route list is a horizontal carousel on this build, so
                    # RIGHT is tried first; DOWN is kept as a fallback in case a
                    # future build changes the layout.
                    if (-not (Invoke-SeekAny -Keys @("RIGHT", "DOWN") -Pattern $trackPattern -MaxAttempts 60 -SettleMs 500 -Label "track")) {
                        throw "Could not find track '$Track' in the route list."
                    }
                    Send-GameKey -Key ENTER
                    Start-Sleep -Seconds 3
                }
            }
            "rival_detail" {
                if ($leavingPreviousBoard) {
                    Write-Nav "backing out of the rival detail card"
                    Send-GameKey -Key ESC
                    Start-Sleep -Milliseconds 1200
                } else {
                    # Dieser Zustand IST derselbe Schirm wie class_screen --
                    # Karte, Streckenname, Klassenleiste UND das Details-Feld mit
                    # "Time to Beat" stehen gleichzeitig darauf; siehe
                    # data/runtime/navigation/20260824_201351/frames/0042.
                    # Hier statt als class_screen einsortiert wird er nur, wenn
                    # '"D" Performance Class' nicht gelesen wurde: die Zeile steht
                    # weiss auf der Satellitenkarte und verschwindet auf hellen
                    # Karten wie der von Electric Town Circuit.
                    #
                    # Y (der Weg zum Board) wird hier NICHT gedrueckt, obwohl es
                    # der richtige Tastendruck waere: welche Klasse die Leiste
                    # gerade zeigt, ist auf diesem Schirm aus keiner OCR-Quelle
                    # ablesbar. Die Kacheln taugen nicht dafuer -- auf 701 Frames
                    # mit lesbarer Klassenzeile sind stets ALLE Kachelzahlen
                    # lesbar (600/700/800/900 gleich oft), sie zeigen also nicht
                    # die Auswahl. Ein Y ins Ungewisse oeffnete ein Board
                    # unbekannter Klasse und schriebe dessen Zeilen unter der
                    # angeforderten Klasse fort: falsche Daten sind schlimmer als
                    # ein nicht erreichtes Board. Erst muss die Klassenzeile
                    # lesbar werden (Get-ScreenState), dann greift class_screen
                    # von selbst und dessen Y ist gedeckt.
                    # HIER KEINE TASTE. Das ESC, das frueher hier stand, war die
                    # Ursache einer Endlosschleife: es bringt zur route_list zurueck,
                    # der Navigator waehlt Index 7 erneut, landet wieder hier, drueckt
                    # wieder ESC. Am 2026-08-25 um 02:40 lief idx07 A viermal so im
                    # Kreis, bis der Ankersuchlauf aufgab -- die Meldung lautete dann
                    # "Could not find the anchor route", was nach einem Karussellfehler
                    # aussieht und in die voellig falsche Richtung weist.
                    # Auch $patientStates rettet das nicht: der Zustand WECHSELT bei
                    # jedem Umlauf (unknown -> rival_detail -> route_list), also erreicht
                    # keiner die Grenze von 55.
                    # Richtig ist stehenbleiben und neu lesen: dieser Schirm IST der
                    # Klassenschirm, nur ist '"D" Performance Class' weiss auf heller
                    # Karte und nur sporadisch lesbar (gemessen 2 von 41 Frames). Sobald
                    # die Zeile durchkommt, greift class_screen und dessen Y oeffnet das
                    # Board -- mit bestaetigter Klasse. Genau so sind idx07 D, C und B
                    # in dieser Nacht durchgelaufen.
                    # 2,5 s zwischen den Lesungen, nicht 1,2. Lesbare Fenster halten
                    # mehrere Frames an -- dicht aufeinanderfolgende Lesungen treffen
                    # also DASSELBE unlesbare Fenster mehrfach und bringen nichts.
                    # Gestreute Lesungen decken mehr verschiedene Fenster ab, und jeder
                    # Zyklus kostet im Gast eine Aufnahme samt OCR.
                    if (-not $routeListSeen) {
                        # Ein Ueberbleibsel: dieser Schirm gehoert zu einer Strecke,
                        # die wir nicht angefordert haben. Warten kann ihn nicht
                        # richtig machen -- also raus zur Streckenliste, von der aus
                        # Strecke UND Klasse neu gewaehlt werden.
                        #
                        # Eine Endlosschleife kann daraus nicht werden: das ESC
                        # fuehrt zur Streckenliste, die setzt $routeListSeen, und ab
                        # dann gilt wieder das Warten. Genau diese Schleife gab es
                        # am 2026-08-25, als hier ein unbedingtes ESC stand.
                        Write-Nav "rivals detail screen left over from an earlier navigation; backing out to the route list"
                        Send-GameKey -Key ESC
                        Start-Sleep -Milliseconds 1200
                    } else {
                        Write-Nav "class screen with an unreadable class line; waiting for a legible frame"
                        Start-Sleep -Milliseconds 2500
                    }
                }
            }
            "class_screen" {
                if ($leavingPreviousBoard) {
                    # Still on the way out. Selecting the class here would pick it
                    # on the OLD track, so keep backing out until the route list.
                    # if/else rather than `continue`: inside a switch, `continue`
                    # means "next switch condition" in PowerShell, not "next loop
                    # iteration", so it would fall through to the selection below.
                    Write-Nav "backing out past the class strip"
                    Send-GameKey -Key ESC
                    Start-Sleep -Milliseconds 1200
                } else {
                    # The class strip reads D 400 / C 500 / B 600 / A 700 / S1 800 /
                    # S2 900 / R / X and opens on whichever class the player last
                    # used, so the wanted one has to be sought rather than assumed.
                    # The strip is horizontal and only part of it is on screen: R
                    # sits to the right of S2 and is not rendered until scrolled to,
                    # so it cannot be found by reading the strip. The header always
                    # names the selected class, so scroll RIGHT and watch it.
                    if ($text -notmatch $classPattern) {
                        if (-not (Invoke-SeekAny -Keys @("RIGHT", "LEFT") -Pattern $classPattern -MaxAttempts 12 -SettleMs 700 -Label "class" -LogText)) {
                            throw "Could not select performance class '$PerformanceClass'."
                        }
                    }
                    # The leaderboard is behind "Change Rival", bound to Y. ENTER
                    # here would start a race against the current rival instead.
                    Write-Nav "class '$PerformanceClass' selected; opening Change Rival with Y"
                    Send-GameKey -Key Y
                    Start-Sleep -Seconds 4
                }
            }
            default {
                # Free roam shows a sparse HUD -- location, car, gamertag -- that
                # matches no menu, so "unknown" is the normal resting state there
                # rather than an error. ESC is the one action that makes progress
                # from free roam and also backs out of a stray menu.
                #
                # The streak must be consecutive, not cumulative: an earlier
                # version counted every unknown seen during the run, so once the
                # splash screens had pushed the total past the threshold it fired
                # ESC on every single unknown frame thereafter.
                $visible = (($text -replace $script:NoisePattern, '') -split "`r?`n" |
                    Where-Object { $_ -match '\S' }) -join ' / '
                Write-Nav "UNKNOWN screen: $visible"
                Start-Sleep -Seconds 2
            }
        }
    }
    throw "Route did not reach the leaderboard within $maxCycles cycles."
}

# ---------------------------------------------------------------- main

$state = [ordered]@{
    run_id = $RunId
    started_at = (Get-Date).ToString("o")
    track = $Track
    performance_class = $PerformanceClass
    rivals_mode = $RivalsMode
    mode = if ($Explore) { "explore" } else { "route" }
    status = "running"
}
$state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8

if ($ClassifyFrames) {
    function ConvertTo-Reference {
        param([System.Drawing.Bitmap] $Source, [int] $Height)
        $ziel = $Source
        if ($Height -gt 0) {
            $w = [int][math]::Round($Height * 16 / 9)
            $klein = New-Object System.Drawing.Bitmap($w, $Height)
            $g = [System.Drawing.Graphics]::FromImage($klein)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.DrawImage($Source, 0, 0, $w, $Height); $g.Dispose()
            $ziel = $klein
        }
        $ref = New-Object System.Drawing.Bitmap($script:RefWidth, $script:RefHeight, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($ref)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($ziel, 0, 0, $script:RefWidth, $script:RefHeight); $g.Dispose()
        if ($ziel -ne $Source) { $ziel.Dispose() }
        return $ref
    }
    $dateien = if (Test-Path -LiteralPath $ClassifyFrames -PathType Container) {
        Get-ChildItem -LiteralPath $ClassifyFrames -Filter *.png | Sort-Object Name
    } else { @(Get-Item -LiteralPath $ClassifyFrames) }
    foreach ($d in $dateien) {
        $quelle = [System.Drawing.Bitmap]::FromFile($d.FullName)
        try { $ref = ConvertTo-Reference -Source $quelle -Height $SimulateHeight } finally { $quelle.Dispose() }
        $frame = Get-ScreenFrameOnce -FromBitmap $ref
        $zustand = Get-ScreenState -Text $frame.Text
        $klasse = if ($frame.Text -match '"?(D|C|B|A|S1|SI|S2|R|X)"?\W{0,3}Performance\s+Class') { $Matches[1] } else { "-" }
        $laenge = if ($frame.Text -match 'Route\s+Length[: ]+([0-9]+(?:\.[0-9]+)?)') { $Matches[1] } else { "-" }
        $kurz = (($frame.Text -split "\s+") -join " "); if ($kurz.Length -gt 160) { $kurz = $kurz.Substring(0, 160) }
        "{0}`t{1}`tclass={2}`tlength={3}`t{4}" -f $d.Name, $zustand, $klasse, $laenge, $kurz
    }
    exit 0
}

if ($CaptureTest) {
    # Eine Aufnahme, keine Taste. Prueft Bereich, Abbildung und Lesbarkeit.
    Initialize-GameWindow
    $r = $script:CaptureRect
    $uhr = [System.Diagnostics.Stopwatch]::StartNew()
    $frame = Get-ScreenFrame
    $ms = $uhr.ElapsedMilliseconds
    Save-Frame -Label "capture-test" -Png $frame.Png -Text $frame.Text
    $ecke = "{0},{1} .. {2},{3}" -f [NavWin]::ToScreenX(0), [NavWin]::ToScreenY(0),
            [NavWin]::ToScreenX($script:RefWidth), [NavWin]::ToScreenY($script:RefHeight)
    Write-Nav "capture test: $($frame.Lines.Count) text line(s) in $ms ms; reference corners map to $ecke"
    Write-Nav ("capture test: first lines: " + ((@($frame.Lines | Select-Object -First 6 | ForEach-Object { $_.Text })) -join ' / '))
    $ok = ([NavWin]::ToScreenX(0) -eq $r.X -and [NavWin]::ToScreenY(0) -eq $r.Y -and
           [NavWin]::ToScreenX($script:RefWidth) -eq ($r.X + $r.Width) -and
           [NavWin]::ToScreenY($script:RefHeight) -eq ($r.Y + $r.Height))
    Write-Nav "capture test: mapping $(if ($ok) { 'ok' } else { 'WRONG' }); frames in $frameRoot"
    exit $(if ($ok -and $frame.Lines.Count -gt 0) { 0 } else { 1 })
}

try {
    Initialize-GameWindow
    if ($Explore) {
        Invoke-Explore
        $state["status"] = "explored"
    } else {
        [void](Invoke-Route)
        $state["status"] = if ($EnumerateMaps) { "maps_captured" } else { "leaderboard_reached" }
        if ($script:SelectedRouteName) {
            $state["confirmed_route_name"] = $script:SelectedRouteName
            Write-Nav "confirmed route on the board: $($script:SelectedRouteName)"
        }
    }
    $state["completed_at"] = (Get-Date).ToString("o")
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Nav "done: $($state.status)"
    exit 0
} catch {
    $state["status"] = "failed"
    $state["error"] = $_.Exception.Message
    $state["completed_at"] = (Get-Date).ToString("o")
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Nav "FAILED $($_.Exception.Message)"
    exit 1
}
