# -*- coding: utf-8 -*-
"""Ganzer Bildschirm als Spielbild (Remote Play im Vollbild, 2026-09-29). 25 Sprachen, nicht von Muttersprachlern geprueft."""

K = {
    "screen": "Entire screen {0}",
    "for": "for games in full screen",
}

_UEBERSETZT = {
 "de": {"screen": "Ganzer Bildschirm {0}", "for": "für Spiele im Vollbild"},
 "fr": {"screen": "Écran entier {0}", "for": "pour les jeux en plein écran"},
 "es": {"screen": "Pantalla completa {0}", "for": "para juegos a pantalla completa"},
 "it": {"screen": "Schermo intero {0}", "for": "per i giochi a schermo intero"},
 "pt": {"screen": "Ecrã inteiro {0}", "for": "para jogos em ecrã inteiro"},
 "nl": {"screen": "Heel scherm {0}", "for": "voor games op volledig scherm"},
 "pl": {"screen": "Cały ekran {0}", "for": "do gier na pełnym ekranie"},
 "cs": {"screen": "Celá obrazovka {0}", "for": "pro hry na celou obrazovku"},
 "da": {"screen": "Hele skærm {0}", "for": "til spil i fuld skærm"},
 "sv": {"screen": "Hela skärm {0}", "for": "för spel i helskärm"},
 "fi": {"screen": "Koko näyttö {0}", "for": "koko näytön peleille"},
 "hu": {"screen": "Teljes képernyő {0}", "for": "teljes képernyős játékokhoz"},
 "ro": {"screen": "Tot ecranul {0}", "for": "pentru jocuri pe tot ecranul"},
 "el": {"screen": "Ολόκληρη οθόνη {0}", "for": "για παιχνίδια σε πλήρη οθόνη"},
 "tr": {"screen": "Tüm ekran {0}", "for": "tam ekran oyunlar için"},
 "ru": {"screen": "Весь экран {0}", "for": "для игр в полноэкранном режиме"},
 "ja": {"screen": "画面全体 {0}", "for": "全画面のゲーム用"},
 "ko": {"screen": "전체 화면 {0}", "for": "전체 화면 게임용"},
 "zh-Hans": {"screen": "整个屏幕 {0}", "for": "用于全屏游戏"},
 "zh-Hant": {"screen": "整個螢幕 {0}", "for": "用於全螢幕遊戲"},
 "th": {"screen": "ทั้งหน้าจอ {0}", "for": "สำหรับเกมแบบเต็มจอ"},
 "vi": {"screen": "Toàn màn hình {0}", "for": "cho game toàn màn hình"},
 "id": {"screen": "Seluruh layar {0}", "for": "untuk game layar penuh"},
 "ms": {"screen": "Seluruh skrin {0}", "for": "untuk permainan skrin penuh"},
 "nb": {"screen": "Hele skjerm {0}", "for": "for spill i fullskjerm"},
}

for _code, _saetze in _UEBERSETZT.items():
    assert set(_saetze) == set(K), (_code, sorted(set(K) ^ set(_saetze)))
    for _id, _text in _saetze.items():
        assert ("{0}" in K[_id]) == ("{0}" in _text), (_code, _id)

ZUSATZ = {code: {K[i]: t for i, t in saetze.items()} for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
