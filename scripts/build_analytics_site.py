"""Build the Rivals analytics page from the sweep output, in one command.

Runs the dataset build in-process and writes one self-contained HTML file with the
laps embedded, so there is no second step and no fetch at runtime: point a browser at
the file and it works. Re-running picks up whatever boards exist at that moment,
which is what makes a fresh scan appear on the site without any further processing.

    python scripts/build_analytics_site.py
    python scripts/build_analytics_site.py --out C:\\somewhere\\rivals.html

The page computes both leaderboards live from the laps, because every filter (only
laps without TCS, only invalid laps, manual gearbox only) changes which lap
represents a car, which changes the order, which changes the points.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

import build_analytics_dataset as dataset

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import local_settings  # noqa: E402

PAGE = r"""<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__SITE_TITLE__ &middot; Rivals Car Ratings</title>
<link rel="icon" href="/favicon.ico" sizes="16x16 32x32 48x48">
<link rel="apple-touch-icon" href="/brand/icon-180.png">
<style>__FONTS_CSS__</style>
<style>
/* GROSSE SCHIRME. Die Seite ist in CSS-Pixeln fuer ~1080p-1440p gebaut (Spalte
   1240px, Schrift 12-15px). Auf einem 4K-Schirm mit 100 % Skalierung war sie ein
   schmaler Streifen winziger Schrift, auf 8K/16K unlesbar. zoom skaliert Layout,
   Schrift, Tabellen und Diagramme gemeinsam; die Stufen haengen an der Breite des
   Fensters in CSS-Pixeln, also greifen sie nicht, wenn Windows ohnehin skaliert. */
@media (min-width: 2200px) { html { zoom: 1.5; } }
@media (min-width: 3000px) { html { zoom: 2; } }
@media (min-width: 4400px) { html { zoom: 3; } }
@media (min-width: 6000px) { html { zoom: 4; } }
@media (min-width: 9000px) { html { zoom: 6; } }
@media (min-width: 12000px) { html { zoom: 8; } }

:root {
  --ground: #f5f6f8;
  --surface: #ffffff;
  --raised: #fafbfc;
  --border: #e3e7ed;
  --border-strong: #cfd6e0;
  --ink: #0f1520;
  --ink-soft: #3d4759;
  --muted: #59647a;
  --bar: #0891b2;
  --bar-soft: #d8ecf3;
  --link: #0e7490;
  --warn: #b45309;
  --warn-bg: #fdf3e3;
  --note: #9f1239;
  --shadow: 0 1px 2px rgba(15,21,32,.05), 0 10px 26px rgba(15,21,32,.045);
  --on-ink: #ffffff;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --ground: #0b1017; --surface: #131a23; --raised: #182029;
    --border: #232e3b; --border-strong: #35424f;
    --ink: #e7ecf3; --ink-soft: #c3ccd9; --muted: #93a2b5;
    --bar: #22b8e6; --bar-soft: #17323f; --link: #7dd3fc;
    --warn: #f0a22e; --warn-bg: #2a2113; --note: #f2637e;
    --shadow: 0 1px 2px rgba(0,0,0,.5), 0 12px 32px rgba(0,0,0,.35);
    --on-ink: #08111a;
  }
}
:root[data-theme="dark"] {
  --ground: #0b1017; --surface: #131a23; --raised: #182029;
  --border: #232e3b; --border-strong: #35424f;
  --ink: #e7ecf3; --ink-soft: #c3ccd9; --muted: #93a2b5;
  --bar: #22b8e6; --bar-soft: #17323f; --link: #7dd3fc;
  --warn: #f0a22e; --warn-bg: #2a2113; --note: #f2637e;
  --shadow: 0 1px 2px rgba(0,0,0,.5), 0 12px 32px rgba(0,0,0,.35);
  --on-ink: #08111a;
}

* { box-sizing: border-box; }
body {
  margin: 0; background: var(--ground); color: var(--ink);
  font-family: "IBM Plex Sans", ui-sans-serif, system-ui, sans-serif;
  font-size: 15px; line-height: 1.5; -webkit-font-smoothing: antialiased;
}
.wrap { max-width: 1240px; margin: 0 auto; padding: 26px 20px 80px; }
.label {
  font-family: "Barlow Condensed", "IBM Plex Sans", sans-serif;
  font-weight: 600; text-transform: uppercase; letter-spacing: .09em;
  font-size: 12.5px; color: var(--muted);
}
.mono { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; }

header.top { display: flex; flex-direction: column; gap: 5px; }
h1 {
  font-family: "Barlow Condensed", "IBM Plex Sans", sans-serif; font-weight: 700;
  font-size: clamp(29px, 4.2vw, 44px); line-height: 1.02; letter-spacing: -.01em;
  margin: 0; text-wrap: balance;
}
h1 .thin { color: var(--muted); font-weight: 500; }
/* Das Logo neben dem Titel: helle und dunkle Fassung, je nach Farbschema. */
h1.marke { display: flex; align-items: center; gap: 16px; }
.logo { width: clamp(48px, 6vw, 68px); height: auto; flex: none; }
.logo-dunkel { display: none; }
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) .logo-hell { display: none; }
  :root:not([data-theme="light"]) .logo-dunkel { display: block; }
}
:root[data-theme="dark"] .logo-hell { display: none; }
:root[data-theme="dark"] .logo-dunkel { display: block; }
header.top p { margin: 0; color: var(--muted); max-width: 76ch; }
/* Die Wege zu Werkzeug, Upload und Verwaltung. Sie standen serverseitig laengst
   bereit, nur zeigte nichts darauf -- und was man nicht sieht, gibt es nicht. */
header.top p.ways { margin-top: 8px; font-size: 13px; }
header.top p.ways a { color: var(--accent, #cbff2f); text-decoration: none;
                      border-bottom: 1px solid rgba(203,255,47,.35); }
header.top p.ways a:hover { border-bottom-color: var(--accent, #cbff2f); }
header.top p.ways .sep { color: var(--muted); margin: 0 8px; }
/* Ein Weg bricht nicht mitten im Namen um ("Source / code on GitHub"). */
header.top p.ways a { white-space: nowrap; }
/* Der Sprung zur Regel und die Links unten: in der Linkfarbe der Seite, nicht im
   Browser-Blau, das in beiden Farbschemata fremd wirkt. */
header.top p a.jump, footer.notes a { color: var(--link); text-decoration: none;
  border-bottom: 1px solid transparent; }
header.top p a.jump:hover, footer.notes a:hover { border-bottom-color: var(--link); }

.tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin: 18px 0 20px; }
.tile { background: var(--surface); border: 1px solid var(--border); border-radius: 10px; padding: 11px 13px; }
.tile .n { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; font-size: 22px; font-weight: 500; display: block; letter-spacing: -.02em; }
.tile .sub { font-size: 12.5px; color: var(--muted); }

.panel { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; box-shadow: var(--shadow); }
.filters { padding: 14px 16px 16px; display: flex; flex-direction: column; gap: 11px; }
.frow { display: flex; flex-wrap: wrap; align-items: center; gap: 7px; }
.frow > .label { min-width: 124px; flex: 0 0 auto; }
.frow.sub { padding-left: 124px; }

.chip {
  font: inherit; font-size: 13.5px; padding: 4px 11px; border-radius: 999px;
  border: 1px solid var(--border-strong); background: var(--raised);
  color: var(--ink-soft); cursor: pointer;
  transition: background .12s, color .12s, border-color .12s;
}
.chip:hover { border-color: var(--bar); color: var(--ink); }
.chip[aria-pressed="true"] { background: var(--ink); border-color: var(--ink); color: var(--on-ink); font-weight: 500; }
.chip.k { font-family: "IBM Plex Mono", ui-monospace, monospace; }
.chip.small { font-size: 12.5px; padding: 3px 9px; }
.chip:focus-visible, input:focus-visible, .sortbtn:focus-visible, .tab:focus-visible { outline: 2px solid var(--bar); outline-offset: 2px; }

.tri { display: inline-flex; border: 1px solid var(--border-strong); border-radius: 999px; overflow: hidden; background: var(--raised); }
.tri button { font: inherit; font-size: 12.5px; padding: 3px 10px; border: 0; background: none; color: var(--muted); cursor: pointer; }
.tri button[aria-pressed="true"] { background: var(--ink); color: var(--on-ink); font-weight: 500; }
.trigroup { display: inline-flex; align-items: center; gap: 6px; margin-right: 4px; }
.trigroup > span { font-size: 12.5px; color: var(--muted); }

/* Two-handle year range. Two native range inputs share one track: the inputs
   themselves ignore the pointer so the lower one cannot swallow clicks meant for the
   upper, and only the thumbs take it back. Native inputs keep keyboard support. */
.range { display: flex; align-items: center; gap: 12px; flex: 1 1 320px; min-width: 260px; }
.range .rail { position: relative; flex: 1; height: 22px; }
.range .rail .bg, .range .rail .fill {
  position: absolute; top: 9px; height: 4px; border-radius: 2px;
}
.range .rail .bg { left: 0; right: 0; background: var(--bar-soft); }
.range .rail .fill { background: var(--bar); }
.range input[type="range"] {
  position: absolute; top: 0; left: 0; width: 100%; height: 22px; margin: 0;
  background: none; -webkit-appearance: none; appearance: none; pointer-events: none;
}
.range input[type="range"]::-webkit-slider-thumb {
  -webkit-appearance: none; appearance: none; pointer-events: auto;
  width: 16px; height: 16px; border-radius: 50%;
  background: var(--surface); border: 2px solid var(--bar); cursor: grab;
  box-shadow: 0 1px 2px rgba(0,0,0,.25);
}
.range input[type="range"]::-moz-range-thumb {
  pointer-events: auto; width: 14px; height: 14px; border-radius: 50%;
  background: var(--surface); border: 2px solid var(--bar); cursor: grab;
}
.range input[type="range"]:focus-visible::-webkit-slider-thumb { outline: 2px solid var(--bar); outline-offset: 2px; }
.range .readout {
  font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums;
  font-size: 13px; color: var(--ink-soft); min-width: 104px; text-align: right;
}

input[type="search"] {
  font: inherit; padding: 6px 11px; min-width: 240px; flex: 1 1 240px;
  border-radius: 8px; border: 1px solid var(--border-strong);
  background: var(--raised); color: var(--ink);
}
input[type="search"]::placeholder { color: var(--muted); }

.tabs { display: flex; gap: 4px; margin: 20px 0 0; flex-wrap: wrap; }
.tab {
  font-family: "Barlow Condensed", "IBM Plex Sans", sans-serif; font-weight: 600;
  text-transform: uppercase; letter-spacing: .07em; font-size: 14px;
  padding: 8px 15px; border: 1px solid var(--border); border-bottom: 0;
  border-radius: 9px 9px 0 0; background: var(--raised); color: var(--muted); cursor: pointer;
}
.tab[aria-selected="true"] { background: var(--surface); color: var(--ink); border-color: var(--border); box-shadow: 0 -1px 0 var(--bar) inset; }
.view { border-radius: 0 12px 12px 12px; }

.context { display: flex; flex-wrap: wrap; gap: 6px 20px; align-items: baseline; padding: 11px 16px; border-bottom: 1px solid var(--border); color: var(--muted); font-size: 13px; }
.context b { color: var(--ink); font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; font-weight: 500; }
.rule { padding: 11px 16px; border-bottom: 1px solid var(--border); color: var(--muted); font-size: 13px; }
.rule b { color: var(--ink-soft); }

.tablewrap { overflow-x: auto; padding: 0 4px 4px; }
table { border-collapse: collapse; width: 100%; min-width: 720px; }
thead th { position: sticky; top: 0; background: var(--surface); border-bottom: 1px solid var(--border-strong); padding: 0; text-align: left; z-index: 2; }
.sortbtn {
  font-family: "Barlow Condensed", "IBM Plex Sans", sans-serif; font-weight: 600;
  text-transform: uppercase; letter-spacing: .08em; font-size: 12.5px;
  color: var(--muted); background: none; border: 0; padding: 9px 10px; width: 100%;
  text-align: inherit; cursor: pointer;
}
.sortbtn[data-active="1"] { color: var(--ink); }
.sortbtn.static { cursor: default; }
th.num, td.num { text-align: right; }
th.num .sortbtn { text-align: right; }
tbody td { border-bottom: 1px solid var(--border); padding: 6px 10px; vertical-align: middle; }
tbody tr:hover td { background: var(--raised); }
td.num, td.m { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; }
td.pos { color: var(--muted); width: 44px; font-family: "IBM Plex Mono", ui-monospace, monospace; }
td.car { font-weight: 500; }
td.car .cid { color: var(--muted); font-weight: 400; font-size: 12.5px; }
td.car button { font: inherit; color: var(--link); background: none; border: 0; padding: 0; cursor: pointer; text-align: left; }
td.car button:hover { text-decoration: underline; }

.gap { display: flex; align-items: center; gap: 8px; min-width: 132px; }
.gap .track { position: relative; flex: 1; height: 6px; border-radius: 3px; background: var(--bar-soft); overflow: hidden; }
.gap .fill { position: absolute; inset: 0 auto 0 0; border-radius: 3px; background: var(--bar); }
.gap .v { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; font-size: 12.5px; color: var(--muted); min-width: 62px; text-align: right; }

.flag { display: inline-flex; align-items: center; gap: 4px; font-size: 11.5px; padding: 0 7px; border-radius: 999px; border: 1px solid currentColor; color: var(--warn); background: var(--warn-bg); white-space: nowrap; }
.sub2 { font-size: 12.5px; color: var(--muted); }
.empty { padding: 32px 16px; text-align: center; color: var(--muted); }
.empty b { color: var(--ink); }

.cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(272px, 1fr)); gap: 12px; padding: 14px 16px 16px; }
.card { border: 1px solid var(--border); border-radius: 10px; padding: 12px 13px; background: var(--raised); }
.card h3 { font-family: "Barlow Condensed", "IBM Plex Sans", sans-serif; text-transform: uppercase; letter-spacing: .08em; font-size: 14px; margin: 0 0 8px; display: flex; align-items: baseline; gap: 8px; }
.card h3 .kbadge { font-family: "IBM Plex Mono", ui-monospace, monospace; font-size: 13px; padding: 1px 8px; border-radius: 999px; background: var(--ink); color: var(--on-ink); letter-spacing: 0; }
.kv { display: flex; justify-content: space-between; gap: 12px; font-size: 13.5px; padding: 2px 0; }
.kv span:first-child { color: var(--muted); }
.kv span:last-child { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; }
.trackline { display: flex; justify-content: space-between; gap: 10px; font-size: 12.5px; padding: 2px 0; border-top: 1px dashed var(--border); }
.trackline span:last-child { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; color: var(--ink-soft); }
.trackline.sub-est span:last-child { color: var(--warn); }

