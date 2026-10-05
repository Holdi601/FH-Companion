/*
 * Smoke-test the generated analytics page without a browser.
 *
 * The page is shipped as one HTML file and cannot be clicked here, so the script
 * block is extracted and run against a DOM stub: every view is rendered for a set
 * of filter combinations, and anything that throws -- a typo, an undefined global,
 * a null element -- fails the test instead of reaching the operator.
 *
 * It also re-derives the two scores independently from the payload and compares
 * them against what the page's own functions produce, so a wrong rule shows up as a
 * mismatch rather than as a plausible-looking table.
 *
 *   node scripts/test_analytics_site.js data/analytics/rivals_auto_wertung.html
 */

const fs = require("fs");

/* ---------- DOM stub: enough for the page's own construction calls ---------- */
class Node {
  constructor(tag) {
    this.tag = tag;
    this.children = [];
    this.dataset = {};
    this.style = {};
    this.attrs = {};
    this._text = "";
  }
  set textContent(value) { this._text = String(value); this.children.length = 0; }
  get textContent() {
    if (this.children.length) return this.children.map(c => c.textContent).join("");
    return this._text;
  }
  set className(v) { this.attrs.class = v; }
  get className() { return this.attrs.class || ""; }
  appendChild(child) { this.children.push(child); return child; }
  append(...kids) { kids.forEach(k => this.children.push(k)); }
  setAttribute(name, value) { this.attrs[name] = value; }
  getAttribute(name) { return this.attrs[name]; }
  addEventListener(type, fn) { this["on" + type] = fn; }
  get firstChild() { return this.children[0]; }
}
class TextNode {
  constructor(text) { this._text = String(text); }
  get textContent() { return this._text; }
}

const ids = ["tiles", "f-cat", "f-cls", "f-trk", "f-clean", "f-gear", "f-assists",
             "f-tune", "f-make", "f-country", "f-cartype",
             "f-car", "f-reset", "tabs", "context", "rulenote", "content",
             "year-from", "year-to", "year-fill", "year-readout", "year-reset",
             "quality", "caveat"];
const store = new Map();
ids.forEach(id => store.set(id, new Node("div")));
store.get("f-car").value = "";

global.document = {
  getElementById: id => {
    if (!store.has(id)) throw new Error("page asked for unknown element #" + id);
    return store.get(id);
  },
  createElement: tag => new Node(tag),
  // The spread view draws its histogram as SVG, so the stub has to answer the
  // namespaced call too -- otherwise that whole tab throws and goes untested.
  createElementNS: (ns, tag) => new Node(tag),
  createTextNode: text => new TextNode(text),
};

/* ---------- load the page's script ---------- */
const file = process.argv[2] || "data/analytics/rivals_auto_wertung.html";
const html = fs.readFileSync(file, "utf8");
const match = html.match(/<script>([\s\S]*)<\/script>/);
if (!match) { console.error("no script block found in " + file); process.exit(2); }

// The page's top-level `const`/`let` stay in this eval scope; expose what the test
// needs by evaluating in a wrapper that hands them back.
const api = eval(match[1] + "\n;({ D, F, ASSISTS, CLASS_ORDER, masks, pickCars, classTable, classesInScope, render, setTab: t => { TAB = t; }, getTab: () => TAB, setFocus: c => { FOCUS_CAR = c; }, parseLap, fasterShare, yearStops, yearIndex, yearBounds, renderYearRange });");

let failures = 0;
let skipped = 0;
// Thrown by a check whose rule needs a shape of board the live data does not
// currently contain. Deeper scans removed the last shallow class-A board on
// 2026-08-23 and turned a passing check into a red one, which is a broken test
// rather than a broken site.
class Untestable extends Error {}
function check(name, fn) {
  try {
    fn();
    console.log("  ok    " + name);
  } catch (error) {
    if (error instanceof Untestable) {
      skipped += 1;
      console.log("  skip  " + name + " -- " + error.message);
      return;
    }
    failures += 1;
    console.log("  FAIL  " + name + " -- " + error.message);
  }
}

