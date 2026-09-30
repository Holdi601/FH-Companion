/* Eingereichte Rundenzeiten in die Bestenlisten der Seite einrechnen.
 *
 * Eine reine Funktion ohne DOM, damit sie sich mit node pruefen laesst -- die Seite
 * selbst bettet diese Datei unveraendert ein (build_analytics_site.py).
 *
 * WARUM EINE EIGENE SCHICHT UND KEINE WEITERE ZEILE IM BOARD
 *
 * pickCars nimmt mit Absicht NICHT die schnellste Runde eines Autos: steht die
 * beste Runde unter den ersten hundert, greift es fuenf Runden tief, weil eine
 * Runde ganz oben meistens ein Ausreisser ist. Eine eingereichte Runde als eine
 * Zeile unter vielen einzuschieben hiesse also, sie genau dann zu uebergehen, wenn
 * sie am schnellsten ist -- das Gegenteil dessen, was eine eingereichte Bestzeit
 * soll. Darum wird sie NACH pickCars angewandt, als ausdrueckliche Ueberschreibung,
 * und traegt ein Kennzeichen, damit niemand sie fuer eine Rivals-Zeit haelt.
 *
 * WARUM DIE KLASSE NACH NAMEN UND NICHT NACH INDEX
 *
 * Die Telemetrie liefert carClass als Zahl in PI-Reihenfolge (0..6 = D C B A S1 S2
 * R). D.classes ist dagegen ALPHABETISCH sortiert (A B C D R S1 S2). Die Zahl als
 * Index zu nehmen, legte eine D-Runde in die Klasse A -- ohne Fehlermeldung, mit
 * einer ueberzeugend falschen Tabelle.
 */

/* KEIN "use strict" auf oberster Ebene. Diese Datei wird als ERSTE Anweisung
   in das Skript der Seite eingesetzt, und eine Direktive dort -- auch hinter
   Kommentaren -- gaelte fuer die GANZE Seite. Die Seite ist nicht dafuer
   geschrieben; eine einzige Zuweisung an eine undeklarierte Variable wuerde
   dann werfen und die Auswertung leer lassen. */

var PI_ORDER = ["D", "C", "B", "A", "S1", "S2", "R"];

/* NUR RIVALS UND HORIZON PLAY KOMMEN IN DIE WERTUNG (seit 2026-09-28).
 * Dort gibt es keine Wandfahrten: Rivals erklaert eine Runde mit Wandkontakt fuer
 * ungueltig, Horizon Play bremst den Motor. Solo, Koop und freie Fahrt erlauben sie,
 * und eine Rivals-Bestenliste mit solchen Zeiten zu mischen hiesse, sie zu verfaelschen.
 * Diese Runden stehen darum in einer eigenen Liste -- ebenso Runden ohne bekannten
 * Modus (eingereicht, bevor die App ihn erkennen konnte). */
var CLEAN_MODES = ["rivals", "horizon-play"];

var MODE_TEXT = { "rivals": "Rivals", "horizon-play": "Horizon Play", "race": "Solo / co-op race",
                  "solo": "Solo", "coop": "Co-op", "freeroam": "Free roam", "unknown": "unknown" };

function modeText(mode) {
  return MODE_TEXT[mode] || String(mode || "unknown");
}

function falteName(s) {
  return String(s || "").trim().toLowerCase().replace(/\s+/g, " ");
}

/* laps: die Liste von /api/lap/list.
 * Gibt zurueck: {
 *   byBoard:  Map("Strecke|Klasse" -> Map(carIdx -> {ms, gamertag, received, id, mode})),
 *   placed:   Anzahl eingerechneter Runden,
 *   unplaced: [{id, reason}]  -- was nicht einzuordnen war, MIT Grund,
 *   newCars:  Anzahl Autos, die es im Datensatz bisher nicht gab,
 *   others:   [{id, mode, track, klasse, car, ms, gamertag, received}] -- Runden aus
 *             Solo, Koop, freier Fahrt oder ohne Modus: gezeigt, nie gewertet
 * }
 * VERAENDERT D: neue Autos werden an carIds/carNames/carMeta angehaengt. */