footer.notes { margin-top: 24px; padding-top: 16px; border-top: 1px solid var(--border); color: var(--muted); font-size: 13.5px; display: flex; flex-direction: column; gap: 9px; }
footer.notes h2 { font-family: "Barlow Condensed", "IBM Plex Sans", sans-serif; text-transform: uppercase; letter-spacing: .09em; font-size: 13px; color: var(--ink); margin: 0; }
footer.notes p { margin: 0; max-width: 84ch; }
footer.notes ol.steps { margin: 0; padding-left: 22px; max-width: 82ch; display: flex; flex-direction: column; gap: 5px; }
[dir="rtl"] footer.notes ol.steps { padding-left: 0; padding-right: 22px; }
footer.notes .endnote { margin-top: 10px; }
.caveat { color: var(--note); }
@media (prefers-reduced-motion: reduce) { * { transition: none !important; } }
@media (max-width: 680px) {
  .frow > .label { min-width: 100%; }
  .frow.sub { padding-left: 0; }
}
/* time spread: one hue for the bars, annotations for the percentile cuts. No second
   axis -- the question is where a boundary sits, not how two scales compare. */
.spreadsvg { width: 100%; height: auto; display: block; margin: 10px 0 4px; }
.spreadbar { fill: var(--bar); opacity: .55; }
.spreadcut { stroke: var(--ink-soft); stroke-width: 1; stroke-dasharray: 3 3; }
.spreadmid { stroke: var(--ink); stroke-width: 1.5; }
.spreadmine { stroke: var(--note); stroke-width: 2; }
.spreadlabel { fill: var(--muted); font-size: 9px; font-family: inherit; }
.spreadaxis { fill: var(--muted); font-size: 9.5px;
  font-family: "IBM Plex Mono", ui-monospace, monospace; }
.cut { display: inline-block; margin-right: 14px; font-variant-numeric: tabular-nums; }
.mine { margin-top: 6px; padding: 7px 10px; border-radius: 6px;
  background: var(--bar-soft); color: var(--ink); font-variant-numeric: tabular-nums; }
h3 { font-size: 15px; margin: 22px 0 8px; color: var(--ink); }
</style>

<div class="wrap">
  <header class="top">
    <span class="label">Forza Horizon 6 &middot; Rivals &middot; read straight out of game memory</span>
    <h1 class="marke"><img class="logo logo-hell" src="/brand/logo-light.png" alt="" width="68" height="68"><img class="logo logo-dunkel" src="/brand/logo-dark.png" alt="" width="68" height="68"><span>__SITE_TITLE__ <span class="thin">Rivals car ratings by performance class</span></span></h1>
    <p>Two rankings over the same laps: points by finishing position per track, and the sum of the chosen lap times. Every filter applies <em>before</em> the lap is chosen, so it decides which lap represents a car &mdash; and with it the whole order. <a class="jump" href="#lap-rule">How the lap for each car is chosen &darr;</a></p>
    <!-- Die Wege, die es serverseitig schon gab und auf die nichts zeigte: die
         App, das Hochladen fremder Laeufe und die Verwaltung. Das Scan-Werkzeug
         steht hier NICHT mehr: es haengt seit dem Passwort-Tor an der Seite zum
         Mitmachen, und ein Knopf, der jedem eine 401 zeigt, ist kein Weg. -->
    <p class="ways">
      <a href="/app">FH Companion &mdash; the app for these ratings</a>
      <span class="sep">&middot;</span>
      <a href="/contribute">Contribute &mdash; scan and share</a>
      <span class="sep">&middot;</span>
      <a href="/admin">Admin</a>__SOURCE_WAY__
    </p>
  </header>

  <div class="tiles" id="tiles"></div>

  <section class="panel">
    <div class="filters">
      <div class="frow"><span class="label">Race category</span><div class="frow" id="f-cat"></div></div>
      <div class="frow"><span class="label">Performance class</span><div class="frow" id="f-cls"></div></div>
      <div class="frow"><span class="label">Tracks</span><div class="frow" id="f-trk"></div></div>
      <div class="frow"><span class="label">Lap</span><div class="frow" id="f-clean"></div></div>
      <div class="frow"><span class="label">Gearbox</span><div class="frow" id="f-gear"></div></div>
      <div class="frow"><span class="label">Assists</span><div class="frow" id="f-assists"></div></div>
      <div class="frow"><span class="label">Tune</span><div class="frow" id="f-tune"></div></div>
      <div class="frow"><span class="label">Make</span><div class="frow" id="f-make"></div></div>
      <div class="frow"><span class="label">Country</span><div class="frow" id="f-country"></div></div>
      <div class="frow"><span class="label">Car type</span><div class="frow" id="f-cartype"></div></div>
      <div class="frow"><span class="label">Model year</span>
        <div class="range">
          <div class="rail">
            <div class="bg"></div><div class="fill" id="year-fill"></div>
            <input type="range" id="year-from" aria-label="Earliest model year">
            <input type="range" id="year-to" aria-label="Latest model year">
          </div>
          <span class="readout" id="year-readout"></span>
        </div>
        <button class="chip small" id="year-reset" type="button">All years</button>
      </div>
      <div class="frow"><span class="label">Car</span>
        <input type="search" id="f-car" placeholder="Name or id, e.g. Ferrari or 4221" autocomplete="off">
        <button class="chip" id="f-reset" type="button">Reset filters</button>
      </div>
    </div>
  </section>

  <div class="tabs" id="tabs"></div>
  <div class="panel view">
    <div class="context" id="context"></div>
    <div class="rule" id="rulenote"></div>
    <div id="content"></div>
  </div>

  <footer class="notes">
    <h2>How the ranking works</h2>
    <!-- DIE AUSWAHL DER RUNDE, Schritt fuer Schritt (2026-09-26 neu gefasst). Vorher
         stand hier ein einziger Satz mit der Regel -- richtig, aber ohne das, was man
         braucht, um ihn zu lesen: dass zuerst gefiltert wird, dass jeder Spieler nur
         eine Runde je Board hat, dass der Rang die Position im Spiel ist und warum
         die Regel ueberhaupt tiefer greift. Die Schritte folgen pickCarsRivals. -->
    <p id="lap-rule"><b>Which lap stands for a car.</b> Every leaderboard in the game is one
    track in one performance class. On each of them, one lap is chosen for every car, in
    this order:</p>
    <ol class="steps">
      <li><b>Filters first.</b> Only laps that pass every filter above take part &mdash;
      valid or invalid, gearbox, and each assist (ABS, TCS, STM and the rest). Everything
      below works on what is left.</li>
      <li><b>One lap per player.</b> The game keeps only each player's best lap on a board.
      A car's laps are therefore the best laps of the different players who drove it
      there.</li>
      <li><b>Where does the car's best lap sit?</b> Its best position on the board is
      looked up &mdash; the rank as the game shows it. Filters do not renumber it: a lap at
      rank 250 stays rank 250.</li>
      <li><b>The better that position, the deeper the reach.</b> Best lap in the
      <b>top 100</b>: the car's <b>5th fastest</b> lap counts. In the <b>top 1,000</b>:
      its <b>2nd fastest</b>. Anywhere else: its <b>fastest</b>.</li>
      <li><b>Too few laps.</b> If the car has fewer laps than step 4 asks for, its slowest
      one counts, and the row is marked <span class="flag">thin</span>.</li>
    </ol>
    <p><b>Why not simply the fastest lap?</b> The top of a board belongs to a handful of
    exceptional drivers, and a single one of them in a car says more about the driver than
    about the car. Reaching down to the 5th fastest lap measures what the car does for a
    strong driver, not for the single best one. Further down a board fewer players drive
    each car, so the reach gets shorter and there it is simply the fastest lap.</p>
    <p><b>Example.</b> A car with laps at ranks 37, 58, 140, 391, 802 and 1,450: its best
    lap is in the top 100, so the 5th fastest counts &mdash; the lap at rank 802. A car whose
    best lap is at rank 450 is represented by its 2nd fastest; a car whose best is at
    rank 3,200 by that lap itself. In <b>Single boards</b>, the column <b>Chosen</b> says
    which one it was (&ldquo;5th fastest&rdquo;), and <b>PI</b> shows the performance index
    the chosen lap was driven at, where the screen showed it.</p>
    <p><b>Times submitted from the app</b> come in after this choice: where one is faster
    than the lap chosen from the leaderboard for that car, track and class, it takes its
    place, marked as submitted, and the replaced leaderboard time stays visible.</p>
    <p><b>Points:</b> on each track, first place scores as many points as there are cars in that ranking, and last place scores 1. A car missing from a track scores 0 there. Highest total wins.</p>
    <p><b>Several performance classes at once:</b> the class chips take more than one.
    Each board still ranks only its own field &mdash; a D car never races an S2 car for
    points &mdash; and the totals add those per-board results up, exactly as they already
    add up across tracks. What changes is what the total <i>means</i>: a car that exists
    in two selected classes now collects from both, so the ranking rewards range as well
    as pace. Pick one class to read it the old way.</p>
    <p><b>Invalid laps:</b> a board lists every valid lap by time, then every invalid one &mdash; and those restart from a faster time. Across all 40 sweep runs there is not a single valid lap after the first invalid one, so the filter defaults to <b>valid only</b>. Switch it to &ldquo;All&rdquo; or &ldquo;invalid only&rdquo; and they appear, faster than the valid ones precisely because they never counted.</p>
    <p><b>Time sum:</b> the chosen lap times in milliseconds, added up. Where a car has no time on a track, the slowest time on that track and class stands in for it. Cars that appear on no selected track of the class drop out entirely. Lowest total wins.</p>
    <p><b>Boards too small to substitute from:</b> a stand-in is only fair if the board is deep
    enough to have a genuinely slow last car. Below <b>1,000 entries</b> it is not &mdash; the sweep
    abandoned some boards at 149 &mdash; so such a track leaves the time sum for every car rather
    than handing a bonus to the ones that never ran it. Its real times and points still count.</p>
    <p><b>Entries:</b> how often a car actually stands in the leaderboards you have selected
    &mdash; every row it holds on every selected board, added up, after every filter. A place
    in the ranking is decided by a <i>single</i> lap per board, so this column is the sample
    size behind that place: two entries make it a guess, four hundred make it settled. The
    same number appears per track in <b>Car detail</b> and per board under <b>Most contested</b>,
    where searching for one car turns the column into &ldquo;how often that car is in this
    leaderboard&rdquo;. It counts rows within the scanned depth of a board, not the whole board.</p>
    <h2>Reading these rankings with care</h2>
    <p class="caveat"><b>A car near the bottom is not &ldquo;the worst car&rdquo;.</b> A board keeps
    only one lap per player, so a driver who later goes quicker in a better car <b>overwrites</b>
    the lap they set in the slower one &mdash; it disappears from the board entirely. What survives
    for a weaker car are mostly the laps of drivers who never went on to beat them. So a low
    position usually means <i>few surviving samples, set by whoever did not improve</i>, not that
    the car is slow. The faster a car is, the more its good laps are the ones that stayed.</p>
    <p class="caveat"><b>How deep this data goes.</b> Most boards are read only to roughly the
    first <b>20,000 entries</b>, and many to far less &mdash; a full board can be far deeper.
    Boards measured on 23 Aug 2026 ranged from about 3,500 to roughly 800,000 entries, and the
    deepest ones hold well under 1&nbsp;% of their laps here. The <b>Scan status</b> tab lists,
    per board, how many laps are in these records and the deepest rank reached; note that its
    Coverage column compares those laps against the deepest rank <i>read</i>, not against the
    board&rsquo;s true length, so it reads higher than true completeness.</p>
    <h2>Data as it stands</h2>
    <p id="quality"></p>
    <p class="caveat" id="caveat"></p>
    <p class="caveat" id="submitted-note" hidden></p>

    <h2>Privacy</h2>
    <p>This is a free, non-commercial hobby site. No ads, no sales, no donations
    asked for here.__CONTACT__</p>
    <p><b>What this site does not do:</b> no cookies, no local storage, no session
    id, no analytics, no tracking pixel, and <i>no external resources at all</i>
    &mdash; the page is a single file and loads no fonts, scripts or images from
    other servers. Opening it contacts exactly one machine. Nothing is passed to
    third parties and nothing leaves the EU.</p>
    <p><b>Gamertags from the in-game leaderboards.</b> The rankings hold roughly
    1.39&nbsp;million lap times read from the public Rivals leaderboards of Forza
    Horizon&nbsp;6, each with its gamertag. A gamertag is personal data under the
    GDPR even without a real name. The basis is legitimate interest (Art.&nbsp;6(1)(f)):
    the data is already visible in the game to every player, it is shown unchanged
    and in the same context, and nobody is being rated here &mdash; cars are.
    <b>If you want your gamertag gone from this site, write to the address above
    and it will be removed.</b> The gamertag is the search key, so this is easy to
    do.</p>
    <p><b>Laps you submit yourself.</b> Submitting through the app sends your
    gamertag if you set one (in the clear &mdash; it is the name your times appear
    under; without one they appear under a temporary name such as
    &ldquo;Player-7F3A2C&rdquo;, derived from the hashed identifier, and a gamertag
    sent later replaces it on all your laps), a
    <i>peppered</i> SHA-256 of a hardware identifier (never the identifier itself,
    and it cannot be reversed without a server-side secret that never leaves the
    machine), the telemetry of the lap, and a random installation id. The basis is
    consent: nothing is sent unless you trigger it, and you can withdraw &mdash; the
    lap is then hidden. Telemetry is kept because a record time without evidence is
    worth nothing.</p>
    <p><b>IP addresses.</b> Three places, all bounded. Every page view and
    download writes the IP to the server's own log, of which at most about
    30&nbsp;MB is kept before it overwrites itself &mdash; there is no growing
    archive. Ten wrong upload passwords lock a machine for 24 hours, which needs the
    IP stored until the lock expires; in memory alone a lock would merely be a
    request. And registering the app records a timestamp against the IP so that no
    more than five registrations an hour come from one address &mdash; those entries
    are dropped after 24 hours, so that file does not grow either.</p>
    <p><b>How long things are kept.</b> Leaderboard rows and submitted laps:
    indefinitely, that is the point of the site. Hidden laps stay but are invisible
    &mdash; what looks wrong today is sometimes tomorrow's only evidence of
    <i>what</i> went wrong. Hardware hashes of accounts that submit nothing for
    12&nbsp;months are dropped; those of blocked accounts are kept longer, since
    otherwise a block would lift itself.</p>
    <p><b>Your rights</b> &mdash; access, correction, deletion, restriction,
    objection, and complaint to a supervisory authority &mdash; all through the
    address above. One honest limit: for submitted laps the operator cannot identify
    anyone. There is a gamertag (if one was given) and a peppered hash, nothing
    else. So a deletion request has to name <i>which gamertag</i> &mdash; or the
    temporary player name; there is nothing else to search by.
    That is the situation Art.&nbsp;11(2) GDPR describes.</p>
    <p class="endnote"><a href="/app">FH Companion</a> &middot; <a href="https://discord.gg/A9ssnMXPZf" rel="noopener noreferrer">Discord</a>__SOURCE_END__</p>
  </footer>
