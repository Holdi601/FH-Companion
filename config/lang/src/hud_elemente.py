# -*- coding: utf-8 -*-
"""Die Schalter im Reiter "Lap delta HUD" (2026-09-25), 25 Sprachen: welche Stuecke
erscheinen, und woher die Karten der Anmeldung kommen.

Nicht von Muttersprachlern geprueft.
"""

A = "Course maps on the Event Sign Up screen"
B = "Live map during the race"
C = "Car note"
D = "Automatic: your laps, else Rivals"
E = "Your laps only (telemetry)"
F = "Rivals maps only"

# kennung: (A, B, C, D, E, F)
_UEBERSETZT = {
 "de": ("Streckenkarten auf dem Anmeldeschirm", "Live-Karte im Rennen", "Autonotiz",
        "Automatisch: eigene Runden, sonst Rivalen", "Nur eigene Runden (Telemetrie)", "Nur Rivalen-Karten"),
 "fr": ("Cartes des parcours à l'inscription", "Carte en direct pendant la course", "Note sur la voiture",
        "Automatique : tes tours, sinon Rivaux", "Tes tours uniquement (télémétrie)", "Cartes Rivaux uniquement"),
 "es": ("Mapas de circuito en la inscripción", "Mapa en vivo durante la carrera", "Nota del coche",
        "Automático: tus vueltas, si no Rivales", "Solo tus vueltas (telemetría)", "Solo mapas de Rivales"),
 "it": ("Mappe dei percorsi all'iscrizione", "Mappa dal vivo in gara", "Nota sull'auto",
        "Automatico: i tuoi giri, altrimenti Rivali", "Solo i tuoi giri (telemetria)", "Solo mappe Rivali"),
 "pt": ("Mapas dos percursos na inscrição", "Mapa ao vivo durante a corrida", "Nota do carro",
        "Automático: as tuas voltas, senão Rivais", "Só as tuas voltas (telemetria)", "Só mapas de Rivais"),
 "nl": ("Parcourskaarten bij het inschrijven", "Live kaart tijdens de race", "Autonotitie",
        "Automatisch: je eigen rondes, anders Rivals", "Alleen je eigen rondes (telemetrie)", "Alleen Rivals-kaarten"),
 "pl": ("Mapy tras na ekranie zapisów", "Mapa na żywo podczas wyścigu", "Notatka o aucie",
        "Automatycznie: twoje okrążenia, inaczej Rywale", "Tylko twoje okrążenia (telemetria)", "Tylko mapy Rywali"),
 "sv": ("Bankartor på anmälningsskärmen", "Livekarta under loppet", "Bilanteckning",
        "Automatiskt: dina varv, annars Rivals", "Bara dina varv (telemetri)", "Bara Rivals-kartor"),
 "da": ("Banekort på tilmeldingsskærmen", "Livekort under løbet", "Bilnote",
        "Automatisk: dine omgange, ellers Rivals", "Kun dine omgange (telemetri)", "Kun Rivals-kort"),
 "fi": ("Ratakartat ilmoittautumisnäytössä", "Live-kartta kilpailun aikana", "Automuistiinpano",
        "Automaattinen: omat kierroksesi, muuten Rivals", "Vain omat kierroksesi (telemetria)", "Vain Rivals-kartat"),
 "cs": ("Mapy tratí na obrazovce přihlášení", "Živá mapa během závodu", "Poznámka k autu",
        "Automaticky: tvá kola, jinak Rivalové", "Jen tvá kola (telemetrie)", "Jen mapy Rivalů"),
 "hu": ("Pályatérképek a nevezési képernyőn", "Élő térkép verseny közben", "Autós jegyzet",
        "Automatikus: saját köreid, egyébként Rivals", "Csak saját köreid (telemetria)", "Csak Rivals-térképek"),
 "ro": ("Hărțile traseelor la înscriere", "Hartă live în timpul cursei", "Notă despre mașină",
        "Automat: turele tale, altfel Rivali", "Doar turele tale (telemetrie)", "Doar hărți Rivali"),
 "el": ("Χάρτες διαδρομών στην εγγραφή", "Ζωντανός χάρτης στον αγώνα", "Σημείωση αυτοκινήτου",
        "Αυτόματα: οι γύροι σου, αλλιώς Rivals", "Μόνο οι γύροι σου (τηλεμετρία)", "Μόνο χάρτες Rivals"),
 "ru": ("Карты трасс на экране записи", "Живая карта во время гонки", "Заметка об авто",
        "Автоматически: твои круги, иначе Соперники", "Только твои круги (телеметрия)", "Только карты Соперников"),
 "tr": ("Kayıt ekranında parkur haritaları", "Yarış sırasında canlı harita", "Araç notu",
        "Otomatik: kendi turların, yoksa Rakipler", "Yalnızca kendi turların (telemetri)", "Yalnızca Rakipler haritaları"),
 "id": ("Peta lintasan di layar pendaftaran", "Peta langsung saat balapan", "Catatan mobil",
        "Otomatis: lap milikmu, jika tidak Rivals", "Hanya lap milikmu (telemetri)", "Hanya peta Rivals"),
 "ms": ("Peta laluan di skrin pendaftaran", "Peta langsung semasa perlumbaan", "Nota kereta",
        "Automatik: pusingan anda, jika tidak Rivals", "Pusingan anda sahaja (telemetri)", "Peta Rivals sahaja"),
 "vi": ("Bản đồ đường đua ở màn hình đăng ký", "Bản đồ trực tiếp khi đua", "Ghi chú xe",
        "Tự động: vòng của bạn, nếu không thì Rivals", "Chỉ vòng của bạn (telemetry)", "Chỉ bản đồ Rivals"),
 "th": ("แผนที่สนามบนหน้าลงทะเบียน", "แผนที่สดระหว่างแข่ง", "บันทึกรถ",
        "อัตโนมัติ: รอบของคุณ ไม่งั้นใช้ Rivals", "เฉพาะรอบของคุณ (เทเลเมทรี)", "เฉพาะแผนที่ Rivals"),
 "ja": ("エントリー画面のコースマップ", "レース中のライブマップ", "車のメモ",
        "自動: 自分のラップ、なければライバル", "自分のラップのみ (テレメトリー)", "ライバルのマップのみ"),
 "ko": ("참가 신청 화면의 코스 지도", "레이스 중 실시간 지도", "차량 메모",
        "자동: 내 랩, 없으면 라이벌", "내 랩만 (텔레메트리)", "라이벌 지도만"),
 "zh-Hans": ("报名界面上的赛道地图", "比赛中的实时地图", "车辆备注",
             "自动：你的圈速，否则用对手地图", "只用你的圈速（遥测）", "只用对手地图"),
 "zh-Hant": ("報名畫面上的賽道地圖", "比賽中的即時地圖", "車輛備註",
             "自動：你的圈速，否則用對手地圖", "只用你的圈速（遙測）", "只用對手地圖"),
}

_KEYS = (A, B, C, D, E, F)
for _code, _saetze in _UEBERSETZT.items():
    assert len(_saetze) == len(_KEYS), (_code, len(_saetze))

ZUSATZ = {code: dict(zip(_KEYS, saetze)) for code, saetze in _UEBERSETZT.items()}
ZUSATZ["zh"] = ZUSATZ["zh-Hans"]
