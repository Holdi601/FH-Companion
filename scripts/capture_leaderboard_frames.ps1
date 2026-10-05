<#
.SYNOPSIS
    Grab the leaderboard table as a frame sequence while something else scrolls it.

.DESCRIPTION
    The screen route to the data, and deliberately a SEPARATE process from the one
    pressing keys: every earlier attempt failed because one thread both drove the list
    and read it, so it was blind exactly while the list moved.

    Only the table is captured, not the whole screen, and the driver column is left in
    the crop but never OCR'd -- gamertags with emoji are the one thing screen reading
    is bad at, and nothing downstream needs them.

    Frame budget: the table shows 11 rows at once and the list travels ~116 rows/s, so
    a frame every ~90 ms already overlaps. This runs faster than that on purpose,
    because a dropped frame is a hole in the data.

.EXAMPLE
    .\capture_leaderboard_frames.ps1 -OutDir C:\ForzaAutomation\data\frames\run1 -Seconds 90
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OutDir,
    [int] $Seconds = 90,
    [int] $IntervalMs = 45,
    # The table, measured from a 1920x1080 capture of the Change Rival screen:
    # ranks start at x=110, the GEAR column ends by x=1770, rows run y=270..875.
    [int] $X = 110,
    [int] $Y = 268,
    [int] $Width = 1660,
    [int] $Height = 610,
    [int] $MaxFrames = 4000,
    # "x,y,breite,hoehe" in Bildschirmpunkten statt der Spielfensterflaeche -- fuer
    # Tests anderer Aufloesungen. X/Y/Width/Height bleiben 1080p-Bezugspunkte.
    [string] $CaptureArea = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

# JEDE AUFLOESUNG. X, Y, Width und Height sind 1080p-BEZUGSPUNKTE -- dort wurde
# die Tabelle vermessen, und dort ist die OCR-Spaltenkarte zu Hause. Aufgenommen
# wird die Spielflaeche: der Ausschnitt wird auf den echten Schirm umgerechnet und
# per StretchBlt direkt in ein Bild der Bezugsgroesse gezogen. Die Frames sehen so
# bei 720p, 4K oder 16K aus wie bei 1080p, und ohne Vollbild im Speicher.
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class FrameGrab {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int m);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, int sw, int sh, int rop);

    // Zeichenflaeche des sichtbaren "App"-Fensters des Spiels; null ohne Fenster.
    public static int[] GameArea(string processName) {
        var pids = new System.Collections.Generic.HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(processName)) pids.Add((uint)p.Id);
        if (pids.Count == 0) return null;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            var c = new StringBuilder(64); GetClassNameW(h, c, 64);
            if (c.ToString() == "App") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;
        RECT r; if (!GetClientRect(found, out r)) return null;
        var pt = new POINT(); ClientToScreen(found, ref pt);
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        if (w < 640 || hh < 360) return null;
        return new int[] { pt.X, pt.Y, w, hh };
    }

    // AUFNEHMEN UND KODIEREN GETRENNT.
    //
    // Nacheinander kostete ein Frame bei 4K ~92 ms -- genau an der Grenze, ab der
    // die laufende Liste zwischen zwei Bildern Zeilen ueberspringt. Hier nimmt ein
    // Faden auf (StretchBlt direkt in den Bezugsrahmen), mehrere kodieren PNG.
    //
    // GEMESSEN am 2026-09-24, 3 s je Lauf: 4K 10,9 fps nacheinander -> 15,6 fps so.
    // Die Gegenprobe -- volle Kopie aufnehmen, Verkleinern in die Arbeiter
    // verlegen -- war LANGSAMER (4K 10,8, 8K 5,3): der teure Teil ist das Lesen
    // vom Bildschirm selbst, und StretchBlt liest verkleinert billiger als BitBlt
    // in voller Groesse.
    public static int Run(string outDir, int seconds, int intervalMs, int maxFrames,
                          int dw, int dh, int sx, int sy, int sw, int sh) {
        var queue = new System.Collections.Concurrent.BlockingCollection<Tuple<Bitmap, string>>(48);
        int workers = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2));
        var tasks = new System.Threading.Tasks.Task[workers];
        for (int i = 0; i < workers; i++) {
            tasks[i] = System.Threading.Tasks.Task.Run(() => {
                foreach (var item in queue.GetConsumingEnumerable()) {
                    try { item.Item1.Save(item.Item2, ImageFormat.Png); }
                    finally { item.Item1.Dispose(); }
                }
            });
        }
        var deadline = DateTime.Now.AddSeconds(seconds);
        var clock = Stopwatch.StartNew();
        int index = 0;
        try {
            while (DateTime.Now < deadline && index < maxFrames) {
                var tick = clock.ElapsedMilliseconds;
                var bmp = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp)) { Grab(g, dw, dh, sx, sy, sw, sh); }
                var name = System.IO.Path.Combine(outDir,
                    string.Format("f{0:d5}_{1}.png", index, DateTime.Now.ToString("HHmmssfff")));
                queue.Add(Tuple.Create(bmp, name));
                index++;
                var wait = intervalMs - (clock.ElapsedMilliseconds - tick);
                if (wait > 1) System.Threading.Thread.Sleep((int)wait);
            }
        } finally {
            queue.CompleteAdding();
            System.Threading.Tasks.Task.WaitAll(tasks);
        }
        return index;
    }

    public static void Grab(Graphics g, int dw, int dh, int sx, int sy, int sw, int sh) {
        IntPtr dst = g.GetHdc(); IntPtr src = GetDC(IntPtr.Zero);
        try {
            SetStretchBltMode(dst, 4); SetBrushOrgEx(dst, 0, 0, IntPtr.Zero);
            if (!StretchBlt(dst, 0, 0, dw, dh, src, sx, sy, sw, sh, 0x00CC0020))
                throw new InvalidOperationException("StretchBlt failed");
        } finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
    }
}
'@
[void][FrameGrab]::SetProcessDPIAware()