</div>

<script>
__SUBMITTED_JS__
const D = __PAYLOAD__;

// Signature bit order comes from the dataset, so adding a flag upstream cannot
// silently shift what a filter means here.
const BIT = {};
D.flags.forEach((name, i) => { BIT[name] = 1 << i; });

const CLASS_ORDER = ["D", "C", "B", "A", "S1", "S2", "R"];
// Slowest to fastest, including the classes a car can be stock in but no board exists
// for, so a stock-vs-board comparison never falls off the end.
const FULL_CLASS_ORDER = ["D", "C", "B", "A", "S1", "S2", "R", "X"];

// A lap is always scored in the class of the BOARD it sits on. This says something
// else: where the car started. Stock A driving a B board is a car detuned to fit, and
// stock D on an S1 board is a car built far past what it left the factory as.
function tuneOf(carIdx, boardClass) {
  const meta = (D.carMeta || [])[carIdx];
  if (!meta || !meta.stockClass) return null;
  const stock = FULL_CLASS_ORDER.indexOf(meta.stockClass);
  const board = FULL_CLASS_ORDER.indexOf(boardClass);
  if (stock < 0 || board < 0) return null;
  if (board < stock) return "down";
  if (board > stock) return "up";
  return "same";
}
const TUNE_LABEL = { down: "detuned", same: "stock class", up: "tuned up" };
// A track only stands in for a missing car if its board is deep enough to have a
// meaningful "slowest car". On a board the sweep abandoned at 149 entries the
// slowest time is nowhere near slow, so the substitution is a gift rather than a
// penalty -- and it lands on exactly the cars that did not run there. Below this,
// the track leaves the time sum entirely, for every car, so the sums stay
// comparable. Points are unaffected: they need no substitute, and a 149-entry
// board can only hand out a handful of them anyway.
const MIN_BOARD_FOR_TIME = 1000;
const ASSISTS = [
  ["tcs", "TCS"], ["abs", "ABS"], ["stm", "STM"],
  ["friction", "Friction"], ["autobrake", "Brake help"], ["supereasy", "Super easy"],
];

// Jeder Filter ist eine MENGE, nicht ein Wert: "Ford oder Toyota", "Coupe oder
// Limousine", "Road Racing und Street Racing", "B und A". Eine leere Menge heisst
// "alle" -- so bleibt der Ausgangszustand derselbe wie vorher, ohne Sonderfall.
//
// Bei der Leistungsklasse hat das eine Folge, die man wissen muss: gewertet wird
// weiterhin JE BOARD (ein D-Auto faehrt nie gegen ein S2-Auto um Punkte), aber die
// Summen laufen ueber alle gewaehlten Klassen. Ein Auto, das in zwei Klassen
// vorkommt, sammelt aus beiden -- die Wertung belohnt dann Bandbreite mit. Genau
// deshalb sind die Klassen-Chips einzeln waehlbar geblieben statt fest gekoppelt.
const F = {
  cat: new Set(),         // leer = alle
  cls: new Set(),         // leer = alle in Reichweite
  make: new Set(),
  country: new Set(),
  carType: new Set(),
  tune: new Set(),        // down | same | up
  yearFrom: null,
  yearTo: null,
  tracks: new Set(),      // empty = all
  clean: "valid",         // any | valid | invalid -- valid by default, because a
                          // board appends its invalid laps after the valid ones and
                          // they restart from a faster time

  gear: "any",            // any | auto | manual | clutch
  assist: {},             // name -> any | with | without
  car: "",
};
ASSISTS.forEach(([key]) => { F.assist[key] = "any"; });

let TAB = "points";
const SORT = { points: { key: "score", dir: -1 }, time: { key: "score", dir: 1 }, boards: { key: "ms", dir: 1 },
               contest: { key: "impl", dir: -1 } };
let FOCUS_CAR = null;     // car index for the single-car view