console.log("payload: " + api.D.boards.length + " boards, " +
  api.D.meta.kept_laps + " laps, " + api.D.carNames.length + " cars");

/* ---------- every view renders under a range of filters ---------- */
const scenarios = [
  ["defaults", () => {}],
  ["class A", () => { api.F.cls = "A"; }],
  ["class A, one track", () => { api.F.cls = "A"; api.F.tracks.add(api.D.tracks[0]); }],
  ["valid laps only", () => { api.F.cls = "A"; api.F.clean = "valid"; }],
  ["invalid laps only", () => { api.F.cls = "A"; api.F.clean = "invalid"; }],
  ["manual gearbox", () => { api.F.cls = "A"; api.F.gear = "manual"; }],
  ["clutch manual", () => { api.F.cls = "A"; api.F.gear = "clutch"; }],
  ["no TCS", () => { api.F.cls = "A"; api.F.assist.tcs = "without"; }],
  ["with TCS and ABS", () => { api.F.cls = "A"; api.F.assist.tcs = "with"; api.F.assist.abs = "with"; }],
  ["impossible combination", () => {
    api.F.cls = "A"; api.F.gear = "auto"; api.F.assist.tcs = "with";
    api.F.assist.stm = "with"; api.F.assist.supereasy = "with"; api.F.clean = "invalid";
  }],
  ["car search", () => { api.F.cls = null; api.F.car = "Ferrari"; }],
  ["car search by id", () => { api.F.car = "4221"; }],
  ["detuned only", () => { api.F.cls = "A"; api.F.tune = "down"; }],
  ["stock class only", () => { api.F.cls = "A"; api.F.tune = "same"; }],
  ["tuned up only", () => { api.F.cls = "A"; api.F.tune = "up"; }],
  ["model years 1990-1999", () => { api.F.cls = "A"; api.F.yearFrom = 1990; api.F.yearTo = 1999; }],
  ["model years from 2020", () => { api.F.yearFrom = 2020; }],
];

function resetFilters() {
  api.F.cat = null; api.F.cls = null; api.F.tracks.clear();
  api.F.clean = "any"; api.F.gear = "any"; api.F.car = "";
  api.ASSISTS.forEach(([key]) => { api.F.assist[key] = "any"; });
  api.F.make = null; api.F.country = null; api.F.carType = null; api.F.tune = null;
  api.F.yearFrom = null; api.F.yearTo = null;
  api.setFocus(null);
}

["points", "time", "boards", "car", "spread", "scans"].forEach(tab => {
  scenarios.forEach(([name, apply]) => {
    check(tab + " / " + name, () => {
      resetFilters();
      apply();
      api.setTab(tab);
      api.render();
      if (!store.get("content").children.length && !store.get("content").textContent) {
        throw new Error("view rendered nothing at all");
      }
    });
  });
});

/* ---------- the rules, re-derived here and compared ---------- */
resetFilters();
check("points: leader has the highest sum and it is > 0", () => {
  const table = api.classTable("A");
  const best = table.cars.reduce((m, c) => (c.points > m.points ? c : m), table.cars[0]);
  if (!(best.points > 0)) throw new Error("no car scored points");
  table.cars.forEach(c => {
    if (c.points > best.points) throw new Error("a car outscores the leader");
  });
});

check("points: per track, first place scores exactly the field size", () => {
  const table = api.classTable("A");
  table.tracks.forEach(entry => {
    const n = entry.picks.size;
    let top = 0;
    table.cars.forEach(agg => {
      const own = agg.per.get(entry.track);
      if (own && !own.substituted && own.points > top) top = own.points;
    });
    if (top !== n) {
      throw new Error(entry.track + ": top score " + top + " but " + n + " cars in the field");
    }
  });
});

check("points: the fastest car on a track is the one that scores the field size", () => {
  const table = api.classTable("A");
  const entry = table.tracks[0];
  let fastest = null;
  entry.picks.forEach((pick, car) => {
    if (!fastest || pick.ms < fastest.ms) fastest = { car: car, ms: pick.ms };
  });
  const agg = table.cars.find(c => c.car === fastest.car);
  const own = agg.per.get(entry.track);
  if (own.points !== entry.picks.size) {
    throw new Error("fastest car scored " + own.points + ", expected " + entry.picks.size);
  }
});

