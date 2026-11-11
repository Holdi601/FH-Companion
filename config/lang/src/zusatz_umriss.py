# -*- coding: utf-8 -*-
"""Die Anmeldekarten nehmen zuerst die nachgezeichnete Rivalen-Karte, sonst die sauberste eigene Runde
(2026-11-11). Ein Satz, 25 Sprachen, nicht von Muttersprachlern geprueft. "Rivals" bleibt, wo die
Sprache den Modus schon bisher so nannte."""

K = {
    "auto": "Automatic: Rivals map, else your cleanest lap",
}

_UEBERSETZT = {
 "de": {"auto": "Automatisch: Rivalen-Karte, sonst deine sauberste Runde"},
 "fr": {"auto": "Automatique : carte Rivaux, sinon ton tour le plus propre"},
 "es": {"auto": "Automático: mapa de Rivales, si no tu vuelta más limpia"},
 "it": {"auto": "Automatico: mappa Rivali, altrimenti il tuo giro più pulito"},
 "pt": {"auto": "Automático: mapa de Rivais, senão a tua volta mais limpa"},
 "nl": {"auto": "Automatisch: Rivals-kaart, anders je schoonste ronde"},
 "pl": {"auto": "Automatycznie: mapa Rywali, inaczej twoje najczystsze okrążenie"},
 "cs": {"auto": "Automaticky: mapa Rivalů, jinak tvé nejčistší kolo"},
 "da": {"auto": "Automatisk: Rivals-kort, ellers din reneste omgang"},
 "sv": {"auto": "Automatiskt: Rivals-karta, annars ditt renaste varv"},
 "nb": {"auto": "Automatisk: Rivaler-kart, ellers din reneste runde"},
 "fi": {"auto": "Automaattinen: Rivals-kartta, muuten puhtain kierroksesi"},
 "hu": {"auto": "Automatikus: Rivals-térkép, egyébként a legtisztább köröd"},
 "ro": {"auto": "Automat: harta Rivali, altfel cel mai curat tur al tău"},
 "el": {"auto": "Αυτόματα: χάρτης Rivals, αλλιώς ο πιο καθαρός γύρος σου"},
 "tr": {"auto": "Otomatik: Rakipler haritası, yoksa en temiz turun"},
 "ru": {"auto": "Автоматически: карта Соперников, иначе твой самый чистый круг"},
 "ja": {"auto": "自動: ライバルのマップ、なければ一番きれいな自分のラップ"},
 "ko": {"auto": "자동: 라이벌 지도, 없으면 가장 깔끔한 내 랩"},
 "zh-Hans": {"auto": "自动：对手地图，否则用你最干净的一圈"},
 "zh-Hant": {"auto": "自動：對手地圖，否則用你最乾淨的一圈"},
 "id": {"auto": "Otomatis: peta Rivals, jika tidak lap terbersihmu"},
 "ms": {"auto": "Automatik: peta Rivals, jika tidak pusingan anda yang paling bersih"},
 "th": {"auto": "อัตโนมัติ: แผนที่ Rivals ไม่งั้นใช้รอบที่สะอาดที่สุดของคุณ"},
 "vi": {"auto": "Tự động: bản đồ Rivals, nếu không thì vòng sạch nhất của bạn"},
}

import re as _re
_FELD = _re.compile(r"\{\d+(?::[^}]*)?\}")
for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _en in K.items():
        assert sorted(_FELD.findall(_en)) == sorted(_FELD.findall(_saetze[_id])), (_code, _id)
        assert _saetze[_id].strip(), (_code, _id)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
