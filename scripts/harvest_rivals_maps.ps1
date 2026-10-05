[CmdletBinding()]
param(
    [string[]] $Categories = @("Road Racing", "Cross-Country", "Street Racing",
                               "Dirt Racing", "Drag Racing", "Touge"),
    [int] $SettleMs = 900
)

<#
.SYNOPSIS
    Die Karte JEDER Rivalen-Strecke aufnehmen -- auf diesem Rechner, ohne VM.

.DESCRIPTION
    ## Warum auf dem Wirt und nicht in der VM

    Die VM mit ihrer GPU-Partitionierung hat diesen Rechner am 2026-09-18,
    2026-09-19 und 2026-09-24 eingefroren, jedes Mal kurz nachdem sie gestartet
    worden war. Forza laeuft hier aber ohnehin -- es ist das Spiel des Nutzers.
    forza_navigator.ps1 liest den Schirm selbst (Get-ScreenFrame) und braucht
    keine VM; dasselbe tut schon das Beitrags-Werkzeug.

    ## Was geschieht

      1. Je Kategorie faehrt der Navigator die Streckenliste an und BESTAETIGT die
         Kategorie am Bild. Stimmt sie nicht, bricht er ab -- lieber keine Karten
         als Karten unter falschem Namen.
      2. -EnumerateMaps oeffnet jede Strecke, nimmt den Kartenschirm auf (magenta
         Linie, Titel, Laenge) und geht zurueck.
      3. route_shapes.py erntet die Linien und liest die Titel mit der Host-OCR.
      4. Am Ende steht, wie viele Strecken des Katalogs eine Karte haben.

    ## Voraussetzung

    Forza laeuft, steht im Hauptmenue und hat den Vordergrund. Der Rechner gehoert
    dem Lauf: nicht klicken, nicht tippen.

.EXAMPLE
    ./scripts/harvest_rivals_maps.ps1
    ./scripts/harvest_rivals_maps.ps1 -Categories "Road Racing"
#>

$ErrorActionPreference = "Stop"

# `powershell -File ... -Categories "A","B"` uebergibt EINEN String "A,B" --
# -File zerlegt keine Listen. Am 2026-09-24 wurden so alle fuenf Kategorien als
# "nicht im Katalog" uebersprungen. Also selbst an den Kommas trennen.
$Categories = @($Categories | ForEach-Object { $_ -split '\s*,\s*' } | Where-Object { $_ })

$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
function Write-Harvest { param([string] $Message) Write-Host "[maps] $Message" }

# Die Streckenzahl je Kategorie aus dem Katalog -- nicht geschaetzt, sonst laeuft
# das Karussell ueber seinen Rundlauf hinaus und nimmt Strecken doppelt auf.
$katalog = Get-Content (Join-Path $workspace "config\fh6_board_catalogue.json") -Raw |
    ConvertFrom-Json
function Get-RouteCount {
    param([string] $Category)
    $eintrag = $katalog.tracks.$Category
    if (-not $eintrag) { return 0 }
    return @($eintrag.verified).Count
}

$forza = Get-Process forzahorizon6 -ErrorAction SilentlyContinue
if (-not $forza) {
    throw "Forza laeuft nicht. Spiel starten, ins Hauptmenue, dann erneut aufrufen."
}

# Eine verlangte Kategorie, die der Katalog nicht kennt, ist ein Aufruffehler --
# kein Grund, still weiterzumachen und am Ende "fertig" zu melden.
$unbekannt = @($Categories | Where-Object { (Get-RouteCount -Category $_) -le 0 })
if ($unbekannt.Count -gt 0) {
    throw ("Nicht im Katalog: " + ($unbekannt -join ", ") + ". Bekannt: " +
           (($katalog.tracks.PSObject.Properties.Name) -join ", "))
}

$outRoot = Join-Path $workspace "data\runtime\navigation"
$gesamt = 0
foreach ($kategorie in $Categories) {
    $anzahl = Get-RouteCount -Category $kategorie
    if ($anzahl -le 0) {
        Write-Harvest "${kategorie}: keine Strecken im Katalog, uebersprungen"
        continue
    }
    Write-Harvest "${kategorie}: $anzahl Strecke(n)"
    $gesamt += $anzahl
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $workspace "scripts\forza_navigator.ps1") `
        -RivalsMode $kategorie -PerformanceClass "A" -RouteIndex 0 `
        -RouteCount $anzahl -EnumerateMaps -KeepFrames `
        -OutputRoot $outRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Harvest "${kategorie}: der Navigator meldete Code $LASTEXITCODE -- weiter mit der naechsten"
    }
}

Write-Harvest "ernte die Karten ..."
& python (Join-Path $workspace "scripts\route_shapes.py") --ernten
Write-Harvest "ordne sie den eigenen Kursen zu ..."
& python (Join-Path $workspace "scripts\route_shapes.py") --setzen

# ABDECKUNG: wie viele Strecken des Katalogs jetzt eine Karte haben. Ohne diese
# Zahl sieht ein halber Lauf aus wie ein ganzer.
$namen = @{}
Get-ChildItem (Join-Path $workspace "data\route_shapes") -Filter *.json -ErrorAction SilentlyContinue |
    ForEach-Object {
        $d = Get-Content $_.FullName -Raw | ConvertFrom-Json
        if ($d.name) { $namen[$d.name] = $true }
    }
# Gegen den GANZEN Katalog, je Kategorie -- nicht nur gegen die Kategorien dieses
# Aufrufs. Sonst meldet ein Nachlauf fuer eine Kategorie "23 von 23", waehrend
# vier andere leer sind; am 2026-09-24 stand hier "24 von 0".
$alle = 0
$gedeckt = 0
foreach ($k in $katalog.tracks.PSObject.Properties.Name) {
    $soll = @($katalog.tracks.$k.verified | ForEach-Object { $_.name })
    $fehlt = @($soll | Where-Object { -not $namen.ContainsKey($_) })
    $alle += $soll.Count
    $gedeckt += $soll.Count - $fehlt.Count
    $zeile = "  {0,-14} {1,2}/{2}" -f $k, ($soll.Count - $fehlt.Count), $soll.Count
    if ($fehlt.Count -gt 0) { $zeile += "  fehlt: " + ($fehlt -join ", ") }
    Write-Harvest $zeile
}
Write-Harvest ("Karten fuer {0} von {1} Strecke(n) im Katalog" -f $gedeckt, $alle)