/* ---------- formatting ---------- */
// The page is English throughout, so the numbers are grouped the English way too:
// "1,234", not "1.234". A German thousands dot next to an English sentence reads as a
// decimal point to everyone the page is written for.
const nf = new Intl.NumberFormat("en-GB");
function num(n) { return nf.format(Math.round(n)); }
function lapText(ms) {
  const total = ms / 1000, m = Math.floor(total / 60), s = total - m * 60;
  return m + ":" + (s < 10 ? "0" : "") + s.toFixed(3);
}
function sumText(ms) {
  const total = Math.round(ms / 1000), h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60), s = total % 60;
  const mm = (m < 10 ? "0" : "") + m, ss = (s < 10 ? "0" : "") + s;
  return (h > 0 ? h + ":" + mm : m) + ":" + ss;
}
function nth(n) {
  const suffix = (n % 100 >= 11 && n % 100 <= 13) ? "th"
    : ({ 1: "st", 2: "nd", 3: "rd" }[n % 10] || "th");
  return n + suffix;
}
function el(tag, cls, text) {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

/* ---------- filter -> signature mask ---------- */
function masks() {
  let need = 0, forbid = 0;
  if (F.clean === "valid") need |= BIT.clean;
  if (F.clean === "invalid") forbid |= BIT.clean;
  if (F.gear === "auto") need |= BIT.autoshift;
  if (F.gear === "manual") { forbid |= BIT.autoshift; forbid |= BIT.clutch; }
  if (F.gear === "clutch") { need |= BIT.clutch; forbid |= BIT.autoshift; }
  ASSISTS.forEach(([key]) => {
    if (F.assist[key] === "with") need |= BIT[key];
    if (F.assist[key] === "without") forbid |= BIT[key];
  });
  return { need, forbid };
}
function anyFilterActive() {
  return F.clean !== "any" || F.gear !== "any" ||
    ASSISTS.some(([k]) => F.assist[k] !== "any");
}

/* ---------- core: which lap represents each car on one board ---------- */
/* Die Rivals-Auswahl, unveraendert. Aufgerufen wird sie nur ueber pickCars
   unten -- so bekommen BEIDE Ansichten (Wertung und Bretter) die
   eingereichten Zeiten, und keine Stelle kann sie vergessen. */
function pickCarsRivals(board, need, forbid) {
  const perCar = new Map();   // carIdx -> { laps: [{ms, rank, pi}], count }
  // Der PI je Runde steht nur in Brettern, auf denen er gelesen wurde; aeltere
  // Datensaetze und Bretter ohne einen einzigen gelesenen PI haben kein `lpi`.
  const lpi = board.lpi || null;
  const ok = [];
  for (let g = 0; g < board.gsig.length; g++) {
    const sig = board.gsig[g];
    ok[g] = ((sig & need) === need) && ((sig & forbid) === 0);
    if (!ok[g]) continue;
    const car = board.gcar[g];
    let bucket = perCar.get(car);
    if (!bucket) { bucket = { laps: [], count: 0 }; perCar.set(car, bucket); }
    bucket.count += board.gcount[g];
  }
  for (let i = 0; i < board.lgrp.length; i++) {
    const g = board.lgrp[i];
    if (!ok[g]) continue;
    perCar.get(board.gcar[g]).laps.push({ ms: board.lms[i], rank: board.lrank[i],
                                          pi: lpi ? lpi[i] : null });
  }

  const out = new Map();
  perCar.forEach((bucket, car) => {
    if (!bucket.laps.length) return;
    bucket.laps.sort((a, b) => a.ms - b.ms);
    const bestRank = bucket.laps.reduce((m, l) => Math.min(m, l.rank), Infinity);
    // The rule: the better the car's best lap looks, the deeper into its own times
    // we reach, because a lap at the very top is usually an outlier.
    const wanted = bestRank <= 100 ? 5 : (bestRank <= 1000 ? 2 : 1);
    const have = Math.min(wanted, bucket.laps.length);
    const chosen = bucket.laps[have - 1];
    out.set(car, {
      ms: chosen.ms, rank: chosen.rank, bestRank: bestRank,
      took: have, wanted: wanted, count: bucket.count,
      thin: bucket.count < wanted,
      // Der PI GENAU DIESER Runde, nicht der des Autos: dasselbe Auto faehrt auf
      // einem Brett mit verschiedenen Abstimmungen, jede mit ihrem eigenen PI.
      pi: chosen.pi == null ? null : chosen.pi,
    });
  });
  return out;
}

/* ---------- eingereichte Zeiten ---------- */
/* Kommen zur Laufzeit von /api/lap/list, nicht aus dem Bau: eingereichte
   Zeiten aendern sich laufend, und ein Neubau der Seite dauert ueber eine
   Stunde. Ohne Server (Seite als Datei geoeffnet) bleibt SUBMITTED leer,
   und die Seite ist genau die alte. */
let SUBMITTED = null;
function pickCars(board, need, forbid) {
  return applySubmitted(D, board, pickCarsRivals(board, need, forbid), SUBMITTED);
}

/* How many rows one board still holds behind the current filters -- the sample size
   behind every number on this page. With `withCars`, the car filters (make, country,
   type, year, the search box) and the tune filter count too, so a search for a single
   car turns this into "how often THAT car stands in this leaderboard".

   It counts what was SCANNED, not what exists: a board is read to the first 20,000
   ranks, so on a 700,000-entry board this is a sample of the top, not the whole. */
function entriesOnBoard(board, need, forbid, withCars) {
  let n = 0;
  for (let g = 0; g < board.gsig.length; g++) {
    const sig = board.gsig[g];
    if (((sig & need) !== need) || ((sig & forbid) !== 0)) continue;
    if (withCars) {
      const car = board.gcar[g];
      if (!carMatches(car)) continue;
      if (F.tune.size && !F.tune.has(tuneOf(car, D.classes[board.k]))) continue;
    }
    n += board.gcount[g];
  }
  return n;
}

/* ---------- boards in scope ---------- */
function boardsInScope() {
  return D.boards.filter(b => {
    if (F.cat.size && !F.cat.has(D.categories[b.c])) return false;
    if (F.tracks.size && !F.tracks.has(D.tracks[b.t])) return false;
    return true;
  });
}

/* ---------- per class: points and time sum ---------- */
/* One ranking over every selected board.
   `classes` is a list, because the class filter allows several at once. Each BOARD
   still ranks only its own field -- a D car never races an S2 car for points -- and
   the totals add those per-board results up, exactly as they already add up across
   tracks. What changes with several classes is the meaning of the total: a car that
   exists in two classes now collects from both, so the ranking rewards range as well
   as pace. The class chips are the switch for that; one class at a time reads as
   before. */
function scoreTable(classes) {
  const { need, forbid } = masks();
  const wanted = new Set(classes);
  const boards = boardsInScope().filter(b => wanted.has(D.classes[b.k]));
  const many = wanted.size > 1;
  const perTrack = [];        // { track, klass, key, picks, worst, deep }
  const shallowTracks = [];
  boards.forEach(board => {
    const picks = pickCars(board, need, forbid);
    if (!picks.size) return;
    let worst = 0;
    picks.forEach(p => { if (p.ms > worst) worst = p.ms; });
    const deep = (board.valid || board.rows) >= MIN_BOARD_FOR_TIME;
    const klass = D.classes[board.k];
    const track = D.tracks[board.t];
    // Der Schluessel MUSS die Klasse enthalten. Dieselbe Strecke kommt in mehreren
    // Klassen vor; mit dem Streckennamen allein wuerde das Board der zweiten Klasse
    // das der ersten ueberschreiben, und ein Auto verlaere die Haelfte seiner Zeiten,
    // ohne dass irgendwo etwas fehlt.
    const key = track + "|" + klass;
    if (!deep) shallowTracks.push(many ? track + " (" + klass + ")" : track);
    perTrack.push({ track: track, klass: klass, key: key, label: many ? track + "  " + klass : track,
                    board: board, picks: picks, worst: worst, deep: deep });
  });

  const cars = new Map();     // carIdx -> aggregate
  perTrack.forEach(entry => {
    const ordered = Array.from(entry.picks.entries()).sort((a, b) => a[1].ms - b[1].ms);
    const n = ordered.length;
    ordered.forEach(([car, pick], index) => {
      // Points are awarded from the full field; the tune filter decides which cars are
      // LISTED, never how many points first place was worth.
      if (F.tune.size && !F.tune.has(tuneOf(car, entry.klass))) return;
      let agg = cars.get(car);
      if (!agg) { agg = { car: car, points: 0, ms: 0, present: 0, thin: 0, entries: 0, per: new Map() }; cars.set(car, agg); }
      // Place 1 scores as many points as there are cars in this leaderboard.
      const points = n - index;
      agg.points += points;
      agg.present += 1;
      // How OFTEN the car stands in these leaderboards, not how well: every row it
      // holds on every selected board, added up. One fast lap and four hundred are
      // the same single dot in the ranking above, and only this number tells them
      // apart -- a car with 400 entries has a settled time, one with 2 does not.
      agg.entries += pick.count;
      if (pick.thin) agg.thin += 1;
      agg.per.set(entry.key, { ms: pick.ms, points: points, pos: index + 1, of: n,
                                took: pick.took, count: pick.count, thin: pick.thin,
                                rank: pick.rank, bestRank: pick.bestRank, substituted: false,
                                pi: pick.pi == null ? null : pick.pi });
    });
  });

  // Time sum: a car missing on a track inherits that track's slowest time, so the
  // sum stays comparable across cars that did not run everywhere. A car that
  // appears on no selected board is not in `cars` at all.
  cars.forEach(agg => {
    perTrack.forEach(entry => {
      const own = agg.per.get(entry.key);
      if (own) {
        // A shallow board still shows its real time and scores its points; it just
        // does not enter the time sum, because the other cars are not charged for
        // it either.
        if (entry.deep) agg.ms += own.ms;
        else own.outOfTimeSum = true;
        return;
      }
      if (!entry.deep) {
        agg.per.set(entry.key, { ms: null, points: 0, pos: null,
                                   of: entry.picks.size, substituted: false,
                                   outOfTimeSum: true, missing: true });
        return;
      }
      agg.ms += entry.worst;
      agg.per.set(entry.key, { ms: entry.worst, points: 0, pos: null,
                                 of: entry.picks.size, substituted: true });
    });
  });

  // The denominator for a car's share: every row on every selected board of this
  // class that survives the lap filters -- counted over ALL cars, including the ones
  // the tune or make filter hides, so the share means "of this leaderboard" and not
  // "of what is currently listed".
  let entriesInScope = 0;
  perTrack.forEach(entry => entry.picks.forEach(pick => { entriesInScope += pick.count; }));

  return { classes: classes, tracks: perTrack, cars: Array.from(cars.values()),
           shallowTracks: shallowTracks, entries: entriesInScope,
           timeTracks: perTrack.filter(entry => entry.deep).length };
}

function classesInScope() {
  const seen = new Set(boardsInScope().map(b => D.classes[b.k]));
  return CLASS_ORDER.filter(k => seen.has(k)).concat(
    Array.from(seen).filter(k => CLASS_ORDER.indexOf(k) < 0));
}

/* Die Klassen, ueber die gerade gewertet wird: die gewaehlten, sonst alle in
   Reichweite. Faellt die Auswahl durch einen anderen Filter leer aus -- eine Klasse
   gewaehlt, dann eine Strecke, auf der es sie nicht gibt -- wird auf "alle"
   zurueckgefallen, statt eine leere Seite zu zeigen. */
function selectedClasses() {
  const inScope = classesInScope();
  if (!F.cls.size) return inScope;
  const picked = inScope.filter(k => F.cls.has(k));
  return picked.length ? picked : inScope;
}

/* Wie die gewertete Klasse ueberschrieben wird: eine als sie selbst, zwei bis drei
   aufgezaehlt, mehr als drei gezaehlt -- eine Kopfzeile mit sieben Klassennamen
   liest niemand. */
/* Beim Start EINE Klasse vorwaehlen.

   Sonst haette der Chip "All" bei den Klassen etwas anderes bedeutet als ueberall
   sonst: die Seite zeigte frueher ohne Auswahl stillschweigend die erste Klasse,
   nicht alle. Jetzt heisst "All" wirklich alle -- ueber sieben Klassen summiert,
   was eine ganz andere Aussage ist. Damit die Seite trotzdem so aufmacht, wie sie
   immer aufgemacht hat, wird die erste Klasse aktiv gesetzt statt sie zu erraten.
   Wer alles zusammen sehen will, klickt "All" und sieht dann auch, dass er es
   getan hat. */
function selectDefaultClass() {
  F.cls.clear();
  const first = classesInScope()[0];
  if (first) F.cls.add(first);
}

function classLabel(classes) {
  if (!classes || !classes.length) return "—";
  if (classes.length <= 3) return classes.join(", ");
  return classes.length + " classes";
}

/* ---------- car matching ---------- */
function carMatches(carIdx) {
  const meta = (D.carMeta || [])[carIdx];
  if (F.make.size && (!meta || !F.make.has(meta.make))) return false;
  if (F.country.size && (!meta || !F.country.has(meta.country))) return false;
  if (F.carType.size && (!meta || !F.carType.has(meta.type))) return false;
  if (F.yearFrom !== null || F.yearTo !== null) {
    // A car whose year is unknown cannot be judged against a range, so a narrowed
    // range excludes it. At full width the filter is off and nothing is excluded.
    if (!meta || !meta.year) return false;
    if (F.yearFrom !== null && meta.year < F.yearFrom) return false;
    if (F.yearTo !== null && meta.year > F.yearTo) return false;
  }
  const needle = F.car.trim().toLowerCase();
  if (!needle) return true;
  return D.carNames[carIdx].toLowerCase().indexOf(needle) >= 0 ||
    String(D.carIds[carIdx]).indexOf(needle) >= 0;
}

/* The distinct years that actually exist, ascending -- the slider's stops.

   A linear year axis is unusable here because the roster is not continuous. Forza
   ships the Halo Warthog with its in-universe model year, 2554, so a raw 1932-2554
   track spends 85% of its travel on a 529-year gap holding one car: every real year
   is crushed into the leftmost sixth. Stepping over the 70 years PRESENT gives each
   one equal width and puts the Warthog one step past 2025, where it belongs.

   The filter itself still compares real years (F.yearFrom/F.yearTo hold years, not
   indices), so only the control's mapping changes, not what it selects. */
let YEAR_STOPS = null;
function yearStops() {
  if (YEAR_STOPS) return YEAR_STOPS;
  const seen = new Set();
  (D.carMeta || []).forEach(meta => { if (meta && meta.year) seen.add(meta.year); });
  YEAR_STOPS = Array.from(seen).sort((a, b) => a - b);
  return YEAR_STOPS;
}
function yearIndex(year, fallback) {
  const stops = yearStops();
  const at = stops.indexOf(year);
  if (at >= 0) return at;
  // A year that is not itself a stop (a stale saved filter) snaps to the nearest one
  // rather than silently resetting the range.
  let best = fallback;
  let gap = Infinity;
  stops.forEach((stop, i) => {
    const d = Math.abs(stop - year);
    if (d < gap) { gap = d; best = i; }
  });
  return best;
}

function yearBounds() {
  const stops = yearStops();
  return stops.length ? [stops[0], stops[stops.length - 1]] : null;
}

function renderYearRange() {
  const bounds = yearBounds();
  if (!bounds) { return; }
  const [low, high] = bounds;
  const stops = yearStops();
  const last = stops.length - 1;
  const from = document.getElementById("year-from");
  const to = document.getElementById("year-to");
  // Indices, not years: one step per year that exists.
  [from, to].forEach(input => {
    input.min = "0";
    input.max = String(last);
    input.step = "1";
  });
  const valueFrom = F.yearFrom === null ? low : F.yearFrom;
  const valueTo = F.yearTo === null ? high : F.yearTo;
  const indexFrom = yearIndex(valueFrom, 0);
  const indexTo = yearIndex(valueTo, last);
  from.value = String(indexFrom);
  to.value = String(indexTo);

  const span = Math.max(1, last);
  const fill = document.getElementById("year-fill");
  fill.style.left = (100 * indexFrom / span) + "%";
  fill.style.right = (100 * (last - indexTo) / span) + "%";

  const readout = document.getElementById("year-readout");
  const known = (D.carMeta || []).filter(meta => meta && meta.year).length;
  if (F.yearFrom === null && F.yearTo === null) {
    readout.textContent = low + " – " + high;
    readout.title = "All model years. " + known + " of " + (D.carMeta || []).length
      + " cars have a year; narrowing the range leaves the rest out. The track steps "
      + "through the " + stops.length + " years present, so gaps take no width.";
  } else {
    readout.textContent = valueFrom + " – " + valueTo;
    readout.title = "Cars without a known model year are excluded while the range is narrowed.";
  }
}

function metaValues(field) {
  const seen = new Set();
  (D.carMeta || []).forEach(meta => { if (meta && meta[field]) seen.add(meta[field]); });
  return Array.from(seen).sort();
}
function carLabel(carIdx) { return D.carNames[carIdx]; }

/* ---------- chips ---------- */
function multiChips(host, values, set, formatter, chipClass) {
  host.textContent = "";
  const all = el("button", "chip", "All");
  all.type = "button";
  all.setAttribute("aria-pressed", set.size === 0 ? "true" : "false");
  all.onclick = () => { set.clear(); render(); };
  host.appendChild(all);
  values.forEach(value => {
    const b = el("button", chipClass || "chip", formatter ? formatter(value) : value);
    b.type = "button";
    b.setAttribute("aria-pressed", set.has(value) ? "true" : "false");
    b.onclick = () => { if (set.has(value)) set.delete(value); else set.add(value); render(); };
    host.appendChild(b);
  });
}
function triGroup(host, caption, options, get, set) {
  const group = el("div", "trigroup");
  if (caption) group.appendChild(el("span", null, caption));
  const box = el("div", "tri");
  options.forEach(([value, text]) => {
    const b = el("button", null, text);
    b.type = "button";
    b.setAttribute("aria-pressed", get() === value ? "true" : "false");
    b.onclick = () => { set(value); render(); };
    box.appendChild(b);
  });
  group.appendChild(box);
  host.appendChild(group);
}

/* ---------- shared table helpers ---------- */
function head(row, cols, tab) {
  row.textContent = "";
  cols.forEach(col => {
    const th = el("th", col.num ? "num" : null);
    if (!col.key) {
      th.appendChild(el("span", "sortbtn static", col.head));
    } else {
      const b = el("button", "sortbtn");
      b.type = "button";
      const active = SORT[tab].key === col.key;
      b.dataset.active = active ? "1" : "0";
      b.textContent = col.head + " ";
      b.appendChild(el("span", "arrow", active ? (SORT[tab].dir > 0 ? "▲" : "▼") : ""));
      b.onclick = () => {
        if (SORT[tab].key === col.key) SORT[tab].dir = -SORT[tab].dir;
        else { SORT[tab].key = col.key; SORT[tab].dir = col.desc ? -1 : 1; }
        render();
      };
      th.appendChild(b);
    }
    row.appendChild(th);
  });
}
function gapCell(value, worst, text, title) {
  const td = el("td");
  const wrap = el("div", "gap");
  const track = el("div", "track");
  const fill = el("div", "fill");
  fill.style.width = Math.max(2, (worst > 0 ? (value / worst) * 100 : 0)) + "%";
  track.appendChild(fill);
  const v = el("span", "v", text);
  wrap.append(track, v);
  td.appendChild(wrap);
  if (title) td.title = title;
  return td;
}
function carCell(carIdx) {
  const td = el("td", "car");
  const b = el("button", null, carLabel(carIdx));
  b.type = "button";
  b.title = "Inspect this car on its own";
  b.onclick = () => { FOCUS_CAR = carIdx; TAB = "car"; render(); };
  td.appendChild(b);
  if (carLabel(carIdx).indexOf("Car #") !== 0) {
    td.appendChild(el("span", "cid", "  #" + D.carIds[carIdx]));
  }
  return td;
}
function emptyBox(strongText, rest) {
  const box = el("div", "empty");
  box.appendChild(el("b", null, strongText));
  box.appendChild(document.createTextNode(rest || ""));
  return box;
}

/* ---------- view: points / time ---------- */
function renderScoreboard(host, mode) {
  const classes = selectedClasses();
  if (!classes.length) { host.appendChild(emptyBox("No boards in this selection.", "")); return; }
  const klass = classLabel(classes);
  const table = scoreTable(classes);
  if (!table.cars.length) {
    host.appendChild(emptyBox("No lap survives these filters.",
      " Set an assist or the gearbox back to “All”."));
    return;
  }

  const tab = mode;
  const rows = table.cars.filter(agg => carMatches(agg.car));
  const sort = SORT[tab];
  const value = agg => (mode === "points" ? agg.points : agg.ms);
  const by = {
    car: agg => carLabel(agg.car),
    score: value,
    present: agg => agg.present,
    entries: agg => agg.entries,
    thin: agg => agg.thin,
  }[sort.key] || value;
  rows.sort((a, b) => {
    const va = by(a), vb = by(b);
    if (va < vb) return -sort.dir;
    if (va > vb) return sort.dir;
    return value(a) - value(b);
  });

  // Positions come from the whole class, not from the filtered view, so searching
  // for one car still tells you where it stands.
  const ordered = table.cars.slice().sort((a, b) =>
    mode === "points" ? b.points - a.points : a.ms - b.ms);
  const place = new Map();
  ordered.forEach((agg, index) => place.set(agg.car, index + 1));
  const leader = ordered[0];
  const worstGap = ordered.reduce((max, agg) => Math.max(max,
    mode === "points" ? leader.points - agg.points : agg.ms - leader.ms), 1);

  const cols = [
    { head: "#" },
    { head: "Car", key: "car" },
    { head: mode === "points" ? "Points" : "Time sum", key: "score", num: true, desc: mode === "points" },
    { head: mode === "points" ? "Gap to first" : "Gap" },
    { head: "Tracks", key: "present", num: true },
    { head: "Entries", key: "entries", num: true, desc: true },
    { head: "Note", key: "thin" },
  ];
  const wrap = el("div", "tablewrap");
  const tableEl = el("table");
  const thead = el("thead");
  const hrow = el("tr");
  head(hrow, cols, tab);
  thead.appendChild(hrow);
  const tbody = el("tbody");

  rows.forEach(agg => {
    const tr = el("tr");
    tr.appendChild(el("td", "pos", place.get(agg.car)));
    tr.appendChild(carCell(agg.car));

    const score = el("td", "num");
    score.textContent = mode === "points" ? num(agg.points) : sumText(agg.ms);
    if (mode !== "points") score.title = num(agg.ms) + " ms";
    tr.appendChild(score);

    const gap = mode === "points" ? leader.points - agg.points : agg.ms - leader.ms;
    tr.appendChild(gapCell(gap, worstGap,
      mode === "points"
        ? (gap === 0 ? "—" : "-" + num(gap))
        : (gap === 0 ? "—" : "+" + (gap / 1000).toFixed(1) + " s"),
      "Baseline: " + carLabel(leader.car)));

    const present = el("td", "num");
    present.textContent = agg.present + " / " + table.tracks.length;
    present.title = table.tracks.length - agg.present === 0
      ? "Present on every selected track"
      : (mode === "points"
        ? "On " + (table.tracks.length - agg.present) + " track(s) without a time: 0 points there"
        : "On " + (table.tracks.length - agg.present) + " track(s), scored with the slowest time there");
    tr.appendChild(present);

    // How many rows this car actually holds behind the current filters. The ranking
    // itself is decided by ONE lap per board, so a car can stand third on a time
    // nobody else repeated. This column is the sample size behind that place.
    const entries = el("td", "num");
    entries.textContent = num(agg.entries);
    const share = table.entries > 0 ? (100 * agg.entries / table.entries) : 0;
    entries.title = num(agg.entries) + " of " + num(table.entries)
      + " entries behind the filters in class " + klass
      + " (" + (share >= 10 ? share.toFixed(0) : share.toFixed(share >= 1 ? 1 : 2)) + "%)"
      + ", over " + agg.present + " board(s). Counted within the scanned depth of each"
      + " board, not the whole leaderboard.";
    tr.appendChild(entries);

    const note = el("td");
    if (agg.thin) {
      const flag = el("span", "flag", "thin ×" + agg.thin);
      flag.title = "On " + agg.thin + " track(s) the car had fewer laps than the rule asks for.";
      note.appendChild(flag);
    }
    tr.appendChild(note);
    tbody.appendChild(tr);
  });

  tableEl.append(thead, tbody);
  wrap.appendChild(tableEl);
  host.appendChild(wrap);
  if (!rows.length) host.appendChild(emptyBox("No car matches the search.", ""));
}

/* ---------- view: boards ---------- */
function renderBoards(host) {
  const { need, forbid } = masks();
  const boards = boardsInScope().filter(b => !F.cls.size || F.cls.has(D.classes[b.k]));
  if (!boards.length) { host.appendChild(emptyBox("No boards in this selection.", "")); return; }

  const rows = [];
  boards.forEach(board => {
    pickCars(board, need, forbid).forEach((pick, car) => {
      if (!carMatches(car)) return;
      if (F.tune.size && !F.tune.has(tuneOf(car, D.classes[board.k]))) return;
      rows.push({ track: D.tracks[board.t], klass: D.classes[board.k], car: car, pick: pick });
    });
  });
  if (!rows.length) { host.appendChild(emptyBox("No lap survives these filters.", "")); return; }

  const sort = SORT.boards;
  const by = {
    car: r => carLabel(r.car),
    track: r => r.track,
    klass: r => CLASS_ORDER.indexOf(r.klass),
    pi: r => (r.pick.pi == null ? -1 : r.pick.pi),
    ms: r => r.pick.ms,
    rank: r => r.pick.rank,
    count: r => r.pick.count,
    took: r => r.pick.took,
  }[sort.key] || (r => r.pick.ms);
  rows.sort((a, b) => {
    const va = by(a), vb = by(b);
    if (va < vb) return -sort.dir;
    if (va > vb) return sort.dir;
    return a.pick.ms - b.pick.ms;
  });

  const fastest = rows.reduce((min, r) => Math.min(min, r.pick.ms), Infinity);
  const worstGap = rows.reduce((max, r) => Math.max(max, r.pick.ms - fastest), 1);

  const cols = [
    { head: "#" },
    { head: "Car", key: "car" },
    { head: "Track", key: "track" },
    { head: "Class", key: "klass" },
    { head: "PI", key: "pi", num: true, desc: true },
    { head: "Time", key: "ms", num: true },
    { head: "Gap" },
    { head: "Rank", key: "rank", num: true },
    { head: "Entries", key: "count", num: true, desc: true },
    { head: "Chosen", key: "took" },
  ];
  const wrap = el("div", "tablewrap");
  const tableEl = el("table");
  const thead = el("thead");
  const hrow = el("tr");
  head(hrow, cols, "boards");
  thead.appendChild(hrow);
  const tbody = el("tbody");

  rows.slice(0, 600).forEach((row, index) => {
    const tr = el("tr");
    tr.appendChild(el("td", "pos", index + 1));
    tr.appendChild(carCell(row.car));
    tr.appendChild(el("td", null, row.track));
    tr.appendChild(el("td", "m", row.klass));
    const pi = el("td", "num", row.pick.pi == null ? "—" : String(row.pick.pi));
    pi.title = row.pick.pi == null
      ? "PI of this lap not read off the leaderboard"
      : "PI the car had on this lap, as the leaderboard shows it";
    tr.appendChild(pi);
    const zeit = el("td", "num", lapText(row.pick.ms));
    if (row.pick.submitted) {
      // SICHTBAR ANDERS. Eine eingereichte Zeit ist keine Rivals-Zeit: sie ist
      // selbst gefahren, selbst gemessen und nicht vom Spiel bestaetigt.
      const s = row.pick.submitted;
      const marke = el("span", "flag", "submitted");
      marke.title = "Submitted by " + (s.gamertag || "a player")
        + (s.received ? " on " + String(s.received).slice(0, 10) : "")
        + (row.pick.rivalsMs ? ". Replaces the Rivals time " + lapText(row.pick.rivalsMs) : "")
        + ". Not verified by the game.";
      zeit.append(document.createTextNode(" "), marke);
    }
    tr.appendChild(zeit);
    tr.appendChild(gapCell(row.pick.ms - fastest, worstGap,
      row.pick.ms === fastest ? "—" : "+" + ((row.pick.ms - fastest) / 1000).toFixed(3),
      "Fastest time in this selection: " + lapText(fastest)));
    const rank = el("td", "num", num(row.pick.rank));
    rank.title = "Best position this car holds on the board: " + num(row.pick.bestRank);
    tr.appendChild(rank);
    tr.appendChild(el("td", "num", num(row.pick.count)));
    const took = el("td");
    took.appendChild(el("span", "sub2", nth(row.pick.took) + " fastest"));
    if (row.pick.thin) {
      const flag = el("span", "flag", "thin");
      flag.title = "Fewer laps than the rule asks for (" + row.pick.wanted + "); the slowest available one counts.";
      took.append(document.createTextNode(" "), flag);
    }
    tr.appendChild(took);
    tbody.appendChild(tr);
  });

  tableEl.append(thead, tbody);
  wrap.appendChild(tableEl);
  host.appendChild(wrap);
  if (rows.length > 600) {
    const note = el("div", "empty", "Showing 600 of " + num(rows.length) +
      " rows. Narrow the filters to see the rest.");
    host.appendChild(note);
  }
}

/* ---------- view: one car across all classes ---------- */
function renderCar(host) {
  if (FOCUS_CAR === null) {
    const needle = F.car.trim();
    if (needle) {
      const hits = D.carNames.map((_, i) => i).filter(carMatches);
      if (hits.length === 1) FOCUS_CAR = hits[0];
      else if (hits.length > 1) {
        const box = el("div", "cards");
        hits.slice(0, 60).forEach(carIdx => {
          const card = el("div", "card");
          const b = el("button", null, carLabel(carIdx));
          b.type = "button";
          b.style.cssText = "font:inherit;background:none;border:0;color:var(--link);cursor:pointer;padding:0";
          b.onclick = () => { FOCUS_CAR = carIdx; render(); };
          const h = el("h3");
          h.appendChild(b);
          card.appendChild(h);
          card.appendChild(el("div", "sub2", "ID " + D.carIds[carIdx]));
          box.appendChild(card);
        });
        host.appendChild(emptyBox(hits.length + " cars match. ", "Pick one:"));
        host.appendChild(box);
        return;
      }
    }
    if (FOCUS_CAR === null) {
      host.appendChild(emptyBox("No car selected.",
        " Search by name or id above, or click a car name in any ranking."));
      return;
    }
  }

  const carIdx = FOCUS_CAR;
  const header = el("div", "context");
  const name = el("b", null, carLabel(carIdx));
  header.append(document.createTextNode("Car: "), name,
    document.createTextNode("  ·  ID "), el("b", null, String(D.carIds[carIdx])));
  const clear = el("button", "chip small", "Another car");
  clear.type = "button";
  clear.onclick = () => { FOCUS_CAR = null; F.car = ""; document.getElementById("f-car").value = ""; render(); };
  header.appendChild(clear);
  host.appendChild(header);

  const cards = el("div", "cards");
  let found = 0;
  selectedClasses().forEach(klass => {
    const table = scoreTable([klass]);
    const mine = table.cars.find(agg => agg.car === carIdx);
    if (!mine) return;
    found += 1;
    const byPoints = table.cars.slice().sort((a, b) => b.points - a.points);
    const byTime = table.cars.slice().sort((a, b) => a.ms - b.ms);
    const posPoints = byPoints.findIndex(agg => agg.car === carIdx) + 1;
    const posTime = byTime.findIndex(agg => agg.car === carIdx) + 1;

    const card = el("div", "card");
    const h = el("h3");
    h.appendChild(el("span", "kbadge", klass));
    h.appendChild(document.createTextNode("Performance class"));
    card.appendChild(h);

    const kv = (k, v, title) => {
      const line = el("div", "kv");
      line.appendChild(el("span", null, k));
      const value = el("span", null, v);
      if (title) value.title = title;
      line.append(value);
      card.appendChild(line);
    };
    kv("Points ranking", "Place " + posPoints + " / " + byPoints.length);
    kv("Points", num(mine.points));
    kv("Time ranking", "Place " + posTime + " / " + byTime.length);
    kv("Time sum", sumText(mine.ms), num(mine.ms) + " ms");
    kv("Tracks", mine.present + " / " + table.tracks.length);
    kv("Entries", num(mine.entries) + (table.entries > 0
        ? "  ·  " + (100 * mine.entries / table.entries).toFixed(
            100 * mine.entries / table.entries >= 10 ? 0 : 1) + "% of the class"
        : ""),
      num(mine.entries) + " of " + num(table.entries) + " rows behind the filters in "
      + "this class, counted within the scanned depth of each board. This is the "
      + "sample size behind the places above: a place built on two laps is a guess, "
      + "one built on hundreds is not.");

    table.tracks.forEach(entry => {
      const own = mine.per.get(entry.key);
      const line = el("div", "trackline" + (own && own.substituted ? " sub-est" : ""));
      const label = el("span", null, entry.track);
      if (!entry.deep) {
        label.textContent = entry.track + "  (board too small)";
        label.title = "Only " + num(entry.board.valid || entry.board.rows)
          + " entries scanned, below the " + num(MIN_BOARD_FOR_TIME)
          + " needed for a fair substitute, so this track is out of the time sum.";
      }
      line.appendChild(label);
      if (!own || (own.missing && own.ms === null)) {
        line.appendChild(el("span", null, "—"));
      } else if (own.substituted) {
        const value = el("span", null, lapText(own.ms) + "  (substituted)");
        value.title = "No time on this track; scored with the slowest time in this class here.";
        line.appendChild(value);
      } else {
        const suffix = own.outOfTimeSum ? "  ·  not in the time sum" : "";
        const value = el("span", null, lapText(own.ms)
          + (own.pi == null ? "" : "  ·  PI " + own.pi)
          + "  ·  " + own.pos + "/" + own.of
          + "  ·  " + num(own.points) + " P  ·  " + num(own.count)
          + (own.count === 1 ? " entry" : " entries") + suffix);
        value.title = nth(own.took) + " fastest of " + own.count + " laps, best position "
          + num(own.bestRank)
          + (own.pi == null ? "" : ". PI " + own.pi + " is the PI the car had on that lap");
        line.appendChild(value);
      }
      card.appendChild(line);
    });
    cards.appendChild(card);
  });

  if (!found) {
    host.appendChild(emptyBox("This car does not appear in the current selection.",
      " Pick other tracks or filters."));
    return;
  }
  host.appendChild(cards);
}

/* ---------- view: time spread ---------- */
// Which track the spread view is looking at, and the lap the reader typed in. Both live
// outside the filter object because they belong to this view alone.
let SPREAD_TRACK = null;
let SPREAD_MS = null;

function parseLap(text) {
  // Accepts 1:23.456, 83.456 and 1:23 -- whatever someone reads off their own screen.
  const clean = String(text || "").trim().replace(",", ".");
  if (!clean) return null;
  const m = clean.match(/^(?:(\d+):)?(\d+(?:\.\d+)?)$/);
  if (!m) return null;
  const minutes = m[1] ? parseInt(m[1], 10) : 0;
  const seconds = parseFloat(m[2]);
  if (!isFinite(seconds)) return null;
  return Math.round((minutes * 60 + seconds) * 1000);
}

function fasterShare(dist, ms) {
  // Share of the field faster than this lap, from the binned counts. Bin resolution is
  // plenty here: a bin spans a few hundredths of a second.
  let faster = 0;
  for (let i = 0; i < dist.counts.length; i++) {
    const binStart = dist.low + i * dist.width;
    if (binStart + dist.width <= ms) faster += dist.counts[i];
    else if (binStart < ms) faster += dist.counts[i] / 2;
  }
  return faster / dist.n;
}

function spreadChart(dist, mine) {
  const W = 640, H = 150, PAD = 22;
  const bars = dist.counts.length;
  const barW = (W - PAD * 2) / bars;
  const peak = Math.max.apply(null, dist.counts) || 1;
  const NS = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("viewBox", "0 0 " + W + " " + H);
  svg.setAttribute("class", "spreadsvg");
  svg.setAttribute("role", "img");
  const xOf = ms => PAD + Math.max(0, Math.min(bars, (ms - dist.low) / dist.width)) * barW;

  dist.counts.forEach((count, i) => {
    const h = Math.round((count / peak) * (H - PAD - 18));
    const r = document.createElementNS(NS, "rect");
    r.setAttribute("x", (PAD + i * barW).toFixed(2));
    r.setAttribute("y", (H - 18 - h).toFixed(2));
    r.setAttribute("width", Math.max(1, barW - 0.6).toFixed(2));
    r.setAttribute("height", String(h));
    r.setAttribute("class", "spreadbar");
    svg.appendChild(r);
  });

  // Percentile cuts are annotations, not a second axis: the question is where the
  // boundary sits, not how two scales compare.
  const cuts = [["1", "top 1%"], ["5", "5%"], ["10", "10%"], ["50", "median"]];
  cuts.forEach(pair => {
    const ms = dist.pct[pair[0]];
    if (ms === undefined) return;
    const x = xOf(ms);
    const line = document.createElementNS(NS, "line");
    line.setAttribute("x1", String(x));
    line.setAttribute("x2", String(x));
    line.setAttribute("y1", "4");
    line.setAttribute("y2", String(H - 18));
    line.setAttribute("class", pair[0] === "50" ? "spreadmid" : "spreadcut");
    svg.appendChild(line);
    const t = document.createElementNS(NS, "text");
    t.setAttribute("x", String(x + 3));
    t.setAttribute("y", "12");
    t.setAttribute("class", "spreadlabel");
    t.textContent = pair[1];
    svg.appendChild(t);
  });

  if (mine !== null && mine !== undefined) {
    const x = xOf(mine);
    const line = document.createElementNS(NS, "line");
    line.setAttribute("x1", String(x));
    line.setAttribute("x2", String(x));
    line.setAttribute("y1", "0");
    line.setAttribute("y2", String(H - 18));
    line.setAttribute("class", "spreadmine");
    svg.appendChild(line);
  }

  const left = document.createElementNS(NS, "text");
  left.setAttribute("x", String(PAD));
  left.setAttribute("y", String(H - 4));
  left.setAttribute("class", "spreadaxis");
  left.textContent = lapText(dist.low) + " (fastest)";
  svg.appendChild(left);
  const right = document.createElementNS(NS, "text");
  right.setAttribute("x", String(W - PAD));
  right.setAttribute("y", String(H - 4));
  right.setAttribute("text-anchor", "end");
  right.setAttribute("class", "spreadaxis");
  right.textContent = lapText(dist.low + bars * dist.width);
  svg.appendChild(right);
  return svg;
}

function renderSpread(host) {
  const withDist = D.boards.filter(b => b.dist);
  if (!withDist.length) {
    host.appendChild(emptyBox("No board has enough laps for a distribution.", ""));
    return;
  }
  const tracks = [];
  withDist.forEach(b => {
    const t = D.tracks[b.t];
    if (tracks.indexOf(t) < 0) tracks.push(t);
  });
  tracks.sort();
  if (!SPREAD_TRACK || tracks.indexOf(SPREAD_TRACK) < 0) SPREAD_TRACK = tracks[0];

  const chooser = el("div", "frow");
  chooser.appendChild(el("span", "label", "Track"));
  const chips = el("div", "frow");
  tracks.forEach(track => {
    const b = el("button", "chip", track);
    b.type = "button";
    b.setAttribute("aria-pressed", track === SPREAD_TRACK ? "true" : "false");
    b.onclick = () => { SPREAD_TRACK = track; render(); };
    chips.appendChild(b);
  });
  chooser.appendChild(chips);
  host.appendChild(chooser);

  const cards = el("div", "cards");

  const mineRow = el("div", "frow");
  mineRow.appendChild(el("span", "label", "Your lap"));
  const input = el("input");
  input.type = "search";
  input.placeholder = "e.g. 1:23.456";
  input.value = SPREAD_MS === null ? "" : lapText(SPREAD_MS);
  input.oninput = () => {
    const parsed = parseLap(input.value);
    if (parsed !== SPREAD_MS) { SPREAD_MS = parsed; renderSpreadCards(cards); }
  };
  mineRow.appendChild(input);
  mineRow.appendChild(el("span", "readout", "typed once, compared against every class"));
  host.appendChild(mineRow);

  host.appendChild(cards);
  renderSpreadCards(cards);
}

function renderSpreadCards(cards) {
  cards.textContent = "";
  const boards = D.boards
    .filter(b => b.dist && D.tracks[b.t] === SPREAD_TRACK)
    .sort((a, b) => CLASS_ORDER.indexOf(D.classes[a.k]) - CLASS_ORDER.indexOf(D.classes[b.k]));

  boards.forEach(board => {
    const dist = board.dist;
    const card = el("div", "card");
    const head = el("div", "top");
    head.appendChild(el("span", "kbadge", D.classes[board.k]));
    head.appendChild(el("b", null, num(dist.n) + " laps"));
    head.appendChild(el("span", "sub", "fastest " + lapText(dist.low)
      + " · median " + lapText(dist.pct["50"])));
    card.appendChild(head);
    card.appendChild(spreadChart(dist, SPREAD_MS));

    const legend = el("div", "sub2");
    [["1", "top 1%"], ["5", "top 5%"], ["10", "top 10%"], ["25", "top 25%"]].forEach(pair => {
      const ms = dist.pct[pair[0]];
      if (ms === undefined) return;
      const span = el("span", "cut");
      span.appendChild(el("b", null, pair[1]));
      span.appendChild(document.createTextNode(" faster than " + lapText(ms)));
      legend.appendChild(span);
    });
    card.appendChild(legend);

    if (SPREAD_MS !== null) {
      const share = fasterShare(dist, SPREAD_MS);
      const pct = share * 100;
      const line = el("div", "mine");
      if (SPREAD_MS < dist.low) {
        line.appendChild(el("b", null, "faster than the whole field"));
        line.appendChild(document.createTextNode(" — " + lapText(SPREAD_MS)
          + " beats the board's best lap"));
      } else {
        const band = pct <= 1 ? "top 1%" : pct <= 5 ? "top 5%" : pct <= 10 ? "top 10%"
          : pct <= 25 ? "top 25%" : pct <= 50 ? "top half" : "bottom half";
        line.appendChild(el("b", null, band));
        line.appendChild(document.createTextNode(" — " + lapText(SPREAD_MS)
          + " would place about " + num(Math.max(1, Math.round(share * dist.n)))
          + " of " + num(dist.n) + " (" + pct.toFixed(1) + "%)"));
      }
      card.appendChild(line);
    }
    cards.appendChild(card);
  });

  // A chart is not the only way to read this, so the same numbers stand as a table.
  const wrap = el("div", "tablewrap");
  const table = el("table");
  const head = el("tr");
  ["Class", "Laps", "Fastest", "top 1%", "top 5%", "top 10%", "Median", "top 90%"]
    .forEach(text => head.appendChild(el("th", null, text)));
  table.appendChild(head);
  boards.forEach(board => {
    const dist = board.dist;
    const tr = el("tr");
    tr.appendChild(el("td", null, D.classes[board.k]));
    tr.appendChild(el("td", "num", num(dist.n)));
    ["low", "1", "5", "10", "50", "90"].forEach(key => {
      const ms = key === "low" ? dist.low : dist.pct[key];
      tr.appendChild(el("td", "num mono", ms === undefined ? "—" : lapText(ms)));
    });
    table.appendChild(tr);
  });
  wrap.appendChild(table);
  cards.appendChild(wrap);
}

/* ---------- view: scan status ---------- */
/* ---------- view: most contested boards ----------
   Welche Strecke und Klasse am umkaempftesten ist, kann man an den gescannten Zeilen
   NICHT ablesen: gescannt werden die ersten 20.000 Raenge, und danach sieht ein Board mit
   787.000 Eintraegen genauso aus wie eines mit 21.000. Die Groesse kommt aus dem
   Scrollbalken -- seine Position bei bekanntem Rang gibt die Gesamtlaenge.
   Deshalb steht hier die SCHAETZUNG vorn und die gescannte Tiefe daneben: die zweite Zahl
   sagt, wie viel davon wir tatsaechlich gelesen haben. */
function renderContest(host) {
  const { need, forbid } = masks();
  const boards = boardsInScope().filter(b => !F.cls.size || F.cls.has(D.classes[b.k]));
  const rows = boards.filter(b => (b.impl || 0) > 0).map(b => ({
    track: D.tracks[b.t],
    klass: D.classes[b.k],
    impl: b.impl || 0,
    scanned: b.maxRank || 0,
    laps: b.valid || 0,
    // The sample this board contributes right now: rows that survive every filter,
    // car filters included. With one car searched, it reads as "that car is in this
    // leaderboard N times".
    entries: entriesOnBoard(b, need, forbid, true),
    share: (b.impl || 0) > 0 ? (b.maxRank || 0) / b.impl : 0,
    est: b.iest || "",
  }));
  const unmeasured = boards.length - rows.length;
  if (!rows.length) {
    host.appendChild(emptyBox("No board length has been measured in this selection.",
      " Length comes from the scrollbar during a scan, so only scanned boards carry it."));
    return;
  }

  const note = el("div", "context");
  note.appendChild(el("b", null, "These are estimates, not counts. "));
  note.appendChild(document.createTextNode(
    "A board's size is read off the scrollbar: its position at a known rank gives the total. "
    + "Two independent runs on Hokubu S1 landed on 413,491 and 413,392 — 0.02% apart — "
    + "so the slope method over many points is solid. A single point is weaker, because the "
    + "scrollbar thumb has a minimum height that only the slope cancels out. "
    + (unmeasured ? unmeasured + " board(s) in this selection carry no measurement and are left out." : "")));
  host.appendChild(note);

  const sort = SORT.contest;
  const by = {
    track: r => r.track,
    klass: r => CLASS_ORDER.indexOf(r.klass),
    impl: r => r.impl,
    scanned: r => r.scanned,
    share: r => r.share,
    laps: r => r.laps,
    entries: r => r.entries,
  }[sort.key] || (r => r.impl);
  rows.sort((a, b) => {
    const va = by(a), vb = by(b);
    if (va < vb) return -sort.dir;
    if (va > vb) return sort.dir;
    return b.impl - a.impl;
  });

  const biggest = rows.reduce((m, r) => Math.max(m, r.impl), 1);
  const cols = [
    { head: "#" },
    { head: "Track", key: "track" },
    { head: "Class", key: "klass" },
    { head: "Estimated entries", key: "impl", num: true },
    { head: "" },
    { head: "Scanned to rank", key: "scanned", num: true },
    { head: "Covered", key: "share", num: true },
    { head: "Entries behind filters", key: "entries", num: true, desc: true },
    { head: "Laps kept", key: "laps", num: true },
    { head: "Estimate" },
  ];
  const wrap = el("div", "tablewrap");
  const tableEl = el("table");
  const thead = el("thead");
  const hrow = el("tr");
  head(hrow, cols, "contest");
  thead.appendChild(hrow);
  tableEl.appendChild(thead);
  const tbody = el("tbody");
  rows.forEach((r, i) => {
    const tr = el("tr");
    tr.appendChild(el("td", "num", String(i + 1)));
    tr.appendChild(el("td", null, r.track));
    tr.appendChild(el("td", null, r.klass));
    tr.appendChild(el("td", "num", num(r.impl)));
    tr.appendChild(gapCell(r.impl, biggest, "",
      "Estimated total entries, relative to the biggest board in this selection"));
    tr.appendChild(el("td", "num", num(r.scanned)));
    tr.appendChild(el("td", "num", (100 * r.share).toFixed(1) + "%"));
    const ent = el("td", "num", num(r.entries));
    ent.title = "Rows on this board that survive the current filters, car filters"
      + " included, within the scanned depth. This is the sample the rankings for this"
      + " track rest on.";
    tr.appendChild(ent);
    tr.appendChild(el("td", "num", num(r.laps)));
    tr.appendChild(el("td", null, r.est === "slope" ? "slope, solid"
      : r.est === "single_point" ? "single point, weak" : r.est || "—"));
    tbody.appendChild(tr);
  });
  tableEl.appendChild(tbody);
  wrap.appendChild(tableEl);
  host.appendChild(wrap);

  // Summen. Eine Strecke ist umkaempft, wenn ueber ALLE Klassen viel gefahren wird, und
  // eine Klasse, wenn sie es ueber alle Strecken tut -- beides ist aus der Tabelle oben
  // nicht ablesbar, weil dort jede Zeile ein einzelnes Board ist.
  const sumBy = (keyOf) => {
    const acc = new Map();
    rows.forEach(r => {
      const key = keyOf(r);
      const cur = acc.get(key) || { total: 0, boards: 0, entries: 0 };
      cur.total += r.impl; cur.boards += 1; cur.entries += r.entries;
      acc.set(key, cur);
    });
    return Array.from(acc.entries()).sort((a, b) => b[1].total - a[1].total);
  };
  [["By track, summed over its classes", sumBy(r => r.track)],
   ["By class, summed over its tracks", sumBy(r => r.klass)]].forEach(([title, list]) => {
    host.appendChild(el("h3", null, title));
    const top = list[0] ? list[0][1].total : 1;
    const w2 = el("div", "tablewrap");
    const t2 = el("table");
    const b2 = el("tbody");
    list.forEach(([name, agg], i) => {
      const tr = el("tr");
      tr.appendChild(el("td", "num", String(i + 1)));
      tr.appendChild(el("td", null, name));
      tr.appendChild(el("td", "num", num(agg.total)));
      tr.appendChild(gapCell(agg.total, top, "", null));
      tr.appendChild(el("td", "num", agg.boards + (agg.boards === 1 ? " board" : " boards")));
      const ent = el("td", "num", num(agg.entries) + " entries");
      ent.title = "Rows behind the current filters across these boards -- the sample,"
        + " next to the estimate. The estimate says how big the leaderboards are; this"
        + " says how much of them we hold.";
      tr.appendChild(ent);
      b2.appendChild(tr);
    });
    t2.appendChild(b2); w2.appendChild(t2); host.appendChild(w2);
  });
}

function renderScans(host) {
  const scans = (D.meta && D.meta.scans) || [];
  const known = (D.meta && D.meta.known_routes) || {};
  const have = {};
  D.boards.forEach(b => { have[D.tracks[b.t] + "|" + D.classes[b.k]] = true; });

  const routes = [];
  Object.keys(known).forEach(cat => {
    (known[cat] || []).forEach(name => {
      // The catalogue's unconfirmed list carries prose alongside names; a route name is
      // short and has no sentence punctuation.
      if (name.length <= 40 && name.indexOf(",") < 0 && routes.indexOf(name) < 0) {
        routes.push(name);
      }
    });
  });
  D.tracks.forEach(t => { if (routes.indexOf(t) < 0) routes.push(t); });
  routes.sort();

  const open = [];
  routes.forEach(track => {
    const missing = CLASS_ORDER.filter(k => !have[track + "|" + k]);
    if (missing.length) open.push([track, missing]);
  });
  const openCount = open.reduce((sum, entry) => sum + entry[1].length, 0);

  const tiles = el("div", "tiles");
  [[num(D.boards.length), "boards with data"],
   [num(routes.length * CLASS_ORDER.length), "boards that could exist"],
   [num(scans.length), "scan runs recorded"],
   [num(openCount), "boards still open"]].forEach(pair => {
    const d = el("div", "tile");
    d.appendChild(el("b", null, pair[0]));
    d.appendChild(el("span", "sub", pair[1]));
    tiles.appendChild(d);
  });
  host.appendChild(tiles);

  host.appendChild(el("h3", null, "What has been read"));
  const wrap = el("div", "tablewrap");
  const table = el("table");
  const head = el("tr");
  ["When", "Track", "Class", "Read by", "Rows", "Deepest rank", "Coverage", "Run ended"]
    .forEach(text => head.appendChild(el("th", null, text)));
  table.appendChild(head);
  scans.forEach(scan => {
    const tr = el("tr");
    tr.appendChild(el("td", "mono", String(scan.when || "").replace("T", " ")));
    tr.appendChild(el("td", null, scan.t));
    tr.appendChild(el("td", null, scan.k));
    tr.appendChild(el("td", null, scan.src === "ocr" ? "screen" : "memory"));
    tr.appendChild(el("td", "num", num(scan.rows)));
    tr.appendChild(el("td", "num", num(scan.maxRank)));
    const cov = scan.maxRank ? (100 * scan.rows / scan.maxRank) : 0;
    tr.appendChild(el("td", "num", scan.maxRank ? cov.toFixed(1) + "%" : "—"));
    tr.appendChild(el("td", "small", scan.status));
    table.appendChild(tr);
  });
  wrap.appendChild(table);
  host.appendChild(wrap);

  host.appendChild(el("h3", null, "What is still open"));
  if (!open.length) {
    host.appendChild(el("p", "sub", "Every class of every known route has data."));
  } else {
    const list = el("div", "cards");
    open.forEach(entry => {
      const card = el("div", "card");
      card.appendChild(el("b", null, entry[0]));
      card.appendChild(el("span", "sub", "missing: " + entry[1].join(", ")));
      list.appendChild(card);
    });
    host.appendChild(list);
  }
  host.appendChild(el("p", "caveat",
    "A run marked truncated stopped before the board's end. The two routes read a board "
    + "differently: a memory run carries the game's own car ids and reaches the deepest "
    + "ranks, a screen run carries the car names those ids are matched against."));
}

/* ---------- chrome ---------- */
const TABS = [
  ["points", "Points ranking"],
  ["time", "Time sum"],
  ["boards", "Single boards"],
  ["car", "Car detail"],
  ["spread", "Time spread"],
  ["contest", "Most contested"],
  ["scans", "Scan status"],
];

function renderTabs() {
  const host = document.getElementById("tabs");
  host.textContent = "";
  TABS.forEach(([key, text]) => {
    const b = el("button", "tab", text);
    b.type = "button";
    b.setAttribute("role", "tab");
    b.setAttribute("aria-selected", TAB === key ? "true" : "false");
    b.onclick = () => { TAB = key; render(); };
    host.appendChild(b);
  });
}

function renderTiles() {
  const boards = boardsInScope();
  const { need, forbid } = masks();
  let laps = 0;
  boards.forEach(board => { laps += entriesOnBoard(board, need, forbid, false); });
  const cars = new Set();
  boards.forEach(board => {
    for (let g = 0; g < board.gsig.length; g++) {
      const sig = board.gsig[g];
      if (((sig & need) === need) && ((sig & forbid) === 0)) cars.add(board.gcar[g]);
    }
  });
  const tiles = [
    [num(boards.length), boards.length === 1 ? "Board" : "Boards"],
    [num(selectedClasses().length), "performance classes"],
    [num(cars.size), "cars"],
    [num(laps), "entries behind the filters"],
  ];
  const host = document.getElementById("tiles");
  host.textContent = "";
  tiles.forEach(([n, sub]) => {
    const d = el("div", "tile");
    d.appendChild(el("span", "n", n));
    d.appendChild(el("span", "sub", sub));
    host.appendChild(d);
  });
}

function renderContext() {
  const host = document.getElementById("context");
  host.textContent = "";
  const boards = boardsInScope().filter(b => !F.cls.size || F.cls.has(D.classes[b.k]));
  const scanned = boards.reduce((sum, b) => sum + (b.valid || b.rows), 0);
  const deepest = boards.reduce((max, b) => Math.max(max, b.maxRank), 0);
  [["Entries read", num(scanned)],
   ["Deepest rank", num(deepest)],
   ["Tracks", String(new Set(boards.map(b => D.tracks[b.t])).size)]].forEach(([k, v]) => {
    const span = el("span", null, k + ": ");
    span.appendChild(el("b", null, v));
    host.appendChild(span);
  });
  const invalid = boards.reduce((sum, b) => sum + (b.invalid || 0), 0);
  if (invalid) {
    const span = el("span", null, "Invalid laps: ");
    span.appendChild(el("b", null, num(invalid)));
    span.title = F.clean === "valid"
      ? "Not scored, because the lap filter is set to “valid only”."
      : "Included by the current filter.";
    host.appendChild(span);
  }
  const shallow = boards.filter(b => b.valid < 1000).length;
  if (shallow) {
    const flag = el("span", "flag", shallow + " board(s) only partly captured");
    flag.title = "The sweep stopped short on these boards; they hold fewer than 1000 entries.";
    host.appendChild(flag);
  }
}

function renderRuleNote() {
  const host = document.getElementById("rulenote");
  host.textContent = "";
  const klass = classLabel(selectedClasses());
  const bits = [];
  if (F.clean === "valid") bits.push("valid laps only");
  if (F.clean === "invalid") bits.push("invalid laps only");
  if (F.gear === "auto") bits.push("Carmatic");
  if (F.gear === "manual") bits.push("manual without clutch");
  if (F.gear === "clutch") bits.push("Clutch + manual");
  ASSISTS.forEach(([key, name]) => {
    if (F.assist[key] === "with") bits.push("with " + name);
    if (F.assist[key] === "without") bits.push("without " + name);
  });
  const excluded = (TAB === "time")
    ? (scoreTable(selectedClasses()).shallowTracks || []) : [];
  const what = {
    points: "points per track, summed",
    time: "sum of the chosen lap times",
    boards: "one row per car and board",
    car: "one car across every class",
  }[TAB];
  host.appendChild(document.createTextNode(what + " · class "));
  host.appendChild(el("b", null, TAB === "car" ? "all" : klass));
  if (bits.length) {
    host.appendChild(document.createTextNode(" · filters: "));
    host.appendChild(el("b", null, bits.join(", ")));
  } else {
    host.appendChild(document.createTextNode(" · no lap filters"));
  }
  if (excluded.length) {
    host.appendChild(document.createTextNode(" · out of the time sum: "));
    const names = el("b", null, excluded.join(", "));
    names.title = "Fewer than " + num(MIN_BOARD_FOR_TIME)
      + " entries scanned, so this board has no meaningful slowest car to stand in.";
    host.appendChild(names);
  }
}

function render() {
  multiChips(document.getElementById("f-cat"), D.categories.slice().sort(), F.cat);
  multiChips(document.getElementById("f-cls"), classesInScope(), F.cls, null, "chip k");
  multiChips(document.getElementById("f-trk"), D.tracks.slice().sort(), F.tracks);

  const cleanHost = document.getElementById("f-clean");
  cleanHost.textContent = "";
  triGroup(cleanHost, null, [["any", "All"], ["valid", "valid only"], ["invalid", "invalid only"]],
    () => F.clean, v => { F.clean = v; });

  const gearHost = document.getElementById("f-gear");
  gearHost.textContent = "";
  triGroup(gearHost, null,
    [["any", "All"], ["auto", "Carmatic"], ["manual", "Manual"], ["clutch", "Clutch + manual"]],
    () => F.gear, v => { F.gear = v; });

  multiChips(document.getElementById("f-tune"), ["down", "same", "up"], F.tune,
    value => TUNE_LABEL[value] || value);
  multiChips(document.getElementById("f-make"), metaValues("make"), F.make);
  multiChips(document.getElementById("f-country"), metaValues("country"), F.country);
  multiChips(document.getElementById("f-cartype"), metaValues("type"), F.carType);
  renderYearRange();

  const assistHost = document.getElementById("f-assists");
  assistHost.textContent = "";
  ASSISTS.forEach(([key, name]) => {
    triGroup(assistHost, name, [["any", "—"], ["with", "with"], ["without", "without"]],
      () => F.assist[key], v => { F.assist[key] = v; });
  });

  renderTabs();
  renderTiles();
  renderContext();
  renderRuleNote();

  const content = document.getElementById("content");
  content.textContent = "";
  if (TAB === "points") renderScoreboard(content, "points");
  else if (TAB === "time") renderScoreboard(content, "time");
  else if (TAB === "boards") renderBoards(content);
  else if (TAB === "spread") renderSpread(content);
  else if (TAB === "contest") renderContest(content);
  else if (TAB === "scans") renderScans(content);
  else renderCar(content);
}

(function init() {
  const meta = D.meta || {};
  document.getElementById("quality").textContent =
    D.boards.length + " boards, " + num(meta.raw_rows || 0) + " entries read, "
    + num(meta.kept_laps || 0) + " laps in this page (the "
    + (meta.laps_per_group || 5) + " fastest per car and assist combination, as deep as the rule ever reaches), "
    + num(meta.invalid_rows || 0) + " of them invalid laps ("
    + (meta.invalid_dropped ? "removed from the page" : "present, but not scored by default") + ").";
  document.getElementById("caveat").textContent =
    "Car names resolve for only " + Math.round(100 * (meta.named_cars || 0) / Math.max(1, meta.cars || 1))
    + " % of cars (" + num(meta.named_cars || 0) + " of " + num(meta.cars || 0)
    + " cars): the car catalogue on disk only covers " + num(meta.catalogue_ids || 0)
    + " ids. Unnamed cars show as “Car #ID” and stay searchable and filterable by that id. "
    + "Guessed names would be worse than none.";

  document.getElementById("f-car").addEventListener("input", event => {
    F.car = event.target.value;
    if (TAB === "car") FOCUS_CAR = null;
    render();
  });
  // The handles must not cross: whichever is dragged past the other pushes it along,
  // which is what every native-feeling range control does.
  const yearFrom = document.getElementById("year-from");
  const yearTo = document.getElementById("year-to");
  const bounds = yearBounds();
  if (bounds) {
    // The inputs carry indices into yearStops(); F still holds real years.
    yearFrom.addEventListener("input", () => {
      const stops = yearStops();
      let value = stops[parseInt(yearFrom.value, 10)];
      const other = F.yearTo === null ? bounds[1] : F.yearTo;
      if (value > other) value = other;
      F.yearFrom = value <= bounds[0] ? null : value;
      render();
    });
    yearTo.addEventListener("input", () => {
      const stops = yearStops();
      let value = stops[parseInt(yearTo.value, 10)];
      const other = F.yearFrom === null ? bounds[0] : F.yearFrom;
      if (value < other) value = other;
      F.yearTo = value >= bounds[1] ? null : value;
      render();
    });
  }
  document.getElementById("year-reset").addEventListener("click", () => {
    F.yearFrom = null;
    F.yearTo = null;
    render();
  });
  document.getElementById("f-reset").addEventListener("click", () => {
    F.cat.clear(); F.cls.clear(); F.tracks.clear(); F.clean = "any"; F.gear = "any";
    F.make.clear(); F.country.clear(); F.carType.clear(); F.tune.clear();
    F.yearFrom = null; F.yearTo = null;
    ASSISTS.forEach(([key]) => { F.assist[key] = "any"; });
    F.car = ""; FOCUS_CAR = null;
    document.getElementById("f-car").value = "";
    selectDefaultClass();
    render();
  });
  selectDefaultClass();
  render();

  // Eingereichte Zeiten nachladen. ERST zeichnen, dann holen: die Seite soll
  // nicht auf den Server warten, und ohne Server steht sie da wie vorher.
  // DEN RUMPF IMMER LESEN, auch bei einem Fehler: eine Antwort, deren Rumpf niemand
  // liest, haelt Chrome offen -- die Anfrage wird nie fertig, und wer auf ein ruhiges
  // Netz wartet (test_no_third_party.py), wartet ewig. So war es auf jedem Server mit
  // abgeschalteter Einreichung (404), bis 2026-09-27.
  fetch("/api/lap/list", { cache: "no-store" })
    .then(r => r.json().then(d => (r.ok ? d : { laps: [] }), () => ({ laps: [] })))
    .then(d => {
      SUBMITTED = buildSubmitted(D, d.laps || []);
      const hinweis = document.getElementById("submitted-note");
      if (hinweis && (SUBMITTED.placed || SUBMITTED.unplaced.length)) {
        hinweis.hidden = false;
        hinweis.textContent = SUBMITTED.placed + " submitted lap time(s) included"
          + (SUBMITTED.newCars ? ", " + SUBMITTED.newCars + " car(s) not yet on any leaderboard" : "")
          + (SUBMITTED.unplaced.length
             ? ". " + SUBMITTED.unplaced.length + " could not be placed ("
               + SUBMITTED.unplaced.slice(0, 3).map(u => u.reason).join("; ") + ")"
             : "")
          + ". Marked submitted; not verified by the game.";
      }
      render();
    })
    .catch(() => { /* ohne Server: nichts zu tun */ });
})();
</script>
"""


def fonts_css_inline() -> str:
    """server/fonts/fonts.css mit den Schriften als data:-Adressen."""
    import base64
    import re
    ordner = Path(__file__).resolve().parent.parent / "server" / "fonts"
    css = (ordner / "fonts.css").read_text(encoding="utf-8")

    def ersetze(m):
        daten = (ordner / m.group(1)).read_bytes()
        return 'url("data:font/woff2;base64,%s")' % base64.b64encode(daten).decode("ascii")

    return re.sub(r'url\("/fonts/([a-z0-9-]+\.woff2)"\)', ersetze, css)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path,
                        default=Path("data/memory_scans/full_sweep"))
    parser.add_argument("--out", type=Path,
                        default=Path("data/analytics/rivals_auto_wertung.html"))
    parser.add_argument("--drop-invalid", action="store_true",
                        help="leave invalid laps out of the page entirely")
    parser.add_argument("--data-out", type=Path,
                        default=Path("data/analytics/laps.json"),
                        help="also keep the raw payload; useful for diffing builds")
    parser.add_argument("--from-data", type=Path, default=None,
                        help="build the PAGE from an existing payload (laps.json) "
                             "instead of rescanning every board -- for changes to "
                             "the page itself, which otherwise wait an hour")
    args = parser.parse_args(argv)

    if args.from_data:
        packed = args.from_data.read_text(encoding="utf-8")
        payload = json.loads(packed)
        args.data_out = None
    else:
        payload = dataset.build(args.root, drop_invalid=args.drop_invalid)
        packed = json.dumps(payload, separators=(",", ":"))
    if args.data_out:
        args.data_out.parent.mkdir(parents=True, exist_ok=True)
        args.data_out.write_text(packed, encoding="utf-8")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    # Die reinen Funktionen fuer eingereichte Zeiten liegen als eigene Datei
    # (scripts/submitted_laps.js), damit node sie pruefen kann. Hier werden sie
    # UNVERAENDERT eingesetzt -- geprueft wird, was ausgeliefert wird.
    eingereicht = (Path(__file__).resolve().parent / "submitted_laps.js").read_text(
        encoding="utf-8")
    seite = PAGE.replace("__SUBMITTED_JS__", eingereicht)
    # DIE SCHRIFTEN EINGEBETTET, nicht von fonts.googleapis.com: dort schickte jeder
    # Besucher seine IP-Adresse an Google (DSGVO). Als data:-Adressen bleibt die
    # Seite eine einzige Datei, die auch ohne Server lesbar ist.
    seite = seite.replace("__FONTS_CSS__", fonts_css_inline())
    # Die Kontaktadresse steht nicht im Quelltext, sondern in config/local.json.
    # Der Name der Seite ebenso (config/local.json, "site_title").
    import html as _html
    seite = seite.replace("__SITE_TITLE__", _html.escape(local_settings.site_title()))
    kontakt = local_settings.contact_email()
    seite = seite.replace("__CONTACT__", f' Contact: <a href="mailto:{kontakt}">{kontakt}</a>.' if kontakt else "")
    # Der Quelltext, oben bei den Wegen und unten am Ende -- nur mit Eintrag
    # ("source_url" in config/local.json): ein privates Repository zeigt Besuchern
    # eine 404, und dann ist kein Link besser als einer.
    quelle = local_settings.source_url()
    q = _html.escape(quelle, quote=True)
    seite = seite.replace("__SOURCE_WAY__", (
        f'\n      <span class="sep">&middot;</span>\n      <a href="{q}" rel="noopener noreferrer">'
        'Source code on GitHub</a>') if quelle else "")
    seite = seite.replace("__SOURCE_END__", (
        f' &middot; <a href="{q}" rel="noopener noreferrer">Source code on GitHub</a>') if quelle else "")
    args.out.write_text(seite.replace("__PAYLOAD__", packed), encoding="utf-8")

    meta = payload["meta"]
    # Die beiden Zahlen haben VERSCHIEDENE Nenner und duerfen nicht voneinander
    # abgezogen werden: kept_laps ist bei LAPS_PER_GROUP je (Auto, Signatur) gedeckelt,
    # invalid_rows zaehlt ungedeckelt jede ungueltige Zeile. Nebeneinander gedruckt sahen
    # sie am 2026-08-27 nach "nur noch 2.234 gueltige Runden" aus; in Wahrheit war es
    # ein Board mit 12.432 gueltigen und 5.723 ungueltigen Zeilen.
    print(f"{len(payload['boards'])} boards, {meta['kept_laps']} laps kept "
          f"(max {meta['laps_per_group']}/group), "
          f"{meta['invalid_rows']} invalid rows scanned, "
          f"{meta['named_cars']}/{meta['cars']} cars named, "
          f"{args.out.stat().st_size / 1024:.0f} KB -> {args.out}")
    if args.data_out:
        refresh_haptics_package(args.data_out)
    return 0


def refresh_haptics_package(dataset_path: Path | None) -> None:
    """Den Datenbestand im weitergebbaren Haptik-Paket mitziehen.

    Das Paket (`dist/fh-companion/`) liefert die Runden als Datei mit, damit es
    ohne Server nutzbar ist -- und wird damit in dem Moment alt, in dem hier ein neuer
    Bestand entsteht. Von Hand nachziehen heisst: es passiert nicht, und irgendwann
    gibt jemand eine ZIP mit dem Stand von vor drei Wochen weiter.

    `--data-only` tauscht nur die JSON und zippt neu, rund zwanzig Sekunden; die
    Binaerdateien bleiben liegen. Gibt es noch kein Paket, passiert nichts.

    Fehler hier duerfen den Seitenbau nicht umbringen: die Seite ist das Hauptprodukt,
    das Paket ein Abfallprodukt davon.
    """
    if dataset_path is None:
        return
    # Nicht relativ zum Arbeitsverzeichnis: der Seitenbau wird auch aus dem Sweep
    # heraus gestartet, und ein anderer Startordner darf nicht heissen "kein Paket da".
    scripts = Path(__file__).resolve().parent
    package = scripts.parent / "dist" / "fh-companion" / "app" / "FH Companion.exe"
    if not package.exists():
        return
    try:
        done = subprocess.run(
            [sys.executable, str(scripts / "build_haptics_package.py"), "--data-only"],
            capture_output=True, text=True, timeout=900)
        tail = (done.stdout or done.stderr or "").strip().splitlines()[-1:] or [""]
        print(f"haptics package: {tail[0].strip()}"
              if done.returncode == 0 else
              f"haptics package NICHT aufgefrischt: {tail[0].strip()}")
    except Exception as error:
        print(f"haptics package NICHT aufgefrischt: {error}")


if __name__ == "__main__":
    raise SystemExit(main())
