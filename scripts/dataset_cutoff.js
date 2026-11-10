/* Der Stichtag fuer die Rivals-Daten: nur Runden, die seit diesem Tag gescannt wurden.
 *
 * Eine reine Funktion ohne DOM, damit sie sich mit node pruefen laesst -- die Seite
 * bettet diese Datei unveraendert ein (build_analytics_site.py), wie submitted_laps.js.
 *
 * Am 2026-11-03 hat das Spiel Fahrzeuge abgeschwaecht und seine Rivals-Bestenlisten
 * geloescht. Die Seite zeigt darum standardmaessig nur, was seit dem Stichtag gescannt
 * wurde (D.meta.since_default); "All data" zeigt alles wie vorher.
 *
 * DIESELBE RECHNUNG wie server/stichtag.py (board_ab, zuschneiden) und
 * RivalsDataset.cs in der App. Das Format steht dort im Kopf der Datei.
 * scripts/test_dataset_cutoff.py vergleicht diese Datei mit der Python-Fassung.
 */

/* KEIN "use strict" auf oberster Ebene -- siehe submitted_laps.js. */

var CUTOFF_DEFAULT = "2026-11-03";
var CUTOFF_PER_DAY = ["lday", "gdays", "dstat", "ddist", "xgrp", "xms", "xrank", "xpi", "xday"];
var CUTOFF_PER_GROUP = 5;

function cutoffInt(list, i, fallback) {
  var v = list ? list[i] : undefined;
  return (typeof v === "number" && isFinite(v)) ? v : (fallback === undefined ? 0 : fallback);
}

/* Ein Board ab dem Stichtag `since` ("JJJJ-MM-TT"), oder null, wenn nichts bleibt.
 * since null/"" = alles, das Board selbst. Das alte Board bleibt unberuehrt.
 * perGroup: wie viele Runden je Gruppe bleiben (meta.laps_per_group). */
function boardSince(b, since, perGroup) {
  perGroup = perGroup || CUTOFF_PER_GROUP;
  if (!since) return b;
  var days = b.days;
  if (!Array.isArray(days) || !days.length) return null;
  var j0 = -1;
  for (var j = 0; j < days.length; j++) { if (String(days[j]) >= since) { j0 = j; break; } }
  if (j0 < 0) return null;
  var out = {};
  Object.keys(b).forEach(function (k) { if (CUTOFF_PER_DAY.indexOf(k) < 0) out[k] = b[k]; });
  out.days = days.slice(j0);
  if (Array.isArray(b.isz)) {
    var best = null;
    for (var s = 0; s < b.isz.length; s++) {
      var e = b.isz[s];
      if (Array.isArray(e) && e.length >= 3 && String(e[0]) >= since) { best = e; break; }
    }
    out.impl = best ? (Number(best[1]) || 0) : 0;
    out.iest = best ? String(best[2] || "") : "";
  }
  if (j0 === 0) return out;

  var r = days.length;
  var lday = b.lday, gdays = b.gdays, dstat = b.dstat;
  var gcar = b.gcar || [], gsig = b.gsig || [];
  if (!Array.isArray(lday) || !Array.isArray(gdays) || !Array.isArray(dstat)
      || gdays.length !== gcar.length * r || dstat.length !== 4 * r) return null;
  var rows = 0, valid = 0, invalid = 0, maxRank = 0;
  for (var d = j0; d < r; d++) {
    rows += cutoffInt(dstat, 4 * d); valid += cutoffInt(dstat, 4 * d + 1);
    invalid += cutoffInt(dstat, 4 * d + 2); maxRank = Math.max(maxRank, cutoffInt(dstat, 4 * d + 3));
  }
  if (rows <= 0) return null;
  out.rows = rows; out.valid = valid; out.invalid = invalid; out.maxRank = maxRank;
  out.dist = (Array.isArray(b.ddist) && j0 < b.ddist.length) ? b.ddist[j0] : null;

  var where = new Map();
  var ncar = [], nsig = [], ncount = [];
  for (var g = 0; g < gcar.length; g++) {
    var n = 0;
    for (var t = j0; t < r; t++) n += cutoffInt(gdays, g * r + t);
    if (n <= 0) continue;
    where.set(g, ncar.length);
    ncar.push(gcar[g]); nsig.push(g < gsig.length ? gsig[g] : 0); ncount.push(n);
  }
  // Die Runden ab dem Stichtag aus beiden Listen (l... und x...), je Gruppe die schnellsten.
  var cand = new Map();
  [["lgrp", "lms", "lrank", "lpi", "lday"], ["xgrp", "xms", "xrank", "xpi", "xday"]].forEach(function (k) {
    var grp = b[k[0]] || [], ms = b[k[1]] || [], rank = b[k[2]] || [];
    var pi = Array.isArray(b[k[3]]) ? b[k[3]] : null, day = b[k[4]] || [];
    for (var i = 0; i < grp.length; i++) {
      if (cutoffInt(day, i, -1) < j0 || !where.has(grp[i])) continue;
      var ng = where.get(grp[i]);
      if (!cand.has(ng)) cand.set(ng, []);
      var p = pi && i < pi.length ? pi[i] : null;
      cand.get(ng).push([cutoffInt(ms, i), cutoffInt(rank, i), p === undefined ? null : p]);
    }
  });
  var mgrp = [], mms = [], mrank = [], mpi = [];
  for (var ng2 = 0; ng2 < ncar.length; ng2++) {
    // Array.prototype.sort ist stabil: bei gleicher Zeit die aus l... zuerst, wie in Python.
    (cand.get(ng2) || []).slice().sort(function (a, z) { return a[0] - z[0]; }).slice(0, perGroup)
      .forEach(function (e) { mgrp.push(ng2); mms.push(e[0]); mrank.push(e[1]); mpi.push(e[2]); });
  }
  out.gcar = ncar; out.gsig = nsig; out.gcount = ncount;
  out.lgrp = mgrp; out.lms = mms; out.lrank = mrank;
  if (mpi.some(function (p) { return p !== null && p !== undefined; })) out.lpi = mpi;
  else delete out.lpi;
  return out;
}

