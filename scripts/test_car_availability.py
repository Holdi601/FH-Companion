"""Die Autoliste fuer "Car collection" (server/car_availability.py) -- ohne Netz.

    python scripts/test_car_availability.py

Geprueft wird alles zwischen einer geholten Seite und der fertigen Datei: die Tabelle
von forza.net, der Wikitext (verschachtelte Vorlagen), jede Art von Weg, die Zuordnung
der car_ids, das Zusammenfuehren -- und dass ein misslungener Abruf die alte Datei
stehen laesst, statt sie durch eine halbe zu ersetzen.
"""
from __future__ import annotations

import json
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

WS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WS / "server"))
import car_availability as ca  # noqa: E402

TABELLE = """<table><thead><tr><th>Make</th><th>Car Name</th><th>Car Type</th><th>Car Class</th>
<th>Country</th><th>Collection</th><th>Add-Ons</th></tr></thead><tbody>
<tr><td>Abarth</td><td>1968 Abarth 595 esseesse</td><td>Cult Cars</td><td>100 D</td><td>Italy</td>
<td>Autoshow, Wheelspin</td><td></td></tr>
<tr><td>McLaren</td><td>2025 McLaren W1</td><td>Hypercars</td><td>920 S2</td><td>UK</td>
<td>Autoshow DLC</td><td>Car Pass</td></tr>
<tr><td>Nissan</td><td>1997 Nissan Skyline GT-R V-Spec</td><td>Retro Sports Cars</td><td>X 999</td>
<td>Japan</td><td>Seasonal</td><td></td></tr>
<tr><td>Odd</td><td>Car Without Year</td><td>Thing</td><td></td><td>Nowhere</td></tr>
<tr><td>short</td><td>row</td></tr>
</tbody></table><p>List Updated 8 September 2026</p>"""


def seite(felder: str, jahr: int = 1968, titel_modell: str = "Abarth 595 esseesse") -> str:
    return ("{{CarInfobox\n|manufacturer = Abarth\n|year = %d\n}}\n"
            "{{CarStats|fm7\n|price = 1}}\n"
            "{{CarStats|fh6\n|2.6|3.7|1.7|2.6|2.0|5.2|100\n%s\n}}\nText" % (jahr, felder))


class Tabelle(unittest.TestCase):
    def test_zeilen(self):
        autos = ca.offizielle_liste(TABELLE)
        self.assertEqual([a["name"] for a in autos],
                         ["Abarth 595 esseesse", "McLaren W1", "Nissan Skyline GT-R V-Spec", "Car Without Year"])
        a = autos[0]
        self.assertEqual((a["year"], a["pi"], a["class"], a["make"]), (1968, 100, "D", "Abarth"))
        self.assertEqual(a["collection"], ["Autoshow", "Wheelspin"])
        self.assertEqual(a["addons"], [])
        self.assertEqual(autos[1]["addons"], ["Car Pass"])

    def test_kaputte_felder(self):
        autos = ca.offizielle_liste(TABELLE)
        self.assertIsNone(autos[2]["pi"], "Klasse in falscher Reihenfolge ergibt keinen PI")
        self.assertIsNone(autos[3]["year"])
        self.assertEqual(autos[3]["collection"], [])

    def test_stand(self):
        self.assertEqual(ca._stand_der_liste(TABELLE), "8 September 2026")
        self.assertEqual(ca._stand_der_liste("Updated on September 8, 2026"), "September 8, 2026")
        self.assertIsNone(ca._stand_der_liste("updated monthly."))

    def test_leer(self):
        self.assertEqual(ca.offizielle_liste(""), [])
        self.assertEqual(ca.offizielle_liste("<table><tr><td>x</td></tr>"), [])


