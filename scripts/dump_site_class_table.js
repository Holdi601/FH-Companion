/*
 * Dump the analytics page's OWN class table as JSON, so another implementation
 * can be checked against it instead of against a re-reading of the rules.
 *
 * The overlay (`scripts/rivals_advisor.py`) recomputes points and time sums in
 * Python. That is a second implementation of the scoring, and a second
 * implementation drifts. This dumper is the reference side of the comparison:
 * it evaluates the shipped page's script block and calls `classTable` directly.
 *
 *   node scripts/dump_site_class_table.js --class A --tracks "Soni Circuit,The Goliath"
 *   node scripts/dump_site_class_table.js --page data/analytics/rivals_auto_wertung.html \
 *        --class S1 --clean valid --gear manual
 *
 * Output on stdout: { klass, tracks, cars: [{car, name, points, ms, present,
 * thin}], byPoints: [carIdx...], byTime: [carIdx...] }.
 */

const fs = require("fs");

/* ---------- DOM stub: the page builds nodes at load time ---------- */
class Node {
  constructor(tag) {
    this.tag = tag; this.children = []; this.dataset = {};
    this.style = {}; this.attrs = {}; this._text = ""; this.value = "";
  }
  set textContent(v) { this._text = String(v); this.children.length = 0; }
  get textContent() {
    if (this.children.length) return this.children.map(c => c.textContent).join("");
    return this._text;
  }
  set className(v) { this.attrs.class = v; }
  get className() { return this.attrs.class || ""; }
  appendChild(c) { this.children.push(c); return c; }
  append(...kids) { kids.forEach(k => this.children.push(k)); }
  setAttribute(n, v) { this.attrs[n] = v; }
  getAttribute(n) { return this.attrs[n]; }
  addEventListener(t, fn) { this["on" + t] = fn; }
  get firstChild() { return this.children[0]; }
}
class TextNode {
  constructor(t) { this._text = String(t); }
  get textContent() { return this._text; }
}
// Auto-vivifying, unlike the smoke test's fixed id list: this dumper only needs
// the page to finish loading, not to prove which ids it touches.
const store = new Map();
global.document = {
  getElementById: id => {
    if (!store.has(id)) store.set(id, new Node("div"));
    return store.get(id);
  },
  createElement: tag => new Node(tag),
  createElementNS: (ns, tag) => new Node(tag),
  createTextNode: text => new TextNode(text),
};

/* ---------- arguments ---------- */
const argv = process.argv.slice(2);
function opt(name, fallback) {
  const at = argv.indexOf("--" + name);
  return at >= 0 && at + 1 < argv.length ? argv[at + 1] : fallback;
}
const page = opt("page", "data/analytics/rivals_auto_wertung.html");
const klass = opt("class", "A");
const tracksArg = opt("tracks", "");
const clean = opt("clean", "valid");
const gear = opt("gear", "any");

const html = fs.readFileSync(page, "utf8");
const match = html.match(/<script>([\s\S]*)<\/script>/);
if (!match) { console.error("no script block in " + page); process.exit(2); }
/* Die Seite hiess diese Funktion frueher `classTable(klass)`. Sie heisst jetzt
 * `scoreTable(classes)` und nimmt ein ARRAY von Klassen -- der Mehrfachfilter der
 * Seite. Seit der Umbenennung lief der Vergleich ins Leere ("ReferenceError:
 * classTable is not defined") und uebersprang STILL 14 Tests -- also genau die,
 * die Seite, Python und C# deckungsgleich halten sollen. Aufgefallen 2026-09-09.
 * Der alte Name wird weiter akzeptiert, damit eine aeltere gebaute Seite pruefbar
 * bleibt. */
const api = eval(match[1] + "\n;({ D, F, ASSISTS, scoreTable: (typeof scoreTable !== 'undefined' ? scoreTable : null), classTable: (typeof classTable !== 'undefined' ? classTable : null) });");
const score = api.scoreTable ? (k) => api.scoreTable([k]) : (k) => api.classTable(k);
if (!api.scoreTable && !api.classTable) {
  console.error("weder scoreTable noch classTable in " + page);
  process.exit(2);
}

/* ---------- the one scenario asked for ---------- */
// Der Seitenfilter arbeitet inzwischen durchgehend mit Sets (leer = alle);
// frueher waren cat/make/country/carType/tune einzelne Werte oder null, und
// cls ein String. Angepasst 2026-09-09.
api.F.cat.clear();
api.F.cls.clear();          // die Klasse kommt als Argument in scoreTable([klass])
api.F.tracks.clear();
if (tracksArg) tracksArg.split(",").map(s => s.trim()).filter(Boolean)
  .forEach(t => api.F.tracks.add(t));
api.F.clean = clean;
api.F.gear = gear;
api.ASSISTS.forEach(([key]) => { api.F.assist[key] = "any"; });
api.F.make.clear(); api.F.country.clear(); api.F.carType.clear(); api.F.tune.clear();
api.F.yearFrom = null; api.F.yearTo = null;
api.F.car = "";

const table = score(klass);
const cars = table.cars.map(agg => ({
  car: agg.car, name: api.D.carNames[agg.car], points: agg.points,
  ms: agg.ms, present: agg.present, thin: agg.thin,
}));
const byPoints = table.cars.slice().sort((a, b) => b.points - a.points).map(a => a.car);
const byTime = table.cars.slice().sort((a, b) => a.ms - b.ms).map(a => a.car);

process.stdout.write(JSON.stringify({
  klass: klass,
  tracks: table.tracks.map(e => e.track),
  timeTracks: table.timeTracks,
  shallowTracks: table.shallowTracks,
  cars: cars, byPoints: byPoints, byTime: byTime,
}));