/* Der ganze Datensatz ab dem Stichtag. Autos, Strecken, Klassen und Kategorien bleiben
 * DIESELBEN Listen (buildSubmitted haengt neue Autos an -- an beide Sichten zugleich);
 * Boards ohne Zeile ab dem Stichtag fehlen, ebenso aeltere Scans in meta.scans. */
function datasetSince(D, since) {
  if (!since) return D;
  var out = {};
  Object.keys(D).forEach(function (k) { out[k] = D[k]; });
  out.boards = [];
  var perGroup = (D.meta && typeof D.meta.laps_per_group === "number" && D.meta.laps_per_group >= 1)
    ? D.meta.laps_per_group : CUTOFF_PER_GROUP;
  (D.boards || []).forEach(function (b) {
    if (!b || typeof b !== "object") return;
    var s = boardSince(b, since, perGroup);
    if (s) out.boards.push(s);
  });
  if (D.meta && typeof D.meta === "object") {
    var meta = {};
    Object.keys(D.meta).forEach(function (k) { meta[k] = D.meta[k]; });
    meta.scans = (D.meta.scans || []).filter(function (s) {
      return s && String(s.when || "").slice(0, 10) >= since;
    });
    meta.since = since;
    out.meta = meta;
  }
  return out;
}

/* Die Vorgabe des Datensatzes: meta.since_default ("" = alles), sonst CUTOFF_DEFAULT. */
function defaultSince(D) {
  var m = D && D.meta;
  if (m && typeof m.since_default === "string") return m.since_default || null;
  return CUTOFF_DEFAULT;
}

/* Eingereichte Runden (/api/lap/list) ab dem Stichtag: "received" ist ein ISO-Zeitpunkt. */
function lapsSince(laps, since) {
  if (!since) return laps || [];
  return (laps || []).filter(function (e) {
    return e && String(e.received || "").slice(0, 10) >= since;
  });
}

if (typeof module !== "undefined") {
  module.exports = { boardSince: boardSince, datasetSince: datasetSince, defaultSince: defaultSince,
                     lapsSince: lapsSince, CUTOFF_DEFAULT: CUTOFF_DEFAULT };
}
