"""Die Auswertungsseite im Kopf durchklicken, ohne Browser.

WARUM ES DAS GIBT: `build_analytics_site.py` ist zu 95 % eine grosse Seite aus HTML
und JavaScript, und dafuer gab es keine einzige Pruefung. Ein Tippfehler in einem
Renderpfad faellt dort erst auf, wenn ein Mensch den betroffenen Reiter oeffnet --
und die selten benutzten (Zeitverteilung, Scan-Stand) oeffnet niemand. Der Bau
selbst bleibt gruen: er schreibt nur eine Datei.

Also: das `<script>` aus der Vorlage nehmen, den ECHTEN Datenbestand hineinlegen,
ein Mini-DOM davorschnallen und jeden Reiter anklicken. Wirft irgendein Renderpfad,
faellt das hier auf.

    python scripts/test_analytics_page.py

Braucht Node. Ist keins da, endet der Lauf mit einer Meldung und Rueckgabe 0 --
ein fehlendes Werkzeug ist kein Fehler in der Seite.
"""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

WORKSPACE = Path(__file__).resolve().parent.parent
BUILDER = WORKSPACE / "scripts" / "build_analytics_site.py"
DATASET = WORKSPACE / "data" / "analytics" / "laps.json"

# Ein Mini-DOM. Nur das, was die Seite wirklich anfasst -- die Inventur dazu:
#   grep -oE "document.[a-zA-Z]+" ergibt getElementById, createElement,
#   createElementNS, createTextNode. Mehr braucht es nicht.
DOM = """
function mkEl(tag) {
  return {
    tagName: tag, children: [], attrs: {}, dataset: {}, style: {}, listeners: {},
    value: "", title: "", className: "", type: "", id: "", _text: "",
    appendChild(c) { this.children.push(c); return c; },
    append(...xs) { xs.forEach(x => this.children.push(x)); },
    setAttribute(k, v) { this.attrs[k] = v; },
    getAttribute(k) { return this.attrs[k]; },
    addEventListener(t, f) { (this.listeners[t] = this.listeners[t] || []).push(f); },
    get textContent() { return this._text; },
    set textContent(v) { this._text = v; if (v === "" || v === null) this.children = []; },
  };
}
const IDS = {};
globalThis.document = {
  createElement: (t) => mkEl(t),
  createElementNS: (ns, t) => mkEl(t),
  createTextNode: (t) => ({ nodeText: String(t) }),
  getElementById: (id) => (IDS[id] = IDS[id] || mkEl("div")),
};
globalThis.__IDS = IDS;
"""

CLICKS = """
const tabs = __IDS["tabs"];
if (!tabs || !tabs.children.length) { console.log("FAIL: kein Reiter gerendert"); process.exit(1); }
let bad = 0;
tabs.children.forEach(b => {
  try { b.onclick(); console.log("  ok    Reiter " + b._text); }
  catch (err) { bad += 1; console.log("  FAIL  Reiter " + b._text + ": " + err.message); }
});
// Die Einzelauto-Ansicht rendert erst, wenn ein Auto gewaehlt ist -- ein anderer
// Codepfad als der leere Reiter, und der mit den meisten Zahlen darin.
try {
  tabs.children[0].onclick();
  const buttons = [];
  (function walk(node) {
    if (!node || typeof node !== "object") return;
    if (node.tagName === "button" && node.title === "Inspect this car on its own") buttons.push(node);
    (node.children || []).forEach(walk);
  })(__IDS["content"]);
  if (!buttons.length) { console.log("  FAIL  kein Autoknopf in der Punktewertung"); bad += 1; }
  else { buttons[0].onclick(); console.log("  ok    Einzelauto: " + buttons[0]._text); }
} catch (err) { bad += 1; console.log("  FAIL  Einzelauto: " + err.message); }
// Die Filter sind Mengen, seit sie mehrfach waehlbar sind: zwei Marken ODER zwei
// Autotypen zugleich. Der leere Ausgangszustand prueft davon nichts -- also je
// zwei Chips druecken und danach jeden Reiter nochmal zeichnen. Genau hier faellt
// auf, wenn irgendwo noch `F.make !== null` statt `F.make.size` steht.
["f-make", "f-cartype", "f-cat", "f-tune"].forEach(id => {
  const host = __IDS[id];
  const chips = (host.children || []).filter(c => c.tagName === "button");
  // Der erste Knopf ist "All"; die beiden danach sind echte Werte.
  chips.slice(1, 3).forEach(chip => {
    try { chip.onclick(); }
    catch (err) { bad += 1; console.log("  FAIL  Chip in " + id + ": " + err.message); }
  });
  console.log("  ok    " + id + ": " + Math.max(0, Math.min(2, chips.length - 1)) + " Chip(s) gesetzt");
});
tabs.children.forEach(b => {
  try { b.onclick(); console.log("  ok    Reiter " + b._text + " mit gesetzten Filtern"); }
  catch (err) { bad += 1; console.log("  FAIL  Reiter " + b._text + " mit Filtern: " + err.message); }
});
console.log(bad ? "\\nFEHLGESCHLAGEN" : "\\nalles bestanden");
process.exit(bad ? 1 : 0);
"""