check("time sum: every car is counted on every track that is deep enough", () => {
  const table = api.classTable("A");
  table.cars.forEach(agg => {
    if (agg.per.size !== table.tracks.length) {
      throw new Error("car " + agg.car + " has " + agg.per.size +
        " track entries for " + table.tracks.length + " tracks");
    }
    let sum = 0;
    agg.per.forEach(entry => { if (!entry.outOfTimeSum && entry.ms !== null) sum += entry.ms; });
    if (Math.abs(sum - agg.ms) > 1) {
      throw new Error("sum mismatch: " + sum + " vs " + agg.ms);
    }
  });
});

check("shallow boards leave the time sum for every car, not just the absent ones", () => {
  let table = null;
  let shallow = [];
  for (const klass of api.D.classes) {
    const candidate = api.classTable(klass);
    const thin = candidate.tracks.filter(entry => !entry.deep);
    if (thin.length) { table = candidate; shallow = thin; break; }
  }
  if (!shallow.length) {
    throw new Untestable("no class has a board under the substitution threshold any more");
  }
  shallow.forEach(entry => {
    table.cars.forEach(agg => {
      const own = agg.per.get(entry.track);
      if (!own.outOfTimeSum) {
        throw new Error(entry.track + " still counts towards a car's time sum");
      }
      if (own.substituted) {
        throw new Error(entry.track + " handed out a substitute despite being shallow");
      }
    });
  });
  // ...and their points survive, because points need no substitute.
  const scored = table.cars.some(agg => {
    const own = agg.per.get(shallow[0].track);
    return own && own.points > 0;
  });
  if (!scored) throw new Error("a shallow board stopped scoring points too");
});

check("substitutes only ever come from boards past the threshold", () => {
  ["A", "D", "S1", "R"].forEach(klass => {
    const table = api.classTable(klass);
    table.cars.forEach(agg => {
      table.tracks.forEach(entry => {
        const own = agg.per.get(entry.track);
        if (own && own.substituted && !entry.deep) {
          throw new Error(klass + "/" + entry.track + " substituted from a shallow board");
        }
      });
    });
  });
});

check("time sum: a missing track is filled with that track's slowest time", () => {
  const table = api.classTable("A");
  let checked = 0;
  table.cars.forEach(agg => {
    table.tracks.forEach(entry => {
      const own = agg.per.get(entry.track);
      if (!own.substituted) return;
      checked += 1;
      if (own.ms !== entry.worst) {
        throw new Error("substituted " + own.ms + " but the slowest here is " + entry.worst);
      }
    });
  });
  if (!checked) throw new Error("no substitution happened, so the rule went untested");
});

check("selection rule: depth follows the car's best rank", () => {
  const board = api.D.boards.find(b => api.D.classes[b.k] === "A" && b.rows > 5000);
  const picks = api.pickCars(board, 0, 0);
  let seen = { five: 0, two: 0, one: 0 };
  picks.forEach(pick => {
    const wanted = pick.bestRank <= 100 ? 5 : (pick.bestRank <= 1000 ? 2 : 1);
    if (pick.wanted !== wanted) {
      throw new Error("bestRank " + pick.bestRank + " asked for " + pick.wanted);
    }
    if (pick.took > wanted) throw new Error("took deeper than the rule allows");
    if (pick.count >= wanted && pick.took !== wanted) {
      throw new Error("enough laps (" + pick.count + ") but took " + pick.took);
    }
    if (wanted === 5) seen.five += 1; else if (wanted === 2) seen.two += 1; else seen.one += 1;
  });
  if (!seen.five || !seen.two || !seen.one) {
    throw new Error("a rule branch never fired: " + JSON.stringify(seen));
  }
});

