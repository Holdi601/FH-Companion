# Die Tune-Menues des Spiels einmal mitschneiden -- als Vorlage fuer das automatische
# Loeschen ungenutzter Tunes ueber die Spieloberflaeche.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture_tune_ui.ps1 -Minuten 25
#
# Nimmt NUR auf, solange forzahorizon6 das Fenster vorne ist:
#   - ein Bild, sobald sich der Schirm sichtbar aendert (hoechstens alle 0,7 s), auf
#     1920x1080 verkleinert;
#   - welche Menuetasten gedrueckt wurden, mit Zeit -- Pfeile, Enter, Esc, Leertaste,
#     Ruecktaste, Tab, Entf und die Tasten, die das Spiel unten einblendet (X, Y, P, L).
#     Andere Buchstaben NICHT: dort landet Chattext.
param([int]$Minuten = 25, [string]$Ziel = "")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class Vorne {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    public static string Prozess() {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }
}
"@
if (-not $Ziel) { $Ziel = Join-Path (Split-Path $PSScriptRoot -Parent) "data\runtime\tune_ui_capture\$(Get-Date -Format yyyyMMdd_HHmmss)" }
New-Item -ItemType Directory -Force $Ziel | Out-Null
$log = Join-Path $Ziel "keys.log"
$tasten = @{ 0x25="LEFT"; 0x26="UP"; 0x27="RIGHT"; 0x28="DOWN"; 0x0D="ENTER"; 0x1B="ESC"; 0x20="SPACE";
             0x08="BACKSPACE"; 0x09="TAB"; 0x2E="DELETE"; 0x21="PAGEUP"; 0x22="PAGEDOWN";
             0x58="X"; 0x59="Y"; 0x50="P"; 0x4C="L" }
$gedrueckt = @{}
$schirm = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$voll = New-Object System.Drawing.Bitmap $schirm.Width, $schirm.Height
$klein = New-Object System.Drawing.Bitmap 1920, 1080
$gv = [System.Drawing.Graphics]::FromImage($voll)
$gk = [System.Drawing.Graphics]::FromImage($klein)
$gk.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
$vorher = $null
$ende = (Get-Date).AddMinutes($Minuten)
$naechstesBild = Get-Date
$bilder = 0
"start $(Get-Date -Format o), screen $($schirm.Width)x$($schirm.Height)" | Out-File $log -Encoding utf8
while ((Get-Date) -lt $ende -and $bilder -lt 900) {
    $vorn = [Vorne]::Prozess() -eq "forzahorizon6"
    foreach ($vk in $tasten.Keys) {
        $unten = ([Vorne]::GetAsyncKeyState($vk) -band 0x8000) -ne 0
        if ($unten -and -not $gedrueckt[$vk] -and $vorn) {
            "$(Get-Date -Format HH:mm:ss.fff) $($tasten[$vk])" | Out-File $log -Append -Encoding utf8
        }
        $gedrueckt[$vk] = $unten
    }
    if ($vorn -and (Get-Date) -ge $naechstesBild) {
        $naechstesBild = (Get-Date).AddMilliseconds(700)
        try {
            $gv.CopyFromScreen($schirm.Location, [System.Drawing.Point]::Empty, $schirm.Size)
            $gk.DrawImage($voll, 0, 0, 1920, 1080)
            # Kleiner Fingerabdruck: 48x27 Grauwerte.
            $fp = New-Object byte[] (48 * 27)
            for ($y = 0; $y -lt 27; $y++) { for ($x = 0; $x -lt 48; $x++) {
                $c = $klein.GetPixel($x * 40 + 20, $y * 40 + 20); $fp[$y * 48 + $x] = [byte](($c.R + $c.G + $c.B) / 3) } }
            $anders = $true
            if ($vorher) {
                $summe = 0; for ($i = 0; $i -lt $fp.Length; $i++) { $summe += [Math]::Abs($fp[$i] - $vorher[$i]) }
                $anders = ($summe / $fp.Length) -ge 3
            }
            if ($anders) {
                $name = "$(Get-Date -Format HHmmss_fff).png"
                $klein.Save((Join-Path $Ziel $name), [System.Drawing.Imaging.ImageFormat]::Png)
                "$(Get-Date -Format HH:mm:ss.fff) FRAME $name" | Out-File $log -Append -Encoding utf8
                $vorher = $fp
                $bilder++
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 40
}
"ende $(Get-Date -Format o), $bilder Bilder" | Out-File $log -Append -Encoding utf8
Write-Output "$bilder Bilder in $Ziel"
