/* Prueft submitted_laps.js gegen den ECHTEN Datensatz.
 *
 *     node scripts/test_submitted_laps.js
 *
 * Mit echten Brettern, echten Autos und echten Rivals-Zeiten -- ein erfundener
 * Mini-Datensatz haette die Falle mit der alphabetischen Klassenreihenfolge nie
 * gezeigt, weil man ihn selbst in der "richtigen" Reihenfolge anlegt.
 */
"use strict";
var fs = require("fs");
var path = require("path");
var S = require("./submitted_laps.js");

var D = JSON.parse(fs.readFileSync(
  path.join(__dirname, "..", "data", "analytics", "laps.json"), "utf8"));

var fehler = 0;
function pruefe(gut, text) {
  console.log((gut ? "  ok   " : "  FEHL ") + text);
  if (!gut) fehler += 1;
}

// Ein Brett, das es wirklich gibt, und sein schnellstes Auto.
var board = D.boards.find(function (b) { return D.classes[b.k] === "B" && b.lms.length > 50; });
var strecke = D.tracks[board.t];
var klasse = D.classes[board.k];
var bestIdx = 0;
for (var i = 1; i < board.lms.length; i++) if (board.lms[i] < board.lms[bestIdx]) bestIdx = i;
var auto = board.gcar[board.lgrp[bestIdx]];
var rivalsBest = board.lms[bestIdx];
var ordinal = D.carIds[auto];
console.log("Brett: " + strecke + " " + klasse + ", Auto #" + ordinal + " "
            + D.carNames[auto] + ", Rivals-Bestzeit " + rivalsBest + " ms\n");

// So, wie pickCars es liefern wuerde: dieses Auto mit seiner Rivals-Zeit.
function picks() {
  var m = new Map();
  m.set(auto, { ms: rivalsBest, rank: 1, bestRank: 1, took: 1, wanted: 1, count: 3 });
  return m;
}
var piKlasse = S.PI_ORDER.indexOf(klasse);

// --- 1. Eine SCHNELLERE eingereichte Runde loest die Rivals-Zeit ab.
var schneller = { id: "a", gamertag: "Tester", hidden: false,
  lap: { track: strecke, carClass: piKlasse, carOrdinal: ordinal, mode: "rivals",
         lapSeconds: (rivalsBest - 500) / 1000 } };
var sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [schneller]);
var p = S.applySubmitted(D, board, picks(), sub).get(auto);
pruefe(p.ms === rivalsBest - 500 && p.submitted && p.rivalsMs === rivalsBest,
       "schnellere Einreichung ersetzt die Rivals-Zeit (" + p.ms + " statt "
       + rivalsBest + ", alte Zeit sichtbar gehalten)");

// --- 2. Eine LANGSAMERE aendert nichts.
var langsamer = JSON.parse(JSON.stringify(schneller));
langsamer.lap.lapSeconds = (rivalsBest + 5000) / 1000;
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [langsamer]);
p = S.applySubmitted(D, board, picks(), sub).get(auto);
pruefe(p.ms === rivalsBest && !p.submitted,
       "langsamere Einreichung laesst die Rivals-Zeit stehen");

// --- 3. Ein Auto, das es im Datensatz NICHT gibt, kommt neu in die Tabelle.
var kopie = JSON.parse(JSON.stringify(D));
var vorher = kopie.carIds.length;
var neu = { id: "n", gamertag: "Tester", hidden: false,
  lap: { track: strecke, carClass: piKlasse, carOrdinal: 999001, mode: "rivals",
         lapSeconds: 99.5, carName: "Brand New Car '27" } };
sub = S.buildSubmitted(kopie, [neu]);
var neuIdx = kopie.carIds.indexOf(999001);
var pk = S.applySubmitted(kopie, board, new Map(), sub).get(neuIdx);
pruefe(sub.newCars === 1 && kopie.carIds.length === vorher + 1
       && kopie.carNames[neuIdx] === "Brand New Car '27"
       && kopie.carMeta.length === kopie.carIds.length
       && pk && pk.ms === 99500 && pk.submitted,
       "neues Auto angelegt (Name, Metadaten) und mit seiner Zeit gelistet");

// --- 4. DIE KLASSENFALLE: carClass ist PI-Reihenfolge, D.classes alphabetisch.
//        Eine D-Runde (carClass 0) darf NICHT im A-Brett landen.
var dBrett = D.boards.find(function (b) { return D.classes[b.k] === "D"; });
var dRunde = { id: "d", gamertag: "Tester", hidden: false,
  lap: { track: D.tracks[dBrett.t], carClass: 0, carOrdinal: ordinal, mode: "rivals", lapSeconds: 80 } };
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [dRunde]);
var landete = Array.from(sub.byBoard.keys());
pruefe(landete.length === 1 && landete[0].endsWith("|D"),
       "carClass 0 landet in Klasse D, nicht in A (" + landete.join(", ") + ")");

// --- 5. Ohne Streckennamen: nicht eingeordnet, aber MIT Grund gemeldet.
var ohne = { id: "x", hidden: false, lap: { carClass: piKlasse, carOrdinal: ordinal,
                                            mode: "rivals", lapSeconds: 90 } };
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [ohne]);
pruefe(sub.placed === 0 && sub.unplaced.length === 1
       && sub.unplaced[0].reason === "no track name",
       "Runde ohne Strecke wird gemeldet statt still verschluckt");