class Wikitext(unittest.TestCase):
    def test_verschachtelt(self):
        text = seite("|season = {{FH6Series|3|au|season|20}}\n|location = {{FH6AMCarLocation|SHI6|TOK2}}\n|price = 25,000")
        f = ca.carstats_fh6(text)
        self.assertEqual(f["price"], "25,000")
        self.assertIn("FH6Series", f["season"])
        self.assertEqual(ca.infobox_jahr(text), 1968)

    def test_nur_fh6_block(self):
        text = "{{CarStats|fm7\n|price = 1\n}}"
        self.assertIsNone(ca.carstats_fh6(text), "ein Block anderer Spiele gilt als fh6")
        self.assertIsNone(ca.carstats_fh6("kein Block"))
        self.assertIsNone(ca.carstats_fh6("{{CarStats|fh6\n|price = 1"), "offener Block wirft")

    def test_jahr(self):
        self.assertIsNone(ca.infobox_jahr("{{CarInfobox\n|year = 19xx\n}}"))
        self.assertIsNone(ca.infobox_jahr("ohne Kasten"))

    def test_felder(self):
        reihe, benannt = ca.vorlage_felder("X|a|[[b|c]]|k = v|{{Y|1|2}}|l=m=n")
        self.assertEqual(reihe, ["a", "[[b|c]]", "{{Y|1|2}}"])
        self.assertEqual(benannt, {"k": "v", "l": "m=n"})

    def test_klar(self):
        self.assertEqual(ca._klar("[[Forza Horizon 4|FH4]] ''x''<ref>y</ref>[[Category:Z]]"), "FH4 x")
        self.assertEqual(ca._klar("[[Southwest Ito]]"), "Southwest Ito")

    def test_playlist(self):
        self.assertEqual(ca.playlist_eintrag("{{FH6Series|3|au|season|20}}"),
                         {"series": 3, "season": "Autumn", "what": "season milestone, 20 points", "wk": "season", "wx": "20"})
        e = ca.playlist_eintrag("{{FH6Series|2|wi|champ|[[Retro Rewind]]}}")
        self.assertEqual((e["season"], e["wx"]), ("Winter", "Retro Rewind"))
        self.assertEqual(ca.playlist_eintrag("{{FH6Series|4|w2|x}}"), {"series": 4, "season": "Winter, week 2"})
        self.assertEqual(ca.playlist_eintrag("{{FH6Series|?|zz|unknown|q}}"), {})
        self.assertIsNone(ca.playlist_eintrag("kein Eintrag"))


def arten(wege):
    return [w["k"] for w in wege]


class Wege(unittest.TestCase):
    AUTO = {"collection": [], "addons": []}

    def test_autoshow(self):
        w = ca.wege({"collection": ["Autoshow", "Wheelspin"], "addons": []}, {"price": "65,000", "unlock": "auto"})
        self.assertEqual(w[0], {"k": "autoshow", "price": 65000})
        self.assertEqual(arten(w), ["autoshow", "wheelspin", "auction"])
        self.assertEqual(ca.wege({"collection": ["Autoshow"], "addons": []}, None)[0], {"k": "autoshow"})

    def test_dlc_zuerst(self):
        w = ca.wege({"collection": ["Autoshow DLC"], "addons": ["Car Pass"]}, {"price": "1,000,000"})
        self.assertEqual(w[:2], [{"k": "dlc", "pack": "Car Pass"}, {"k": "autoshow", "price": 1000000}])

    def test_playlist(self):
        w = ca.wege({"collection": ["Seasonal"], "addons": []}, {"unlock": "htf"})
        self.assertEqual(w[0], {"k": "playlist"})
        w = ca.wege(self.AUTO, {"unlock": "htf", "season": "{{FH6Series|1|au|season|15}}"})
        self.assertEqual(w[0]["series"], 1)

    def test_wheelspin_nein(self):
        self.assertNotIn("wheelspin", arten(ca.wege(self.AUTO, {"wheelspin": "n"})))
        self.assertIn("wheelspin", arten(ca.wege(self.AUTO, {"wheelspin": "un"})))

    def test_aftermarket(self):
        w = ca.wege(self.AUTO, {"after": "y", "amprice": "18,750",
                                "location": "{{FH6AMCarLocation|SHI6|TOK2}}",
                                "amtakeover": "{{FH6AMLimited|3|Italian Exotics Dealership|SHI6|18,750}}"})
        self.assertEqual(w[0], {"k": "aftermarket", "price": 18750, "where": "SHI6, TOK2",
                                "event": "Italian Exotics Dealership"})
        self.assertNotIn("aftermarket", arten(ca.wege(self.AUTO, {"after": "n"})))
        self.assertIn("aftermarket", arten(ca.wege({"collection": ["Aftermarket"], "addons": []}, None)))

    def test_orte_und_rest(self):
        self.assertEqual(ca.wege(self.AUTO, {"unlock": "barn", "barn": "[[Southwest Ito]]"})[0],
                         {"k": "barn", "where": "Southwest Ito"})
        self.assertEqual(ca.wege(self.AUTO, {"unlock": "treasure"})[0], {"k": "treasure"})
        self.assertEqual(ca.wege(self.AUTO, {"unlock": "cm", "cm": "[[Chevrolet Corvette]]"})[0],
                         {"k": "mastery", "car": "Chevrolet Corvette"})
        self.assertEqual(ca.wege(self.AUTO, {"collection": "y", "cat": "Day Trips", "points": "2,700"})[0],
                         {"k": "journal", "cat": "Day Trips", "points": 2700})
        self.assertEqual(ca.wege(self.AUTO, {"campaign": "y", "band": "Blue"})[0], {"k": "campaign", "band": "Blue"})
        self.assertEqual(ca.wege(self.AUTO, {"gift": "y", "giftd": "August 1, 2026"})[0],
                         {"k": "gift", "date": "August 1, 2026"})
        self.assertEqual(ca.wege(self.AUTO, {"loyalty": "[[Forza Horizon 4]]"})[0],
                         {"k": "loyalty", "game": "Forza Horizon 4"})

    def test_auktion(self):
        self.assertEqual(arten(ca.wege(self.AUTO, {"unlock": "un"})), ["unobtainable"])
        self.assertEqual(arten(ca.wege(self.AUTO, {"auction": "n"})), [])
        self.assertEqual(arten(ca.wege(self.AUTO, None)), ["auction"])

    def test_zahl(self):
        self.assertEqual(ca._zahl("48,000,000"), 48000000)
        self.assertIsNone(ca._zahl(""))
        self.assertIsNone(ca._zahl(None))
        self.assertIsNone(ca._zahl("?"))


