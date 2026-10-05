# -*- coding: utf-8 -*-
"""Reiter "Scan leaderboards": ein Board, ueber dem das Spiel abstuerzte (2026-10-04). 25 Sprachen, nicht von Muttersprachlern geprueft."""

_SATZ = "game crashed"

_UEBERSETZT = {
    "cs": "hra spadla",
    "da": "spillet gik ned",
    "de": "Spiel abgestürzt",
    "el": "το παιχνίδι κατέρρευσε",
    "es": "el juego se cerró",
    "fi": "peli kaatui",
    "fr": "jeu planté",
    "hu": "a játék összeomlott",
    "id": "game mogok",
    "it": "gioco bloccato",
    "ja": "ゲームがクラッシュ",
    "ko": "게임 충돌",
    "ms": "permainan ranap",
    "nb": "spillet krasjet",
    "nl": "spel gecrasht",
    "pl": "gra uległa awarii",
    "pt": "o jogo travou",
    "ro": "jocul s-a blocat",
    "ru": "игра вылетела",
    "sv": "spelet kraschade",
    "th": "เกมแครช",
    "tr": "oyun çöktü",
    "vi": "trò chơi bị lỗi",
    "zh-Hans": "游戏崩溃",
    "zh-Hant": "遊戲當機",
}

ZUSATZ = {code: {_SATZ: text} for code, text in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
