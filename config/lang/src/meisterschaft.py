# -*- coding: utf-8 -*-
"""Die Schilder auf den Streckenkacheln einer Meisterschaft (2026-09-26), 25 Sprachen.

Kurz, weil sie in einer Ecke der Kachel stehen. Nicht von Muttersprachlern geprueft.
"""

K = ("done", "next", "now")

_UEBERSETZT = {
    "de": ("gefahren", "danach", "jetzt"),
    "fr": ("fait", "ensuite", "maintenant"),
    "es": ("hecho", "después", "ahora"),
    "it": ("fatto", "dopo", "ora"),
    "pt": ("feito", "a seguir", "agora"),
    "nl": ("gereden", "daarna", "nu"),
    "pl": ("gotowe", "potem", "teraz"),
    "sv": ("klar", "sedan", "nu"),
    "da": ("kørt", "derefter", "nu"),
    "fi": ("ajettu", "seuraava", "nyt"),
    "cs": ("hotovo", "potom", "teď"),
    "hu": ("kész", "utána", "most"),
    "ro": ("gata", "apoi", "acum"),
    "el": ("έγινε", "μετά", "τώρα"),
    "ru": ("пройдено", "далее", "сейчас"),
    "tr": ("bitti", "sonra", "şimdi"),
    "id": ("selesai", "berikutnya", "sekarang"),
    "ms": ("selesai", "seterusnya", "sekarang"),
    "vi": ("xong", "tiếp theo", "bây giờ"),
    "th": ("เสร็จ", "ถัดไป", "ตอนนี้"),
    "ja": ("完了", "次", "今"),
    "ko": ("완료", "다음", "지금"),
    "zh-Hans": ("已完成", "下一场", "当前"),
    "zh-Hant": ("已完成", "下一場", "目前"),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