class Ids(unittest.TestCase):
    LISTE = [{"name": "Chevrolet Corvette", "year": 1953}, {"name": "Toyota 2000GT", "year": 1969},
             {"name": "Mercedes-Benz 300 SL Coupé", "year": 1954}]

    def test_meistbelegte_id(self):
        namen = {"1564": {"name": "Chevrolet Corvette", "year": 1953, "votes": 3},
                 "2177": {"name": "Chevrolet Corvette", "year": 1953, "votes": 40},
                 "3118": {"name": "Chevrolet Corvette", "year": 1953, "votes": 9},
                 "247": {"name": "Toyota 2000GT", "year": 1969, "votes": 143}}
        ids = ca.ids_je_auto(self.LISTE, namen)
        self.assertEqual(ids[("chevrolet corvette", 1953)], [2177])
        self.assertEqual(ids[("toyota 2000gt", 1969)], [247])

    def test_ungueltiges(self):
        namen = {"-5": {"name": "Toyota 2000GT", "year": 1969, "votes": 9},
                 "x": {"name": "Toyota 2000GT", "year": 1969},
                 "7": {"name": "Toyota 2000GT", "year": "neunzehn"},
                 "8": {"name": "Unbekannt", "year": 1969, "votes": 99},
                 "9": {"year": 1969}}
        self.assertEqual(ca.ids_je_auto(self.LISTE, namen), {})

    def test_akzente(self):
        namen = {"251": {"name": "Mercedes-Benz 300 SL Coupe", "year": 1954, "votes": 1}}
        self.assertEqual(ca.ids_je_auto(self.LISTE, namen)[(ca._norm("Mercedes-Benz 300 SL Coupé"), 1954)], [251])


class Zusammenfuehren(unittest.TestCase):
    def test_wiki_und_id(self):
        liste = ca.offizielle_liste(TABELLE)
        seiten = [("Abarth 595 esseesse", seite("|unlock = auto|price = 25,000|wheelspin = n")),
                  ("McLaren W1 (2025)", seite("|price = 1,000,000", 2025)),
                  ("Nissan Skyline GT-R V-Spec (1997)", seite("|unlock = htf", 1997)),
                  ("Ohne Stats", "{{CarInfobox\n|year = 2000\n}}")]
        ids = {("abarth 595 esseesse", 1968): [2017]}
        autos, zahlen = ca.zusammenfuehren(liste, seiten, ids)
        self.assertEqual(zahlen, {"wiki": 3, "id": 1})
        a = autos[0]
        self.assertEqual((a["id"], a["ids"], a["price"], a["wiki"]), (2017, [2017], 25000, "Abarth 595 esseesse"))
        # Die offizielle Liste nennt Wheelspin, das Wiki verneint: die offizielle gewinnt.
        self.assertIn("wheelspin", arten(a["ways"]), "das Wiki ueberstimmt die offizielle Liste")
        self.assertEqual(autos[1]["wiki"], "McLaren W1 (2025)")
        self.assertNotIn("id", autos[1])
        self.assertTrue(all(a["ways"] for a in autos), "ein Auto ohne Weg")

    def test_falsches_jahr_passt_nicht(self):
        liste = ca.offizielle_liste(TABELLE)[:1]
        autos, zahlen = ca.zusammenfuehren(liste, [("Abarth 595 esseesse", seite("|price = 1", 1999))], {})
        self.assertEqual(zahlen["wiki"], 0)
        self.assertEqual(arten(autos[0]["ways"]), ["autoshow", "wheelspin", "auction"])