check("filters actually narrow: no TCS keeps fewer laps than any", () => {
  const board = api.D.boards.find(b => b.rows > 5000);
  const all = api.pickCars(board, 0, 0);
  const noTcs = api.pickCars(board, 0, 1 << api.D.flags.indexOf("tcs"));
  if (!(noTcs.size <= all.size)) throw new Error("filtering grew the field");
  if (noTcs.size === all.size) throw new Error("the TCS filter changed nothing at all");
});

check("cars absent from a whole class are left out of the time sum", () => {
  api.F.cls = null;
  const table = api.classTable("R");
  const universe = new Set();
  table.tracks.forEach(entry => entry.picks.forEach((_, car) => universe.add(car)));
  if (table.cars.length !== universe.size) {
    throw new Error(table.cars.length + " scored cars vs " + universe.size + " present");
  }
});


check("spread: percentiles rise with the percentage and stay inside the field", () => {
  const boards = api.D.boards.filter(b => b.dist);
  if (!boards.length) throw new Error("no board carries a distribution");
  boards.forEach(board => {
    const d = board.dist;
    let previous = d.low - 1;
    ["1", "5", "10", "25", "50", "75", "90"].forEach(key => {
      const ms = d.pct[key];
      if (ms === undefined) return;
      if (ms < previous) throw new Error("p" + key + " is faster than the cut below it");
      if (ms < d.low || ms > d.slowest) throw new Error("p" + key + " is outside the range");
      previous = ms;
    });
    const summed = d.counts.reduce((a, b) => a + b, 0);
    if (summed !== d.n) throw new Error("histogram counts " + summed + " of " + d.n);
  });
});

check("spread: a lap on the median lands near the 50th percentile", () => {
  const board = api.D.boards.filter(b => b.dist && b.dist.n > 200)[0];
  if (!board) throw new Error("no board deep enough to test the placement");
  const share = api.fasterShare(board.dist, board.dist.pct["50"]);
  if (Math.abs(share - 0.5) > 0.08) throw new Error("median placed at " + (share*100).toFixed(1) + "%");
});

check("spread: lap times parse the way a player would type them", () => {
  [["1:23.456", 83456], ["83.456", 83456], ["1:23", 83000], ["1:23,456", 83456]]
    .forEach(pair => {
      const got = api.parseLap(pair[0]);
      if (got !== pair[1]) throw new Error(pair[0] + " parsed as " + got);
    });
  ["", "abc", "1:2:3"].forEach(text => {
    if (api.parseLap(text) !== null) throw new Error(text + " should not parse");
  });
});

check("year slider: one step per year present, no dead travel over the gap", () => {
  // Forza ships the Halo Warthog with model year 2554, 529 years past the next car.
  // On a linear track that gap eats 85% of the travel and crushes every real year
  // into the left sixth, so the control steps over the years that EXIST instead.
  const stops = api.yearStops();
  if (stops.length < 2) throw new Error("only " + stops.length + " year stop(s)");
  for (let i = 1; i < stops.length; i++) {
    if (stops[i] <= stops[i - 1]) throw new Error("stops not strictly ascending at " + i);
  }
  const bounds = api.yearBounds();
  if (bounds[0] !== stops[0] || bounds[1] !== stops[stops.length - 1]) {
    throw new Error("bounds " + bounds + " disagree with the stops");
  }
  // Every stop must round-trip through the index, and the outlier must sit exactly
  // one step past its neighbour rather than 529 years away.
  stops.forEach((year, i) => {
    if (api.yearIndex(year, 0) !== i) throw new Error(year + " maps to the wrong index");
  });
  const last = stops.length - 1;
  if (api.yearIndex(stops[last], 0) - api.yearIndex(stops[last - 1], 0) !== 1) {
    throw new Error("the highest year is not one step past the previous one");
  }
  // A year that is not a stop snaps to the nearest instead of resetting the filter.
  if (api.yearIndex(stops[last] - 1, 0) !== last) {
    throw new Error("an off-stop year did not snap to its nearest stop");
  }
  // The inputs must be driven by index, and the fill must not exceed the track.
  api.F.yearFrom = null; api.F.yearTo = null;
  api.renderYearRange();
  const from = document.getElementById("year-from");
  const to = document.getElementById("year-to");
  // The page assigns .min/.max as properties, so read them the same way.
  if (String(from.max) !== String(last) || String(to.max) !== String(last)) {
    throw new Error("slider max is " + from.max + ", expected " + last);
  }
  if (String(from.min) !== "0" || String(to.min) !== "0") {
    throw new Error("slider min is " + from.min + ", expected 0");
  }
  if (Number(from.value) !== 0 || Number(to.value) !== last) {
    throw new Error("full range should span index 0.." + last);
  }
  const fill = document.getElementById("year-fill");
  ["left", "right"].forEach(side => {
    const pct = parseFloat(fill.style[side]);
    if (!(pct >= 0 && pct <= 100)) throw new Error("fill " + side + " is " + fill.style[side]);
  });
});

