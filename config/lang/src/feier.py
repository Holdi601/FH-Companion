# -*- coding: utf-8 -*-
"""Die Feier bei einer Runde, die die Website schlaegt (2026-09-27), 25 Sprachen.

Nicht von Muttersprachlern geprueft.
"""

K = (
    "Celebrate when a lap beats the website's time",
    "Play a sound with it",
    "Try it",
    "You beat the leaderboard!",
    "Website best {0}",
    "Example: this is how it looks when a lap beats the website's time.",
)

_UEBERSETZT = {
 "de": ("Feiern, wenn eine Runde die Zeit der Website schlägt", "Mit Ton", "Ausprobieren",
        "Bestenliste geschlagen!", "Bestzeit der Website {0}",
        "Beispiel: So sieht es aus, wenn eine Runde die Zeit der Website schlägt."),
 "fr": ("Fêter un tour qui bat le temps du site", "Avec un son", "Essayer",
        "Classement battu !", "Meilleur temps du site {0}",
        "Exemple : voici ce qui s'affiche quand un tour bat le temps du site."),
 "es": ("Celebrar cuando una vuelta supera el tiempo de la web", "Con sonido", "Probar",
        "¡Has superado la clasificación!", "Mejor tiempo de la web {0}",
        "Ejemplo: así se ve cuando una vuelta supera el tiempo de la web."),
 "it": ("Festeggia quando un giro batte il tempo del sito", "Con suono", "Prova",
        "Classifica battuta!", "Miglior tempo del sito {0}",
        "Esempio: ecco come appare quando un giro batte il tempo del sito."),
 "pt": ("Celebrar quando uma volta bate o tempo do site", "Com som", "Experimentar",
        "Bateste a classificação!", "Melhor tempo do site {0}",
        "Exemplo: é assim que aparece quando uma volta bate o tempo do site."),
 "nl": ("Vieren als een ronde de tijd van de website verslaat", "Met geluid", "Uitproberen",
        "Klassement verslagen!", "Beste tijd van de website {0}",
        "Voorbeeld: zo ziet het eruit als een ronde de tijd van de website verslaat."),
 "pl": ("Świętuj, gdy okrążenie pobije czas ze strony", "Z dźwiękiem", "Wypróbuj",
        "Ranking pobity!", "Najlepszy czas na stronie {0}",
        "Przykład: tak to wygląda, gdy okrążenie pobije czas ze strony."),
 "sv": ("Fira när ett varv slår webbplatsens tid", "Med ljud", "Prova",
        "Du slog topplistan!", "Webbplatsens bästa tid {0}",
        "Exempel: så här ser det ut när ett varv slår webbplatsens tid."),
 "da": ("Fejr, når en omgang slår hjemmesidens tid", "Med lyd", "Prøv",
        "Du slog ranglisten!", "Hjemmesidens bedste tid {0}",
        "Eksempel: sådan ser det ud, når en omgang slår hjemmesidens tid."),
 "fi": ("Juhli, kun kierros lyö sivuston ajan", "Äänen kanssa", "Kokeile",
        "Löit tulostaulun!", "Sivuston paras aika {0}",
        "Esimerkki: tältä näyttää, kun kierros lyö sivuston ajan."),
 "cs": ("Oslavit, když kolo překoná čas z webu", "Se zvukem", "Vyzkoušet",
        "Žebříček překonán!", "Nejlepší čas na webu {0}",
        "Příklad: takhle to vypadá, když kolo překoná čas z webu."),
 "hu": ("Ünneplés, ha egy kör megveri a weboldal idejét", "Hanggal", "Kipróbálás",
        "Megverted a ranglistát!", "A weboldal legjobb ideje {0}",
        "Példa: így néz ki, amikor egy kör megveri a weboldal idejét."),
 "ro": ("Sărbătorește când o tură bate timpul de pe site", "Cu sunet", "Încearcă",
        "Ai bătut clasamentul!", "Cel mai bun timp de pe site {0}",
        "Exemplu: așa arată când o tură bate timpul de pe site."),
 "el": ("Γιορτή όταν ένας γύρος νικά τον χρόνο του ιστότοπου", "Με ήχο", "Δοκίμασε",
        "Νίκησες την κατάταξη!", "Καλύτερος χρόνος ιστότοπου {0}",
        "Παράδειγμα: έτσι φαίνεται όταν ένας γύρος νικά τον χρόνο του ιστότοπου."),
 "ru": ("Праздновать, когда круг быстрее времени на сайте", "Со звуком", "Попробовать",
        "Таблица лидеров побита!", "Лучшее время на сайте {0}",
        "Пример: так это выглядит, когда круг быстрее времени на сайте."),
 "tr": ("Bir tur sitedeki süreyi geçince kutla", "Sesli", "Dene",
        "Sıralamayı geçtin!", "Sitedeki en iyi süre {0}",
        "Örnek: bir tur sitedeki süreyi geçtiğinde böyle görünür."),
 "id": ("Rayakan saat lap mengalahkan waktu di situs", "Dengan suara", "Coba",
        "Kamu mengalahkan papan peringkat!", "Waktu terbaik di situs {0}",
        "Contoh: seperti inilah tampilannya saat lap mengalahkan waktu di situs."),
 "ms": ("Raikan apabila pusingan mengalahkan masa di laman web", "Dengan bunyi", "Cuba",
        "Anda mengalahkan papan pendahulu!", "Masa terbaik laman web {0}",
        "Contoh: beginilah rupanya apabila pusingan mengalahkan masa di laman web."),
 "vi": ("Ăn mừng khi một vòng vượt thời gian trên trang web", "Kèm âm thanh", "Thử",
        "Bạn đã vượt bảng xếp hạng!", "Thời gian tốt nhất trên trang web {0}",
        "Ví dụ: đây là giao diện khi một vòng vượt thời gian trên trang web."),
 "th": ("ฉลองเมื่อรอบเร็วกว่าเวลาบนเว็บไซต์", "พร้อมเสียง", "ลองดู",
        "คุณชนะลีดเดอร์บอร์ดแล้ว!", "เวลาดีที่สุดบนเว็บไซต์ {0}",
        "ตัวอย่าง: นี่คือสิ่งที่แสดงเมื่อรอบเร็วกว่าเวลาบนเว็บไซต์"),
 "ja": ("ラップがサイトのタイムを上回ったらお祝いする", "サウンド付き", "試す",
        "リーダーボードを更新！", "サイトのベスト {0}",
        "例：ラップがサイトのタイムを上回ると、このように表示されます。"),
 "ko": ("랩이 웹사이트 기록을 넘으면 축하하기", "소리 포함", "미리 보기",
        "리더보드를 넘었습니다!", "웹사이트 최고 기록 {0}",
        "예시: 랩이 웹사이트 기록을 넘으면 이렇게 보입니다."),
 "zh-Hans": ("圈速超过网站成绩时庆祝", "播放声音", "试试看",
        "你超越了排行榜！", "网站最佳 {0}",
        "示例：圈速超过网站成绩时就会这样显示。"),
 "zh-Hant": ("圈速超越網站成績時慶祝", "播放聲音", "試試看",
        "你超越了排行榜！", "網站最佳 {0}",
        "範例：圈速超越網站成績時就會這樣顯示。"),
}

for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(K), (_code, len(_saetze))
    for _k, _s in zip(K, _saetze):
        assert ("{0}" in _k) == ("{0}" in _s), (_code, _s)

ZUSATZ = {code: dict(zip(K, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
