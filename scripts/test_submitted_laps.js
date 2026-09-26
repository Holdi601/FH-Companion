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
  lap: { track: strecke, carClass: piKlasse, carOrdinal: ordinal,
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
  lap: { track: strecke, carClass: piKlasse, carOrdinal: 999001,
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
  lap: { track: D.tracks[dBrett.t], carClass: 0, carOrdinal: ordinal, lapSeconds: 80 } };
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [dRunde]);
var landete = Array.from(sub.byBoard.keys());
pruefe(landete.length === 1 && landete[0].endsWith("|D"),
       "carClass 0 landet in Klasse D, nicht in A (" + landete.join(", ") + ")");

// --- 5. Ohne Streckennamen: nicht eingeordnet, aber MIT Grund gemeldet.
var ohne = { id: "x", hidden: false, lap: { carClass: piKlasse, carOrdinal: ordinal,
                                            lapSeconds: 90 } };
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [ohne]);
pruefe(sub.placed === 0 && sub.unplaced.length === 1
       && sub.unplaced[0].reason === "no track name",
       "Runde ohne Strecke wird gemeldet statt still verschluckt");

// --- 6. Ausgeblendete Runden zaehlen nicht.
var versteckt = JSON.parse(JSON.stringify(schneller));
versteckt.hidden = true;
sub = S.buildSubmitted(JSON.parse(JSON.stringify(D)), [versteckt]);
pruefe(sub.placed === 0, "im Admin ausgeblendete Runde erscheint nicht");

console.log("");
if (fehler) { console.log(fehler + " Pruefung(en) fehlgeschlagen."); process.exit(1); }
console.log("Alle Pruefungen bestanden.");