check("scan status: every board with data has a scan run behind it", () => {
  const scans = (api.D.meta && api.D.meta.scans) || [];
  if (!scans.length) throw new Error("no scan provenance in the payload");
  const seen = new Set(scans.map(s => s.t + "|" + s.k));
  api.D.boards.forEach(board => {
    const key = api.D.tracks[board.t] + "|" + api.D.classes[board.k];
    if (!seen.has(key)) throw new Error(key + " has laps but no recorded scan");
  });
});

check("most contested: ranks by estimated size, and every share is a real fraction", () => {
  // Die Rangliste ist die einzige Ansicht, die eine SCHAETZUNG als Hauptzahl zeigt --
  // gescannt werden nur die ersten 20.000 Raenge, die Groesse kommt aus dem Scrollbalken.
  // Zwei Dinge muessen halten: die Sortierung nach der Schaetzung, und dass nie tiefer
  // gelesen wurde als das Board lang sein soll. Ein Anteil ueber 100% hiesse, dass die
  // Schaetzung falsch ist -- nicht die Anzeige.
  const withSize = api.D.boards.filter(b => (b.impl || 0) > 0);
  if (!withSize.length) throw new Error("no board carries an estimated length");
  withSize.forEach(b => {
    const key = api.D.tracks[b.t] + " " + api.D.classes[b.k];
    if ((b.maxRank || 0) > b.impl * 1.15) {
      throw new Error(key + ": scanned to " + b.maxRank + " but estimated only " + b.impl);
    }
    if (!b.iest) throw new Error(key + ": has a length but no estimator recorded");
  });

  // Die DOM-Attrappe kennt keine Selektoren, also wird der Baum abgelaufen.
  const findAll = (node, tag, out) => {
    out = out || [];
    (node.children || []).forEach(kid => {
      if (kid.tag === tag) out.push(kid);
      findAll(kid, tag, out);
    });
    return out;
  };

  api.F.cls = null;
  api.F.tracks.clear();
  api.setTab("contest");
  api.render();
  const content = document.getElementById("content");
  const bodies = findAll(content, "tbody");
  if (!bodies.length) throw new Error("the contested view rendered no table");
  const rows = findAll(bodies[0], "tr");
  if (rows.length !== withSize.length) {
    throw new Error(rows.length + " rows for " + withSize.length + " measured boards");
  }
  const parse = t => Number(String(t).replace(/[^0-9]/g, "")) || 0;
  const sizes = rows.map(tr => parse(tr.children[3].textContent));
  for (let i = 1; i < sizes.length; i += 1) {
    if (sizes[i] > sizes[i - 1]) {
      throw new Error("row " + i + " (" + sizes[i] + ") outranks the row above (" + sizes[i - 1] + ")");
    }
  }
  if (sizes[0] !== withSize.reduce((m, b) => Math.max(m, b.impl), 0)) {
    throw new Error("the top row is not the biggest board");
  }
  const heads = findAll(content, "h3").map(h => h.textContent);
  if (heads.length !== 2) {
    throw new Error("expected a per-track and a per-class summary, got " + heads.length);
  }
});

console.log(failures ? "\n" + failures + " FAILED" : "\nall checks passed");
process.exit(failures ? 1 : 0);