# Reicht, damit jeder Reiter etwas zu zeichnen hat, wenn kein echter Bestand daliegt.
FALLBACK = {
    "tracks": ["Satta Sprint"], "classes": ["A"], "categories": ["Road Racing"],
    "carNames": ["Test Car"], "carIds": [300], "carMeta": [None],
    # Die Seite liest die Bitfolge aus `flags` (wie build_analytics_dataset.FLAGS).
    # Hier stand nur das alte `bits`, und die Ersatzdaten brachen darum schon beim
    # Laden ab -- aufgefallen erst, als sie zum ersten Mal wirklich gebraucht wurden.
    "flags": ["clean", "tcs", "abs", "stm", "friction", "autobrake", "autoshift",
              "clutch", "supereasy"],
    "bits": {"clean": 1}, "classPI": {},
    "boards": [{"t": 0, "k": 0, "c": 0, "rows": 4, "valid": 4, "invalid": 0,
                "maxRank": 4, "gcar": [0], "gsig": [1], "gcount": [4],
                "lgrp": [0], "lms": [70000], "lrank": [1],
                # PI je Runde; aeltere Datensaetze haben das Feld nicht, und der echte
                # Bestand prueft genau diesen Fall mit.
                "lpi": [692]}],
    "meta": {"cars": 1, "named_cars": 1, "raw_rows": 4, "kept_laps": 4,
             "invalid_rows": 0, "laps_per_group": 5, "scans": []},
}


def page_script() -> str:
    src = BUILDER.read_text(encoding="utf-8")
    start = src.index('PAGE = r"""') + len('PAGE = r"""')
    page = src[start:src.index('"""', start)]
    found = re.search(r"<script>(.*)</script>", page, re.S)
    if not found:
        raise SystemExit("kein <script> in der Seitenvorlage gefunden")
    # Wie der Bau: die Funktionen fuer eingereichte Zeiten stehen in einer eigenen
    # Datei und werden unveraendert eingesetzt. Ohne das scheiterte dieser Test seit
    # 2026-09-24 an "__SUBMITTED_JS__ is not defined".
    eingereicht = (BUILDER.parent / "submitted_laps.js").read_text(encoding="utf-8")
    return found.group(1).replace("__SUBMITTED_JS__", eingereicht)


def main() -> int:
    node = shutil.which("node")
    if not node:
        print("kein Node gefunden -- Seitenpruefung uebersprungen")
        return 0

    payload = (DATASET.read_text(encoding="utf-8") if DATASET.exists()
               else json.dumps(FALLBACK))
    where = "echter Bestand" if DATASET.exists() else "Ersatzdaten"
    print(f"Seite pruefen ({where})")

    script = DOM + page_script().replace("__PAYLOAD__", payload) + CLICKS
    with tempfile.TemporaryDirectory() as tmp:
        target = Path(tmp) / "page.js"
        target.write_text(script, encoding="utf-8")
        # Erst der Syntaxcheck: ein Klammerfehler soll nicht als Renderfehler
        # irgendeines Reiters erscheinen.
        syntax = subprocess.run([node, "--check", str(target)],
                                capture_output=True, text=True)
        if syntax.returncode != 0:
            print("  FAIL  JavaScript-Syntax")
            print(syntax.stderr.strip()[:2000])
            return 1
        print("  ok    JavaScript-Syntax")
        run = subprocess.run([node, str(target)], capture_output=True, text=True)
        print(run.stdout.strip())
        if run.returncode != 0 and run.stderr.strip():
            print(run.stderr.strip()[:2000])
        return run.returncode


if __name__ == "__main__":
    raise SystemExit(main())