class Aktualisieren(unittest.TestCase):
    def setUp(self):
        self.ordner = tempfile.TemporaryDirectory()
        self.ziel = Path(self.ordner.name) / "cars" / "fh6_car_availability.json"

    def tearDown(self):
        self.ordner.cleanup()

    def viele(self, n):
        zeilen = "".join(f"<tr><td>M</td><td>2020 Auto {i}</td><td>T</td><td>500 C</td><td>X</td>"
                         f"<td>Autoshow</td><td></td></tr>" for i in range(n))
        return f"<table>{zeilen}</table>".encode()

    def test_schreibt(self):
        with mock.patch.object(ca, "_holen", return_value=self.viele(450)), \
                mock.patch.object(ca, "wiki_seiten", return_value=[]):
            self.assertTrue(ca.aktualisieren(lambda m: None, self.ziel))
        d = json.loads(self.ziel.read_text(encoding="utf-8"))
        self.assertEqual((d["format"], len(d["cars"])), ("fhc-cars-1", 450))
        self.assertFalse(self.ziel.with_suffix(".tmp").exists(), "Zwischendatei bleibt liegen")

    def test_halbe_liste_ersetzt_nichts(self):
        self.ziel.parent.mkdir(parents=True)
        self.ziel.write_text("ALT", encoding="utf-8")
        with mock.patch.object(ca, "_holen", return_value=self.viele(50)), \
                mock.patch.object(ca, "wiki_seiten", return_value=[]):
            self.assertFalse(ca.aktualisieren(lambda m: None, self.ziel))
        self.assertEqual(self.ziel.read_text(encoding="utf-8"), "ALT")

    def test_netzfehler_ersetzt_nichts(self):
        self.ziel.parent.mkdir(parents=True)
        self.ziel.write_text("ALT", encoding="utf-8")
        with mock.patch.object(ca, "_holen", side_effect=RuntimeError("offline")):
            self.assertFalse(ca.aktualisieren(lambda m: None, self.ziel))
        self.assertEqual(self.ziel.read_text(encoding="utf-8"), "ALT")

    def test_ohne_wiki_trotzdem_liste(self):
        with mock.patch.object(ca, "_holen", return_value=self.viele(450)), \
                mock.patch.object(ca, "wiki_seiten", side_effect=RuntimeError("fandom down")):
            self.assertTrue(ca.aktualisieren(lambda m: None, self.ziel))
        d = json.loads(self.ziel.read_text(encoding="utf-8"))
        self.assertTrue(all(a["ways"] for a in d["cars"]), "ohne Wiki muss die grobe Spalte reichen")

    def test_holen_versucht_dreimal(self):
        aufrufe = []

        def scheitert(*a, **k):
            aufrufe.append(1)
            raise OSError("weg")
        with mock.patch("urllib.request.urlopen", side_effect=scheitert), mock.patch.object(time, "sleep"):
            with self.assertRaises(RuntimeError):
                ca._holen("https://example.invalid/x")
        self.assertEqual(len(aufrufe), 3)


class Schnittstelle(unittest.TestCase):
    def test_api_cars(self):
        import analytics_api
        with tempfile.TemporaryDirectory() as t:
            datei = Path(t) / "x.json"
            with mock.patch.object(ca, "AUSGABE", datei):
                status, _, _ = analytics_api.handle("GET", "/api/cars", {}, b"", keys={})
                self.assertEqual(status, 503)
                datei.write_text('{"format":"fhc-cars-1"}', encoding="utf-8")
                status, typ, rumpf = analytics_api.handle("GET", "/api/cars", {}, b"", keys={})
                self.assertEqual((status, rumpf), (200, b'{"format":"fhc-cars-1"}'))
                self.assertIn("json", typ)


if __name__ == "__main__":
    lauf = unittest.main(exit=False, verbosity=1)
    ok = lauf.result.wasSuccessful()
    print(f"car_availability: {lauf.result.testsRun} Tests, {'alle bestanden' if ok else 'FEHLER'}")
    sys.exit(0 if ok else 1)