function buildSubmitted(D, laps) {
  var byBoard = new Map();
  var unplaced = [];
  var others = [];
  var placed = 0;
  var newCars = 0;

  // Strecke nach gefaltetem Namen, Klasse nach Namen -- beide als Nachschlagewerk.
  var trackIdx = new Map();
  D.tracks.forEach(function (t, i) { trackIdx.set(falteName(t), i); });
  var boardFor = new Map();
  D.boards.forEach(function (b) {
    boardFor.set(b.t + "|" + D.classes[b.k], b);
  });
  var carIdx = new Map();
  D.carIds.forEach(function (id, i) { carIdx.set(Number(id), i); });

  (laps || []).forEach(function (eintrag) {
    if (!eintrag || eintrag.hidden) return;
    var lap = eintrag.lap || {};
    var id = eintrag.id || "?";

    var sek = Number(lap.lapSeconds);
    if (!(sek > 0)) { unplaced.push({ id: id, reason: "no lap time" }); return; }

    // DER MODUS ENTSCHEIDET ZUERST: was nicht aus Rivals oder Horizon Play kommt, wird
    // gezeigt, aber nie gewertet -- auch wenn es zu einem Brett passen wuerde.
    var mode = String(lap.mode || "unknown");
    if (CLEAN_MODES.indexOf(mode) < 0) {
      var nr = Number(lap.carOrdinal);
      var bekannt = carIdx.get(nr);
      others.push({ id: id, mode: mode, track: String(lap.track || ""),
                    klasse: PI_ORDER[Number(lap.carClass)] || "?",
                    car: bekannt !== undefined ? D.carNames[bekannt]
                         : (lap.carName ? String(lap.carName) : (nr > 0 ? "Car #" + nr : "?")),
                    ms: Math.round(sek * 1000), gamertag: eintrag.gamertag || "",
                    received: eintrag.received || "",
                    download: eintrag.telemetryDownload === true });
      return;
    }

    var t = trackIdx.get(falteName(lap.track));
    if (t === undefined) {
      unplaced.push({ id: id, reason: lap.track ? "unknown track '" + lap.track + "'"
                                                : "no track name" });
      return;
    }

    var klasse = PI_ORDER[Number(lap.carClass)];
    if (!klasse || D.classes.indexOf(klasse) < 0) {
      unplaced.push({ id: id, reason: "unknown class " + lap.carClass });
      return;
    }

    var board = boardFor.get(t + "|" + klasse);
    if (!board) {
      unplaced.push({ id: id, reason: "no board for " + D.tracks[t] + " " + klasse });
      return;
    }

    var ordinal = Number(lap.carOrdinal);
    if (!(ordinal > 0)) { unplaced.push({ id: id, reason: "no car" }); return; }

    // EIN AUTO, DAS ES IM DATENSATZ NOCH NICHT GIBT, wird angehaengt -- ein
    // neu erschienenes Auto hat noch keine Bestenliste, aber eine Zeit, und die
    // gehoert in die Tabelle. Den Namen bringt die Einreichung mit, wenn sie ihn
    // kennt; sonst gilt dieselbe Schreibweise wie fuer jedes unbenannte Auto hier.
    // Der PI, mit dem DIESE Runde gefahren wurde. Er gehoert zur Runde, nicht zum
    // Auto: bis 2026-09-26 landete er als `stockPi` in den Autodaten -- dort steht
    // aber der Serien-PI, und ein getuntes Auto hat mit seinem Serienwert nichts zu
    // tun. Jetzt reist er mit der Runde, wie der PI je Runde aus den Bestenlisten.
    var pi = Math.round(Number(lap.performanceIndex));
    if (!(pi > 0)) pi = null;

    var car = carIdx.get(ordinal);
    if (car === undefined) {
      car = D.carIds.length;
      D.carIds.push(ordinal);
      D.carNames.push(lap.carName ? String(lap.carName) : "Car #" + ordinal);
      D.carMeta.push({ make: "", year: null, country: "", type: "Submitted",
                       stockClass: klasse, stockPi: null });
      carIdx.set(ordinal, car);
      newCars += 1;
    }

    var key = D.tracks[t] + "|" + klasse;
    var karte = byBoard.get(key);
    if (!karte) { karte = new Map(); byBoard.set(key, karte); }
    var ms = Math.round(sek * 1000);
    var da = karte.get(car);
    // Je Auto und Board nur die SCHNELLSTE eingereichte Runde.
    if (!da || ms < da.ms) {
      // Ob die volle Telemetrie herunterzuladen ist, sagt der Server (seit 2026-09-30):
      // nur bei Runden, deren Fahrer es mit der Einreichung erlaubt hat.
      karte.set(car, { ms: ms, gamertag: eintrag.gamertag || "",
                       received: eintrag.received || "", id: id, pi: pi, mode: mode,
                       download: eintrag.telemetryDownload === true });
    }
    placed += 1;
  });

  others.sort(function (a, b) {
    return a.track.localeCompare(b.track) || a.klasse.localeCompare(b.klasse) || a.ms - b.ms;
  });
  return { byBoard: byBoard, placed: placed, unplaced: unplaced, newCars: newCars, others: others };
}

/* Die Auswahl von pickCars mit den eingereichten Zeiten verrechnen.
 *
 * Schneller als die Rivals-Zeit -> ersetzt sie, gekennzeichnet.
 * Langsamer                     -> aendert nichts; die bessere Zeit bleibt stehen.
 * Auto ohne Rivals-Zeit         -> kommt neu hinzu, gekennzeichnet. */
function applySubmitted(D, board, picks, submitted) {
  if (!submitted) return picks;
  var karte = submitted.byBoard.get(D.tracks[board.t] + "|" + D.classes[board.k]);
  if (!karte) return picks;
  karte.forEach(function (s, car) {
    var p = picks.get(car);
    if (!p) {
      picks.set(car, { ms: s.ms, rank: 0, bestRank: 0, took: 1, wanted: 1,
                       count: 1, thin: true, submitted: s,
                       pi: s.pi == null ? null : s.pi });
    } else if (s.ms < p.ms) {
      p.rivalsMs = p.ms;          // die abgeloeste Zeit bleibt sichtbar
      p.ms = s.ms;
      p.submitted = s;
      // Gezeigt wird jetzt die eingereichte Runde, also auch ihr PI.
      p.pi = s.pi == null ? null : s.pi;
    }
  });
  return picks;
}

if (typeof module !== "undefined") {
  module.exports = { buildSubmitted: buildSubmitted, applySubmitted: applySubmitted,
                     PI_ORDER: PI_ORDER, CLEAN_MODES: CLEAN_MODES, modeText: modeText };
}