$flaeche = if ($CaptureArea) {
    $teile = @($CaptureArea -split '\s*,\s*' | ForEach-Object { [int]$_ })
    if ($teile.Count -ne 4) { throw "-CaptureArea erwartet 'x,y,breite,hoehe'" }
    $teile
} else { [FrameGrab]::GameArea("forzahorizon6") }
if (-not $flaeche) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $flaeche = @($b.X, $b.Y, $b.Width, $b.Height)
}
# Mittiger 16:9-Teil (vermessen ist 16:9; anderes Verhaeltnis: Annahme).
$fw = $flaeche[2]; $fh = $flaeche[3]
if ($fw * 9 -gt $fh * 16) { $fw = [int][math]::Round($fh * 16 / 9) } elseif ($fw * 9 -lt $fh * 16) { $fh = [int][math]::Round($fw * 9 / 16) }
if ($fw -lt 1280 -or $fh -lt 720) { throw "Spielflaeche ${fw}x${fh} ist kleiner als 720p." }
$fx = $flaeche[0] + [int][math]::Floor(($flaeche[2] - $fw) / 2)
$fy = $flaeche[1] + [int][math]::Floor(($flaeche[3] - $fh) / 2)
$s = $fw / 1920.0
$srcX = $fx + [int][math]::Round($X * $s)
$srcY = $fy + [int][math]::Round($Y * $s)
$srcW = [int][math]::Round($Width * $s)
$srcH = [int][math]::Round($Height * $s)
Write-Host ("[frames] game area {0}x{1} at {2},{3}; table {4}x{5} at {6},{7} -> {8}x{9} reference" -f `
    $flaeche[2], $flaeche[3], $flaeche[0], $flaeche[1], $srcW, $srcH, $srcX, $srcY, $Width, $Height)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$started = Get-Date
$index = [FrameGrab]::Run($OutDir, $Seconds, $IntervalMs, $MaxFrames,
                          $Width, $Height, $srcX, $srcY, $srcW, $srcH)
$elapsed = ((Get-Date) - $started).TotalSeconds
$fps = $index / [Math]::Max(0.001, $elapsed)
Write-Host ("[frames] {0} frames in {1:N1}s -> {2:N1} fps, {3}" -f $index, $elapsed, $fps, $OutDir)
# Unter ~11 Bildern je Sekunde ueberspringt die laufende Liste Zeilen zwischen zwei
# Aufnahmen (11 sichtbare Zeilen, ~116 Zeilen/s). Laut sagen statt Loecher liefern.
if ($Seconds -ge 2 -and $fps -lt 11) {
    Write-Host (("[frames] WARNING {0:N1} fps is below the ~11 fps the scrolling list needs; " +
                 "rows can be skipped between frames. Use a smaller game window or resolution.") -f $fps)
}
