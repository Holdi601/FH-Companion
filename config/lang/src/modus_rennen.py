# -*- coding: utf-8 -*-
"""Der Modus "race": Anmeldeschirm ohne Horizon-Play-Reihe, also Solo oder Koop (2026-09-28), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = (
    "Solo / co-op race",
)

_UEBERSETZT = {
 "de": ("Solo-/Koop-Rennen",),
 "fr": ("Course solo / coop",),
 "es": ("Carrera en solitario / cooperativa",),
 "it": ("Gara in solitaria / cooperativa",),
 "pt": ("Corrida solo / cooperativa",),
 "nl": ("Solo-/co-op-race",),
 "pl": ("Wyścig solo / w kooperacji",),
 "sv": ("Solo-/co-op-lopp",),
 "da": ("Solo-/co-op-løb",),
 "fi": ("Soolo-/yhteistyökisa",),
 "cs": ("Sólo / kooperativní závod",),
 "hu": ("Egyéni / kooperatív verseny",),
 "ro": ("Cursă solo / cooperativă",),
 "el": ("Αγώνας σόλο / συνεργατικός",),
 "ru": ("Одиночная / кооперативная гонка",),
 "tr": ("Tek başına / ortak yarış",),
 "id": ("Balapan solo / co-op",),
 "ms": ("Perlumbaan solo / co-op",),
 "vi": ("Đua đơn / co-op",),
 "th": ("แข่งเดี่ยว / โคออป",),
 "ja": ("ソロ / 協力レース",),
 "ko": ("솔로 / 협동 레이스",),
 "zh-Hans": ("单人 / 合作比赛",),
 "zh-Hant": ("單人 / 合作比賽",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