// --- 6. Ausgeblendete Runden zaehlen nicht.
var versteckt = JSON.parse(JSON.stringify(schneller));
versteckt.hidden = true;
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [versteckt]);
pruefe(sub.placed === 0, "im Admin ausgeblendete Runde erscheint nicht");

// --- 7. NUR RIVALS UND HORIZON PLAY IN DIE WERTUNG (2026-09-28). Eine schnellere
//        Runde aus einem Solo-/Koop-Rennen, der freien Fahrt oder ohne Modus steht in
//        der eigenen Liste und laesst die Rivals-Zeit unberuehrt.
["race", "freeroam", "coop", "unknown", undefined].forEach(function (modus) {
  var r = JSON.parse(JSON.stringify(schneller));
  if (modus === undefined) delete r.lap.mode; else r.lap.mode = modus;
  var s7 = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [r]);
  var p7 = S.applySubmitted(D, board, picks(), s7).get(auto);
  pruefe(s7.placed === 0 && s7.others.length === 1 && p7.ms === rivalsBest && !p7.submitted
         && s7.others[0].mode === (modus || "unknown") && s7.others[0].ms === rivalsBest - 500,
         "Modus " + (modus || "(keiner)") + ": gezeigt in eigener Liste, nicht gewertet");
});
var hp = JSON.parse(JSON.stringify(schneller));
hp.lap.mode = "horizon-play";
var s8 = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [hp]);
var p8 = S.applySubmitted(D, board, picks(), s8).get(auto);
pruefe(s8.placed === 1 && p8.submitted && p8.submitted.mode === "horizon-play",
       "Horizon Play zaehlt und traegt seinen Modus");
pruefe(S.modeText("race") === "Solo / co-op race" && S.modeText("horizon-play") === "Horizon Play",
       "Anzeigenamen der Modi");

// Die Telemetrie zum Herunterladen (seit 2026-09-30): nur, wenn der Server sie anbietet.
var mitSpur = JSON.parse(JSON.stringify(hp));
mitSpur.telemetryDownload = true;
var s9 = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [mitSpur]);
var p9 = S.applySubmitted(D, board, picks(), s9).get(auto);
pruefe(p9.submitted && p9.submitted.download === true && p9.submitted.id === mitSpur.id,
       "eine angebotene Telemetrie kommt mit Kennung bis in die Zeile");
pruefe(p8.submitted && p8.submitted.download === false,
       "ohne Angebot vom Server gibt es keinen Download");

// --- Horizon-Play-Zeiten und die Quellenwahl (2026-10-03): die schnellste je Auto gewinnt.
var hpSchnell = S.buildHp(D, [{ track: strecke, "class": klasse, car: ordinal, kind: "lap", ms: rivalsBest - 900, n: 4, confirmed: true }]);
var pq = S.pickWithSources(D, board, picks(), null, hpSchnell, "all").get(auto);
pruefe(pq.ms === rivalsBest - 900 && pq.hp && pq.rivalsMs === rivalsBest, "eine schnellere Horizon-Play-Zeit ersetzt die Rivals-Zeit");
var hpLangsam = S.buildHp(D, [{ track: strecke, "class": klasse, car: ordinal, kind: "lap", ms: rivalsBest + 900, n: 4, confirmed: true }]);
pq = S.pickWithSources(D, board, picks(), null, hpLangsam, "all").get(auto);
pruefe(pq.ms === rivalsBest && !pq.hp, "eine langsamere Horizon-Play-Zeit aendert nichts");
var subUndHp = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [schneller]);
pq = S.pickWithSources(D, board, picks(), subUndHp, hpSchnell, "all").get(auto);
pruefe(pq.ms === rivalsBest - 900 && pq.hp && !pq.submitted, "unter allen Quellen gewinnt die schnellste (hier Horizon Play)");
pq = S.pickWithSources(D, board, picks(), subUndHp, hpSchnell, "rivals").get(auto);
pruefe(pq.ms === rivalsBest && !pq.hp && !pq.submitted, "'nur Rivals' laesst beide Fremdzeiten weg");
pq = S.pickWithSources(D, board, picks(), subUndHp, hpSchnell, "hp").get(auto);
pruefe(pq.ms === rivalsBest - 900 && pq.hp, "'nur Horizon Play' zeigt die Horizon-Play-Zeit");
var hpSub = JSON.parse(JSON.stringify(schneller)); hpSub.lap.mode = "horizon-play"; hpSub.lap.lapSeconds = (rivalsBest - 2000) / 1000;
var nurHpSub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [hpSub, schneller]);
pq = S.pickWithSources(D, board, picks(), nurHpSub, null, "hp").get(auto);
pruefe(pq.ms === rivalsBest - 2000 && pq.submitted && pq.submitted.mode === "horizon-play",
       "'nur Horizon Play' nimmt eingereichte Horizon-Play-Runden, keine Rivals-Runden");

console.log("");
if (fehler) { console.log(fehler + " Pruefung(en) fehlgeschlagen."); process.exit(1); }
console.log("Alle Pruefungen bestanden.");
