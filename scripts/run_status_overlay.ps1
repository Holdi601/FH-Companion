[CmdletBinding()]
param(
    # Die Datei, die Navigator, Ernte-Skript und Bediener beschreiben.
    [string] $StatusPath = "",
    # Ohne neue Zeile so lange: das Fenster sagt es deutlich ("keine Meldung seit").
    [int] $StaleSeconds = 60,
    # Nach so vielen Minuten ohne jede Aenderung schliesst es sich selbst.
    [int] $ExitAfterIdleMinutes = 30
)

<#
.SYNOPSIS
    Zeigt ueber dem Spiel, was ein automatischer Lauf gerade tut.

.DESCRIPTION
    Waehrend eines Laufs muss Forza vorne sein, und dann sieht man sonst nichts:
    minutenlang passiert scheinbar nichts, obwohl im Hintergrund gearbeitet wird --
    oder es passiert wirklich nichts, und man merkt es nicht.

    Dieses Fenster zeigt Phase, Schritt und Alter der letzten Meldung.

    Drei Eigenschaften sind Pflicht, nicht Zier:
      * DURCHKLICKBAR und NIE AKTIV: es darf dem Spiel weder Fokus noch Mausklick
        wegnehmen -- der Navigator prueft den Fokus und schwebt mit der Maus.
      * VON JEDER AUFNAHME AUSGENOMMEN (WDA_EXCLUDEFROMCAPTURE): der Navigator liest
        den Schirm; ein Fenster, das in seine Bilder geraet, liest er mit. Genau das
        ist mit dem Overlay-Panel schon einmal passiert.
      * NUR EINES: ein zweiter Start beendet sich sofort.

    Beschrieben wird die Datei mit scripts/run_status.ps1.
#>

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (-not $StatusPath) { $StatusPath = Join-Path $workspace "data\runtime\run_status.json" }

$mutex = New-Object System.Threading.Mutex($false, "Local\ForzaRunStatusOverlay")
if (-not $mutex.WaitOne(0)) { exit 0 }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public class StatusForm : Form {
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80,
              WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    public bool Excluded;
    public StatusForm() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 22, 28);
        Opacity = 0.86;
        DoubleBuffered = true;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try { Excluded = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { Excluded = false; }
    }
}
'@

[void][StatusForm]::SetProcessDPIAware()
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
# Schrift und Groesse nach der Schirmhoehe: 720p bis 16K sieht gleich aus.
$unit = [math]::Max(12, [int]($screen.Height / 72))
$form = New-Object StatusForm
$form.Width = [int]($screen.Width * 0.42)
$form.Height = [int]($unit * 4.6)
# Oben mittig, knapp unter dem Rand: dort liegen in den Rivals-Menues nur
# Auto- und Spielername, nichts, worauf der Navigator zeigt.
$form.Left = $screen.X + [int](($screen.Width - $form.Width) / 2)
$form.Top = $screen.Y + [int]($screen.Height * 0.075)

$titleFont = New-Object System.Drawing.Font("Segoe UI Semibold", ($unit * 0.72), [System.Drawing.GraphicsUnit]::Pixel)
$lineFont = New-Object System.Drawing.Font("Segoe UI", ($unit * 0.62), [System.Drawing.GraphicsUnit]::Pixel)
$script:status = @{ phase = "waiting for status"; detail = ""; updated = $null; state = "" }
$script:lastChange = Get-Date
$script:lastRaw = ""

$form.add_Paint({
    param($s, $e)
    $g = $e.Graphics
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $alter = if ($script:status.updated) { [int]((Get-Date) - [datetime]$script:status.updated).TotalSeconds } else { -1 }
    $farbe = switch ($script:status.state) {
        "error" { [System.Drawing.Color]::FromArgb(255, 110, 100) }
        "done" { [System.Drawing.Color]::FromArgb(120, 220, 140) }
        "idle" { [System.Drawing.Color]::FromArgb(150, 190, 255) }
        default { [System.Drawing.Color]::FromArgb(202, 255, 2) }
    }
    $pad = [int]($unit * 0.5)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($farbe)), 0, 0, [int]($unit * 0.25), $form.Height)
    $g.DrawString($script:status.phase, $titleFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $pad + $unit * 0.2, $pad * 0.6)
    $detail = $script:status.detail
    $g.DrawString($detail, $lineFont, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 220, 228))),
                  (New-Object System.Drawing.RectangleF(($pad + $unit * 0.2), ($unit * 1.55), ($form.Width - 2 * $pad), ($unit * 1.8))))
    $fuss = if ($alter -lt 0) { "no status yet" }
            elseif ($alter -gt $StaleSeconds) { "NO UPDATE FOR $alter s -- the run may be stuck" }
            else { "updated $alter s ago" }
    if (-not $form.Excluded) { $fuss += "   (capture exclusion unavailable: may show in the navigator's frames)" }
    $fussFarbe = if ($alter -gt $StaleSeconds) { [System.Drawing.Color]::FromArgb(255, 160, 90) } else { [System.Drawing.Color]::FromArgb(140, 150, 165) }
    $g.DrawString($fuss, $lineFont, (New-Object System.Drawing.SolidBrush($fussFarbe)), $pad + $unit * 0.2, $form.Height - $unit * 1.05)
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.add_Tick({
    try {
        if (Test-Path -LiteralPath $StatusPath) {
            $raw = [IO.File]::ReadAllText($StatusPath)
            if ($raw -ne $script:lastRaw) {
                $d = $raw | ConvertFrom-Json
                $script:status = @{ phase = [string]$d.phase; detail = [string]$d.detail; updated = $d.updated; state = [string]$d.state }
                $script:lastRaw = $raw
                $script:lastChange = Get-Date
            }
        }
    } catch {
        # Halb geschriebene Datei: beim naechsten Takt wieder.
    }
    if (((Get-Date) - $script:lastChange).TotalMinutes -gt $ExitAfterIdleMinutes) { $form.Close() }
    $form.Invalidate()
})
$timer.Start()
[System.Windows.Forms.Application]::Run($form)
$mutex.ReleaseMutex()
