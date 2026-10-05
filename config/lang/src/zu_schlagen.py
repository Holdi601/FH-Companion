# -*- coding: utf-8 -*-
"""Die Zeit zum Schlagen unter dem Delta -- Schalter im Reiter "Lap delta HUD" (2026-09-27), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = (
    "Show the time to beat for the website's leaderboard",
)

_UEBERSETZT = {
 "de": ("Zeit anzeigen, die für die Bestenliste der Website zu schlagen ist",),
 "fr": ("Afficher le temps à battre pour le classement du site",),
 "es": ("Mostrar el tiempo a batir para la clasificación de la web",),
 "it": ("Mostra il tempo da battere per la classifica del sito",),
 "pt": ("Mostrar o tempo a bater para a classificação do site",),
 "nl": ("Tijd tonen die je moet verslaan voor het klassement van de website",),
 "pl": ("Pokaż czas do pobicia w rankingu na stronie",),
 "sv": ("Visa tiden att slå för webbplatsens topplista",),
 "da": ("Vis tiden, der skal slås, for hjemmesidens rangliste",),
 "fi": ("Näytä aika, joka pitää lyödä sivuston tulostaululle",),
 "cs": ("Zobrazit čas, který je třeba překonat pro žebříček na webu",),
 "hu": ("A weboldal ranglistájához megverendő idő mutatása",),
 "ro": ("Arată timpul de bătut pentru clasamentul de pe site",),
 "el": ("Εμφάνιση του χρόνου που πρέπει να νικήσεις για την κατάταξη του ιστότοπου",),
 "ru": ("Показывать время, которое нужно побить для таблицы лидеров сайта",),
 "tr": ("Sitedeki sıralama için geçilmesi gereken süreyi göster",),
 "id": ("Tampilkan waktu yang harus dikalahkan untuk papan peringkat situs",),
 "ms": ("Tunjukkan masa untuk dikalahkan bagi papan pendahulu laman web",),
 "vi": ("Hiện thời gian cần vượt để lên bảng xếp hạng của trang web",),
 "th": ("แสดงเวลาที่ต้องทำให้ได้เพื่อขึ้นลีดเดอร์บอร์ดของเว็บไซต์",),
 "ja": ("サイトのリーダーボードに載るために上回るべきタイムを表示",),
 "ko": ("웹사이트 리더보드에 오르려면 넘어야 할 기록 표시",),
 "zh-Hans": ("显示登上网站排行榜需要超越的成绩",),
 "zh-Hant": ("顯示登上網站排行榜需要超越的成績",),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
